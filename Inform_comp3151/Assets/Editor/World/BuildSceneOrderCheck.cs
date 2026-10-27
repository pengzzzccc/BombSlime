using System.Linq;
using Inkform.Level;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;

namespace Inkform.WorldTools
{
    /// <summary>
    /// Refuses a player build whose first enabled scene is not the world's menu scene. A stale
    /// Build Settings list (menu unchecked) still builds fine and boots straight into whatever
    /// room comes first — the GameManager runs in every scene, so nothing looks broken until the
    /// player notices there was no main menu. Sync Build Settings is the fix.
    /// </summary>
    public class BuildSceneOrderCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            WorldDefinition world = WorldSetup.FindWorld();
            if (world == null) return;   // no world asset: nothing to check against

            string menuPath = WorldSetup.ScenePathOf(world.MenuScene);
            if (string.IsNullOrEmpty(menuPath)) return;

            // The build's own list: the active build profile's when it overrides the global one
            BuildProfile profile = BuildProfile.GetActiveBuildProfile();
            EditorBuildSettingsScene[] scenes = profile != null && profile.overrideGlobalScenes
                ? profile.scenes
                : EditorBuildSettings.scenes;
            string first = EditorBuildSettingsScene.GetActiveSceneList(scenes).FirstOrDefault();
            if (first == menuPath) return;

            throw new BuildFailedException(
                $"The first enabled build scene is '{first ?? "(none)"}', not the menu scene '{menuPath}', " +
                "so the build would skip the main menu. Run Tools > Inkform > World > Sync Build Settings.");
        }
    }
}
