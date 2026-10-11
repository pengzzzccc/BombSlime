using System.Collections.Generic;
using Inkform.Bus;
using Inkform.Tool;
using UnityEngine;

namespace Inkform.Interactable.Parts
{
    /// <summary>
    /// Tutorial zone: while the player stands in this node's trigger, its control hint fades in at
    /// the bottom of the screen; leaving fades it out. The zone only says "my hint is wanted" on
    /// UiBus — the HUD (TutorialHintView) owns the copy, the device-aware icons and the fade.
    ///
    /// Purely presentational like InteractionPromptPart: it never claims contacts, so it can sit on
    /// any trigger. Drop Prefabs/Item/TutorialZone into a room, size its box, pick the hint.
    /// </summary>
    public class TutorialHintPart : MonoBehaviour, IInteractablePart
    {
        [Tooltip("Which control hint this zone shows")]
        [SerializeField] private TutorialHint hint;
        [Tooltip("Seconds the hint stays up while the player lingers in the zone. 0 = until the player leaves")]
        [SerializeField, Min(0f)] private float maxShowSeconds;

        private readonly HashSet<Collider2D> players = new HashSet<Collider2D>();
        private bool showing;

        public TutorialHint Hint => hint;

        public void Attach(Interactable root) { }

        public bool HandleContact(ContactPhase phase, Collider2D other)
        {
            if (other == null || !other.CompareTag(Tags.Player)) return false;

            if (phase == ContactPhase.Enter) players.Add(other);
            else if (phase == ContactPhase.Exit) players.Remove(other);
            Refresh();
            return false;   // presentation only — never claims the contact
        }

        void Update()
        {
            // A player collider torn down mid-contact sends no Exit: drop it so the hint cannot stick
            if (players.Count > 0 && players.RemoveWhere(collider => collider == null) > 0) Refresh();
        }

        // Disable covers destroy and scene unload too: the HUD outlives every room
        void OnDisable()
        {
            players.Clear();
            Refresh();
        }

        private void Refresh()
        {
            bool want = players.Count > 0;
            if (want == showing) return;
            showing = want;
            if (showing) UiBus.RaiseTutorialShown(this, hint, maxShowSeconds);
            else UiBus.RaiseTutorialHidden(this);
        }

#if UNITY_EDITOR
        // The zone has no sprite: draw its box and hint name so designers can find it in the room
        private void OnDrawGizmos()
        {
            if (!TryGetComponent(out Collider2D zone)) return;
            Gizmos.color = new Color(0.3f, 0.85f, 1f, 0.6f);
            Gizmos.DrawWireCube(zone.bounds.center, zone.bounds.size);
            UnityEditor.Handles.Label(zone.bounds.center, $"Tutorial: {hint}");
        }
#endif
    }
}
