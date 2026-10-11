using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Inkform.Ability;
using Inkform.Bus;
using Inkform.Item;
using UnityEngine;

namespace Inkform.Save
{
    /// <summary>Three transactional, atomically-written save slots, stored in a Saves folder beside
    /// the game itself (the executable's directory in a build, the project root in the editor).</summary>
    public static class SaveStore
    {
        public const int SlotCount = 3;
        private const string SavesFolder = "Saves";

        public static int ActiveSlot { get; private set; } = -1;
        public static event Action Changed;

        /// <summary>Raised only after a slot file has actually been written to disk — the
        /// "your progress is safe" signal for the HUD's "Saved" toast (Hud.ShowToast). Changed also
        /// fires for in-memory updates (BeginNewRun, restore paths) that write nothing.</summary>
        public static event Action Saved;

        private static SaveData[] slots;
        private static float runPlayBase;
        private static float runActiveSeconds;   // played since the run (re)started: see AdvancePlayTime
        private static int runDeathBase;
        private static int runDeathSessionBase;

        // An overwrite remains reversible until the entry scene records its first valid position.
        private static int pendingNewSlot = -1;
        private static SaveData pendingPrevious;

        // Guards the one-time copy from the old per-user AppData folder (see MigrateFromAppDataOnce)
        private static bool migratedFromAppData;
#if UNITY_INCLUDE_TESTS
        private static string testSavesDirectory;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Changed = null;
            Saved = null;
            ActiveSlot = -1;
            slots = null;
            runPlayBase = 0f;
            runActiveSeconds = 0f;
            runDeathBase = 0;
            runDeathSessionBase = 0;
            pendingNewSlot = -1;
            pendingPrevious = null;
            migratedFromAppData = false;
#if UNITY_INCLUDE_TESTS
            testSavesDirectory = null;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LoadAll()
        {
            MigrateFromAppDataOnce();
            slots = new SaveData[SlotCount];
            for (int i = 0; i < SlotCount; i++) slots[i] = ReadSlot(i);
        }

        private static void EnsureLoaded()
        {
            if (slots == null) LoadAll();
        }

        // One-time move from when the save folder lived in per-user AppData (persistentDataPath):
        // copy whatever the old install left there so existing runs do not vanish from the slot
        // menu. Only runs while the new folder does not exist — the copy itself creates it, so this
        // never fires twice — and never in edit mode (tests) or under a redirected test directory.
        private static void MigrateFromAppDataOnce()
        {
#if UNITY_INCLUDE_TESTS
            if (!string.IsNullOrEmpty(testSavesDirectory)) return;
#endif
            if (!Application.isPlaying || migratedFromAppData) return;
            migratedFromAppData = true;

            try
            {
                string legacy = Path.Combine(Application.persistentDataPath, SavesFolder);
                if (Directory.Exists(SavesDir) || !Directory.Exists(legacy)) return;

                Directory.CreateDirectory(SavesDir);
                foreach (string file in Directory.GetFiles(legacy))
                    File.Copy(file, Path.Combine(SavesDir, Path.GetFileName(file)), false);
                Debug.Log($"SaveStore: moved legacy saves from '{legacy}' to '{SavesDir}'");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SaveStore: could not move legacy saves ({e.Message}) — starting fresh in '{SavesDir}'");
            }
        }

        public static SaveData Get(int slot)
        {
            EnsureLoaded();
            return IsValidSlot(slot) ? slots[slot] : new SaveData();
        }

        public static bool HasSave(int slot) => !Get(slot).IsEmpty;

        // ---- The run's clock and death count: one number everywhere ----

        /// <summary>The run's play time — what the HUD timer shows, the slot stores and the end
        /// sheet reports. It only advances while the player is actually playing (AdvancePlayTime);
        /// a continued run picks up from the slot's stored time.</summary>
        public static float PlaySeconds => runPlayBase + runActiveSeconds;

        /// <summary>The run's deaths: the slot's stored count plus this session's.</summary>
        public static int RunDeaths => runDeathBase + Mathf.Max(0, LifeBus.DeathCount - runDeathSessionBase);

