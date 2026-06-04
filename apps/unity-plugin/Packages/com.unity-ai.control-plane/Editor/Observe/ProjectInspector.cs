using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ProjectSnapshot
    {
        public string projectPath;
        public string unityVersion;
        public string activeScenePath;
        public string activeBuildTarget;
        public int rootGameObjectCount;
        public string[] rootGameObjectNames;
        public string capturedAtUtc;
    }

    public static class ProjectInspector
    {
        public static ProjectSnapshot InspectActiveProject()
        {
            var activeScene = EditorSceneManager.GetActiveScene();
            var rootGameObjects = activeScene.IsValid() ? activeScene.GetRootGameObjects() : Array.Empty<GameObject>();
            var rootGameObjectNames = new string[rootGameObjects.Length];

            for (var index = 0; index < rootGameObjects.Length; index++)
            {
                rootGameObjectNames[index] = rootGameObjects[index].name;
            }

            return new ProjectSnapshot
            {
                projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                unityVersion = Application.unityVersion,
                activeScenePath = activeScene.path,
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                rootGameObjectCount = rootGameObjects.Length,
                rootGameObjectNames = rootGameObjectNames,
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }
    }
}
