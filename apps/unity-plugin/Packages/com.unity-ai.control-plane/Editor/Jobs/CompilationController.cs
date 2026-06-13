using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class CompilationWaitRequest
    {
        public CompilationWaitInput input = new();
    }

    [Serializable]
    public sealed class CompilationWaitInput
    {
        public bool triggerRefresh = true;
        public int timeoutSeconds = 300;
        public int maxErrorCount = 0;
    }

    [Serializable]
    public sealed class CompilationStatusReport
    {
        public bool isCompiling;
        public bool isUpdating;
        public int errorCount;
        public int warningCount;
        public bool clean;
        public string capturedAtUtc;
    }

    [InitializeOnLoad]
    public static class CompilationController
    {
        private const string Capability = "unity.compilation.wait";
        private static readonly Dictionary<string, int> StableFrames = new();

        static CompilationController()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public static CompilationStatusReport GetStatus()
        {
            var console = ConsoleLogBridge.Diagnose();
            return new CompilationStatusReport
            {
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                errorCount = console.errorCount,
                warningCount = console.warningCount,
                clean = !EditorApplication.isCompiling && !EditorApplication.isUpdating && console.errorCount == 0,
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static UnityAiJobStartResult Start(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new CompilationWaitInput();
            input.timeoutSeconds = Math.Max(5, Math.Min(input.timeoutSeconds, 3600));
            input.maxErrorCount = Math.Max(0, input.maxErrorCount);
            var envelope = UnityAiJobStore.ParseEnvelope(requestBody);
            var job = UnityAiJobStore.Create(Capability, "compilation", envelope, JsonUtility.ToJson(new CompilationWaitRequest { input = input }), "Waiting for Unity compilation and import to settle.");
            UnityAiJobStore.MarkRunning(job.jobId, "waiting", "Waiting for compilation.", 0.1f);

            if (input.triggerRefresh)
            {
                EditorApplication.delayCall += () => AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            return new UnityAiJobStartResult
            {
                accepted = true,
                jobId = job.jobId,
                status = "running",
                message = "Compilation wait job started.",
                requiredPermissions = new[] { "read_console", "read_project" },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static void Tick()
        {
            foreach (var job in UnityAiJobStore.List("running", "compilation", 100))
            {
                if (job.cancelRequested)
                {
                    UnityAiJobStore.Cancel(job.jobId, "Compilation wait cancelled.");
                    StableFrames.Remove(job.jobId);
                    continue;
                }

                var input = ParseRequest(job.requestJson).input ?? new CompilationWaitInput();
                if (IsTimedOut(job, input.timeoutSeconds))
                {
                    UnityAiJobStore.Fail(job.jobId, $"Compilation did not settle within {input.timeoutSeconds} seconds.", GetStatus());
                    StableFrames.Remove(job.jobId);
                    continue;
                }

                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    StableFrames[job.jobId] = 0;
                    UnityAiJobStore.UpdateProgress(job.jobId, EditorApplication.isCompiling ? "compiling" : "importing", "Unity is compiling or importing assets.", 0.5f);
                    continue;
                }

                StableFrames.TryGetValue(job.jobId, out var stable);
                stable++;
                StableFrames[job.jobId] = stable;
                if (stable < 3)
                {
                    continue;
                }

                var status = GetStatus();
                if (status.errorCount <= Math.Max(0, input.maxErrorCount))
                {
                    var signals = status.errorCount == 0
                        ? new[] { "compilation_completed", "console_snapshot", "console_clean" }
                        : new[] { "compilation_completed", "console_snapshot" };
                    UnityAiJobStore.Complete(job.jobId, status, $"Compilation settled with {status.errorCount} error(s).", signals);
                }
                else
                {
                    UnityAiJobStore.Fail(job.jobId, $"Compilation settled with {status.errorCount} error(s), exceeding maxErrorCount={input.maxErrorCount}.", status, "compilation_completed", "console_snapshot");
                }

                StableFrames.Remove(job.jobId);
            }
        }

        private static void OnCompilationStarted(object context)
        {
            foreach (var job in UnityAiJobStore.List("running", "compilation", 100))
            {
                StableFrames[job.jobId] = 0;
                UnityAiJobStore.UpdateProgress(job.jobId, "compiling", "Unity compilation started.", 0.4f);
            }
        }

        private static void OnCompilationFinished(object context)
        {
            foreach (var job in UnityAiJobStore.List("running", "compilation", 100))
            {
                UnityAiJobStore.UpdateProgress(job.jobId, "verifying_console", "Compilation finished; verifying console state.", 0.8f);
            }
        }

        private static bool IsTimedOut(UnityAiJobRecord job, int timeoutSeconds)
        {
            return DateTime.TryParse(job.createdAtUtc, out var created)
                && DateTime.UtcNow > created.ToUniversalTime().AddSeconds(Math.Max(5, timeoutSeconds));
        }

        private static CompilationWaitRequest ParseRequest(string requestBody)
        {
            try { return JsonUtility.FromJson<CompilationWaitRequest>(requestBody) ?? new CompilationWaitRequest(); }
            catch { return new CompilationWaitRequest(); }
        }
    }
}
