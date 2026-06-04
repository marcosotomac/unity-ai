using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class PrefabListRequest
    {
        public PrefabListInput input = new();
    }

    [Serializable]
    public sealed class PrefabListInput
    {
        public string folder = "Assets";
        public int maxResults = 200;
    }

    [Serializable]
    public sealed class PrefabListItem
    {
        public string guid;
        public string path;
        public string rootName;
        public string[] rootComponents;
    }

    [Serializable]
    public sealed class PrefabListReport
    {
        public string folder;
        public int totalFound;
        public int returned;
        public bool truncated;
        public PrefabListItem[] prefabs;
        public string capturedAtUtc;
    }

    [Serializable]
    public sealed class PrefabInspectRequest
    {
        public PrefabInspectInput input = new();
    }

    [Serializable]
    public sealed class PrefabInspectInput
    {
        public string path;
        public bool includeComponents = true;
        public int maxDepth = 3;
        public int maxGameObjects = 200;
    }

    [Serializable]
    public sealed class PrefabGameObjectInfo
    {
        public string name;
        public string path;
        public bool activeSelf;
        public int childCount;
        public string[] components;
    }

    [Serializable]
    public sealed class PrefabInspectReport
    {
        public string path;
        public bool found;
        public string rootName;
        public int returnedGameObjectCount;
        public bool truncated;
        public bool truncatedByDepth;
        public bool truncatedByCount;
        public PrefabGameObjectInfo[] gameObjects;
        public string capturedAtUtc;
    }

    public static class PrefabObserver
    {
        public static PrefabListReport ListPrefabs(string requestBody)
        {
            var input = ParseListRequest(requestBody).input ?? new PrefabListInput();
            var folder = string.IsNullOrWhiteSpace(input.folder) ? "Assets" : input.folder.Trim();
            var maxResults = input.maxResults <= 0 ? 200 : Math.Min(input.maxResults, 1000);
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            var prefabs = new List<PrefabListItem>();

            foreach (var guid in guids)
            {
                if (prefabs.Count >= maxResults)
                {
                    break;
                }

                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                prefabs.Add(new PrefabListItem
                {
                    guid = guid,
                    path = path,
                    rootName = prefab != null ? prefab.name : string.Empty,
                    rootComponents = prefab != null ? GetComponentNames(prefab) : Array.Empty<string>()
                });
            }

            return new PrefabListReport
            {
                folder = folder,
                totalFound = guids.Length,
                returned = prefabs.Count,
                truncated = guids.Length > prefabs.Count,
                prefabs = prefabs.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static PrefabInspectReport InspectPrefab(string requestBody)
        {
            var input = ParseInspectRequest(requestBody).input ?? new PrefabInspectInput();
            var path = input.path ?? string.Empty;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var gameObjects = new List<PrefabGameObjectInfo>();
            var truncatedByDepth = false;
            var truncatedByCount = false;
            var maxDepth = Math.Max(0, Math.Min(input.maxDepth, 10));
            var maxGameObjects = input.maxGameObjects <= 0 ? 200 : Math.Min(input.maxGameObjects, 1000);

            if (prefab != null)
            {
                AddGameObject(prefab, prefab.name, 0, maxDepth, maxGameObjects, input.includeComponents, gameObjects, ref truncatedByDepth, ref truncatedByCount);
            }

            return new PrefabInspectReport
            {
                path = path,
                found = prefab != null,
                rootName = prefab != null ? prefab.name : string.Empty,
                returnedGameObjectCount = gameObjects.Count,
                truncated = truncatedByDepth || truncatedByCount,
                truncatedByDepth = truncatedByDepth,
                truncatedByCount = truncatedByCount,
                gameObjects = gameObjects.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void AddGameObject(GameObject gameObject, string path, int depth, int maxDepth, int maxGameObjects, bool includeComponents, List<PrefabGameObjectInfo> output, ref bool truncatedByDepth, ref bool truncatedByCount)
        {
            if (output.Count >= maxGameObjects)
            {
                truncatedByCount = true;
                return;
            }

            output.Add(new PrefabGameObjectInfo
            {
                name = gameObject.name,
                path = path,
                activeSelf = gameObject.activeSelf,
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

        private static PrefabListRequest ParseListRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody) ? new PrefabListRequest() : JsonUtility.FromJson<PrefabListRequest>(requestBody) ?? new PrefabListRequest();
            }
            catch
            {
                return new PrefabListRequest();
            }
        }

        private static PrefabInspectRequest ParseInspectRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody) ? new PrefabInspectRequest() : JsonUtility.FromJson<PrefabInspectRequest>(requestBody) ?? new PrefabInspectRequest();
            }
            catch
            {
                return new PrefabInspectRequest();
            }
        }
    }
}
