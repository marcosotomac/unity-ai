using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class SceneAuthoringVector3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public sealed class SceneAuthoringTransformInput
    {
        public SceneAuthoringVector3 position = new();
        public SceneAuthoringVector3 rotationEuler = new();
        public SceneAuthoringVector3 scale = new();
    }

    [Serializable]
    public sealed class SceneUpsertGameObjectInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string name;
        public string path;
        public string primitive = "empty";
        public SceneAuthoringTransformInput transform = new();
        public string tag;
        public string layer;
        public bool active;
        public string mode = "upsert";
    }

    [Serializable]
    public sealed class SceneUpsertGameObjectRequest
    {
        public SceneUpsertGameObjectInput input = new();
    }

    [Serializable]
    public sealed class SceneUpsertGameObjectResult
    {
        public bool dryRun;
        public bool created;
        public bool updated;
        public bool refused;
        public string requestId;
        public string correlationId;
        public string mode;
        public string primitive;
        public string requestedName;
        public string finalName;
        public string objectPath;
        public string scenePath;
        public int rootGameObjectCountBefore;
        public int rootGameObjectCountAfter;
        public string audit;
        public string verification;
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

    public static class SceneUpsertGameObjectOperation
    {
        private const string Capability = "unity.scene.upsert_game_object";
        private const int MaxNameLength = 80;
        private const int MaxPathLength = 512;
        private const int MaxPathDepth = 16;
        private const float MaxPositionMagnitude = 10000f;
        private const float MaxRotationMagnitude = 360000f;
        private const float MaxScaleMagnitude = 1000f;

        public static SceneUpsertGameObjectResult Execute(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var envelope = ParseEnvelope(requestBody);
            var input = request.input ?? new SceneUpsertGameObjectInput();
            var scene = EditorSceneManager.GetActiveScene();
            var countBefore = scene.IsValid() ? scene.GetRootGameObjects().Length : 0;
            var warnings = new List<string>();

            if (!TryBuildSpec(requestBody, input, warnings, out var spec, out var refusal))
            {
                return BuildResult(envelope, input, input.dryRun, false, false, true, false, countBefore, countBefore, string.Empty, refusal, "refused", new[] { "operation_audited", "structured_observation" }, "Refused unsafe scene authoring request.", refusal, warnings);
            }

            var existing = FindByPath(spec.objectPath);
            var exists = existing != null;

            if (spec.mode == "create" && exists)
            {
                return BuildResult(envelope, input, spec.dryRun, false, false, true, false, countBefore, countBefore, spec.objectPath, "Create mode refused because the target GameObject already exists.", "refused", new[] { "operation_audited", "structured_observation" }, "Refused scene authoring create request for existing object.", "Target object already exists.", warnings);
            }

            if (spec.mode == "update" && !exists)
            {
                return BuildResult(envelope, input, spec.dryRun, false, false, true, false, countBefore, countBefore, spec.objectPath, "Update mode refused because the target GameObject does not exist.", "refused", new[] { "operation_audited", "structured_observation" }, "Refused scene authoring update request for missing object.", "Target object is missing.", warnings);
            }

            if (spec.dryRun)
            {
                var plannedAction = exists ? "update" : "create";
                return BuildResult(envelope, input, true, false, false, false, false, countBefore, countBefore, spec.objectPath, $"DRY RUN: would {plannedAction} GameObject '{spec.objectPath}'.", "passed", new[] { "operation_audited", "structured_observation" }, $"Dry-run planned GameObject {plannedAction} for '{spec.objectPath}'.", "No scene mutation performed.", warnings);
            }

            if (!spec.confirm)
            {
                var plannedAction = exists ? "update" : "create";
                return BuildResult(envelope, input, false, false, false, false, true, countBefore, countBefore, spec.objectPath, $"CONFIRMATION REQUIRED: would {plannedAction} GameObject '{spec.objectPath}'.", "needs_confirmation", new[] { "operation_audited", "structured_observation" }, $"Confirmation required before GameObject {plannedAction} for '{spec.objectPath}'.", "No scene mutation performed because confirm=true was not provided.", warnings);
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unity AI Scene Upsert GameObject");

            var target = existing;
            var created = false;

            if (target == null)
            {
                target = CreateTarget(spec.primitive);
                target.name = spec.name;
                Undo.RegisterCreatedObjectUndo(target, "Unity AI Create GameObject");
                AttachToParent(target, spec.parentPath);
                created = true;
            }
            else
            {
                Undo.RegisterFullObjectHierarchyUndo(target, "Unity AI Update GameObject");
            }

            ApplySpec(target, spec, requestBody);
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);

            var countAfter = scene.IsValid() ? scene.GetRootGameObjects().Length : countBefore;
            var verified = FindByPath(spec.objectPath) == target && VerifySpec(target, spec, requestBody);
            var action = created ? "Created" : "Updated";

            return BuildResult(envelope, input, false, created, !created, false, false, countBefore, countAfter, GetGameObjectPath(target), $"{action} GameObject '{GetGameObjectPath(target)}' in the active scene via controlled scene authoring.", verified ? "passed" : "failed", verified ? new[] { "operation_audited", "structured_observation", "scene_mutation_verified" } : new[] { "operation_audited", "structured_observation" }, $"{action} GameObject '{GetGameObjectPath(target)}'.", verified ? "Verified target GameObject exists with requested safe fields." : "Target GameObject write completed but verification did not match requested safe fields.", warnings);
        }

        private static bool TryBuildSpec(string requestBody, SceneUpsertGameObjectInput input, List<string> warnings, out SceneAuthoringSpec spec, out string refusal)
        {
            spec = new SceneAuthoringSpec();
            refusal = string.Empty;

            var name = (input.name ?? string.Empty).Trim();
            if (!IsSafeName(name))
            {
                refusal = "Invalid GameObject name. Names must be non-empty, bounded, and cannot contain slashes, parent traversal, or control characters.";
                return false;
            }

            var primitive = string.IsNullOrWhiteSpace(input.primitive) ? "empty" : input.primitive.Trim().ToLowerInvariant();
            if (!IsAllowedPrimitive(primitive))
            {
                refusal = "Invalid primitive. Allowed values are empty, cube, sphere, capsule, cylinder, plane, and quad.";
                return false;
            }

            var mode = string.IsNullOrWhiteSpace(input.mode) ? "upsert" : input.mode.Trim().ToLowerInvariant();
            if (mode != "create" && mode != "update" && mode != "upsert")
            {
                refusal = "Invalid mode. Allowed values are create, update, and upsert.";
                return false;
            }

            if (!TryBuildPaths(input.path, name, out var parentPath, out var objectPath, out refusal))
            {
                return false;
            }

            if (!TryValidateTransform(requestBody, input.transform, out refusal))
            {
                return false;
            }

            if (!TryValidateTag(input.tag, out var tag, out refusal))
            {
                return false;
            }

            if (!TryValidateLayer(requestBody, input.layer, out var layer, out refusal))
            {
                return false;
            }

            spec.dryRun = input.dryRun;
            spec.confirm = input.confirm;
            spec.name = name;
            spec.parentPath = parentPath;
            spec.objectPath = objectPath;
            spec.primitive = primitive;
            spec.mode = mode;
            spec.transform = input.transform ?? new SceneAuthoringTransformInput();
            spec.tag = tag;
            spec.layer = layer;
            spec.active = input.active;
            return true;
        }

        private static bool TryBuildPaths(string rawPath, string name, out string parentPath, out string objectPath, out string refusal)
        {
            parentPath = string.Empty;
            objectPath = name;
            refusal = string.Empty;

            var path = (rawPath ?? string.Empty).Trim().Replace('\\', '/');
            if (path.Length == 0)
            {
                return true;
            }

            if (path.Length > MaxPathLength || path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal) || path.Contains("//"))
            {
                refusal = "Invalid hierarchy path. Use a relative scene hierarchy path without empty segments.";
                return false;
            }

            var segments = path.Split('/');
            if (segments.Length > MaxPathDepth)
            {
                refusal = "Invalid hierarchy path. Path depth is too large.";
                return false;
            }

            foreach (var segment in segments)
            {
                if (!IsSafeName(segment))
                {
                    refusal = "Invalid hierarchy path. Each segment must be a safe GameObject name.";
                    return false;
                }
            }

            if (segments[segments.Length - 1] == name)
            {
                objectPath = path;
                parentPath = segments.Length > 1 ? string.Join("/", segments, 0, segments.Length - 1) : string.Empty;
                return true;
            }

            parentPath = path;
            objectPath = path + "/" + name;
            return true;
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

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateTransform(string requestBody, SceneAuthoringTransformInput transform, out string refusal)
        {
            refusal = string.Empty;
            transform ??= new SceneAuthoringTransformInput();

            if (HasField(requestBody, "position") && !IsBoundedVector(transform.position, MaxPositionMagnitude))
            {
                refusal = "Invalid transform.position. Values must be finite and within +/-10000.";
                return false;
            }

            if (HasField(requestBody, "rotationEuler") && !IsBoundedVector(transform.rotationEuler, MaxRotationMagnitude))
            {
                refusal = "Invalid transform.rotationEuler. Values must be finite and within +/-360000.";
                return false;
            }

            if (HasField(requestBody, "scale") && (!IsBoundedVector(transform.scale, MaxScaleMagnitude) || transform.scale.x == 0f || transform.scale.y == 0f || transform.scale.z == 0f))
            {
                refusal = "Invalid transform.scale. Values must be finite, non-zero, and within +/-1000.";
                return false;
            }

            return true;
        }

        private static bool IsBoundedVector(SceneAuthoringVector3 vector, float maxAbs)
        {
            return vector != null
                && IsFinite(vector.x) && IsFinite(vector.y) && IsFinite(vector.z)
                && Mathf.Abs(vector.x) <= maxAbs
                && Mathf.Abs(vector.y) <= maxAbs
                && Mathf.Abs(vector.z) <= maxAbs;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool TryValidateTag(string inputTag, out string tag, out string refusal)
        {
            tag = null;
            refusal = string.Empty;

            if (string.IsNullOrWhiteSpace(inputTag))
            {
                return true;
            }

            var requested = inputTag.Trim();
            if (Array.IndexOf(InternalEditorUtility.tags, requested) < 0)
            {
                refusal = "Invalid tag. The tag must already exist in the Unity project; this operation does not create tags.";
                return false;
            }

            tag = requested;
            return true;
        }

        private static bool TryValidateLayer(string requestBody, string inputLayer, out int? layer, out string refusal)
        {
            layer = null;
            refusal = string.Empty;

            if (!HasField(requestBody, "layer"))
            {
                return true;
            }

            var rawNumber = Regex.Match(requestBody, "\\\"layer\\\"\\s*:\\s*(\\d+)");
            if (rawNumber.Success)
            {
                if (int.TryParse(rawNumber.Groups[1].Value, out var numericLayer) && numericLayer >= 0 && numericLayer <= 31 && !string.IsNullOrWhiteSpace(LayerMask.LayerToName(numericLayer)))
                {
                    layer = numericLayer;
                    return true;
                }

                refusal = "Invalid layer. Numeric layers must refer to an existing named Unity layer.";
                return false;
            }

            var requested = (inputLayer ?? string.Empty).Trim();
            var namedLayer = LayerMask.NameToLayer(requested);
            if (namedLayer < 0)
            {
                refusal = "Invalid layer. The layer must already exist in the Unity project; this operation does not create layers.";
                return false;
            }

            layer = namedLayer;
            return true;
        }

        private static bool IsAllowedPrimitive(string primitive)
        {
            switch (primitive)
            {
                case "empty":
                case "cube":
                case "sphere":
                case "capsule":
                case "cylinder":
                case "plane":
                case "quad":
                    return true;
                default:
                    return false;
            }
        }

        private static GameObject CreateTarget(string primitive)
        {
            switch (primitive)
            {
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
                    return new GameObject();
            }
        }

        private static void AttachToParent(GameObject target, string parentPath)
        {
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return;
            }

            var parent = EnsureParentPath(parentPath);
            target.transform.SetParent(parent.transform, false);
        }

        private static GameObject EnsureParentPath(string parentPath)
        {
            var currentPath = string.Empty;
            GameObject current = null;

            foreach (var segment in parentPath.Split('/'))
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? segment : currentPath + "/" + segment;
                var existing = FindByPath(currentPath);
                if (existing != null)
                {
                    current = existing;
                    continue;
                }

                var created = new GameObject(segment);
                Undo.RegisterCreatedObjectUndo(created, "Unity AI Create Parent GameObject");
                if (current != null)
                {
                    created.transform.SetParent(current.transform, false);
                }

                current = created;
            }

            return current;
        }

        private static void ApplySpec(GameObject target, SceneAuthoringSpec spec, string requestBody)
        {
            target.name = spec.name;

            if (HasField(requestBody, "position"))
            {
                target.transform.localPosition = ToUnityVector(spec.transform.position);
            }

            if (HasField(requestBody, "rotationEuler"))
            {
                target.transform.localEulerAngles = ToUnityVector(spec.transform.rotationEuler);
            }

            if (HasField(requestBody, "scale"))
            {
                target.transform.localScale = ToUnityVector(spec.transform.scale);
            }

            if (spec.tag != null)
            {
                target.tag = spec.tag;
            }

            if (spec.layer.HasValue)
            {
                target.layer = spec.layer.Value;
            }

            if (HasField(requestBody, "active"))
            {
                target.SetActive(spec.active);
            }
        }

        private static bool VerifySpec(GameObject target, SceneAuthoringSpec spec, string requestBody)
        {
            if (target == null || target.name != spec.name)
            {
                return false;
            }

            if (HasField(requestBody, "position") && !Approximately(target.transform.localPosition, ToUnityVector(spec.transform.position)))
            {
                return false;
            }

            if (HasField(requestBody, "scale") && !Approximately(target.transform.localScale, ToUnityVector(spec.transform.scale)))
            {
                return false;
            }

            if (spec.tag != null && target.tag != spec.tag)
            {
                return false;
            }

            if (spec.layer.HasValue && target.layer != spec.layer.Value)
            {
                return false;
            }

            if (HasField(requestBody, "active") && target.activeSelf != spec.active)
            {
                return false;
            }

            return true;
        }

        private static bool Approximately(Vector3 left, Vector3 right)
        {
            return Mathf.Approximately(left.x, right.x) && Mathf.Approximately(left.y, right.y) && Mathf.Approximately(left.z, right.z);
        }

        private static Vector3 ToUnityVector(SceneAuthoringVector3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path.Split('/');
            var roots = EditorSceneManager.GetActiveScene().GetRootGameObjects();
            GameObject current = null;

            foreach (var root in roots)
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

        private static bool HasField(string json, string field)
        {
            return !string.IsNullOrEmpty(json) && json.Contains("\"" + field + "\"");
        }

        private static SceneUpsertGameObjectResult BuildResult(UnityAiRequestEnvelope envelope, SceneUpsertGameObjectInput input, bool dryRun, bool created, bool updated, bool refused, bool requiresConfirmation, int countBefore, int countAfter, string objectPath, string auditMessage, string verificationStatus, string[] verificationSignals, string eventMessage, string verificationMessage, List<string> warnings)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var effect = created || updated ? "scene_change" : "report_only";
            var auditEvents = new[]
            {
                CreateAuditEvent(timestamp, envelope, eventMessage, effect, true)
            };
            var auditPersisted = PersistAudit(auditEvents);
            var responseAuditEvents = auditPersisted
                ? auditEvents
                : new[] { CreateAuditEvent(timestamp, envelope, eventMessage, effect, false) };
            var scene = EditorSceneManager.GetActiveScene();
            var requestedName = (input.name ?? string.Empty).Trim();

            return new SceneUpsertGameObjectResult
            {
                dryRun = dryRun,
                created = created,
                updated = updated,
                refused = refused,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                mode = string.IsNullOrWhiteSpace(input.mode) ? "upsert" : input.mode.Trim().ToLowerInvariant(),
                primitive = string.IsNullOrWhiteSpace(input.primitive) ? "empty" : input.primitive.Trim().ToLowerInvariant(),
                requestedName = requestedName,
                finalName = requestedName,
                objectPath = objectPath,
                scenePath = scene.path,
                rootGameObjectCountBefore = countBefore,
                rootGameObjectCountAfter = countAfter,
                audit = auditMessage,
                verification = verificationMessage,
                auditEvents = responseAuditEvents,
                verificationSignals = verificationSignals,
                verificationStatus = verificationStatus,
                requiresConfirmation = requiresConfirmation,
                requiredPermissions = new[] { "modify_scenes" },
                auditPersisted = auditPersisted,
                auditLogPath = AuditLogStore.AuditLogRelativePath,
                timestampUtc = timestamp,
                warnings = warnings.ToArray()
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

        private static UnityAiAuditEvent CreateAuditEvent(string timestamp, UnityAiRequestEnvelope envelope, string message, string effect, bool includeAuditPersistenceEffect)
        {
            return new UnityAiAuditEvent
            {
                timestamp = timestamp,
                capability = Capability,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                message = message,
                effects = includeAuditPersistenceEffect ? new[] { effect, "write_audit_log" } : new[] { effect }
            };
        }

        private static UnityAiRequestEnvelope ParseEnvelope(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new UnityAiRequestEnvelope();
            }

            try
            {
                return JsonUtility.FromJson<UnityAiRequestEnvelope>(requestBody) ?? new UnityAiRequestEnvelope();
            }
            catch
            {
                return new UnityAiRequestEnvelope();
            }
        }

        private static SceneUpsertGameObjectRequest ParseRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new SceneUpsertGameObjectRequest();
            }

            try
            {
                return JsonUtility.FromJson<SceneUpsertGameObjectRequest>(requestBody) ?? new SceneUpsertGameObjectRequest();
            }
            catch
            {
                return new SceneUpsertGameObjectRequest();
            }
        }

        private sealed class SceneAuthoringSpec
        {
            public bool dryRun;
            public bool confirm;
            public bool active;
            public string name;
            public string parentPath;
            public string objectPath;
            public string primitive;
            public string mode;
            public string tag;
            public int? layer;
            public SceneAuthoringTransformInput transform = new();
        }
    }
}
