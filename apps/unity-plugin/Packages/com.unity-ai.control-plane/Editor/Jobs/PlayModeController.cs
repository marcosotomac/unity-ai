using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class PlayModeControlRequest
    {
        public PlayModeControlInput input = new();
    }

    [Serializable]
    public sealed class PlayModeControlInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string action;
    }

    [Serializable]
    public sealed class PlayModeStatusReport
    {
        public bool isPlaying;
        public bool isPlayingOrWillChange;
        public bool isPaused;
        public string state;
        public string capturedAtUtc;
    }

    [InitializeOnLoad]
    public static class PlayModeController
    {
        private const string Capability = "unity.playmode.control";

        static PlayModeController()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update -= ResumePendingJobs;
            EditorApplication.update += ResumePendingJobs;
        }

        public static PlayModeStatusReport GetStatus()
        {
            return new PlayModeStatusReport
            {
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChange = EditorApplication.isPlayingOrWillChangePlaymode,
                isPaused = EditorApplication.isPaused,
                state = EditorApplication.isPlaying
                    ? EditorApplication.isPaused ? "paused" : "playing"
                    : EditorApplication.isPlayingOrWillChangePlaymode ? "transitioning" : "stopped",
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static UnityAiJobStartResult Start(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var input = request.input ?? new PlayModeControlInput();
            var action = (input.action ?? string.Empty).Trim().ToLowerInvariant();
            if (!new[] { "enter", "exit", "pause", "resume", "step" }.Contains(action))
            {
                return Rejected(input.dryRun, "action must be enter, exit, pause, resume, or step.");
            }

            if (input.dryRun)
            {
                return new UnityAiJobStartResult
                {
                    accepted = true,
                    dryRun = true,
                    status = "preview",
                    message = $"DRY RUN: would request Play Mode action '{action}'.",
                    requiredPermissions = new[] { "execute_editor_operation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return new UnityAiJobStartResult
                {
                    accepted = false,
                    requiresConfirmation = true,
                    status = "needs_confirmation",
                    message = $"Play Mode action '{action}' requires confirm=true.",
                    requiredPermissions = new[] { "execute_editor_operation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            var envelope = UnityAiJobStore.ParseEnvelope(requestBody);
            var job = UnityAiJobStore.Create(Capability, "playmode", envelope, requestBody, $"Queued Play Mode action '{action}'.");
            job.stage = action;
            UnityAiJobStore.Save(job);

            EditorApplication.delayCall += () => Execute(job.jobId, action);
            return Accepted(job);
        }

        private static void Execute(string jobId, string action)
        {
            var job = UnityAiJobStore.Get(jobId);
            if (job == null || job.cancelRequested)
            {
                UnityAiJobStore.Cancel(jobId, "Play Mode action cancelled before execution.");
                return;
            }

            try
            {
                UnityAiJobStore.MarkRunning(jobId, action, $"Executing Play Mode action '{action}'.", 0.5f);
                switch (action)
                {
                    case "enter":
                        if (EditorApplication.isPlaying)
                        {
                            Complete(jobId, "Play Mode was already active.");
                        }
                        else
                        {
                            EditorApplication.EnterPlaymode();
                        }
                        break;
                    case "exit":
                        if (!EditorApplication.isPlayingOrWillChangePlaymode)
                        {
                            Complete(jobId, "Play Mode was already stopped.");
                        }
                        else
                        {
                            EditorApplication.ExitPlaymode();
                        }
                        break;
                    case "pause":
                        if (!EditorApplication.isPlaying)
                        {
                            UnityAiJobStore.Fail(jobId, "Cannot pause because Play Mode is not active.");
                            break;
                        }

                        EditorApplication.isPaused = true;
                        Complete(jobId, "Play Mode paused.");
                        break;
                    case "resume":
                        if (!EditorApplication.isPlaying)
                        {
                            UnityAiJobStore.Fail(jobId, "Cannot resume because Play Mode is not active.");
                            break;
                        }

                        EditorApplication.isPaused = false;
                        Complete(jobId, "Play Mode resumed.");
                        break;
                    case "step":
                        if (!EditorApplication.isPlaying)
                        {
                            UnityAiJobStore.Fail(jobId, "Cannot step because Play Mode is not active.");
                            break;
                        }

                        EditorApplication.Step();
                        Complete(jobId, "Play Mode advanced one frame.");
                        break;
                }
            }
            catch (Exception exception)
            {
                UnityAiJobStore.Fail(jobId, exception.Message);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            foreach (var job in UnityAiJobStore.List("running", "playmode", 100))
            {
                if (job.stage == "enter" && state == PlayModeStateChange.EnteredPlayMode)
                {
                    Complete(job.jobId, "Entered Play Mode.");
                }
                else if (job.stage == "exit" && state == PlayModeStateChange.EnteredEditMode)
                {
                    Complete(job.jobId, "Exited Play Mode.");
                }
            }
        }

        private static void ResumePendingJobs()
        {
            foreach (var job in UnityAiJobStore.List("running", "playmode", 100))
            {
                if (job.cancelRequested)
                {
                    UnityAiJobStore.Cancel(job.jobId, "Play Mode action cancelled.");
                }
                else if (job.stage == "enter" && EditorApplication.isPlaying)
                {
                    Complete(job.jobId, "Entered Play Mode.");
                }
                else if (job.stage == "exit" && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    Complete(job.jobId, "Exited Play Mode.");
                }
            }
        }

        private static void Complete(string jobId, string message)
        {
            UnityAiJobStore.Complete(jobId, GetStatus(), message, "playmode_state_verified");
        }

        private static UnityAiJobStartResult Accepted(UnityAiJobRecord job)
        {
            return new UnityAiJobStartResult
            {
                accepted = true,
                jobId = job.jobId,
                status = job.status,
                message = job.message,
                requiredPermissions = new[] { "execute_editor_operation" },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
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

        private static PlayModeControlRequest ParseRequest(string requestBody)
        {
            try { return JsonUtility.FromJson<PlayModeControlRequest>(requestBody) ?? new PlayModeControlRequest(); }
            catch { return new PlayModeControlRequest(); }
        }
    }
}