        /// <summary>Adds played time. Called by the HUD every frame the game is in play (in a level,
        /// state Playing, no scene transition) — pause, menus and transitions do not count. Runs
        /// with no active slot too (a session started straight in a level), so the timer moves.</summary>
        public static void AdvancePlayTime(float deltaTime)
        {
            if (deltaTime > 0f) runActiveSeconds += deltaTime;
        }

        public static void BeginNewRun(int slot)
        {
            EnsureLoaded();
            if (!IsValidSlot(slot)) return;
            if (pendingNewSlot >= 0) AbortNewRun();

            pendingNewSlot = slot;
            pendingPrevious = Clone(slots[slot]);
            slots[slot] = new SaveData();
            InventoryStore.ClearWithoutSaving();
            AbilityStore.ClearWithoutSaving();
            StartRun(slot, 0f, 0);
            Changed?.Invoke();
        }

        public static bool AbortNewRun()
        {
            EnsureLoaded();
            if (pendingNewSlot < 0) return false;

            int slot = pendingNewSlot;
            slots[slot] = pendingPrevious ?? new SaveData();
            ActiveSlot = -1;
            ClearPending();
            RestoreInventory(slots[slot]);
            Changed?.Invoke();
            return true;
        }

        public static void ContinueRun(int slot)
        {
            EnsureLoaded();
            if (!IsValidSlot(slot)) return;

            SaveData data = slots[slot];
            if (data.IsEmpty) { BeginNewRun(slot); return; }

            ClearPending();
            StartRun(slot, data.playSeconds, data.deaths);
            RestoreInventory(data);
        }

        private static void StartRun(int slot, float playBase, int deathBase)
        {
            ActiveSlot = slot;
            runPlayBase = playBase;
            runActiveSeconds = 0f;
            runDeathBase = deathBase;
            runDeathSessionBase = LifeBus.DeathCount;
        }

        public static void RecordProgress(string sceneName, Vector2 spawn)
        {
            if (ActiveSlot < 0 || string.IsNullOrEmpty(sceneName)) return;

            SaveData data = slots[ActiveSlot];
            data.sceneName = sceneName;
            data.spawnX = spawn.x;
            data.spawnY = spawn.y;
            CaptureInventory(data);
            Stamp(data);

            if (WriteSlot(ActiveSlot, data)) { CommitPendingIfNeeded(ActiveSlot); Saved?.Invoke(); }
            Changed?.Invoke();
        }

        public static void RecordInventory(string[] ids, int capacity, string[] collectedCapacityPickupIds)
        {
            if (ActiveSlot < 0) return;
            SaveData data = slots[ActiveSlot];
            data.inventoryItemIds = ids ?? Array.Empty<string>();
            data.inventoryCapacity = Mathf.Max(InventoryStore.InitialCapacity, capacity, data.inventoryItemIds.Length);
            data.collectedInventoryCapacityPickupIds = collectedCapacityPickupIds ?? Array.Empty<string>();

            // A brand-new run is not committed until RespawnDirector supplies a loadable scene/position.
            if (data.IsEmpty) return;

            Stamp(data);
            if (WriteSlot(ActiveSlot, data)) { CommitPendingIfNeeded(ActiveSlot); Saved?.Invoke(); }
            Changed?.Invoke();
        }

        /// <summary>Mirror of RecordInventory for AbilityStore: an unlock lands in the slot file the
        /// moment it happens, so a crash right after pickup cannot eat an ability.</summary>
        public static void RecordAbilities(string[] abilityIds)
        {
            if (ActiveSlot < 0) return;
            SaveData data = slots[ActiveSlot];
            data.unlockedAbilityIds = abilityIds ?? Array.Empty<string>();

            if (data.IsEmpty) return;

            Stamp(data);
            if (WriteSlot(ActiveSlot, data)) { CommitPendingIfNeeded(ActiveSlot); Saved?.Invoke(); }
            Changed?.Invoke();
        }

