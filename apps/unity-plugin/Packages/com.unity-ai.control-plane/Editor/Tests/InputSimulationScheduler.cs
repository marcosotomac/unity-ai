using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class InputSimulationEventInput
    {
        public string valueType;
        public int frameOffset;
        public string targetTest;
        public string device;
        public string control;
        public string action;
        public int durationFrames = 1;
        public float value;
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class InputSimulationAction
    {
        public string valueType;
        public int frameOffset;
        public string device;
        public string control;
        public string action;
        public float value;
        public float x;
        public float y;
        public bool executed;
    }

    [Serializable]
    public sealed class InputSimulationSchedule
    {
        public string jobId;
        public int startDelayFrames;
        public int requestedEventCount;
        public int executedActionCount;
        public string activeTest;
        public int activationFrame;
        public string error;
        public InputSimulationEventInput[] sourceEvents = Array.Empty<InputSimulationEventInput>();
        public InputSimulationAction[] pendingActions = Array.Empty<InputSimulationAction>();
    }

    [Serializable]
    public sealed class InputSimulationReport
    {
        public int requestedEventCount;
        public int executedActionCount;
        public string error;
    }

    [InitializeOnLoad]
    public static class InputSimulationScheduler
    {
        private const string BackendTypeName = "UnityAI.ControlPlane.Editor.InputSystemSupport.InputSystemSimulationBackend, Unity.AI.ControlPlane.Editor.InputSystem";
        private static readonly Dictionary<string, InputSimulationSchedule> Schedules = new();
        private static Type _backendType;

        static InputSimulationScheduler()
        {
            LoadSchedules();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static bool IsAvailable()
        {
            var backend = ResolveBackendType();
            var method = backend?.GetMethod("IsAvailable", BindingFlags.Public | BindingFlags.Static);
            return method != null && method.Invoke(null, Array.Empty<object>()) is bool available && available;
        }

        public static bool Validate(InputSimulationEventInput input, out string error)
        {
            if (input == null)
            {
                error = "event cannot be null.";
                return false;
            }

            var valueType = Normalize(input.valueType);
            var device = Normalize(input.device);
            var action = Normalize(input.action);
            if (valueType != "button" && valueType != "axis" && valueType != "vector2")
            {
                error = "valueType must be button, axis, or vector2.";
                return false;
            }

            if (device != "keyboard" && device != "mouse" && device != "gamepad")
            {
                error = "device must be keyboard, mouse, or gamepad.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(input.control)
                || input.control.Length > 128
                || input.control.Any(char.IsControl))
            {
                error = "control must be a bounded Input System control path.";
                return false;
            }

            if (input.frameOffset < 0 || input.frameOffset > 100000)
            {
                error = "frameOffset must be between 0 and 100000.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(input.targetTest) && input.targetTest.Length > 512)
            {
                error = "targetTest cannot exceed 512 characters.";
                return false;
            }

            if (valueType == "button")
            {
                if (action != "press" && action != "release")
                {
                    error = "button events require action press or release.";
                    return false;
                }

                if (input.durationFrames < 1 || input.durationFrames > 100000)
                {
                    error = "durationFrames must be between 1 and 100000.";
                    return false;
                }
            }
            else
            {
                if (action != "set")
                {
                    error = "axis and vector2 events require action set.";
                    return false;
                }

                if (device == "keyboard")
                {
                    error = "keyboard events support button controls only.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static void Register(string jobId, InputSimulationEventInput[] events, int startDelayFrames)
        {
            var schedule = new InputSimulationSchedule
            {
                jobId = jobId,
                startDelayFrames = Math.Max(0, Math.Min(startDelayFrames, 10000)),
                requestedEventCount = events?.Length ?? 0,
                sourceEvents = events?.Select(CloneEvent).ToArray() ?? Array.Empty<InputSimulationEventInput>()
            };
            Schedules[jobId] = schedule;
            Save(schedule);
        }

        public static void ActivateForTest(string jobId, string testName)
        {
            if (!EditorApplication.isPlaying || !Schedules.TryGetValue(jobId, out var schedule))
            {
                return;
            }

            var selected = schedule.sourceEvents
                .Where(input => string.IsNullOrWhiteSpace(input.targetTest)
                    || string.Equals(input.targetTest.Trim(), testName, StringComparison.Ordinal))
                .ToArray();
            var actions = new List<InputSimulationAction>();
            foreach (var input in selected)
            {
                var action = ToAction(input, schedule.startDelayFrames);
                actions.Add(action);
                if (Normalize(input.valueType) == "button" && Normalize(input.action) == "press")
                {
                    var release = ToAction(input, schedule.startDelayFrames);
                    release.action = "release";
                    release.value = 0f;
                    release.frameOffset += Math.Max(1, input.durationFrames);
                    actions.Add(release);
                }
            }

            schedule.activeTest = testName ?? string.Empty;
            schedule.activationFrame = Time.frameCount;
            schedule.pendingActions = actions.OrderBy(action => action.frameOffset).ToArray();
            Save(schedule);
        }

        public static void CompleteTest(string jobId, string testName)
        {
            if (!Schedules.TryGetValue(jobId, out var schedule)
                || !string.Equals(schedule.activeTest, testName, StringComparison.Ordinal))
            {
                return;
            }

            var resetError = InvokeBackend("Reset");
            if (string.IsNullOrWhiteSpace(schedule.error) && !string.IsNullOrWhiteSpace(resetError))
            {
                schedule.error = resetError;
            }

            schedule.activeTest = string.Empty;
            schedule.pendingActions = Array.Empty<InputSimulationAction>();
            Save(schedule);
        }

        public static InputSimulationReport Finish(string jobId)
        {
            if (!Schedules.TryGetValue(jobId, out var schedule))
            {
                return new InputSimulationReport();
            }

            var resetError = InvokeBackend("Reset");
            if (string.IsNullOrWhiteSpace(schedule.error) && !string.IsNullOrWhiteSpace(resetError))
            {
                schedule.error = resetError;
            }

            var report = new InputSimulationReport
            {
                requestedEventCount = schedule.requestedEventCount,
                executedActionCount = schedule.executedActionCount,
                error = schedule.error ?? string.Empty
            };
            Remove(jobId);
            return report;
        }

        public static void Cancel(string jobId)
        {
            if (Schedules.ContainsKey(jobId))
            {
                InvokeBackend("Reset");
                Remove(jobId);
            }
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying)
            {
                return;
            }

            foreach (var schedule in Schedules.Values.ToArray())
            {
                if (string.IsNullOrWhiteSpace(schedule.activeTest)
                    || schedule.pendingActions == null
                    || schedule.pendingActions.Length == 0
                    || !string.IsNullOrWhiteSpace(schedule.error))
                {
                    continue;
                }

                var elapsedFrames = Math.Max(0, Time.frameCount - schedule.activationFrame);
                var changed = false;
                foreach (var action in schedule.pendingActions)
                {
                    if (action.executed || action.frameOffset > elapsedFrames)
                    {
                        continue;
                    }

                    var error = InvokeBackend(
                        "Queue",
                        action.device,
                        action.control,
                        action.valueType,
                        action.value,
                        action.x,
                        action.y);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        schedule.error = error;
                        changed = true;
                        break;
                    }

                    action.executed = true;
                    schedule.executedActionCount++;
                    changed = true;
                }

                if (changed)
                {
                    Save(schedule);
                }
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            var resetError = InvokeBackend("Reset");
            foreach (var schedule in Schedules.Values)
            {
                if (string.IsNullOrWhiteSpace(schedule.error) && !string.IsNullOrWhiteSpace(resetError))
                {
                    schedule.error = resetError;
                }

                schedule.activeTest = string.Empty;
                schedule.pendingActions = Array.Empty<InputSimulationAction>();
                Save(schedule);
            }
        }

        private static InputSimulationEventInput CloneEvent(InputSimulationEventInput input)
        {
            return new InputSimulationEventInput
            {
                valueType = Normalize(input.valueType),
                frameOffset = input.frameOffset,
                targetTest = input.targetTest?.Trim() ?? string.Empty,
                device = Normalize(input.device),
                control = input.control?.Trim() ?? string.Empty,
                action = Normalize(input.action),
                durationFrames = input.durationFrames,
                value = input.value,
                x = input.x,
                y = input.y
            };
        }

        private static InputSimulationAction ToAction(InputSimulationEventInput input, int startDelayFrames)
        {
            var action = Normalize(input.action);
            return new InputSimulationAction
            {
                valueType = Normalize(input.valueType),
                frameOffset = checked(input.frameOffset + startDelayFrames),
                device = Normalize(input.device),
                control = input.control?.Trim() ?? string.Empty,
                action = action,
                value = action == "press" ? 1f : action == "release" ? 0f : input.value,
                x = input.x,
                y = input.y
            };
        }

        private static string InvokeBackend(string methodName, params object[] arguments)
        {
            try
            {
                var backend = ResolveBackendType();
                var method = backend?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    return "New Input System simulation backend is unavailable.";
                }

                return method.Invoke(null, arguments) as string ?? string.Empty;
            }
            catch (Exception exception)
            {
                return exception.GetBaseException().Message;
            }
        }

        private static Type ResolveBackendType()
        {
            if (_backendType != null)
            {
                return _backendType;
            }

            _backendType = Type.GetType(BackendTypeName, false);
            if (_backendType != null)
            {
                return _backendType;
            }

            try
            {
                var assembly = Assembly.Load("Unity.AI.ControlPlane.Editor.InputSystem");
                _backendType = assembly.GetType("UnityAI.ControlPlane.Editor.InputSystemSupport.InputSystemSimulationBackend", false);
            }
            catch
            {
                _backendType = null;
            }

            return _backendType;
        }

        private static void LoadSchedules()
        {
            var directory = GetScheduleDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    var schedule = JsonUtility.FromJson<InputSimulationSchedule>(File.ReadAllText(path));
                    if (schedule != null && !string.IsNullOrWhiteSpace(schedule.jobId))
                    {
                        Schedules[schedule.jobId] = schedule;
                    }
                }
                catch
                {
                    // Ignore malformed stale schedules; they cannot safely drive input.
                }
            }
        }

        private static void Save(InputSimulationSchedule schedule)
        {
            var directory = GetScheduleDirectory();
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, schedule.jobId + ".json"), JsonUtility.ToJson(schedule, true));
        }

        private static void Remove(string jobId)
        {
            Schedules.Remove(jobId);
            var path = Path.Combine(GetScheduleDirectory(), jobId + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static string GetScheduleDirectory()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectRoot, "Library", "UnityAIControlPlane", "InputSchedules");
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
