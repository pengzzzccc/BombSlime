using Inkform.Audio;
using UnityEngine;

namespace Inkform.Interactable.Parts
{
    /// <summary>
    /// Patrol translation along a waypoint route. Waypoints are local offsets based on the world
    /// position captured at Attach — the object can be placed anywhere and the patrol starts from
    /// there; moving the whole group needs no endpoint re-tuning. Combine with Spinner for a
    /// "rotating gear that strafes". Pure driver: does not consume contact.
    ///
    /// Route: any number of waypoints (fewer than 2 falls back to the legacy pointA/pointB pair,
    /// so prefabs serialized before waypoints existed behave exactly as before). The first leg
    /// launches from the placed position toward waypoints[1] — the placed position acts as a
    /// pseudo-first-point for that opening leg only, same as the legacy two-point behavior.
    ///
    /// Modes: PingPong reverses at both route ends (legacy behavior); Loop wraps the last point
    /// straight back to the first and keeps circling. Speed: Fixed uses the forward/return speeds
    /// (legacy), RandomRange re-rolls a speed within [min, max] at the start of every leg.
    /// Curves: forwardCurve shapes legs traveling in ascending-index direction, returnCurve the
    /// descending ones (Loop only ever ascends).
    /// </summary>
    public class PatrolMover : MonoBehaviour, IInteractablePart
    {
        public enum PatrolMode
        {
            PingPong,   // A→B→C→B→A: reverse at both route ends
            Loop        // A→B→C→A: wrap the last point back to the first, keep circling
        }

        public enum SpeedMode
        {
            Fixed,          // forward speed / return speed per direction (legacy)
            RandomRange     // re-roll a random speed within [min, max] at every leg start
        }

        [Header("Route")]
        [Tooltip("Waypoints as local offsets relative to the initial position. Fewer than 2 entries fall back to the legacy Point A / Point B pair below")]
        [SerializeField] private Vector2[] waypoints;
        [Tooltip("How the route is traversed: reverse at the ends, or wrap around and keep circling")]
        [SerializeField] private PatrolMode mode = PatrolMode.PingPong;
        [Tooltip("Per-waypoint stop times (seconds), index-aligned with Waypoints. Entries beyond the route length are ignored; unlisted waypoints fall back to the endpoint stops below")]
        [SerializeField] private float[] stopTimes;

        [Header("Legacy Two-Point Route (used when Waypoints has fewer than 2 entries)")]
        [Tooltip("Left endpoint (local offset relative to the initial position)")]
        [SerializeField] private Vector2 pointA = new Vector2(-2.5f, 0f);
        [Tooltip("Right endpoint (local offset relative to the initial position)")]
        [SerializeField] private Vector2 pointB = new Vector2(2.5f, 0f);

        [Header("Speed")]
        [Tooltip("Fixed mode: forward movement speed, units/second")]
        [InspectorName("Forward Speed")]
        [SerializeField, Min(0f)] private float speed = 2.2f;
        [Tooltip("Fixed mode: return movement speed, units/second. Zero uses Forward Speed")]
        [SerializeField, Min(0f)] private float returnSpeed;
        [Tooltip("Fixed: Forward / Return Speed. Random: every leg re-rolls a speed between the min and max below")]
        [SerializeField] private SpeedMode speedMode = SpeedMode.Fixed;
        [Tooltip("Random mode: lowest speed a leg can roll, units/second")]
        [SerializeField, Min(0f)] private float randomSpeedMin = 1.5f;
        [Tooltip("Random mode: highest speed a leg can roll, units/second")]
        [SerializeField, Min(0f)] private float randomSpeedMax = 3f;

        [Header("Endpoint Stops (fallback when Stop Times has no entry for a waypoint)")]
        [Tooltip("Seconds to remain at the first waypoint before departing")]
        [SerializeField, Min(0f)] private float stopTimeAtA;
        [Tooltip("Seconds to remain at the last waypoint before departing")]
        [SerializeField, Min(0f)] private float stopTimeAtB;

