using System.Collections.Generic;
using System.IO;
using Inkform.Level;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Inkform.WorldTools
{
    /// <summary>
    /// World workflow commands. Migrate From LevelGraph.txt is the one-time bridge from the old
    /// text-graph system: it builds the RoomDefinition/WorldDefinition assets, rewrites every door
    /// (exitId → destination reference + targetSpawnId), converts Spawn_ object names into
    /// Checkpoint.spawnId fields, offers to delete doors that pointed outside the declared topology,
    /// cleans dead Build Settings entries and wires the GameManager prefab. Sync Build Settings is
    /// the standing command: menu scene first, every registered room after it, both enabled.
    /// </summary>
    public static class WorldSetup
    {
        private const string LegacyGraphPath = "Assets/Scenes/LevelGraph.txt";
        private const string WorldDirectory = "Assets/Settings/World";
        private const string RoomsDirectory = WorldDirectory + "/Rooms";
        private const string WorldAssetPath = WorldDirectory + "/World.asset";
        private const string GameManagerPrefabPath = "Assets/Prefabs/Control/GameManager.prefab";
        private const string BuildProfilesDirectory = "Assets/Settings/Build Profiles";

        // ---- Migrate ----

        [MenuItem("Tools/Inkform/World/Migrate From LevelGraph.txt")]
        private static void MigrateFromLevelGraph()
        {
            TextAsset legacyFile = AssetDatabase.LoadAssetAtPath<TextAsset>(LegacyGraphPath);
            if (legacyFile == null)
            {
                EditorUtility.DisplayDialog("World migration", $"{LegacyGraphPath} not found — nothing to migrate.", "OK");
                return;
            }

            LegacyGraph legacy = LegacyGraph.Parse(legacyFile.text);
            if (legacy.rooms.Count == 0)
            {
                EditorUtility.DisplayDialog("World migration", "LevelGraph.txt declares no rooms — nothing to migrate.", "OK");
                return;
            }

            // Resolve every scene up front: a room whose scene file cannot be found is a hard stop,
            // half-migrated assets are worse than no migration
            var roomPaths = new Dictionary<string, string>();
            if (!TryResolvePaths(legacy, roomPaths)) return;

            // Pass 1 (read-only): open each room scene, collect what would change
            var deadDoors = new List<string>();       // "scene : Door_x"
            var deadSpawns = new List<string>();      // "scene : Spawn_x"
            int doorCount = 0;
            int spawnCount = 0;
            string originalScene = SceneManager.GetActiveScene().path;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            foreach ((string room, _) in legacy.rooms)
            {
                EditorSceneManager.OpenScene(roomPaths[room], OpenSceneMode.Single);
                foreach (LevelExit exit in Object.FindObjectsByType<LevelExit>(FindObjectsInactive.Include))
                {
                    string exitId = ReadLegacyExitId(exit);
                    if (string.IsNullOrEmpty(exitId)) continue;   // already migrated on an earlier run
                    doorCount++;
                    if (!legacy.HasEdgeFrom(room, exitId)) deadDoors.Add($"{room} : {exit.name}");
                }
                foreach (Checkpoint checkpoint in Object.FindObjectsByType<Checkpoint>(FindObjectsInactive.Include))
                {
                    if (!checkpoint.name.StartsWith("Spawn_")) continue;
                    spawnCount++;
                    if (!legacy.IsRoom(checkpoint.name.Substring("Spawn_".Length))) deadSpawns.Add($"{room} : {checkpoint.name}");
                }
            }

            string deadList = "";
            if (deadDoors.Count + deadSpawns.Count > 0)
                deadList = $"\n\nDelete {deadDoors.Count + deadSpawns.Count} leftover item(s) from the old topology:\n" +
                    string.Join("\n", deadDoors) + (deadSpawns.Count > 0 ? "\n" + string.Join("\n", deadSpawns) : "");

            bool confirmed = EditorUtility.DisplayDialog("World migration",
                $"Creates {legacy.rooms.Count} RoomDefinition asset(s) + World.asset,\n" +
                $"rewires {doorCount} door(s) and {spawnCount} Spawn_ checkpoint(s),\n" +
                $"syncs Build Settings and wires the GameManager prefab." + deadList,
                "Migrate", "Cancel");
            if (!confirmed)
            {
                if (!string.IsNullOrEmpty(originalScene)) EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
                return;
            }

            // Assets first, so doors can reference them; roomAssets is built here because pass 2
            // (the scene writes below) needs the same dictionary
            var roomAssets = new Dictionary<string, RoomDefinition>();
            AssetDatabase.StartAssetEditing();
            try
            {
                EnsureFolder(WorldDirectory);
                EnsureFolder(RoomsDirectory);

                foreach ((string room, string display) in legacy.rooms)
                    roomAssets[room] = CreateOrUpdateRoom(room, display, roomPaths[room]);

                WorldDefinition world = CreateOrUpdateWorld(legacy, roomAssets);
                AssetDatabase.SaveAssets();
                WireGameManager(world);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();

            // Pass 2 (writes): rewire doors and spawns, delete leftovers
            foreach ((string room, _) in legacy.rooms)
            {
                Scene scene = EditorSceneManager.OpenScene(roomPaths[room], OpenSceneMode.Single);
                bool dirty = false;

                foreach (LevelExit exit in Object.FindObjectsByType<LevelExit>(FindObjectsInactive.Include))
                {
                    string exitId = ReadLegacyExitId(exit);
                    if (string.IsNullOrEmpty(exitId)) continue;   // already migrated on an earlier run
                    string target = legacy.TargetOf(room, exitId);
                    if (target == null || !roomAssets.TryGetValue(target, out RoomDefinition destination))
                    {
                        Object.DestroyImmediate(exit.gameObject);   // a door to nowhere outside the topology
                        dirty = true;
                        continue;
                    }

                    var so = new SerializedObject(exit);
                    so.FindProperty("destination").objectReferenceValue = destination;
                    // The old Spawn_<source scene> convention, made explicit: arrive beside the door
                    // this one pairs with, in the destination room
                    so.FindProperty("targetSpawnId").stringValue = room;
                    so.FindProperty("legacyExitId").stringValue = "";
                    so.ApplyModifiedPropertiesWithoutUndo();
                    dirty = true;
                }

                foreach (Checkpoint checkpoint in Object.FindObjectsByType<Checkpoint>(FindObjectsInactive.Include))
                {
                    if (!checkpoint.name.StartsWith("Spawn_")) continue;
                    string source = checkpoint.name.Substring("Spawn_".Length);
                    if (!legacy.IsRoom(source))
                    {
                        Object.DestroyImmediate(checkpoint.gameObject);   // arrival point for a room that no longer exists
                        dirty = true;
                        continue;
                    }

                    var so = new SerializedObject(checkpoint);
                    so.FindProperty("spawnId").stringValue = source;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    dirty = true;
                }

                if (dirty) EditorSceneManager.SaveScene(scene);
            }

            SyncBuildSettings();

            if (!string.IsNullOrEmpty(originalScene)) EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
            Debug.Log($"World migration complete: {legacy.rooms.Count} rooms, Build Settings synced. You can delete {LegacyGraphPath} now.");
        }

        private static string ReadLegacyExitId(LevelExit exit)
        {
            var so = new SerializedObject(exit);
            SerializedProperty property = so.FindProperty("legacyExitId");
            return property != null ? property.stringValue : "";
        }

        // ---- Missing doors & spawns ----

        /// <summary>Creates the doors and arrival checkpoints the existing doors imply but the
        /// scenes lack — the reverse door for every one-way connection (except into the end
        /// room, a credits card), and the arrival checkpoint every door's targetSpawnId names,
        /// the new reverse doors' included, in one run. New doors land in a row near the scene
        /// origin (the old fixer's convention): the tool knows the topology, not the level
        /// geometry, so placing them is the level editor's half of the job. A new arrival
        /// checkpoint is parented beside the door it pairs with, so it follows when the door is
        /// dragged into place.</summary>
        [MenuItem("Tools/Inkform/World/Create Missing Doors & Spawns")]
        private static void CreateMissingDoorsAndSpawns()
        {
            WorldDefinition world = FindWorld();
            if (world == null)
            {
                EditorUtility.DisplayDialog("Missing doors & spawns",
                    "No WorldDefinition asset found.", "OK");
                return;
            }

            var roomBySceneName = new Dictionary<string, RoomDefinition>();
            foreach (RoomDefinition room in world.Rooms)
                if (room != null && room.IsSet) roomBySceneName[room.SceneName] = room;
            if (roomBySceneName.Count == 0)
            {
                EditorUtility.DisplayDialog("Missing doors & spawns", "The world has no rooms.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string originalScene = SceneManager.GetActiveScene().path;

            // Pass 1: read every room scene once — doors as (source room → destination room) pairs,
            // plus each scene's arrival spawn ids
            var doorTargets = new Dictionary<string, List<string>>();       // room → destination scene names
            var doorSpawnIds = new Dictionary<string, List<string>>();      // room → targetSpawnId per door (same order as doorTargets)
            var spawnIds = new Dictionary<string, HashSet<string>>();       // room → existing Checkpoint.spawnIds
            foreach (string sceneName in roomBySceneName.Keys)
            {
                EditorSceneManager.OpenScene(AssetDatabase.GetAssetPath(roomBySceneName[sceneName].Scene.SceneAsset), OpenSceneMode.Single);
                var targets = new List<string>();
                var spawnRefs = new List<string>();
                foreach (LevelExit exit in Object.FindObjectsByType<LevelExit>(FindObjectsInactive.Include))
                {
                    if (exit.Destination == null || !roomBySceneName.ContainsKey(exit.Destination.SceneName)) continue;
                    targets.Add(exit.Destination.SceneName);
                    spawnRefs.Add(exit.TargetSpawnId);
                }
                doorTargets[sceneName] = targets;
                doorSpawnIds[sceneName] = spawnRefs;

                var ids = new HashSet<string>();
                foreach (Checkpoint checkpoint in Object.FindObjectsByType<Checkpoint>(FindObjectsInactive.Include))
                    if (!string.IsNullOrEmpty(checkpoint.SpawnId)) ids.Add(checkpoint.SpawnId);
                spawnIds[sceneName] = ids;
            }

            // Compute what is missing: a reverse door in B for every A→B with no B→A, and the
            // arrival checkpoint each door's targetSpawnId names in its destination
            var doorsToCreate = new Dictionary<string, List<(string dest, string spawnId)>>();
            var spawnsToCreate = new Dictionary<string, List<string>>();
            string endScene = world.EndRoom != null ? world.EndRoom.SceneName : null;
            foreach (string source in doorTargets.Keys)
            {
                for (int i = 0; i < doorTargets[source].Count; i++)
                {
                    string dest = doorTargets[source][i];

                    // Reverse door: arrive in `source`... created in `dest`, pointing back at
                    // `source`, arriving at the spawn named after `dest` (the pairing convention).
                    // The end room is a credits card, not a place to walk back out of: a door
                    // into it never gets a reverse.
                    bool hasReverse = doorTargets.TryGetValue(dest, out List<string> back)
                        && back.Contains(source);
                    if (!hasReverse && dest != endScene)
                    {
                        AddCreation(doorsToCreate, dest, (source, dest));
                        // The new reverse door names the spawn `dest` in `source`: plan that
                        // arrival checkpoint in this same run, not on a second one
                        AddSpawnIfMissing(spawnsToCreate, spawnIds, source, dest);
                    }

                    AddSpawnIfMissing(spawnsToCreate, spawnIds, dest, doorSpawnIds[source][i]);
                }
            }

            int doorCount = 0, spawnCount = 0;
            foreach (List<(string, string)> list in doorsToCreate.Values) doorCount += list.Count;
            foreach (List<string> list in spawnsToCreate.Values) spawnCount += list.Count;
            if (doorCount + spawnCount == 0)
            {
                if (!string.IsNullOrEmpty(originalScene)) EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
                EditorUtility.DisplayDialog("Missing doors & spawns", "Nothing missing — every door has its reverse and every named arrival checkpoint exists.", "OK");
                return;
            }

            var summary = new System.Text.StringBuilder();
            foreach (string room in doorsToCreate.Keys)
                foreach ((string dest, _) in doorsToCreate[room])
                    summary.AppendLine($"{room}: Door_{dest}");
            foreach (string room in spawnsToCreate.Keys)
                foreach (string spawnId in spawnsToCreate[room])
                    summary.AppendLine($"{room}: Spawn_{spawnId}");

            if (!EditorUtility.DisplayDialog("Missing doors & spawns",
                $"Create {doorCount} door(s) and {spawnCount} spawn checkpoint(s):\n\n{summary}\n" +
                "New objects are placed in a row near the scene origin —\ndrag each one to its exit / beside its door afterwards.",
                "Create", "Cancel"))
            {
                if (!string.IsNullOrEmpty(originalScene)) EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
                return;
            }

            // Pass 2: reopen each affected scene and build the objects
            foreach (string sceneName in roomBySceneName.Keys)
            {
                bool doors = doorsToCreate.TryGetValue(sceneName, out List<(string dest, string spawnId)> newDoors);
                bool spawns = spawnsToCreate.TryGetValue(sceneName, out List<string> newSpawns);
                if ((!doors || newDoors.Count == 0) && (!spawns || newSpawns.Count == 0)) continue;

                Scene scene = EditorSceneManager.OpenScene(
                    AssetDatabase.GetAssetPath(roomBySceneName[sceneName].Scene.SceneAsset), OpenSceneMode.Single);
                int slot = 0;
                if (doors)
                    foreach ((string dest, string spawnId) in newDoors)
                        CreateDoor($"Door_{dest}", roomBySceneName[dest], spawnId, slot++);
                if (spawns)
                    foreach (string spawnId in newSpawns)
                        CreateSpawnCheckpoint($"Spawn_{spawnId}", spawnId, slot++);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            if (!string.IsNullOrEmpty(originalScene)) EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
            Debug.Log($"World: created {doorCount} door(s) and {spawnCount} spawn checkpoint(s) near the scene origin — drag them into position.");
        }

        // A checkpoint with this spawn id is wanted in `room`: plan it unless the room already
        // has one (or it is already planned). An empty id means the start point — nothing to make.
        private static void AddSpawnIfMissing(Dictionary<string, List<string>> spawnsToCreate,
            Dictionary<string, HashSet<string>> spawnIds, string room, string spawnId)
        {
            if (string.IsNullOrEmpty(spawnId)) return;
            if (!spawnIds.TryGetValue(room, out HashSet<string> existing) || existing.Contains(spawnId)) return;

            if (!spawnsToCreate.TryGetValue(room, out List<string> list))
            {
                list = new List<string>();
                spawnsToCreate[room] = list;
            }
            if (!list.Contains(spawnId)) list.Add(spawnId);
        }

        private static void AddCreation(Dictionary<string, List<(string dest, string spawnId)>> map, string room, (string dest, string spawnId) entry)
        {
            if (!map.TryGetValue(room, out List<(string dest, string spawnId)> list))
            {
                list = new List<(string dest, string spawnId)>();
                map[room] = list;
            }
            if (!list.Exists(x => x.dest == entry.dest)) list.Add(entry);
        }

        // Same trigger shape and naming the level-graph fixer used to build doors with; the gizmo
        // label shows the destination, so an unplaced door is easy to spot
        private static void CreateDoor(string doorName, RoomDefinition destination, string targetSpawnId, int slot)
        {
            GameObject go = new GameObject(doorName);
            go.transform.position = new Vector3(slot * 3f, 0f, 0f);
            BoxCollider2D collider = go.AddComponent<BoxCollider2D>();
            collider.isTrigger = true;
            collider.size = new Vector2(1.2f, 2f);

            LevelExit exit = go.AddComponent<LevelExit>();
            var so = new SerializedObject(exit);
            so.FindProperty("destination").objectReferenceValue = destination;
            so.FindProperty("targetSpawnId").stringValue = targetSpawnId;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateSpawnCheckpoint(string objectName, string spawnId, int slot)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/env/Checkpoint.prefab");
            if (prefab == null)
            {
                Debug.LogError("World: Assets/Prefabs/env/Checkpoint.prefab not found — cannot create arrival checkpoints.");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = objectName;
            instance.transform.position = new Vector3(slot * 3f, 0f, 0f);

            // The pairing convention: Spawn_X sits beside the door to X — the way back to where
            // the player came from. Parent it there (just clear of the door's 1.2-wide trigger,
            // so arriving does not walk straight back out) and it moves when the door is dragged
            // into place. Flip the offset to the room's inner side if needed.
            foreach (LevelExit exit in Object.FindObjectsByType<LevelExit>(FindObjectsInactive.Include))
            {
                if (exit.Destination == null || exit.Destination.SceneName != spawnId) continue;
                instance.transform.SetParent(exit.transform, false);
                instance.transform.localPosition = new Vector3(1.5f, 0f, 0f);
                break;
            }

            var so = new SerializedObject(instance.GetComponent<Checkpoint>());
            so.FindProperty("spawnId").stringValue = spawnId;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---- Build settings ----

        [MenuItem("Tools/Inkform/World/Sync Build Settings")]
        public static void SyncBuildSettings()
        {
            WorldDefinition world = FindWorld();
            if (world == null)
            {
                EditorUtility.DisplayDialog("Sync Build Settings",
                    "No WorldDefinition asset found. Run the migration or create one via Create > Inkform > World Definition.", "OK");
                return;
            }

            var result = new List<EditorBuildSettingsScene>();
            string menuPath = ScenePathOf(world.MenuScene);
            if (!string.IsNullOrEmpty(menuPath)) result.Add(new EditorBuildSettingsScene(menuPath, true));

            foreach (RoomDefinition room in world.Rooms)
            {
                string path = room != null ? ScenePathOf(room.Scene) : null;
                if (string.IsNullOrEmpty(path)) continue;
                result.Add(new EditorBuildSettingsScene(path, true));
            }

            // Keep unrelated entries that still exist on disk (test scenes), preserving their enabled
            // flag; entries whose file is gone are dead weight and dropped
            foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
            {
                if (string.IsNullOrEmpty(existing.path) || !File.Exists(existing.path)) continue;
                if (result.Exists(x => x.path == existing.path)) continue;
                result.Add(existing);
            }

            EditorBuildSettings.scenes = result.ToArray();
            int profiles = SyncBuildProfiles(world);
            Debug.Log($"World: Build Settings synced — menu first, {world.Rooms.Count} room(s) enabled, " +
                      $"{EditorBuildSettings.scenes.Length} entries total ({profiles} build-profile change(s)).");
        }

        // Every build profile that overrides the global scene list gets the same shape as Build
        // Settings: menu first, every room after it, all enabled, then its other live entries.
        // Goes through the public BuildProfile API — the serialized m_guid is a GUID struct, not a
        // string, so writing it through SerializedProperty.stringValue throws
        private static int SyncBuildProfiles(WorldDefinition world)
        {
            if (!AssetDatabase.IsValidFolder(BuildProfilesDirectory)) return 0;

            var worldScenes = new List<string>();
            string menuPath = ScenePathOf(world.MenuScene);
            if (!string.IsNullOrEmpty(menuPath)) worldScenes.Add(menuPath);
            foreach (RoomDefinition room in world.Rooms)
            {
                string path = room != null ? ScenePathOf(room.Scene) : null;
                if (!string.IsNullOrEmpty(path) && !worldScenes.Contains(path)) worldScenes.Add(path);
            }

            int changed = 0;
            foreach (string file in Directory.GetFiles(BuildProfilesDirectory, "*.asset"))
            {
                var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(file.Replace('\\', '/'));
                if (profile == null || !profile.overrideGlobalScenes) continue;

                var result = new List<EditorBuildSettingsScene>();
                foreach (string path in worldScenes) result.Add(new EditorBuildSettingsScene(path, true));
                foreach (EditorBuildSettingsScene existing in profile.scenes)
                {
                    if (string.IsNullOrEmpty(existing.path) || !File.Exists(existing.path)) continue;
                    if (result.Exists(x => x.path == existing.path)) continue;
                    result.Add(existing);
                }

                if (SameScenes(profile.scenes, result)) continue;
                profile.scenes = result.ToArray();
                EditorUtility.SetDirty(profile);
                changed++;
            }
            AssetDatabase.SaveAssets();
            return changed;
        }

        private static bool SameScenes(EditorBuildSettingsScene[] a, List<EditorBuildSettingsScene> b)
        {
            if (a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].path != b[i].path || a[i].enabled != b[i].enabled) return false;
            return true;
        }

        // ---- Helpers ----

        public static WorldDefinition FindWorld()
        {
            string[] guids = AssetDatabase.FindAssets("t:WorldDefinition");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<WorldDefinition>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        internal static string ScenePathOf(SceneReference scene)
        {
            if (scene == null || !scene.IsSet) return null;
            if (scene.SceneAsset != null) return AssetDatabase.GetAssetPath(scene.SceneAsset);
            return File.Exists(scene.ScenePath) ? scene.ScenePath : null;
        }

        private static bool TryResolvePaths(LegacyGraph legacy, Dictionary<string, string> paths)
        {
            string menuPath = ScenePathFor(legacy.menu);
            if (string.IsNullOrEmpty(menuPath))
            {
                EditorUtility.DisplayDialog("World migration", $"Menu scene '{legacy.menu}' not found — aborting.", "OK");
                return false;
            }

            foreach ((string room, _) in legacy.rooms)
            {
                string path = ScenePathFor(room);
                if (string.IsNullOrEmpty(path))
                {
                    EditorUtility.DisplayDialog("World migration", $"Scene for room '{room}' not found — aborting.", "OK");
                    return false;
                }
                paths[room] = path;
            }
            return true;
        }

        // Scene names are unique in this project (the validator can check it); the name baked into
        // the FindAssets query narrows the search, the file-name comparison makes it exact
        private static string ScenePathFor(string sceneName)
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:Scene {sceneName}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == sceneName) return path;
            }
            return null;
        }

        private static RoomDefinition CreateOrUpdateRoom(string room, string display, string scenePath)
        {
            string assetPath = $"{RoomsDirectory}/{room}.asset";
            RoomDefinition asset = AssetDatabase.LoadAssetAtPath<RoomDefinition>(assetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<RoomDefinition>();
                AssetDatabase.CreateAsset(asset, assetPath);
            }

            var so = new SerializedObject(asset);
            so.FindProperty("scene").FindPropertyRelative("sceneAsset").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            so.FindProperty("scene").FindPropertyRelative("scenePath").stringValue = scenePath;
            so.FindProperty("displayName").stringValue = display;
            so.ApplyModifiedPropertiesWithoutUndo();
            return asset;
        }

        private static WorldDefinition CreateOrUpdateWorld(LegacyGraph legacy, Dictionary<string, RoomDefinition> roomAssets)
        {
            WorldDefinition world = FindWorld();
            if (world == null)
            {
                world = ScriptableObject.CreateInstance<WorldDefinition>();
                AssetDatabase.CreateAsset(world, WorldAssetPath);
            }

            var so = new SerializedObject(world);
            so.FindProperty("menuScene").FindPropertyRelative("sceneAsset").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePathFor(legacy.menu));
            so.FindProperty("entryRoom").objectReferenceValue =
                legacy.entry != null && roomAssets.TryGetValue(legacy.entry, out RoomDefinition entry) ? entry : null;

            SerializedProperty rooms = so.FindProperty("rooms");
            rooms.arraySize = legacy.rooms.Count;
            int index = 0;
            foreach ((string room, _) in legacy.rooms)
                rooms.GetArrayElementAtIndex(index++).objectReferenceValue = roomAssets[room];
            so.ApplyModifiedPropertiesWithoutUndo();
            return world;
        }

        private static void WireGameManager(WorldDefinition world)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(GameManagerPrefabPath);
            try
            {
                SceneDirector director = root.GetComponent<SceneDirector>();
                if (director == null)
                {
                    Debug.LogError($"World: {GameManagerPrefabPath} has no SceneDirector — wire the world by hand.");
                    return;
                }

                var serialized = new SerializedObject(director);
                SerializedProperty property = serialized.FindProperty("world");
                if (property == null || property.objectReferenceValue == world) return;
                property.objectReferenceValue = world;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, GameManagerPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void EnsureFolder(string path)
        {
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || AssetDatabase.IsValidFolder(path)) return;
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ---- Legacy text-graph parsing (self-contained on purpose: outlives LevelGraph.cs) ----

        /// <summary>The four line types the old LevelGraph.txt had, with display names in quotes and
        /// the editor-only @x,y / door N annotations ignored. Behaviour mirrors the old runtime
        /// reader, including the "&lt;-&gt; expands to both directions, reverse edge takes the source
        /// room's name as its exit id" rule, because that is what the doors in the scenes encode.</summary>
        private sealed class LegacyGraph
        {
            public string menu = "MainMenu";
            public string entry = "";
            public readonly List<(string name, string display)> rooms = new List<(string, string)>();
            private readonly List<(string from, string exitId, string to)> edges = new List<(string, string, string)>();

            public bool IsRoom(string name)
            {
                foreach ((string room, _) in rooms)
                    if (room == name) return true;
                return false;
            }

            public bool HasEdgeFrom(string from, string exitId) => TargetOf(from, exitId) != null;

            public string TargetOf(string from, string exitId)
            {
                foreach ((string edgeFrom, string id, string to) in edges)
                    if (edgeFrom == from && id == exitId) return to;   // first match wins, like the old reader
                return null;
            }

            public static LegacyGraph Parse(string text)
            {
                var graph = new LegacyGraph();
                foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    List<string> tokens = Tokenize(StripComment(raw).Trim());
                    if (tokens.Count == 0) continue;

                    switch (tokens[0])
                    {
                        case "menu" when tokens.Count >= 2:
                            graph.menu = Join(tokens, 1, tokens.Count);
                            break;
                        case "entry" when tokens.Count >= 2:
                            graph.entry = Join(tokens, 1, tokens.Count);
                            break;
                        case "room" when tokens.Count >= 2:
                            ParseRoom(graph, tokens);
                            break;
                        case "link" when tokens.Count >= 3:
                            ParseLink(graph, tokens);
                            break;
                    }
                }
                return graph;
            }

            // room <name> [@x,y] ["display"] [door N] — names are unquoted and may contain spaces
            // ("Mine Cave 1"), so the name is every token up to the first editor annotation
            private static void ParseRoom(LegacyGraph graph, List<string> tokens)
            {
                int end = tokens.Count;
                for (int t = 1; t < tokens.Count; t++)
                {
                    if (tokens[t].StartsWith("@") || tokens[t] == "door") { end = t; break; }
                }
                if (end <= 1) return;

                string name = Join(tokens, 1, end);
                string display = "";
                for (int t = end; t < tokens.Count; t++)
                {
                    if (tokens[t].StartsWith("@") || tokens[t].StartsWith("id:")) continue;
                    if (tokens[t] == "door") { t++; continue; }     // skip the count after "door"
                    display = Join(tokens, t, tokens.Count);
                    break;
                }
                graph.rooms.Add((name, display));
            }

            // link <from> (<-> | ->) <to> [id:<exitId>] — both sides are space-joined names split by
            // the arrow token; a two-way link expands to both directions, reverse edge taking the
            // source room's name as its exit id
            private static void ParseLink(LegacyGraph graph, List<string> tokens)
            {
                int arrow = -1;
                for (int t = 1; t < tokens.Count; t++)
                {
                    if (tokens[t] == "<->" || tokens[t] == "->") { arrow = t; break; }
                }
                if (arrow < 1) return;

                string from = Join(tokens, 1, arrow);
                int toEnd = tokens.Count;
                for (int t = arrow + 1; t < tokens.Count; t++)
                {
                    if (tokens[t].StartsWith("id:")) { toEnd = t; break; }
                }
                if (arrow + 1 >= toEnd) return;
                string to = Join(tokens, arrow + 1, toEnd);

                string exitId = "";
                for (int t = toEnd; t < tokens.Count; t++)
                    if (tokens[t].StartsWith("id:") && tokens[t].Length > 3) exitId = tokens[t].Substring(3);

                if (tokens[arrow] == "<->")
                {
                    graph.edges.Add((from, to, to));
                    graph.edges.Add((to, from, from));
                }
                else
                {
                    graph.edges.Add((from, string.IsNullOrEmpty(exitId) ? to : exitId, to));
                }
            }

            private static string Join(List<string> tokens, int start, int end)
            {
                var joined = new System.Text.StringBuilder();
                for (int t = start; t < end; t++)
                {
                    if (joined.Length > 0) joined.Append(' ');
                    joined.Append(tokens[t]);
                }
                return joined.ToString();
            }

            private static string StripComment(string line)
            {
                bool inQuotes = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c == '"') inQuotes = !inQuotes;
                    else if (c == '#' && !inQuotes) return line.Substring(0, i);
                }
                return line;
            }

            private static List<string> Tokenize(string line)
            {
                var tokens = new List<string>();
                var current = new System.Text.StringBuilder();
                bool inQuotes = false, hasToken = false;
                foreach (char c in line)
                {
                    if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
                    if (!inQuotes && char.IsWhiteSpace(c))
                    {
                        if (hasToken) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
                        continue;
                    }
                    current.Append(c);
                    hasToken = true;
                }
                if (hasToken) tokens.Add(current.ToString());
                return tokens;
            }
        }
    }
}
