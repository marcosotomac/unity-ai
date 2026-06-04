using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class AssetDependencyRequest
    {
        public AssetDependencyInput input = new();
    }

    [Serializable]
    public sealed class AssetDependencyInput
    {
        public string path;
        public bool recursive = true;
        public int maxResults = 200;
    }

    [Serializable]
    public sealed class AssetDependencyItem
    {
        public string path;
        public string type;
    }

    [Serializable]
    public sealed class AssetDependencyReport
    {
        public string path;
        public bool exists;
        public bool recursive;
        public int totalFound;
        public int returned;
        public bool truncated;
        public AssetDependencyItem[] dependencies;
        public string capturedAtUtc;
    }

    public static class AssetDependencyObserver
    {
        public static AssetDependencyReport InspectDependencies(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new AssetDependencyInput();
            var path = input.path ?? string.Empty;
            var maxResults = input.maxResults <= 0 ? 200 : Math.Min(input.maxResults, 1000);
            var exists = !string.IsNullOrWhiteSpace(path) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path));
            var dependencyPaths = exists ? AssetDatabase.GetDependencies(path, input.recursive) : Array.Empty<string>();
            var dependencies = new List<AssetDependencyItem>();

            foreach (var dependencyPath in dependencyPaths)
            {
                if (dependencies.Count >= maxResults)
                {
                    break;
                }

                var type = AssetDatabase.GetMainAssetTypeAtPath(dependencyPath);
                dependencies.Add(new AssetDependencyItem
                {
                    path = dependencyPath,
                    type = type != null ? type.Name : string.Empty
                });
            }

            return new AssetDependencyReport
            {
                path = path,
                exists = exists,
                recursive = input.recursive,
                totalFound = dependencyPaths.Length,
                returned = dependencies.Count,
                truncated = dependencyPaths.Length > dependencies.Count,
                dependencies = dependencies.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static AssetDependencyRequest ParseRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody) ? new AssetDependencyRequest() : UnityEngine.JsonUtility.FromJson<AssetDependencyRequest>(requestBody) ?? new AssetDependencyRequest();
            }
            catch
            {
                return new AssetDependencyRequest();
            }
        }
    }
}
