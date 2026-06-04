using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ScriptListRequest
    {
        public ScriptListInput input = new();
    }

    [Serializable]
    public sealed class ScriptListInput
    {
        public bool includePackages = true;
        public int maxResults = 500;
    }

    [Serializable]
    public sealed class ScriptListItem
    {
        public string guid;
        public string path;
        public string className;
        public string namespaceName;
    }

    [Serializable]
    public sealed class ScriptListReport
    {
        public int totalFound;
        public int returned;
        public bool includePackages;
        public bool truncated;
        public ScriptListItem[] scripts;
        public string capturedAtUtc;
    }

    [Serializable]
    public sealed class AssemblyListRequest
    {
        public AssemblyListInput input = new();
    }

    [Serializable]
    public sealed class AssemblyListInput
    {
        public int maxResults = 500;
    }

    [Serializable]
    public sealed class AssemblyListItem
    {
        public string name;
        public int sourceFileCount;
        public string[] defines;
    }

    [Serializable]
    public sealed class AssemblyListReport
    {
        public int totalFound;
        public int returned;
        public bool truncated;
        public AssemblyListItem[] assemblies;
        public string capturedAtUtc;
    }

    public static class ScriptAndAssemblyObserver
    {
        public static ScriptListReport ListScripts(string requestBody)
        {
            var input = ParseScriptRequest(requestBody).input ?? new ScriptListInput();
            var maxResults = input.maxResults <= 0 ? 500 : Math.Min(input.maxResults, 2000);
            var folders = input.includePackages ? new[] { "Assets", "Packages" } : new[] { "Assets" };
            var guids = AssetDatabase.FindAssets("t:MonoScript", folders);
            var scripts = new List<ScriptListItem>();

            foreach (var guid in guids)
            {
                if (scripts.Count >= maxResults)
                {
                    break;
                }

                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                var scriptClass = script != null ? script.GetClass() : null;

                scripts.Add(new ScriptListItem
                {
                    guid = guid,
                    path = path,
                    className = scriptClass != null ? scriptClass.Name : string.Empty,
                    namespaceName = scriptClass != null ? scriptClass.Namespace : string.Empty
                });
            }

            return new ScriptListReport
            {
                totalFound = guids.Length,
                returned = scripts.Count,
                includePackages = input.includePackages,
                truncated = guids.Length > scripts.Count,
                scripts = scripts.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static AssemblyListReport ListAssemblies(string requestBody)
        {
            var input = ParseAssemblyRequest(requestBody).input ?? new AssemblyListInput();
            var maxResults = input.maxResults <= 0 ? 500 : Math.Min(input.maxResults, 1000);
            var assemblies = CompilationPipeline.GetAssemblies();
            var items = new List<AssemblyListItem>();

            foreach (var assembly in assemblies)
            {
                if (items.Count >= maxResults)
                {
                    break;
                }

                items.Add(new AssemblyListItem
                {
                    name = assembly.name,
                    sourceFileCount = assembly.sourceFiles != null ? assembly.sourceFiles.Length : 0,
                    defines = assembly.defines,
                });
            }

            return new AssemblyListReport
            {
                totalFound = assemblies.Length,
                returned = items.Count,
                truncated = assemblies.Length > items.Count,
                assemblies = items.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static ScriptListRequest ParseScriptRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody) ? new ScriptListRequest() : UnityEngine.JsonUtility.FromJson<ScriptListRequest>(requestBody) ?? new ScriptListRequest();
            }
            catch
            {
                return new ScriptListRequest();
            }
        }

        private static AssemblyListRequest ParseAssemblyRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody) ? new AssemblyListRequest() : UnityEngine.JsonUtility.FromJson<AssemblyListRequest>(requestBody) ?? new AssemblyListRequest();
            }
            catch
            {
                return new AssemblyListRequest();
            }
        }
    }
}
