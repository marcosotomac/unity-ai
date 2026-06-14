using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

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
        public RenderPipelineEnvironment renderPipeline = new();
        public InputSystemEnvironment inputSystem = new();
        public string[] compatibilityWarnings = Array.Empty<string>();
        public string capturedAtUtc;
    }

    [Serializable]
    public sealed class ProjectEnvironmentReport
    {
        public RenderPipelineEnvironment renderPipeline = new();
        public InputSystemEnvironment inputSystem = new();
        public string[] compatibilityWarnings = Array.Empty<string>();
    }

    [Serializable]
    public sealed class RenderPipelineEnvironment
    {
        public string kind = "unknown";
        public bool isScriptableRenderPipeline;
        public string assetName = string.Empty;
        public string assetType = string.Empty;
        public string assetPath = string.Empty;
        public string effectiveAssetSource = "none";
        public int qualityLevel;
        public string qualityLevelName = string.Empty;
        public string recommendedShader = string.Empty;
    }

    [Serializable]
    public sealed class InputSystemEnvironment
    {
        public string mode = "unknown";
        public int activeInputHandler = -1;
        public bool legacyInputManagerEnabled;
        public bool inputSystemPackageEnabled;
        public bool inputSystemPackageInstalled;
        public string inputSystemPackageVersion = string.Empty;
        public string recommendedApi = string.Empty;
        public string detectionSource = "ProjectSettings/ProjectSettings.asset";
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

            var environment = ProjectEnvironmentInspector.Inspect();
            return new ProjectSnapshot
            {
                projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                unityVersion = Application.unityVersion,
                activeScenePath = activeScene.path,
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                rootGameObjectCount = rootGameObjects.Length,
                rootGameObjectNames = rootGameObjectNames,
                renderPipeline = environment.renderPipeline,
                inputSystem = environment.inputSystem,
                compatibilityWarnings = environment.compatibilityWarnings,
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }
    }

    public static class ProjectEnvironmentInspector
    {
        private const string InputSystemPackageName = "com.unity.inputsystem";

        public static ProjectEnvironmentReport Inspect()
        {
            var warnings = new List<string>();
            var renderPipeline = InspectRenderPipeline(warnings);
            var inputSystem = InspectInputSystem(warnings);
            return new ProjectEnvironmentReport
            {
                renderPipeline = renderPipeline,
                inputSystem = inputSystem,
                compatibilityWarnings = warnings.ToArray()
            };
        }

        private static RenderPipelineEnvironment InspectRenderPipeline(List<string> warnings)
        {
            var qualityAsset = QualitySettings.renderPipeline;
#pragma warning disable CS0618
            var defaultAsset = GraphicsSettings.renderPipelineAsset;
#pragma warning restore CS0618
            var effectiveAsset = qualityAsset != null ? qualityAsset : defaultAsset;
            var kind = ClassifyRenderPipeline(effectiveAsset);
            if (kind == "custom")
            {
                warnings.Add("Custom Scriptable Render Pipeline detected; verify shader compatibility before authoring materials.");
            }

            return new RenderPipelineEnvironment
            {
                kind = kind,
                isScriptableRenderPipeline = effectiveAsset != null,
                assetName = effectiveAsset != null ? effectiveAsset.name : string.Empty,
                assetType = effectiveAsset != null ? effectiveAsset.GetType().FullName : string.Empty,
                assetPath = effectiveAsset != null ? AssetDatabase.GetAssetPath(effectiveAsset) : string.Empty,
                effectiveAssetSource = qualityAsset != null ? "quality" : defaultAsset != null ? "graphics" : "none",
                qualityLevel = QualitySettings.GetQualityLevel(),
                qualityLevelName = QualitySettings.names.ElementAtOrDefault(QualitySettings.GetQualityLevel()) ?? string.Empty,
                recommendedShader = RecommendedShader(kind)
            };
        }

        private static InputSystemEnvironment InspectInputSystem(List<string> warnings)
        {
            var activeInputHandler = ReadActiveInputHandler();
            var packageVersion = FindRegisteredPackageVersion(InputSystemPackageName);
            var packageInstalled = !string.IsNullOrEmpty(packageVersion);
            var mode = activeInputHandler switch
            {
                0 => "legacy",
                1 => "input_system",
                2 => "both",
                _ => "unknown"
            };
            var legacyEnabled = activeInputHandler == 0 || activeInputHandler == 2;
            var inputSystemEnabled = activeInputHandler == 1 || activeInputHandler == 2;

            if (inputSystemEnabled && !packageInstalled)
            {
                warnings.Add("The Input System is enabled in Player Settings but com.unity.inputsystem is not registered.");
            }
            else if (packageInstalled && activeInputHandler == 0)
            {
                warnings.Add("com.unity.inputsystem is installed but Player Settings currently use only the legacy Input Manager.");
            }
            else if (activeInputHandler < 0)
            {
                warnings.Add("Unable to determine Active Input Handling; do not generate input code until the setting is verified.");
            }

            return new InputSystemEnvironment
            {
                mode = mode,
                activeInputHandler = activeInputHandler,
                legacyInputManagerEnabled = legacyEnabled,
                inputSystemPackageEnabled = inputSystemEnabled,
                inputSystemPackageInstalled = packageInstalled,
                inputSystemPackageVersion = packageVersion,
                recommendedApi = RecommendedInputApi(mode, packageInstalled)
            };
        }

        private static string ClassifyRenderPipeline(RenderPipelineAsset asset)
        {
            if (asset == null)
            {
                return "built_in";
            }

            var typeName = asset.GetType().FullName ?? asset.GetType().Name;
            if (typeName.IndexOf("UniversalRenderPipelineAsset", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "urp";
            }

            if (typeName.IndexOf("HDRenderPipelineAsset", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("HighDefinitionRenderPipelineAsset", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "hdrp";
            }

            return "custom";
        }

        private static string RecommendedShader(string kind)
        {
            return kind switch
            {
                "built_in" => "Standard",
                "urp" => "Universal Render Pipeline/Lit",
                "hdrp" => "HDRP/Lit",
                _ => "Verify against the active RenderPipelineAsset"
            };
        }

        private static string RecommendedInputApi(string mode, bool packageInstalled)
        {
            return mode switch
            {
                "legacy" => "UnityEngine.Input",
                "input_system" => packageInstalled ? "UnityEngine.InputSystem" : "Install com.unity.inputsystem before generating input code",
                "both" => packageInstalled ? "UnityEngine.InputSystem (preferred); UnityEngine.Input is also enabled" : "UnityEngine.Input",
                _ => "Inspect Active Input Handling before generating input code"
            };
        }

        private static int ReadActiveInputHandler()
        {
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                var path = Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset");
                if (!File.Exists(path))
                {
                    return -1;
                }

                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("activeInputHandler:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var rawValue = trimmed.Substring("activeInputHandler:".Length).Trim();
                    return int.TryParse(rawValue, out var value) ? value : -1;
                }
            }
            catch
            {
                return -1;
            }

            return -1;
        }

        private static string FindRegisteredPackageVersion(string packageName)
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo
                    .GetAllRegisteredPackages()
                    .FirstOrDefault(candidate => string.Equals(candidate.name, packageName, StringComparison.Ordinal));
                return package?.version ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
