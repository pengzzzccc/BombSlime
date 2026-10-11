using Inkform.Bus;
using Inkform.Interactable;
using Inkform.Life;
using Inkform.Settings;
using UnityEngine;

namespace Inkform.Player
{
    /// <summary>
    /// Rope gun: replaces dash as the primary weapon (dash moved to Shift). Always available — the
    /// rope gun ability is a built-in default (AbilityStore).
    ///
    /// Aiming: the reticle is a free cursor, driven by mouse delta / right stick (the Aim action),
    /// moving around the player between a small safety radius and the current maximum range. The
    /// ballistic solve behind the preview is exact: the reticle turns green when solid terrain
    /// intercepts the trajectory within range.
    ///
    /// Firing is INSTANT: a green reticle means the preview already resolved the intercept — the
    /// press anchors right there and the pull starts the same frame. No projectile flies, nothing
    /// to wait for or miss. A red reticle fires too — a miss shot: the hook snaps out to the
    /// cursor point, hangs there briefly (no anchor, no pull) and retracts with the cancel cue.
    /// A carriable the preview locked (inside the range, in front of the first terrain blocker)
    /// takes precedence over terrain and becomes an eat-pull (swallow on arrival). The hook is a
    /// plain static sprite at the anchor (no rigidbody); the rope renders as a straight line, no
    /// Verlet simulation.
    /// Pulling: hard-velocity straight-line pull toward the anchor (follows moving terrain);
    /// pressing fire or jump mid-pull releases the rope outright; reaching near the anchor
    /// releases automatically, keeping momentum.
    ///
    /// Range: maxRange, fixed.
    /// Attach to the Player; without it the game still plays (PlayerHandler probes with TryGetComponent).
    /// </summary>
    public class RopeGun : MonoBehaviour
    {
        public enum RopePhase { Idle, Pulling, Missing }

        [Header("Range")]
        [SerializeField] private float maxRange = 4f;                       // default reticle / hook travel cap
        [SerializeField, Min(0f)] private float minAimDistance = 0.7f;      // keeps the target safely ahead of the muzzle
        [SerializeField] private LayerMask hitMask = (1 << 6) | (1 << 11);  // Terrain | Breakable

        [Header("Projectile")]
        [SerializeField] private float launchSpeed = 22f;                   // preview parabola solve (no projectile flies anymore)
        [SerializeField] private float bulletGravityScale = 1f;             // preview parabola gravity
        [SerializeField] private float bulletRadius = 0.12f;                // preview cast radius + special-hit probe padding
        [SerializeField] private Sprite bulletSprite;                       // swappable
        [SerializeField] private float spriteAngleOffset = 0f;              // hook sprite facing offset (degrees); 0 = art points right (+X)
        [SerializeField] private float muzzleOffset = 0.5f;                 // parabola origin moved forward along the aim, avoids overlapping the player
        [SerializeField] private float bombDetectRadius = 0.55f;            // carriable probe radius along the fired parabola
        [SerializeField] private float missDangleTime = 0.2f;                // red-shot miss: how long the hook + rope hang at the cursor point before auto-retract

        [Header("Aim")]
        [SerializeField] private float mouseAimSensitivity = 1f;
        [SerializeField] private float stickAimSpeed = 5f;
        [SerializeField] private Sprite crosshairSprite;                    // reticle, swappable
        [SerializeField] private float crosshairSize = 0.6f;
        [SerializeField] private Color hitColor = new Color(0.35f, 1f, 0.35f);
        [SerializeField] private Color missColor = new Color(1f, 0.35f, 0.35f);

        // Snap behaviour (wall/bomb snap, dead zone, adaptive slide speed) is player-facing and
        // lives in SettingsStore — read live at each use site. Only the adaptive speed's runtime
        // state is kept here.

        // Adaptive wall-slide speed: the snap anchor moves as intercept(aimOffset), so its speed
        // depends on the wall angle (a grazing aim barely moves the intercept, a square-on aim
        // moves it 1:1). Each sliding frame measures |anchor move| / |cursor move|, smooths it,
        // and the next frame's aim input is divided by it — the anchor then slides at exactly
        // the free-cursor speed the settings define. Clamped so grazing-angle spikes cannot
        // explode the input.
        private float snapGainSmooth = 1f;
        private bool snapSliding;
        private Vector2 lastAnchorPos;
        private Vector2 lastAimOffset;

        // Set by Aim() on non-zero input, consumed by UpdatePreview: the edge-snap escape is a
        // deliberate "pull the cursor closer" gesture and must not fire on input-less frames —
        // a lock's anchor distance drifts as the player walks, which would otherwise fake the pull
        private bool aimMovedSincePreview;
        private const float SnapGainMin = 0.2f;
        private const float SnapGainMax = 5f;

