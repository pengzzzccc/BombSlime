using System;
using Inkform.Item;
using UnityEngine;

namespace Inkform.Bus
{
    /// <summary>The beats of a capacity crystal's flight into the HUD (CapacityUpgradeFlight):
    /// Rise toward the screen centre, Hold there glowing, Fly to the backpack, Land on it.
    /// Cancel replaces Land when the flight is torn down before it arrives (a scene change).</summary>
    public enum CapacityFlightPhase { Rise, Hold, Fly, Land, Cancel }

    /// <summary>Announcements for inventory storage and world release.</summary>
    public static class ItemBus
    {
        public static event Action<InventoryItemDefinition> ItemStored;
        public static event Action<InventoryItemDefinition, Vector2, Vector2> ItemReleased;
        public static event Action<Vector2, int> InventoryCapacityUpgraded;
        public static event Action<CapacityFlightPhase, int> CapacityFlight;

        public static void RaiseItemStored(InventoryItemDefinition item) => ItemStored?.Invoke(item);

        public static void RaiseItemReleased(InventoryItemDefinition item, Vector2 pos, Vector2 velocity) =>
            ItemReleased?.Invoke(item, pos, velocity);

        public static void RaiseInventoryCapacityUpgraded(Vector2 pos, int capacityIncrease) =>
            InventoryCapacityUpgraded?.Invoke(pos, capacityIncrease);

        public static void RaiseCapacityFlight(CapacityFlightPhase phase, int capacityIncrease) =>
            CapacityFlight?.Invoke(phase, capacityIncrease);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ItemStored = null;
            ItemReleased = null;
            InventoryCapacityUpgraded = null;
            CapacityFlight = null;
        }
    }
}
