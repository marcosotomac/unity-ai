using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ConsoleLogSummary
    {
        public int errorCount;
        public int warningCount;
        public int logCount;
        public List<ConsoleLogEntry> recentEntries = new();
    }

    [Serializable]
    public sealed class ConsoleLogEntry
    {
        public string type;
        public string condition;
        public string stackTrace;
    }

    [Serializable]
    public sealed class ConsoleDiagnosticReport
    {
        public string timestampUtc;
        public int errorCount;
        public int warningCount;
        public int logCount;
        public int totalEntries;
        public int diagnosticCount;
        public bool hasErrors;
        public List<ConsoleDiagnosticEntry> diagnostics = new();
    }

    [Serializable]
    public sealed class ConsoleDiagnosticEntry
    {
        public string category;
        public string severity;
        public string message;
        public string file;
        public int line;
        public string stackHint;
        public string functionHint;
        public string likelyRootCause;
        public string suggestedNextSafeAction;
    }

    [Serializable]
    public sealed class ConsoleFixPlanReport
    {
        public string timestampUtc;
        public int diagnosticCount;
        public int planCount;
        public List<ConsoleFixPlan> plans = new();
        public List<string> verificationSignals = new();
    }

    [Serializable]
    public sealed class ConsoleFixPlan
    {
        public string id;
        public string diagnosticCategory;
        public string severity;
        public string targetFile;
        public int targetLine;
        public string summary;
        public string rationale;
        public string riskLevel;
        public bool canAutoApply;
        public bool requiresConfirmationBeforeApply;
        public List<string> proposedSteps = new();
        public List<string> verificationSteps = new();
        public string rollbackNotes;
    }

    [Serializable]
    public sealed class ConsoleApplyFixInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string targetFile;
        public int targetLine;
        public string expectedOriginalLine;
        public string replacementLine;
        public string expectedDiagnosticCategory;
        public string expectedMessageContains;
        public string planId;
    }

    [Serializable]
    public sealed class ConsoleApplyFixRequest
    {
        public ConsoleApplyFixInput input = new();
    }

    [Serializable]
    public sealed class ConsoleApplyFixResult
    {
        public bool dryRun;
        public bool applied;
        public bool refused;
        public bool requiresConfirmation;
        public string requestId;
        public string correlationId;
        public string targetFile;
        public int targetLine;
        public string audit;
        public string verification;
        public UnityAiAuditEvent[] auditEvents;
        public string[] verificationSignals;
        public string verificationStatus;
        public string[] requiredPermissions;
        public bool auditPersisted;
        public string auditLogPath;
        public string checkpointPath;
        public bool checkpointCreated;
        public string timestampUtc;
    }

    internal sealed class ConsoleEntrySnapshot
    {
        public string type;
        public string message;
        public string stackTrace;
        public string file;
        public int line;
        public int mode;
    }

    [InitializeOnLoad]
    public static class ConsoleLogBridge
    {
        private const int MaxEntries = 100;
        private static readonly int ErrorModeMask = ResolveLogMessageFlags("Error", "Fatal", "Assert", "AssetImportError", "ScriptingError", "ScriptingException", "ScriptCompileError", "ScriptingAssertion", "GraphError", "VisualScriptingError");
        private static readonly int WarningModeMask = ResolveLogMessageFlags("Warning", "AssetImportWarning", "ScriptingWarning", "ScriptCompileWarning");
        private static readonly List<ConsoleLogEntry> RecentEntries = new();

        static ConsoleLogBridge()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceived += OnLogMessageReceived;
        }

        public static ConsoleLogSummary GetSummary()
        {
            var summary = new ConsoleLogSummary();
            TryReadEditorConsoleCounts(summary);
            summary.recentEntries = new List<ConsoleLogEntry>(RecentEntries);
            return summary;
        }

        public static ConsoleDiagnosticReport Diagnose()
        {
            var summary = GetSummary();
            var entries = TryReadEditorConsoleEntries();

            foreach (var recentEntry in RecentEntries)
            {
                var recentSnapshot = new ConsoleEntrySnapshot
                {
                    type = recentEntry.type,
                    message = recentEntry.condition,
                    stackTrace = recentEntry.stackTrace
                };

                if (!ContainsEquivalentEntry(entries, recentSnapshot))
                {
                    entries.Add(recentSnapshot);
                }
            }

            var report = new ConsoleDiagnosticReport
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                errorCount = summary.errorCount,
                warningCount = summary.warningCount,
                logCount = summary.logCount,
                totalEntries = entries.Count
            };

            foreach (var entry in entries)
            {
                var diagnostic = BuildDiagnostic(entry);
                report.diagnostics.Add(diagnostic);

                if (diagnostic.severity == "error")
                {
                    report.hasErrors = true;
                }
            }

            report.diagnosticCount = report.diagnostics.Count;
            return report;
        }

        public static ConsoleFixPlanReport PlanFix()
        {
            var diagnosticReport = Diagnose();
            var report = new ConsoleFixPlanReport
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                diagnosticCount = diagnosticReport.diagnosticCount
            };

            for (var index = 0; index < diagnosticReport.diagnostics.Count; index += 1)
            {
                report.plans.Add(BuildFixPlan(diagnosticReport.diagnostics[index], index + 1));
            }

            report.planCount = report.plans.Count;
            if (report.planCount > 0)
            {
                report.verificationSignals.Add("fix_plan_generated");
            }

            return report;
        }

        public static ConsoleApplyFixResult ApplyFix(string requestBody)
        {
            var request = ParseApplyFixRequest(requestBody);
            var envelope = ParseEnvelope(requestBody);
            var input = request.input ?? new ConsoleApplyFixInput();
            var dryRun = input.dryRun;
            var targetFile = NormalizeApplyFixTargetPath(input.targetFile);
            var validationError = ValidateApplyFixInput(input, targetFile);

            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return BuildApplyFixResult(envelope, dryRun, false, true, false, targetFile, input.targetLine, string.Empty, false, "refused", new[] { "operation_audited", "structured_observation" }, $"REFUSED: {validationError}", "No file mutation performed.", new[] { "report_only" });
            }

            if (!dryRun && !input.confirm)
            {
                return BuildApplyFixResult(envelope, false, false, false, true, targetFile, input.targetLine, string.Empty, false, "needs_confirmation", new[] { "operation_audited", "structured_observation" }, $"CONFIRMATION REQUIRED: would replace one line in {targetFile}:{input.targetLine}.", "No file mutation performed because confirm=true was not provided.", new[] { "report_only" });
            }

            var expectationError = ValidateDiagnosticExpectation(input, targetFile);
            if (!string.IsNullOrWhiteSpace(expectationError))
            {
                return BuildApplyFixResult(envelope, dryRun, false, true, false, targetFile, input.targetLine, string.Empty, false, "refused", new[] { "operation_audited", "structured_observation" }, $"REFUSED: {expectationError}", "No file mutation performed.", new[] { "report_only" });
            }

            var containmentError = TryResolveSafeApplyFixPath(targetFile, out var absolutePath);
            if (!string.IsNullOrWhiteSpace(containmentError))
            {
                return BuildApplyFixResult(envelope, dryRun, false, true, false, targetFile, input.targetLine, string.Empty, false, "refused", new[] { "operation_audited", "structured_observation" }, $"REFUSED: {containmentError}", "No file mutation performed.", new[] { "report_only" });
            }

            var readError = TryReadLine(absolutePath, input.targetLine, out var lines, out var lineEnding, out var currentLine);
            if (!string.IsNullOrWhiteSpace(readError))
            {
                return BuildApplyFixResult(envelope, dryRun, false, true, false, targetFile, input.targetLine, string.Empty, false, "refused", new[] { "operation_audited", "structured_observation" }, $"REFUSED: {readError}", "No file mutation performed.", new[] { "report_only" });
            }

            if (!string.Equals(currentLine, input.expectedOriginalLine ?? string.Empty, StringComparison.Ordinal))
            {
                return BuildApplyFixResult(envelope, dryRun, false, true, false, targetFile, input.targetLine, string.Empty, false, "refused", new[] { "operation_audited", "structured_observation" }, $"REFUSED: target line did not match expectedOriginalLine for {targetFile}:{input.targetLine}.", "No file mutation performed because the file content changed or the request targeted the wrong line.", new[] { "report_only" });
            }

            if (dryRun)
            {
                return BuildApplyFixResult(envelope, true, false, false, false, targetFile, input.targetLine, string.Empty, false, "passed", new[] { "operation_audited", "structured_observation" }, $"DRY RUN: would replace one line in {targetFile}:{input.targetLine}.", "No file mutation performed.", new[] { "report_only" });
            }

            var checkpointPath = CreateCheckpoint(absolutePath, targetFile);
            lines[input.targetLine - 1] = input.replacementLine ?? string.Empty;
            File.WriteAllText(absolutePath, string.Join(lineEnding, lines), DetectUtf8WithBom(absolutePath) ? new System.Text.UTF8Encoding(true) : new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(targetFile);

            var verified = VerifyLine(absolutePath, input.targetLine, input.replacementLine ?? string.Empty);
            return BuildApplyFixResult(
                envelope,
                false,
                verified,
                false,
                false,
                targetFile,
                input.targetLine,
                checkpointPath,
                true,
                verified ? "passed" : "failed",
                verified
                    ? new[] { "operation_audited", "structured_observation", "checkpoint_created", "line_replacement_verified" }
                    : new[] { "operation_audited", "structured_observation", "checkpoint_created" },
                verified ? $"Applied one-line replacement to {targetFile}:{input.targetLine}." : $"Applied one-line replacement to {targetFile}:{input.targetLine}, but verification failed.",
                verified ? "Target line now matches replacementLine." : "Target line did not match replacementLine after writing.",
                new[] { "asset_change", "write_checkpoint" }
            );
        }

        private static ConsoleApplyFixResult BuildApplyFixResult(UnityAiRequestEnvelope envelope, bool dryRun, bool applied, bool refused, bool requiresConfirmation, string targetFile, int targetLine, string checkpointPath, bool checkpointCreated, string verificationStatus, string[] verificationSignals, string auditMessage, string verificationMessage, string[] effects)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var auditEvents = new[]
            {
                CreateApplyFixAuditEvent(timestamp, envelope, auditMessage, effects, true)
            };
            var auditPersisted = PersistAudit(auditEvents);
            var responseAuditEvents = auditPersisted
                ? auditEvents
                : new[] { CreateApplyFixAuditEvent(timestamp, envelope, auditMessage, effects, false) };

            return new ConsoleApplyFixResult
            {
                dryRun = dryRun,
                applied = applied,
                refused = refused,
                requiresConfirmation = requiresConfirmation,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                targetFile = targetFile,
                targetLine = targetLine,
                audit = auditMessage,
                verification = verificationMessage,
                auditEvents = responseAuditEvents,
                verificationSignals = verificationSignals,
                verificationStatus = verificationStatus,
                requiredPermissions = new[] { "modify_assets" },
                auditPersisted = auditPersisted,
                auditLogPath = AuditLogStore.AuditLogRelativePath,
                checkpointPath = checkpointPath,
                checkpointCreated = checkpointCreated,
                timestampUtc = timestamp
            };
        }

        private static string ValidateApplyFixInput(ConsoleApplyFixInput input, string targetFile)
        {
            var originalTargetFile = input.targetFile ?? string.Empty;

            if (string.IsNullOrWhiteSpace(originalTargetFile))
            {
                return "targetFile is required.";
            }

            if (Path.IsPathRooted(originalTargetFile) || originalTargetFile.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(originalTargetFile, @"^[A-Za-z]:[\\/]"))
            {
                return "absolute targetFile paths are not allowed.";
            }

            if (originalTargetFile.Contains("..", StringComparison.Ordinal))
            {
                return "targetFile must not contain '..'.";
            }

            if (!targetFile.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return "targetFile must be under Assets/.";
            }

            if (targetFile.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return "Packages/ edits are not supported.";
            }

            if (!targetFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || targetFile.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return "only Assets/**/*.cs files are supported.";
            }

            if (targetFile.StartsWith("Assets/Generated/", StringComparison.OrdinalIgnoreCase) || targetFile.Contains("/Generated/", StringComparison.OrdinalIgnoreCase))
            {
                return "generated folders are not supported.";
            }

            if (input.targetLine < 1)
            {
                return "targetLine must be 1-based.";
            }

            if (ContainsLineBreak(input.expectedOriginalLine) || ContainsLineBreak(input.replacementLine))
            {
                return "expectedOriginalLine and replacementLine must be single lines.";
            }

            return string.Empty;
        }

        private static string NormalizeApplyFixTargetPath(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
            return Path.IsPathRooted(normalized) || Regex.IsMatch(normalized, @"^[A-Za-z]:[/]") ? string.Empty : normalized;
        }

        private static string TryResolveSafeApplyFixPath(string targetFile, out string absolutePath)
        {
            absolutePath = string.Empty;

            try
            {
                var projectRoot = Path.GetFullPath(GetProjectRoot()).Replace('\\', '/').TrimEnd('/');
                var assetsRoot = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
                var candidate = Path.GetFullPath(Path.Combine(projectRoot, targetFile)).Replace('\\', '/');

                if (!candidate.StartsWith(projectRoot + "/", StringComparison.Ordinal) || !candidate.StartsWith(assetsRoot + "/", StringComparison.Ordinal))
                {
                    return "targetFile resolved outside the Unity project Assets directory.";
                }

                var reparsePointError = ValidateNoReparsePoints(candidate, assetsRoot);
                if (!string.IsNullOrWhiteSpace(reparsePointError))
                {
                    return reparsePointError;
                }

                absolutePath = candidate;
                return string.Empty;
            }
            catch (Exception exception)
            {
                return $"targetFile could not be resolved safely: {exception.Message}";
            }
        }

        private static string ValidateNoReparsePoints(string candidate, string assetsRoot)
        {
            var current = assetsRoot;
            var relative = candidate.Substring(assetsRoot.Length).TrimStart('/');
            var parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                current = Path.Combine(current, part).Replace('\\', '/');
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    continue;
                }

                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    return "targetFile must not resolve through symlinks or reparse points.";
                }
            }

            return string.Empty;
        }

        private static string ValidateDiagnosticExpectation(ConsoleApplyFixInput input, string targetFile)
        {
            var hasCategory = !string.IsNullOrWhiteSpace(input.expectedDiagnosticCategory);
            var hasMessage = !string.IsNullOrWhiteSpace(input.expectedMessageContains);
            var hasPlan = !string.IsNullOrWhiteSpace(input.planId);

            if (!hasCategory && !hasMessage && !hasPlan)
            {
                return string.Empty;
            }

            var category = (input.expectedDiagnosticCategory ?? string.Empty).Trim();
            if (hasCategory && !IsSupportedDiagnosticCategory(category))
            {
                return $"unsupported diagnostic category expectation: {category}.";
            }

            if (hasPlan)
            {
                var planReport = PlanFix();
                var foundPlan = false;
                foreach (var plan in planReport.plans)
                {
                    if (string.Equals(plan.id, input.planId, StringComparison.Ordinal) && string.Equals(plan.targetFile, targetFile, StringComparison.Ordinal) && plan.targetLine == input.targetLine)
                    {
                        foundPlan = !hasCategory || string.Equals(plan.diagnosticCategory, category, StringComparison.Ordinal);
                        break;
                    }
                }

                if (!foundPlan)
                {
                    return "planId expectation did not match the current fix plans.";
                }
            }

            if (!hasCategory && !hasMessage)
            {
                return string.Empty;
            }

            var diagnostics = Diagnose().diagnostics;
            foreach (var diagnostic in diagnostics)
            {
                var matchesCategory = !hasCategory || string.Equals(diagnostic.category, category, StringComparison.Ordinal);
                var matchesMessage = !hasMessage || (diagnostic.message ?? string.Empty).Contains(input.expectedMessageContains, StringComparison.Ordinal);
                var matchesLocation = string.Equals(diagnostic.file, targetFile, StringComparison.Ordinal) && diagnostic.line == input.targetLine;

                if (matchesCategory && matchesMessage && matchesLocation)
                {
                    return string.Empty;
                }
            }

            return "diagnostic expectations did not match current console diagnostics.";
        }

        private static bool IsSupportedDiagnosticCategory(string category)
        {
            switch (category)
            {
                case "compiler_error":
                case "runtime_exception":
                case "import_error":
                case "warning":
                case "unknown":
                    return true;
                default:
                    return false;
            }
        }

        private static string TryReadLine(string absolutePath, int targetLine, out string[] lines, out string lineEnding, out string currentLine)
        {
            lines = Array.Empty<string>();
            lineEnding = "\n";
            currentLine = string.Empty;

            if (!File.Exists(absolutePath))
            {
                return "targetFile does not exist.";
            }

            var text = File.ReadAllText(absolutePath);
            lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            lines = text.Split(new[] { lineEnding }, StringSplitOptions.None);

            if (targetLine > lines.Length || (targetLine == lines.Length && lines[targetLine - 1].Length == 0 && text.EndsWith(lineEnding, StringComparison.Ordinal)))
            {
                return "targetLine is outside the file.";
            }

            currentLine = lines[targetLine - 1];
            return string.Empty;
        }

        private static string CreateCheckpoint(string absolutePath, string targetFile)
        {
            var checkpointRelativeDirectory = "UnityAIArtifacts/Checkpoints";
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            var checkpointName = Regex.Replace(targetFile, @"[^A-Za-z0-9_.-]+", "_");
            var checkpointRelativePath = $"{checkpointRelativeDirectory}/{timestamp}_{checkpointName}.bak";
            var checkpointAbsolutePath = Path.Combine(GetProjectRoot(), checkpointRelativePath);
            var checkpointDirectory = Path.GetDirectoryName(checkpointAbsolutePath);

            if (!string.IsNullOrWhiteSpace(checkpointDirectory))
            {
                Directory.CreateDirectory(checkpointDirectory);
            }

            File.Copy(absolutePath, checkpointAbsolutePath, false);
            return checkpointRelativePath;
        }

        private static bool VerifyLine(string absolutePath, int targetLine, string replacementLine)
        {
            var readError = TryReadLine(absolutePath, targetLine, out _, out _, out var currentLine);
            return string.IsNullOrWhiteSpace(readError) && string.Equals(currentLine, replacementLine, StringComparison.Ordinal);
        }

        private static bool DetectUtf8WithBom(string absolutePath)
        {
            var bytes = File.ReadAllBytes(absolutePath);
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static bool ContainsLineBreak(string value)
        {
            return (value ?? string.Empty).Contains("\n", StringComparison.Ordinal) || (value ?? string.Empty).Contains("\r", StringComparison.Ordinal);
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

        private static UnityAiAuditEvent CreateApplyFixAuditEvent(string timestamp, UnityAiRequestEnvelope envelope, string message, string[] effects, bool includeAuditPersistenceEffect)
        {
            var auditEffects = effects ?? Array.Empty<string>();
            if (includeAuditPersistenceEffect)
            {
                var withAudit = new string[auditEffects.Length + 1];
                Array.Copy(auditEffects, withAudit, auditEffects.Length);
                withAudit[withAudit.Length - 1] = "write_audit_log";
                auditEffects = withAudit;
            }

            return new UnityAiAuditEvent
            {
                timestamp = timestamp,
                capability = "unity.console.apply_fix",
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                message = message,
                effects = auditEffects
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

        private static ConsoleApplyFixRequest ParseApplyFixRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new ConsoleApplyFixRequest();
            }

            try
            {
                return JsonUtility.FromJson<ConsoleApplyFixRequest>(requestBody) ?? new ConsoleApplyFixRequest();
            }
            catch
            {
                return new ConsoleApplyFixRequest();
            }
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            RecentEntries.Add(new ConsoleLogEntry
            {
                type = type.ToString(),
                condition = condition,
                stackTrace = stackTrace
            });

            if (RecentEntries.Count > MaxEntries)
            {
                RecentEntries.RemoveAt(0);
            }
        }

        private static void TryReadEditorConsoleCounts(ConsoleLogSummary summary)
        {
            var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor.dll");
            var getCountsMethod = logEntriesType?.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (getCountsMethod == null)
            {
                return;
            }

            var parameters = new object[] { 0, 0, 0 };
            getCountsMethod.Invoke(null, parameters);

            summary.errorCount = (int)parameters[0];
            summary.warningCount = (int)parameters[1];
            summary.logCount = (int)parameters[2];
        }

        private static List<ConsoleEntrySnapshot> TryReadEditorConsoleEntries()
        {
            var results = new List<ConsoleEntrySnapshot>();
            var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor.dll");
            var logEntryType = Type.GetType("UnityEditor.LogEntry,UnityEditor.dll");
            var startMethod = logEntriesType?.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var endMethod = logEntriesType?.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getCountMethod = logEntriesType?.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getEntryMethod = logEntriesType?.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (logEntryType == null || getCountMethod == null || getEntryMethod == null)
            {
                return results;
            }

            try
            {
                startMethod?.Invoke(null, Array.Empty<object>());
                var count = Math.Min((int)getCountMethod.Invoke(null, Array.Empty<object>()), MaxEntries);

                for (var index = 0; index < count; index += 1)
                {
                    var logEntry = Activator.CreateInstance(logEntryType);
                    getEntryMethod.Invoke(null, new[] { (object)index, logEntry });
                    var mode = ReadIntMember(logEntryType, logEntry, "mode");
                    results.Add(new ConsoleEntrySnapshot
                    {
                        type = TypeFromMode(mode),
                        message = ReadFirstStringMember(logEntryType, logEntry, "condition", "message", "text"),
                        stackTrace = ReadFirstStringMember(logEntryType, logEntry, "stackTrace", "callstack"),
                        file = NormalizeUnityPath(ReadStringMember(logEntryType, logEntry, "file")),
                        line = ReadIntMember(logEntryType, logEntry, "line"),
                        mode = mode
                    });
                }
            }
            catch
            {
                results.Clear();
            }
            finally
            {
                endMethod?.Invoke(null, Array.Empty<object>());
            }

            return results;
        }

        private static bool ContainsEquivalentEntry(List<ConsoleEntrySnapshot> entries, ConsoleEntrySnapshot candidate)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.type, candidate.type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.message, candidate.message, StringComparison.Ordinal) &&
                    string.Equals(entry.stackTrace, candidate.stackTrace, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static ConsoleDiagnosticEntry BuildDiagnostic(ConsoleEntrySnapshot entry)
        {
            var message = SanitizeDiagnosticText(entry.message);
            var stackTrace = SanitizeDiagnosticText(entry.stackTrace);
            var file = !string.IsNullOrWhiteSpace(entry.file) ? entry.file : ExtractUnityPath(message + "\n" + stackTrace);
            var line = entry.line > 0 ? entry.line : ExtractLine(message + "\n" + stackTrace);
            var severity = DetermineSeverity(entry, message);
            var category = ClassifyEntry(severity, message, stackTrace);

            return new ConsoleDiagnosticEntry
            {
                category = category,
                severity = severity,
                message = message,
                file = file,
                line = line,
                stackHint = ExtractStackHint(stackTrace),
                functionHint = ExtractFunctionHint(stackTrace),
                likelyRootCause = SummarizeRootCause(category, message, file, line),
                suggestedNextSafeAction = SuggestNextSafeAction(category, file, line)
            };
        }

        private static ConsoleFixPlan BuildFixPlan(ConsoleDiagnosticEntry diagnostic, int ordinal)
        {
            var target = string.IsNullOrWhiteSpace(diagnostic.file) ? "the reported Unity context" : diagnostic.file;
            var location = FormatLocation(diagnostic.file, diagnostic.line);
            var plan = new ConsoleFixPlan
            {
                id = $"console-fix-plan-{ordinal:000}",
                diagnosticCategory = diagnostic.category,
                severity = diagnostic.severity,
                targetFile = diagnostic.file,
                targetLine = diagnostic.line,
                summary = SummarizeFixPlan(diagnostic, location),
                rationale = BuildFixRationale(diagnostic, location),
                riskLevel = DeterminePlanRisk(diagnostic.category),
                canAutoApply = false,
                requiresConfirmationBeforeApply = true,
                rollbackNotes = "No changes are made by this plan. If a later confirmed apply step edits files or scene data, capture a pre-change snapshot and revert only that confirmed change if verification fails."
            };

            AddProposedSteps(plan, diagnostic, target, location);
            plan.verificationSteps.Add("Run Unity compilation or wait for the Editor compile cycle to finish.");
            plan.verificationSteps.Add("Run unity.console.diagnose again and compare the targeted diagnostic category, file, and line.");
            plan.verificationSteps.Add("Only proceed if diagnostics are reduced or the remaining messages have a new explicit plan.");
            return plan;
        }

        private static string SummarizeFixPlan(ConsoleDiagnosticEntry diagnostic, string location)
        {
            if (diagnostic.category == "compiler_error")
            {
                var codeSummary = SummarizeCSharpCompilerCode(diagnostic.message);
                return string.IsNullOrWhiteSpace(codeSummary)
                    ? $"Inspect the compiler error at {location} and prepare a minimal C# fix plan."
                    : $"Inspect {location}: {codeSummary}";
            }

            switch (diagnostic.category)
            {
                case "runtime_exception":
                    return $"Inspect the first project stack frame for the runtime exception at {location}.";
                case "import_error":
                    return $"Inspect asset and importer metadata for the import error at {location}.";
                case "warning":
                    return $"Defer the warning at {location} unless it blocks verification.";
                default:
                    return $"Collect more context before proposing a change for the console entry at {location}.";
            }
        }

        private static string BuildFixRationale(ConsoleDiagnosticEntry diagnostic, string location)
        {
            switch (diagnostic.category)
            {
                case "compiler_error":
                    return $"Compiler errors block recompilation, but this read-only plan must inspect {location} before proposing any confirmed file edit.";
                case "runtime_exception":
                    return $"Runtime exceptions can depend on scene state and object references, so inspect {location} and reproduce before any confirmed change.";
                case "import_error":
                    return $"Import failures can be caused by asset data, importer settings, or package state; inspect metadata before changing assets.";
                case "warning":
                    return "Warnings are usually non-blocking; prioritize errors and only plan a confirmed fix if verification is blocked.";
                default:
                    return "The diagnostic category is not specific enough for a safe change; gather more structured context first.";
            }
        }

        private static void AddProposedSteps(ConsoleFixPlan plan, ConsoleDiagnosticEntry diagnostic, string target, string location)
        {
            switch (diagnostic.category)
            {
                case "compiler_error":
                    plan.proposedSteps.Add($"Read the target script at {location}; do not edit during planning.");
                    plan.proposedSteps.Add("Identify the smallest C# change that addresses the reported compiler error code and preserves surrounding behavior.");
                    plan.proposedSteps.Add("Prepare a later confirmed apply request for that one targeted change only.");
                    break;
                case "runtime_exception":
                    plan.proposedSteps.Add($"Inspect the first project stack frame and related scene object state for {target}.");
                    plan.proposedSteps.Add("Reproduce the exception path and identify the missing guard, reference, or invalid state.");
                    plan.proposedSteps.Add("Prepare a guarded later apply request only after the failing state is understood.");
                    break;
                case "import_error":
                    plan.proposedSteps.Add($"Inspect the asset path or package context for {target}.");
                    plan.proposedSteps.Add("Read importer metadata and dependencies before proposing asset or settings changes.");
                    plan.proposedSteps.Add("Prepare a later confirmed apply request only if metadata points to a narrow safe change.");
                    break;
                case "warning":
                    plan.proposedSteps.Add("Record the warning and resolve blocking errors first.");
                    plan.proposedSteps.Add("Only inspect the warning further if it blocks verification or points to deprecated API usage in project code.");
                    break;
                default:
                    plan.proposedSteps.Add("Collect more context with project, script, asset, scene, or package inspection.");
                    plan.proposedSteps.Add("Classify the diagnostic before proposing any confirmed change.");
                    break;
            }
        }

        private static string DeterminePlanRisk(string category)
        {
            switch (category)
            {
                case "warning":
                    return "low";
                case "unknown":
                    return "medium";
                default:
                    return "medium";
            }
        }

        private static string SummarizeCSharpCompilerCode(string message)
        {
            var match = Regex.Match(message ?? string.Empty, @"\bCS\d{4}\b", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return string.Empty;
            }

            var code = match.Value.ToUpperInvariant();
            switch (code)
            {
                case "CS0103":
                    return "CS0103 means the referenced name is not in scope; inspect spelling, using directives, fields, and local variables.";
                case "CS0246":
                    return "CS0246 means a type or namespace cannot be found; inspect assembly references, namespaces, and using directives.";
                case "CS1061":
                    return "CS1061 means a member is missing on the target type; inspect the receiver type and available API.";
                case "CS1503":
                    return "CS1503 means an argument type does not match the called API; inspect the call signature and conversion.";
                case "CS1002":
                    return "CS1002 means a semicolon is expected; inspect the nearby statement syntax.";
                case "CS1513":
                    return "CS1513 means a closing brace is expected; inspect the surrounding block structure.";
                default:
                    return $"{code} is a C# compiler error; inspect the referenced script and the smallest syntax or API mismatch near the reported line.";
            }
        }

        private static string FormatLocation(string file, int line)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return "the reported Unity context";
            }

            return line > 0 ? $"{file}:{line}" : file;
        }

        private static string DetermineSeverity(ConsoleEntrySnapshot entry, string message)
        {
            if ((entry.mode & ErrorModeMask) != 0)
            {
                return "error";
            }

            if ((entry.mode & WarningModeMask) != 0)
            {
                return "warning";
            }

            if (string.Equals(entry.type, LogType.Warning.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return "warning";
            }

            if (string.Equals(entry.type, LogType.Error.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.type, LogType.Exception.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.type, LogType.Assert.ToString(), StringComparison.OrdinalIgnoreCase) ||
                message.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                return "error";
            }

            if (message.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                return "warning";
            }

            return "info";
        }

        private static string TypeFromMode(int mode)
        {
            if ((mode & ErrorModeMask) != 0)
            {
                return LogType.Error.ToString();
            }

            if ((mode & WarningModeMask) != 0)
            {
                return LogType.Warning.ToString();
            }

            return LogType.Log.ToString();
        }

        private static int ResolveLogMessageFlags(params string[] names)
        {
            var flagsType = Type.GetType("UnityEditor.LogMessageFlags,UnityEditor.dll");
            if (flagsType == null || !flagsType.IsEnum)
            {
                return 0;
            }

            var mask = 0;
            foreach (var name in names)
            {
                if (Enum.IsDefined(flagsType, name))
                {
                    mask |= Convert.ToInt32(Enum.Parse(flagsType, name));
                }
            }

            return mask;
        }

        private static string ClassifyEntry(string severity, string message, string stackTrace)
        {
            var text = (message + "\n" + stackTrace).Trim();

            if (text.Contains("error CS", StringComparison.OrdinalIgnoreCase) || text.Contains("Compilation failed", StringComparison.OrdinalIgnoreCase))
            {
                return "compiler_error";
            }

            if (text.Contains("Importer", StringComparison.OrdinalIgnoreCase) || text.Contains("failed to import", StringComparison.OrdinalIgnoreCase) || text.Contains("Asset import", StringComparison.OrdinalIgnoreCase))
            {
                return "import_error";
            }

            if (text.Contains("Exception", StringComparison.OrdinalIgnoreCase) || text.Contains("NullReferenceException", StringComparison.OrdinalIgnoreCase))
            {
                return "runtime_exception";
            }

            if (severity == "warning")
            {
                return "warning";
            }

            return "unknown";
        }

        private static string SummarizeRootCause(string category, string message, string file, int line)
        {
            var location = string.IsNullOrWhiteSpace(file) ? string.Empty : $" at {file}{(line > 0 ? ":" + line : string.Empty)}";

            switch (category)
            {
                case "compiler_error":
                    return $"Unity reported a C# compiler error{location}; inspect the referenced script before attempting fixes.";
                case "runtime_exception":
                    return $"Unity reported a runtime exception{location}; inspect the first project stack frame and object state.";
                case "import_error":
                    return $"Unity reported an asset import problem{location}; inspect the asset and importer settings.";
                case "warning":
                    return $"Unity reported a warning{location}; review it after blocking errors are resolved.";
                default:
                    return string.IsNullOrWhiteSpace(message) ? "No actionable console message was available." : "Unity reported a console entry that needs manual classification.";
            }
        }

        private static string SuggestNextSafeAction(string category, string file, int line)
        {
            var location = string.IsNullOrWhiteSpace(file) ? "the reported location" : $"{file}{(line > 0 ? ":" + line : string.Empty)}";

            switch (category)
            {
                case "compiler_error":
                    return $"Read {location}, propose a minimal fix plan, then recompile and rerun console diagnostics before mutating files.";
                case "runtime_exception":
                    return $"Inspect {location} and related scene/object state, propose a guarded fix plan, then reproduce before editing.";
                case "import_error":
                    return $"Inspect {location} and importer metadata; avoid asset changes until the importer failure is understood.";
                case "warning":
                    return $"Record the warning and prioritize errors first; fix only if it blocks verification.";
                default:
                    return "Collect more context with project, asset, script, or scene inspection before proposing changes.";
            }
        }

        private static string ExtractUnityPath(string text)
        {
            var match = Regex.Match(text ?? string.Empty, @"(?:^|[\s\(])((?:Assets|Packages)/[^\s\):]+)");
            return match.Success ? NormalizeUnityPath(match.Groups[1].Value) : string.Empty;
        }

        private static int ExtractLine(string text)
        {
            var match = Regex.Match(text ?? string.Empty, @"(?:Assets|Packages)/[^\s\):]+\((\d+),\d+\)");

            if (match.Success && int.TryParse(match.Groups[1].Value, out var line))
            {
                return line;
            }

            match = Regex.Match(text ?? string.Empty, @":line (\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out line) ? line : 0;
        }

        private static string ExtractStackHint(string stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
            {
                return string.Empty;
            }

            var lines = stackTrace.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = NormalizeUnityPath(line.Trim());
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        private static string ExtractFunctionHint(string stackTrace)
        {
            var hint = ExtractStackHint(stackTrace);
            var index = hint.IndexOf(" (", StringComparison.Ordinal);
            return index > 0 ? hint.Substring(0, index) : hint;
        }

        private static string NormalizeUnityPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace('\\', '/');
            var assetsIndex = normalized.IndexOf("Assets/", StringComparison.Ordinal);
            var packagesIndex = normalized.IndexOf("Packages/", StringComparison.Ordinal);
            var start = -1;

            if (assetsIndex >= 0 && packagesIndex >= 0)
            {
                start = Math.Min(assetsIndex, packagesIndex);
            }
            else if (assetsIndex >= 0)
            {
                start = assetsIndex;
            }
            else if (packagesIndex >= 0)
            {
                start = packagesIndex;
            }

            if (start >= 0)
            {
                return normalized.Substring(start);
            }

            return Path.IsPathRooted(normalized) ? string.Empty : normalized;
        }

        private static string SanitizeDiagnosticText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace('\\', '/');
            var projectRoot = GetProjectRoot();

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                normalized = normalized.Replace(projectRoot + "/", string.Empty);
            }

            normalized = Regex.Replace(normalized, @"(?:[A-Za-z]:)?/[^\s\)\(]*(?=(?:Assets|Packages)/)", string.Empty);
            normalized = Regex.Replace(normalized, @"(^|[\s\(])(?:[A-Za-z]:/[^\s\)\(]+|/[^\s\)\(]+)", match => match.Groups[1].Value + "[absolute-path]");
            return normalized;
        }

        private static string GetProjectRoot()
        {
            try
            {
                var dataDirectory = Directory.GetParent(Application.dataPath);
                return dataDirectory?.FullName.Replace('\\', '/') ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadStringMember(Type type, object instance, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(instance) as string ?? string.Empty;
            }

            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance) as string ?? string.Empty;
        }

        private static string ReadFirstStringMember(Type type, object instance, params string[] names)
        {
            foreach (var name in names)
            {
                var value = ReadStringMember(type, instance, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static int ReadIntMember(Type type, object instance, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = field != null ? field.GetValue(instance) : type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
            return value is int intValue ? intValue : 0;
        }
    }
}