        [Header("Rope")]
        [SerializeField] private Sprite ropeSegmentSprite;                  // vertical rope strip: the art runs along the sprite's +Y, tiled along the rope's length
        [SerializeField] private Material ropeMaterial;                     // optional; null = the SpriteRenderer default (Sprites-Default)
        [SerializeField] private float ropeWidth = 0.06f;
        [SerializeField] private int ropeSortingOrder = -5;
        [SerializeField] private float anchorClearance = 0.04f;             // hook anchor offset outward from the contact surface (keeps a bit of rope out of the wall)

        [Header("Pull")]
        [SerializeField] private float pullSpeed = 16f;                     // hard-velocity pull (terrain hit / bomb grab shared): must beat the player's gravity or upward pulls fail
        [SerializeField] private float arrivalDistance = 0.7f;              // arrival-at-contact-point threshold (blocked by a wall, the center sits ≈0.5 from the wall; 0.7 = arrived)
        [SerializeField] private float stuckTime = 0.25f;                   // pull-stuck fallback: distance stops dropping this long → release the rope
        [SerializeField] private float swallowDistance = 0.75f;             // distance to a bomb that triggers swallowing

        [Header("Hit")]
        [SerializeField] private float hitStopTime = 0.06f;                 // brief hitstop on hit (via FxBus; GameTimeController caps it at maxHitStop)

        private RopePhase phase = RopePhase.Idle;
        private float missTimer;      // red-shot miss: countdown to the auto-retract (FixedUpdate ticks it)

        /// <summary>Current hook state — the performance recorder logs it so frame costs can be
        /// correlated with the rope's Pulling/Missing phases.</summary>
        public RopePhase Phase => phase;

        // Sensitivity bases: the serialized values are the designers' tuning. The user setting is a
        // multiplier applied on top, captured once in Awake before SettingsStore overrides the fields.
        private float mouseSensitivityBase;
        private float stickAimSpeedBase;

        private Rigidbody2D playerBody;
        private SpriteRenderer bodySprite;   // the player's own renderer — its sorting layer is the rope family's layer
        private PlayerMotor motor;

        private Vector2 aimOffset;      // free cursor offset, clamped between the safe inner radius and current range

        private GameObject hookGo;      // static anchor sprite at the pull target (no physics)

        private ICarriable grapple;        // eat-pull target; null = plain terrain pull
        private Vector2 pullTarget;     // anchor: terrain hit point / carriable's current position
        private Collider2D anchor;      // the collider the terrain hook is attached to; null = static anchor
        private Vector2 anchorLocal;    // hit point in the anchor collider's local space, so the hook follows moving terrain
        private float lastPullDist;     // pull-stuck detection: distance to the anchor last physics step
        private float pullStuck;        // accumulated time the distance has stopped dropping

        private Transform ropeTf;
        private SpriteRenderer ropeRenderer;
        private float ropeTileWidth;    // world size of one tile across the rope's width; derived from the sprite once in Awake

        private Transform reticle;
        private SpriteRenderer reticleSprite;
        private readonly RaycastHit2D[] previewCastHits = new RaycastHit2D[8];

        // The preview's resolved target, refreshed every idle frame and consumed by TryFire:
        // a green reticle already knows where the shot anchors — a terrain intercept (previewHit)
        // or a carriable under the path probe (previewCarriable). Both are only valid when
        // previewGreen; carriables are probed first, so they take precedence.
        private RaycastHit2D previewHit;
        private ICarriable previewCarriable;
        private bool previewGreen;

        // The last green anchor's distance from the player: handed to the free cursor when the
        // reticle flips green→red, so the red reticle starts where the green one vanished.
        private float lastAnchorDistance;

        // Fixed probe buffer for the preview's per-sample carriable sweep (no per-frame allocs)
        private readonly Collider2D[] previewProbeHits = new Collider2D[8];

        // Built once in Awake from the serialized hitMask: UpdatePreview's per-segment casts reuse
        // it instead of reconstructing a filter every segment of every idle frame
        private ContactFilter2D previewFilter;

        private static Sprite discSprite;   // runtime-generated white disc, fallback when no sprite is configured

