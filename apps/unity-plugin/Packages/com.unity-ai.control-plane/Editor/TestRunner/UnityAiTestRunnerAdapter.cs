using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityAI.ControlPlane.Editor;

namespace UnityAI.ControlPlane.Editor.Testing
{
    [Serializable]
    public sealed class TestFailureInfo
    {
        public string fullName;
        public string resultState;
        public string message;
        public string stackTrace;
        public double durationSeconds;
    }

    [Serializable]
    public sealed class TestRunResult
    {
        public string mode;
        public int passed;
        public int failed;
        public int skipped;
        public int inconclusive;
        public double durationSeconds;
        public string resultState;
        public string resultPath;
        public TestFailureInfo[] failures = Array.Empty<TestFailureInfo>();
        public string completedAtUtc;
    }

    [InitializeOnLoad]
    public sealed class UnityAiTestRunnerAdapter : ICallbacks
    {
        private static readonly Dictionary<string, UnityAiTestRunnerAdapter> Active = new();
        private readonly string _jobId;
        private readonly string _mode;
        private TestRunnerApi _api;
        private string _runGuid;

        static UnityAiTestRunnerAdapter()
        {
            EditorApplication.delayCall += RecoverPendingRuns;
        }

        private UnityAiTestRunnerAdapter(string jobId, string mode)
        {
            _jobId = jobId;
            _mode = mode;
        }

        public static bool Start(string jobId, string requestBody)
        {
            var request = JsonUtility.FromJson<TestRunRequest>(requestBody) ?? new TestRunRequest();
            var input = request.input ?? new TestRunInput();
            var mode = string.Equals(input.mode, "play", StringComparison.OrdinalIgnoreCase) ? "play" : "edit";
            var adapter = new UnityAiTestRunnerAdapter(jobId, mode);
            adapter._api = ScriptableObject.CreateInstance<TestRunnerApi>();
            adapter._api.RegisterCallbacks(adapter);

            var filter = new Filter
            {
                testMode = mode == "play" ? TestMode.PlayMode : TestMode.EditMode,
                testNames = EmptyToNull(input.testNames),
                groupNames = EmptyToNull(input.groupNames),
                categoryNames = EmptyToNull(input.categoryNames),
                assemblyNames = EmptyToNull(input.assemblyNames)
            };
            var settings = new ExecutionSettings(filter)
            {
                runSynchronously = mode == "edit" && input.runSynchronously
            };

            Active[jobId] = adapter;
            UnityAiJobStore.MarkRunning(jobId, "discovering", $"Discovering {mode} mode tests.", 0.1f);
            adapter._runGuid = adapter._api.Execute(settings);
            var record = UnityAiJobStore.Get(jobId);
            if (record != null)
            {
                record.stage = "running:" + adapter._runGuid;
                UnityAiJobStore.Save(record);
            }

            return true;
        }

        public static bool Cancel(string jobId)
        {
            if (!Active.TryGetValue(jobId, out var adapter) || string.IsNullOrWhiteSpace(adapter._runGuid))
            {
                return false;
            }

            return TestRunnerApi.CancelTestRun(adapter._runGuid);
        }

        private static void RecoverPendingRuns()
        {
            foreach (var job in UnityAiJobStore.List("running", "tests", 20))
            {
                if (Active.ContainsKey(job.jobId) || string.IsNullOrWhiteSpace(job.stage) || !job.stage.StartsWith("running:", StringComparison.Ordinal))
                {
                    continue;
                }

                var input = (JsonUtility.FromJson<TestRunRequest>(job.requestJson) ?? new TestRunRequest()).input ?? new TestRunInput();
                var mode = string.Equals(input.mode, "play", StringComparison.OrdinalIgnoreCase) ? "play" : "edit";
                var adapter = new UnityAiTestRunnerAdapter(job.jobId, mode)
                {
                    _api = ScriptableObject.CreateInstance<TestRunnerApi>(),
                    _runGuid = job.stage.Substring("running:".Length)
                };
                adapter._api.RegisterCallbacks(adapter);
                Active[job.jobId] = adapter;
                UnityAiJobStore.UpdateProgress(job.jobId, "running", $"Reattached to {ModeLabel(mode)} mode test run after domain reload.", 0.5f);
            }
        }

        private static string ModeLabel(string mode)
        {
            return mode == "play" ? "Play" : "Edit";
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            UnityAiJobStore.UpdateProgress(_jobId, "running", $"{_mode} mode test run started.", 0.2f);
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            try
            {
                var directory = "UnityAIArtifacts/TestResults";
                Directory.CreateDirectory(Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath, directory));
                var resultPath = $"{directory}/{_mode}-{_jobId}.xml";
                TestRunnerApi.SaveResultToFile(result, resultPath);
                var failures = new List<TestFailureInfo>();
                CollectFailures(result, failures, 100);
                var summary = new TestRunResult
                {
                    mode = _mode,
                    passed = result.PassCount,
                    failed = result.FailCount,
                    skipped = result.SkipCount,
                    inconclusive = result.InconclusiveCount,
                    durationSeconds = result.Duration,
                    resultState = result.ResultState,
                    resultPath = resultPath,
                    failures = failures.ToArray(),
                    completedAtUtc = DateTime.UtcNow.ToString("O")
                };

                if (result.FailCount == 0)
                {
                    UnityAiJobStore.Complete(_jobId, summary, $"{_mode} mode tests passed.", "tests_passed", "test_results_available");
                }
                else
                {
                    UnityAiJobStore.Fail(_jobId, $"{result.FailCount} test(s) failed.", summary, "test_results_available");
                }
            }
            catch (Exception exception)
            {
                UnityAiJobStore.Fail(_jobId, exception.Message);
            }
            finally
            {
                _api.UnregisterCallbacks(this);
                UnityEngine.Object.DestroyImmediate(_api);
                Active.Remove(_jobId);
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
            UnityAiJobStore.UpdateProgress(_jobId, "running", $"Running {test.FullName}.", 0.5f);
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }

        private static void CollectFailures(ITestResultAdaptor result, List<TestFailureInfo> output, int limit)
        {
            if (output.Count >= limit)
            {
                return;
            }

            if (!result.HasChildren && result.FailCount > 0)
            {
                output.Add(new TestFailureInfo
                {
                    fullName = result.FullName,
                    resultState = result.ResultState,
                    message = result.Message ?? string.Empty,
                    stackTrace = result.StackTrace ?? string.Empty,
                    durationSeconds = result.Duration
                });
            }

            if (!result.HasChildren)
            {
                return;
            }

            foreach (var child in result.Children)
            {
                CollectFailures(child, output, limit);
                if (output.Count >= limit)
                {
                    return;
                }
            }
        }

        private static string[] EmptyToNull(string[] values)
        {
            return values != null && values.Any(value => !string.IsNullOrWhiteSpace(value))
                ? values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                : null;
        }
    }
}
