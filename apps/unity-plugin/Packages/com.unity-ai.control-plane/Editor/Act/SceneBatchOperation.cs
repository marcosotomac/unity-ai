using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class SceneSerializedValueInput
    {
        public string kind;
        public bool boolValue;
        public long integerValue;
        public double numberValue;
        public string stringValue;
        public float x;
        public float y;
        public float z;
        public float w;
        public float x2;
        public float y2;
        public float z2;
        public string assetPath;
        public string enumName;
        public int enumIndex;
    }

    [Serializable]
    public sealed class SceneBatchItemInput
    {
        public string kind;
        public string targetPath;
        public string name;
        public string parentPath;
        public string primitive = "empty";
        public string prefabPath;
        public bool worldPositionStays = true;
        public bool active;
        public string componentType;
        public int componentIndex;
        public string propertyPath;
        public SceneSerializedValueInput value = new();
    }

    [Serializable]
    public sealed class SceneBatchInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public SceneBatchItemInput[] operations = Array.Empty<SceneBatchItemInput>();
    }

    [Serializable]
    public sealed class SceneBatchRequest
    {
        public SceneBatchInput input = new();
    }

    [Serializable]
    public sealed class SceneBatchItemResult
    {
        public int index;
        public string kind;
        public string targetPath;
        public string outputPath;
        public bool applied;
        public bool verified;
        public string message;
    }

    [Serializable]
    public sealed class SceneBatchResult
    {
        public bool dryRun;
        public bool applied;
        public bool refused;
        public bool rolledBack;
        public string requestId;
        public string correlationId;
        public int requestedOperationCount;
        public int appliedOperationCount;
        public string scenePath;
        public string audit;
        public string verification;
        public SceneBatchItemResult[] operations;
        public UnityAiAuditEvent[] auditEvents;
        public string[] verificationSignals;
        public string verificationStatus;
        public bool requiresConfirmation;
        public string[] requiredPermissions;
        public bool auditPersisted;
        public string auditLogPath;
        public string timestampUtc;
        public string[] warnings;
    }

    public static class SceneBatchOperation
    {
        private const string Capability = "unity.scene.batch";
        private const int MaxOperations = 50;
        private const int MaxNameLength = 80;
        private const int MaxPathLength = 512;
        private const int MaxPathDepth = 24;

        private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
        {
            "create",
            "instantiate_prefab",
            "delete",
            "duplicate",
            "rename",
            "reparent",
            "set_active",
            "add_component",
            "remove_component",
            "set_property"
        };

        public static SceneBatchResult Execute(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var envelope = ParseEnvelope(requestBody);
            var input = request.input ?? new SceneBatchInput();
            var operations = input.operations ?? Array.Empty<SceneBatchItemInput>();
            var warnings = new List<string>();

            if (!TryValidateBatch(operations, out var refusal))
            {
                return BuildResult(envelope, input, false, true, false, false, Array.Empty<SceneBatchItemResult>(), refusal, "refused", "Scene batch request was refused before mutation.", warnings);
            }

            if (input.dryRun)
            {
                var planned = operations.Select((operation, index) => new SceneBatchItemResult
                {
                    index = index,
                    kind = Normalize(operation.kind),
                    targetPath = NormalizePath(operation.targetPath),
                    outputPath = PredictOutputPath(operation),
                    applied = false,
                    verified = false,
                    message = DescribePlan(operation)
                }).ToArray();

                return BuildResult(envelope, input, false, false, false, false, planned, $"DRY RUN: validated {operations.Length} scene operation(s).", "passed", "No scene mutation performed.", warnings);
            }

            if (!input.confirm)
            {
                return BuildResult(envelope, input, false, false, false, true, Array.Empty<SceneBatchItemResult>(), $"CONFIRMATION REQUIRED: would apply {operations.Length} scene operation(s) atomically.", "needs_confirmation", "No scene mutation performed because confirm=true was not provided.", warnings);
            }

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return BuildResult(envelope, input, false, true, false, false, Array.Empty<SceneBatchItemResult>(), "No valid active scene is available.", "refused", "Scene batch request was refused before mutation.", warnings);
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unity AI Scene Batch");
            var results = new List<SceneBatchItemResult>();

            try
            {
                for (var index = 0; index < operations.Length; index++)
                {
                    results.Add(ApplyOperation(operations[index], index));
                }

                Undo.CollapseUndoOperations(undoGroup);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                results.Add(new SceneBatchItemResult
                {
                    index = results.Count,
                    kind = results.Count < operations.Length ? Normalize(operations[results.Count].kind) : string.Empty,
                    targetPath = results.Count < operations.Length ? NormalizePath(operations[results.Count].targetPath) : string.Empty,
                    applied = false,
                    verified = false,
                    message = exception.Message
                });

                return BuildResult(envelope, input, false, false, true, false, results.ToArray(), $"Scene batch failed and rolled back: {exception.Message}", "failed", "All mutations in this batch were reverted through Unity Undo.", warnings);
            }

            return BuildResult(envelope, input, true, false, false, false, results.ToArray(), $"Applied {results.Count} scene operation(s) atomically.", "passed", "Every operation was applied and verified before the Undo group was committed.", warnings);
        }

        private static SceneBatchItemResult ApplyOperation(SceneBatchItemInput input, int index)
        {
            var kind = Normalize(input.kind);
            var targetPath = NormalizePath(input.targetPath);

            switch (kind)
            {
                case "create":
                    return Create(input, index);
                case "instantiate_prefab":
                    return InstantiatePrefab(input, index);
                case "delete":
                    return Delete(input, index, targetPath);
                case "duplicate":
                    return Duplicate(input, index, targetPath);
                case "rename":
                    return Rename(input, index, targetPath);
                case "reparent":
                    return Reparent(input, index, targetPath);
                case "set_active":
                    return SetActive(input, index, targetPath);
                case "add_component":
                    return AddComponent(input, index, targetPath);
                case "remove_component":
                    return RemoveComponent(input, index, targetPath);
                case "set_property":
                    return SetProperty(input, index, targetPath);
                default:
                    throw new InvalidOperationException($"Unsupported scene batch operation '{kind}'.");
            }
        }

        private static SceneBatchItemResult Create(SceneBatchItemInput input, int index)
        {
            var name = RequireSafeName(input.name, "create.name");
            var parent = ResolveOptionalParent(input.parentPath);
            var outputPath = CombinePath(parent != null ? GetGameObjectPath(parent) : string.Empty, name);

            if (FindByPath(outputPath) != null)
            {
                throw new InvalidOperationException($"Cannot create '{outputPath}' because it already exists.");
            }

            var primitive = Normalize(input.primitive);
            var created = CreatePrimitive(primitive);
            created.name = name;
            Undo.RegisterCreatedObjectUndo(created, "Unity AI Create GameObject");

            if (parent != null)
            {
                created.transform.SetParent(parent.transform, false);
            }

            var verified = FindByPath(outputPath) == created;
            RequireVerified(verified, $"Created GameObject '{outputPath}' could not be verified.");
            return ItemResult(index, input, outputPath, $"Created {primitive} GameObject '{outputPath}'.");
        }

        private static SceneBatchItemResult InstantiatePrefab(SceneBatchItemInput input, int index)
        {
            var prefabPath = RequireAssetPath(input.prefabPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Prefab asset was not found at '{prefabPath}'.");
            }

            var parent = ResolveOptionalParent(input.parentPath);
            var instance = PrefabUtility.InstantiatePrefab(prefab, EditorSceneManager.GetActiveScene()) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException($"Unity could not instantiate prefab '{prefabPath}'.");
            }

            Undo.RegisterCreatedObjectUndo(instance, "Unity AI Instantiate Prefab");
            if (parent != null)
            {
                instance.transform.SetParent(parent.transform, false);
            }

            if (!string.IsNullOrWhiteSpace(input.name))
            {
                instance.name = RequireSafeName(input.name, "instantiate_prefab.name");
            }

            var outputPath = GetGameObjectPath(instance);
            var verified = FindByPath(outputPath) == instance
                && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance) == prefabPath;
            RequireVerified(verified, $"Prefab instance '{outputPath}' could not be verified.");
            return ItemResult(index, input, outputPath, $"Instantiated prefab '{prefabPath}' as '{outputPath}'.");
        }

        private static SceneBatchItemResult Delete(SceneBatchItemInput input, int index, string targetPath)
        {
            var target = RequireTarget(targetPath);
            Undo.DestroyObjectImmediate(target);
            var verified = FindByPath(targetPath) == null;
            RequireVerified(verified, $"Deleted GameObject '{targetPath}' is still present.");
            return ItemResult(index, input, string.Empty, $"Deleted GameObject '{targetPath}'.");
        }

        private static SceneBatchItemResult Duplicate(SceneBatchItemInput input, int index, string targetPath)
        {
            var source = RequireTarget(targetPath);
            var parent = string.IsNullOrWhiteSpace(input.parentPath)
                ? source.transform.parent != null ? source.transform.parent.gameObject : null
                : ResolveOptionalParent(input.parentPath);
            var name = string.IsNullOrWhiteSpace(input.name)
                ? source.name + " Copy"
                : RequireSafeName(input.name, "duplicate.name");
            var outputPath = CombinePath(parent != null ? GetGameObjectPath(parent) : string.Empty, name);

            if (FindByPath(outputPath) != null)
            {
                throw new InvalidOperationException($"Cannot duplicate to '{outputPath}' because it already exists.");
            }

            var duplicate = UnityEngine.Object.Instantiate(source);
            duplicate.name = name;
            Undo.RegisterCreatedObjectUndo(duplicate, "Unity AI Duplicate GameObject");
            duplicate.transform.SetParent(parent != null ? parent.transform : null, input.worldPositionStays);

            var verified = FindByPath(outputPath) == duplicate;
            RequireVerified(verified, $"Duplicated GameObject '{outputPath}' could not be verified.");
            return ItemResult(index, input, outputPath, $"Duplicated '{targetPath}' to '{outputPath}'.");
        }

        private static SceneBatchItemResult Rename(SceneBatchItemInput input, int index, string targetPath)
        {
            var target = RequireTarget(targetPath);
            var name = RequireSafeName(input.name, "rename.name");
            var parentPath = target.transform.parent != null ? GetGameObjectPath(target.transform.parent.gameObject) : string.Empty;
            var outputPath = CombinePath(parentPath, name);
            var collision = FindByPath(outputPath);

            if (collision != null && collision != target)
            {
                throw new InvalidOperationException($"Cannot rename to '{outputPath}' because it already exists.");
            }

            Undo.RecordObject(target, "Unity AI Rename GameObject");
            target.name = name;
            var verified = FindByPath(outputPath) == target && (outputPath == targetPath || FindByPath(targetPath) == null);
            RequireVerified(verified, $"Renamed GameObject '{outputPath}' could not be verified.");
            return ItemResult(index, input, outputPath, $"Renamed '{targetPath}' to '{outputPath}'.");
        }

        private static SceneBatchItemResult Reparent(SceneBatchItemInput input, int index, string targetPath)
        {
            var target = RequireTarget(targetPath);
            var parent = ResolveOptionalParent(input.parentPath);

            if (parent == target || (parent != null && parent.transform.IsChildOf(target.transform)))
            {
                throw new InvalidOperationException("A GameObject cannot be parented to itself or one of its descendants.");
            }

            var outputPath = CombinePath(parent != null ? GetGameObjectPath(parent) : string.Empty, target.name);
            var collision = FindByPath(outputPath);
            if (collision != null && collision != target)
            {
                throw new InvalidOperationException($"Cannot reparent to '{outputPath}' because it already exists.");
            }

            Undo.SetTransformParent(target.transform, parent != null ? parent.transform : null, "Unity AI Reparent GameObject");
            if (!input.worldPositionStays)
            {
                target.transform.localPosition = Vector3.zero;
                target.transform.localRotation = Quaternion.identity;
                target.transform.localScale = Vector3.one;
            }

            var verified = FindByPath(outputPath) == target;
            RequireVerified(verified, $"Reparented GameObject '{outputPath}' could not be verified.");
            return ItemResult(index, input, outputPath, $"Reparented '{targetPath}' to '{outputPath}'.");
        }

        private static SceneBatchItemResult SetActive(SceneBatchItemInput input, int index, string targetPath)
        {
            var target = RequireTarget(targetPath);
            Undo.RecordObject(target, "Unity AI Set GameObject Active");
            target.SetActive(input.active);
            RequireVerified(target.activeSelf == input.active, $"Active state for '{targetPath}' could not be verified.");
            return ItemResult(index, input, targetPath, $"Set active={input.active.ToString().ToLowerInvariant()} on '{targetPath}'.");
        }

        private static SceneBatchItemResult AddComponent(SceneBatchItemInput input, int index, string targetPath)
        {
            var target = RequireTarget(targetPath);
            var type = ResolveComponentType(input.componentType);
            if (type == typeof(Transform))
            {
                throw new InvalidOperationException("Transform cannot be added because every GameObject already owns one.");
            }

            var before = target.GetComponents(type).Length;
            var component = Undo.AddComponent(target, type);
            if (component == null)
            {
                throw new InvalidOperationException($"Unity could not add component '{type.FullName}' to '{targetPath}'.");
            }

            var after = target.GetComponents(type).Length;
            RequireVerified(after == before + 1, $"Added component '{type.FullName}' could not be verified.");
            return ItemResult(index, input, targetPath, $"Added component '{type.FullName}' to '{targetPath}'.");
        }

        private static SceneBatchItemResult RemoveComponent(SceneBatchItemInput input, int index, string targetPath)
        {
            var target = RequireTarget(targetPath);
            var type = ResolveComponentType(input.componentType);
            if (type == typeof(Transform))
            {
                throw new InvalidOperationException("Transform cannot be removed.");
            }

            var components = target.GetComponents(type);
            var component = RequireComponent(components, input.componentIndex, type, targetPath);
            var before = components.Length;
            Undo.DestroyObjectImmediate(component);
            var after = target.GetComponents(type).Length;
            RequireVerified(after == before - 1, $"Removed component '{type.FullName}' could not be verified.");
            return ItemResult(index, input, targetPath, $"Removed component '{type.FullName}' index {input.componentIndex} from '{targetPath}'.");
        }

        private static SceneBatchItemResult SetProperty(SceneBatchItemInput input, int index, string targetPath)
        {
            var target = RequireTarget(targetPath);
            var type = ResolveComponentType(input.componentType);
            var component = RequireComponent(target.GetComponents(type), input.componentIndex, type, targetPath);
            var propertyPath = (input.propertyPath ?? string.Empty).Trim();

            if (propertyPath.Length == 0 || propertyPath.Length > 512 || propertyPath == "m_Script")
            {
                throw new InvalidOperationException("propertyPath must be a bounded serialized property path and cannot replace m_Script.");
            }

            var serializedObject = new SerializedObject(component);
            serializedObject.Update();
            var property = serializedObject.FindProperty(propertyPath);
            if (property == null)
            {
                throw new InvalidOperationException($"Serialized property '{propertyPath}' was not found on '{type.FullName}'.");
            }

            Undo.RecordObject(component, "Unity AI Set Serialized Property");
            ApplySerializedValue(property, input.value ?? new SceneSerializedValueInput());
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();

            var verifiedProperty = serializedObject.FindProperty(propertyPath);
            var verified = verifiedProperty != null && SerializedValueMatches(verifiedProperty, input.value ?? new SceneSerializedValueInput());
            RequireVerified(verified, $"Serialized property '{propertyPath}' on '{type.FullName}' could not be verified.");
            EditorUtility.SetDirty(component);
            return ItemResult(index, input, targetPath, $"Set '{propertyPath}' on component '{type.FullName}' index {input.componentIndex} at '{targetPath}'.");
        }

        private static void ApplySerializedValue(SerializedProperty property, SceneSerializedValueInput input)
        {
            var kind = Normalize(input.kind);

            switch (kind)
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
                case "vector2_int" when property.propertyType == SerializedPropertyType.Vector2Int:
                    property.vector2IntValue = new Vector2Int((int)input.x, (int)input.y);
                    return;
                case "vector3_int" when property.propertyType == SerializedPropertyType.Vector3Int:
                    property.vector3IntValue = new Vector3Int((int)input.x, (int)input.y, (int)input.z);
                    return;
                case "rect" when property.propertyType == SerializedPropertyType.Rect:
                    property.rectValue = new Rect(input.x, input.y, input.z, input.w);
                    return;
                case "rect_int" when property.propertyType == SerializedPropertyType.RectInt:
                    property.rectIntValue = new RectInt((int)input.x, (int)input.y, (int)input.z, (int)input.w);
                    return;
                case "bounds" when property.propertyType == SerializedPropertyType.Bounds:
                    property.boundsValue = new Bounds(new Vector3(input.x, input.y, input.z), new Vector3(input.x2, input.y2, input.z2));
                    return;
                case "bounds_int" when property.propertyType == SerializedPropertyType.BoundsInt:
                    property.boundsIntValue = new BoundsInt(new Vector3Int((int)input.x, (int)input.y, (int)input.z), new Vector3Int((int)input.x2, (int)input.y2, (int)input.z2));
                    return;
                case "quaternion" when property.propertyType == SerializedPropertyType.Quaternion:
                    property.quaternionValue = new Quaternion(input.x, input.y, input.z, input.w);
                    return;
                case "enum" when property.propertyType == SerializedPropertyType.Enum:
                    property.enumValueIndex = ResolveEnumIndex(property, input);
                    return;
                case "object_reference" when property.propertyType == SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = LoadObjectReference(input.assetPath);
                    return;
                case "null" when property.propertyType == SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = null;
                    return;
                case "array_size" when property.propertyType == SerializedPropertyType.ArraySize:
                    property.intValue = checked((int)input.integerValue);
                    return;
                case "character" when property.propertyType == SerializedPropertyType.Character:
                    property.intValue = checked((int)input.integerValue);
                    return;
                default:
                    throw new InvalidOperationException($"Value kind '{kind}' is not compatible with serialized property type '{property.propertyType}'.");
            }
        }

        private static bool SerializedValueMatches(SerializedProperty property, SceneSerializedValueInput input)
        {
            const float tolerance = 0.0001f;
            var kind = Normalize(input.kind);

            switch (kind)
            {
                case "bool":
                    return property.boolValue == input.boolValue;
                case "integer":
                    return property.longValue == input.integerValue;
                case "number":
                    return Math.Abs(property.doubleValue - input.numberValue) <= tolerance;
                case "string":
                    return property.stringValue == (input.stringValue ?? string.Empty);
                case "color":
                    return Approximately(property.colorValue, new Color(input.x, input.y, input.z, input.w), tolerance);
                case "vector2":
                    return Vector2.Distance(property.vector2Value, new Vector2(input.x, input.y)) <= tolerance;
                case "vector3":
                    return Vector3.Distance(property.vector3Value, new Vector3(input.x, input.y, input.z)) <= tolerance;
                case "vector4":
                    return Vector4.Distance(property.vector4Value, new Vector4(input.x, input.y, input.z, input.w)) <= tolerance;
                case "vector2_int":
                    return property.vector2IntValue == new Vector2Int((int)input.x, (int)input.y);
                case "vector3_int":
                    return property.vector3IntValue == new Vector3Int((int)input.x, (int)input.y, (int)input.z);
                case "rect":
                    return property.rectValue == new Rect(input.x, input.y, input.z, input.w);
                case "rect_int":
                    return property.rectIntValue == new RectInt((int)input.x, (int)input.y, (int)input.z, (int)input.w);
                case "bounds":
                    var bounds = new Bounds(new Vector3(input.x, input.y, input.z), new Vector3(input.x2, input.y2, input.z2));
                    return Vector3.Distance(property.boundsValue.center, bounds.center) <= tolerance && Vector3.Distance(property.boundsValue.size, bounds.size) <= tolerance;
                case "bounds_int":
                    return property.boundsIntValue == new BoundsInt(new Vector3Int((int)input.x, (int)input.y, (int)input.z), new Vector3Int((int)input.x2, (int)input.y2, (int)input.z2));
                case "quaternion":
                    return Quaternion.Angle(property.quaternionValue, new Quaternion(input.x, input.y, input.z, input.w)) <= tolerance;
                case "enum":
                    return property.enumValueIndex == ResolveEnumIndex(property, input);
                case "object_reference":
                    return property.objectReferenceValue == LoadObjectReference(input.assetPath);
                case "null":
                    return property.objectReferenceValue == null;
                case "array_size":
                case "character":
                    return property.intValue == checked((int)input.integerValue);
                default:
                    return false;
            }
        }

        private static int ResolveEnumIndex(SerializedProperty property, SceneSerializedValueInput input)
        {
            if (!string.IsNullOrWhiteSpace(input.enumName))
            {
                var name = input.enumName.Trim();
                for (var index = 0; index < property.enumNames.Length; index++)
                {
                    if (string.Equals(property.enumNames[index], name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.enumDisplayNames[index], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }

                throw new InvalidOperationException($"Enum value '{name}' is not valid for '{property.propertyPath}'.");
            }

            if (input.enumIndex < 0 || input.enumIndex >= property.enumNames.Length)
            {
                throw new InvalidOperationException($"Enum index {input.enumIndex} is outside the valid range for '{property.propertyPath}'.");
            }

            return input.enumIndex;
        }

        private static UnityEngine.Object LoadObjectReference(string rawPath)
        {
            var path = RequireAssetPath(rawPath);
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                throw new InvalidOperationException($"Object reference asset was not found at '{path}'.");
            }

            return asset;
        }

        private static bool TryValidateBatch(SceneBatchItemInput[] operations, out string refusal)
        {
            refusal = string.Empty;
            if (operations.Length == 0 || operations.Length > MaxOperations)
            {
                refusal = $"operations must contain between 1 and {MaxOperations} items.";
                return false;
            }

            for (var index = 0; index < operations.Length; index++)
            {
                var operation = operations[index];
                if (operation == null)
                {
                    refusal = $"operations[{index}] is null.";
                    return false;
                }

                var kind = Normalize(operation.kind);
                if (!AllowedKinds.Contains(kind))
                {
                    refusal = $"operations[{index}].kind '{kind}' is not supported.";
                    return false;
                }

                if (kind != "create" && kind != "instantiate_prefab" && !IsSafeScenePath(operation.targetPath, false))
                {
                    refusal = $"operations[{index}].targetPath is not a safe scene hierarchy path.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(operation.parentPath) && !IsSafeScenePath(operation.parentPath, false))
                {
                    refusal = $"operations[{index}].parentPath is not a safe scene hierarchy path.";
                    return false;
                }

                if ((kind == "create" || kind == "rename") && !IsSafeName((operation.name ?? string.Empty).Trim()))
                {
                    refusal = $"operations[{index}].name is not a safe GameObject name.";
                    return false;
                }

                if ((kind == "duplicate" || kind == "instantiate_prefab")
                    && !string.IsNullOrWhiteSpace(operation.name)
                    && !IsSafeName(operation.name.Trim()))
                {
                    refusal = $"operations[{index}].name is not a safe GameObject name.";
                    return false;
                }

                if (kind == "create" && !IsAllowedPrimitive(Normalize(operation.primitive)))
                {
                    refusal = $"operations[{index}].primitive is not supported.";
                    return false;
                }

                if ((kind == "add_component" || kind == "remove_component" || kind == "set_property")
                    && string.IsNullOrWhiteSpace(operation.componentType))
                {
                    refusal = $"operations[{index}].componentType is required.";
                    return false;
                }

                if (operation.componentIndex < 0)
                {
                    refusal = $"operations[{index}].componentIndex cannot be negative.";
                    return false;
                }

                if (kind == "instantiate_prefab" && !IsSafeAssetPath(operation.prefabPath))
                {
                    refusal = $"operations[{index}].prefabPath must be a safe Assets/ or Packages/ path.";
                    return false;
                }
            }

            return true;
        }

        private static GameObject ResolveOptionalParent(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return null;
            }

            return RequireTarget(NormalizePath(rawPath));
        }

        private static GameObject RequireTarget(string path)
        {
            var target = FindByPath(path);
            if (target == null)
            {
                throw new InvalidOperationException($"GameObject '{path}' was not found in the active scene.");
            }

            return target;
        }

        private static Component RequireComponent(Component[] components, int index, Type type, string targetPath)
        {
            if (index < 0 || index >= components.Length || components[index] == null)
            {
                throw new InvalidOperationException($"Component '{type.FullName}' index {index} was not found on '{targetPath}'.");
            }

            return components[index];
        }

        private static Type ResolveComponentType(string rawTypeName)
        {
            var typeName = (rawTypeName ?? string.Empty).Trim();
            if (typeName.Length == 0 || typeName.Length > 256)
            {
                throw new InvalidOperationException("componentType must be a non-empty bounded type name.");
            }

            var exact = Type.GetType(typeName, false);
            if (IsComponentType(exact))
            {
                return exact;
            }

            var matches = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var assemblyMatch = assembly.GetType(typeName, false);
                if (IsComponentType(assemblyMatch))
                {
                    matches.Add(assemblyMatch);
                    continue;
                }

                if (typeName.Contains("."))
                {
                    continue;
                }

                try
                {
                    matches.AddRange(assembly.GetTypes().Where(type => type.Name == typeName && IsComponentType(type)));
                }
                catch (ReflectionTypeLoadException exception)
                {
                    matches.AddRange(exception.Types.Where(type => type != null && type.Name == typeName && IsComponentType(type)));
                }
            }

            matches = matches.Distinct().ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException($"Component type name '{typeName}' is ambiguous. Use the full namespace-qualified type name.");
            }

            throw new InvalidOperationException($"Component type '{typeName}' was not found or is not a Unity Component.");
        }

        private static bool IsComponentType(Type type)
        {
            return type != null && !type.IsAbstract && typeof(Component).IsAssignableFrom(type);
        }

        private static GameObject CreatePrimitive(string primitive)
        {
            switch (primitive)
            {
                case "empty":
                    return new GameObject();
                case "cube":
                    return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere":
                    return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "capsule":
                    return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                case "cylinder":
                    return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "plane":
                    return GameObject.CreatePrimitive(PrimitiveType.Plane);
                case "quad":
                    return GameObject.CreatePrimitive(PrimitiveType.Quad);
                default:
                    throw new InvalidOperationException($"Primitive '{primitive}' is not supported.");
            }
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path.Split('/');
            GameObject current = null;

            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == segments[0])
                {
                    current = root;
                    break;
                }
            }

            if (current == null)
            {
                return null;
            }

            for (var index = 1; index < segments.Length; index++)
            {
                var child = current.transform.Find(segments[index]);
                if (child == null)
                {
                    return null;
                }

                current = child.gameObject;
            }

            return current;
        }

        private static string GetGameObjectPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static SceneBatchItemResult ItemResult(int index, SceneBatchItemInput input, string outputPath, string message)
        {
            return new SceneBatchItemResult
            {
                index = index,
                kind = Normalize(input.kind),
                targetPath = NormalizePath(input.targetPath),
                outputPath = outputPath,
                applied = true,
                verified = true,
                message = message
            };
        }

        private static SceneBatchResult BuildResult(UnityAiRequestEnvelope envelope, SceneBatchInput input, bool applied, bool refused, bool rolledBack, bool requiresConfirmation, SceneBatchItemResult[] operationResults, string auditMessage, string verificationStatus, string verificationMessage, List<string> warnings)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var effect = applied || rolledBack ? "scene_change" : "report_only";
            var auditEvents = new[] { CreateAuditEvent(timestamp, envelope, auditMessage, effect, true) };
            var auditPersisted = PersistAudit(auditEvents);
            var responseEvents = auditPersisted
                ? auditEvents
                : new[] { CreateAuditEvent(timestamp, envelope, auditMessage, effect, false) };
            var verificationSignals = new List<string> { "operation_audited", "structured_observation" };
            if (applied)
            {
                verificationSignals.Add("scene_mutation_verified");
                verificationSignals.Add("batch_applied");

                if (input.operations != null && input.operations.Any(IsComponentOperation))
                {
                    verificationSignals.Add("component_state_verified");
                }
            }

            return new SceneBatchResult
            {
                dryRun = input.dryRun,
                applied = applied,
                refused = refused,
                rolledBack = rolledBack,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                requestedOperationCount = input.operations != null ? input.operations.Length : 0,
                appliedOperationCount = applied ? operationResults.Count(result => result.applied) : 0,
                scenePath = EditorSceneManager.GetActiveScene().path,
                audit = auditMessage,
                verification = verificationMessage,
                operations = operationResults,
                auditEvents = responseEvents,
                verificationSignals = verificationSignals.ToArray(),
                verificationStatus = verificationStatus,
                requiresConfirmation = requiresConfirmation,
                requiredPermissions = new[] { "read_assets", "modify_scenes" },
                auditPersisted = auditPersisted,
                auditLogPath = AuditLogStore.AuditLogRelativePath,
                timestampUtc = timestamp,
                warnings = warnings.ToArray()
            };
        }

        private static UnityAiAuditEvent CreateAuditEvent(string timestamp, UnityAiRequestEnvelope envelope, string message, string effect, bool includePersistence)
        {
            return new UnityAiAuditEvent
            {
                timestamp = timestamp,
                capability = Capability,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                message = message,
                effects = includePersistence ? new[] { effect, "write_audit_log" } : new[] { effect }
            };
        }

        private static bool PersistAudit(UnityAiAuditEvent[] auditEvents)
        {
            try
            {
                AuditLogStore.Append(auditEvents);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to persist Unity AI audit event: {exception.Message}");
                return false;
            }
        }

        private static string DescribePlan(SceneBatchItemInput input)
        {
            var kind = Normalize(input.kind);
            var target = NormalizePath(input.targetPath);
            var output = PredictOutputPath(input);
            return string.IsNullOrEmpty(output)
                ? $"Would {kind} '{target}'."
                : $"Would {kind} '{target}' with result '{output}'.";
        }

        private static string PredictOutputPath(SceneBatchItemInput input)
        {
            var kind = Normalize(input.kind);
            switch (kind)
            {
                case "create":
                    return CombinePath(NormalizePath(input.parentPath), (input.name ?? string.Empty).Trim());
                case "instantiate_prefab":
                    return CombinePath(NormalizePath(input.parentPath), string.IsNullOrWhiteSpace(input.name) ? "[prefab-name]" : input.name.Trim());
                case "rename":
                    return CombinePath(GetParentPath(NormalizePath(input.targetPath)), (input.name ?? string.Empty).Trim());
                case "reparent":
                    return CombinePath(NormalizePath(input.parentPath), GetLeafName(NormalizePath(input.targetPath)));
                case "duplicate":
                    return CombinePath(
                        string.IsNullOrWhiteSpace(input.parentPath) ? GetParentPath(NormalizePath(input.targetPath)) : NormalizePath(input.parentPath),
                        string.IsNullOrWhiteSpace(input.name) ? GetLeafName(NormalizePath(input.targetPath)) + " Copy" : input.name.Trim()
                    );
                case "delete":
                    return string.Empty;
                default:
                    return NormalizePath(input.targetPath);
            }
        }

        private static string RequireSafeName(string rawName, string field)
        {
            var name = (rawName ?? string.Empty).Trim();
            if (!IsSafeName(name))
            {
                throw new InvalidOperationException($"{field} is not a safe GameObject name.");
            }

            return name;
        }

        private static string RequireAssetPath(string rawPath)
        {
            var path = NormalizePath(rawPath);
            if (!IsSafeAssetPath(path))
            {
                throw new InvalidOperationException("Asset path must be a relative Assets/ or Packages/ path without parent traversal.");
            }

            return path;
        }

        private static bool IsSafeAssetPath(string rawPath)
        {
            var path = NormalizePath(rawPath);
            return path.Length > 0
                && path.Length <= MaxPathLength
                && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                && !path.Contains("..")
                && !path.Contains("//")
                && !path.EndsWith("/", StringComparison.Ordinal);
        }

        private static bool IsSafeScenePath(string rawPath, bool allowEmpty)
        {
            var path = NormalizePath(rawPath);
            if (path.Length == 0)
            {
                return allowEmpty;
            }

            if (path.Length > MaxPathLength || path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal) || path.Contains("//"))
            {
                return false;
            }

            var segments = path.Split('/');
            return segments.Length <= MaxPathDepth && segments.All(IsSafeName);
        }

        private static bool IsSafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxNameLength || value == "." || value == "..")
            {
                return false;
            }

            if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0)
            {
                return false;
            }

            return value.All(character => !char.IsControl(character));
        }

        private static bool IsAllowedPrimitive(string primitive)
        {
            return primitive == "empty"
                || primitive == "cube"
                || primitive == "sphere"
                || primitive == "capsule"
                || primitive == "cylinder"
                || primitive == "plane"
                || primitive == "quad";
        }

        private static bool IsComponentOperation(SceneBatchItemInput operation)
        {
            var kind = Normalize(operation?.kind);
            return kind == "add_component" || kind == "remove_component" || kind == "set_property";
        }

        private static string CombinePath(string parent, string name)
        {
            return string.IsNullOrEmpty(parent) ? name : parent + "/" + name;
        }

        private static string GetParentPath(string path)
        {
            var index = path.LastIndexOf('/');
            return index >= 0 ? path.Substring(0, index) : string.Empty;
        }

        private static string GetLeafName(string path)
        {
            var index = path.LastIndexOf('/');
            return index >= 0 ? path.Substring(index + 1) : path;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static bool Approximately(Color left, Color right, float tolerance)
        {
            return Math.Abs(left.r - right.r) <= tolerance
                && Math.Abs(left.g - right.g) <= tolerance
                && Math.Abs(left.b - right.b) <= tolerance
                && Math.Abs(left.a - right.a) <= tolerance;
        }

        private static void RequireVerified(bool verified, string message)
        {
            if (!verified)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static UnityAiRequestEnvelope ParseEnvelope(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody)
                    ? new UnityAiRequestEnvelope()
                    : JsonUtility.FromJson<UnityAiRequestEnvelope>(requestBody) ?? new UnityAiRequestEnvelope();
            }
            catch
            {
                return new UnityAiRequestEnvelope();
            }
        }

        private static SceneBatchRequest ParseRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody)
                    ? new SceneBatchRequest()
                    : JsonUtility.FromJson<SceneBatchRequest>(requestBody) ?? new SceneBatchRequest();
            }
            catch
            {
                return new SceneBatchRequest();
            }
        }
    }
}
