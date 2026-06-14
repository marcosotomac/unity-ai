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
        public SceneInspectFilterInput filter;
    }

    [Serializable]
    public sealed class SceneInspectFilterInput
    {
        public string nameContains;
        public string pathPrefix;
        public string componentType;
        public string activeState = "any";
        public SceneRadiusFilterInput withinRadius;
    }

    [Serializable]
    public sealed class SceneRadiusFilterInput
    {
        public string centerPath;
        public SceneVector3 center;
        public float radius;
    }

    [Serializable]
    public sealed class SceneGameObjectInfo
    {
        public string name;
        public string path;
        public bool activeSelf;
        public SceneVector3 position;
        public SceneVector3 worldPosition;
        public SceneVector3 rotationEuler;
        public SceneVector3 scale;
        public float distanceFromFilterCenter;
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
        public int visitedGameObjectCount;
        public int matchedGameObjectCount;
        public int returnedGameObjectCount;
        public bool filtered;
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
            var visitedGameObjectCount = 0;
            var matchedGameObjectCount = 0;
            var filter = NormalizeFilter(input.filter);
            var radiusCenter = ResolveRadiusCenter(filter);

            foreach (var root in roots)
            {
                AddGameObject(
                    root,
                    root.name,
                    0,
                    maxDepth,
                    maxGameObjects,
                    input.includeComponents,
                    filter,
                    radiusCenter,
                    gameObjects,
                    ref visitedGameObjectCount,
                    ref matchedGameObjectCount,
                    ref truncatedByDepth);
            }

            var truncatedByCount = matchedGameObjectCount > gameObjects.Count;
            return new SceneInspectReport
            {
                scenePath = scene.path,
                sceneName = scene.name,
                isDirty = scene.isDirty,
                isLoaded = scene.isLoaded,
                rootGameObjectCount = roots.Length,
                visitedGameObjectCount = visitedGameObjectCount,
                matchedGameObjectCount = matchedGameObjectCount,
                returnedGameObjectCount = gameObjects.Count,
                filtered = filter != null,
                truncated = truncatedByDepth || truncatedByCount,
                truncatedByDepth = truncatedByDepth,
                truncatedByCount = truncatedByCount,
                gameObjects = gameObjects.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void AddGameObject(
            GameObject gameObject,
            string path,
            int depth,
            int maxDepth,
            int maxGameObjects,
            bool includeComponents,
            SceneInspectFilterInput filter,
            Vector3? radiusCenter,
            List<SceneGameObjectInfo> output,
            ref int visitedGameObjectCount,
            ref int matchedGameObjectCount,
            ref bool truncatedByDepth)
        {
            visitedGameObjectCount++;
            var distanceFromFilterCenter = radiusCenter.HasValue
                ? Vector3.Distance(gameObject.transform.position, radiusCenter.Value)
                : -1f;

            if (MatchesFilter(gameObject, path, filter, distanceFromFilterCenter))
            {
                matchedGameObjectCount++;
                if (output.Count < maxGameObjects)
                {
                    output.Add(new SceneGameObjectInfo
                    {
                        name = gameObject.name,
                        path = path,
                        activeSelf = gameObject.activeSelf,
                        position = ToSceneVector3(gameObject.transform.localPosition),
                        worldPosition = ToSceneVector3(gameObject.transform.position),
                        rotationEuler = ToSceneVector3(gameObject.transform.localEulerAngles),
                        scale = ToSceneVector3(gameObject.transform.localScale),
                        distanceFromFilterCenter = distanceFromFilterCenter,
                        childCount = gameObject.transform.childCount,
                        components = includeComponents ? GetComponentNames(gameObject) : Array.Empty<string>()
                    });
                }
            }

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
                AddGameObject(
                    child,
                    $"{path}/{child.name}",
                    depth + 1,
                    maxDepth,
                    maxGameObjects,
                    includeComponents,
                    filter,
                    radiusCenter,
                    output,
                    ref visitedGameObjectCount,
                    ref matchedGameObjectCount,
                    ref truncatedByDepth);
            }
        }

        private static SceneInspectFilterInput NormalizeFilter(SceneInspectFilterInput filter)
        {
            if (filter == null)
            {
                return null;
            }

            var hasName = !string.IsNullOrWhiteSpace(filter.nameContains);
            var hasPath = !string.IsNullOrWhiteSpace(filter.pathPrefix);
            var hasComponent = !string.IsNullOrWhiteSpace(filter.componentType);
            var activeState = (filter.activeState ?? "any").Trim().ToLowerInvariant();
            var hasActiveState = activeState == "active" || activeState == "inactive";
            filter.activeState = hasActiveState ? activeState : "any";

            return hasName || hasPath || hasComponent || hasActiveState || filter.withinRadius != null
                ? filter
                : null;
        }

        private static Vector3? ResolveRadiusCenter(SceneInspectFilterInput filter)
        {
            var radiusFilter = filter?.withinRadius;
            if (radiusFilter == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(radiusFilter.centerPath))
            {
                var centerObject = FindByPath(radiusFilter.centerPath.Trim().Replace('\\', '/'));
                if (centerObject == null)
                {
                    throw new InvalidOperationException($"Radius filter centerPath was not found: {radiusFilter.centerPath}");
                }

                return centerObject.transform.position;
            }

            if (radiusFilter.center == null)
            {
                throw new InvalidOperationException("Radius filter requires centerPath or center.");
            }

            return new Vector3(radiusFilter.center.x, radiusFilter.center.y, radiusFilter.center.z);
        }

        private static bool MatchesFilter(GameObject gameObject, string path, SceneInspectFilterInput filter, float distanceFromFilterCenter)
        {
            if (filter == null)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(filter.nameContains)
                && gameObject.name.IndexOf(filter.nameContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(filter.pathPrefix)
                && !path.StartsWith(filter.pathPrefix.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(filter.componentType) && !HasComponentType(gameObject, filter.componentType))
            {
                return false;
            }

            if (filter.activeState == "active" && !gameObject.activeInHierarchy)
            {
                return false;
            }

            if (filter.activeState == "inactive" && gameObject.activeInHierarchy)
            {
                return false;
            }

            return filter.withinRadius == null || distanceFromFilterCenter <= Math.Max(0f, filter.withinRadius.radius);
        }

        private static bool HasComponentType(GameObject gameObject, string requestedType)
        {
            var normalized = requestedType.Trim();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (string.Equals(type.Name, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.FullName, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path.Split('/');
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!string.Equals(root.name, segments[0], StringComparison.Ordinal))
                {
                    continue;
                }

                var current = root.transform;
                for (var index = 1; index < segments.Length && current != null; index++)
                {
                    current = current.Find(segments[index]);
                }

                if (current != null)
                {
                    return current.gameObject;
                }
            }

            return null;
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
