using System;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class SceneInspectRequest
    {
        public SceneInspectInput input = new();
    }

    [Serializable]
    public sealed class SceneInspectInput
    {
        public bool includeComponents = true;
        public int maxDepth = 3;
        public int maxGameObjects = 200;
    }

    [Serializable]
    public sealed class SceneGameObjectInfo
    {
        public string name;
        public string path;
        public bool activeSelf;
        public SceneVector3 position;
        public SceneVector3 rotationEuler;
        public SceneVector3 scale;
        public int childCount;
        public string[] components;
    }

    [Serializable]
    public sealed class SceneVector3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public sealed class SceneInspectReport
    {
        public string scenePath;
        public string sceneName;
        public bool isDirty;
        public bool isLoaded;
        public int rootGameObjectCount;
        public int returnedGameObjectCount;
        public bool truncated;
        public bool truncatedByDepth;
        public bool truncatedByCount;
        public SceneGameObjectInfo[] gameObjects;
        public string capturedAtUtc;
    }

    public static class SceneInspector
    {
        public static SceneInspectReport InspectActiveScene(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new SceneInspectInput();
            var maxDepth = Math.Max(0, Math.Min(input.maxDepth, 10));
            var maxGameObjects = input.maxGameObjects <= 0 ? 200 : Math.Min(input.maxGameObjects, 1000);
            var scene = EditorSceneManager.GetActiveScene();
            var roots = scene.IsValid() ? scene.GetRootGameObjects() : Array.Empty<GameObject>();
            var gameObjects = new List<SceneGameObjectInfo>();
            var truncatedByDepth = false;
            var truncatedByCount = false;

            foreach (var root in roots)
            {
                AddGameObject(root, root.name, 0, maxDepth, maxGameObjects, input.includeComponents, gameObjects, ref truncatedByDepth, ref truncatedByCount);

                if (gameObjects.Count >= maxGameObjects)
                {
                    truncatedByCount = true;
                    break;
                }
            }

            return new SceneInspectReport
            {
                scenePath = scene.path,
                sceneName = scene.name,
                isDirty = scene.isDirty,
                isLoaded = scene.isLoaded,
                rootGameObjectCount = roots.Length,
                returnedGameObjectCount = gameObjects.Count,
                truncated = truncatedByDepth || truncatedByCount,
                truncatedByDepth = truncatedByDepth,
                truncatedByCount = truncatedByCount,
                gameObjects = gameObjects.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void AddGameObject(GameObject gameObject, string path, int depth, int maxDepth, int maxGameObjects, bool includeComponents, List<SceneGameObjectInfo> output, ref bool truncatedByDepth, ref bool truncatedByCount)
        {
            if (output.Count >= maxGameObjects)
            {
                truncatedByCount = true;
                return;
            }

            output.Add(new SceneGameObjectInfo
            {
                name = gameObject.name,
                path = path,
                activeSelf = gameObject.activeSelf,
                position = ToSceneVector3(gameObject.transform.localPosition),
                rotationEuler = ToSceneVector3(gameObject.transform.localEulerAngles),
                scale = ToSceneVector3(gameObject.transform.localScale),
                childCount = gameObject.transform.childCount,
                components = includeComponents ? GetComponentNames(gameObject) : Array.Empty<string>()
            });

            if (depth >= maxDepth)
            {
                if (gameObject.transform.childCount > 0)
                {
                    truncatedByDepth = true;
                }

                return;
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                var child = gameObject.transform.GetChild(index).gameObject;
                AddGameObject(child, $"{path}/{child.name}", depth + 1, maxDepth, maxGameObjects, includeComponents, output, ref truncatedByDepth, ref truncatedByCount);
            }
        }

        private static string[] GetComponentNames(GameObject gameObject)
        {
            var components = gameObject.GetComponents<Component>();
            var names = new string[components.Length];

            for (var index = 0; index < components.Length; index++)
            {
                names[index] = components[index] != null ? components[index].GetType().Name : "MissingComponent";
            }

            return names;
        }

        private static SceneVector3 ToSceneVector3(Vector3 value)
        {
            return new SceneVector3
            {
                x = value.x,
                y = value.y,
                z = value.z
            };
        }

        private static SceneInspectRequest ParseRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new SceneInspectRequest();
            }

            try
            {
                return JsonUtility.FromJson<SceneInspectRequest>(requestBody) ?? new SceneInspectRequest();
            }
            catch
            {
                return new SceneInspectRequest();
            }
        }
    }
}
