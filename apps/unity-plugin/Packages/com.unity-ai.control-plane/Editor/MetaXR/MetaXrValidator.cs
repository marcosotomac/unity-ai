using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class MetaXrValidationReport
    {
        public bool valid;
        public bool likelyMetaXrInstalled;
        public bool xrManagementInstalled;
        public bool openXrInstalled;
        public bool metaOpenXrInstalled;
        public bool openXrLoaderAssigned;
        public bool metaFeatureEnabled;
        public bool androidBuildSettingsValid;
        public string activeBuildTarget;
        public string[] enabledOpenXrFeatures = Array.Empty<string>();
        public List<string> findings = new();
        public string capturedAtUtc;
    }

    public static class MetaXrValidator
    {
        public static MetaXrValidationReport Validate()
        {
            var packages = new HashSet<string>(
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(package => package.name),
                StringComparer.Ordinal);
            var build = BuildOperations.ValidateAndroidQuest();
            var features = GetEnabledOpenXrFeatures();
            var loaderAssigned = IsOpenXrLoaderAssigned();
            var report = new MetaXrValidationReport
            {
                xrManagementInstalled = packages.Contains("com.unity.xr.management"),
                openXrInstalled = packages.Contains("com.unity.xr.openxr"),
                metaOpenXrInstalled = packages.Contains("com.unity.xr.meta-openxr"),
                openXrLoaderAssigned = loaderAssigned,
                metaFeatureEnabled = features.Any(name => name.Contains("Meta", StringComparison.OrdinalIgnoreCase) || name.Contains("Oculus", StringComparison.OrdinalIgnoreCase)),
                androidBuildSettingsValid = build.valid,
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                enabledOpenXrFeatures = features,
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
            report.likelyMetaXrInstalled = report.metaOpenXrInstalled || AssetDatabase.FindAssets("OVRCameraRig t:Prefab").Length > 0;

            if (!report.xrManagementInstalled) report.findings.Add("XR Plug-in Management package is not installed.");
            if (!report.openXrInstalled) report.findings.Add("Unity OpenXR package is not installed.");
            if (!report.metaOpenXrInstalled) report.findings.Add("Unity Meta OpenXR package is not installed.");
            if (!report.openXrLoaderAssigned) report.findings.Add("OpenXRLoader is not assigned for Android.");
            if (!report.metaFeatureEnabled) report.findings.Add("No enabled Meta/Oculus OpenXR feature was detected for Android.");
            report.findings.AddRange(build.errors);
            report.findings.AddRange(build.warnings);
            report.valid = report.xrManagementInstalled
                && report.openXrInstalled
                && report.metaOpenXrInstalled
                && report.openXrLoaderAssigned
                && report.metaFeatureEnabled
                && report.androidBuildSettingsValid;

            if (report.findings.Count == 0)
            {
                report.findings.Add("Meta Quest OpenXR setup passed package, loader, feature, and Android build checks.");
            }

            return report;
        }

        internal static bool IsOpenXrLoaderAssigned()
        {
            var manager = GetAndroidXrManagerSettings();
            if (manager == null)
            {
                return false;
            }

            var property = manager.GetType().GetProperty("activeLoaders", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(manager) is not IEnumerable loaders)
            {
                return false;
            }

            return loaders.Cast<object>().Any(loader => loader != null && loader.GetType().FullName == "UnityEngine.XR.OpenXR.OpenXRLoader");
        }

        internal static object GetAndroidXrManagerSettings()
        {
            var perTargetType = FindType("UnityEngine.XR.Management.XRGeneralSettingsPerBuildTarget");
            var method = perTargetType?.GetMethod(
                "XRGeneralSettingsForBuildTarget",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(BuildTargetGroup) },
                null);
            var generalSettings = method?.Invoke(null, new object[] { BuildTargetGroup.Android });
            return generalSettings?.GetType().GetProperty("Manager", BindingFlags.Instance | BindingFlags.Public)?.GetValue(generalSettings);
        }

        internal static string[] GetEnabledOpenXrFeatures()
        {
            var settingsType = FindType("UnityEngine.XR.OpenXR.OpenXRSettings");
            var method = settingsType?.GetMethod(
                "GetSettingsForBuildTargetGroup",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(BuildTargetGroup) },
                null);
            var settings = method?.Invoke(null, new object[] { BuildTargetGroup.Android });
            var features = settings?.GetType().GetProperty("features", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(settings) as IEnumerable;
            if (features == null)
            {
                return Array.Empty<string>();
            }

            return features.Cast<object>()
                .Where(feature => feature != null && ReadEnabled(feature))
                .Select(feature => feature.GetType().FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        internal static Type FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }

        internal static bool ReadEnabled(object feature)
        {
            var property = feature.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(feature) is bool enabled && enabled;
        }
    }
}
