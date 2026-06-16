using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class BuildSceneInput
    {
        public string path;
        public bool enabled = true;
    }

    [Serializable]
    public sealed class ProjectSettingsUpdateRequest
    {
        public ProjectSettingsUpdateInput input = new();
    }

    [Serializable]
    public sealed class ProjectSettingsUpdateInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string companyName;
        public string productName;
        public string applicationIdentifier;
        public string colorSpace;
        public string scriptingBackend;
        public int androidMinSdk;
        public int androidTargetSdk;
        public string[] androidArchitectures = Array.Empty<string>();
        public bool buildAppBundle;
        public bool developmentBuild;
        public bool connectProfiler;
        public string[] addTags = Array.Empty<string>();
        public string[] addLayers = Array.Empty<string>();
        public string activeInputHandling;
        public BuildSceneInput[] scenes = Array.Empty<BuildSceneInput>();
    }

    [Serializable]
    public sealed class ProjectSettingsUpdateResult
    {
        public bool dryRun;
        public bool applied;
        public bool refused;
        public bool requiresConfirmation;
        public string checkpointId;
        public string[] changedFields = Array.Empty<string>();
        public ProjectSettingsReport settings;
        public string[] buildScenes = Array.Empty<string>();
        public string message;
        public string verificationStatus;
        public string[] verificationSignals = Array.Empty<string>();
        public string timestampUtc;
    }

    public static class ProjectSettingsUpdateOperation
    {
        public static ProjectSettingsUpdateResult Execute(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new ProjectSettingsUpdateInput();
            if (!TryValidate(requestBody, input, out var error))
            {
                return Refused(input.dryRun, error);
            }

            var planned = GetChangedFields(requestBody).ToArray();
            if (input.dryRun)
            {
                return new ProjectSettingsUpdateResult
                {
                    dryRun = true,
                    changedFields = planned,
                    message = $"DRY RUN: would update {planned.Length} project/build setting field(s).",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return new ProjectSettingsUpdateResult
                {
                    requiresConfirmation = true,
                    changedFields = planned,
                    message = "Project Settings mutation requires confirm=true.",
                    verificationStatus = "needs_confirmation",
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            try
            {
                var checkpoint = DurableCheckpointStore.CreateInternal("project-settings-update", new[] { "ProjectSettings", "Packages/manifest.json", "Packages/packages-lock.json" });
                Apply(requestBody, input);
                AssetDatabase.SaveAssets();
                var settings = ProjectSettingsInspector.Inspect();
                var buildScenes = EditorBuildSettings.scenes.Select(scene => $"{scene.path}|enabled={scene.enabled}").ToArray();
                return new ProjectSettingsUpdateResult
                {
                    applied = true,
                    checkpointId = checkpoint.checkpointId,
                    changedFields = planned,
                    settings = settings,
                    buildScenes = buildScenes,
                    message = "Project Settings and Build Settings updated with a durable checkpoint.",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "checkpoint_created", "project_settings_verified" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }
            catch (Exception exception)
            {
                return Refused(false, exception.Message);
            }
        }

        private static void Apply(string body, ProjectSettingsUpdateInput input)
        {
            var group = BuildTargetGroup.Android;
            if (HasField(body, "companyName")) PlayerSettings.companyName = input.companyName.Trim();
            if (HasField(body, "productName")) PlayerSettings.productName = input.productName.Trim();
            if (HasField(body, "applicationIdentifier")) PlayerSettings.SetApplicationIdentifier(group, input.applicationIdentifier.Trim());
            if (HasField(body, "colorSpace")) PlayerSettings.colorSpace = ParseEnum<ColorSpace>(input.colorSpace);
            if (HasField(body, "scriptingBackend")) PlayerSettings.SetScriptingBackend(group, ParseEnum<ScriptingImplementation>(input.scriptingBackend));
            if (HasField(body, "androidMinSdk")) PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)input.androidMinSdk;
            if (HasField(body, "androidTargetSdk")) PlayerSettings.Android.targetSdkVersion = input.androidTargetSdk <= 0 ? AndroidSdkVersions.AndroidApiLevelAuto : (AndroidSdkVersions)input.androidTargetSdk;
            if (HasField(body, "androidArchitectures")) PlayerSettings.Android.targetArchitectures = ParseArchitectures(input.androidArchitectures);
            if (HasField(body, "buildAppBundle")) EditorUserBuildSettings.buildAppBundle = input.buildAppBundle;
            if (HasField(body, "developmentBuild")) EditorUserBuildSettings.development = input.developmentBuild;
            if (HasField(body, "connectProfiler")) EditorUserBuildSettings.connectProfiler = input.connectProfiler;
            if (HasField(body, "addTags")) AddTags(input.addTags);
            if (HasField(body, "addLayers")) AddLayers(input.addLayers);
            if (HasField(body, "activeInputHandling")) SetActiveInputHandling(input.activeInputHandling);
            if (HasField(body, "scenes"))
            {
                EditorBuildSettings.scenes = input.scenes.Select(scene => new EditorBuildSettingsScene(scene.path.Trim().Replace('\\', '/'), scene.enabled)).ToArray();
            }
        }

        private static bool TryValidate(string body, ProjectSettingsUpdateInput input, out string error)
        {
            if (HasField(body, "companyName") && string.IsNullOrWhiteSpace(input.companyName)) return Error("companyName cannot be empty.", out error);
            if (HasField(body, "productName") && string.IsNullOrWhiteSpace(input.productName)) return Error("productName cannot be empty.", out error);
            if (HasField(body, "applicationIdentifier") && (string.IsNullOrWhiteSpace(input.applicationIdentifier) || !input.applicationIdentifier.Contains("."))) return Error("applicationIdentifier must be reverse-DNS-like.", out error);
            if (HasField(body, "colorSpace") && !Enum.TryParse<ColorSpace>(input.colorSpace, true, out _)) return Error("Unsupported colorSpace.", out error);
            if (HasField(body, "scriptingBackend") && !Enum.TryParse<ScriptingImplementation>(input.scriptingBackend, true, out _)) return Error("Unsupported scriptingBackend.", out error);
            if (HasField(body, "androidMinSdk") && input.androidMinSdk < 21) return Error("androidMinSdk must be at least 21.", out error);
            if (HasField(body, "androidTargetSdk") && input.androidTargetSdk < 0) return Error("androidTargetSdk must be zero (automatic) or positive.", out error);
            if (HasField(body, "androidArchitectures") && ParseArchitectures(input.androidArchitectures) == 0) return Error("androidArchitectures must contain ARM64, ARMv7, or X86.", out error);
            if (HasField(body, "addTags") && !ValidateNames(input.addTags, 64, "tag", out error)) return false;
            if (HasField(body, "addLayers") && !ValidateNames(input.addLayers, 32, "layer", out error)) return false;
            if (HasField(body, "activeInputHandling") && ParseInputHandling(input.activeInputHandling) < 0) return Error("activeInputHandling must be legacy, old, input_system, new, or both.", out error);
            if (HasField(body, "scenes") && input.scenes.Any(scene => scene == null || string.IsNullOrWhiteSpace(scene.path) || !scene.path.StartsWith("Assets/", StringComparison.Ordinal) || !scene.path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || scene.path.Contains(".."))) return Error("Every build scene must be a safe Assets/*.unity path.", out error);
            error = string.Empty;
            return true;
        }

        private static AndroidArchitecture ParseArchitectures(string[] values)
        {
            var result = (AndroidArchitecture)0;
            foreach (var value in values ?? Array.Empty<string>())
            {
                if (Enum.TryParse<AndroidArchitecture>(value, true, out var parsed)) result |= parsed;
            }

            return result;
        }

        private static IEnumerable<string> GetChangedFields(string body)
        {
            foreach (var field in new[] { "companyName", "productName", "applicationIdentifier", "colorSpace", "scriptingBackend", "androidMinSdk", "androidTargetSdk", "androidArchitectures", "buildAppBundle", "developmentBuild", "connectProfiler", "addTags", "addLayers", "activeInputHandling", "scenes" })
            {
                if (HasField(body, field)) yield return field;
            }
        }

        private static void AddTags(string[] tags)
        {
            var manager = LoadTagManager();
            var property = manager.FindProperty("tags");
            foreach (var tag in NormalizeNames(tags).Distinct(StringComparer.Ordinal))
            {
                if (ContainsString(property, tag))
                {
                    continue;
                }

                property.InsertArrayElementAtIndex(property.arraySize);
                property.GetArrayElementAtIndex(property.arraySize - 1).stringValue = tag;
            }

            manager.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddLayers(string[] layers)
        {
            var manager = LoadTagManager();
            var property = manager.FindProperty("layers");
            foreach (var layer in NormalizeNames(layers).Distinct(StringComparer.Ordinal))
            {
                if (ContainsString(property, layer))
                {
                    continue;
                }

                var slot = -1;
                for (var index = 8; index < property.arraySize; index++)
                {
                    if (string.IsNullOrWhiteSpace(property.GetArrayElementAtIndex(index).stringValue))
                    {
                        slot = index;
                        break;
                    }
                }

                if (slot < 0)
                {
                    throw new InvalidOperationException("No free user layer slots are available.");
                }

                property.GetArrayElementAtIndex(slot).stringValue = layer;
            }

            manager.ApplyModifiedPropertiesWithoutUndo();
        }

        private static SerializedObject LoadTagManager()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                throw new InvalidOperationException("ProjectSettings/TagManager.asset could not be loaded.");
            }

            return new SerializedObject(assets[0]);
        }

        private static bool ContainsString(SerializedProperty property, string value)
        {
            for (var index = 0; index < property.arraySize; index++)
            {
                if (string.Equals(property.GetArrayElementAtIndex(index).stringValue, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetActiveInputHandling(string value)
        {
            var code = ParseInputHandling(value);
            if (code < 0)
            {
                throw new InvalidOperationException("Unsupported activeInputHandling value.");
            }

            var property = typeof(PlayerSettings).GetProperty("activeInputHandling", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property != null && property.CanWrite && property.PropertyType.IsEnum)
            {
                property.SetValue(null, Enum.ToObject(property.PropertyType, code));
                return;
            }

            WriteProjectSettingsActiveInputHandler(code);
        }

        private static void WriteProjectSettingsActiveInputHandler(int code)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var path = Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset");
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("ProjectSettings.asset could not be found.");
            }

            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var trimmed = lines[index].TrimStart();
                if (!trimmed.StartsWith("activeInputHandler:", StringComparison.Ordinal))
                {
                    continue;
                }

                var indentation = lines[index].Substring(0, lines[index].Length - trimmed.Length);
                lines[index] = indentation + "activeInputHandler: " + code;
                File.WriteAllLines(path, lines);
                return;
            }

            throw new InvalidOperationException("activeInputHandler was not found in ProjectSettings.asset.");
        }

        private static int ParseInputHandling(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_"))
            {
                case "legacy":
                case "old":
                case "input_manager":
                    return 0;
                case "new":
                case "input_system":
                case "inputsystem":
                    return 1;
                case "both":
                    return 2;
                default:
                    return -1;
            }
        }

        private static bool ValidateNames(string[] values, int maxLength, string label, out string error)
        {
            foreach (var name in NormalizeNames(values))
            {
                if (name.Length > maxLength
                    || name.Contains("/")
                    || name.Contains("\\")
                    || name.Contains("..")
                    || name.Any(char.IsControl))
                {
                    error = $"Invalid {label} name '{name}'.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static IEnumerable<string> NormalizeNames(string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                var normalized = (value ?? string.Empty).Trim();
                if (normalized.Length > 0)
                {
                    yield return normalized;
                }
            }
        }

        private static T ParseEnum<T>(string value) where T : struct
        {
            return Enum.TryParse<T>(value, true, out var parsed) ? parsed : default;
        }

        private static bool HasField(string json, string field)
        {
            return !string.IsNullOrEmpty(json) && json.Contains("\"" + field + "\"");
        }

        private static bool Error(string message, out string error)
        {
            error = message;
            return false;
        }

        private static ProjectSettingsUpdateResult Refused(bool dryRun, string message)
        {
            return new ProjectSettingsUpdateResult
            {
                dryRun = dryRun,
                refused = true,
                message = message,
                verificationStatus = "refused",
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static ProjectSettingsUpdateRequest ParseRequest(string body)
        {
            try { return JsonUtility.FromJson<ProjectSettingsUpdateRequest>(body) ?? new ProjectSettingsUpdateRequest(); }
            catch { return new ProjectSettingsUpdateRequest(); }
        }
    }
}
