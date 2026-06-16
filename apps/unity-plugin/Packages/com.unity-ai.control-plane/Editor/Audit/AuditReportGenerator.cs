using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class AuditReportEvidenceInput
    {
        public string phase = "supporting";
        public string kind = "artifact";
        public string path;
        public string description;
    }

    [Serializable]
    public sealed class AuditReportVerificationInput
    {
        public string signal;
        public string status = "inconclusive";
        public string summary;
        public string[] evidencePaths = Array.Empty<string>();
    }

    [Serializable]
    public sealed class AuditReportInput
    {
        public string title = "Unity AI verification report";
        public string summary;
        public string[] requestIds = Array.Empty<string>();
        public string[] correlationIds = Array.Empty<string>();
        public string[] capabilities = Array.Empty<string>();
        public string sinceUtc;
        public string untilUtc;
        public int maxEvents = 200;
        public long maxEvidenceBytes = 2147483648;
        public bool requireBeforeAfter;
        public AuditReportEvidenceInput[] evidence = Array.Empty<AuditReportEvidenceInput>();
        public AuditReportVerificationInput[] verifications = Array.Empty<AuditReportVerificationInput>();
    }

    [Serializable]
    public sealed class AuditReportRequest
    {
        public string requestId;
        public string correlationId;
        public AuditReportInput input = new();
    }

    [Serializable]
    public sealed class AuditReportEvidence
    {
        public string phase;
        public string kind;
        public string path;
        public string description;
        public bool available;
        public long byteLength;
        public string sha256;
        public string lastWriteTimeUtc;
        public string error;
    }

    [Serializable]
    public sealed class AuditReportVerification
    {
        public string signal;
        public string status;
        public string summary;
        public string[] evidencePaths = Array.Empty<string>();
        public bool evidenceReferencesValid;
    }

    [Serializable]
    public sealed class AuditReportDocument
    {
        public string reportId;
        public string generatedAtUtc;
        public string generatedByRequestId;
        public string generatedByCorrelationId;
        public string title;
        public string summary;
        public string status;
        public bool requireBeforeAfter;
        public bool beforeAfterComplete;
        public int totalAuditEvents;
        public int matchedAuditEvents;
        public int malformedAuditLines;
        public bool truncated;
        public string[] requestIds = Array.Empty<string>();
        public string[] correlationIds = Array.Empty<string>();
        public string[] capabilities = Array.Empty<string>();
        public string sinceUtc;
        public string untilUtc;
        public UnityAiAuditEvent[] auditEvents = Array.Empty<UnityAiAuditEvent>();
        public AuditReportEvidence[] evidence = Array.Empty<AuditReportEvidence>();
        public AuditReportVerification[] verifications = Array.Empty<AuditReportVerification>();
    }

    [Serializable]
    public sealed class AuditReportResult
    {
        public bool generated;
        public bool refused;
        public string reportId;
        public string requestId;
        public string correlationId;
        public string title;
        public string status;
        public string message;
        public int totalAuditEvents;
        public int matchedAuditEvents;
        public int malformedAuditLines;
        public bool truncated;
        public bool requireBeforeAfter;
        public bool beforeAfterComplete;
        public AuditReportEvidence[] evidence = Array.Empty<AuditReportEvidence>();
        public AuditReportVerification[] verifications = Array.Empty<AuditReportVerification>();
        public UnityAiAuditEvent[] auditEvents = Array.Empty<UnityAiAuditEvent>();
        public string reportJsonPath;
        public string reportMarkdownPath;
        public string reportJsonSha256;
        public string reportMarkdownSha256;
        public bool reportFilesVerified;
        public bool auditPersisted;
        public UnityAiAuditEvent auditEvent;
        public string auditLogPath;
        public string[] verificationSignals = Array.Empty<string>();
        public string generatedAtUtc;
    }

    public static class AuditReportGenerator
    {
        private const string ArtifactRoot = "UnityAIArtifacts";
        private const string ReportDirectory = "UnityAIArtifacts/Audit/Reports";

        public static AuditReportResult Generate(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var input = request.input ?? new AuditReportInput();
            var generatedAt = DateTime.UtcNow.ToString("O");
            var requestIds = NormalizeValues(input.requestIds, 200);
            var correlationIds = NormalizeValues(input.correlationIds, 200);
            var capabilities = NormalizeValues(input.capabilities, 200);
            var maxEvents = Math.Max(1, Math.Min(input.maxEvents, 5000));
            var maxEvidenceBytes = Math.Max(1, Math.Min(input.maxEvidenceBytes, 10L * 1024 * 1024 * 1024));

            if (!TryParseTimestamp(input.sinceUtc, out var sinceUtc, out var normalizedSince, out var sinceError))
            {
                return Refused(request, sinceError, generatedAt);
            }

            if (!TryParseTimestamp(input.untilUtc, out var untilUtc, out var normalizedUntil, out var untilError))
            {
                return Refused(request, untilError, generatedAt);
            }

            if (sinceUtc.HasValue && untilUtc.HasValue && sinceUtc.Value > untilUtc.Value)
            {
                return Refused(request, "sinceUtc must be earlier than or equal to untilUtc.", generatedAt);
            }

            var allEvents = AuditLogStore.ReadAll(out var malformedLineCount);
            var matched = allEvents
                .Where(auditEvent => MatchesFilters(auditEvent, requestIds, correlationIds, capabilities, sinceUtc, untilUtc))
                .ToArray();
            var truncated = matched.Length > maxEvents;
            var selectedEvents = matched
                .Skip(Math.Max(0, matched.Length - maxEvents))
                .ToArray();

            var evidence = (input.evidence ?? Array.Empty<AuditReportEvidenceInput>())
                .Take(100)
                .Select(item => InspectEvidence(item, maxEvidenceBytes))
                .ToArray();
            var availableEvidencePaths = new HashSet<string>(
                evidence.Where(item => item.available).Select(item => item.path),
                StringComparer.Ordinal);
            var verifications = (input.verifications ?? Array.Empty<AuditReportVerificationInput>())
                .Take(200)
                .Select(item => NormalizeVerification(item, availableEvidencePaths))
                .ToArray();
            var beforeAfterComplete = evidence.Any(item => item.available && item.phase == "before")
                && evidence.Any(item => item.available && item.phase == "after");
            var hasFilters = requestIds.Length > 0 || correlationIds.Length > 0 || capabilities.Length > 0
                || sinceUtc.HasValue || untilUtc.HasValue;
            var status = DetermineStatus(
                evidence,
                verifications,
                input.requireBeforeAfter,
                beforeAfterComplete,
                hasFilters,
                matched.Length);
            var reportId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var title = SanitizeText(input.title, 160, "Unity AI verification report");
            var summary = SanitizeText(input.summary, 4000, string.Empty);
            var document = new AuditReportDocument
            {
                reportId = reportId,
                generatedAtUtc = generatedAt,
                generatedByRequestId = request.requestId,
                generatedByCorrelationId = request.correlationId,
                title = title,
                summary = summary,
                status = status,
                requireBeforeAfter = input.requireBeforeAfter,
                beforeAfterComplete = beforeAfterComplete,
                totalAuditEvents = allEvents.Length,
                matchedAuditEvents = matched.Length,
                malformedAuditLines = malformedLineCount,
                truncated = truncated,
                requestIds = requestIds,
                correlationIds = correlationIds,
                capabilities = capabilities,
                sinceUtc = normalizedSince,
                untilUtc = normalizedUntil,
                auditEvents = selectedEvents,
                evidence = evidence,
                verifications = verifications
            };

            try
            {
                var relativeJsonPath = $"{ReportDirectory}/{reportId}.json";
                var relativeMarkdownPath = $"{ReportDirectory}/{reportId}.md";
                var absoluteJsonPath = ResolveArtifactPath(relativeJsonPath, true);
                var absoluteMarkdownPath = ResolveArtifactPath(relativeMarkdownPath, true);
                File.WriteAllText(absoluteJsonPath, JsonUtility.ToJson(document, true), new UTF8Encoding(false));
                File.WriteAllText(absoluteMarkdownPath, BuildMarkdown(document), new UTF8Encoding(false));

                var jsonHash = ComputeSha256(absoluteJsonPath);
                var markdownHash = ComputeSha256(absoluteMarkdownPath);
                var filesVerified = File.Exists(absoluteJsonPath)
                    && File.Exists(absoluteMarkdownPath)
                    && jsonHash == ComputeSha256(absoluteJsonPath)
                    && markdownHash == ComputeSha256(absoluteMarkdownPath);
                var generationAuditEvent = new UnityAiAuditEvent
                {
                    timestamp = DateTime.UtcNow.ToString("O"),
                    capability = "unity.audit.report",
                    requestId = request.requestId,
                    correlationId = request.correlationId,
                    message = $"Generated audit report '{reportId}' with {matched.Length} matched event(s) and status '{status}'.",
                    effects = new[] { "write_artifacts", "write_audit_log" }
                };
                var auditPersisted = PersistAudit(generationAuditEvent);
                var responseAuditEvent = auditPersisted
                    ? generationAuditEvent
                    : new UnityAiAuditEvent
                    {
                        timestamp = generationAuditEvent.timestamp,
                        capability = generationAuditEvent.capability,
                        requestId = generationAuditEvent.requestId,
                        correlationId = generationAuditEvent.correlationId,
                        message = generationAuditEvent.message,
                        effects = new[] { "write_artifacts" }
                    };
                var signals = new List<string> { "audit_report_generated", "structured_observation" };
                if (evidence.Length > 0 && evidence.All(item => item.available && !string.IsNullOrEmpty(item.sha256)))
                {
                    signals.Add("evidence_hash_verified");
                }

                if (auditPersisted)
                {
                    signals.Add("operation_audited");
                }

                return new AuditReportResult
                {
                    generated = true,
                    reportId = reportId,
                    requestId = request.requestId,
                    correlationId = request.correlationId,
                    title = title,
                    status = status,
                    message = $"Generated JSON and Markdown audit report artifacts with {matched.Length} matched event(s).",
                    totalAuditEvents = allEvents.Length,
                    matchedAuditEvents = matched.Length,
                    malformedAuditLines = malformedLineCount,
                    truncated = truncated,
                    requireBeforeAfter = input.requireBeforeAfter,
                    beforeAfterComplete = beforeAfterComplete,
                    evidence = evidence,
                    verifications = verifications,
                    auditEvents = selectedEvents,
                    reportJsonPath = relativeJsonPath,
                    reportMarkdownPath = relativeMarkdownPath,
                    reportJsonSha256 = jsonHash,
                    reportMarkdownSha256 = markdownHash,
                    reportFilesVerified = filesVerified,
                    auditPersisted = auditPersisted,
                    auditEvent = responseAuditEvent,
                    auditLogPath = AuditLogStore.AuditLogRelativePath.Replace('\\', '/'),
                    verificationSignals = signals.ToArray(),
                    generatedAtUtc = generatedAt
                };
            }
            catch (Exception exception)
            {
                return Refused(request, $"Audit report generation failed: {exception.Message}", generatedAt);
            }
        }

        private static bool MatchesFilters(
            UnityAiAuditEvent auditEvent,
            string[] requestIds,
            string[] correlationIds,
            string[] capabilities,
            DateTime? sinceUtc,
            DateTime? untilUtc)
        {
            if (requestIds.Length > 0 && !requestIds.Contains(auditEvent.requestId, StringComparer.Ordinal))
            {
                return false;
            }

            if (correlationIds.Length > 0 && !correlationIds.Contains(auditEvent.correlationId, StringComparer.Ordinal))
            {
                return false;
            }

            if (capabilities.Length > 0 && !capabilities.Contains(auditEvent.capability, StringComparer.Ordinal))
            {
                return false;
            }

            if (!sinceUtc.HasValue && !untilUtc.HasValue)
            {
                return true;
            }

            if (!DateTime.TryParse(
                    auditEvent.timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var eventTimestamp))
            {
                return false;
            }

            return (!sinceUtc.HasValue || eventTimestamp >= sinceUtc.Value)
                && (!untilUtc.HasValue || eventTimestamp <= untilUtc.Value);
        }

        private static AuditReportEvidence InspectEvidence(AuditReportEvidenceInput input, long maxEvidenceBytes)
        {
            input ??= new AuditReportEvidenceInput();
            var phase = NormalizePhase(input.phase);
            var kind = SanitizeToken(input.kind, 64, "artifact");
            var path = NormalizeArtifactPath(input.path);
            var result = new AuditReportEvidence
            {
                phase = phase,
                kind = kind,
                path = path,
                description = SanitizeText(input.description, 1000, string.Empty)
            };

            if (string.IsNullOrEmpty(path))
            {
                result.error = $"Evidence paths must be project-relative files under {ArtifactRoot}.";
                return result;
            }

            try
            {
                var absolutePath = ResolveArtifactPath(path, false);
                if (!File.Exists(absolutePath))
                {
                    result.error = "Evidence file does not exist.";
                    return result;
                }

                var file = new FileInfo(absolutePath);
                result.byteLength = file.Length;
                result.lastWriteTimeUtc = file.LastWriteTimeUtc.ToString("O");
                if (file.Length > maxEvidenceBytes)
                {
                    result.error = $"Evidence file exceeds maxEvidenceBytes ({maxEvidenceBytes}).";
                    return result;
                }

                result.sha256 = ComputeSha256(absolutePath);
                result.available = !string.IsNullOrEmpty(result.sha256);
                return result;
            }
            catch (Exception exception)
            {
                result.error = exception.Message;
                return result;
            }
        }

        private static AuditReportVerification NormalizeVerification(
            AuditReportVerificationInput input,
            HashSet<string> availableEvidencePaths)
        {
            input ??= new AuditReportVerificationInput();
            var requestedEvidencePaths = NormalizeValues(input.evidencePaths, 100);
            var evidencePaths = requestedEvidencePaths
                .Select(NormalizeArtifactPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToArray();
            return new AuditReportVerification
            {
                signal = SanitizeToken(input.signal, 128, "unspecified"),
                status = NormalizeStatus(input.status),
                summary = SanitizeText(input.summary, 2000, string.Empty),
                evidencePaths = evidencePaths,
                evidenceReferencesValid = requestedEvidencePaths.Length == evidencePaths.Length
                    && evidencePaths.All(availableEvidencePaths.Contains)
            };
        }

        private static string DetermineStatus(
            AuditReportEvidence[] evidence,
            AuditReportVerification[] verifications,
            bool requireBeforeAfter,
            bool beforeAfterComplete,
            bool hasFilters,
            int matchedEventCount)
        {
            if (evidence.Any(item => !item.available)
                || verifications.Any(item => item.status == "failed" || !item.evidenceReferencesValid)
                || (hasFilters && matchedEventCount == 0)
                || (requireBeforeAfter && !beforeAfterComplete))
            {
                return "failed";
            }

            if (evidence.Length == 0
                || verifications.Length == 0
                || verifications.Any(item => item.status == "inconclusive"))
            {
                return "inconclusive";
            }

            return "passed";
        }

        private static string BuildMarkdown(AuditReportDocument report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# " + MarkdownText(report.title));
            builder.AppendLine();
            builder.AppendLine($"- Report ID: `{MarkdownText(report.reportId)}`");
            builder.AppendLine($"- Generated: `{MarkdownText(report.generatedAtUtc)}`");
            builder.AppendLine($"- Status: **{MarkdownText(report.status)}**");
            builder.AppendLine($"- Audit events: {report.matchedAuditEvents} matched, {report.auditEvents.Length} included");
            builder.AppendLine($"- Before/after complete: {report.beforeAfterComplete.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrEmpty(report.summary))
            {
                builder.AppendLine();
                builder.AppendLine("## Summary");
                builder.AppendLine();
                builder.AppendLine(MarkdownText(report.summary));
            }

            builder.AppendLine();
            builder.AppendLine("## Verification");
            builder.AppendLine();
            builder.AppendLine("| Status | Signal | Summary | Evidence |");
            builder.AppendLine("|---|---|---|---|");
            foreach (var verification in report.verifications)
            {
                builder.AppendLine($"| {MarkdownCell(verification.status)} | `{MarkdownCell(verification.signal)}` | {MarkdownCell(verification.summary)} | {MarkdownCell(string.Join(", ", verification.evidencePaths))} |");
            }

            if (report.verifications.Length == 0)
            {
                builder.AppendLine("| inconclusive | `none` | No verification claims supplied. | |");
            }

            builder.AppendLine();
            builder.AppendLine("## Evidence");
            builder.AppendLine();
            builder.AppendLine("| Phase | Kind | Path | Available | Bytes | SHA-256 | Description |");
            builder.AppendLine("|---|---|---|---:|---:|---|---|");
            foreach (var evidence in report.evidence)
            {
                builder.AppendLine($"| {MarkdownCell(evidence.phase)} | {MarkdownCell(evidence.kind)} | `{MarkdownCell(evidence.path)}` | {evidence.available.ToString().ToLowerInvariant()} | {evidence.byteLength} | `{MarkdownCell(evidence.sha256)}` | {MarkdownCell(string.IsNullOrEmpty(evidence.error) ? evidence.description : evidence.error)} |");
            }

            if (report.evidence.Length == 0)
            {
                builder.AppendLine("| supporting | artifact | | false | 0 | | No evidence supplied. |");
            }

            builder.AppendLine();
            builder.AppendLine("## Audit Events");
            builder.AppendLine();
            builder.AppendLine("| Timestamp | Capability | Request ID | Correlation ID | Effects | Message |");
            builder.AppendLine("|---|---|---|---|---|---|");
            foreach (var auditEvent in report.auditEvents)
            {
                builder.AppendLine($"| `{MarkdownCell(auditEvent.timestamp)}` | `{MarkdownCell(auditEvent.capability)}` | `{MarkdownCell(auditEvent.requestId)}` | `{MarkdownCell(auditEvent.correlationId)}` | {MarkdownCell(string.Join(", ", auditEvent.effects ?? Array.Empty<string>()))} | {MarkdownCell(auditEvent.message)} |");
            }

            if (report.auditEvents.Length == 0)
            {
                builder.AppendLine("| | | | | | No matching audit events. |");
            }

            return builder.ToString();
        }

        private static bool TryParseTimestamp(
            string value,
            out DateTime? timestamp,
            out string normalized,
            out string error)
        {
            timestamp = null;
            normalized = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (!DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                error = $"Invalid UTC timestamp '{SanitizeText(value, 100, string.Empty)}'.";
                return false;
            }

            timestamp = parsed;
            normalized = parsed.ToString("O");
            return true;
        }

        private static string ResolveArtifactPath(string relativePath, bool createParent)
        {
            var normalized = NormalizeArtifactPath(relativePath);
            if (string.IsNullOrEmpty(normalized))
            {
                throw new InvalidOperationException($"Artifact path must stay under {ArtifactRoot}.");
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, ArtifactRoot));
            var absolutePath = Path.GetFullPath(Path.Combine(projectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = artifactRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!absolutePath.StartsWith(prefix, comparison))
            {
                throw new InvalidOperationException("Artifact path escapes UnityAIArtifacts.");
            }

            if (createParent)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? artifactRoot);
            }

            return absolutePath;
        }

        private static string NormalizeArtifactPath(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(normalized)
                || Path.IsPathRooted(normalized)
                || normalized.EndsWith("/", StringComparison.Ordinal)
                || !normalized.StartsWith(ArtifactRoot + "/", StringComparison.Ordinal)
                || normalized.Split('/').Any(segment => segment == ".." || segment.Length == 0))
            {
                return string.Empty;
            }

            return normalized;
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
                Debug.LogError($"Failed to persist audit report event: {exception.Message}");
                return false;
            }
        }

        private static AuditReportResult Refused(AuditReportRequest request, string message, string generatedAt)
        {
            return new AuditReportResult
            {
                refused = true,
                requestId = request.requestId,
                correlationId = request.correlationId,
                status = "failed",
                message = message,
                auditLogPath = AuditLogStore.AuditLogRelativePath.Replace('\\', '/'),
                generatedAtUtc = generatedAt
            };
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string[] NormalizeValues(string[] values, int maxResults)
        {
            return (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => SanitizeText(value, 256, string.Empty))
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .Take(maxResults)
                .ToArray();
        }

        private static string NormalizePhase(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "before" || normalized == "after" ? normalized : "supporting";
        }

        private static string NormalizeStatus(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "passed" || normalized == "failed" ? normalized : "inconclusive";
        }

        private static string SanitizeToken(string value, int maxLength, string fallback)
        {
            var sanitized = new string((value ?? string.Empty)
                .Trim()
                .Take(maxLength)
                .Select(character => char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.' ? character : '_')
                .ToArray())
                .Trim('_');
            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }

        private static string SanitizeText(string value, int maxLength, string fallback)
        {
            var sanitized = new string((value ?? string.Empty)
                .Take(maxLength)
                .Select(character => char.IsControl(character) && character != '\n' && character != '\t' ? ' ' : character)
                .ToArray())
                .Trim();
            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }

        private static string MarkdownText(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", "  \n");
        }

        private static string MarkdownCell(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("|", "\\|")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }

        private static AuditReportRequest ParseRequest(string body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body)
                    ? new AuditReportRequest()
                    : JsonUtility.FromJson<AuditReportRequest>(body) ?? new AuditReportRequest();
            }
            catch
            {
                return new AuditReportRequest();
            }
        }
    }
}