        public static void SaveNow()
        {
            if (ActiveSlot < 0) return;
            SaveData data = slots[ActiveSlot];
            if (data.IsEmpty) return;

            CaptureInventory(data);
            Stamp(data);
            if (WriteSlot(ActiveSlot, data)) { CommitPendingIfNeeded(ActiveSlot); Saved?.Invoke(); }
            Changed?.Invoke();
        }

        public static void EndRun()
        {
            if (ActiveSlot < 0) return;
            int slot = ActiveSlot;

            if (slot == pendingNewSlot && slots[slot].IsEmpty)
            {
                slots[slot] = pendingPrevious ?? new SaveData();
                ClearPending();
                Changed?.Invoke();
            }
            else
            {
                SaveNow();
            }
            ActiveSlot = -1;
        }

        private static void CaptureInventory(SaveData data)
        {
            data.inventoryItemIds = InventoryStore.SnapshotIds();
            data.inventoryCapacity = InventoryStore.Capacity;
            data.collectedInventoryCapacityPickupIds = InventoryStore.SnapshotCollectedCapacityPickupIds();
            data.unlockedAbilityIds = AbilityStore.SnapshotIds();
        }

        private static void RestoreInventory(SaveData data)
        {
            InventoryStore.Restore(
                data.inventoryItemIds,
                data.inventoryCapacity,
                data.collectedInventoryCapacityPickupIds);
            AbilityStore.Restore(data.unlockedAbilityIds);
        }

        private static void Stamp(SaveData data)
        {
            data.version = SaveData.CurrentVersion;
            data.playSeconds = PlaySeconds;
            data.deaths = RunDeaths;
            data.savedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        private static void CommitPendingIfNeeded(int slot)
        {
            if (pendingNewSlot == slot) ClearPending();
        }

        private static void ClearPending()
        {
            pendingNewSlot = -1;
            pendingPrevious = null;
        }

        private static bool IsValidSlot(int slot) => slot >= 0 && slot < SlotCount;

        // The game's own folder: the executable's directory in a build (the parent of <product>_Data),
        // the project root in the editor (the parent of Assets) — a portable save that travels with
        // the game instead of hiding in per-user AppData.
        private static string SavesDir
        {
            get
            {
#if UNITY_INCLUDE_TESTS
                if (!string.IsNullOrEmpty(testSavesDirectory)) return testSavesDirectory;
#endif
                string appDir = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(appDir, SavesFolder);
            }
        }

#if UNITY_INCLUDE_TESTS
        public static void UseTestSaveDirectory(string path)
        {
            ResetStatics();
            testSavesDirectory = path;
        }
#endif
        private static string PathFor(int slot) => Path.Combine(SavesDir, $"slot{slot}.json");
        private static string TempPathFor(int slot) => PathFor(slot) + ".tmp";
        private static string BackupPathFor(int slot) => PathFor(slot) + ".bak";

        private static SaveData ReadSlot(int slot)
        {
            string primary = PathFor(slot);
            string temp = TempPathFor(slot);
            string backup = BackupPathFor(slot);

            foreach (string candidate in new[] { primary, temp, backup })
            {
                if (!TryRead(candidate, out SaveData data)) continue;

                if (candidate != primary)
                {
                    Debug.LogWarning($"SaveStore: recovered slot {slot} from '{Path.GetFileName(candidate)}'");
                    WriteSlot(slot, data);
                }
                return data;
            }
            return new SaveData();
        }

        private static bool TryRead(string path, out SaveData data)
        {
            data = null;
            try
            {
                if (!File.Exists(path)) return false;
                data = JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
                if (data == null) return false;

                if (data.version == 1)
                {
                    data.version = SaveData.CurrentVersion;
                    data.inventoryItemIds = Array.Empty<string>();
                    data.inventoryCapacity = InventoryStore.InitialCapacity;
                    data.collectedInventoryCapacityPickupIds = Array.Empty<string>();
                    data.unlockedAbilityIds = Array.Empty<string>();
                }
                else if (data.version == 2)
                {
                    data.version = SaveData.CurrentVersion;
                    data.inventoryItemIds ??= Array.Empty<string>();
                    data.inventoryCapacity = Mathf.Max(InventoryStore.InitialCapacity, data.inventoryCapacity, CountValidIds(data.inventoryItemIds));
                    data.collectedInventoryCapacityPickupIds = Array.Empty<string>();
                    MigrateRetiredTimeCard(data);
                }
                else if (data.version == 3)
                {
                    data.version = SaveData.CurrentVersion;
                    MigrateRetiredTimeCard(data);
                }
                else if (data.version != SaveData.CurrentVersion)
                {
                    return false;
                }

                data.inventoryItemIds ??= Array.Empty<string>();
                data.collectedInventoryCapacityPickupIds ??= Array.Empty<string>();
                data.unlockedAbilityIds ??= Array.Empty<string>();
                data.inventoryCapacity = Mathf.Max(
                    InventoryStore.InitialCapacity,
                    data.inventoryCapacity,
                    CountValidIds(data.inventoryItemIds));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SaveStore: could not read '{path}' ({e.Message})");
                data = null;
                return false;
            }
        }

        private static bool WriteSlot(int slot, SaveData data)
        {
            string primary = PathFor(slot);
            string temp = TempPathFor(slot);
            string backup = BackupPathFor(slot);

            try
            {
                Directory.CreateDirectory(SavesDir);
                byte[] bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(data, true));

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                // Read-back verification (Celeste's write-verify-copy, minus the copy step):
                // the primary is only ever replaced by a file the loader accepts. FromJson throws on
                // malformed text, which the catch below turns into a failed write.
                JsonUtility.FromJson<SaveData>(File.ReadAllText(temp));

                if (File.Exists(primary))
                    File.Replace(temp, primary, backup, true);
                else
                    File.Move(temp, primary);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SaveStore: could not atomically write slot {slot} ({e.Message})");
                return false;
            }
        }

