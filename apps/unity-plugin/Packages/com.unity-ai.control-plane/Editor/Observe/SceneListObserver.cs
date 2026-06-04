using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class SceneListItem
    {
        public string guid;
        public string path;
        public bool inBuildSettings;
        public bool enabledInBuildSettings;
    }

    [Serializable]
    public sealed class SceneListReport
    {
        public int totalFound;
        public int buildSettingsCount;
        public int returned;
        public bool truncated;
        public SceneListItem[] scenes;
        public string capturedAtUtc;
    }

    public static class SceneListObserver
    {
        private const int MaxScenes = 500;

        public static SceneListReport ListScenes()
        {
            var buildScenes = EditorBuildSettings.scenes;
            var buildSceneLookup = new Dictionary<string, bool>();

            foreach (var buildScene in buildScenes)
            {
                buildSceneLookup[buildScene.path] = buildScene.enabled;
            }

            var sceneGuids = AssetDatabase.FindAssets("t:Scene");
            var scenes = new List<SceneListItem>();

            foreach (var guid in sceneGuids)
            {
                if (scenes.Count >= MaxScenes)
                {
                    break;
                }

                var path = AssetDatabase.GUIDToAssetPath(guid);
                var inBuildSettings = buildSceneLookup.ContainsKey(path);

                scenes.Add(new SceneListItem
                {
                    guid = guid,
                    path = path,
                    inBuildSettings = inBuildSettings,
                    enabledInBuildSettings = inBuildSettings && buildSceneLookup[path]
                });
            }

            return new SceneListReport
            {
                totalFound = scenes.Count,
                buildSettingsCount = buildScenes.Length,
                returned = scenes.Count,
                truncated = sceneGuids.Length > scenes.Count,
                scenes = scenes.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }
    }
}
