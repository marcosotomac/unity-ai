using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ProjectSettingsReport
    {
        public string companyName;
        public string productName;
        public string applicationIdentifier;
        public string activeBuildTarget;
        public string activeBuildTargetGroup;
        public string colorSpace;
        public string scriptingBackend;
        public string apiCompatibilityLevel;
        public bool developmentBuild;
        public bool connectProfiler;
        public string[] tags = Array.Empty<string>();
        public string[] layers = Array.Empty<string>();
        public RenderPipelineEnvironment renderPipeline = new();
        public InputSystemEnvironment inputSystem = new();
        public string[] compatibilityWarnings = Array.Empty<string>();
        public string capturedAtUtc;
    }

    public static class ProjectSettingsInspector
    {
        public static ProjectSettingsReport Inspect()
        {
            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var environment = ProjectEnvironmentInspector.Inspect();

            return new ProjectSettingsReport
            {
                companyName = PlayerSettings.companyName,
                productName = PlayerSettings.productName,
                applicationIdentifier = SafeGetApplicationIdentifier(buildTargetGroup),
                activeBuildTarget = buildTarget.ToString(),
                activeBuildTargetGroup = buildTargetGroup.ToString(),
                colorSpace = PlayerSettings.colorSpace.ToString(),
                scriptingBackend = SafeGetScriptingBackend(buildTargetGroup),
                apiCompatibilityLevel = SafeGetApiCompatibilityLevel(buildTargetGroup),
                developmentBuild = EditorUserBuildSettings.development,
                connectProfiler = EditorUserBuildSettings.connectProfiler,
                tags = SafeGetTags(),
                layers = SafeGetLayers(),
                renderPipeline = environment.renderPipeline,
                inputSystem = environment.inputSystem,
                compatibilityWarnings = environment.compatibilityWarnings,
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static string SafeGetApplicationIdentifier(BuildTargetGroup group)
        {
            try
            {
                return PlayerSettings.GetApplicationIdentifier(group);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeGetScriptingBackend(BuildTargetGroup group)
        {
            try
            {
                return PlayerSettings.GetScriptingBackend(group).ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeGetApiCompatibilityLevel(BuildTargetGroup group)
        {
            try
            {
                return PlayerSettings.GetApiCompatibilityLevel(group).ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string[] SafeGetTags()
        {
            try
            {
                return InternalEditorUtility.tags ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string[] SafeGetLayers()
        {
            try
            {
                return InternalEditorUtility.layers ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
