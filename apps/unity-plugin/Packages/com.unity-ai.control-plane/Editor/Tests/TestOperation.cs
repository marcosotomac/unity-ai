using System;
using System.Linq;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class TestRunRequest
    {
        public TestRunInput input = new();
    }

    [Serializable]
    public sealed class TestRunInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string mode = "edit";
        public string[] testNames = Array.Empty<string>();
        public string[] groupNames = Array.Empty<string>();
        public string[] categoryNames = Array.Empty<string>();
        public string[] assemblyNames = Array.Empty<string>();
        public bool runSynchronously;
        public bool saveModifiedScenes;
        public int inputStartDelayFrames = 1;
        public InputSimulationEventInput[] inputEvents = Array.Empty<InputSimulationEventInput>();
    }

    public static class TestOperation
    {
        private const string Capability = "unity.tests.run";
        private const string AdapterTypeName = "UnityAI.ControlPlane.Editor.Testing.UnityAiTestRunnerAdapter, Unity.AI.ControlPlane.Editor.TestRunner";

        public static UnityAiJobStartResult Start(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new TestRunInput();
            var mode = (input.mode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode != "edit" && mode != "play")
            {
                return Rejected(input.dryRun, "mode must be edit or play.");
            }

            if (!ValidateInputSimulation(input, mode, out var inputError))
            {
                return Rejected(input.dryRun, inputError);
            }

            if (input.dryRun)
            {
                return new UnityAiJobStartResult
                {
                    accepted = true,
                    dryRun = true,
                    status = "preview",
                    message = $"DRY RUN: would run {mode} mode tests with the supplied filters.",
                    requiredPermissions = new[] { "run_tests" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return new UnityAiJobStartResult
                {
                    requiresConfirmation = true,
                    status = "needs_confirmation",
                    message = "Running Unity tests can enter Play Mode and requires confirm=true.",
                    requiredPermissions = new[] { "run_tests" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            var dirtyScenes = Enumerable.Range(0, SceneManager.sceneCount)
                .Select(SceneManager.GetSceneAt)
                .Where(scene => scene.IsValid() && scene.isDirty)
                .Select(scene => string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path)
                .ToArray();
            if (dirtyScenes.Length > 0)
            {
                if (!input.saveModifiedScenes)
                {
                    return Rejected(false, "Open scenes contain unsaved changes. Set saveModifiedScenes=true to save them before running tests.");
                }

                if (!EditorSceneManager.SaveOpenScenes())
                {
                    return Rejected(false, "Unity could not save all modified scenes before the test run.");
                }
            }

            var adapterType = Type.GetType(AdapterTypeName, false);
            var startMethod = adapterType?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
            if (startMethod == null)
            {
                return Rejected(false, "Unity Test Framework is not installed. Add com.unity.test-framework, then recompile.");
            }

            if (UnityAiJobStore.List(null, "tests", 100).Any(job => job.status == "queued" || job.status == "running"))
            {
                return Rejected(false, "Another Unity test run is already active.");
            }

            var envelope = UnityAiJobStore.ParseEnvelope(requestBody);
            var job = UnityAiJobStore.Create(Capability, "tests", envelope, requestBody, $"Queued {mode} mode test run.");
            try
            {
                if (input.inputEvents.Length > 0)
                {
                    InputSimulationScheduler.Register(job.jobId, input.inputEvents, input.inputStartDelayFrames);
                }

                var started = startMethod.Invoke(null, new object[] { job.jobId, requestBody }) is bool result && result;
                if (!started)
                {
                    InputSimulationScheduler.Cancel(job.jobId);
                    UnityAiJobStore.Fail(job.jobId, "Unity Test Framework rejected the test run.");
                    return Rejected(false, "Unity Test Framework rejected the test run.");
                }
            }
            catch (Exception exception)
            {
                InputSimulationScheduler.Cancel(job.jobId);
                UnityAiJobStore.Fail(job.jobId, exception.GetBaseException().Message);
                return Rejected(false, exception.GetBaseException().Message);
            }

            return new UnityAiJobStartResult
            {
                accepted = true,
                jobId = job.jobId,
                status = "running",
                message = $"{mode} mode test run started.",
                requiredPermissions = new[] { "run_tests" },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static bool Cancel(string jobId)
        {
            InputSimulationScheduler.Cancel(jobId);
            var adapterType = Type.GetType(AdapterTypeName, false);
            var method = adapterType?.GetMethod("Cancel", BindingFlags.Public | BindingFlags.Static);
            return method != null && method.Invoke(null, new object[] { jobId }) is bool result && result;
        }

        private static bool ValidateInputSimulation(TestRunInput input, string mode, out string error)
        {
            var events = input.inputEvents ?? Array.Empty<InputSimulationEventInput>();
            input.inputEvents = events;
            if (events.Length == 0)
            {
                error = string.Empty;
                return true;
            }

            if (mode != "play")
            {
                error = "inputEvents are supported only for Play Mode tests.";
                return false;
            }

            if (events.Length > 500)
            {
                error = "inputEvents cannot contain more than 500 events.";
                return false;
            }

            if (!InputSimulationScheduler.IsAvailable())
            {
                error = "New Input System simulation is unavailable. Install com.unity.inputsystem and recompile.";
                return false;
            }

            input.inputStartDelayFrames = Math.Max(0, Math.Min(input.inputStartDelayFrames, 10000));
            for (var index = 0; index < events.Length; index++)
            {
                if (!InputSimulationScheduler.Validate(events[index], out error))
                {
                    error = $"inputEvents[{index}]: {error}";
                    return false;
                }
            }

            error = string.Empty;
            return true;
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

        private static TestRunRequest ParseRequest(string body)
        {
            try { return JsonUtility.FromJson<TestRunRequest>(body) ?? new TestRunRequest(); }
            catch { return new TestRunRequest(); }
        }
    }
}
