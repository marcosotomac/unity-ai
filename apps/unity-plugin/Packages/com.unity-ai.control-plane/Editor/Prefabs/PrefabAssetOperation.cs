using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class PrefabAssetRequest
    {
        public PrefabAssetInput input = new();
    }

    [Serializable]
    public sealed class PrefabAssetInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string action;
        public string prefabPath;
        public string targetPath;
        public string sceneObjectPath;
        public bool connectToScene;
        public PrefabEditItemInput[] operations = Array.Empty<PrefabEditItemInput>();
    }

    [Serializable]
    public sealed class PrefabEditItemInput
    {
        public string kind;
        public string objectPath;
        public string name;
        public bool active;
        public string componentType;
        public int componentIndex;
        public string propertyPath;
        public SceneSerializedValueInput value = new();
    }

    [Serializable]
    public sealed class PrefabAssetResult
    {
        public bool dryRun;
        public bool applied;
        public bool refused;
        public bool requiresConfirmation;
        public string action;
        public string prefabPath;
        public string sceneObjectPath;
        public string prefabAssetType;
        public int appliedOperationCount;
        public string checkpointId;
        public string message;
        public string verificationStatus;
        public string[] verificationSignals = Array.Empty<string>();
        public string timestampUtc;
    }

    public static class PrefabAssetOperation
    {
        private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
        {
            "save_scene_object",
            "create_variant",
            "edit_asset",
            "apply_overrides",
            "revert_overrides"
        };

        private static readonly HashSet<string> EditKinds = new(StringComparer.Ordinal)
        {
            "create_child",
            "delete",
            "rename",
            "set_active",
            "add_component",
            "remove_component",
            "set_property"
        };

        public static PrefabAssetResult Execute(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new PrefabAssetInput();
            var action = Normalize(input.action);
            input.prefabPath = NormalizeAssetPath(input.prefabPath);
            input.targetPath = NormalizeAssetPath(input.targetPath);
            input.sceneObjectPath = NormalizeHierarchyPath(input.sceneObjectPath);

            if (!Validate(input, action, out var error))
            {
                return Refused(input.dryRun, action, input.prefabPath, input.sceneObjectPath, error);
            }

            if (input.dryRun)
            {
                return new PrefabAssetResult
                {
                    dryRun = true,
                    action = action,
                    prefabPath = action == "create_variant" ? input.targetPath : input.prefabPath,
                    sceneObjectPath = input.sceneObjectPath,
                    message = $"DRY RUN: validated prefab action '{action}'.",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return new PrefabAssetResult
                {
                    action = action,
                    prefabPath = action == "create_variant" ? input.targetPath : input.prefabPath,
                    sceneObjectPath = input.sceneObjectPath,
                    requiresConfirmation = true,
                    message = "Prefab mutation requires confirm=true.",
                    verificationStatus = "needs_confirmation",
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            try
            {
                return action switch
                {
                    "save_scene_object" => SaveSceneObject(input),
                    "create_variant" => CreateVariant(input),
                    "edit_asset" => EditAsset(input),
                    "apply_overrides" => ApplyOverrides(input),
                    "revert_overrides" => RevertOverrides(input),
                    _ => Refused(false, action, input.prefabPath, input.sceneObjectPath, "Unsupported prefab action.")
                };
            }
            catch (Exception exception)
            {
                return Refused(false, action, input.prefabPath, input.sceneObjectPath, exception.GetBaseException().Message);
            }
        }

        private static PrefabAssetResult SaveSceneObject(PrefabAssetInput input)
        {
            var source = RequireSceneObject(input.sceneObjectPath);
            var checkpoint = CheckpointAsset(input.prefabPath, "prefab-save");
            EnsureAssetParent(input.prefabPath);
            GameObject saved;
            if (input.connectToScene)
            {
                saved = PrefabUtility.SaveAsPrefabAssetAndConnect(source, input.prefabPath, InteractionMode.AutomatedAction);
            }
            else
            {
                saved = PrefabUtility.SaveAsPrefabAsset(source, input.prefabPath);
            }

            if (saved == null)
            {
                throw new InvalidOperationException("Unity did not save the prefab asset.");
            }

            return VerifiedResult("save_scene_object", input.prefabPath, input.sceneObjectPath, 1, checkpoint.checkpointId, "Saved scene object as prefab.");
        }

        private static PrefabAssetResult CreateVariant(PrefabAssetInput input)
        {
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(input.prefabPath);
            if (basePrefab == null)
            {
                throw new InvalidOperationException($"Base prefab '{input.prefabPath}' was not found.");
            }

            var checkpoint = CheckpointAsset(input.targetPath, "prefab-variant");
            EnsureAssetParent(input.targetPath);
            var instance = PrefabUtility.InstantiatePrefab(basePrefab) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException("Unity could not instantiate the base prefab.");
            }

            try
            {
                var saved = PrefabUtility.SaveAsPrefabAsset(instance, input.targetPath);
                if (saved == null)
                {
                    throw new InvalidOperationException("Unity did not save the prefab variant.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            if (PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(input.targetPath)) != PrefabAssetType.Variant)
            {
                throw new InvalidOperationException("Saved asset is not a prefab variant.");
            }

            return VerifiedResult("create_variant", input.targetPath, string.Empty, 1, checkpoint.checkpointId, "Created and verified prefab variant.");
        }

        private static PrefabAssetResult EditAsset(PrefabAssetInput input)
        {
            var checkpoint = CheckpointAsset(input.prefabPath, "prefab-edit");
            var root = PrefabUtility.LoadPrefabContents(input.prefabPath);
            if (root == null)
            {
                throw new InvalidOperationException($"Prefab '{input.prefabPath}' could not be loaded for editing.");
            }

            var applied = 0;
            try
            {
                foreach (var operation in input.operations ?? Array.Empty<PrefabEditItemInput>())
                {
                    ApplyEdit(root, operation);
                    applied++;
                }

                var saved = PrefabUtility.SaveAsPrefabAsset(root, input.prefabPath);
                if (saved == null)
                {
                    throw new InvalidOperationException("Unity did not save the edited prefab.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return VerifiedResult("edit_asset", input.prefabPath, string.Empty, applied, checkpoint.checkpointId, $"Applied {applied} prefab edit operation(s).");
        }

        private static PrefabAssetResult ApplyOverrides(PrefabAssetInput input)
        {
            var scene = RequireSavedActiveScene();
            var instance = RequirePrefabInstance(input.sceneObjectPath);
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
            var checkpoint = DurableCheckpointStore.CreateInternal("prefab-apply-overrides", new[] { assetPath, assetPath + ".meta", scene.path, scene.path + ".meta" });
            PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
            EditorSceneManager.MarkSceneDirty(scene);
            return VerifiedResult("apply_overrides", assetPath, input.sceneObjectPath, 1, checkpoint.checkpointId, "Applied prefab instance overrides.");
        }

        private static PrefabAssetResult RevertOverrides(PrefabAssetInput input)
        {
            var scene = RequireSavedActiveScene();
            var instance = RequirePrefabInstance(input.sceneObjectPath);
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
            var checkpoint = DurableCheckpointStore.CreateInternal("prefab-revert-overrides", new[] { scene.path, scene.path + ".meta" });
            PrefabUtility.RevertPrefabInstance(instance, InteractionMode.AutomatedAction);
            EditorSceneManager.MarkSceneDirty(scene);
            return VerifiedResult("revert_overrides", assetPath, input.sceneObjectPath, 1, checkpoint.checkpointId, "Reverted prefab instance overrides.");
        }

        private static void ApplyEdit(GameObject root, PrefabEditItemInput input)
        {
            if (input == null)
            {
                throw new InvalidOperationException("Prefab edit operation cannot be null.");
            }

            var kind = Normalize(input.kind);
            var target = RequirePrefabObject(root, input.objectPath);
            switch (kind)
            {
                case "create_child":
                    RequireSafeName(input.name);
                    if (target.transform.Find(input.name) != null)
                    {
                        throw new InvalidOperationException($"Child '{input.name}' already exists under '{target.name}'.");
                    }

                    new GameObject(input.name).transform.SetParent(target.transform, false);
                    break;
                case "delete":
                    if (target == root)
                    {
                        throw new InvalidOperationException("The prefab root cannot be deleted.");
                    }

                    UnityEngine.Object.DestroyImmediate(target);
                    break;
                case "rename":
                    RequireSafeName(input.name);
                    target.name = input.name.Trim();
                    break;
                case "set_active":
                    target.SetActive(input.active);
                    break;
                case "add_component":
                    var addType = ResolveComponentType(input.componentType);
                    if (addType == typeof(Transform))
                    {
                        throw new InvalidOperationException("Transform cannot be added.");
                    }

                    if (target.AddComponent(addType) == null)
                    {
                        throw new InvalidOperationException($"Could not add component '{addType.FullName}'.");
                    }
                    break;
                case "remove_component":
                    var removeType = ResolveComponentType(input.componentType);
                    if (removeType == typeof(Transform))
                    {
                        throw new InvalidOperationException("Transform cannot be removed.");
                    }

                    var components = target.GetComponents(removeType);
                    if (input.componentIndex < 0 || input.componentIndex >= components.Length)
                    {
                        throw new InvalidOperationException($"Component index {input.componentIndex} was not found.");
                    }

                    UnityEngine.Object.DestroyImmediate(components[input.componentIndex]);
                    break;
                case "set_property":
                    SetProperty(target, input);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported prefab edit kind '{kind}'.");
            }
        }

        private static void SetProperty(GameObject target, PrefabEditItemInput input)
        {
            var type = ResolveComponentType(input.componentType);
            var components = target.GetComponents(type);
            if (input.componentIndex < 0 || input.componentIndex >= components.Length)
            {
                throw new InvalidOperationException($"Component '{type.FullName}' index {input.componentIndex} was not found.");
            }

            var propertyPath = (input.propertyPath ?? string.Empty).Trim();
            if (propertyPath.Length == 0 || propertyPath.Length > 512 || propertyPath == "m_Script")
            {
                throw new InvalidOperationException("propertyPath is invalid or attempts to replace m_Script.");
            }

            var serializedObject = new SerializedObject(components[input.componentIndex]);
            var property = serializedObject.FindProperty(propertyPath);
            if (property == null)
            {
                throw new InvalidOperationException($"Serialized property '{propertyPath}' was not found.");
            }

            ApplySerializedValue(property, input.value ?? new SceneSerializedValueInput());
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ApplySerializedValue(SerializedProperty property, SceneSerializedValueInput input)
        {
            switch (Normalize(input.kind))
            {
                case "bool" when property.propertyType == SerializedPropertyType.Boolean:
                    property.boolValue = input.boolValue;
                    return;
                case "integer" when property.propertyType == SerializedPropertyType.Integer || property.propertyType == SerializedPropertyType.LayerMask:
                    property.longValue = input.integerValue;
                    return;
                case "number" when property.propertyType == SerializedPropertyType.Float:
                    property.doubleValue = input.numberValue;
                    return;
                case "string" when property.propertyType == SerializedPropertyType.String:
                    property.stringValue = input.stringValue ?? string.Empty;
                    return;
                case "color" when property.propertyType == SerializedPropertyType.Color:
                    property.colorValue = new Color(input.x, input.y, input.z, input.w);
                    return;
                case "vector2" when property.propertyType == SerializedPropertyType.Vector2:
                    property.vector2Value = new Vector2(input.x, input.y);
                    return;
                case "vector3" when property.propertyType == SerializedPropertyType.Vector3:
                    property.vector3Value = new Vector3(input.x, input.y, input.z);
                    return;
                case "vector4" when property.propertyType == SerializedPropertyType.Vector4:
                    property.vector4Value = new Vector4(input.x, input.y, input.z, input.w);
                    return;
                case "enum" when property.propertyType == SerializedPropertyType.Enum:
                    property.enumValueIndex = ResolveEnumIndex(property, input);
                    return;
                case "object_reference" when property.propertyType == SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = AssetDatabase.LoadMainAssetAtPath(NormalizeAssetPath(input.assetPath));
                    return;
                case "null" when property.propertyType == SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = null;
                    return;
                default:
                    throw new InvalidOperationException($"Value kind '{input.kind}' is not compatible with '{property.propertyType}'.");
            }
        }

        private static int ResolveEnumIndex(SerializedProperty property, SceneSerializedValueInput input)
        {
            if (!string.IsNullOrWhiteSpace(input.enumName))
            {
                for (var index = 0; index < property.enumNames.Length; index++)
                {
                    if (string.Equals(property.enumNames[index], input.enumName.Trim(), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.enumDisplayNames[index], input.enumName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }
            }

            if (input.enumIndex >= 0 && input.enumIndex < property.enumNames.Length)
            {
                return input.enumIndex;
            }

            throw new InvalidOperationException("Enum value is outside the valid range.");
        }

        private static bool Validate(PrefabAssetInput input, string action, out string error)
        {
            if (!Actions.Contains(action))
            {
                error = "action must be save_scene_object, create_variant, edit_asset, apply_overrides, or revert_overrides.";
                return false;
            }

            if ((action == "save_scene_object" || action == "edit_asset") && !IsSafePrefabPath(input.prefabPath))
            {
                error = "prefabPath must be a .prefab path under Assets.";
                return false;
            }

            if (action == "create_variant" && (!IsSafePrefabPath(input.prefabPath) || !IsSafePrefabPath(input.targetPath) || input.prefabPath == input.targetPath))
            {
                error = "create_variant requires distinct safe prefabPath and targetPath values.";
                return false;
            }

            if ((action == "save_scene_object" || action == "apply_overrides" || action == "revert_overrides") && string.IsNullOrWhiteSpace(input.sceneObjectPath))
            {
                error = "sceneObjectPath is required.";
                return false;
            }

            if (action == "edit_asset")
            {
                var operations = input.operations ?? Array.Empty<PrefabEditItemInput>();
                if (operations.Length == 0 || operations.Length > 50 || operations.Any(operation => operation == null || !EditKinds.Contains(Normalize(operation.kind))))
                {
                    error = "edit_asset requires 1-50 supported operations.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static PrefabAssetResult VerifiedResult(string action, string prefabPath, string sceneObjectPath, int count, string checkpointId, string message)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var verified = prefab != null && PrefabUtility.GetPrefabAssetType(prefab) != PrefabAssetType.NotAPrefab;
            return new PrefabAssetResult
            {
                applied = verified,
                action = action,
                prefabPath = prefabPath,
                sceneObjectPath = sceneObjectPath,
                prefabAssetType = prefab != null ? PrefabUtility.GetPrefabAssetType(prefab).ToString() : string.Empty,
                appliedOperationCount = count,
                checkpointId = checkpointId,
                message = verified ? message : "Prefab operation completed, but asset verification failed.",
                verificationStatus = verified ? "passed" : "failed",
                verificationSignals = verified
                    ? new[] { "checkpoint_created", "prefab_mutation_verified" }
                    : new[] { "checkpoint_created" },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static CheckpointManifest CheckpointAsset(string path, string label)
        {
            return DurableCheckpointStore.CreateInternal(label, new[] { path, path + ".meta" });
        }

        private static Scene RequireSavedActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrWhiteSpace(scene.path))
            {
                throw new InvalidOperationException("The active scene must be saved before managing prefab overrides.");
            }

            return scene;
        }

        private static GameObject RequirePrefabInstance(string path)
        {
            var target = RequireSceneObject(path);
            var root = PrefabUtility.GetNearestPrefabInstanceRoot(target);
            if (root == null)
            {
                throw new InvalidOperationException($"Scene object '{path}' is not part of a prefab instance.");
            }

            return root;
        }

        private static GameObject RequireSceneObject(string path)
        {
            var normalized = NormalizeHierarchyPath(path);
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == normalized)
                {
                    return root;
                }

                if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
                {
                    var child = root.transform.Find(normalized.Substring(root.name.Length + 1));
                    if (child != null)
                    {
                        return child.gameObject;
                    }
                }
            }

            throw new InvalidOperationException($"Scene object '{normalized}' was not found.");
        }

        private static GameObject RequirePrefabObject(GameObject root, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && path.Contains(".."))
            {
                throw new InvalidOperationException("Prefab object path cannot contain '..'.");
            }

            var normalized = NormalizeHierarchyPath(path);
            if (string.IsNullOrEmpty(normalized) || normalized == root.name)
            {
                return root;
            }

            if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(root.name.Length + 1);
            }

            var transform = root.transform.Find(normalized);
            if (transform == null)
            {
                throw new InvalidOperationException($"Prefab object '{path}' was not found.");
            }

            return transform.gameObject;
        }

        private static Type ResolveComponentType(string rawTypeName)
        {
            var typeName = (rawTypeName ?? string.Empty).Trim();
            if (typeName.Length == 0 || typeName.Length > 256)
            {
                throw new InvalidOperationException("componentType is required.");
            }

            var candidates = new List<Type>();
            var exact = Type.GetType(typeName, false);
            if (exact != null) candidates.Add(exact);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var match = assembly.GetType(typeName, false);
                if (match != null) candidates.Add(match);
                if (!typeName.Contains("."))
                {
                    try
                    {
                        candidates.AddRange(assembly.GetTypes().Where(type => type.Name == typeName));
                    }
                    catch (ReflectionTypeLoadException exception)
                    {
                        candidates.AddRange(exception.Types.Where(type => type != null && type.Name == typeName));
                    }
                }
            }

            var distinct = candidates.Where(type => typeof(Component).IsAssignableFrom(type) && !type.IsAbstract).Distinct().ToArray();
            if (distinct.Length != 1)
            {
                throw new InvalidOperationException(distinct.Length == 0
                    ? $"Component type '{typeName}' was not found."
                    : $"Component type '{typeName}' is ambiguous; use its full name.");
            }

            return distinct[0];
        }

        private static bool IsSafePrefabPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && path.StartsWith("Assets/", StringComparison.Ordinal)
                && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("..")
                && !Path.IsPathRooted(path)
                && path.Length <= 512;
        }

        private static void EnsureAssetParent(string path)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            var parent = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        private static void RequireSafeName(string name)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 80 || value.Contains("/") || value.Contains("\\"))
            {
                throw new InvalidOperationException("GameObject name is invalid.");
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizeAssetPath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static string NormalizeHierarchyPath(string value)
        {
            var path = (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (path.Contains("..") || path.Length > 512)
            {
                return string.Empty;
            }

            return path;
        }

        private static PrefabAssetResult Refused(bool dryRun, string action, string prefabPath, string sceneObjectPath, string message)
        {
            return new PrefabAssetResult
            {
                dryRun = dryRun,
                refused = true,
                action = action,
                prefabPath = prefabPath,
                sceneObjectPath = sceneObjectPath,
                message = message,
                verificationStatus = "failed",
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static PrefabAssetRequest ParseRequest(string requestBody)
        {
            try { return JsonUtility.FromJson<PrefabAssetRequest>(requestBody) ?? new PrefabAssetRequest(); }
            catch { return new PrefabAssetRequest(); }
        }
    }
}
