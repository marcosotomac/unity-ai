using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class MetaXrConfigurationRequest
    {
        public MetaXrConfigurationInput input = new();
    }

    [Serializable]
    public sealed class MetaXrConfigurationInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public bool installPackages = true;
        public bool switchToAndroid = true;
        public bool installMetaOpenXr = true;
        public int androidMinSdk = 29;
        public string applicationIdentifier;
    }

    [Serializable]
    public sealed class MetaXrConfigurationResult
    {
        public string checkpointId;
        public string[] installedPackages = Array.Empty<string>();
        public string[] enabledFeatures = Array.Empty<string>();
        public bool loaderAssigned;
        public MetaXrValidationReport validation;
        public string completedAtUtc;
    }

    [InitializeOnLoad]
    public static class MetaXrConfigurationController
    {
        private const string Capability = "unity.meta_xr.configure";
        private static readonly Dictionary<string, AddAndRemoveRequest> PackageRequests = new();

        static MetaXrConfigurationController()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        public static UnityAiJobStartResult Start(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new MetaXrConfigurationInput();
            input.androidMinSdk = Math.Max(29, Math.Min(input.androidMinSdk, 99));
            if (!string.IsNullOrWhiteSpace(input.applicationIdentifier) && !input.applicationIdentifier.Contains("."))
            {
                return Rejected(input.dryRun, "applicationIdentifier must be reverse-DNS-like.");
            }

            var missing = GetMissingPackages(input);
            if (input.dryRun)
            {
                return new UnityAiJobStartResult
                {
                    accepted = true,
                    dryRun = true,
                    status = "preview",
                    message = $"DRY RUN: would configure Quest OpenXR and install {missing.Length} missing package(s).",
                    requiredPermissions = new[] { "modify_project_settings" },
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return new UnityAiJobStartResult
                {
                    requiresConfirmation = true,
                    status = "needs_confirmation",
                    message = "Meta XR/OpenXR configuration changes packages and Project Settings and requires confirm=true.",
                    requiredPermissions = new[] { "modify_project_settings" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (missing.Length > 0 && !input.installPackages)
            {
                return Rejected(false, "Required packages are missing and installPackages=false: " + string.Join(", ", missing));
            }

            if (UnityAiJobStore.List(null, "meta_xr", 100).Any(job => job.status == "queued" || job.status == "running"))
            {
                return Rejected(false, "Another Meta XR configuration job is active.");
            }

            var checkpoint = DurableCheckpointStore.CreateInternal(
                "meta-xr-configure",
                new[] { "ProjectSettings", "Packages/manifest.json", "Packages/packages-lock.json" });
            var normalized = new MetaXrConfigurationRequest { input = input };
            var job = UnityAiJobStore.Create(
                Capability,
                "meta_xr",
                UnityAiJobStore.ParseEnvelope(requestBody),
                JsonUtility.ToJson(normalized),
                $"Configuring Meta XR/OpenXR. Checkpoint: {checkpoint.checkpointId}");
            job.resultJson = checkpoint.checkpointId;
            UnityAiJobStore.Save(job);
            UnityAiJobStore.MarkRunning(job.jobId, "packages", "Checking required XR packages.", 0.1f);

            return new UnityAiJobStartResult
            {
                accepted = true,
                jobId = job.jobId,
                status = "running",
                message = "Meta XR/OpenXR configuration started.",
                requiredPermissions = new[] { "modify_project_settings" },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void Tick()
        {
            foreach (var job in UnityAiJobStore.List("running", "meta_xr", 20))
            {
                try
                {
                    if (job.cancelRequested)
                    {
                        UnityAiJobStore.Cancel(job.jobId, "Meta XR configuration cancelled.");
                        PackageRequests.Remove(job.jobId);
                        continue;
                    }

                    var input = ParseRequest(job.requestJson).input ?? new MetaXrConfigurationInput();
                    if (!EnsurePackages(job, input))
                    {
                        continue;
                    }

                    if (!EnsureAndroidTarget(job, input))
                    {
                        continue;
                    }

                    Configure(job, input);
                }
                catch (Exception exception)
                {
                    PackageRequests.Remove(job.jobId);
                    UnityAiJobStore.Fail(job.jobId, exception.GetBaseException().Message);
                }
            }
        }

        private static bool EnsurePackages(UnityAiJobRecord job, MetaXrConfigurationInput input)
        {
            var missing = GetMissingPackages(input);
            if (missing.Length == 0)
            {
                PackageRequests.Remove(job.jobId);
                return true;
            }

            if (!input.installPackages)
            {
                throw new InvalidOperationException("Required XR packages are missing: " + string.Join(", ", missing));
            }

            if (!PackageRequests.TryGetValue(job.jobId, out var request))
            {
                UnityAiJobStore.UpdateProgress(job.jobId, "packages", "Installing required XR packages.", 0.25f);
                PackageRequests[job.jobId] = Client.AddAndRemove(missing, Array.Empty<string>());
                return false;
            }

            if (!request.IsCompleted)
            {
                return false;
            }

            PackageRequests.Remove(job.jobId);
            if (request.Status == StatusCode.Failure)
            {
                throw new InvalidOperationException(request.Error?.message ?? "Failed to install XR packages.");
            }

            return GetMissingPackages(input).Length == 0;
        }

        private static bool EnsureAndroidTarget(UnityAiJobRecord job, MetaXrConfigurationInput input)
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
            {
                return true;
            }

            if (!input.switchToAndroid)
            {
                throw new InvalidOperationException("Active build target is not Android and switchToAndroid=false.");
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException("Unity Android Build Support is not installed.");
            }

            UnityAiJobStore.UpdateProgress(job.jobId, "switching_target", "Switching the active build target to Android.", 0.45f);
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android);
            }

            return false;
        }

        private static void Configure(UnityAiJobRecord job, MetaXrConfigurationInput input)
        {
            UnityAiJobStore.UpdateProgress(job.jobId, "configuring", "Applying Android, XR loader, and OpenXR feature settings.", 0.7f);
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)Math.Max(29, input.androidMinSdk);
            if (!string.IsNullOrWhiteSpace(input.applicationIdentifier))
            {
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, input.applicationIdentifier.Trim());
            }

            AssignOpenXrLoader();
            var enabledFeatures = EnableMetaFeatures();
            AssetDatabase.SaveAssets();
            var validation = MetaXrValidator.Validate();
            var result = new MetaXrConfigurationResult
            {
                checkpointId = job.resultJson,
                installedPackages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(package => package.name.StartsWith("com.unity.xr.", StringComparison.Ordinal))
                    .Select(package => package.name + "@" + package.version)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                enabledFeatures = enabledFeatures,
                loaderAssigned = MetaXrValidator.IsOpenXrLoaderAssigned(),
                validation = validation,
                completedAtUtc = DateTime.UtcNow.ToString("O")
            };

            if (validation.valid)
            {
                UnityAiJobStore.Complete(job.jobId, result, "Meta Quest OpenXR setup configured and verified.", "checkpoint_created", "xr_settings_valid", "meta_xr_configured");
            }
            else
            {
                UnityAiJobStore.Fail(job.jobId, "Meta XR configuration completed but validation still reports findings: " + string.Join(" ", validation.findings), result, "checkpoint_created");
            }
        }

        private static void AssignOpenXrLoader()
        {
            var manager = MetaXrValidator.GetAndroidXrManagerSettings();
            if (manager == null)
            {
                throw new InvalidOperationException("XR Manager Settings for Android are unavailable.");
            }

            var metadataType = MetaXrValidator.FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
            var method = metadataType?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return candidate.Name == "AssignLoader"
                        && parameters.Length == 3
                        && parameters[1].ParameterType == typeof(string)
                        && parameters[2].ParameterType == typeof(BuildTargetGroup);
                });
            if (method == null)
            {
                throw new InvalidOperationException("XRPackageMetadataStore.AssignLoader is unavailable.");
            }

            var assigned = method.Invoke(null, new[] { manager, "UnityEngine.XR.OpenXR.OpenXRLoader, Unity.XR.OpenXR", (object)BuildTargetGroup.Android });
            if (assigned is bool success && !success && !MetaXrValidator.IsOpenXrLoaderAssigned())
            {
                throw new InvalidOperationException("Unity could not assign OpenXRLoader for Android.");
            }
        }

        private static string[] EnableMetaFeatures()
        {
            var settingsType = MetaXrValidator.FindType("UnityEngine.XR.OpenXR.OpenXRSettings");
            var getSettings = settingsType?.GetMethod(
                "GetSettingsForBuildTargetGroup",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(BuildTargetGroup) },
                null);
            var settings = getSettings?.Invoke(null, new object[] { BuildTargetGroup.Android });
            var features = settings?.GetType().GetProperty("features", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(settings) as IEnumerable;
            if (features == null)
            {
                throw new InvalidOperationException("Android OpenXR feature settings are unavailable.");
            }

            var enabled = new List<string>();
            foreach (var feature in features.Cast<object>().Where(feature => feature != null))
            {
                var name = feature.GetType().FullName ?? string.Empty;
                if (!name.Contains("MetaQuestFeature", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("OculusTouchControllerProfile", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("MetaQuestTouchProControllerProfile", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var property = feature.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanWrite == true)
                {
                    property.SetValue(feature, true);
                    if (feature is UnityEngine.Object unityObject)
                    {
                        EditorUtility.SetDirty(unityObject);
                    }

                    enabled.Add(name);
                }
            }

            if (!enabled.Any(name => name.Contains("MetaQuestFeature", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Meta Quest OpenXR feature was not found after package installation.");
            }

            return enabled.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static string[] GetMissingPackages(MetaXrConfigurationInput input)
        {
            var installed = new HashSet<string>(
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(package => package.name),
                StringComparer.Ordinal);
            var required = new List<string> { "com.unity.xr.management", "com.unity.xr.openxr" };
            if (input.installMetaOpenXr)
            {
                required.Add("com.unity.xr.meta-openxr");
            }

            return required.Where(package => !installed.Contains(package)).ToArray();
        }

        private static UnityAiJobStartResult Rejected(bool dryRun, string message)
        {
            return new UnityAiJobStartResult
            {
                accepted = false,
                dryRun = dryRun,
                status = "refused",
                message = message,
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static MetaXrConfigurationRequest ParseRequest(string body)
        {
            try { return JsonUtility.FromJson<MetaXrConfigurationRequest>(body) ?? new MetaXrConfigurationRequest(); }
            catch { return new MetaXrConfigurationRequest(); }
        }
    }
}
