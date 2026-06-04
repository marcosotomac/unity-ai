using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class UnityAiAuditEvent
    {
        public string timestamp;
        public string capability;
        public string requestId;
        public string correlationId;
        public string message;
        public string[] effects;
    }

    [Serializable]
    public sealed class UnityAiRequestEnvelope
    {
        public string requestId;
        public string correlationId;
    }

    [Serializable]
    public sealed class CreateEmptyGameObjectInput
    {
        public string name = "Unity AI GameObject";
        public bool dryRun = true;
        public bool confirm = false;
    }

    [Serializable]
    public sealed class CreateEmptyGameObjectRequest
    {
        public CreateEmptyGameObjectInput input = new();
    }

    [Serializable]
    public sealed class CreateEmptyGameObjectResult
    {
        public bool dryRun;
        public bool created;
        public string requestId;
        public string correlationId;
        public string requestedName;
        public string finalName;
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
        public string auditLogAbsolutePath;
        public string timestampUtc;
    }

    public static class CreateEmptyGameObjectOperation
    {
        public static CreateEmptyGameObjectResult Execute(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var envelope = ParseEnvelope(requestBody);
            var input = request.input ?? new CreateEmptyGameObjectInput();
            var activeScene = EditorSceneManager.GetActiveScene();
            var countBefore = activeScene.IsValid() ? activeScene.GetRootGameObjects().Length : 0;
            var requestedName = string.IsNullOrWhiteSpace(input.name) ? "Unity AI GameObject" : input.name.Trim();

            if (input.dryRun)
            {
                var timestamp = DateTime.UtcNow.ToString("O");
                var auditEvents = new[]
                {
                    CreateAuditEvent(timestamp, envelope, $"Dry-run planned empty GameObject creation for '{requestedName}'.", "report_only", true)
                };
                var auditPersisted = PersistAudit(auditEvents);
                var responseAuditEvents = auditPersisted
                    ? auditEvents
                    : new[] { CreateAuditEvent(timestamp, envelope, auditEvents[0].message, "report_only", false) };

                return new CreateEmptyGameObjectResult
                {
                    dryRun = true,
                    created = false,
                    requestId = envelope.requestId,
                    correlationId = envelope.correlationId,
                    requestedName = requestedName,
                    finalName = requestedName,
                    scenePath = activeScene.path,
                    rootGameObjectCountBefore = countBefore,
                    rootGameObjectCountAfter = countBefore,
                    audit = $"DRY RUN: would create an empty GameObject named '{requestedName}' in the active scene.",
                    verification = "No scene mutation performed.",
                    auditEvents = responseAuditEvents,
                    verificationSignals = new[] { "operation_audited", "structured_observation" },
                    verificationStatus = "passed",
                    requiresConfirmation = false,
                    requiredPermissions = new[] { "modify_scenes" },
                    auditPersisted = auditPersisted,
                    auditLogPath = AuditLogStore.AuditLogRelativePath,
                    auditLogAbsolutePath = AuditLogStore.AuditLogPath,
                    timestampUtc = timestamp
                };
            }

            if (!input.confirm)
            {
                var confirmationTimestamp = DateTime.UtcNow.ToString("O");
                var confirmationAuditEvents = new[]
                {
                    CreateAuditEvent(confirmationTimestamp, envelope, $"Confirmation required before creating empty GameObject '{requestedName}'.", "report_only", true)
                };
                var confirmationAuditPersisted = PersistAudit(confirmationAuditEvents);
                var confirmationResponseAuditEvents = confirmationAuditPersisted
                    ? confirmationAuditEvents
                    : new[] { CreateAuditEvent(confirmationTimestamp, envelope, confirmationAuditEvents[0].message, "report_only", false) };

                return new CreateEmptyGameObjectResult
                {
                    dryRun = false,
                    created = false,
                    requestId = envelope.requestId,
                    correlationId = envelope.correlationId,
                    requestedName = requestedName,
                    finalName = requestedName,
                    scenePath = activeScene.path,
                    rootGameObjectCountBefore = countBefore,
                    rootGameObjectCountAfter = countBefore,
                    audit = $"CONFIRMATION REQUIRED: would create an empty GameObject named '{requestedName}' in the active scene.",
                    verification = "No scene mutation performed because confirm=true was not provided.",
                    auditEvents = confirmationResponseAuditEvents,
                    verificationSignals = new[] { "operation_audited", "structured_observation" },
                    verificationStatus = "needs_confirmation",
                    requiresConfirmation = true,
                    requiredPermissions = new[] { "modify_scenes" },
                    auditPersisted = confirmationAuditPersisted,
                    auditLogPath = AuditLogStore.AuditLogRelativePath,
                    auditLogAbsolutePath = AuditLogStore.AuditLogPath,
                    timestampUtc = confirmationTimestamp
                };
            }

            var gameObject = new GameObject(requestedName);
            Undo.RegisterCreatedObjectUndo(gameObject, "Unity AI Create Empty GameObject");
            EditorSceneManager.MarkSceneDirty(activeScene);

            var countAfter = activeScene.IsValid() ? activeScene.GetRootGameObjects().Length : countBefore + 1;
            var mutationVerified = countAfter > countBefore;
            var createdTimestamp = DateTime.UtcNow.ToString("O");
            var createdAuditEvents = new[]
            {
                CreateAuditEvent(createdTimestamp, envelope, $"Created empty GameObject '{gameObject.name}' in the active scene.", "scene_change", true)
            };
            var createdAuditPersisted = PersistAudit(createdAuditEvents);
            var createdResponseAuditEvents = createdAuditPersisted
                ? createdAuditEvents
                : new[] { CreateAuditEvent(createdTimestamp, envelope, createdAuditEvents[0].message, "scene_change", false) };

            return new CreateEmptyGameObjectResult
            {
                dryRun = false,
                created = true,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                requestedName = requestedName,
                finalName = gameObject.name,
                scenePath = activeScene.path,
                rootGameObjectCountBefore = countBefore,
                rootGameObjectCountAfter = countAfter,
                audit = $"Created empty GameObject '{gameObject.name}' in the active scene via controlled Editor operation.",
                verification = mutationVerified ? "Root GameObject count increased." : "Created object but root count did not increase as expected.",
                auditEvents = createdResponseAuditEvents,
                verificationSignals = mutationVerified
                    ? new[] { "operation_audited", "structured_observation", "scene_mutation_verified" }
                    : new[] { "operation_audited", "structured_observation" },
                verificationStatus = mutationVerified ? "passed" : "failed",
                requiresConfirmation = false,
                requiredPermissions = new[] { "modify_scenes" },
                auditPersisted = createdAuditPersisted,
                auditLogPath = AuditLogStore.AuditLogRelativePath,
                auditLogAbsolutePath = AuditLogStore.AuditLogPath,
                timestampUtc = createdTimestamp
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
                capability = "unity.editor.create_empty_game_object",
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

        private static CreateEmptyGameObjectRequest ParseRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new CreateEmptyGameObjectRequest();
            }

            try
            {
                return JsonUtility.FromJson<CreateEmptyGameObjectRequest>(requestBody) ?? new CreateEmptyGameObjectRequest();
            }
            catch
            {
                return new CreateEmptyGameObjectRequest();
            }
        }
    }
}
