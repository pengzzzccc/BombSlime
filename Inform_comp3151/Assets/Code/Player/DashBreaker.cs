using Inkform.Bus;
using Inkform.Interactable.Parts;
using Inkform.Life;
using UnityEngine;

namespace Inkform.Player
{
    /// <summary>
    /// Dash smash: while the motor's attack-dash runs, an overlap probe ahead of the player
    /// covers everything breakable in the dash's path — near enough that the break lands BEFORE
    /// physical contact, so the dash never stalls on the crate. Victims claim themselves through
    /// the same HazardBus.Exploded path a bomb uses (fragment burst, break sound, the lot), and
    /// the spread wave publishes once per dash via HazardBus.Blast, which FxDirector /
    /// AudioDirector / HapticsDirector render exactly like a bomb's.
    ///
    /// Deliberately an INDEPENDENT loop (own Update, the RopeGun pattern) rather than a step
    /// ticked by PlayerHandler: PlayerHandler.Update is a sense → move → animate chain where any
    /// earlier throw aborts the rest of the frame, and this feature must not depend on that
    /// chain's health. All it needs from the rig is PlayerMotor.IsDashing / DashDirection.
    ///
    /// An overlap, deliberately not a cast: the player often starts the dash already pressed
    /// against the wall (initial-overlap casts are unreliable), and during a pinned dash the
    /// per-frame movement is ~zero, so segment-based probing skips the very frames that matter.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public class DashBreaker : MonoBehaviour
    {
        [Tooltip("Layers a running dash smashes through. Standable breakable walls may sit on Terrain(6) as well as Breakable(11) (BreakablePart's own doc suggests either) - keep both bits; the TryGetPart filter ignores plain solids, so the extra bit is harmless.")]
        [SerializeField] private LayerMask dashBreakMask = (1 << 6) | (1 << 11);
        [Tooltip("The blast published when a dash smash breaks something. Radius and force match a bomb's blastRadius/blastForce, so the spread wave looks identical.")]
        [SerializeField] private float dashBlastRadius = 2f;
        [SerializeField] private float dashBlastForce = 18f;

        private const float Reach = 0.45f;    // probe-centre offset along the dash direction
        private const float Radius = 0.5f;    // anything breakable within reach+radius ahead of the player smashes

        private PlayerMotor motor;
        private readonly Collider2D[] hits = new Collider2D[8];   // fixed buffer, RopeGun's probe convention
        private bool blastRaised;      // one Blast wave per dash, however many crates it grinds through

        void Awake()
        {
            motor = GetComponent<PlayerMotor>();

            // Self-heal a zero mask (e.g. stale serialized data): with no layer bits the smash would
            // silently never fire. Terrain | Breakable — standable breakable walls may sit on either
            // layer (the TryGetPart filter sorts solids out anyway).
            if (dashBreakMask == 0) dashBreakMask = (1 << 6) | (1 << 11);
        }

        void Update()
        {
            if (LifeBus.IsDead) return;   // same stance as PlayerHandler.Update: nothing runs while dead

            if (!motor.IsDashing)
            {
                blastRaised = false;   // re-arm: one wave per dash
                return;
            }

            Vector2 probeCentre = (Vector2)transform.position + motor.DashDirection * Reach;
            int count = Physics2D.OverlapCircleNonAlloc(probeCentre, Radius, hits, dashBreakMask);

            for (int i = 0; i < count; i++)
            {
                // Only nodes that actually carry a BreakablePart are claimed — a plain solid that
                // happens to sit on the layer must not raise Exploded at other subscribers.
                // Fully qualified: from inside Inkform.Player the namespace Inkform.Interactable
                // shadows the class of the same name (same reason RopeGun qualifies it)
                Collider2D collider = hits[i];
                Inkform.Interactable.Interactable node =
                    collider != null ? collider.GetComponentInParent<Inkform.Interactable.Interactable>() : null;
                if (node == null || !node.TryGetPart(out BreakablePart _)) continue;

                // Blast centre on the victim itself: the fragments fly radially away from the smash
                HazardBus.RaiseExploded(node.gameObject, collider.bounds.center, dashBlastForce);
                if (!blastRaised)
                {
                    blastRaised = true;
                    HazardBus.RaiseBlast(collider.bounds.center, dashBlastRadius, dashBlastForce);
                }
            }
        }
    }
}
