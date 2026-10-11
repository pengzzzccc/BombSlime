using System;
using System.Collections.Generic;
using Inkform.Save;
using UnityEngine;

namespace Inkform.Item
{
    /// <summary>FIFO, data-backed inventory with a persistent, upgradeable capacity. A new run starts
    /// with no slots at all: every slot is a capacity crystal the player has found.</summary>
    public static class InventoryStore
    {
        public const int InitialCapacity = 0;

        public static event Action Changed;

        private static readonly List<InventoryItemDefinition> items = new List<InventoryItemDefinition>();
        private static readonly HashSet<string> collectedCapacityPickupIds =
            new HashSet<string>(StringComparer.Ordinal);
        private static Dictionary<string, InventoryItemDefinition> catalogue;
        private static int capacity = InitialCapacity;
        private static bool suppressPersistence;

        public static IReadOnlyList<InventoryItemDefinition> Items => items;
        public static int Count => items.Count;
        public static int Capacity => capacity;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Changed = null;
            items.Clear();
            collectedCapacityPickupIds.Clear();
            catalogue = null;
            capacity = InitialCapacity;
            suppressPersistence = false;
        }

        public static bool TryAdd(InventoryItemDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.Id) || items.Count >= capacity)
                return false;

            items.Add(definition);
            Notify();
            return true;
        }

        public static bool TryPeekFirst(out InventoryItemDefinition definition)
        {
            if (items.Count == 0)
            {
                definition = null;
                return false;
            }

            definition = items[0];
            return definition != null;
        }

        public static bool RemoveFirst() => RemoveAt(0);

        /// <summary>Backing-model removal used by PlayerInventory for consuming the first matching item.</summary>
        internal static bool RemoveAt(int index)
        {
            if (index < 0 || index >= items.Count) return false;

            items.RemoveAt(index);
            Notify();
            return true;
        }

        public static bool IsCapacityPickupCollected(string pickupId) =>
            !string.IsNullOrWhiteSpace(pickupId) && collectedCapacityPickupIds.Contains(pickupId);

        /// <summary>Atomically records a unique pickup and increases capacity with one notification/save.</summary>
        public static bool TryCollectCapacityPickup(string pickupId, int capacityIncrease)
        {
            if (string.IsNullOrWhiteSpace(pickupId) || capacityIncrease <= 0 ||
                collectedCapacityPickupIds.Contains(pickupId) || capacity > int.MaxValue - capacityIncrease)
                return false;

            collectedCapacityPickupIds.Add(pickupId);
            capacity += capacityIncrease;
            Notify();
            return true;
        }

#if UNITY_EDITOR
        /// <summary>Editor cheat: raw capacity bump, no pickup-id bookkeeping. Persists and
        /// notifies like a real capacity pickup, so the HUD and the active save slot follow.</summary>
        public static void AddCapacityForCheat(int increase)
        {
            if (increase <= 0 || capacity > int.MaxValue - increase) return;
            capacity += increase;
            Notify();
        }
#endif

        /// <summary>Empties the backpack but keeps what the crystals earned — the capacity and the
        /// collected pickup ids are permanent. Saved like any other change (the player's death).</summary>
        public static void ClearItems()
        {
            if (items.Count == 0) return;
            items.Clear();
            Notify();
        }

        public static void Clear() => ClearInternal(true);

        internal static void ClearWithoutSaving() => ClearInternal(false);

        private static void ClearInternal(bool persist)
        {
            bool changed = items.Count > 0 || capacity != InitialCapacity || collectedCapacityPickupIds.Count > 0;
            items.Clear();
            collectedCapacityPickupIds.Clear();
            capacity = InitialCapacity;
            if (changed) Notify(persist);
        }

        public static void Restore(string[] ids, int savedCapacity, string[] collectedPickupIds)
        {
            suppressPersistence = true;
            try
            {
                items.Clear();
                collectedCapacityPickupIds.Clear();
                EnsureCatalogue();

                if (ids != null)
                {
                    foreach (string id in ids)
                    {
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        if (catalogue.TryGetValue(id, out InventoryItemDefinition definition)) items.Add(definition);
                        else Debug.LogWarning($"InventoryStore: saved item id '{id}' has no definition; skipping it");
                    }
                }

                if (collectedPickupIds != null)
                {
                    foreach (string pickupId in collectedPickupIds)
                    {
                        if (!string.IsNullOrWhiteSpace(pickupId)) collectedCapacityPickupIds.Add(pickupId);
                    }
                }

                capacity = Mathf.Max(InitialCapacity, savedCapacity, items.Count);
            }
            finally
            {
                suppressPersistence = false;
            }

            Changed?.Invoke();
        }

        public static string[] SnapshotIds()
        {
            var result = new string[items.Count];
            for (int i = 0; i < items.Count; i++) result[i] = items[i] != null ? items[i].Id : string.Empty;
            return result;
        }

        public static string[] SnapshotCollectedCapacityPickupIds()
        {
            var result = new string[collectedCapacityPickupIds.Count];
            collectedCapacityPickupIds.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        private static void EnsureCatalogue()
        {
            if (catalogue != null) return;
            catalogue = new Dictionary<string, InventoryItemDefinition>(StringComparer.Ordinal);
            foreach (InventoryItemDefinition definition in Resources.LoadAll<InventoryItemDefinition>("Inventory"))
            {
                if (definition == null || string.IsNullOrWhiteSpace(definition.Id)) continue;
                if (!catalogue.TryAdd(definition.Id, definition))
                    Debug.LogWarning($"InventoryStore: duplicate inventory id '{definition.Id}'");
            }
        }

        private static void Notify(bool persist = true)
        {
            Changed?.Invoke();
            if (persist && !suppressPersistence)
            {
                SaveStore.RecordInventory(
                    SnapshotIds(),
                    Capacity,
                    SnapshotCollectedCapacityPickupIds());
            }
        }
    }
}
