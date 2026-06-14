using System;
using System.Collections.Generic;
using System.Linq;
using UnityAI.ControlPlane.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class GameplayVector3Input
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public sealed class GameplayTemplateInput
    {
        public string kind;
        public string targetPath;
        public string interactorPath;
        public string interactorTag = "Player";
        public float activationDistance = 2f;

        public float deactivationDistance = 2.5f;
        public GameplayVector3Input openOffset = new() { y = 2.5f };
        public float speed = 3f;
        public bool startsOpen;
        public bool closeWhenOutOfRange = true;

        public string pickupId = "pickup";
        public int value = 1;
        public string collectAction = "deactivate";
        public GameplayVector3Input spinAxis = new() { y = 1f };
        public float spinDegreesPerSecond = 90f;
        public float bobAmplitude = 0.15f;
        public float bobFrequency = 1f;

        public string[] affectedPaths = Array.Empty<string>();
        public string action = "activate";
        public bool oneShot = true;
        public bool revertOnExit;
    }

    [Serializable]
    public sealed class GameplayComposeInput
    {
        public bool dryRun = true;
        public bool confirm;
        public GameplayTemplateInput[] templates = Array.Empty<GameplayTemplateInput>();
    }

    [Serializable]
    public sealed class GameplayComposeRequest
    {
        public GameplayComposeInput input = new();
    }

    [Serializable]
    public sealed class GameplayTemplateResult
    {
        public int index;
        public string kind;
        public string targetPath;
        public string componentType;
        public string interactorPath;
        public string[] affectedPaths = Array.Empty<string>();
        public bool applied;
        public bool verified;
        public string message;
    }

    [Serializable]
    public sealed class GameplayComposeResult
    {
        public bool dryRun;
        public bool applied;
        public bool refused;
        public bool rolledBack;
        public bool requiresConfirmation;
        public string requestId;
        public string correlationId;
        public string scenePath;
        public string checkpointId;
        public int requestedTemplateCount;
        public int appliedTemplateCount;
        public GameplayTemplateResult[] templates = Array.Empty<GameplayTemplateResult>();
        public string message;
        public string verificationStatus;
        public string[] requiredPermissions = Array.Empty<string>();
        public string[] verificationSignals = Array.Empty<string>();
        public UnityAiAuditEvent[] auditEvents = Array.Empty<UnityAiAuditEvent>();
        public bool auditPersisted;
        public string auditLogPath;
        public string timestampUtc;
    }

    public static class GameplayComposeOperation
    {
        private const string Capability = "unity.gameplay.compose";
        private const int MaxTemplates = 20;
        private const int MaxAffectedTargets = 32;

        public static GameplayComposeResult Execute(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var input = request.input ?? new GameplayComposeInput();
            var templates = input.templates ?? Array.Empty<GameplayTemplateInput>();
            var envelope = UnityAiJobStore.ParseEnvelope(requestBody);
            var scene = EditorSceneManager.GetActiveScene();

            if (!scene.IsValid() || string.IsNullOrWhiteSpace(scene.path))
            {
                return BuildResult(
                    envelope,
                    input,
                    false,
                    true,
                    false,
                    false,
                    string.Empty,
                    Array.Empty<GameplayTemplateResult>(),
                    "The active scene must be saved before gameplay templates can create a durable checkpoint.",
                    "refused");
            }

            if (!ValidateTemplates(templates, out var refusal))
            {
                return BuildResult(
                    envelope,
                    input,
                    false,
                    true,
                    false,
                    false,
                    string.Empty,
                    Array.Empty<GameplayTemplateResult>(),
                    refusal,
                    "refused");
            }

            if (input.dryRun)
            {
                var plans = templates.Select((template, index) => BuildPlan(template, index)).ToArray();
                return BuildResult(
                    envelope,
                    input,
                    false,
                    false,
                    false,
                    false,
                    string.Empty,
                    plans,
                    $"DRY RUN: validated {plans.Length} gameplay template(s).",
                    "passed");
            }

            if (!input.confirm)
            {
                return BuildResult(
                    envelope,
                    input,
                    false,
                    false,
                    false,
                    true,
                    string.Empty,
                    Array.Empty<GameplayTemplateResult>(),
                    $"CONFIRMATION REQUIRED: would apply {templates.Length} gameplay template(s) atomically.",
                    "needs_confirmation");
            }

            var checkpoint = DurableCheckpointStore.CreateInternal(
                "gameplay-compose",
                new[] { scene.path, scene.path + ".meta" });
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unity AI Compose Gameplay");
            var results = new List<GameplayTemplateResult>();

            try
            {
                for (var index = 0; index < templates.Length; index++)
                {
                    results.Add(ApplyTemplate(templates[index], index));
                }

                EditorSceneManager.MarkSceneDirty(scene);
                Undo.CollapseUndoOperations(undoGroup);
                return BuildResult(
                    envelope,
                    input,
                    true,
                    false,
                    false,
                    false,
                    checkpoint.checkpointId,
                    results.ToArray(),
                    $"Applied and verified {results.Count} gameplay template(s).",
                    "passed");
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return BuildResult(
                    envelope,
                    input,
                    false,
                    false,
                    true,
                    false,
                    checkpoint.checkpointId,
                    results.ToArray(),
                    $"Gameplay composition failed and all in-memory changes were rolled back: {exception.GetBaseException().Message}",
                    "failed");
            }
        }

        private static GameplayTemplateResult ApplyTemplate(GameplayTemplateInput input, int index)
        {
            var kind = Normalize(input.kind);
            var targetPath = NormalizePath(input.targetPath);
            var target = RequireObject(targetPath);
            var interactor = ResolveInteractor(input.interactorPath);

            switch (kind)
            {
                case "door":
                    return ApplyDoor(input, index, targetPath, target, interactor);
                case "pickup":
                    return ApplyPickup(input, index, targetPath, target, interactor);
                case "activator":
                    return ApplyActivator(input, index, targetPath, target, interactor);
                default:
                    throw new InvalidOperationException($"Unsupported gameplay template '{kind}'.");
            }
        }

        private static GameplayTemplateResult ApplyDoor(
            GameplayTemplateInput input,
            int index,
            string targetPath,
            GameObject target,
            Transform interactor)
        {
            var component = GetOrAddComponent<ProximityDoor>(target);
            component.interactor = interactor;
            component.interactorTag = NormalizeTag(input.interactorTag);
            component.activationDistance = input.activationDistance;
            component.deactivationDistance = input.deactivationDistance;
            component.openLocalOffset = ToVector(input.openOffset);
            component.movementSpeed = input.speed;
            component.startsOpen = input.startsOpen;
            component.closeWhenOutOfRange = input.closeWhenOutOfRange;
            EditorUtility.SetDirty(component);

            var verified = component.interactor == interactor
                && component.interactorTag == NormalizeTag(input.interactorTag)
                && Approximately(component.activationDistance, input.activationDistance)
                && Approximately(component.deactivationDistance, input.deactivationDistance)
                && Vector3.Distance(component.openLocalOffset, ToVector(input.openOffset)) <= 0.0001f
                && Approximately(component.movementSpeed, input.speed)
                && component.startsOpen == input.startsOpen
                && component.closeWhenOutOfRange == input.closeWhenOutOfRange;
            RequireVerified(verified, $"Door template on '{targetPath}' could not be verified.");
            return AppliedResult(index, input, typeof(ProximityDoor), Array.Empty<string>(), "Configured proximity door.");
        }

        private static GameplayTemplateResult ApplyPickup(
            GameplayTemplateInput input,
            int index,
            string targetPath,
            GameObject target,
            Transform interactor)
        {
            var component = GetOrAddComponent<ProximityPickup>(target);
            component.interactor = interactor;
            component.interactorTag = NormalizeTag(input.interactorTag);
            component.activationDistance = input.activationDistance;
            component.pickupId = input.pickupId.Trim();
            component.value = input.value;
            component.collectAction = ParseCollectAction(input.collectAction);
            component.spinAxis = ToVector(input.spinAxis);
            component.spinDegreesPerSecond = input.spinDegreesPerSecond;
            component.bobAmplitude = input.bobAmplitude;
            component.bobFrequency = input.bobFrequency;
            EditorUtility.SetDirty(component);

            var verified = component.interactor == interactor
                && component.interactorTag == NormalizeTag(input.interactorTag)
                && Approximately(component.activationDistance, input.activationDistance)
                && component.pickupId == input.pickupId.Trim()
                && component.value == input.value
                && component.collectAction == ParseCollectAction(input.collectAction)
                && Vector3.Distance(component.spinAxis, ToVector(input.spinAxis)) <= 0.0001f
                && Approximately(component.spinDegreesPerSecond, input.spinDegreesPerSecond)
                && Approximately(component.bobAmplitude, input.bobAmplitude)
                && Approximately(component.bobFrequency, input.bobFrequency);
            RequireVerified(verified, $"Pickup template on '{targetPath}' could not be verified.");
            return AppliedResult(index, input, typeof(ProximityPickup), Array.Empty<string>(), "Configured proximity pickup.");
        }

        private static GameplayTemplateResult ApplyActivator(
            GameplayTemplateInput input,
            int index,
            string targetPath,
            GameObject target,
            Transform interactor)
        {
            var affectedPaths = input.affectedPaths.Select(NormalizePath).ToArray();
            var affectedObjects = affectedPaths.Select(RequireObject).ToArray();
            var component = GetOrAddComponent<ProximityActivator>(target);
            component.interactor = interactor;
            component.interactorTag = NormalizeTag(input.interactorTag);
            component.activationDistance = input.activationDistance;
            component.action = ParseTargetAction(input.action);
            component.oneShot = input.oneShot;
            component.revertOnExit = input.revertOnExit;
            var serializedObject = new SerializedObject(component);
            serializedObject.Update();
            var targetsProperty = serializedObject.FindProperty("targets")
                ?? throw new InvalidOperationException("ProximityActivator.targets is not serialized.");
            targetsProperty.arraySize = affectedObjects.Length;
            for (var targetIndex = 0; targetIndex < affectedObjects.Length; targetIndex++)
            {
                targetsProperty.GetArrayElementAtIndex(targetIndex).objectReferenceValue = affectedObjects[targetIndex];
            }

            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();
            EditorUtility.SetDirty(component);

            var verified = component.interactor == interactor
                && component.interactorTag == NormalizeTag(input.interactorTag)
                && Approximately(component.activationDistance, input.activationDistance)
                && component.targets.SequenceEqual(affectedObjects)
                && component.action == ParseTargetAction(input.action)
                && component.oneShot == input.oneShot
                && component.revertOnExit == input.revertOnExit
                && targetsProperty.arraySize == affectedObjects.Length
                && Enumerable.Range(0, affectedObjects.Length).All(targetIndex =>
                    targetsProperty.GetArrayElementAtIndex(targetIndex).objectReferenceValue == affectedObjects[targetIndex]);
            RequireVerified(verified, $"Activator template on '{targetPath}' could not be verified.");
            return AppliedResult(index, input, typeof(ProximityActivator), affectedPaths, "Configured proximity activator.");
        }

        private static T GetOrAddComponent<T>(GameObject target) where T : Component
        {
            var existing = target.GetComponent<T>();
            if (existing != null)
            {
                Undo.RecordObject(existing, "Unity AI Update Gameplay Component");
                return existing;
            }

            var created = Undo.AddComponent<T>(target);
            if (created == null)
            {
                throw new InvalidOperationException($"Unity could not add component '{typeof(T).FullName}' to '{GetPath(target)}'.");
            }

            return created;
        }

        private static bool ValidateTemplates(GameplayTemplateInput[] templates, out string refusal)
        {
            if (templates.Length == 0 || templates.Length > MaxTemplates)
            {
                refusal = $"templates must contain between 1 and {MaxTemplates} items.";
                return false;
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < templates.Length; index++)
            {
                var template = templates[index];
                if (template == null)
                {
                    refusal = $"templates[{index}] is null.";
                    return false;
                }

                var kind = Normalize(template.kind);
                if (kind != "door" && kind != "pickup" && kind != "activator")
                {
                    refusal = $"templates[{index}].kind must be door, pickup, or activator.";
                    return false;
                }

                var targetPath = NormalizePath(template.targetPath);
                if (!IsSafePath(targetPath) || FindByPath(targetPath) == null)
                {
                    refusal = $"templates[{index}].targetPath must identify an existing GameObject.";
                    return false;
                }

                if (!identities.Add(kind + "\n" + targetPath))
                {
                    refusal = $"templates[{index}] duplicates the {kind} template for '{targetPath}'.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(template.interactorPath))
                {
                    var interactorPath = NormalizePath(template.interactorPath);
                    if (!IsSafePath(interactorPath) || FindByPath(interactorPath) == null)
                    {
                        refusal = $"templates[{index}].interactorPath must identify an existing GameObject.";
                        return false;
                    }
                }
                else if (!IsExistingTag(template.interactorTag))
                {
                    refusal = $"templates[{index}].interactorTag must be an existing Unity tag when interactorPath is omitted.";
                    return false;
                }

                if (!IsFiniteInRange(template.activationDistance, 0.01f, 1000f))
                {
                    refusal = $"templates[{index}].activationDistance must be finite and between 0.01 and 1000.";
                    return false;
                }

                if (kind == "door" && !ValidateDoor(template, index, out refusal))
                {
                    return false;
                }

                if (kind == "pickup" && !ValidatePickup(template, index, out refusal))
                {
                    return false;
                }

                if (kind == "activator" && !ValidateActivator(template, index, out refusal))
                {
                    return false;
                }
            }

            refusal = string.Empty;
            return true;
        }

        private static bool ValidateDoor(GameplayTemplateInput input, int index, out string refusal)
        {
            if (!IsFiniteInRange(input.deactivationDistance, 0.01f, 1000f)
                || !IsFiniteInRange(input.speed, 0.01f, 1000f)
                || !IsBoundedVector(input.openOffset, 10000f))
            {
                refusal = $"templates[{index}] contains invalid door distance, speed, or openOffset values.";
                return false;
            }

            refusal = string.Empty;
            return true;
        }

        private static bool ValidatePickup(GameplayTemplateInput input, int index, out string refusal)
        {
            var pickupId = (input.pickupId ?? string.Empty).Trim();
            if (pickupId.Length == 0 || pickupId.Length > 128
                || input.value < 0 || input.value > 1000000
                || (Normalize(input.collectAction) != "deactivate" && Normalize(input.collectAction) != "destroy")
                || !IsBoundedVector(input.spinAxis, 1000f)
                || !IsFiniteInRange(input.spinDegreesPerSecond, -10000f, 10000f)
                || !IsFiniteInRange(input.bobAmplitude, 0f, 1000f)
                || !IsFiniteInRange(input.bobFrequency, 0f, 1000f))
            {
                refusal = $"templates[{index}] contains invalid pickup id, value, action, spin, or bob settings.";
                return false;
            }

            refusal = string.Empty;
            return true;
        }

        private static bool ValidateActivator(GameplayTemplateInput input, int index, out string refusal)
        {
            var affectedPaths = input.affectedPaths ?? Array.Empty<string>();
            var action = Normalize(input.action);
            if (affectedPaths.Length == 0 || affectedPaths.Length > MaxAffectedTargets)
            {
                refusal = $"templates[{index}].affectedPaths must contain between 1 and {MaxAffectedTargets} targets.";
                return false;
            }

            if (action != "activate" && action != "deactivate" && action != "toggle")
            {
                refusal = $"templates[{index}].action must be activate, deactivate, or toggle.";
                return false;
            }

            foreach (var rawPath in affectedPaths)
            {
                var path = NormalizePath(rawPath);
                if (!IsSafePath(path) || FindByPath(path) == null)
                {
                    refusal = $"templates[{index}].affectedPaths contains a missing or invalid GameObject path.";
                    return false;
                }
            }

            refusal = string.Empty;
            return true;
        }

        private static GameplayTemplateResult BuildPlan(GameplayTemplateInput input, int index)
        {
            var kind = Normalize(input.kind);
            var type = kind switch
            {
                "door" => typeof(ProximityDoor),
                "pickup" => typeof(ProximityPickup),
                _ => typeof(ProximityActivator)
            };
            return new GameplayTemplateResult
            {
                index = index,
                kind = kind,
                targetPath = NormalizePath(input.targetPath),
                componentType = type.FullName,
                interactorPath = NormalizePath(input.interactorPath),
                affectedPaths = kind == "activator"
                    ? (input.affectedPaths ?? Array.Empty<string>()).Select(NormalizePath).ToArray()
                    : Array.Empty<string>(),
                message = $"Would configure {kind} gameplay on '{NormalizePath(input.targetPath)}'."
            };
        }

        private static GameplayTemplateResult AppliedResult(
            int index,
            GameplayTemplateInput input,
            Type componentType,
            string[] affectedPaths,
            string message)
        {
            return new GameplayTemplateResult
            {
                index = index,
                kind = Normalize(input.kind),
                targetPath = NormalizePath(input.targetPath),
                componentType = componentType.FullName,
                interactorPath = NormalizePath(input.interactorPath),
                affectedPaths = affectedPaths,
                applied = true,
                verified = true,
                message = message
            };
        }

        private static GameplayComposeResult BuildResult(
            UnityAiRequestEnvelope envelope,
            GameplayComposeInput input,
            bool applied,
            bool refused,
            bool rolledBack,
            bool requiresConfirmation,
            string checkpointId,
            GameplayTemplateResult[] templateResults,
            string message,
            string verificationStatus)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var effect = applied || rolledBack ? "scene_change" : "report_only";
            var auditEvent = new UnityAiAuditEvent
            {
                timestamp = timestamp,
                capability = Capability,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                message = message,
                effects = applied || rolledBack
                    ? new[] { effect, "write_checkpoint", "write_audit_log" }
                    : new[] { effect, "write_audit_log" }
            };
            var auditPersisted = PersistAudit(auditEvent);
            var signals = new List<string> { "operation_audited", "structured_observation" };
            if (!string.IsNullOrWhiteSpace(checkpointId))
            {
                signals.Add("checkpoint_created");
            }

            if (applied)
            {
                signals.Add("gameplay_template_applied");
                signals.Add("component_state_verified");
                signals.Add("scene_mutation_verified");
            }

            if (rolledBack)
            {
                signals.Add("rollback_verified");
            }

            return new GameplayComposeResult
            {
                dryRun = input.dryRun,
                applied = applied,
                refused = refused,
                rolledBack = rolledBack,
                requiresConfirmation = requiresConfirmation,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                scenePath = EditorSceneManager.GetActiveScene().path,
                checkpointId = checkpointId,
                requestedTemplateCount = input.templates?.Length ?? 0,
                appliedTemplateCount = applied ? templateResults.Count(result => result.applied) : 0,
                templates = templateResults,
                message = message,
                verificationStatus = verificationStatus,
                requiredPermissions = new[] { "read_scenes", "modify_scenes" },
                verificationSignals = signals.ToArray(),
                auditEvents = new[] { auditEvent },
                auditPersisted = auditPersisted,
                auditLogPath = AuditLogStore.AuditLogRelativePath,
                timestampUtc = timestamp
            };
        }

        private static bool PersistAudit(UnityAiAuditEvent auditEvent)
        {
            try
            {
                AuditLogStore.Append(new[] { auditEvent });
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to persist gameplay composition audit event: {exception.Message}");
                return false;
            }
        }

        private static Transform ResolveInteractor(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : RequireObject(NormalizePath(path)).transform;
        }

        private static GameObject RequireObject(string path)
        {
            return FindByPath(path)
                ?? throw new InvalidOperationException($"GameObject '{path}' was not found in the active scene.");
        }

        private static GameObject FindByPath(string path)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            var segments = normalized.Split('/');
            var current = EditorSceneManager.GetActiveScene()
                .GetRootGameObjects()
                .FirstOrDefault(root => root.name == segments[0]);
            for (var index = 1; current != null && index < segments.Length; index++)
            {
                var child = current.transform.Find(segments[index]);
                current = child != null ? child.gameObject : null;
            }

            return current;
        }

        private static string GetPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static bool IsSafePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.StartsWith("/", StringComparison.Ordinal)
                || path.EndsWith("/", StringComparison.Ordinal) || path.Contains("//") || path.Contains(".."))
            {
                return false;
            }

            return path.Split('/').All(segment =>
                !string.IsNullOrWhiteSpace(segment)
                && segment.Length <= 80
                && !segment.Any(char.IsControl));
        }

        private static bool IsExistingTag(string tag)
        {
            var normalized = NormalizeTag(tag);
            return normalized.Length > 0 && Array.IndexOf(InternalEditorUtility.tags, normalized) >= 0;
        }

        private static bool IsBoundedVector(GameplayVector3Input vector, float maximum)
        {
            return vector != null
                && IsFiniteInRange(vector.x, -maximum, maximum)
                && IsFiniteInRange(vector.y, -maximum, maximum)
                && IsFiniteInRange(vector.z, -maximum, maximum);
        }

        private static bool IsFiniteInRange(float value, float minimum, float maximum)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= minimum && value <= maximum;
        }

        private static Vector3 ToVector(GameplayVector3Input value)
        {
            return value == null ? Vector3.zero : new Vector3(value.x, value.y, value.z);
        }

        private static PickupCollectAction ParseCollectAction(string value)
        {
            return Normalize(value) == "destroy" ? PickupCollectAction.Destroy : PickupCollectAction.Deactivate;
        }

        private static ProximityTargetAction ParseTargetAction(string value)
        {
            return Normalize(value) switch
            {
                "deactivate" => ProximityTargetAction.Deactivate,
                "toggle" => ProximityTargetAction.Toggle,
                _ => ProximityTargetAction.Activate
            };
        }

        private static string NormalizeTag(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool Approximately(float left, float right)
        {
            return Mathf.Abs(left - right) <= 0.0001f;
        }

        private static void RequireVerified(bool verified, string message)
        {
            if (!verified)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static GameplayComposeRequest ParseRequest(string body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body)
                    ? new GameplayComposeRequest()
                    : JsonUtility.FromJson<GameplayComposeRequest>(body) ?? new GameplayComposeRequest();
            }
            catch
            {
                return new GameplayComposeRequest();
            }
        }
    }
}