        [Header("Movement Curves")]
        [Tooltip("Normalized time to normalized distance while moving in ascending-index direction")]
        [SerializeField] private AnimationCurve forwardCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("Normalized time to normalized distance while moving in descending-index direction")]
        [SerializeField] private AnimationCurve returnCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Header("Sound")]
        [Tooltip("Constant looping sound carried along with the mover (like a laser hum). Always positional: full volume up close, fading to silence at the Cue's Falloff Range. Empty = silent")]
        [SerializeField] private SoundCue loopCue;

        private Interactable root;
        private Rigidbody2D body;       // when a rigidbody exists, sync its position so players standing on top get carried
        private Vector2[] route;        // world-space waypoints resolved at Attach
        private Vector2 startPos;   // world position at Attach, patrol baseline
        private Vector2 segmentStart;
        private int targetIndex;        // route index of the current leg's destination
        private int direction = 1;      // +1 ascending (forward curve), -1 descending (return curve)
        private float legSpeed;         // speed of the current leg: fixed per direction, or rolled per leg
        private MotionPhase motionPhase;
        private float segmentElapsed;
        private float waitRemaining;

        private const float DistanceEpsilon = 0.0001f;
        private const int MaxTransitionsPerTick = 64;

        private enum MotionPhase
        {
            Moving,
            Waiting,
            Stationary
        }

        public void Attach(Interactable root)
        {
            this.root = root;
            body = root.GetComponent<Rigidbody2D>();
            startPos = root.transform.position;

            // The loop rides an AmbientSource on the moving root, so it follows the patrol (same
            // component the laser hum uses). Added here so a Cue in the slot is all a prefab needs
            if (loopCue != null) root.gameObject.AddComponent<AmbientSource>().Play(loopCue);

            // Resolve the route: explicit waypoints, or the legacy pointA/pointB pair — pre-asset
            // prefabs keep their serialized data and their exact old behavior
            route = ResolveWaypoints();
            if (route.Length < 2 || AllCoincident(route))
            {
                // A fully degenerate route has nothing to patrol; the object simply stays put
                route = new[] { startPos, startPos };
                motionPhase = MotionPhase.Stationary;
                segmentStart = startPos;
                targetIndex = 1;
                return;
            }

            segmentStart = startPos;    // opening leg: placed position → waypoints[1] (legacy first-target semantics)
            targetIndex = 1;
            direction = 1;
            segmentElapsed = 0f;
            waitRemaining = 0f;
            legSpeed = RollLegSpeed();
            motionPhase = MotionPhase.Moving;
        }

        public bool HandleContact(ContactPhase phase, Collider2D other) => false;

        void Update()
        {
            Advance(Time.deltaTime);
        }

        // Kept separate from Update so movement remains deterministic and can be covered by EditMode tests.
        private void Advance(float deltaTime)
        {
            if (root == null || deltaTime <= 0f || motionPhase == MotionPhase.Stationary) return;

            float remaining = deltaTime;
            for (int transitions = 0; transitions < MaxTransitionsPerTick && remaining > 0f; transitions++)
            {
                if (motionPhase == MotionPhase.Waiting)
                {
                    float waitStep = Mathf.Min(remaining, waitRemaining);
                    waitRemaining -= waitStep;
                    remaining -= waitStep;

                    if (waitRemaining > 0f) return;
                    BeginNextSegment();
                    continue;
                }

                Vector2 target = route[targetIndex];
                if (legSpeed <= 0f) return;

                float segmentDistance = Vector2.Distance(segmentStart, target);
                if (segmentDistance <= DistanceEpsilon)
                {
                    SetPosition(target);
                    FinishSegment();
                    continue;
                }

                float duration = segmentDistance / legSpeed;
                float timeToEndpoint = Mathf.Max(0f, duration - segmentElapsed);
                float movementStep = Mathf.Min(remaining, timeToEndpoint);
                segmentElapsed += movementStep;
                remaining -= movementStep;

                if (segmentElapsed >= duration)
                {
                    SetPosition(target);
                    FinishSegment();
                    continue;
                }

                float normalizedTime = segmentElapsed / duration;
                AnimationCurve curve = direction > 0 ? forwardCurve : returnCurve;
                float normalizedDistance = curve == null || curve.length == 0
                    ? normalizedTime
                    : curve.Evaluate(normalizedTime);
                SetPosition(Vector2.Lerp(segmentStart, target, Mathf.Clamp01(normalizedDistance)));
                return;
            }
        }

        private Vector2[] ResolveWaypoints()
        {
            if (waypoints != null && waypoints.Length >= 2)
            {
                Vector2[] world = new Vector2[waypoints.Length];
                for (int i = 0; i < waypoints.Length; i++) world[i] = startPos + waypoints[i];
                return world;
            }
            return new[] { startPos + pointA, startPos + pointB };
        }

        private static bool AllCoincident(Vector2[] points)
        {
            for (int i = 1; i < points.Length; i++)
                if ((points[i] - points[0]).sqrMagnitude > DistanceEpsilon * DistanceEpsilon) return false;
            return true;
        }

        /// <summary>
        /// Speed for the leg about to begin. Fixed mode keeps the legacy direction split (return
        /// speed zero falls back to the forward speed); RandomRange re-rolls within the configured
        /// interval on every leg — organic patrol pacing, never the same lap twice.
        /// </summary>
        private float RollLegSpeed()
        {
            if (speedMode == SpeedMode.RandomRange)
            {
                float min = Mathf.Max(0f, randomSpeedMin);
                float max = Mathf.Max(min, randomSpeedMax);
                return Random.Range(min, max);
            }
            if (direction > 0) return Mathf.Max(0f, speed);
            return returnSpeed > 0f ? returnSpeed : Mathf.Max(0f, speed);
        }

        private float StopAt(int index)
        {
            if (stopTimes != null && index < stopTimes.Length)
                return Mathf.Max(0f, stopTimes[index]);
            if (index == route.Length - 1) return Mathf.Max(0f, stopTimeAtB);
            if (index == 0) return Mathf.Max(0f, stopTimeAtA);
            return 0f;      // intermediate waypoint without a Stop Times entry: pass right through
        }

        private void FinishSegment()
        {
            waitRemaining = StopAt(targetIndex);
            motionPhase = MotionPhase.Waiting;
        }

        private void BeginNextSegment()
        {
            if (mode == PatrolMode.Loop)
            {
                direction = 1;      // looping only ever ascends; the return curve is a PingPong concern
                targetIndex = (targetIndex + 1) % route.Length;
            }
            else
            {
                if (targetIndex == route.Length - 1) direction = -1;
                else if (targetIndex == 0) direction = 1;
                targetIndex += direction;
            }

            segmentStart = route[PreviousIndex()];
            segmentElapsed = 0f;
            legSpeed = RollLegSpeed();
            motionPhase = MotionPhase.Moving;
        }

        // The waypoint the current leg started from: where we came from is one direction-step
        // behind the target — except after a Loop wrap, where it is the route's last point
        private int PreviousIndex()
        {
            if (mode == PatrolMode.Loop && targetIndex == 0 && direction > 0) return route.Length - 1;
            return Mathf.Clamp(targetIndex - direction, 0, route.Length - 1);
        }

        // The project sets m_AutoSyncTransforms = 0: transform and rigidbody positions do not sync,
        // write both — without the rigidbody sync the physics collider stays behind and players
        // standing on top get left behind (no push, no follow to speak of)
        private void SetPosition(Vector2 next)
        {
            root.transform.position = next;
            if (body != null) body.position = next;
        }

        // In the editor Attach never ran; draw the hint from the current transform.position
        void OnDrawGizmosSelected()
        {
            Vector3 basePos = transform.position;
            Vector2[] local = waypoints != null && waypoints.Length >= 2
                ? waypoints
                : new[] { pointA, pointB };

            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
            for (int i = 1; i < local.Length; i++)
                Gizmos.DrawLine(basePos + (Vector3)local[i - 1], basePos + (Vector3)local[i]);
            if (mode == PatrolMode.Loop && local.Length > 2)
                Gizmos.DrawLine(basePos + (Vector3)local[local.Length - 1], basePos + (Vector3)local[0]);   // the wrap-around leg
            foreach (Vector2 point in local)
                Gizmos.DrawWireSphere(basePos + (Vector3)point, 0.15f);
        }
    }
}
