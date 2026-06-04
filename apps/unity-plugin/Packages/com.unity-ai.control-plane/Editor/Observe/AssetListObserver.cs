using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class AssetListRequest
    {
        public AssetListInput input = new();
    }

    [Serializable]
    public sealed class AssetListInput
    {
        public string folder = "Assets";
        public int maxResults = 200;
    }

    [Serializable]
    public sealed class AssetListItem
    {
        public string guid;
        public string path;
        public string type;
    }

    [Serializable]
    public sealed class AssetListReport
    {
        public string folder;
        public int totalFound;
        public int returned;
        public AssetListItem[] assets;
        public string capturedAtUtc;
    }

    public static class AssetListObserver
    {
        public static AssetListReport ListAssets(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new AssetListInput();
            var folder = string.IsNullOrWhiteSpace(input.folder) ? "Assets" : input.folder.Trim();
            var maxResults = input.maxResults <= 0 ? 200 : Math.Min(input.maxResults, 1000);
            var guids = AssetDatabase.FindAssets(string.Empty, new[] { folder });
            var items = new List<AssetListItem>();

            for (var index = 0; index < guids.Length && items.Count < maxResults; index++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[index]);
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);

                items.Add(new AssetListItem
                {
                    guid = guids[index],
                    path = path,
                    type = type != null ? type.Name : string.Empty
                });
            }

            return new AssetListReport
            {
                folder = folder,
                totalFound = guids.Length,
                returned = items.Count,
                assets = items.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static AssetListRequest ParseRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new AssetListRequest();
            }

            try
            {
                return UnityEngine.JsonUtility.FromJson<AssetListRequest>(requestBody) ?? new AssetListRequest();
            }
            catch
            {
                return new AssetListRequest();
            }
        }
    }
}
