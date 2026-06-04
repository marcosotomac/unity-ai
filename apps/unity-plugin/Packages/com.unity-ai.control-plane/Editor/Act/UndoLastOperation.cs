using System;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class UndoLastOperationInput
    {
        public bool dryRun = true;
        public bool confirm = false;
    }

    [Serializable]
    public sealed class UndoLastOperationRequest
    {
        public UndoLastOperationInput input = new();
    }

    [Serializable]
    public sealed class UndoLastOperationResult
    {
        public bool dryRun;
        public bool undone;
        public string requestId;
        public string correlationId;
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

    public static class UndoLastOperation
    {
        public static UndoLastOperationResult Execute(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var envelope = ParseEnvelope(requestBody);
            var input = request.input ?? new UndoLastOperationInput();
            var activeScene = EditorSceneManager.GetActiveScene();
            var countBefore = activeScene.IsValid() ? activeScene.GetRootGameObjects().Length : 0;

            if (input.dryRun)
            {
                return BuildResult(
                    envelope: envelope,
                    dryRun: true,
                    undone: false,
                    requiresConfirmation: false,
                    countBefore: countBefore,
                    countAfter: countBefore,
                    effect: "report_only",
                    verificationStatus: "passed",
                    verificationSignals: new[] { "operation_audited", "structured_observation" },
                    auditMessage: "DRY RUN: would undo the last Unity Editor operation.",
                    verificationMessage: "No rollback performed."
                );
            }

            if (!input.confirm)
            {
                return BuildResult(
                    envelope: envelope,
                    dryRun: false,
                    undone: false,
                    requiresConfirmation: true,
                    countBefore: countBefore,
                    countAfter: countBefore,
                    effect: "report_only",
                    verificationStatus: "needs_confirmation",
                    verificationSignals: new[] { "operation_audited", "structured_observation" },
                    auditMessage: "CONFIRMATION REQUIRED: would undo the last Unity Editor operation.",
                    verificationMessage: "No rollback performed because confirm=true was not provided."
                );
            }

            Undo.PerformUndo();
            var countAfterUndo = activeScene.IsValid() ? activeScene.GetRootGameObjects().Length : countBefore;
            var rollbackVerified = countAfterUndo < countBefore;
            EditorSceneManager.MarkSceneDirty(activeScene);

            return BuildResult(
                envelope: envelope,
                dryRun: false,
                undone: rollbackVerified,
                requiresConfirmation: false,
                countBefore: countBefore,
                countAfter: countAfterUndo,
                effect: "scene_change",
                verificationStatus: rollbackVerified ? "passed" : "failed",
                verificationSignals: rollbackVerified
                    ? new[] { "operation_audited", "structured_observation", "rollback_verified" }
                    : new[] { "operation_audited", "structured_observation" },
                auditMessage: rollbackVerified ? "Undid the last Unity Editor operation." : "Unity Undo did not change the root GameObject count.",
                verificationMessage: rollbackVerified ? "Rollback verified by root GameObject count decrease." : "Rollback could not be verified by root GameObject count decrease."
            );
        }

        private static UndoLastOperationResult BuildResult(UnityAiRequestEnvelope envelope, bool dryRun, bool undone, bool requiresConfirmation, int countBefore, int countAfter, string effect, string verificationStatus, string[] verificationSignals, string auditMessage, string verificationMessage)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var auditEvents = new[]
            {
                CreateAuditEvent(timestamp, envelope, auditMessage, effect, true)
            };
            var auditPersisted = PersistAudit(auditEvents);
            var responseAuditEvents = auditPersisted
                ? auditEvents
                : new[] { CreateAuditEvent(timestamp, envelope, auditMessage, effect, false) };

            return new UndoLastOperationResult
            {
                dryRun = dryRun,
                undone = undone,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
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
                auditLogAbsolutePath = AuditLogStore.AuditLogPath,
                timestampUtc = timestamp
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
                UnityEngine.Debug.LogError($"Failed to persist Unity AI audit event: {exception.Message}");
                return false;
            }
        }

        private static UnityAiAuditEvent CreateAuditEvent(string timestamp, UnityAiRequestEnvelope envelope, string message, string effect, bool includeAuditPersistenceEffect)
        {
            return new UnityAiAuditEvent
            {
                timestamp = timestamp,
                capability = "unity.editor.undo_last_operation",
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
                return UnityEngine.JsonUtility.FromJson<UnityAiRequestEnvelope>(requestBody) ?? new UnityAiRequestEnvelope();
            }
            catch
            {
                return new UnityAiRequestEnvelope();
            }
        }

        private static UndoLastOperationRequest ParseRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new UndoLastOperationRequest();
            }

            try
            {
                return UnityEngine.JsonUtility.FromJson<UndoLastOperationRequest>(requestBody) ?? new UndoLastOperationRequest();
            }
            catch
            {
                return new UndoLastOperationRequest();
            }
        }
    }
}
