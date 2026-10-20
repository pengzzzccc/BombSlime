using Inkform.Bus;
using Inkform.Tool;
using UnityEngine;

namespace Inkform.Level
{
    /// <summary>
    /// Checkpoint: a timecard machine. Touching it stamps the card and records the respawn position
    /// here (the checkpoint ability is a built-in default, so every player can stamp). The one with
    /// isStartPoint checked also serves as the level spawn point — at startup RespawnDirector
    /// teleports the player there, so "spawn point" and "checkpoint" remain the same kind of object.
    ///
    /// Machines are mutually exclusive: the respawn point is one place, so when a machine is stamped
    /// it announces itself on LifeBus.CheckpointSet and every other machine resets to its idle,
    /// un-stamped look — walking back into an older machine simply stamps it anew and steals the
    /// respawn point back.
    ///
    /// Presentation is a hand-driven sprite sequence rather than an Animator: frame 0 stands resident
    /// while idle, activation steps through every frame once and freezes on the last one — which falls
    /// out of simply never writing another sprite afterwards (same manual sprite-swapping pattern as
    /// ExplodePart's trigger frames). Needs an Is-Trigger collider.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class Checkpoint : MonoBehaviour
    {

        [Header("Checkpoint setting")]
        [SerializeField] private bool isStartPoint = false;                     // checked = also the level spawn point; only one per level
        [SerializeField] private Vector2 spawnOffset = new Vector2(0f, 0.5f);   // raised a bit so respawn feet do not sink into the ground and get pushed out

        [Header("Arrival spawn")]
        [Tooltip("Door-arrival id: the LevelExit leading into this scene names this checkpoint in its targetSpawnId. Replaces the old Spawn_<scene> object-name convention; plain mid-level checkpoints leave it empty")]
        [SerializeField] private string spawnId = "";

        public enum WalkDirection { Right, Left }

        [Header("Arrival walk")]
        [Tooltip("Arriving here through a door, the player walks this way on their own before getting control back (left / right: the slime walks, it does not climb)")]
        [SerializeField] private WalkDirection arrivalWalkDirection = WalkDirection.Right;
        [Tooltip("How far the arrival walk goes, in world units. 0 = no walk, control comes back at once. Stops early against a wall")]
        [SerializeField, Min(0f)] private float arrivalWalkDistance = 0f;
        [Tooltip("Walking pace as a share of the full run speed")]
        [SerializeField, Range(0.1f, 1f)] private float arrivalWalkSpeed = 0.6f;

        [Header("Timecard machine look")]
        [Tooltip("Activation sequence: first frame = idle resident, last frame = stamped freeze-frame")]
        [SerializeField] private Sprite[] frames;
        [SerializeField] private float framesPerSecond = 12f;

        private SpriteRenderer spriteRenderer;
        private Coroutine playingAnimation;
        private bool active;    // stamped: passing through again must not re-raise feedback

        public bool IsStartPoint => isStartPoint;
        public Vector2 SpawnPos => (Vector2)transform.position + spawnOffset;
        public string SpawnId => spawnId;

        /// <summary>+1 right, -1 left.</summary>
        public float ArrivalWalkSign => arrivalWalkDirection == WalkDirection.Left ? -1f : 1f;
        public float ArrivalWalkDistance => arrivalWalkDistance;
        public float ArrivalWalkSpeed => arrivalWalkSpeed;

        void Awake()
        {
            // The idle look IS the first animation frame, so editor authoring and runtime can never drift apart
            spriteRenderer = GetComponent<SpriteRenderer>();
            ShowFrame(0);
        }

        // Mutually exclusive machines: each stamp goes out on the bus, and every machine that did
        // not just stamp resets. OnEnable/OnDisable pairing keeps a destroyed machine from receiving
        // the event after its scene unloads (LifeBus outlives scene objects)
        void OnEnable() => LifeBus.CheckpointSet += OnCheckpointSetElsewhere;
        void OnDisable() => LifeBus.CheckpointSet -= OnCheckpointSetElsewhere;

        private void OnCheckpointSetElsewhere(Vector2 pos)
        {
            if (pos != SpawnPos) Deactivate();
        }

        /// <summary>Back to the un-stamped state: idle frame, animation stopped, and the trigger gate
        /// open again so this machine can be stamped anew (which steals the respawn point back).</summary>
        private void Deactivate()
        {
            if (!active) return;
            active = false;
            if (playingAnimation != null)
            {
                StopCoroutine(playingAnimation);
                playingAnimation = null;
            }
            ShowFrame(0);
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (active) return;
            if (!other.CompareTag(Tags.Player)) return;

            active = true;
            LifeBus.RaiseCheckpointSet(SpawnPos);
            PlayActivationSequence();
        }

        private void PlayActivationSequence()
        {
            if (playingAnimation != null) StopCoroutine(playingAnimation);
            // Coroutines need the player loop (EditMode tests invoke the trigger directly)
            if (!Application.isPlaying) return;
            playingAnimation = StartCoroutine(PlayFrames());
        }

        private System.Collections.IEnumerator PlayFrames()
        {
            if (frames != null && frames.Length > 0)
            {
                float step = 1f / Mathf.Max(framesPerSecond, 0.01f);
                for (int i = 0; i < frames.Length; i++)
                {
                    ShowFrame(i);
                    yield return new WaitForSeconds(step);
                }
                // Ending mid-sequence leaves the final frame written — the freeze-frame comes for free
            }
            playingAnimation = null;
        }

        private void ShowFrame(int index)
        {
            if (spriteRenderer == null) return;
            if (frames == null || frames.Length == 0) return;
            spriteRenderer.sprite = frames[Mathf.Clamp(index, 0, frames.Length - 1)];
        }

        // OnDrawGizmos rather than ...Selected: while placing levels you must be able to scan all
        // respawn points at a glance and spot a missing spawn point — clicking each one is too slow
        void OnDrawGizmos()
        {
            bool touched = Application.isPlaying && active;
            Color c = isStartPoint ? new Color(0.35f, 1f, 0.45f) : new Color(0.35f, 0.85f, 1f);

            // Trigger range: the player must enter this box to count as touching. Read the collider
            // directly, so the gizmo and the actual check never diverge — tuning one and forgetting the other
            Collider2D box = GetComponent<Collider2D>();
            if (box != null && box.bounds.size.sqrMagnitude > 0f)
            {
                Gizmos.color = touched ? c : c * 0.55f;
                Gizmos.DrawWireCube(box.bounds.center, box.bounds.size);
            }

            // The true respawn coordinate. A line to it shows at a glance how high spawnOffset is —
            // if this point is buried in the ground, the player gets physically pushed out at respawn
            Gizmos.color = c;
            Gizmos.DrawLine(transform.position, SpawnPos);

            if (touched) Gizmos.DrawSphere(SpawnPos, 0.14f);        // solid = stepped on this run
            else Gizmos.DrawWireSphere(SpawnPos, 0.14f);

#if UNITY_EDITOR
            // The arrival id at a glance: it is the exact string a door's targetSpawnId must match
            if (!string.IsNullOrEmpty(spawnId))
            {
                Gizmos.color = c;
                UnityEditor.Handles.Label(SpawnPos + Vector2.up * 0.4f, spawnId);
            }
#endif

            DrawArrivalWalk();

            // The spawn point gets an extra cross to distinguish it from plain checkpoints (only one per level)
            if (!isStartPoint) return;
            Gizmos.DrawLine(SpawnPos + Vector2.left * 0.3f, SpawnPos + Vector2.right * 0.3f);
            Gizmos.DrawLine(SpawnPos + Vector2.down * 0.3f, SpawnPos + Vector2.up * 0.3f);
        }

        // The arrival walk: an arrow from the spawn point to where the walk ends, tick marks every
        // unit, and its length as a label — direction and reach read at a glance while placing doors
        private void DrawArrivalWalk()
        {
            if (arrivalWalkDistance <= 0f) return;

            Vector2 from = SpawnPos;
            Vector2 dir = Vector2.right * ArrivalWalkSign;
            Vector2 to = from + dir * arrivalWalkDistance;
            Gizmos.color = new Color(1f, 0.85f, 0.25f, 0.95f);
            Gizmos.DrawLine(from, to);
            Gizmos.DrawLine(to, to - dir * 0.35f + Vector2.up * 0.22f);
            Gizmos.DrawLine(to, to - dir * 0.35f + Vector2.down * 0.22f);
            for (int i = 1; i < arrivalWalkDistance; i++)
            {
                Vector2 tick = from + dir * i;
                Gizmos.DrawLine(tick + Vector2.down * 0.08f, tick + Vector2.up * 0.08f);
            }
#if UNITY_EDITOR
            UnityEditor.Handles.Label(to + Vector2.up * 0.35f, $"walk {arrivalWalkDistance:0.#}");
#endif
        }
    }
}
