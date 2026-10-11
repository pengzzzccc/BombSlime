using Inkform.Bus;
using Inkform.Interactable;
using Inkform.Item;
using Inkform.Life;
using Inkform.Tool;
using UnityEngine;

namespace Inkform.Player
{
    /// <summary>Player-side inventory gameplay facade. World parts store through this component;
    /// player actions consume or release through it. InventoryStore remains the data/save backing.
    /// Dying empties the backpack (the bombs, not the capacity the crystals earned).</summary>
    public class PlayerInventory : MonoBehaviour
    {
        [SerializeField] private float spitOffset = 0.9f;
        [SerializeField] private float spitSpeed = 24f;

        public int Count => InventoryStore.Count;
        public bool IsEmpty => InventoryStore.Count == 0;

        void OnEnable() => LifeBus.Died += OnDied;

        void OnDisable() => LifeBus.Died -= OnDied;

        private void OnDied(DeathContext ctx)
        {
            if (ctx.Victim != gameObject) return;

            InventoryStore.ClearItems();
        }

        public bool TryStore(InventoryItemDefinition definition) => InventoryStore.TryAdd(definition);

        public bool TryCollectCapacityUpgrade(string pickupId, int capacityIncrease = 1) =>
            InventoryStore.TryCollectCapacityPickup(pickupId, capacityIncrease);

        public bool TryConsumeDashFuel()
        {
            for (int i = 0; i < InventoryStore.Items.Count; i++)
            {
                InventoryItemDefinition item = InventoryStore.Items[i];
                if (item != null && item.IsDashFuel)
                    return InventoryStore.RemoveAt(i);
            }

            return false;
        }

        public bool TryReleaseFirst(Vector2 dir)
        {
            bool fromBackpack = InventoryStore.TryPeekFirst(out InventoryItemDefinition definition);
#if UNITY_EDITOR
            // F1 infinite bombs: an empty backpack spits the one bomb item in the project
            // (Resources/Inventory/AllinoneBomb) without storing it — a new run has no slots to
            // hold it in
            if (!fromBackpack && DebugCheats.InfiniteBombs)
                definition = Resources.Load<InventoryItemDefinition>("Inventory/AllinoneBomb");
#endif
            if (definition == null) return false;
            if (definition.WorldPrefab == null)
            {
                Debug.LogWarning($"Inventory item '{definition.Id}' has no world prefab; retaining it in the backpack", definition);
                return false;
            }

            Vector2 mouth = (Vector2)transform.position + dir * spitOffset;
            GameObject instance = Instantiate(definition.WorldPrefab, mouth, Quaternion.identity);
            Inkform.Interactable.Interactable node =
                instance.GetComponentInChildren<Inkform.Interactable.Interactable>(true);
            if (node == null || !node.TryGetPart(out ICarriable carriable))
            {
                Debug.LogWarning($"Inventory prefab '{definition.WorldPrefab.name}' has no Interactable ICarriable part; retaining the item", instance);
                Destroy(instance);
                return false;
            }

            Vector2 velocity = dir * spitSpeed;
            carriable.Release(mouth, velocity);
#if UNITY_EDITOR
            // F1 infinite bombs: the item stays in the backpack — one bomb, endless spits
            if (fromBackpack && !DebugCheats.InfiniteBombs)
#else
            if (fromBackpack)
#endif
                InventoryStore.RemoveFirst();
            ItemBus.RaiseItemReleased(definition, mouth, velocity);
            return true;
        }
    }
}
