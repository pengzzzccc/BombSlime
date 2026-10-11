#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Reflection;
using Inkform.Audio;
using Inkform.Bus;
using Inkform.Fx;
using Inkform.Interactable;
using Inkform.Interactable.Parts;
using Inkform.Input;
using Inkform.Item;
using Inkform.Level;
using Inkform.Life;
using Inkform.Player;
using Inkform.Save;
using Inkform.Settings;
using Inkform.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Inkform.Tests
{
    public sealed class InkformRuntimeEdgeTests
    {
        [TearDown]
        public void TearDown()
        {
            DestroyAllBlastWaves();
            InventoryStore.Clear();
            InvokeStatic(typeof(SaveStore), "ResetStatics");
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        [Test]
        public void Inventory_StartsEmptyAndUniquePickupsGrowBeyondFour()
        {
            InventoryStore.Clear();
            InventoryItemDefinition[] definitions = new InventoryItemDefinition[5];
            try
            {
                for (int i = 0; i < definitions.Length; i++)
                {
                    definitions[i] = ScriptableObject.CreateInstance<InventoryItemDefinition>();
                    SetField(definitions[i], "id", $"test-{i}");
                }

                Assert.AreEqual(0, InventoryStore.Capacity, "a new run has no backpack slots");
                Assert.IsFalse(InventoryStore.TryAdd(definitions[0]), "an item must remain in the world before the first crystal");

                for (int i = 0; i < definitions.Length; i++)
                {
                    Assert.IsTrue(InventoryStore.TryCollectCapacityPickup($"upgrade-{i}", 1));
                    Assert.IsTrue(InventoryStore.TryAdd(definitions[i]));
                }

                Assert.AreEqual(5, InventoryStore.Capacity, "capacity has no four-slot gameplay ceiling");
                CollectionAssert.AreEqual(definitions, InventoryStore.Items);
                Assert.IsFalse(InventoryStore.TryCollectCapacityPickup("upgrade-1", 1),
                    "the same stable pickup id cannot grant capacity twice");
            }
            finally
            {
                foreach (InventoryItemDefinition definition in definitions)
                    if (definition != null) UnityEngine.Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void SaveV1_MigratesToCurrentWithAnEmptySlotlessInventory()
        {
            string path = Path.Combine(Application.temporaryCachePath, $"inkform-v1-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, "{\"version\":1,\"sceneName\":\"B2\",\"spawnX\":2,\"spawnY\":3}");
                MethodInfo read = typeof(SaveStore).GetMethod("TryRead", BindingFlags.Static | BindingFlags.NonPublic);
                object[] args = { path, null };
                Assert.IsTrue((bool)read.Invoke(null, args));
                SaveData data = (SaveData)args[1];
                Assert.AreEqual(SaveData.CurrentVersion, data.version);
                Assert.IsNotNull(data.inventoryItemIds);
                Assert.IsEmpty(data.inventoryItemIds);
                Assert.AreEqual(InventoryStore.InitialCapacity, data.inventoryCapacity);
                Assert.IsNotNull(data.collectedInventoryCapacityPickupIds);
                Assert.IsEmpty(data.collectedInventoryCapacityPickupIds);
                Assert.AreEqual("B2", data.sceneName);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void PendingOverwrite_AbortRestoresPreviousSlot()
        {
            SaveData previous = new SaveData { sceneName = "B3", inventoryItemIds = Array.Empty<string>() };
            SetStaticField(typeof(SaveStore), "slots", new[] { previous, new SaveData(), new SaveData() });

            SaveStore.BeginNewRun(0);
            Assert.IsTrue(SaveStore.Get(0).IsEmpty);
            Assert.IsTrue(SaveStore.AbortNewRun());
            Assert.AreEqual("B3", SaveStore.Get(0).sceneName);
            Assert.AreEqual(-1, SaveStore.ActiveSlot);
        }

        [Test]
        public void SaveRecovery_UsesValidTempWhenPrimaryIsCorrupt()
        {
            string directory = Path.Combine(Application.temporaryCachePath, $"inkform-save-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "slot0.json"), "not-json");
                File.WriteAllText(Path.Combine(directory, "slot0.json.tmp"),
                    JsonUtility.ToJson(new SaveData { sceneName = "B2", inventoryItemIds = Array.Empty<string>() }));

                SaveStore.UseTestSaveDirectory(directory);
                Assert.AreEqual("B2", SaveStore.Get(0).sceneName);

                SaveData recovered = JsonUtility.FromJson<SaveData>(File.ReadAllText(Path.Combine(directory, "slot0.json")));
                Assert.AreEqual("B2", recovered.sceneName, "the valid temp file must repair the primary");
                Assert.IsTrue(File.Exists(Path.Combine(directory, "slot0.json.bak")));
            }
            finally
            {
                InvokeStatic(typeof(SaveStore), "ResetStatics");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void SavedEvent_FiresOnDiskWritesButNotOnInMemoryUpdates()
        {
            string directory = Path.Combine(Application.temporaryCachePath, $"inkform-saved-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            int saved = 0;
            int changed = 0;
            try
            {
                // Redirect first: UseTestSaveDirectory itself calls ResetStatics, which would wipe
                // subscriptions attached before it
                SaveStore.UseTestSaveDirectory(directory);
                SaveStore.Saved += OnSaved;
                SaveStore.Changed += OnChanged;

                SaveStore.BeginNewRun(0);   // in-memory only: clears the slot, writes nothing
                Assert.AreEqual(0, saved, "a pending new run must not claim a disk write");
                Assert.GreaterOrEqual(changed, 1, "BeginNewRun still publishes the in-memory Changed");

                SaveStore.RecordProgress("B2", new Vector2(1f, 2f));   // first valid position -> written
                Assert.AreEqual(1, saved, "recording progress must fire Saved exactly once");
                Assert.IsTrue(File.Exists(Path.Combine(directory, "slot0.json")));

                SaveStore.EndRun();        // SaveNow writes the run's final numbers
                Assert.AreEqual(2, saved);

                Assert.AreEqual("B2", JsonUtility.FromJson<SaveData>(
                    File.ReadAllText(Path.Combine(directory, "slot0.json"))).sceneName);
            }
            finally
            {
                SaveStore.Saved -= OnSaved;
                SaveStore.Changed -= OnChanged;
                InvokeStatic(typeof(SaveStore), "ResetStatics");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }

            void OnSaved() => saved++;
            void OnChanged() => changed++;
        }

        [Test]
        public void GameStateStore_SetFiresOnChangeOnlyAndResetsToBoot()
        {
            int changed = 0;
            GameStateStore.Changed += OnChanged;
            try
            {
                Assert.AreEqual(GameStateStore.GameState.Boot, GameStateStore.Current);

                GameStateStore.Set(GameStateStore.GameState.MainMenu);
                Assert.AreEqual(GameStateStore.GameState.MainMenu, GameStateStore.Current);
                Assert.AreEqual(1, changed);

                GameStateStore.Set(GameStateStore.GameState.MainMenu);
                Assert.AreEqual(1, changed, "re-asserting the state it already holds must stay silent");

                GameStateStore.Set(GameStateStore.GameState.Playing);
                Assert.AreEqual(GameStateStore.GameState.Playing, GameStateStore.Current);
                Assert.AreEqual(2, changed);
            }
            finally
            {
                GameStateStore.Changed -= OnChanged;
                InvokeStatic(typeof(GameStateStore), "ResetStatics");
            }

            Assert.AreEqual(GameStateStore.GameState.Boot, GameStateStore.Current,
                "ResetStatics must return the store to Boot with no subscribers");

            void OnChanged() => changed++;
        }

        [Test]
        public void MainCameraPrefab_FollowsThePlayerCursorMidpoint()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Control/MainCamera.prefab");
            Assert.IsNotNull(prefab);
            CamHandler handler = prefab.GetComponent<CamHandler>();
            Assert.IsNotNull(handler);

            Assert.AreEqual(CamHandler.FollowMode.PlayerCursorMidpoint, GetField<CamHandler.FollowMode>(handler, "mode"),
                "the shipped camera must follow the player/cursor midpoint — switch it back if this was deliberate, " +
                "but not silently");
        }

        [Test]
        public void PauseAndHitStop_AreIndependentFreezeReasons()
        {
            GameObject go = new GameObject("GameTimeController test");
            go.SetActive(false);
            GameTimeController controller = go.AddComponent<GameTimeController>();
            Invoke(controller, "Awake");
            Invoke(controller, "OnEnable");
            try
            {
                controller.RequestHitStop(0.2f);
                Assert.IsTrue(controller.IsHitStopped);
                Assert.AreEqual(0f, Time.timeScale);

                controller.SetUserPaused(true);
                controller.ClearHitStop();
                Assert.IsTrue(controller.IsUserPaused);
                Assert.AreEqual(0f, Time.timeScale, "ending hitstop must not release a user pause");
                Assert.AreEqual(0f, GameTimeController.PresentationDeltaTime,
                    "presentation effects must freeze during a user pause");

                controller.RequestHitStop(0.2f);
                controller.SetUserPaused(false);
                Assert.IsFalse(controller.IsUserPaused);
                Assert.IsTrue(controller.IsHitStopped);
                Assert.AreEqual(0f, Time.timeScale, "unpausing must preserve a remaining hitstop");
                Assert.AreEqual(Time.unscaledDeltaTime, GameTimeController.PresentationDeltaTime, 0.000001f,
                    "presentation effects must keep advancing while only hitstop is active");
            }
            finally
            {
                Invoke(controller, "OnDisable");
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BlastWaveFx_ExpandsWithEaseOutThinsFadesAndRemainsPresentationOnly()
        {
            int blastCount = 0;
            int explodedCount = 0;
            Action<Vector2, float, float> onBlast = (_, _, _) => blastCount++;
            Action<GameObject, Vector2, float> onExploded = (_, _, _) => explodedCount++;
            HazardBus.Blast += onBlast;
            HazardBus.Exploded += onExploded;

            BlastWaveFx wave = null;
            try
            {
                Color color = new Color(1f, 0.8f, 0.3f, 0.95f);
                wave = BlastWaveFx.Spawn(
                    new Vector2(3f, 4f), 2f, color, 0.28f, 0.12f, 0.22f, 0.04f, 64,
                    SortingLayer.NameToID("player"), 2, 1f);

                Assert.IsNotNull(wave);
                AssertVector2(new Vector2(3f, 4f), wave.transform.position);
                LineRenderer line = wave.GetComponent<LineRenderer>();
                Assert.IsNotNull(line);
                Assert.IsTrue(line.loop);
                Assert.AreEqual(64, line.positionCount);
                Assert.AreEqual(SortingLayer.NameToID("player"), line.sortingLayerID);
                Assert.AreEqual(2, line.sortingOrder);
                Assert.AreEqual(0, wave.GetComponents<Collider2D>().Length);
                Assert.IsNull(wave.GetComponent<Rigidbody2D>());
                Assert.AreEqual(0, blastCount, "the visual must not publish another Blast");
                Assert.AreEqual(0, explodedCount, "the visual must not publish gameplay damage");
                Assert.AreEqual(0.12f, wave.CurrentRadius, 0.0001f);
                Assert.AreEqual(0.22f, line.widthMultiplier, 0.0001f);
                Assert.AreEqual(0.95f, line.startColor.a, 0.002f,
                    "LineRenderer colors are stored with 8-bit channel precision");

                AdvanceBlastWave(wave, 0.14f);
                float expectedHalfRadius = Mathf.Lerp(0.12f, 2f, 0.875f);
                Assert.AreEqual(expectedHalfRadius, wave.CurrentRadius, 0.0001f,
                    "half time must use cubic ease-out displacement");
                Assert.AreEqual(0.13f, line.widthMultiplier, 0.0001f);
                Assert.AreEqual(0.475f, line.startColor.a, 0.002f);

                ApplyBlastWave(wave, 1f);
                Assert.AreEqual(2f, wave.CurrentRadius, 0.0001f,
                    "the rendered radius must land exactly on the gameplay blast radius");
                Assert.AreEqual(0.04f, line.widthMultiplier, 0.0001f);
                Assert.AreEqual(0f, line.startColor.a, 0.0001f);

                AdvanceBlastWave(wave, 0.14f);
                Assert.IsTrue(wave == null, "the one-shot object must remove itself at the end");
            }
            finally
            {
                HazardBus.Blast -= onBlast;
                HazardBus.Exploded -= onExploded;
                if (wave != null) UnityEngine.Object.DestroyImmediate(wave.gameObject);
            }
        }

        [Test]
        public void FxDirector_CreatesOneWavePerBlastAndHonorsIntensityAndFalloff()
        {
            DestroyAllBlastWaves();
            float previousIntensity = SettingsStore.FxIntensity;
            GameObject go = new GameObject("FxDirector blast-wave test");
            go.SetActive(false);
            FxDirector director = go.AddComponent<FxDirector>();
            Invoke(director, "OnEnable");
            try
            {
                SetStaticProperty(typeof(SettingsStore), "FxIntensity", 1f);

                HazardBus.RaiseExploded(null, Vector2.zero, 1f);
                HazardBus.RaiseExploded(null, Vector2.zero, 1f);
                Assert.AreEqual(0, FindBlastWaves().Length,
                    "per-victim Exploded signals must not create duplicate rings");

                HazardBus.RaiseBlast(Vector2.zero, 2f, 1f);
                BlastWaveFx[] waves = FindBlastWaves();
                Assert.AreEqual(1, waves.Length, "one overall Blast must create exactly one ring");
                Assert.AreEqual(2f, GetField<float>(waves[0], "targetRadius"), 0.0001f);

                HazardBus.RaiseBlast(new Vector2(0.5f, 0f), 3f, 1f);
                Assert.AreEqual(2, FindBlastWaves().Length,
                    "separate explosions must own independent wave objects");

                DestroyAllBlastWaves();
                SetStaticProperty(typeof(SettingsStore), "FxIntensity", 0f);
                HazardBus.RaiseBlast(Vector2.zero, 2f, 1f);
                Assert.AreEqual(0, FindBlastWaves().Length, "zero FX intensity must disable the ring");

                SetStaticProperty(typeof(SettingsStore), "FxIntensity", 0.5f);
                HazardBus.RaiseBlast(Vector2.zero, 2f, 1f);
                waves = FindBlastWaves();
                Assert.AreEqual(1, waves.Length);
                Assert.AreEqual(2f, GetField<float>(waves[0], "targetRadius"), 0.0001f,
                    "FX intensity must never change the represented blast radius");
                Assert.AreEqual(0.475f, waves[0].GetComponent<LineRenderer>().startColor.a, 0.002f);

                DestroyAllBlastWaves();
                SetStaticProperty(typeof(SettingsStore), "FxIntensity", 1f);
                SetField(director, "falloffRange", 2f);
                HazardBus.RaiseBlast(new Vector2(1f, 0f), 4f, 1f);
                waves = FindBlastWaves();
                Assert.AreEqual(1, waves.Length);
                Assert.AreEqual(4f, GetField<float>(waves[0], "targetRadius"), 0.0001f);
                Assert.AreEqual(0.475f, waves[0].GetComponent<LineRenderer>().startColor.a, 0.002f,
                    "distance falloff must scale opacity only");

                DestroyAllBlastWaves();
                HazardBus.RaiseBlast(new Vector2(2f, 0f), 4f, 1f);
                Assert.AreEqual(0, FindBlastWaves().Length,
                    "an explosion at or beyond the falloff edge must not create an invisible ring");
            }
            finally
            {
                SetStaticProperty(typeof(SettingsStore), "FxIntensity", previousIntensity);
                Invoke(director, "OnDisable");
                DestroyAllBlastWaves();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void MainCameraPrefab_HasBlastWaveDefaults()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Control/MainCamera.prefab");
            Assert.IsNotNull(prefab);
            FxDirector director = prefab.GetComponent<FxDirector>();
            Assert.IsNotNull(director);

            Assert.IsTrue(GetField<bool>(director, "blastWaveEnabled"));
            Color color = GetField<Color>(director, "blastWaveColor");
            Assert.AreEqual(1f, color.r, 0.0001f);
            Assert.AreEqual(0.81960785f, color.g, 0.0001f);
            Assert.AreEqual(0.36078432f, color.b, 0.0001f);
            Assert.AreEqual(0.95f, color.a, 0.0001f);
            Assert.AreEqual(0.28f, GetField<float>(director, "blastWaveDuration"), 0.0001f);
            Assert.AreEqual(0.12f, GetField<float>(director, "blastWaveStartRadius"), 0.0001f);
            Assert.AreEqual(0.22f, GetField<float>(director, "blastWaveStartWidth"), 0.0001f);
            Assert.AreEqual(0.04f, GetField<float>(director, "blastWaveEndWidth"), 0.0001f);
            Assert.AreEqual(64, GetField<int>(director, "blastWaveSegments"));
            Assert.AreEqual("player", GetField<string>(director, "blastWaveSortingLayer"));
            Assert.AreEqual(2, GetField<int>(director, "blastWaveSortingOrder"));
        }

        [Test]
        public void RopeAim_MovesFreelyAndClampsToSafeBounds()
        {
            GameObject go = new GameObject("RopeGun free aim test");
            go.SetActive(false);
            go.AddComponent<Rigidbody2D>();
            RopeGun gun = go.AddComponent<RopeGun>();
            go.SetActive(true);
            InitializeRopeGun(gun);
            try
            {
                SetField(gun, "mouseAimSensitivity", 1f);
                SetField(gun, "aimOffset", new Vector2(1.5f, 0f));
                gun.Aim(new Vector2(1f, 0f), true);
                float movedDistance = GetField<Vector2>(gun, "aimOffset").magnitude;
                Assert.Greater(movedDistance, 1.5f, "aim input must be able to change cursor distance");
                Assert.Less(movedDistance, 4f, "ordinary input must not force the cursor to max range");

                SetField(gun, "aimOffset", new Vector2(0.1f, 0f));
                Invoke(gun, "ClampAimOffsetToRange");
                Assert.AreEqual(0.7f, GetField<Vector2>(gun, "aimOffset").magnitude, 0.001f);

                SetField(gun, "aimOffset", new Vector2(10f, 0f));
                Invoke(gun, "ClampAimOffsetToRange");
                Assert.AreEqual(4f, GetField<Vector2>(gun, "aimOffset").magnitude, 0.001f);
            }
            finally
            {
                ShutdownRopeGun(gun);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void CapacityUpgrade_RejectsInvalidDuplicateAndOverflowWithoutChangingState()
        {
            InventoryStore.Clear();
            int changed = 0;
            void OnChanged() => changed++;
            InventoryStore.Changed += OnChanged;
            try
            {
                Assert.IsFalse(InventoryStore.TryCollectCapacityPickup(null, 1));
                Assert.IsFalse(InventoryStore.TryCollectCapacityPickup("   ", 1));
                Assert.IsFalse(InventoryStore.TryCollectCapacityPickup("zero", 0));
                Assert.IsFalse(InventoryStore.TryCollectCapacityPickup("negative", -1));
                Assert.AreEqual(0, InventoryStore.Capacity);
                Assert.AreEqual(0, changed);

                Assert.IsTrue(InventoryStore.TryCollectCapacityPickup("valid", 2));
                Assert.AreEqual(2, InventoryStore.Capacity);
                Assert.AreEqual(1, changed, "one capacity transaction must publish one inventory change");
                Assert.IsFalse(InventoryStore.TryCollectCapacityPickup("valid", 2));
                Assert.AreEqual(1, changed);

                SetStaticField(typeof(InventoryStore), "capacity", int.MaxValue);
                Assert.IsFalse(InventoryStore.TryCollectCapacityPickup("overflow", 1));
                Assert.IsFalse(InventoryStore.IsCapacityPickupCollected("overflow"));
                Assert.AreEqual(1, changed);
            }
            finally
            {
                InventoryStore.Changed -= OnChanged;
                InventoryStore.Clear();
            }
        }

        [Test]
        public void SaveV2_MigratesCapacityFromNonEmptyInventoryIds()
        {
            string path = Path.Combine(Application.temporaryCachePath, $"inkform-v2-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path,
                    "{\"version\":2,\"sceneName\":\"B2\",\"inventoryItemIds\":[\"allinone-bomb\",\"\",\"allinone-bomb\"]}");
                MethodInfo read = typeof(SaveStore).GetMethod("TryRead", BindingFlags.Static | BindingFlags.NonPublic);
                object[] args = { path, null };
                Assert.IsTrue((bool)read.Invoke(null, args));
                SaveData data = (SaveData)args[1];
                Assert.AreEqual(SaveData.CurrentVersion, data.version);
                Assert.AreEqual(2, data.inventoryCapacity);
                Assert.IsEmpty(data.collectedInventoryCapacityPickupIds);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Save_RestoresLargeCapacityPickupIdsAndKeepsSlotsIsolated()
        {
            string directory = Path.Combine(Application.temporaryCachePath, $"inkform-v3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                SaveData first = new SaveData
                {
                    sceneName = "B2",
                    inventoryItemIds = new[] { "allinone-bomb" },
                    inventoryCapacity = 7,
                    collectedInventoryCapacityPickupIds = new[] { "cave-upgrade", "lab-upgrade" }
                };
                SaveData second = new SaveData
                {
                    sceneName = "B3",
                    inventoryItemIds = Array.Empty<string>(),
                    inventoryCapacity = 2,
                    collectedInventoryCapacityPickupIds = new[] { "other-save-upgrade" }
                };
                File.WriteAllText(Path.Combine(directory, "slot0.json"), JsonUtility.ToJson(first));
                File.WriteAllText(Path.Combine(directory, "slot1.json"), JsonUtility.ToJson(second));

                SaveStore.UseTestSaveDirectory(directory);
                SaveStore.ContinueRun(0);
                Assert.AreEqual(7, InventoryStore.Capacity);
                Assert.AreEqual(1, InventoryStore.Count);
                Assert.IsTrue(InventoryStore.IsCapacityPickupCollected("cave-upgrade"));
                Assert.IsFalse(InventoryStore.IsCapacityPickupCollected("other-save-upgrade"));

                SaveStore.ContinueRun(1);
                Assert.AreEqual(2, InventoryStore.Capacity);
                Assert.AreEqual(0, InventoryStore.Count);
                Assert.IsTrue(InventoryStore.IsCapacityPickupCollected("other-save-upgrade"));
                Assert.IsFalse(InventoryStore.IsCapacityPickupCollected("cave-upgrade"));
            }
            finally
            {
                InvokeStatic(typeof(SaveStore), "ResetStatics");
                InventoryStore.Clear();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Interactable_ResolvesCarriableCapabilityFromAChildPart()
        {
            GameObject root = new GameObject("Interactable carriable lookup test");
            root.SetActive(false);
            root.AddComponent<Rigidbody2D>();
            root.AddComponent<BoxCollider2D>();
            Inkform.Interactable.Interactable node =
                root.AddComponent<Inkform.Interactable.Interactable>();
            GameObject child = new GameObject("Carriable child part");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<CarriablePart>();
            root.SetActive(true);
            try
            {
                Assert.IsTrue(node.TryGetPart(out ICarriable carriable));
                Assert.IsNotNull(carriable);
                Assert.AreEqual(child.transform, carriable.transform);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void InventoryCapacityPart_CollectsOnPlayerContactAndRemovesReloadedDuplicate()
        {
            InventoryStore.Clear();
            GameObject player = new GameObject("Capacity pickup player");
            player.tag = "Player";
            Collider2D playerCollider = player.AddComponent<BoxCollider2D>();
            player.AddComponent<PlayerInventory>();
            GameObject pickup = null;
            GameObject reloadedPickup = null;
            int upgradeEvents = 0;
            int reportedIncrease = 0;
            Vector2 reportedPosition = Vector2.zero;
            Action<Vector2, int> onCapacityUpgraded = (pos, increase) =>
            {
                upgradeEvents++;
                reportedPosition = pos;
                reportedIncrease = increase;
            };
            ItemBus.InventoryCapacityUpgraded += onCapacityUpgraded;
            try
            {
                pickup = CreateCapacityPickup("cave-capacity-01", 2, out InventoryCapacityPart part);
                pickup.transform.position = new Vector2(3f, 4f);
                Assert.IsFalse((object)part is IRestorablePart,
                    "permanent inventory upgrades must not participate in death/checkpoint restoration");
                Assert.IsTrue(part.HandleContact(ContactPhase.Enter, playerCollider));
                Assert.AreEqual(2, InventoryStore.Capacity);
                Assert.IsTrue(InventoryStore.IsCapacityPickupCollected("cave-capacity-01"));
                Assert.AreEqual(1, upgradeEvents);
                Assert.AreEqual(2, reportedIncrease);
                Assert.AreEqual(new Vector2(3f, 4f), reportedPosition);
                // Edit mode: no flight into the HUD (CapacityUpgradeFlight plays only in Play mode)
                Assert.IsTrue(pickup == null, "a collected world pickup must be destroyed immediately");

                reloadedPickup = CreateCapacityPickup("cave-capacity-01", 2, out _);
                Assert.IsTrue(reloadedPickup == null,
                    "an already collected scene instance must remove itself as soon as it attaches");
                Assert.AreEqual(2, InventoryStore.Capacity, "scene reload must not grant capacity again");
                Assert.AreEqual(1, upgradeEvents, "a duplicate pickup must not replay the collection sound event");
            }
            finally
            {
                ItemBus.InventoryCapacityUpgraded -= onCapacityUpgraded;
                if (pickup != null) UnityEngine.Object.DestroyImmediate(pickup);
                if (reloadedPickup != null) UnityEngine.Object.DestroyImmediate(reloadedPickup);
                UnityEngine.Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void InventoryCapacityPickupPrefab_IsAnInteractableTriggerShellWithAssignableSprite()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Item/InventoryCapacityPickup.prefab");
            Assert.IsNotNull(prefab);
            Assert.IsNotNull(prefab.GetComponent<Inkform.Interactable.Interactable>());
            Assert.IsNotNull(prefab.GetComponent<InventoryCapacityPart>());
            Assert.IsTrue(prefab.GetComponent<Collider2D>().isTrigger);
            Assert.IsNotNull(prefab.GetComponent<SpriteRenderer>());
        }

        [Test]
        public void AllinoneBomb_UsesInteractableCarriablePartAndIsDashFuel()
        {
            InventoryItemDefinition definition = AssetDatabase.LoadAssetAtPath<InventoryItemDefinition>(
                "Assets/Resources/Inventory/AllinoneBomb.asset");
            Assert.IsNotNull(definition);
            Assert.IsTrue(definition.IsDashFuel);
            Assert.IsNotNull(definition.WorldPrefab);

            GameObject instance = UnityEngine.Object.Instantiate(definition.WorldPrefab);
            try
            {
                Inkform.Interactable.Interactable node =
                    instance.GetComponent<Inkform.Interactable.Interactable>();
                Assert.IsNotNull(node);
                Assert.IsTrue(node.TryGetPart(out ICarriable carriable));
                Assert.AreSame(definition, carriable.Definition);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void PlayerInventory_ReleaseFailureRetainsFifoHeadAndSuccessRemovesOnlyHead()
        {
            GameObject player = new GameObject("FIFO release test");
            PlayerInventory inventory = player.AddComponent<PlayerInventory>();
            InventoryItemDefinition first = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            InventoryItemDefinition second = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            InventoryItemDefinition bombDefinition = AssetDatabase.LoadAssetAtPath<InventoryItemDefinition>(
                "Assets/Resources/Inventory/AllinoneBomb.asset");
            SetField(first, "id", "fifo-first");
            SetField(second, "id", "fifo-second");
            try
            {
                Assert.IsTrue(inventory.TryCollectCapacityUpgrade("fifo-release-capacity", 2));
                Assert.IsTrue(inventory.TryStore(first));
                Assert.IsTrue(inventory.TryStore(second));

                Assert.IsFalse(inventory.TryReleaseFirst(Vector2.right));
                Assert.AreEqual(2, inventory.Count);
                Assert.IsTrue(InventoryStore.TryPeekFirst(out InventoryItemDefinition retained));
                Assert.AreSame(first, retained, "failed prefab creation must retain the FIFO head");

                SetField(first, "worldPrefab", bombDefinition.WorldPrefab);
                Assert.IsTrue(inventory.TryReleaseFirst(Vector2.right));
                Assert.AreEqual(1, inventory.Count);
                Assert.IsTrue(InventoryStore.TryPeekFirst(out InventoryItemDefinition next));
                Assert.AreSame(second, next);
            }
            finally
            {
                foreach (Inkform.Interactable.Interactable spawned in
                    UnityEngine.Object.FindObjectsByType<Inkform.Interactable.Interactable>())
                {
                    if (spawned != null && spawned.name == "AllinoneBomb(Clone)")
                        UnityEngine.Object.DestroyImmediate(spawned.gameObject);
                }
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void Hud_InventoryShowsCountAndUsesFifoHeadIcon()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/Hud.uxml");
            Assert.IsNotNull(tree, "missing Resources/UI/Hud.uxml");
            VisualElement host = tree.Instantiate();
            Hud hud = new Hud(host);
            InventoryItemDefinition item = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            SetField(item, "id", "hud-head");
            try
            {
                Label count = host.Q<Label>("InventoryCount");
                VisualElement icon = host.Q("InventoryIcon");
                Assert.AreEqual("0/0", count.text);
                Assert.AreEqual(DisplayStyle.None, icon.style.display.value);

                Assert.IsTrue(InventoryStore.TryCollectCapacityPickup("hud-head-capacity", 1));
                Assert.AreEqual("0/1", count.text);
                Assert.IsTrue(InventoryStore.TryAdd(item));
                Assert.AreEqual("1/1", count.text);
                Assert.AreEqual(item.Icon, icon.style.backgroundImage.value.sprite);
            }
            finally
            {
                hud.Dispose();
                UnityEngine.Object.DestroyImmediate(item);
            }
        }

        [Test]
        public void Hud_HoldsTheNewCapacityBackUntilTheCrystalLands()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/Hud.uxml");
            Assert.IsNotNull(tree, "missing Resources/UI/Hud.uxml");
            VisualElement host = tree.Instantiate();
            Hud hud = new Hud(host);
            try
            {
                Label count = host.Q<Label>("InventoryCount");
                Assert.IsNotNull(host.Q<Label>("InventoryPlus"), "the landing \"+1\" label");
                Assert.AreEqual("0/0", count.text);

                // The pickup's order: the store grows, then the flight rises — the same frame
                Assert.IsTrue(InventoryStore.TryCollectCapacityPickup("hud-crystal", 1));
                ItemBus.RaiseCapacityFlight(CapacityFlightPhase.Rise, 1);
                Assert.AreEqual("0/0", count.text, "the old capacity stays up while the crystal flies");
                ItemBus.RaiseCapacityFlight(CapacityFlightPhase.Hold, 1);
                ItemBus.RaiseCapacityFlight(CapacityFlightPhase.Fly, 1);
                Assert.AreEqual("0/0", count.text);

                ItemBus.RaiseCapacityFlight(CapacityFlightPhase.Land, 1);
                Assert.AreEqual("0/1", count.text, "landing shows the new capacity");

                // A flight torn down early releases its hold too
                Assert.IsTrue(InventoryStore.TryCollectCapacityPickup("hud-crystal-2", 1));
                ItemBus.RaiseCapacityFlight(CapacityFlightPhase.Rise, 1);
                Assert.AreEqual("0/1", count.text);
                ItemBus.RaiseCapacityFlight(CapacityFlightPhase.Cancel, 1);
                Assert.AreEqual("0/2", count.text);
            }
            finally
            {
                hud.Dispose();
            }
        }

        [Test]
        public void InventoryStore_ClearItemsKeepsTheCapacityAndTheCollectedCrystals()
        {
            InventoryItemDefinition item = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            SetField(item, "id", "clear-items-bomb");
            try
            {
                Assert.IsTrue(InventoryStore.TryCollectCapacityPickup("clear-items-crystal", 2));
                Assert.IsTrue(InventoryStore.TryAdd(item));
                Assert.IsTrue(InventoryStore.TryAdd(item));
                int notifications = 0;
                Action onChanged = () => notifications++;
                InventoryStore.Changed += onChanged;
                try
                {
                    InventoryStore.ClearItems();
                    Assert.AreEqual(0, InventoryStore.Count);
                    Assert.AreEqual(2, InventoryStore.Capacity);
                    Assert.IsTrue(InventoryStore.IsCapacityPickupCollected("clear-items-crystal"));
                    Assert.AreEqual(1, notifications);

                    InventoryStore.ClearItems();
                    Assert.AreEqual(1, notifications, "an empty backpack has nothing to clear or save");
                }
                finally
                {
                    InventoryStore.Changed -= onChanged;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(item);
            }
        }

        [Test]
        public void PlayerInventory_EmptiesTheBackpackOnlyOnItsOwnDeath()
        {
            GameObject player = new GameObject("Death clears backpack player");
            PlayerInventory inventory = player.AddComponent<PlayerInventory>();
            GameObject other = new GameObject("Some other victim");
            InventoryItemDefinition item = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            SetField(item, "id", "death-clear-bomb");
            MethodInfo onDied = typeof(PlayerInventory).GetMethod("OnDied", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(onDied, "PlayerInventory listens for LifeBus.Died");
            try
            {
                Assert.IsTrue(InventoryStore.TryCollectCapacityPickup("death-clear-crystal", 2));
                Assert.IsTrue(InventoryStore.TryAdd(item));
                Assert.IsTrue(InventoryStore.TryAdd(item));

                onDied.Invoke(inventory, new object[] { new DeathContext(other, Vector2.zero, DeathCause.Spike) });
                Assert.AreEqual(2, InventoryStore.Count, "someone else's death leaves the backpack alone");

                onDied.Invoke(inventory, new object[] { new DeathContext(player, Vector2.zero, DeathCause.Spike) });
                Assert.AreEqual(0, InventoryStore.Count, "the player's death empties the backpack");
                Assert.AreEqual(2, InventoryStore.Capacity, "the crystals' capacity is permanent");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player);
                UnityEngine.Object.DestroyImmediate(other);
                UnityEngine.Object.DestroyImmediate(item);
            }
        }

        [Test]
        public void HudUxml_HasAllFourReadoutsAndNeverBlocksPointer()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/Hud.uxml");
            Assert.IsNotNull(tree);
            VisualElement host = tree.Instantiate();

            Assert.IsNotNull(host.Q("Gameplay"));
            Assert.IsNotNull(host.Q("TimerRow"), "Show Timer hides this row");
            Assert.IsNotNull(host.Q("ClockIcon"));
            Assert.IsNotNull(host.Q<Label>("TimerLabel"));
            Assert.IsNotNull(host.Q("DeathIcon"));
            Assert.IsNotNull(host.Q<Label>("DeathLabel"));
            Assert.IsNotNull(host.Q<Label>("FpsLabel"));
            Assert.IsNotNull(host.Q("Inventory"));
            Assert.IsNotNull(host.Q("InventoryIcon"));
            Assert.IsNotNull(host.Q<Label>("InventoryCount"));
            Assert.IsNotNull(host.Q<Label>("InventoryPlus"));
            Assert.IsNotNull(host.Q<Label>("SaveToast"));

            // The old prefab asserted raycastTarget == false on every Graphic; the Toolkit shape
            // of that contract is picking-mode Ignore on every element of the HUD tree.
            foreach (VisualElement element in host.Query().Build())
                Assert.AreEqual(PickingMode.Ignore, element.pickingMode, $"{element.name} must not block pointer input");
        }

        [Test]
        public void GameManager_KeepsUiManagerAndItsSoundCueSlots()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Control/GameManager.prefab");
            Assert.IsNotNull(prefab);

            UIManager manager = prefab.GetComponent<UIManager>();
            Assert.IsNotNull(manager);

            // The Toolkit migration kept the serialized cue slots (the UiBus -> AudioService
            // pipeline is unchanged); losing them would silently mute every menu sound.
            SerializedObject serialized = new SerializedObject(manager);
            Assert.IsNotNull(serialized.FindProperty("hoverCue"));
            Assert.IsNotNull(serialized.FindProperty("clickCue"));
            Assert.IsNotNull(serialized.FindProperty("toggleOnCue"));
            Assert.IsNotNull(serialized.FindProperty("toggleOffCue"));
        }

        [Test]
        public void EndPanelUxml_HasBackButtonAndBothRunReadouts()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/EndPanel.uxml");
            Assert.IsNotNull(tree, "missing Resources/UI/EndPanel.uxml");

            VisualElement host = tree.Instantiate();
            // EndPanel finds every control by element name (ToolkitPanel.Q), so the names are the
            // contract with the UXML — a rename here silently leaves the sheet empty at runtime.
            Assert.IsNotNull(host.Q("TitleRow"));
            Assert.IsNotNull(host.Q<UnityEngine.UIElements.Button>("Btn_Back"));
            Assert.IsNotNull(host.Q<Label>("Lbl_Deaths"));
            Assert.IsNotNull(host.Q<Label>("Lbl_Time"));
        }

        [Test]
        public void ToolkitUi_ResourcesContainEverySheetAndTheme()
        {
            // Panels the UIManager mounts, plus the HUD and the menu scene's backdrop layer
            foreach (string sheet in new[] { "MainMenu", "SaveMenu", "PauseMenu", "Settings", "EndPanel", "Hud",
                                             "Boot", "MenuBackdrop" })
            {
                VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"Assets/Resources/UI/{sheet}.uxml");
                Assert.IsNotNull(tree, $"missing Resources/UI/{sheet}.uxml — the UIManager logs a warning and loses that sheet");
            }

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Resources/UI/Theme.uss"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>("Assets/Resources/UI/RuntimeTheme.tss"));

            // Boot content asset: every slot ships empty on purpose (text placeholders + silence),
            // but the asset itself must exist — the sequence's timings read from it
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Inkform.UI.BootLogos>("Assets/Resources/UI/BootLogos.asset"),
                "missing Resources/UI/BootLogos.asset — the boot sequence falls back to hard defaults");
        }

        [Test]
        public void GameManager_RuntimeSpawnerReferencesThePlayerHandlerRoot()
        {
            GameObject managerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Control/GameManager.prefab");
            GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Control/Player.prefab");
            Assert.IsNotNull(managerPrefab);
            Assert.IsNotNull(playerPrefab);

            RespawnDirector respawn = managerPrefab.GetComponent<RespawnDirector>();
            Assert.IsNotNull(respawn);
            SerializedProperty property = new SerializedObject(respawn).FindProperty("playerPrefab");
            Assert.IsNotNull(property);
            Assert.AreSame(playerPrefab.GetComponent<PlayerHandler>(), property.objectReferenceValue,
                "the runtime spawn slot must reference PlayerHandler on the Player root, never a sensor child");
            Assert.IsNotNull(playerPrefab.GetComponent<SpriteRenderer>());
            Assert.IsNotNull(playerPrefab.GetComponent<Animator>());
            Assert.IsNotNull(playerPrefab.GetComponent<Rigidbody2D>());
            Assert.IsNotNull(playerPrefab.GetComponent<Collider2D>());
        }

        [Test]
        public void RoomIntroPrefab_ContainsOnlyTheCoordinatedEntranceTiming()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Control/RoomIntro.prefab");
            Assert.IsNotNull(prefab);
            RoomIntro intro = prefab.GetComponent<RoomIntro>();
            Assert.IsNotNull(intro);

            SerializedObject serialized = new SerializedObject(intro);
            AssertSerializedReference(intro, "camAnchor");
            AssertSerializedReference(intro, "introCue");
            Assert.AreEqual(1f, serialized.FindProperty("spawnDelay").floatValue);
            Assert.AreEqual(1f, serialized.FindProperty("barsSeconds").floatValue);
            Assert.IsNull(serialized.FindProperty("fallHeight"));
            Assert.IsNull(serialized.FindProperty("startDelay"));
            Assert.IsNull(serialized.FindProperty("panSeconds"));

            CinematicBars bars = prefab.GetComponent<CinematicBars>();
            Assert.IsNotNull(bars);
            Assert.AreEqual(0.1f,
                new SerializedObject(bars).FindProperty("barHeightFraction").floatValue);
        }

        [Test]
        public void CinematicBars_ShowInstantUsesTheConfiguredFilmHeight()
        {
            GameObject host = new GameObject("cinematic bars test", typeof(CinematicBars));
            try
            {
                CinematicBars bars = host.GetComponent<CinematicBars>();
                bars.ShowInstant();

                RectTransform top = GetField<RectTransform>(bars, "top");
                RectTransform bottom = GetField<RectTransform>(bars, "bottom");
                Assert.IsNotNull(top);
                Assert.IsNotNull(bottom);
                Assert.AreEqual(Screen.height * 0.1f, top.sizeDelta.y, 0.01f);
                Assert.AreEqual(top.sizeDelta.y, bottom.sizeDelta.y, 0.01f);
                if (Screen.height > 0) Assert.Less(top.sizeDelta.y, Screen.height);

                foreach (Graphic graphic in host.GetComponentsInChildren<Graphic>(true))
                    Assert.IsFalse(graphic.raycastTarget);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CamHandler_HoldAndSmoothResumePreserveTheStagedPosition()
        {
            GameObject cameraGo = new GameObject("held camera", typeof(Camera), typeof(CamHandler));
            GameObject anchorGo = new GameObject("camera anchor");
            anchorGo.transform.position = new Vector3(12f, 8f, 4f);
            try
            {
                CamHandler handler = cameraGo.GetComponent<CamHandler>();
                handler.HoldAt(anchorGo.transform);
                Assert.IsTrue(handler.IsFollowHeld);
                Assert.AreEqual(new Vector3(12f, 8f, cameraGo.transform.position.z), cameraGo.transform.position);

                Vector3 staged = cameraGo.transform.position;
                handler.ResumeFollow();
                Assert.IsFalse(handler.IsFollowHeld);
                Assert.AreEqual(staged, cameraGo.transform.position,
                    "smooth resume must not cut to the player on the release frame");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(anchorGo);
                UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        [Test]
        public void CamHandler_MapFocusMovesAndZoomsWithoutDisablingFollowComponent()
        {
            InvokeStatic(typeof(PlayerBus), "ResetStatics");
            GameObject cameraGo = new GameObject("map focus camera", typeof(Camera), typeof(CamHandler));
            GameObject anchorGo = new GameObject("map focus anchor");
            GameObject targetGo = new GameObject("map return target");
            anchorGo.transform.position = new Vector3(12f, 8f, 0f);
            targetGo.transform.position = new Vector3(3f, 2f, 0f);
            try
            {
                Camera camera = cameraGo.GetComponent<Camera>();
                camera.orthographic = true;
                CamHandler handler = cameraGo.GetComponent<CamHandler>();
                SetField(handler, "target", targetGo.transform);
                SetField(handler, "lookAhead", 0f);

                System.Collections.IEnumerator focus = handler.FocusAt(anchorGo.transform, 2.5f, 0f);
                while (focus.MoveNext()) { }
                Invoke(handler, "LateUpdate");

                Assert.IsTrue(handler.enabled, "focus keeps CamHandler alive for shake and lens presentation");
                Assert.IsTrue(handler.IsFollowHeld);
                Assert.AreEqual(new Vector3(12f, 8f, cameraGo.transform.position.z), cameraGo.transform.position);
                Assert.AreEqual(2.5f, camera.orthographicSize, 0.001f);

                System.Collections.IEnumerator back = handler.ReturnToFollow(0f);
                while (back.MoveNext()) { }
                Invoke(handler, "LateUpdate");

                Assert.IsFalse(handler.IsFollowHeld);
                Assert.AreEqual(5f, camera.orthographicSize, 0.001f);
                Assert.AreEqual(new Vector3(3f, 2f, cameraGo.transform.position.z), cameraGo.transform.position);
            }
            finally
            {
                InvokeStatic(typeof(PlayerBus), "ResetStatics");
                UnityEngine.Object.DestroyImmediate(targetGo);
                UnityEngine.Object.DestroyImmediate(anchorGo);
                UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        [Test]
        public void GameTimeController_WorldFreezeStacksAndLeavesAudioUnpaused()
        {
            GameObject host = new GameObject("world-freeze controller");
            GameObject ownerA = new GameObject("freeze owner A");
            GameObject ownerB = new GameObject("freeze owner B");
            GameTimeController controller = host.AddComponent<GameTimeController>();
            try
            {
                controller.SetWorldFrozen(ownerA, true);
                controller.SetWorldFrozen(ownerB, true);
                Assert.IsTrue(controller.IsWorldFrozen);
                Assert.AreEqual(0f, Time.timeScale);
                Assert.IsFalse(AudioListener.pause, "wall-map freeze must not pause music");

                controller.SetUserPaused(true);
                controller.SetUserPaused(false);
                Assert.AreEqual(0f, Time.timeScale,
                    "resuming the pause menu must not release another owner's world freeze");
                Assert.IsFalse(AudioListener.pause);

                controller.SetWorldFrozen(ownerA, false);
                Assert.AreEqual(0f, Time.timeScale, "the second owner still holds the freeze");
                controller.SetWorldFrozen(ownerB, false);
                Assert.IsFalse(controller.IsWorldFrozen);
                Assert.AreEqual(1f, Time.timeScale);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ownerB);
                UnityEngine.Object.DestroyImmediate(ownerA);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void InputHandler_ScopedLockSurvivesPauseResumeRequests()
        {
            GameObject host = new GameObject("input-lock handler");
            GameObject owner = new GameObject("input-lock owner");
            InputHandler input = host.AddComponent<InputHandler>();
            try
            {
                input.SetPlaying(true);
                Assert.IsTrue(GetField<bool>(input, "actionsEnabled"));

                input.SetGameplayInputLocked(owner, true);
                Assert.IsFalse(GetField<bool>(input, "actionsEnabled"));

                input.SetPlaying(false);
                input.SetPlaying(true);
                Assert.IsFalse(GetField<bool>(input, "actionsEnabled"),
                    "flow resume cannot bypass a live presentation lock");

                input.SetGameplayInputLocked(owner, false);
                Assert.IsTrue(GetField<bool>(input, "actionsEnabled"));
            }
            finally
            {
                input.SetPlaying(false);
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void MapViewPrefab_HasWallContentAnchorAndFocusConfiguration()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/MapView.prefab");
            Assert.IsNotNull(prefab);

            MapViewPart map = prefab.GetComponent<MapViewPart>();
            Assert.IsNotNull(map);
            SpriteRenderer content = GetField<SpriteRenderer>(map, "mapContent");
            Assert.IsNotNull(content);
            Assert.IsNotNull(content.sprite, "the prefab retains a visible placeholder until final map art is assigned");
            Assert.IsNotNull(GetField<Transform>(map, "viewAnchor"));
            Assert.AreEqual(2.5f, GetField<float>(map, "focusOrthoSize"), 0.001f);
            Assert.AreEqual(0.6f, GetField<float>(map, "glideSeconds"), 0.001f);
            Assert.IsTrue(prefab.GetComponent<Collider2D>().isTrigger);
            Assert.IsNotNull(prefab.GetComponent<InteractionPromptPart>());
        }

        [Test]
        public void MapViewPrompt_CanBeSuppressedDuringTheFrozenInspection()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/MapView.prefab");
            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                InteractionPromptPart prompt = instance.GetComponent<InteractionPromptPart>();
                Assert.IsNotNull(prompt);
                prompt.SetSuppressed(true);
                Assert.IsTrue(prompt.IsSuppressed);
                prompt.SetSuppressed(false);
                Assert.IsFalse(prompt.IsSuppressed);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [TestCase(0f, "00:00:000")]
        [TestCase(-3f, "00:00:000")]
        [TestCase(59.999f, "00:59:999")]
        [TestCase(61.5f, "01:01:500")]
        [TestCase(3599.5f, "59:59:500")]
        [TestCase(4503.12f, "75:03:120")]
        public void RunTime_FormatsMinutesSecondsMilliseconds(float seconds, string expected)
        {
            Assert.AreEqual(expected, RunTimeFormat.Format(seconds));
        }

        [Test]
        public void PlayerInventory_DashConsumesEarliestFuelAndPreservesFifoOrder()
        {
            GameObject go = new GameObject("PlayerInventory fuel test");
            PlayerInventory inventory = go.AddComponent<PlayerInventory>();
            InventoryItemDefinition fuelA = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            InventoryItemDefinition normal = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            InventoryItemDefinition fuelB = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            SetField(fuelA, "id", "fuel-a");
            SetField(fuelA, "dashFuel", true);
            SetField(normal, "id", "normal");
            SetField(fuelB, "id", "fuel-b");
            SetField(fuelB, "dashFuel", true);
            try
            {
                Assert.IsTrue(inventory.TryCollectCapacityUpgrade("fuel-test-capacity", 3));
                Assert.IsTrue(inventory.TryStore(normal));
                Assert.IsTrue(inventory.TryStore(fuelA));
                Assert.IsTrue(inventory.TryStore(fuelB));

                int changed = 0;
                InventoryStore.Changed += OnChanged;
                try
                {
                    Assert.IsTrue(inventory.TryConsumeDashFuel());
                    Assert.AreEqual(1, changed, "one fuel consumption must publish one inventory change");
                }
                finally
                {
                    InventoryStore.Changed -= OnChanged;
                }

                CollectionAssert.AreEqual(new[] { normal, fuelB }, InventoryStore.Items,
                    "the earliest fuel must be removed without reordering non-consumed items");
                Assert.IsTrue(inventory.TryConsumeDashFuel());
                CollectionAssert.AreEqual(new[] { normal }, InventoryStore.Items);
                Assert.IsFalse(inventory.TryConsumeDashFuel());

                void OnChanged() => changed++;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fuelA);
                UnityEngine.Object.DestroyImmediate(normal);
                UnityEngine.Object.DestroyImmediate(fuelB);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PlayerDash_ConsumesFuelAndPublishesSuccessThenFailure()
        {
            GameObject go = new GameObject("Bomb-powered dash test");
            go.SetActive(false);
            Rigidbody2D body = go.AddComponent<Rigidbody2D>();
            go.AddComponent<ContactSensor>();
            PlayerMotor motor = go.AddComponent<PlayerMotor>();
            AnimStateResolver anim = go.AddComponent<AnimStateResolver>();
            PlayerInventory inventory = go.AddComponent<PlayerInventory>();
            PlayerHandler player = go.AddComponent<PlayerHandler>();
            InventoryItemDefinition fuel = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            SetField(fuel, "id", "dash-fuel");
            SetField(fuel, "dashFuel", true);
            go.SetActive(true);
            Invoke(motor, "Awake");
            Invoke(anim, "Awake");
            Invoke(player, "Awake");

            int attempts = 0;
            bool lastSucceeded = false;
            Action<Vector2, bool> onDashAttempted = (pos, succeeded) =>
            {
                attempts++;
                lastSucceeded = succeeded;
            };
            PlayerBus.DashAttempted += onDashAttempted;
            try
            {
                Assert.IsTrue(inventory.TryCollectCapacityUpgrade("dash-fuel-capacity", 1));
                Assert.IsTrue(inventory.TryStore(fuel));
                player.Dash();
                Assert.AreEqual(0, inventory.Count);
                Assert.Greater(body.linearVelocityX, 0f);
                Assert.AreEqual(1, attempts);
                Assert.IsTrue(lastSucceeded);

                body.linearVelocity = Vector2.zero;
                player.Dash();
                Assert.AreEqual(Vector2.zero, body.linearVelocity,
                    "a dash without fuel must not change velocity");
                Assert.AreEqual(2, attempts);
                Assert.IsFalse(lastSucceeded);

                // Free-angle dash, fixed trajectory, gravity-free (motor-level — this rig has no rope
                // gun): direction is captured as given, StepDash reasserts it every Tick, and
                // ApplyNonLinearGravity yields while the dash timer runs. movingSpeed/attackMultiplier
                // are the code defaults here (10 / 1), so a straight-up dash asserts as exactly (0, 10)
                motor.Dash(Vector2.up);
                Assert.AreEqual(0f, body.linearVelocityX);
                Assert.Greater(body.linearVelocityY, 0f);
                Invoke(motor, "ApplyNonLinearGravity");
                Assert.AreEqual(0f, body.gravityScale, "a dash must be gravity-free");

                body.linearVelocity = new Vector2(5f, -3f);   // e.g. a knockback landed mid-dash
                motor.Tick();   // public — Invoke()'s reflection only resolves NonPublic members
                Assert.AreEqual(new Vector2(0f, 10f), body.linearVelocity,
                    "StepDash must reassert the captured direction and speed every frame");

            }
            finally
            {
                PlayerBus.DashAttempted -= onDashAttempted;
                UnityEngine.Object.DestroyImmediate(fuel);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PlayerDash_SmashesBreakableInFrontThroughTheBombPath()
        {
            // Player rig — same shape as the fuel-dash test above
            GameObject go = new GameObject("Dash break test player");
            go.SetActive(false);
            Rigidbody2D body = go.AddComponent<Rigidbody2D>();
            go.AddComponent<ContactSensor>();
            PlayerMotor motor = go.AddComponent<PlayerMotor>();
            AnimStateResolver anim = go.AddComponent<AnimStateResolver>();
            PlayerInventory inventory = go.AddComponent<PlayerInventory>();
            PlayerHandler player = go.AddComponent<PlayerHandler>();
            DashBreaker breaker = go.AddComponent<DashBreaker>();   // own independent loop, RopeGun pattern
            InventoryItemDefinition fuel = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            SetField(fuel, "id", "dash-fuel");
            SetField(fuel, "dashFuel", true);

            // Wall rig helper — the BreakableWall prefab's shape: one node,
            // Interactable + BreakablePart + BoxCollider2D (+ renderer to observe the hide)
            BoxCollider2D WallRig(string name, int layer, float x, out SpriteRenderer sprite)
            {
                var wallGo = new GameObject(name);
                wallGo.layer = layer;
                BoxCollider2D box = wallGo.AddComponent<BoxCollider2D>();
                box.size = Vector2.one;
                sprite = wallGo.AddComponent<SpriteRenderer>();
                wallGo.AddComponent<Inkform.Interactable.Interactable>();
                Invoke(wallGo.AddComponent<BreakablePart>(), "OnEnable");   // EditMode never runs it; the bus subscription is the smash path
                wallGo.transform.position = new Vector3(x, 0f, 0f);
                Physics2D.SyncTransforms();
                return box;
            }

            // The regression this test locks: PlayerTest's standable BreakableWall sits on
            // Terrain(6), not Breakable(11) — the probe mask must cover both layers
            BoxCollider2D wallTerrain = WallRig("Dash break wall A (Terrain)", 6, 1.2f, out SpriteRenderer spriteA);
            BoxCollider2D wallBreakable = WallRig("Dash break wall B (Breakable)", 11, 2.6f, out SpriteRenderer spriteB);

            int blasts = 0;
            Action<Vector2, float, float> onBlast = (_, _, _) => blasts++;
            HazardBus.Blast += onBlast;
            try
            {
                Assert.IsTrue(inventory.TryCollectCapacityUpgrade("dash-break-capacity", 1));
                Assert.IsTrue(inventory.TryStore(fuel));

                go.SetActive(true);
                Invoke(motor, "Awake");
                Invoke(anim, "Awake");
                Invoke(player, "Awake");
                Invoke(breaker, "Awake");

                player.Dash();                    // facing defaults R: dashes straight through both walls
                body.position += Vector2.right * 0.5f;
                Physics2D.SyncTransforms();
                Invoke(breaker, "Update");        // the breaker's own loop, not PlayerHandler's chain

                Assert.IsFalse(wallTerrain.enabled, "a dash must smash a standable breakable on the Terrain layer");
                Assert.IsFalse(spriteA.enabled, "the smashed wall's renderers must hide");

                body.position += Vector2.right * 1.2f;   // same dash carries on into the second wall
                Physics2D.SyncTransforms();
                Invoke(breaker, "Update");

                Assert.IsFalse(wallBreakable.enabled, "the same dash must also smash a Breakable-layer wall");
                Assert.IsFalse(spriteB.enabled, "the smashed wall's renderers must hide");
                Assert.AreEqual(1, blasts, "exactly one blast wave per dash, however many walls it grinds through");

                Invoke(breaker, "Update");        // broken walls' colliders are off: nothing re-raises
                Assert.AreEqual(1, blasts, "the smash must not re-fire on a broken wall");

                // Without a dash (timer cleared by a respawn) the same proximity must not break anything
                BoxCollider2D wall2 = WallRig("Dash break wall C (no dash)", 11, -0.8f, out _);
                motor.RespawnAt(Vector2.zero);    // clears the dash timer
                Invoke(breaker, "Update");
                Assert.IsTrue(wall2.enabled, "walking-pace proximity without a dash must not smash");
                UnityEngine.Object.DestroyImmediate(wall2.gameObject);
            }
            finally
            {
                HazardBus.Blast -= onBlast;
                UnityEngine.Object.DestroyImmediate(fuel);
                UnityEngine.Object.DestroyImmediate(wallTerrain.gameObject);
                UnityEngine.Object.DestroyImmediate(wallBreakable.gameObject);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AudioDirector_DashAttemptPlaysTriggerAndSuccessAddsBlastWithoutHazardBlast()
        {
            GameObject go = new GameObject("Dash audio routing test");
            go.SetActive(false);
            AudioService manager = go.AddComponent<AudioService>();
            SetField(manager, "voiceCount", 4);
            AudioDirector director = go.AddComponent<AudioDirector>();
            SoundCue trigger = ScriptableObject.CreateInstance<SoundCue>();
            SoundCue blast = ScriptableObject.CreateInstance<SoundCue>();
            AudioClip triggerClip = AudioClip.Create("dash-trigger", 32, 1, 8000, false);
            AudioClip blastClip = AudioClip.Create("dash-blast", 32, 1, 8000, false);
            trigger.clips = new[] { triggerClip };
            trigger.cooldown = 0f;
            trigger.maxConcurrent = 4;
            blast.clips = new[] { blastClip };
            blast.cooldown = 0f;
            blast.maxConcurrent = 4;
            SetField(director, "bombTick", trigger);
            SetField(director, "blast", blast);
            InvokeStatic(typeof(AudioService), "ResetStatics");
            go.SetActive(true);
            if (AudioService.Instance != manager) Invoke(manager, "Awake");
            Invoke(director, "OnEnable");

            int hazardBlasts = 0;
            Action<Vector2, float, float> onHazardBlast = (center, radius, force) => hazardBlasts++;
            HazardBus.Blast += onHazardBlast;
            try
            {
                PlayerBus.RaiseDashAttempted(Vector2.zero, false);
                Assert.AreEqual(1, manager.ActiveCountOf(trigger));
                Assert.AreEqual(0, manager.ActiveCountOf(blast));

                PlayerBus.RaiseDashAttempted(Vector2.zero, true);
                Assert.AreEqual(2, manager.ActiveCountOf(trigger));
                Assert.AreEqual(1, manager.ActiveCountOf(blast));
                Assert.AreEqual(0, hazardBlasts,
                    "dash audio must not publish a gameplay explosion");

            }
            finally
            {
                HazardBus.Blast -= onHazardBlast;
                Invoke(director, "OnDisable");
                UnityEngine.Object.DestroyImmediate(go);
                InvokeStatic(typeof(AudioService), "ResetStatics");
                UnityEngine.Object.DestroyImmediate(trigger);
                UnityEngine.Object.DestroyImmediate(blast);
                UnityEngine.Object.DestroyImmediate(triggerClip);
                UnityEngine.Object.DestroyImmediate(blastClip);
            }
        }

        [Test]
        public void AudioDirector_CapacityPickupUsesConfiguredItemSoundOnce()
        {
            GameObject gameManagerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Control/GameManager.prefab");
            AudioDirector prefabDirector = gameManagerPrefab.GetComponent<AudioDirector>();
            Assert.IsNotNull(prefabDirector);
            Assert.IsNotNull(GetField<SoundCue>(prefabDirector, "inventoryCapacityUpgrade"),
                "the existing GameManager must ship with a capacity-pickup cue assigned");

            GameObject go = new GameObject("Capacity pickup audio routing test");
            go.SetActive(false);
            AudioService manager = go.AddComponent<AudioService>();
            SetField(manager, "voiceCount", 2);
            AudioDirector director = go.AddComponent<AudioDirector>();
            SoundCue cue = ScriptableObject.CreateInstance<SoundCue>();
            AudioClip clip = AudioClip.Create("capacity-pickup", 32, 1, 8000, false);
            cue.clips = new[] { clip };
            cue.cooldown = 0f;
            cue.maxConcurrent = 2;
            SetField(director, "inventoryCapacityUpgrade", cue);
            InvokeStatic(typeof(AudioService), "ResetStatics");
            go.SetActive(true);
            if (AudioService.Instance != manager) Invoke(manager, "Awake");
            Invoke(director, "OnEnable");
            try
            {
                ItemBus.RaiseInventoryCapacityUpgraded(new Vector2(3f, 4f), 1);
                Assert.AreEqual(1, manager.ActiveCountOf(cue));
            }
            finally
            {
                Invoke(director, "OnDisable");
                UnityEngine.Object.DestroyImmediate(go);
                InvokeStatic(typeof(AudioService), "ResetStatics");
                UnityEngine.Object.DestroyImmediate(cue);
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void RopeFire_AnchorsInstantlyAtThePreviewIntercept()
        {
            GameObject go = new GameObject("RopeGun instant fire test");
            GameObject wall = new GameObject("instant fire wall");
            Vector2 testOrigin = new Vector2(10000f, 10000f);
            go.transform.position = testOrigin;
            go.SetActive(false);
            go.AddComponent<Rigidbody2D>();
            RopeGun gun = go.AddComponent<RopeGun>();
            go.SetActive(true);
            InitializeRopeGun(gun);
            wall.layer = 6;
            BoxCollider2D box = wall.AddComponent<BoxCollider2D>();
            box.size = new Vector2(0.02f, 4f);
            try
            {
                // Aim straight at a solid wall 2 units right; the preview resolves the intercept…
                wall.transform.position = testOrigin + new Vector2(2f, 0f);
                SetField(gun, "aimOffset", new Vector2(1.5f, 0f));
                Physics2D.SyncTransforms();
                Invoke(gun, "UpdatePreview");

                // …and the press anchors there the same frame: no projectile, straight to Pulling.
                gun.TryFire();
                Assert.AreEqual("Pulling", GetField<object>(gun, "phase").ToString(),
                    "a green reticle press must anchor instantly — no Flying phase exists anymore");

                GameObject hook = GetField<GameObject>(gun, "hookGo");
                Assert.IsNotNull(hook, "the static hook sprite spawns at the anchor");
                Assert.AreEqual(testOrigin.x + 2f - 0.01f - 0.04f, hook.transform.position.x, 0.05f,
                    "hook sits on the preview intercept, nudged outward by the anchor clearance");

                // A red reticle (nothing intercepts within range) refuses the shot like the ability gate.
                Invoke(gun, "Finish");
                wall.transform.position = testOrigin + new Vector2(8f, 0f);
                Physics2D.SyncTransforms();
                Invoke(gun, "UpdatePreview");
                gun.TryFire();
                Assert.AreEqual("Idle", GetField<object>(gun, "phase").ToString(),
                    "a red reticle press must not anchor anything");
            }
            finally
            {
                GameObject hook = GetField<GameObject>(gun, "hookGo");
                if (hook != null) UnityEngine.Object.DestroyImmediate(hook);
                UnityEngine.Object.DestroyImmediate(wall);
                ShutdownRopeGun(gun);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RopePreview_IgnoresTriggersAndTerrainPastTheRangeBoundary()
        {
            GameObject player = new GameObject("RopeGun preview test");
            GameObject wall = new GameObject("preview wall");
            Vector2 testOrigin = new Vector2(10000f, 10000f);
            player.transform.position = testOrigin;
            player.SetActive(false);
            player.AddComponent<Rigidbody2D>();
            RopeGun gun = player.AddComponent<RopeGun>();
            player.SetActive(true);
            InitializeRopeGun(gun);
            wall.layer = 6;
            BoxCollider2D box = wall.AddComponent<BoxCollider2D>();
            box.size = new Vector2(0.02f, 4f);
            try
            {
                wall.transform.position = testOrigin + new Vector2(2f, 0f);
                box.isTrigger = true;
                Physics2D.SyncTransforms();
                Invoke(gun, "UpdatePreview");
                Assert.AreEqual(GetField<Color>(gun, "missColor"),
                    GetField<SpriteRenderer>(gun, "reticleSprite").color,
                    "a trigger must not make the reticle green");

                box.isTrigger = false;
                Physics2D.SyncTransforms();
                Invoke(gun, "UpdatePreview");
                Assert.AreEqual(GetField<Color>(gun, "hitColor"),
                    GetField<SpriteRenderer>(gun, "reticleSprite").color,
                    "thin solid terrain inside the range must make the reticle green");

                wall.transform.position = testOrigin + new Vector2(4.1f, 0f);
                Physics2D.SyncTransforms();
                Invoke(gun, "UpdatePreview");
                Assert.AreEqual(GetField<Color>(gun, "hitColor"),
                    GetField<SpriteRenderer>(gun, "reticleSprite").color,
                    "the hook radius must allow a legal contact at the range boundary");

                wall.transform.position = testOrigin + new Vector2(4.35f, 0f);
                Physics2D.SyncTransforms();
                Invoke(gun, "UpdatePreview");
                Assert.AreEqual(GetField<Color>(gun, "missColor"),
                    GetField<SpriteRenderer>(gun, "reticleSprite").color,
                    "terrain beyond the range boundary must not make the reticle green");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wall);
                ShutdownRopeGun(gun);
                UnityEngine.Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void BreakableWallPrefab_IsOnTheHookableBreakableLayer()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Item/BreakableWall.prefab");
            Assert.IsNotNull(prefab);
            Assert.AreEqual(11, prefab.layer,
                "direct BreakableWall instances must not inherit the unhookable Default layer");
        }

        [Test]
        public void PatrolMover_StartsFromPlacedPositionAndUsesForwardCurve()
        {
            AnimationCurve forward = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.5f, 0.25f),
                new Keyframe(1f, 1f));
            PatrolMover mover = CreatePatrolMover(
                new Vector2(10f, 3f), new Vector2(-2f, 0f), new Vector2(2f, 0f),
                2f, 0f, 0f, 0f, forward, AnimationCurve.Linear(0f, 0f, 1f, 1f),
                out GameObject go, out Rigidbody2D body);
            try
            {
                AssertVector2(new Vector2(10f, 3f), go.transform.position,
                    "Attach must not snap the placed object to point A");

                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(10.5f, 3f), go.transform.position,
                    "the forward curve must map normalized time to normalized distance");
                AssertVector2(go.transform.position, body.position,
                    "the transform and Rigidbody2D must remain synchronized");

                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(12f, 3f), go.transform.position,
                    "the mover must land exactly on point B");
                AssertVector2(go.transform.position, body.position);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PatrolMover_WaitsAtBothEndsAndUsesIndependentReturnSettings()
        {
            AnimationCurve returnCurve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.5f, 0.75f),
                new Keyframe(1f, 1f));
            PatrolMover mover = CreatePatrolMover(
                Vector2.zero, new Vector2(-2f, 0f), new Vector2(2f, 0f),
                2f, 4f, 0.25f, 0.5f, AnimationCurve.Linear(0f, 0f, 1f, 1f), returnCurve,
                out GameObject go, out _);
            try
            {
                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position);

                AdvancePatrol(mover, 0.4f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position,
                    "the mover must remain at B for the configured stop time");
                AdvancePatrol(mover, 0.1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position);

                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(-1f, 0f), go.transform.position,
                    "the return leg must use its own speed and curve");
                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(-2f, 0f), go.transform.position,
                    "the mover must land exactly on point A");

                AdvancePatrol(mover, 0.2f);
                AssertVector2(new Vector2(-2f, 0f), go.transform.position,
                    "the mover must remain at A for the configured stop time");
                AdvancePatrol(mover, 0.05f);
                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(-1f, 0f), go.transform.position,
                    "after the A stop, the next forward cycle must begin normally");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PatrolMover_LargeStepMatchesSplitStepsAcrossMovementAndWait()
        {
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            PatrolMover largeStepMover = CreatePatrolMover(
                Vector2.zero, new Vector2(-2f, 0f), new Vector2(2f, 0f),
                2f, 4f, 0f, 0.5f, linear, linear,
                out GameObject largeStepGo, out Rigidbody2D largeStepBody);
            PatrolMover splitStepMover = CreatePatrolMover(
                Vector2.zero, new Vector2(-2f, 0f), new Vector2(2f, 0f),
                2f, 4f, 0f, 0.5f, linear, linear,
                out GameObject splitStepGo, out Rigidbody2D splitStepBody);
            try
            {
                AdvancePatrol(largeStepMover, 1.75f);
                for (int i = 0; i < 7; i++) AdvancePatrol(splitStepMover, 0.25f);

                AssertVector2(splitStepGo.transform.position, largeStepGo.transform.position,
                    "unused frame time must continue through endpoint waits and into the next leg");
                AssertVector2(new Vector2(1f, 0f), largeStepGo.transform.position);
                AssertVector2(largeStepGo.transform.position, largeStepBody.position);
                AssertVector2(splitStepGo.transform.position, splitStepBody.position);

                AdvancePatrol(largeStepMover, 0.5f);
                AdvancePatrol(splitStepMover, 0.5f);
                AssertVector2(splitStepGo.transform.position, largeStepGo.transform.position,
                    "large and split steps must leave the state machine in the same state");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(largeStepGo);
                UnityEngine.Object.DestroyImmediate(splitStepGo);
            }
        }

        [Test]
        public void PatrolMover_ZeroReturnSpeedFallsBackToLegacyForwardSpeed()
        {
            PatrolMover mover = CreatePatrolMover(
                Vector2.zero, new Vector2(-2f, 0f), new Vector2(2f, 0f),
                2f, 0f, 0f, 0f,
                AnimationCurve.Linear(0f, 0f, 1f, 1f), AnimationCurve.Linear(0f, 0f, 1f, 1f),
                out GameObject go, out _);
            try
            {
                Assert.AreEqual(2f, GetField<float>(mover, "speed"),
                    "the legacy serialized speed field must remain the forward-speed backing field");
                AdvancePatrol(mover, 1f);
                AdvancePatrol(mover, 1f);
                AssertVector2(Vector2.zero, go.transform.position,
                    "returnSpeed zero must reuse the legacy forward speed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PatrolMover_InvalidSpeedsAndZeroLengthRouteRemainStable()
        {
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            PatrolMover stoppedMover = CreatePatrolMover(
                new Vector2(3f, 4f), new Vector2(-2f, 0f), new Vector2(2f, 0f),
                0f, 5f, 0f, 0f, linear, linear,
                out GameObject stoppedGo, out Rigidbody2D stoppedBody);
            PatrolMover zeroLengthMover = CreatePatrolMover(
                new Vector2(7f, 8f), Vector2.zero, Vector2.zero,
                2f, 2f, 0f, 0f, linear, linear,
                out GameObject zeroLengthGo, out Rigidbody2D zeroLengthBody);
            try
            {
                SetField(stoppedMover, "speed", -2f);
                AdvancePatrol(stoppedMover, 100f);
                AssertVector2(new Vector2(3f, 4f), stoppedGo.transform.position);
                AssertVector2(stoppedGo.transform.position, stoppedBody.position);

                AdvancePatrol(zeroLengthMover, 100f);
                Vector2 stablePosition = zeroLengthGo.transform.position;
                AssertVector2(new Vector2(7f, 8f), stablePosition);
                Assert.IsFalse(float.IsNaN(stablePosition.x) || float.IsNaN(stablePosition.y));
                AssertVector2(stablePosition, zeroLengthBody.position);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stoppedGo);
                UnityEngine.Object.DestroyImmediate(zeroLengthGo);
            }
        }

        [Test]
        public void PatrolMover_Loop_ThreeWaypointsWrapsToFirst()
        {
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            // A return curve that parks at zero for the whole first half: if Loop wrongly shaped
            // the wrap leg with it, the mid-wrap position would sit still at (4,0)
            AnimationCurve parkingReturn = new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.5f, 0f), new Keyframe(1f, 1f));
            PatrolMover mover = CreatePatrolMover(
                Vector2.zero, Vector2.zero, Vector2.zero,
                2f, 4f, 0f, 0f, linear, parkingReturn,
                out GameObject go, out _,
                waypoints: new[] { Vector2.zero, new Vector2(2f, 0f), new Vector2(4f, 0f) },
                mode: PatrolMover.PatrolMode.Loop);
            try
            {
                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position, "first leg: placed → waypoint 1");

                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(4f, 0f), go.transform.position, "second leg: waypoint 1 → waypoint 2");

                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position,
                    "the wrap leg (waypoint 2 → waypoint 0) must travel and use the forward curve, not the return curve");

                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(0f, 0f), go.transform.position, "the wrap must land exactly on waypoint 0");

                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position, "after wrapping, the circle repeats from waypoint 1");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PatrolMover_PingPong_ThreeWaypointsReversesAtBothEnds()
        {
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            PatrolMover mover = CreatePatrolMover(
                Vector2.zero, Vector2.zero, Vector2.zero,
                2f, 4f, 0f, 0f, linear, linear,
                out GameObject go, out _,
                waypoints: new[] { Vector2.zero, new Vector2(2f, 0f), new Vector2(4f, 0f) });
            try
            {
                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position, "first leg: placed → waypoint 1");

                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(4f, 0f), go.transform.position, "second leg reaches the route end");

                // return speed 4: the reversal at the end must switch to the descending direction
                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position,
                    "after the route end the mover must reverse with the return speed");

                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(0f, 0f), go.transform.position, "the reversal at the first waypoint lands exactly");

                AdvancePatrol(mover, 0.25f);
                AssertVector2(new Vector2(1f, 0f), go.transform.position,
                    "after the first waypoint the mover must ascend again with the forward speed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PatrolMover_RandomSpeed_RollsPerLegWithinRange()
        {
            UnityEngine.Random.InitState(12345);
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            PatrolMover mover = CreatePatrolMover(
                Vector2.zero, Vector2.zero, Vector2.zero,
                2f, 2f, 0f, 0f, linear, linear,
                out GameObject go, out _,
                waypoints: new[] { Vector2.zero, new Vector2(2f, 0f), new Vector2(4f, 0f) },
                speedMode: PatrolMover.SpeedMode.RandomRange,
                randomSpeedMin: 1f, randomSpeedMax: 3f);
            try
            {
                float first = GetField<float>(mover, "legSpeed");
                Assert.GreaterOrEqual(first, 1f, "the opening leg's rolled speed must respect the interval floor");
                Assert.LessOrEqual(first, 3f, "the opening leg's rolled speed must respect the interval ceiling");

                // drive several legs; every re-rolled speed must stay within [min, max] and the
                // mover must never leave the route's span
                for (int i = 0; i < 12; i++)
                {
                    AdvancePatrol(mover, 0.2f);
                    float rolled = GetField<float>(mover, "legSpeed");
                    Assert.GreaterOrEqual(rolled, 1f, $"leg {i}: rolled speed below the interval floor");
                    Assert.LessOrEqual(rolled, 3f, $"leg {i}: rolled speed above the interval ceiling");

                    float x = go.transform.position.x;
                    Assert.GreaterOrEqual(x, -0.01f, $"leg {i}: the mover escaped the route span");
                    Assert.LessOrEqual(x, 4.01f, $"leg {i}: the mover escaped the route span");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PatrolMover_MultiWaypoint_PerPointStops()
        {
            AnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            PatrolMover mover = CreatePatrolMover(
                Vector2.zero, Vector2.zero, Vector2.zero,
                2f, 2f, 9f, 9f, linear, linear,      // endpoint fallbacks set absurdly high: any stop observed must come from Stop Times
                out GameObject go, out _,
                waypoints: new[] { Vector2.zero, new Vector2(2f, 0f), new Vector2(4f, 0f) },
                stopTimes: new[] { 0.5f, 0.25f, 1f });
            try
            {
                AdvancePatrol(mover, 1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position, "first leg arrives at waypoint 1");

                AdvancePatrol(mover, 0.1f);
                AssertVector2(new Vector2(2f, 0f), go.transform.position,
                    "the 0.25s per-point stop at waypoint 1 must hold the mover");

                AdvancePatrol(mover, 0.15f + 0.5f);
                AssertVector2(new Vector2(3f, 0f), go.transform.position,
                    "after the per-point stop the next leg departs on schedule (fallback endpoint stops must not apply)");

                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(4f, 0f), go.transform.position, "arrival at the route end");

                AdvancePatrol(mover, 0.5f);
                AssertVector2(new Vector2(4f, 0f), go.transform.position,
                    "the 1s per-point stop at the route end must hold the mover");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AudioService_DestroyedActiveVoiceRepairsCountAndCapacity()
        {
            GameObject go = new GameObject("AudioService recycle test");
            go.SetActive(false);
            AudioService manager = go.AddComponent<AudioService>();
            SetField(manager, "voiceCount", 1);
            InvokeStatic(typeof(AudioService), "ResetStatics");
            go.SetActive(true);
            if (AudioService.Instance != manager) Invoke(manager, "Awake");
            SoundCue cue = ScriptableObject.CreateInstance<SoundCue>();
            AudioClip clip = AudioClip.Create("test", 32, 1, 8000, false);
            cue.clips = new[] { clip };
            cue.cooldown = 0f;
            cue.maxConcurrent = 1;
            try
            {
                AudioService.Play(cue);
                Assert.AreEqual(1, manager.ActiveCountOf(cue));
                AudioSource first = Array.Find(manager.GetComponentsInChildren<AudioSource>(), s => s.clip == clip);
                Assert.IsNotNull(first, "the voice carrying the clip");
                UnityEngine.Object.DestroyImmediate(first.gameObject);

                Invoke(manager, "Update");
                Assert.AreEqual(0, manager.ActiveCountOf(cue), "a destroyed voice gives its count back");
                AudioService.Play(cue);
                Assert.AreEqual(1, manager.ActiveCountOf(cue), "a replacement voice restores the fixed capacity");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cue);
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(go);
                InvokeStatic(typeof(AudioService), "ResetStatics");
            }
        }

        private static void Invoke(object target, string method) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);

        private static void InvokeStatic(Type type, string method) =>
            type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);

        private static void InitializeRopeGun(RopeGun gun)
        {
            Invoke(gun, "Awake");
            Invoke(gun, "OnEnable");
        }

        private static void ShutdownRopeGun(RopeGun gun) => Invoke(gun, "OnDisable");

        private static GameObject CreateCapacityPickup(
            string pickupId,
            int capacityIncrease,
            out InventoryCapacityPart part)
        {
            GameObject go = new GameObject("Inventory capacity pickup test");
            go.SetActive(false);
            CircleCollider2D collider = go.AddComponent<CircleCollider2D>();
            collider.isTrigger = true;
            go.AddComponent<SpriteRenderer>();
            Inkform.Interactable.Interactable node =
                go.AddComponent<Inkform.Interactable.Interactable>();
            part = go.AddComponent<InventoryCapacityPart>();
            SetField(part, "pickupId", pickupId);
            SetField(part, "capacityIncrease", capacityIncrease);
            part.Attach(node);
            return go;
        }

        private static PatrolMover CreatePatrolMover(
            Vector2 placedPosition,
            Vector2 pointA,
            Vector2 pointB,
            float forwardSpeed,
            float returnSpeed,
            float stopTimeAtA,
            float stopTimeAtB,
            AnimationCurve forwardCurve,
            AnimationCurve returnCurve,
            out GameObject go,
            out Rigidbody2D body,
            Vector2[] waypoints = null,
            Inkform.Interactable.Parts.PatrolMover.PatrolMode mode =
                Inkform.Interactable.Parts.PatrolMover.PatrolMode.PingPong,
            Inkform.Interactable.Parts.PatrolMover.SpeedMode speedMode =
                Inkform.Interactable.Parts.PatrolMover.SpeedMode.Fixed,
            float randomSpeedMin = 0f,
            float randomSpeedMax = 0f,
            float[] stopTimes = null)
        {
            go = new GameObject("PatrolMover test");
            go.SetActive(false);
            go.transform.position = placedPosition;
            body = go.AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Kinematic;
            go.AddComponent<BoxCollider2D>();
            PatrolMover mover = go.AddComponent<PatrolMover>();
            SetField(mover, "pointA", pointA);
            SetField(mover, "pointB", pointB);
            SetField(mover, "speed", forwardSpeed);
            SetField(mover, "returnSpeed", returnSpeed);
            SetField(mover, "stopTimeAtA", stopTimeAtA);
            SetField(mover, "stopTimeAtB", stopTimeAtB);
            SetField(mover, "forwardCurve", forwardCurve);
            SetField(mover, "returnCurve", returnCurve);
            if (waypoints != null) SetField(mover, "waypoints", waypoints);
            SetField(mover, "mode", mode);
            SetField(mover, "speedMode", speedMode);
            SetField(mover, "randomSpeedMin", randomSpeedMin);
            SetField(mover, "randomSpeedMax", randomSpeedMax);
            if (stopTimes != null) SetField(mover, "stopTimes", stopTimes);
            Inkform.Interactable.Interactable node =
                go.AddComponent<Inkform.Interactable.Interactable>();
            // EditMode tests do not run the normal player-loop Awake sequence, so attach explicitly.
            mover.Attach(node);
            go.SetActive(true);
            return mover;
        }

        private static void AdvancePatrol(PatrolMover mover, float deltaTime) =>
            mover.GetType().GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(mover, new object[] { deltaTime });

        private static void AdvanceBlastWave(BlastWaveFx wave, float deltaTime) =>
            wave.GetType().GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(wave, new object[] { deltaTime });

        private static void ApplyBlastWave(BlastWaveFx wave, float normalized) =>
            wave.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(wave, new object[] { normalized });

        private static BlastWaveFx[] FindBlastWaves() =>
            UnityEngine.Object.FindObjectsByType<BlastWaveFx>(FindObjectsInactive.Include);

        private static void DestroyAllBlastWaves()
        {
            foreach (BlastWaveFx wave in FindBlastWaves())
                if (wave != null) UnityEngine.Object.DestroyImmediate(wave.gameObject);
        }

        private static void AssertVector2(Vector2 expected, Vector2 actual, string message = null) =>
            Assert.That(Vector2.Distance(expected, actual), Is.LessThanOrEqualTo(0.0001f), message);

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);

        private static T GetField<T>(object target, string name) =>
            (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void AssertSerializedReference(UnityEngine.Object target, string propertyName)
        {
            SerializedProperty property = new SerializedObject(target).FindProperty(propertyName);
            Assert.IsNotNull(property, $"Missing serialized property {propertyName} on {target.GetType().Name}");
            Assert.IsNotNull(property.objectReferenceValue,
                $"Unassigned serialized property {propertyName} on {target.GetType().Name}");
        }

        private static void SetStaticField(Type type, string name, object value) =>
            type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, value);

        private static void SetStaticProperty(Type type, string name, object value) =>
            type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
                .SetValue(null, value);
    }
}
#endif
