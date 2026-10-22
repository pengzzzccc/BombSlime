using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Inkform.Level;
namespace Inkform.WorldTools
{
    /// <summary>
    /// World content checks, run on demand from Tools > Inkform > World. The quick pass reads only
    /// assets (world wiring, room scenes existing and in the build); the deep pass opens every room
    /// scene and cross-checks doors against the arrival checkpoints they name. Findings go to the
    /// console grouped by severity, with a summary dialog — the two Inspector references a door is
    /// made of are cheap to validate exactly because there are only two.
    /// </summary>
    public static class WorldValidator
    {
        private readonly struct Finding
        {
            public readonly string Severity;   // "Error" / "Warning" / "Info"
            public readonly string Message;
            public Finding(string severity, string message) { Severity = severity; Message = message; }
        }

        [MenuItem("Tools/Inkform/World/Validate World (quick)")]
        private static void ValidateQuick() => Run(deep: false);

        [MenuItem("Tools/Inkform/World/Validate World (deep, opens scenes)")]
        private static void ValidateDeep() => Run(deep: true);

        private static void Run(bool deep)
        {
            var findings = new List<Finding>();

            WorldDefinition world = WorldSetup.FindWorld();
            if (world == null)
            {
                EditorUtility.DisplayDialog("World validation", "No WorldDefinition asset found.", "OK");
                return;
            }

            if (!world.MenuScene.IsSet) findings.Add(new Finding("Error", "World: no menu scene assigned"));
            if (world.EntryRoom == null || !world.EntryRoom.IsSet)
                findings.Add(new Finding("Error", "World: no entry room assigned"));
            if (world.EntryRoom != null && world.EntryRoom.IsSet && !RoomListed(world, world.EntryRoom))
                findings.Add(new Finding("Error", "World: the entry room is not in the rooms list"));

            var seenSceneNames = new Dictionary<string, string>();
            foreach (RoomDefinition room in world.Rooms)
            {
                if (room == null) { findings.Add(new Finding("Error", "World: a rooms-list entry is empty")); continue; }
                if (!room.IsSet) { findings.Add(new Finding("Error", $"{room.name}: no scene assigned")); continue; }

                if (seenSceneNames.TryGetValue(room.SceneName, out string other))
                    findings.Add(new Finding("Error", $"{room.name} and {other} both use scene '{room.SceneName}'"));
                else
                    seenSceneNames[room.SceneName] = room.name;

                string path = AssetDatabase.GetAssetPath(room.Scene.SceneAsset);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    findings.Add(new Finding("Error", $"{room.name}: scene file missing at '{room.Scene.ScenePath}'"));
                    continue;
                }
                // A renamed / moved scene keeps its asset reference (GUID), but the serialized path —
                // which is the room's identity (SceneName), what doors load and saves hold — only
                // catches up when the room asset is saved again. Until then the build ships the old name.
                if (path != room.Scene.ScenePath)
                    findings.Add(new Finding("Error",
                        $"{room.name}: stale scene path '{room.Scene.ScenePath}' — the scene is now '{path}'. Re-save the room asset (select it, change nothing, File > Save Project) or fix scenePath"));
                if (SceneUtility.GetBuildIndexByScenePath(path) < 0)
                    findings.Add(new Finding("Error", $"{room.name}: scene '{room.SceneName}' is NOT in the Build Settings — Tools > Inkform > World > Sync Build Settings"));
            }

            if (world.MenuScene.IsSet)   // an unset menu scene is reported above
            {
                string menuPath = AssetDatabase.GetAssetPath(world.MenuScene.SceneAsset);
                if (!string.IsNullOrEmpty(menuPath) && SceneUtility.GetBuildIndexByScenePath(menuPath) != 0)
                    findings.Add(new Finding("Warning", "World: the menu scene is not the first enabled Build Settings entry"));
            }

            if (deep) ValidateDoors(world, findings);
            Report(findings, deep);
        }

        // One pass over every room scene: doors must have a destination and a matching arrival
        // checkpoint; spawn ids must be unique per scene. Door→spawn cross-checking needs the
        // destination scene's checkpoint list, so the first loop collects every scene's ids and the
        // second loop re-opens each scene to check its doors against them.
        private static void ValidateDoors(WorldDefinition world, List<Finding> findings)
        {
            var spawnIdsByScene = new Dictionary<string, HashSet<string>>();

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string originalScene = SceneManager.GetActiveScene().path;

            foreach (RoomDefinition room in world.Rooms)
            {
                if (room == null || !room.IsSet) continue;
                string path = AssetDatabase.GetAssetPath(room.Scene.SceneAsset);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var ids = new HashSet<string>();
                foreach (Checkpoint checkpoint in Object.FindObjectsByType<Checkpoint>(FindObjectsInactive.Include))
                {
                    if (string.IsNullOrEmpty(checkpoint.SpawnId)) continue;
                    if (!ids.Add(checkpoint.SpawnId))
                        findings.Add(new Finding("Error", $"{room.SceneName}: duplicate spawn id '{checkpoint.SpawnId}'"));
                }
                spawnIdsByScene[room.SceneName] = ids;

                foreach (LevelExit exit in Object.FindObjectsByType<LevelExit>(FindObjectsInactive.Include))
                {
                    if (exit.Destination == null)
                    {
                        findings.Add(new Finding("Error", $"{room.SceneName}: door '{exit.name}' has no destination"));
                        continue;
                    }
                    if (!string.IsNullOrEmpty(exit.LegacyExitId))
                        findings.Add(new Finding("Warning", $"{room.SceneName}: door '{exit.name}' still carries unmigrated exit id '{exit.LegacyExitId}'"));
                }
            }

            // Second pass: each door's targetSpawnId must exist in its destination scene
            foreach (RoomDefinition room in world.Rooms)
            {
                if (room == null || !room.IsSet) continue;
                string path = AssetDatabase.GetAssetPath(room.Scene.SceneAsset);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                foreach (LevelExit exit in Object.FindObjectsByType<LevelExit>(FindObjectsInactive.Include))
                {
                    if (exit.Destination == null || string.IsNullOrEmpty(exit.TargetSpawnId)) continue;
                    string target = exit.Destination.SceneName;
                    if (spawnIdsByScene.TryGetValue(target, out HashSet<string> ids) && !ids.Contains(exit.TargetSpawnId))
                        findings.Add(new Finding("Error",
                            $"{room.SceneName}: door '{exit.name}' names spawn '{exit.TargetSpawnId}' which does not exist in '{target}'"));
                }
            }

            if (!string.IsNullOrEmpty(originalScene)) EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
        }

        private static bool RoomListed(WorldDefinition world, RoomDefinition room)
        {
            foreach (RoomDefinition listed in world.Rooms)
                if (listed == room) return true;
            return false;
        }

        private static void Report(List<Finding> findings, bool deep)
        {
            int errors = 0, warnings = 0, infos = 0;
            foreach (Finding finding in findings)
            {
                switch (finding.Severity)
                {
                    case "Error": errors++; Debug.LogError($"[World] {finding.Message}"); break;
                    case "Warning": warnings++; Debug.LogWarning($"[World] {finding.Message}"); break;
                    default: infos++; Debug.Log($"[World] {finding.Message}"); break;
                }
            }

            EditorUtility.DisplayDialog("World validation",
                $"{(deep ? "Deep" : "Quick")} pass complete.\n{errors} error(s), {warnings} warning(s), {infos} info.\n" +
                "See the Console for details.", "OK");
        }
    }
}
