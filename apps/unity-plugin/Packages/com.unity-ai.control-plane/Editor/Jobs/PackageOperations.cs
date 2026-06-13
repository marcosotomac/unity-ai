using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class PackageChangeRequest
    {
        public PackageChangeInput input = new();
    }

    [Serializable]
    public sealed class PackageChangeInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string[] add = Array.Empty<string>();
        public string[] remove = Array.Empty<string>();
    }

    [Serializable]
    public sealed class PackageChangeResult
    {
        public string[] added = Array.Empty<string>();
        public string[] removed = Array.Empty<string>();
        public string[] installedPackages = Array.Empty<string>();
        public string checkpointId;
        public string completedAtUtc;
    }

    [InitializeOnLoad]
    public static class PackageOperations
    {
        private const string Capability = "unity.packages.change";
        private static readonly Dictionary<string, AddAndRemoveRequest> ActiveRequests = new();

        static PackageOperations()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        public static UnityAiJobStartResult Start(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new PackageChangeInput();
            input.add = Normalize(input.add);
            input.remove = Normalize(input.remove);
            if (input.add.Length == 0 && input.remove.Length == 0)
            {
                return Rejected(input.dryRun, "At least one package must be added or removed.");
            }

            if (input.add.Intersect(input.remove, StringComparer.Ordinal).Any())
            {
                return Rejected(input.dryRun, "The same package cannot be added and removed in one operation.");
            }

            if (input.dryRun)
            {
                return new UnityAiJobStartResult
                {
                    accepted = true,
                    dryRun = true,
                    status = "preview",
                    message = $"DRY RUN: would add {input.add.Length} and remove {input.remove.Length} package(s).",
                    requiredPermissions = new[] { "modify_project_settings" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return new UnityAiJobStartResult
                {
                    requiresConfirmation = true,
                    status = "needs_confirmation",
                    message = "Package changes require confirm=true and may trigger a domain reload.",
                    requiredPermissions = new[] { "modify_project_settings" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (UnityAiJobStore.List(null, "packages", 100).Any(job => job.status == "queued" || job.status == "running"))
            {
                return Rejected(false, "Another package operation is already active.");
            }

            var checkpoint = DurableCheckpointStore.CreateInternal("package-change", new[] { "Packages/manifest.json", "Packages/packages-lock.json" });
            var envelope = UnityAiJobStore.ParseEnvelope(requestBody);
            var normalizedRequest = new PackageChangeRequest { input = input };
            var job = UnityAiJobStore.Create(Capability, "packages", envelope, JsonUtility.ToJson(normalizedRequest), $"Queued package changes. Checkpoint: {checkpoint.checkpointId}");
            job.resultJson = checkpoint.checkpointId;
            UnityAiJobStore.Save(job);
            UnityAiJobStore.MarkRunning(job.jobId, "resolving", "Resolving package changes.", 0.2f);
            StartRequest(job.jobId, input);

            return new UnityAiJobStartResult
            {
                accepted = true,
                jobId = job.jobId,
                status = "running",
                message = "Package change job started. The bridge will recover after domain reload.",
                requiredPermissions = new[] { "modify_project_settings" },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void Tick()
        {
            foreach (var job in UnityAiJobStore.List("running", "packages", 100))
            {
                if (job.cancelRequested)
                {
                    UnityAiJobStore.Cancel(job.jobId, "Package operation cancellation requested. In-flight Package Manager requests cannot always be interrupted.");
                    ActiveRequests.Remove(job.jobId);
                    continue;
                }

                var input = ParseRequest(job.requestJson).input ?? new PackageChangeInput();
                input.add = Normalize(input.add);
                input.remove = Normalize(input.remove);
                if (DesiredStateReached(input))
                {
                    Complete(job, input);
                    ActiveRequests.Remove(job.jobId);
                    continue;
                }

                if (!ActiveRequests.TryGetValue(job.jobId, out var request))
                {
                    StartRequest(job.jobId, input);
                    continue;
                }

                if (!request.IsCompleted)
                {
                    UnityAiJobStore.UpdateProgress(job.jobId, "resolving", "Unity Package Manager is resolving dependencies.", 0.5f);
                    continue;
                }

                ActiveRequests.Remove(job.jobId);
                if (request.Status == StatusCode.Failure)
                {
                    UnityAiJobStore.Fail(job.jobId, request.Error?.message ?? "Unity Package Manager request failed.");
                }
                else if (DesiredStateReached(input))
                {
                    Complete(job, input);
                }
            }
        }

        private static void StartRequest(string jobId, PackageChangeInput input)
        {
            try
            {
                ActiveRequests[jobId] = Client.AddAndRemove(input.add, input.remove);
            }
            catch (Exception exception)
            {
                UnityAiJobStore.Fail(jobId, exception.Message);
            }
        }

        private static void Complete(UnityAiJobRecord job, PackageChangeInput input)
        {
            var installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(package => $"{package.name}@{package.version}").OrderBy(value => value).ToArray();
            var result = new PackageChangeResult
            {
                added = input.add,
                removed = input.remove,
                installedPackages = installed,
                checkpointId = job.resultJson,
                completedAtUtc = DateTime.UtcNow.ToString("O")
            };
            UnityAiJobStore.Complete(job.jobId, result, "Package changes resolved and verified.", "checkpoint_created", "packages_resolved");
        }

        private static bool DesiredStateReached(PackageChangeInput input)
        {
            var installed = new HashSet<string>(UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(package => package.name), StringComparer.Ordinal);
            return input.add.All(package => installed.Contains(GetPackageName(package)))
                && input.remove.All(package => !installed.Contains(GetPackageName(package)));
        }

        private static string[] Normalize(string[] values)
        {
            return (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(IsSafePackageIdentifier)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsSafePackageIdentifier(string value)
        {
            return value.Length <= 256
                && !value.Any(char.IsWhiteSpace)
                && !value.Contains("..")
                && value.StartsWith("com.", StringComparison.Ordinal)
                && value.All(character => char.IsLetterOrDigit(character) || character == '.' || character == '-' || character == '_' || character == '@');
        }

        private static string GetPackageName(string identifier)
        {
            var separator = identifier.IndexOf('@');
            return separator > 0 ? identifier.Substring(0, separator) : identifier;
        }

        private static UnityAiJobStartResult Rejected(bool dryRun, string message)
        {
            return new UnityAiJobStartResult
            {
                accepted = false,
                dryRun = dryRun,
                status = "refused",
                message = message,
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static PackageChangeRequest ParseRequest(string body)
        {
            try { return JsonUtility.FromJson<PackageChangeRequest>(body) ?? new PackageChangeRequest(); }
            catch { return new PackageChangeRequest(); }
        }
    }
}
