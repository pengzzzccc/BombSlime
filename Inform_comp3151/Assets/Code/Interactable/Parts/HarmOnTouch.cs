using Inkform.Bus;
using Inkform.Life;
using Inkform.Tool;
using UnityEngine;

namespace Inkform.Interactable.Parts
{
    /// <summary>
    /// Hazard behavior: kills the player on contact (any direction). All "touch = death" level objects
    /// share this component — spikes attach it directly; a timed laser pillar / spinning gear / timed
    /// spike only needs visibility/motion parts stacked on top.
    /// Only requires an Is-Trigger collider on the node (Interactable), so a hand-placed spike and a
    /// full Tilemap of spikes (TilemapCollider2D) use the same code.
    /// This class only publishes the fact "someone got spiked"; shards/shake/sound are performed
    /// uniformly by the DeathStrategy selected by DeathDirector.
    /// </summary>
    public class HarmOnTouch : MonoBehaviour, IInteractablePart
    {
        [Header("Harm")]
        [Tooltip("Death cause: DeathDirector picks the presentation strategy by it (spikes default to Spike)")]
        [SerializeField] private DeathCause cause = DeathCause.Spike;
        [Tooltip("Who it harms: CompareTag never errors on a wrong string, it just never matches — use the Tags constants")]
        [SerializeField] private string targetTag = Tags.Player;

        private Collider2D hitBox;      // collider on the node: used for the kill point
        private GameObject source;      // the hazard as a whole, named in the death context for analytics

        public void Attach(Interactable root)
        {
            source = root.gameObject;
            // RequireComponent guarantees a collider on the node; not getting one means misconfiguration — complain
            hitBox = root.GetComponent<Collider2D>();
            if (hitBox == null)
                Debug.LogWarning($"{root.name}'s Interactable has no Collider2D; the hazard will not work", root);
        }

        public bool HandleContact(ContactPhase phase, Collider2D other)
        {
            // Enter and Stay both matter: Enter is not re-raised while the player sits still inside the
            // spikes, and a respawn point sitting on a spike works the same
            if (phase == ContactPhase.Exit) return false;
            if (hitBox == null) return false;
            if (!other.CompareTag(targetTag)) return false;

            // Kill point = the closest point on the collider to the player, not transform.position —
            // on one Tilemap with a whole row of spikes that would be the grid origin, and shards
            // would fly tens of cells away
            LifeBus.RaiseDied(new DeathContext(
                other.gameObject,
                hitBox.ClosestPoint(other.bounds.center),
                cause,
                source));

            return true;    // handled: short-circuit later parts (only the player can die)
        }
    }
}
