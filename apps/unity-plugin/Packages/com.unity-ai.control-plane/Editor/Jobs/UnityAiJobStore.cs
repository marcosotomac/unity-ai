using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class UnityAiJobRecord
    {
        public string jobId;
        public string capability;
        public string kind;
        public string status;
        public float progress;
        public string stage;
        public string message;
        public string requestId;
        public string correlationId;
        public string requestJson;
        public string resultJson;
        public string error;
        public bool cancelRequested;
        public string[] verificationSignals = Array.Empty<string>();
        public string createdAtUtc;
        public string updatedAtUtc;
        public string startedAtUtc;
        public string completedAtUtc;
    }

    [Serializable]
    public sealed class UnityAiJobStartResult
    {
        public bool accepted;
        public bool dryRun;
        public bool requiresConfirmation;
        public string jobId;
        public string status;
        public string message;
        public string[] requiredPermissions = Array.Empty<string>();
        public string[] verificationSignals = Array.Empty<string>();
        public string timestampUtc;
    }

    [Serializable]
    public sealed class UnityAiJobListResult
    {
        public int totalFound;
        public int returned;
        public UnityAiJobRecord[] jobs = Array.Empty<UnityAiJobRecord>();
        public string capturedAtUtc;
    }

    [Serializable]
    public sealed class UnityAiJobGetRequest
    {
        public UnityAiJobGetInput input = new();
    }

    [Serializable]
    public sealed class UnityAiJobGetInput
    {
        public string jobId;
    }

    [Serializable]
    public sealed class UnityAiJobListRequest
    {
        public UnityAiJobListInput input = new();
    }

    [Serializable]
    public sealed class UnityAiJobListInput
    {
        public string status;
        public string kind;
        public int maxResults = 100;
    }

    [Serializable]
    public sealed class UnityAiJobCancelRequest
    {
        public UnityAiJobCancelInput input = new();
    }

    [Serializable]
    public sealed class UnityAiJobCancelInput
    {
        public string jobId;
    }

    public static class UnityAiJobStore
    {
        private const string JobsDirectory = "Library/UnityAIControlPlane/Jobs";
        private static readonly object Gate = new();

        public static UnityAiJobRecord Create(string capability, string kind, UnityAiRequestEnvelope envelope, string requestJson, string message)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var record = new UnityAiJobRecord
            {
                jobId = Guid.NewGuid().ToString("N"),
                capability = capability,
                kind = kind,
                status = "queued",
                progress = 0f,
                stage = "queued",
                message = message ?? string.Empty,
                requestId = envelope?.requestId ?? string.Empty,
                correlationId = envelope?.correlationId ?? string.Empty,
                requestJson = requestJson ?? string.Empty,
                resultJson = string.Empty,
                error = string.Empty,
                createdAtUtc = timestamp,
                updatedAtUtc = timestamp
            };
            Save(record);
            return record;
        }

        public static UnityAiJobRecord Get(string jobId)
        {
            if (!IsSafeJobId(jobId))
            {
                return null;
            }

            lock (Gate)
            {
                var path = GetJobPath(jobId);
                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    return JsonUtility.FromJson<UnityAiJobRecord>(File.ReadAllText(path));
                }
                catch
                {
                    return null;
                }
            }
        }

        public static UnityAiJobRecord[] List(string status = null, string kind = null, int maxResults = 100)
        {
            lock (Gate)
            {
                var directory = GetJobsDirectory();
                if (!Directory.Exists(directory))
                {
                    return Array.Empty<UnityAiJobRecord>();
                }

                return Directory.GetFiles(directory, "*.json")
                    .Select(path =>
                    {
                        try
                        {
                            return JsonUtility.FromJson<UnityAiJobRecord>(File.ReadAllText(path));
                        }
                        catch
                        {
                            return null;
                        }
                    })
                    .Where(record => record != null)
                    .Where(record => string.IsNullOrWhiteSpace(status) || string.Equals(record.status, status.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Where(record => string.IsNullOrWhiteSpace(kind) || string.Equals(record.kind, kind.Trim(), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(record => record.updatedAtUtc)
                    .Take(Math.Max(1, Math.Min(maxResults, 500)))
                    .ToArray();
            }
        }

        public static void Save(UnityAiJobRecord record)
        {
            if (record == null || !IsSafeJobId(record.jobId))
            {
                return;
            }

            lock (Gate)
            {
                Directory.CreateDirectory(GetJobsDirectory());
                record.updatedAtUtc = DateTime.UtcNow.ToString("O");
                var path = GetJobPath(record.jobId);
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, JsonUtility.ToJson(record, true));

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temporaryPath, path);
            }
        }

        public static void MarkRunning(string jobId, string stage, string message, float progress = 0f)
        {
            var record = Get(jobId);
            if (record == null)
            {
                return;
            }

            record.status = "running";
            record.stage = stage ?? "running";
            record.message = message ?? string.Empty;
            record.progress = Mathf.Clamp01(progress);
            record.startedAtUtc = string.IsNullOrEmpty(record.startedAtUtc) ? DateTime.UtcNow.ToString("O") : record.startedAtUtc;
            Save(record);
        }

        public static void UpdateProgress(string jobId, string stage, string message, float progress)
        {
            var record = Get(jobId);
            if (record == null)
            {
                return;
            }

            record.stage = stage ?? record.stage;
            record.message = message ?? record.message;
            record.progress = Mathf.Clamp01(progress);
            Save(record);
        }

        public static void Complete(string jobId, object result, string message, params string[] verificationSignals)
        {
            var record = Get(jobId);
            if (record == null)
            {
                return;
            }

            record.status = "succeeded";
            record.stage = "completed";
            record.progress = 1f;
            record.message = message ?? "Job completed.";
            record.resultJson = result != null ? JsonUtility.ToJson(result, true) : string.Empty;
            record.error = string.Empty;
            record.verificationSignals = verificationSignals ?? Array.Empty<string>();
            record.completedAtUtc = DateTime.UtcNow.ToString("O");
            Save(record);
        }

        public static void Fail(string jobId, string error, object result = null, params string[] verificationSignals)
        {
            var record = Get(jobId);
            if (record == null)
            {
                return;
            }

            record.status = "failed";
            record.stage = "failed";
            record.message = "Job failed.";
            record.error = error ?? "Unknown error.";
            record.resultJson = result != null ? JsonUtility.ToJson(result, true) : string.Empty;
            record.verificationSignals = verificationSignals ?? Array.Empty<string>();
            record.completedAtUtc = DateTime.UtcNow.ToString("O");
            Save(record);
        }

        public static void Cancel(string jobId, string message)
        {
            var record = Get(jobId);
            if (record == null)
            {
                return;
            }

            record.status = "cancelled";
            record.stage = "cancelled";
            record.message = message ?? "Job cancelled.";
            record.cancelRequested = true;
            record.completedAtUtc = DateTime.UtcNow.ToString("O");
            Save(record);
        }

        public static bool RequestCancellation(string jobId)
        {
            var record = Get(jobId);
            if (record == null || record.status == "succeeded" || record.status == "failed" || record.status == "cancelled")
            {
                return false;
            }

            record.cancelRequested = true;
            record.message = "Cancellation requested.";
            Save(record);
            return true;
        }

        public static UnityAiJobRecord GetFromRequest(string requestBody)
        {
            var request = ParseGetRequest(requestBody);
            return Get(request.input?.jobId);
        }

        public static UnityAiJobListResult ListFromRequest(string requestBody)
        {
            var request = ParseListRequest(requestBody);
            var input = request.input ?? new UnityAiJobListInput();
            var jobs = List(input.status, input.kind, input.maxResults);
            return new UnityAiJobListResult
            {
                totalFound = jobs.Length,
                returned = jobs.Length,
                jobs = jobs,
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static UnityAiJobRecord CancelFromRequest(string requestBody)
        {
            var request = ParseCancelRequest(requestBody);
            var jobId = request.input?.jobId;
            var record = Get(jobId);
            if (record == null)
            {
                return null;
            }

            if (record.kind == "tests")
            {
                TestOperation.Cancel(jobId);
            }
            else if (record.kind == "build")
            {
                BuildOperations.Cancel(jobId);
            }

            RequestCancellation(jobId);
            return Get(jobId);
        }

        public static UnityAiRequestEnvelope ParseEnvelope(string requestBody)
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

        private static string GetJobsDirectory()
        {
            return Path.Combine(GetProjectRoot(), JobsDirectory);
        }

        private static string GetJobPath(string jobId)
        {
            return Path.Combine(GetJobsDirectory(), jobId + ".json");
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        private static bool IsSafeJobId(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length == 32
                && value.All(character => char.IsLetterOrDigit(character));
        }

        private static UnityAiJobGetRequest ParseGetRequest(string requestBody)
        {
            try
            {
                return JsonUtility.FromJson<UnityAiJobGetRequest>(requestBody) ?? new UnityAiJobGetRequest();
            }
            catch
            {
                return new UnityAiJobGetRequest();
            }
        }

        private static UnityAiJobListRequest ParseListRequest(string requestBody)
        {
            try
            {
                return JsonUtility.FromJson<UnityAiJobListRequest>(requestBody) ?? new UnityAiJobListRequest();
            }
            catch
            {
                return new UnityAiJobListRequest();
            }
        }

        private static UnityAiJobCancelRequest ParseCancelRequest(string requestBody)
        {
            try
            {
                return JsonUtility.FromJson<UnityAiJobCancelRequest>(requestBody) ?? new UnityAiJobCancelRequest();
            }
            catch
            {
                return new UnityAiJobCancelRequest();
            }
        }
    }
}
