using System;
using UnityEditor;
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
        public string capturedAtUtc;
    }

    public static class ProjectSettingsInspector
    {
        public static ProjectSettingsReport Inspect()
        {
            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);

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
    }
}
