using System;

namespace Inkform.Save
{
    /// <summary>
    /// One save slot as data: where the run is, and what it has cost so far. Pure fields with no logic,
    /// because JsonUtility only serializes public fields on a [Serializable] class — properties,
    /// Vector2 shorthand and DateTime would all be dropped silently.
    ///
    /// Deliberately *not* a snapshot of the level: the only thing needed to resume a run is which room
    /// to load and which coordinate to stand on. Broken walls and spent bombs are session state owned by
    /// LevelMemento (see its class docs) and come back whole on load, exactly as they do on respawn.
    ///
    /// Written and read only by SaveStore.
    /// </summary>
    [Serializable]
    public class SaveData
    {
        /// <summary>Bump when a field's meaning changes. SaveStore treats a file from a newer or
        /// unknown version as an empty slot rather than guessing at it — a save that loads wrong is
        /// worse than a save that is gone. v4 added unlockedAbilityIds and retired the time-card
        /// inventory item (v3 saves holding one get it migrated into the checkpoint ability).</summary>
        public const int CurrentVersion = 4;

        public int version = CurrentVersion;

        /// <summary>The room to load, matching a room registered in the WorldDefinition asset. Empty = this slot holds
        /// nothing; it is also the one field IsEmpty tests, so it must be written last-ish, never
        /// speculatively.</summary>
        public string sceneName;

        // The respawn point RespawnDirector had settled on when this was written — a checkpoint the
        // player touched, or the spawn it resolved on entering the room. Split into two floats because
        // JsonUtility serializes Vector2 as a nested object for no gain here.
        public float spawnX;
        public float spawnY;

        public float playSeconds;
        public int deaths;

        /// <summary>Stable InventoryItemDefinition ids, in FIFO order.</summary>
        public string[] inventoryItemIds = Array.Empty<string>();

        public int inventoryCapacity = 0;   // InventoryStore.InitialCapacity: a new run has no slots

        /// <summary>Stable scene pickup ids already consumed by this save slot.</summary>
        public string[] collectedInventoryCapacityPickupIds = Array.Empty<string>();

        /// <summary>Stable AbilityIds unlocked by this save slot. Unlike inventory these never leave:
        /// once earned, an ability holds for the whole life of the slot.</summary>
        public string[] unlockedAbilityIds = Array.Empty<string>();

        /// <summary>DateTime.UtcNow.ToString("o"). A string rather than a DateTime because JsonUtility
        /// cannot serialize DateTime at all — it writes an empty object and loses the value.</summary>
        public string savedAtUtc;

        /// <summary>Nothing has been recorded into this slot. The save menu shows these as "New Game",
        /// and SaveStore.SaveNow refuses to write one — a file with no sceneName could never load.</summary>
        public bool IsEmpty => string.IsNullOrEmpty(sceneName);
    }
}
