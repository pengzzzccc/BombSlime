using System.Collections;
using System.Collections.Generic;
using Inkform.Bus;
using Inkform.Player;
using Inkform.Settings;
using Inkform.Tool;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Inkform.Fx
{
    /// <summary>
    /// Follow camera: smoothly follows the target with a look-ahead offset toward its facing.
    /// Shake and zoom punches are amounts layered on top of the follow result, never written back to the
    /// base position — otherwise they fight the follow and never settle back.
    /// Shake runs on unscaled time, follow on scaled time — so during hitstop the screen "freezes but still shakes".
    /// The follow mode picks the anchor: Player centres on the player, PlayerCursorMidpoint centres on
    /// the middle of the line between player and aim cursor — pointing the reticle somewhere pans the
    /// view halfway toward it while the player never leaves the frame.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class CamHandler : MonoBehaviour
    {
        public enum FollowMode { Player, PlayerCursorMidpoint }

        /// <summary>Active follow mode — logged by the performance recorder so runs with different
        /// camera modes stay comparable.</summary>
        public FollowMode Mode => mode;

        [Header("Follow setting")]
        [SerializeField] private Transform target;                  // fallback follow target; PlayerBus.Player wins whenever one is registered
        [SerializeField] private FollowMode mode = FollowMode.Player;
        [SerializeField] private Vector2 followOffset = Vector2.zero;
        [SerializeField] private float followSmooth = 0.18f;        // SmoothDamp time for normal follow
        [SerializeField] private float lookAhead = 1.2f;            // look-ahead distance toward facing, 0 = off
        [SerializeField] private float lookAheadSmooth = 0.35f;

        [Header("Limit walls")]
        [Tooltip("Seconds a CameraLimit takes to fade in / out as the player enters / leaves its zone — the camera glides instead of snapping")]
        [SerializeField, Min(0f)] private float limitBlendSeconds = 0.3f;

        [Header("Shake")]
        [SerializeField] private float traumaDecay = 1.6f;          // trauma decay per second
        [SerializeField] private float maxShakeOffset = 0.6f;       // max offset at trauma = 1
        [SerializeField] private float shakeFrequency = 22f;

        private Camera cam;
        private float baseZ;
        private float baseOrthoSize;
        private float viewOrthoSize;

        private Vector2 followVel;
        private Vector2 followBasePosition;

        private float trauma;
        private float seedX, seedY;

        private float zoomAmount, zoomDuration, zoomRemaining;
        private float shakeClock;

        private float lookAheadNow, lookAheadVel;
        private bool followHeld;

        // One-directional limit walls of the current scene (CameraLimit). Resolved once per scene
        // load and cached — a scene without walls costs zero per-frame scans, and the deathCache's
        // fake-null re-resolve trick does not fit a list
        private readonly List<CameraLimit> limits = new List<CameraLimit>();
        private readonly List<float> limitWeights = new List<float>();   // per wall, 0..1; < 0 = not yet set
        private bool limitsResolved;

        /// <summary>True while a cutscene owns the camera's base position.</summary>
        public bool IsFollowHeld => followHeld;

        // Follow target = the current scene's live player, resolved from the bus at use time so
        // follow/snap survive scene switches; the serialized field only serves as a fallback for
        // scenes without a player (menus) and for older scene data
        private Transform Target => PlayerBus.Player != null ? PlayerBus.Player.transform : target;

        // Same lazy fake-null re-lookup pattern as RespawnDirector.deathCache: after a scene switch
        // the destroyed gun reads null here and the next use re-resolves against the new player
        private RopeGun cursorGunCache;

        private RopeGun CursorGun
        {
            get
            {
                if (cursorGunCache == null)
                {
                    Transform current = Target;
                    cursorGunCache = current != null ? current.GetComponent<RopeGun>() : null;
                }
                return cursorGunCache;
            }
        }

        // The point the camera centres on before offsets and look-ahead are applied
        private Vector2 AnchorOf(Transform followTarget)
        {
            Vector2 anchor = (Vector2)followTarget.position;
            if (mode == FollowMode.PlayerCursorMidpoint && CursorGun != null)
                anchor = (anchor + CursorGun.CursorPosition) * 0.5f;
            return anchor;
        }

        void Awake()
        {
            cam = GetComponent<Camera>();
            baseZ = transform.position.z;
            baseOrthoSize = cam.orthographicSize;
            viewOrthoSize = baseOrthoSize;
            followBasePosition = transform.position;

            // Different noise seeds per axis, or x/y would be perfectly in phase and shake in a line
            seedX = Random.value * 1000f;
            seedY = Random.value * 1000f;
        }

        void OnEnable()
        {
            FxBus.ShakeRequested += OnShake;
            FxBus.ZoomRequested += OnZoom;
            FxBus.SnapRequested += SnapToTarget;
            SceneManager.sceneLoaded += OnSceneLoaded;   // limit walls are per-scene: re-resolve on every load
        }

        void OnDisable()
        {
            FxBus.ShakeRequested -= OnShake;
            FxBus.ZoomRequested -= OnZoom;
            FxBus.SnapRequested -= SnapToTarget;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => limitsResolved = false;

        void Start()
        {
            // Snap into place at startup rather than sliding over from (0,0)
            if (!followHeld) SnapToTarget();
        }

        /// <summary>
        /// Holds the follow base at a staged world-space anchor without disabling this component.
        /// Keeping the component alive avoids a delayed Start/Snap when the cutscene releases it and
        /// lets presentation effects continue to tick while normal following is suspended.
        /// </summary>
        public void HoldAt(Transform anchor)
        {
            followHeld = true;
            followVel = Vector2.zero;
            lookAheadVel = 0f;
            cursorGunCache = null;
            viewOrthoSize = baseOrthoSize;

            Vector2 hold = anchor != null ? (Vector2)anchor.position : (Vector2)transform.position;
            followBasePosition = hold;
            transform.position = new Vector3(hold.x, hold.y, baseZ);
        }

        /// <summary>
        /// Smoothly takes presentation ownership of the camera, moving its follow base to a world
        /// anchor and changing its orthographic size without disabling this component. Shake and
        /// zoom punches keep layering over the held shot. The transition advances during a scoped
        /// world freeze, but stops while the real pause menu owns presentation time.
        /// </summary>
        public IEnumerator FocusAt(Transform anchor, float orthographicSize, float duration)
        {
            if (anchor == null) yield break;

            followHeld = true;
            followVel = Vector2.zero;
            lookAheadVel = 0f;
            cursorGunCache = null;

            Vector2 startPosition = followBasePosition;
            Vector2 targetPosition = anchor.position;
            float startSize = viewOrthoSize;
            float targetSize = Mathf.Max(0.01f, orthographicSize);
            float seconds = Mathf.Max(0f, duration);

            if (seconds <= 0f)
            {
                followBasePosition = targetPosition;
                viewOrthoSize = targetSize;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < seconds)
            {
                float dt = GameTimeController.PresentationDeltaTime;
                if (dt > 0f) elapsed = Mathf.Min(seconds, elapsed + dt);

                float t = Mathf.SmoothStep(0f, 1f, elapsed / seconds);
                followBasePosition = Vector2.LerpUnclamped(startPosition, targetPosition, t);
                viewOrthoSize = Mathf.LerpUnclamped(startSize, targetSize, t);
                yield return null;
            }
        }

        /// <summary>
        /// Smoothly returns a held shot to the current live follow target and restores the camera's
        /// authored orthographic size. The target is resolved every frame so a teleport during the
        /// presentation cannot return the camera to stale coordinates.
        /// </summary>
        public IEnumerator ReturnToFollow(float duration)
        {
            Vector2 startPosition = followBasePosition;
            float startSize = viewOrthoSize;
            float seconds = Mathf.Max(0f, duration);

            if (seconds <= 0f)
            {
                SnapHeldBaseToTarget();
                ReleaseHeldFollow();
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < seconds)
            {
                float dt = GameTimeController.PresentationDeltaTime;
                if (dt > 0f) elapsed = Mathf.Min(seconds, elapsed + dt);

                float t = Mathf.SmoothStep(0f, 1f, elapsed / seconds);
                followBasePosition = Vector2.LerpUnclamped(startPosition,
                    CurrentFollowPosition(), t);
                viewOrthoSize = Mathf.LerpUnclamped(startSize, baseOrthoSize, t);
                yield return null;
            }

            SnapHeldBaseToTarget();
            ReleaseHeldFollow();
        }

        /// <summary>Returns control to normal following; SmoothDamp starts at the staged position.</summary>
        public void ResumeFollow()
        {
            followHeld = false;
            cursorGunCache = null;
            followVel = Vector2.zero;
            lookAheadVel = 0f;
            followBasePosition = transform.position;
            viewOrthoSize = baseOrthoSize;
        }

        private Vector2 CurrentFollowPosition()
        {
            Transform followTarget = Target;
            if (followTarget == null) return followBasePosition;
            return AnchorOf(followTarget) + followOffset + new Vector2(lookAheadNow, 0f);
        }

        private void SnapHeldBaseToTarget()
        {
            Transform followTarget = Target;
            if (followTarget != null) followBasePosition = CurrentFollowPosition();
            viewOrthoSize = baseOrthoSize;
        }

        private void ReleaseHeldFollow()
        {
            followHeld = false;
            cursorGunCache = null;
            followVel = Vector2.zero;
            lookAheadVel = 0f;
        }

        /// <summary>Snaps onto the target immediately. Shared path for startup and player teleports (respawn).</summary>
        private void SnapToTarget()
        {
            Transform target = Target;
            if (target == null) return;

            // All three velocities must zero: SmoothDamp's velocity lives in fields, and without this
            // the residual inertia overshoots the frame after snapping — looks like "cut over then bounced"
            followVel = Vector2.zero;
            lookAheadVel = 0f;
            lookAheadNow = lookAhead * (PlayerBus.Face == FaceDirection.R ? 1f : -1f);

            Vector2 want = AnchorOf(target) + followOffset + new Vector2(lookAheadNow, 0f);
            want = ClampToLimits(want, instant: true);   // a teleport must not land the view past a limit wall
            followBasePosition = want;
            transform.position = new Vector3(want.x, want.y, baseZ);
        }

        // LateUpdate: the player has finished moving in Update, so following this frame avoids a one-frame lag jitter
        void LateUpdate()
        {
            // Held shots are designer-authored (FocusAt anchors) — limits clamp only live following
            Vector2 basePos = followHeld ? followBasePosition : ClampToLimits(FollowStep(), instant: false);
            Vector2 shakeOffset = ShakeStep();
            ZoomStep();

            // z must stay constant, otherwise 2D render sorting breaks
            transform.position = new Vector3(basePos.x + shakeOffset.x, basePos.y + shakeOffset.y, baseZ);
        }

        /// <summary>
        /// Keeps the view rect on the allowed side of every limit wall. Each wall is the owning
        /// transform's own segment (up axis, `length` reach, centre at its position) and blocks one
        /// side along its right axis; the view's half-size projected onto the wall normal is the
        /// standoff distance, so the SCREEN edge stops on the wall line. Walls apply only while the
        /// camera centre projects inside the segment's span, and only while the player stands in
        /// the wall's zone (CameraLimit.InZone) — otherwise one short wall would grip a whole band
        /// of the map. Entering / leaving a zone fades the wall in / out over limitBlendSeconds
        /// (instant on a teleport), so the camera glides rather than snaps. SmoothDamp's internal
        /// state stays unclamped — the camera presses against the wall while the target lies beyond
        /// it and resumes the instant the target comes back, no snap either way.
        /// </summary>
        private Vector2 ClampToLimits(Vector2 position, bool instant)
        {
            if (!limitsResolved)
            {
                limits.Clear();
                // Parameterless overload: FindObjectsSortMode and its overloads were deprecated
                // in Unity 6.4; active-only unsorted is the same set the old call returned.
                limits.AddRange(FindObjectsByType<CameraLimit>());
                limitWeights.Clear();
                for (int i = 0; i < limits.Count; i++) limitWeights.Add(-1f);
                limitsResolved = true;
            }
            if (limits.Count == 0) return position;

            float halfHeight = cam.orthographicSize;
            float halfWidth = halfHeight * cam.aspect;

            // The zones are judged by the player, not the camera: the camera lags and leads, the
            // player is where the play is
            Transform target = Target;
            Vector2 player = target != null ? AnchorOf(target) : position;
            float blendStep = limitBlendSeconds > 0f
                ? GameTimeController.PresentationDeltaTime / limitBlendSeconds
                : 1f;

            for (int i = 0; i < limits.Count; i++)
            {
                CameraLimit wall = limits[i];
                if (wall == null) continue;

                float wantWeight = wall.InZone(player) ? 1f : 0f;
                float weight = instant || limitWeights[i] < 0f
                    ? wantWeight
                    : Mathf.MoveTowards(limitWeights[i], wantWeight, blendStep);
                limitWeights[i] = weight;
                if (weight <= 0f) continue;

                Transform wallTransform = wall.transform;
                Vector2 centre = wallTransform.position;
                Vector2 wallDir = wallTransform.up;
                Vector2 normal = wall.Normal;

                // Past either end of the segment the wall does not exist — follow freely there
                Vector2 offset = position - centre;
                if (Mathf.Abs(Vector2.Dot(offset, wallDir)) > wall.Length * 0.5f) continue;

                // Support of the view rectangle along the wall normal: the standoff that keeps the
                // whole view rect on the allowed side of the wall line
                float standoff = Mathf.Abs(halfWidth * normal.x) + Mathf.Abs(halfHeight * normal.y);
                float distance = Vector2.Dot(offset, normal);
                if (distance < standoff) position += normal * ((standoff - distance) * weight);
            }
            return position;
        }

        private Vector2 FollowStep()
        {
            Transform target = Target;
            if (target == null) return followBasePosition;

            // Look-ahead toward facing: read the bus snapshot directly, no PlayerHandler reference needed
            float wantAhead = lookAhead * (PlayerBus.Face == FaceDirection.R ? 1f : -1f);
            lookAheadNow = Mathf.SmoothDamp(lookAheadNow, wantAhead, ref lookAheadVel, lookAheadSmooth);

            Vector2 want = AnchorOf(target) + followOffset + new Vector2(lookAheadNow, 0f);
            followBasePosition = Vector2.SmoothDamp(followBasePosition, want, ref followVel, followSmooth);
            return followBasePosition;
        }

        private Vector2 ShakeStep()
        {
            float dt = GameTimeController.PresentationDeltaTime;
            trauma = Mathf.Max(0f, trauma - traumaDecay * dt);
            if (trauma <= 0f) return Vector2.zero;

            // Squared: small trauma is barely felt, large is strong — more layered than linear
            float shake = trauma * trauma;
            shakeClock += dt;
            float t = shakeClock * shakeFrequency;

            // Perlin rather than pure random: adjacent frames are continuous, shaking rather than twitching
            return new Vector2(
                Mathf.PerlinNoise(seedX, t) * 2f - 1f,
                Mathf.PerlinNoise(seedY, t) * 2f - 1f) * (maxShakeOffset * shake);
        }

        private void ZoomStep()
        {
            zoomRemaining = Mathf.Max(0f, zoomRemaining - GameTimeController.PresentationDeltaTime);
            if (zoomDuration <= 0f || zoomRemaining <= 0f)
            {
                cam.orthographicSize = viewOrthoSize;
                return;
            }

            cam.orthographicSize = viewOrthoSize + zoomAmount * (zoomRemaining / zoomDuration);
        }

        // Accumulate rather than overwrite: chain explosions hit harder instead of restarting each time.
        // Scaled by the user's FX intensity at request time (read once per request):
        // intensity 0 = the camera never shakes; a later change affects new requests, not current trauma.
        private void OnShake(float amount)
        {
            trauma = Mathf.Clamp01(trauma + amount * SettingsStore.FxIntensity);
        }

        private void OnZoom(float amount, float duration)
        {
            if (duration <= 0f) return;

            zoomAmount = amount * SettingsStore.FxIntensity;
            zoomDuration = duration;
            zoomRemaining = duration;
        }
    }
}
