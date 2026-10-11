using System.Collections.Generic;
using Inkform.Bus;
using Inkform.Fx;
using Inkform.Item;
using Inkform.Player;
using Inkform.Tool;
using UnityEngine;

namespace Inkform.Interactable.Parts
{
    /// <summary>
    /// Permanent inventory-capacity pickup. The stable id is saved per slot, so an already collected
    /// instance removes itself when its scene is loaded again and can never grant capacity twice.
    /// In play, the collected crystal is not removed on the spot: it performs its flight into the
    /// HUD backpack (CapacityUpgradeFlight — grow to the screen centre, glow, dash to the HUD) and
    /// that removes it on arrival.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryCapacityPart : MonoBehaviour, IInteractablePart
    {
        [Header("Inventory Capacity Pickup")]
        [Tooltip("Stable, unique id for this scene instance. Blank ids are rejected.")]
        [SerializeField] private string pickupId;
        [Tooltip("Backpack slots granted by this pickup.")]
        [SerializeField, Min(1)] private int capacityIncrease = 1;

        [Header("Pickup Flight")]
        [Tooltip("Material with the Inkform/SpriteOutline shader (CrystalGlow): the white edge glow while the crystal hangs at the screen centre. Empty = no glow, the flight still plays.")]
        [SerializeField] private Material glowMaterial;
        [SerializeField] private CapacityFlightSettings flight = new CapacityFlightSettings();

        private Interactable root;
        private readonly List<Collider2D> colliders = new List<Collider2D>();
        private readonly List<Renderer> renderers = new List<Renderer>();
        private bool consumed;

        public void Attach(Interactable interactable)
        {
            root = interactable;
            colliders.Clear();
            colliders.AddRange(root.GetComponentsInChildren<Collider2D>(true));
            renderers.Clear();
            renderers.AddRange(root.GetComponentsInChildren<Renderer>(true));

            Collider2D rootCollider = root.GetComponent<Collider2D>();
            if (rootCollider != null && !rootCollider.isTrigger)
                Debug.LogWarning($"{root.name} inventory capacity pickup collider should be a Trigger", root);
            if (string.IsNullOrWhiteSpace(pickupId))
                Debug.LogWarning($"{root.name} inventory capacity pickup has no stable pickup id", this);

            if (InventoryStore.IsCapacityPickupCollected(pickupId)) ConsumeWorldObject();
        }

        public bool HandleContact(ContactPhase phase, Collider2D other)
        {
            if (consumed || phase == ContactPhase.Exit || other == null || !other.CompareTag(Tags.Player))
                return false;

            if (InventoryStore.IsCapacityPickupCollected(pickupId))
            {
                ConsumeWorldObject();
                return true;
            }

            PlayerInventory inventory = other.GetComponentInParent<PlayerInventory>();
            if (inventory == null || !inventory.TryCollectCapacityUpgrade(pickupId, capacityIncrease))
                return false;

            ItemBus.RaiseInventoryCapacityUpgraded(root.transform.position, capacityIncrease);
            if (!TryLaunchFlight()) ConsumeWorldObject();
            return true;
        }

        // The crystal's sprite flies itself into the HUD and the flight destroys it on arrival.
        // Edit-mode callers (tests) and a crystal without art keep the immediate removal.
        private bool TryLaunchFlight()
        {
            if (!Application.isPlaying || root == null) return false;
            SpriteRenderer body = root.GetComponentInChildren<SpriteRenderer>();
            if (body == null || body.sprite == null) return false;

            consumed = true;
            foreach (Collider2D itemCollider in colliders)
            {
                if (itemCollider != null) itemCollider.enabled = false;
            }
            foreach (Renderer itemRenderer in renderers)
            {
                if (itemRenderer != null && itemRenderer != body) itemRenderer.enabled = false;
            }

            root.gameObject.AddComponent<CapacityUpgradeFlight>()
                .Launch(body, glowMaterial, capacityIncrease, flight);
            return true;
        }

        private void ConsumeWorldObject()
        {
            if (consumed) return;
            consumed = true;
            foreach (Collider2D itemCollider in colliders)
            {
                if (itemCollider != null) itemCollider.enabled = false;
            }
            foreach (Renderer itemRenderer in renderers)
            {
                if (itemRenderer != null) itemRenderer.enabled = false;
            }

            if (root == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(root.gameObject);
            else
#endif
                Destroy(root.gameObject);
        }
    }
}