        private static SaveData Clone(SaveData source)
        {
            if (source == null) return new SaveData();
            SaveData clone = JsonUtility.FromJson<SaveData>(JsonUtility.ToJson(source));
            clone.inventoryItemIds ??= Array.Empty<string>();
            clone.collectedInventoryCapacityPickupIds ??= Array.Empty<string>();
            clone.unlockedAbilityIds ??= Array.Empty<string>();
            clone.inventoryCapacity = Mathf.Max(
                InventoryStore.InitialCapacity,
                clone.inventoryCapacity,
                CountValidIds(clone.inventoryItemIds));
            return clone;
        }

        // The pre-v4 world: the timecard rode the backpack and gated checkpoints from there. v4 turned
        // it into an ability, so migration strips the retired item (its definition is gone from the
        // catalogue — Restore would drop it with a warning) and grants the ability it used to gate.
        private const string RetiredTimeCardItemId = "time-card";

        /// <summary>Strips the retired time-card item and grants the checkpoint ability in its place,
        /// so a v2/v3 player who had the card keeps working checkpoints after the change.</summary>
        private static void MigrateRetiredTimeCard(SaveData data)
        {
            data.inventoryItemIds ??= Array.Empty<string>();
            if (!ContainsId(data.inventoryItemIds, RetiredTimeCardItemId))
            {
                data.unlockedAbilityIds = Array.Empty<string>();
                return;
            }

            var kept = new List<string>(data.inventoryItemIds.Length - 1);
            foreach (string id in data.inventoryItemIds)
            {
                if (id != RetiredTimeCardItemId) kept.Add(id);
            }
            data.inventoryItemIds = kept.ToArray();
            data.unlockedAbilityIds = new[] { AbilityIds.Checkpoint };
        }

        private static bool ContainsId(string[] ids, string id)
        {
            if (ids == null) return false;
            foreach (string candidate in ids)
            {
                if (candidate == id) return true;
            }
            return false;
        }

        private static int CountValidIds(string[] ids)
        {
            if (ids == null) return 0;
            int count = 0;
            foreach (string id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id)) count++;
            }
            return count;
        }
    }
}