        private static Sprite DiscSprite
        {
            get
            {
                if (discSprite != null) return discSprite;

                const int size = 32;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float r = size * 0.5f - 1f;
                Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                        tex.SetPixel(x, y, d <= r ? Color.white : Color.clear);
                    }
                }
                tex.Apply();
                discSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
                return discSprite;
            }
        }

        /// <summary>
        /// Effective fire direction: always exactly toward the reticle (the persistent aim cursor),
        /// whether or not the player recently moved it — no movement-direction fallback. Shared by
        /// rope-gun firing and bomb spitting.
        /// </summary>
        public Vector2 EffectiveFireDir
        {
            get
            {
                if (aimOffset.sqrMagnitude > 0.0001f) return aimOffset.normalized;

                // aimOffset is initialized to a non-zero value and only ever clamped to a range ≥ 0.1,
                // so this path is unreachable in practice — defensive fallback only
                return PlayerBus.Face == FaceDirection.R ? Vector2.right : Vector2.left;
            }
        }

        /// <summary>The aim cursor's world position (player centre + aim offset) — the same point
        /// UpdatePreview parks the reticle at. Read by the camera's midpoint follow mode; anything
        /// else that cares where the player is pointing should read this too, not rebuild it.</summary>
        public Vector2 CursorPosition =>
            playerBody != null ? playerBody.position + aimOffset : (Vector2)transform.position + aimOffset;

        void Awake()
        {
            playerBody = GetComponent<Rigidbody2D>();
            bodySprite = GetComponent<SpriteRenderer>();
            TryGetComponent(out motor);
            previewFilter.SetLayerMask(hitMask);
            previewFilter.useTriggers = false;

            mouseSensitivityBase = mouseAimSensitivity;
            stickAimSpeedBase = stickAimSpeed;

            reticle = CreateFx("RopeReticle", out reticleSprite,
                crosshairSprite != null ? crosshairSprite : DiscSprite, 20);
            reticle.localScale = Vector3.one * crosshairSize;

            CreateRope();

            aimOffset = Vector2.right * maxRange * 0.6f;
        }

        void OnEnable()
        {
            LifeBus.Died += OnDied;
            LifeBus.Respawned += OnRespawned;

            // SettingsStore is static and always loaded, so subscription needs no instance guard.
            // Re-apply here too: after a scene reload this RopeGun is fresh while the settings live on.
            SettingsStore.Changed += OnSettingsChanged;
            ApplySensitivity();
            ClampAimOffsetToRange();
        }

        void OnDisable()
        {
            LifeBus.Died -= OnDied;
            LifeBus.Respawned -= OnRespawned;
            SettingsStore.Changed -= OnSettingsChanged;
        }

        void OnDestroy()
        {
            // The rope and an active hook live outside the player hierarchy (see CreateRope /
            // CreateHookAt), so they are not destroyed with it
            if (ropeTf != null) Destroy(ropeTf.gameObject);
            DespawnHook();
        }

        private void OnSettingsChanged() => ApplySensitivity();

        private void ApplySensitivity()
        {
            mouseAimSensitivity = mouseSensitivityBase * SettingsStore.MouseSensitivity;
            stickAimSpeed = stickAimSpeedBase * SettingsStore.StickSensitivity;
        }

        // ---- Input entries (forwarded by PlayerHandler) ----

        /// <summary>Aiming input. pixelDelta = mouse pixel delta (needs conversion to world units by screen
        /// height), otherwise a right-stick analog value (speed × dt). Freely moves the always-visible
        /// reticle within its safe minimum and current maximum range.</summary>
        public void Aim(Vector2 delta, bool pixelDelta)
        {
            if (phase != RopePhase.Idle || LifeBus.IsDead) return;

            // Real input is the only legitimate "pull the cursor" gesture — UpdatePreview's
            // edge-snap escape is evaluated exclusively on these frames (see there)
            if (delta.sqrMagnitude > 0f) aimMovedSincePreview = true;

            // Adaptive wall-slide speed: while the reticle slides on a wall snap, divide the
            // input by the measured geometric gain — the anchor then moves along the wall at
            // the same speed the free cursor would have at the current sensitivity settings,
            // regardless of the wall angle. Same path for mouse and stick.
            if (SettingsStore.RopeAdaptiveSpeed && snapSliding)
                delta *= Mathf.Clamp(1f / snapGainSmooth, 1f / SnapGainMax, 1f / SnapGainMin);

            if (pixelDelta)
            {
                Camera cam = Camera.main;
                float worldPerPixel = cam != null
                    ? cam.orthographicSize * 2f / Mathf.Max(1f, Screen.height)
                    : 0.01f;
                aimOffset += delta * worldPerPixel * mouseAimSensitivity;
            }
            else
            {
                aimOffset += delta * (stickAimSpeed * Time.deltaTime);
            }

            ClampAimOffsetToRange();
        }

        public void TryFire()
        {
            if (LifeBus.IsDead) return;

            if (phase != RopePhase.Idle)
            {
                Cancel();           // pressing again mid-pull = release the rope
                return;
            }

            // Instant hit: the press anchors right where the preview resolved the intercept, same
            // frame (green). A red reticle fires too — the miss shot at the tail of this method.
            Vector2 fireDir = EffectiveFireDir;
            RopeGunBus.RaiseFired(fireDir);

            // The press consumes exactly what the preview resolved, so what the reticle shows is
            // what the shot does: a green carriable lock becomes an eat-pull (highest priority).
            // A carriable destroyed since the preview reads non-null through the interface (same
            // fake-null guard Finish uses) — and previewHit was not refreshed on a carriable
            // frame, so there is no terrain anchor to fall back on: that press is a miss shot.
            bool carriableLost = false;
            if (previewGreen && previewCarriable != null)
            {
                if (previewCarriable as MonoBehaviour != null)
                {
                    ICarriable carriable = previewCarriable;
                    Vector2 targetPos = carriable.transform.position;
                    CreateHookAt(targetPos, targetPos - playerBody.position);
                    StartPullingCarriable(carriable);
                    return;
                }
                carriableLost = true;
            }

            if (!previewGreen || carriableLost)
            {
                // Miss shot: nothing intercepts along the path, so the hook snaps out to the
                // cursor point and hangs there — no anchor, no pull, movement stays free. The
                // rope draws itself in LateUpdate from pullTarget like any active rope; the
                // Missing-phase timer in FixedUpdate retracts it.
                Vector2 missPoint = playerBody.position + aimOffset;
                CreateHookAt(missPoint, aimOffset);
                pullTarget = missPoint;
                phase = RopePhase.Missing;
                missTimer = Mathf.Max(0f, missDangleTime);
                return;
            }

            Vector2 hitPoint = previewHit.point;
            CreateHookAt(hitPoint, hitPoint - playerBody.position);
            AttachAtTerrain(previewHit);
        }

        // Builds the static hook sprite at the anchor: a plain renderer, no rigidbody — the hook
        // never participates in physics, it is just the rope's far end (and follows the pull
        // target through LateUpdate while pulling).
        private void CreateHookAt(Vector2 position, Vector2 dir)
        {
            hookGo = new GameObject("GrappleHook");
            hookGo.transform.position = position;

            var sr = hookGo.AddComponent<SpriteRenderer>();
            sr.sprite = bulletSprite != null ? bulletSprite : DiscSprite;
            // Same sorting layer as the rope/reticle: the player's (follows the body if it ever moves)
            if (bodySprite != null) sr.sortingLayerID = bodySprite.sortingLayerID;
            sr.sortingOrder = 15;

            if (dir.sqrMagnitude > 0.0001f)
                hookGo.transform.rotation = Quaternion.Euler(0f, 0f,
                    Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg + spriteAngleOffset);

            ropeRenderer.enabled = true;
        }

        /// <summary>Called by PlayerHandler on jump press: during a pull or a miss dangle, release
        /// the rope and let the jump happen.</summary>
        public void DetachOnJump()
        {
            if (phase != RopePhase.Pulling && phase != RopePhase.Missing) return;
            RopeGunBus.RaiseRopeCancelled();   // deliberate break, not a natural end
            Finish();
        }

        // ---- Internal flow ----

        /// <summary>Releases an active pull outright. Invoked by re-pressing fire, and by
        /// PlayerHandler.Dash — the dash takes over motion and must not fight the pull's
        /// per-physics-step velocity writes.</summary>
        public void Cancel()
        {
            if (phase == RopePhase.Idle) return;
            RopeGunBus.RaiseRopeCancelled();   // deliberate break (re-press / dash), not a natural end
            Finish();
        }

        // Terrain anchoring from the preview's resolved intercept: no flight, no range re-check
        // (the preview already clipped the path to the legal range circle), the hook sprite sits
        // on the anchor and the pull starts immediately.
        private void AttachAtTerrain(RaycastHit2D hit)
        {
            Vector2 hitPoint = AnchorPointOf(hit);

            pullTarget = hitPoint;
            // Terrain may move (PatrolMover platforms, spinners, ...): keep the anchor attached to the
            // hit collider's local point so hook + rope follow the object instead of hanging in space.
            // Static terrain colliders never move, so this local-space bookkeeping is a no-op there
            anchor = hit.collider;
            anchorLocal = anchor.transform.InverseTransformPoint(hitPoint);
            motor?.SetMoveLocked(true);

            // Direction from the player toward the anchor, for directional feedback (haptics, ...)
            RopeGunBus.RaiseHit((hitPoint - playerBody.position).normalized);

            // Brief hitstop at the hit moment: impact feel. Via FxBus; ScreenFx restores timeScale
            if (hitStopTime > 0f) FxBus.RaiseHitStop(hitStopTime);

            // Straight-line pull: no hanging, no swinging — the player is pulled along a straight line
            // toward the anchor (Pulling); pressing fire/jump mid-pull stops the force
            phase = RopePhase.Pulling;
            lastPullDist = float.MaxValue;      // first frame always advances; stuck timing only starts when truly stopped
            pullStuck = 0f;
        }

        // The anchor of a resolved intercept: the contact point nudged outward from the surface —
        // the contact point itself sits on the terrain, and a small offset keeps a bit of rope from
        // rendering inside the wall. Shared by the reticle snap (shows where the shot would anchor)
        // and AttachAtTerrain (where it actually does). The normal direction convention is easy to
        // get backwards — check the sign against the player (the side the cast came from).
        private Vector2 AnchorPointOf(RaycastHit2D hit)
        {
            Vector2 outward = hit.normal;
            if (Vector2.Dot(outward, playerBody.position - hit.point) < 0f) outward = -outward;
            return hit.point + outward * Mathf.Max(anchorClearance, 0.01f);
        }

        private void StartPullingCarriable(ICarriable target)
        {
            target.MarkRopeGrappled();
            grapple = target;

            // The anchor must be live before the phase flips: LateUpdate draws the rope (and snaps the
            // hook) from pullTarget, but FixedUpdate only refreshes it on the NEXT physics step — and
            // the hitstop below zeroes timeScale, which suspends FixedUpdate entirely. A stale
            // pullTarget would therefore hang the rope off the previous shot's anchor (or the world
            // origin on the first grab) for the whole freeze, not just one frame.
            pullTarget = target.transform.position;
            phase = RopePhase.Pulling;
            motor?.SetMoveLocked(true);

            // Same directional feedback as the terrain hit: from the player toward the grabbed target
            RopeGunBus.RaiseHit(((Vector2)target.transform.position - playerBody.position).normalized);

            // Same reset as the terrain path: a stuck-timeout release leaves pullStuck at the limit,
            // and without clearing it the very first PullStep would trip the timeout and cancel the grab
            lastPullDist = float.MaxValue;
            pullStuck = 0f;

            // Grabbing a carriable is also a "hit", same brief hitstop
            if (hitStopTime > 0f) FxBus.RaiseHitStop(hitStopTime);
        }

        private void Finish()
        {
            // The interface has no Unity fake-null overload: cast to MonoBehaviour first to recognize
            // destroyed targets
            if (grapple as MonoBehaviour != null)
            {
                grapple.ClearRopeGrappled();
            }
            grapple = null;
            anchor = null;
            motor?.SetMoveLocked(false);
            phase = RopePhase.Idle;
            DespawnHook();
            ropeRenderer.enabled = false;
        }

        // A pull ending on its own (arrival, stuck timeout, failed swallow) — the counterpart of
        // Cancel's deliberate break, so feedback can tell the two apart
        private void ReleaseNaturally()
        {
            RopeGunBus.RaiseRopeReleased();
            Finish();
        }

        private void DespawnHook()
        {
            if (hookGo != null) Destroy(hookGo);
            hookGo = null;
        }

        // ---- Frame loop ----

        void Update()
        {
            if (LifeBus.IsDead) return;

            // The reticle updates every frame while idle, so the player always knows exactly where
            // the next shot will go
            if (phase == RopePhase.Idle) UpdatePreview();
        }

        void FixedUpdate()
        {
            if (phase == RopePhase.Idle || LifeBus.IsDead) return;

            float dt = Time.fixedDeltaTime;

            // Miss shot: nothing to pull toward — the hook just hangs at the fired cursor point
            // for its moment, then retracts. Must NOT reach PullStep below: that hard-writes the
            // body's velocity and would yank the player toward a point in open air.
            if (phase == RopePhase.Missing)
            {
                missTimer -= dt;
                if (missTimer <= 0f)
                {
                    RopeGunBus.RaiseRopeCancelled();   // retract sounds like a deliberate break
                    Finish();
                }
                return;
            }

            if (grapple != null)
            {
                // Eat-pull: the anchor follows the target; swallowing ends the pull.
                // The interface has no Unity fake-null overload: cast to MonoBehaviour first
                // to recognize a blasted-away target
                if (grapple as MonoBehaviour == null) { Finish(); return; }
                pullTarget = grapple.transform.position;

                if (PullStep(pullTarget, dt) <= swallowDistance)
                {
                    // Cannot swallow (mouth full etc.): release the rope, do not stall. A successful
                    // swallow already has its own feedback (ItemBus.ItemStored)
                    if (!grapple.TrySwallowByRope(transform)) RopeGunBus.RaiseRopeReleased();
                    Finish();
                    return;
                }

                if (pullStuck >= stuckTime) ReleaseNaturally();   // target behind a wall and unreachable: timeout release
                return;
            }

            // Terrain pull: the anchor follows the hit collider (static terrain never moves;
            // moving platforms/spinners carry the hook with them), release on arrival
            if (anchor != null) pullTarget = anchor.transform.TransformPoint(anchorLocal);
            if (PullStep(pullTarget, dt) <= arrivalDistance)
            {
                // Reached the contact point (blocked by a wall, the center sits ≈0.5 from it):
                // release, keep momentum
                ReleaseNaturally();
                return;
            }
            if (pullStuck >= stuckTime) ReleaseNaturally();   // stuck fallback: sliding along a wall keeps distance dropping, not stuck
        }

        // Single pull step (terrain Pulling and eat Pulling share it):
        // hard velocity write-back (beats gravity, straight line) + stuck-distance advance.
        // Returns the current distance to the target; the endpoint decision is the caller's.
        private float PullStep(Vector2 target, float dt)
        {
            Vector2 to = target - playerBody.position;
            float dist = to.magnitude;
            if (dist < 0.0001f) return dist;

            // The player's gravity (≈19.6 m/s² at the prefab's gravity 2) would crush an acceleration-style pull (upward
            // pulls would fail entirely); hard-writing beats gravity, path near-straight
            playerBody.linearVelocity = to / dist * pullSpeed;

            // Stuck advance: distance stops dropping (sliding along a wall keeps it dropping, not
            // stuck) → accumulated timeout releases the rope
            if (dist < lastPullDist - 0.01f) pullStuck = 0f;
            else pullStuck += dt;
            lastPullDist = dist;
            return dist;
        }

        private static bool TryGetCarriable(Collider2D collider, out ICarriable carriable)
        {
            carriable = null;
            if (collider == null) return false;

            Inkform.Interactable.Interactable node =
                collider.GetComponentInParent<Inkform.Interactable.Interactable>();
            return node != null && node.TryGetPart(out carriable);
        }

        // Aim prediction: the reticle is a free cursor and the trajectory is the parabola solved to pass
        // through it. Fixed-step samples walk the path: at each one a carriable probe (bombs etc.
        // live on Default, which the terrain mask never sees) runs FIRST, then a terrain circle cast.
        // Whatever stops the walk is the snap target; only solid Terrain/Breakable colliders or a
        // carriable inside the range make the reticle green. TryFire consumes this result as-is.
        private void UpdatePreview()
        {
            Vector2 aim = aimOffset.sqrMagnitude > 0.0001f ? aimOffset.normalized : Vector2.right;
            Vector2 rangeCenter = playerBody.position;

            // Muzzle origin moved forward along the aim, avoids overlapping the player
            Vector2 origin = rangeCenter + aim * muzzleOffset;
            Vector2 target = rangeCenter + aimOffset;
            Vector2 g = Physics2D.gravity * bulletGravityScale;

            SolveBallistic(origin, target, launchSpeed, g, out Vector2 v0);

            float stepDt = Time.fixedDeltaTime;
            Vector2 last = origin;
            bool hit = false;

            // Re-resolved from scratch every frame: a terrain hit must not inherit last frame's
            // carriable, or the reticle and TryFire would still treat that bomb as the target
            previewCarriable = null;

            // The final segment is clipped before any query, so targets beyond the legal range can
            // never make the reticle green.
            for (float t = stepDt; ; t += stepDt)
            {
                Vector2 p = origin + v0 * t + 0.5f * g * t * t;
                p = ClipSegmentToRange(rangeCenter, last, p, maxRange, out bool reachedRange);

                // Carriable probe first (TryFire consumes previewCarriable directly):
                // finding one along the path means the press would eat-pull it. Skipped entirely
                // when bombSnap is off — the reticle never gets hijacked by a bomb on the path,
                // and the per-frame probes stop costing anything.
                if (SettingsStore.RopeBombSnap)
                {
                    int probeCount = Physics2D.OverlapCircleNonAlloc(
                        p, bombDetectRadius + bulletRadius, previewProbeHits);
                    for (int i = 0; i < probeCount; i++)
                    {
                        // The probe circle reaches past the range circle by its own radius, so the
                        // carriable itself must sit inside the range — otherwise walking away from a
                        // locked bomb would keep the reticle stuck on it outside the circle
                        if (TryGetCarriable(previewProbeHits[i], out ICarriable carriable)
                            && ((Vector2)carriable.transform.position - rangeCenter).sqrMagnitude
                                <= maxRange * maxRange)
                        {
                            previewCarriable = carriable;
                            hit = true;
                            break;
                        }
                    }
                    if (hit) break;
                }

                Vector2 seg = p - last;
                float segLen = seg.magnitude;
                if (segLen > 0.0001f)
                {
                    int hitCount = Physics2D.CircleCast(
                        last, bulletRadius, seg / segLen, previewFilter, previewCastHits, segLen);
                    if (hitCount > 0)
                    {
                        hit = true;
                        previewHit = previewCastHits[0];   // nearest blocker = the instant shot's anchor
                        break;
                    }
                }

                if (reachedRange) break;
                last = p;
            }

            bool wasGreen = previewGreen;
            previewGreen = hit;

            // The interface has no Unity fake-null overload: a carriable destroyed since the last
            // frame (e.g. a bomb swallowed or blown up mid-aim) still reads non-null through the
            // interface reference — clear it the same way Finish does. Also covers a stale hit
            // from this frame's probe (the physics scene lags destroys by a sync).
            if (!hit || previewCarriable as MonoBehaviour == null) previewCarriable = null;

            // Reticle: green SNAPS to the resolved target — a carriable it would eat-pull (follows
            // it as it swings) or, with wallSnap on, the terrain anchor — so what the player sees
            // glowing is what the press pulls to. With wallSnap off the green reticle rides the
            // free cursor instead (lastAnchorDistance then equals the cursor's own length, so the
            // escape and the handover below degenerate into no-ops — color and firing unchanged).
            // Red rides the free cursor; on the green→red flip the free cursor adopts the last
            // anchor's distance first, so the red reticle starts where the green one vanished.
            if (hit)
            {
                Vector2 anchorPos = previewCarriable != null
                    ? (Vector2)previewCarriable.transform.position
                    : SettingsStore.RopeWallSnap ? AnchorPointOf(previewHit) : target;
                lastAnchorDistance = Vector2.Distance(rangeCenter, anchorPos);

                // Edge-snap escape: pulling the free cursor CLOSER than the snap target releases
                // the snap — the reticle turns red right where the player pulled it (the cursor's
                // own length is kept; the handover below must not stretch it back to the anchor).
                // The dead zone keeps the flip from flickering at the boundary.
                // Evaluated only on frames with real aim input: the check reads the anchor's
                // DISTANCE from the player, which drifts while a lock is held and the player
                // walks (a bomb receding as the player backs off) — an ungated escape would fire
                // with no input at all, skip the wasGreen handover below, and leave the cursor
                // parked short of every anchor on the ray: the reticle could then never turn
                // green again until the aim moved (stuck-red crosshair, wall ungrabbable).
                if (aimMovedSincePreview &&
                    aimOffset.magnitude < lastAnchorDistance - SettingsStore.RopeSnapDeadZone)
                {
                    previewGreen = false;
                    reticle.position = target;
                }
                else
                {
                    reticle.position = anchorPos;

                    // Adaptive wall-slide gain: measure |anchor move| / |cursor move| on
                    // consecutive terrain-slide frames only (bomb snaps are skipped — their
                    // target moves on its own and would poison the ratio; so do switch frames
                    // and frames the aim stood still, e.g. on a moving platform).
                    if (SettingsStore.RopeWallSnap && previewCarriable == null && snapSliding)
                    {
                        float aimMove = (aimOffset - lastAimOffset).magnitude;
                        float anchorMove = (anchorPos - lastAnchorPos).magnitude;
                        if (aimMove > 0.001f && anchorMove > 0.0001f)
                        {
                            float gain = Mathf.Clamp(anchorMove / aimMove, SnapGainMin, SnapGainMax);
                            snapGainSmooth = Mathf.Lerp(snapGainSmooth, gain, 0.35f);
                        }
                    }
                }
            }
            else
            {
                if (wasGreen)
                {
                    aimOffset = aim * lastAnchorDistance;
                    ClampAimOffsetToRange();
                    target = rangeCenter + aimOffset;
                }
                reticle.position = target;
            }

            snapSliding = previewGreen && SettingsStore.RopeWallSnap && previewCarriable == null;
            lastAnchorPos = reticle.position;
            lastAimOffset = aimOffset;
            aimMovedSincePreview = false;

            reticleSprite.color = previewGreen ? hitColor : missColor;
            float s = crosshairSize * (previewGreen ? 1.25f : 1f);
            reticle.localScale = new Vector3(s, s, 1f);
        }

        // Clips start→end to the first exit from a circle. Callers keep start inside the circle; an
        // already-outside muzzle (only possible with a deliberately tiny range) collapses safely.
        private static Vector2 ClipSegmentToRange(Vector2 center, Vector2 start, Vector2 end,
                                                  float range, out bool clipped)
        {
            float rangeSq = range * range;
            if ((end - center).sqrMagnitude <= rangeSq)
            {
                clipped = false;
                return end;
            }

            clipped = true;
            Vector2 d = end - start;
            float a = Vector2.Dot(d, d);
            if (a <= 0.0000001f) return start;

            Vector2 m = start - center;
            float b = 2f * Vector2.Dot(m, d);
            float c = Vector2.Dot(m, m) - rangeSq;
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f) return start;

            float t = (-b + Mathf.Sqrt(discriminant)) / (2f * a);
            return start + d * Mathf.Clamp01(t);
        }

        /// <summary>
        /// Ballistic solve: given the start and target points, launch speed magnitude, and gravity, find
        /// the initial velocity that sends the projectile through the target. Letting u = t² and
        /// substituting into the parabola equation yields a quadratic in u; take the low-arc root
        /// (smaller t). t is clamped with a lower bound to avoid explosive speeds when the target is
        /// too close.
        /// </summary>
        private void SolveBallistic(Vector2 origin, Vector2 target, float speed,
                                    Vector2 g, out Vector2 velocity)
        {
            float dx = target.x - origin.x;
            float dy = target.y - origin.y;
            float gy = g.y;                 // negative (downward)

            // 0.25·g²·u² − (dy·g + v²)·u + (dx² + dy²) = 0
            float a = 0.25f * gy * gy;
            float b = -(dy * gy + speed * speed);
            float c = dx * dx + dy * dy;

            float u;
            if (a > 1e-8f && b * b >= 4f * a * c)
            {
                float disc = Mathf.Sqrt(b * b - 4f * a * c);
                u = (-b - disc) / (2f * a);     // low-arc root
                u = Mathf.Max(u, 0f);
            }
            else
            {
                u = 0f;                          // target unreachable (cannot happen: the cursor is clamped inside the range)
            }

            float flightTime = Mathf.Max(Mathf.Sqrt(u), 0.05f);
            velocity = new Vector2(
                dx / flightTime,
                dy / flightTime - 0.5f * gy * flightTime);
        }

        void LateUpdate()
        {
            if (phase == RopePhase.Idle) return;

            // The static hook follows the live anchor — eat-pull follows the carriable, terrain
            // pull follows the moving collider (both synced manually; static terrain never moves)
            if (hookGo != null) hookGo.transform.position = pullTarget;

            // The reticle is a child of the player and UpdatePreview only places it while idle:
            // pin it to the hook, or it rides along with the player away from the anchor
            reticle.position = pullTarget;

            // Rope rendering: a straight span between the two ends — the rope gun uses no rope
            // simulation, so the rope is always straight (no sag)
            Vector2 a = pullTarget;             // anchor: terrain hit point / carriable position
            Vector2 b = playerBody.position;

            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.0001f)
            {
                // Hook sitting on the player: the direction is undefined, so draw nothing rather
                // than flash a rope at an arbitrary angle
                ropeRenderer.enabled = false;
                return;
            }

            ropeRenderer.enabled = true;
            ropeTf.position = (a + b) * 0.5f;                    // the sprite's pivot is Center
            // The art runs along the sprite's own +Y, so aiming +Y down the rope is a -90° offset
            ropeTf.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg - 90f);
            ropeTf.localScale = new Vector3(ropeWidth / ropeTileWidth, 1f, 1f);
            ropeRenderer.size = new Vector2(ropeTileWidth, len); // tiles along the length: fixed spacing
        }

        // ---- Bus callbacks ----

        private void ClampAimOffsetToRange()
        {
            float effectiveMax = Mathf.Max(0.1f, maxRange);
            float effectiveMin = Mathf.Min(Mathf.Max(0f, minAimDistance), effectiveMax);
            Vector2 fallback = PlayerBus.Face == FaceDirection.R ? Vector2.right : Vector2.left;
            Vector2 direction = aimOffset.sqrMagnitude > 0.0001f ? aimOffset.normalized : fallback;
            float distance = Mathf.Clamp(aimOffset.magnitude, effectiveMin, effectiveMax);
            aimOffset = direction * distance;
        }

        private void OnDied(DeathContext ctx)
        {
            if (ctx.Victim != gameObject) return;

            // Finish is idempotent: clears the grapple mark, unlocks movement, resets state, destroys
            // the hook — all in one
            Finish();
        }

        private void OnRespawned(GameObject victim, Vector2 pos)
        {
            if (victim != gameObject) return;
            aimOffset = (PlayerBus.Face == FaceDirection.R ? Vector2.right : Vector2.left)
                * maxRange * 0.6f;
            ClampAimOffsetToRange();
            // The reticle repositions from the new aimOffset on the next UpdatePreview
        }

        // ---- Runtime objects ----

        private Transform CreateFx(string name, out SpriteRenderer sprite, Sprite fallback, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            sprite = go.AddComponent<SpriteRenderer>();
            sprite.sprite = fallback;
            // Rope-family rendering rides the player's sorting layer (same stance as
            // InteractionPromptPart copying its host): if the body ever moves layers, the rope,
            // hook and reticle follow without a code change. Relative orders stay as authored.
            if (bodySprite != null) sprite.sortingLayerID = bodySprite.sortingLayerID;
            sprite.sortingOrder = sortingOrder;
            return go.transform;
        }

        // Rope rendering is a tiled SpriteRenderer, not a LineRenderer: a LineRenderer maps the
        // texture's U axis along the line's length, but the rope art is a narrow vertical strip inside
        // a mostly-empty atlas, so the line sampled the atlas's blank columns and only a fifth of it
        // ever showed pixels. A SpriteRenderer samples the sprite's sub-rect instead — the strip is
        // all there is. The strip is turned along the rope by rotating the transform, and
        // SpriteDrawMode.Tiled repeats it so the link spacing holds at any rope length.
        private void CreateRope()
        {
            ropeTf = CreateFx("Rope", out ropeRenderer,
                ropeSegmentSprite != null ? ropeSegmentSprite : DiscSprite, ropeSortingOrder);
            // World space, not a child of the player: LateUpdate sizes the rope in world units, and
            // the player root's scale (1.2 on the prefab) would otherwise stretch it past both ends.
            // Destroyed with the player in OnDestroy
            ropeTf.SetParent(null, false);
            ropeRenderer.drawMode = SpriteDrawMode.Tiled;
            ropeRenderer.tileMode = SpriteTileMode.Continuous;  // trailing part-tile just clips; spacing stays exact
            // sharedMaterial, not material: nothing here writes to the material, so there is no reason
            // to instantiate a per-player copy of it
            if (ropeMaterial != null) ropeRenderer.sharedMaterial = ropeMaterial;
            ropeRenderer.enabled = false;

            // Tiled mode clips any axis sized under one whole tile, so the rope's width must hold
            // exactly one tile and the visual thickness comes from localScale instead (applied in
            // LateUpdate, so ropeWidth stays tunable in play mode) — that keeps the sprite's
            // pixels-per-unit (link spacing) and ropeWidth (thickness) independent of each other.
            Sprite s = ropeRenderer.sprite;
            ropeTileWidth = s.rect.width / s.pixelsPerUnit;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, maxRange);
        }
    }
}
