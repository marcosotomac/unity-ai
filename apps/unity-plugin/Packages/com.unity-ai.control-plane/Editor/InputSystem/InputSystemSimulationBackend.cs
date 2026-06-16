using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace UnityAI.ControlPlane.Editor.InputSystemSupport
{
    public static class InputSystemSimulationBackend
    {
        private static readonly Dictionary<string, InputDevice> Devices = new(StringComparer.Ordinal);
        private static readonly Dictionary<InputControl, string> TouchedControls = new();

        public static bool IsAvailable()
        {
            return true;
        }

        public static string Queue(string deviceName, string controlPath, string valueType, float value, float x, float y)
        {
            try
            {
                var device = GetOrCreateDevice(deviceName);
                var control = device[controlPath];
                switch (valueType)
                {
                    case "button":
                    case "axis":
                        if (!(control is InputControl<float> scalarControl))
                        {
                            return $"Control '{deviceName}/{controlPath}' does not accept a scalar value.";
                        }

                        QueueValue(scalarControl, value);
                        TouchedControls[control] = "scalar";
                        break;
                    case "vector2":
                        if (!(control is InputControl<Vector2> vectorControl))
                        {
                            return $"Control '{deviceName}/{controlPath}' does not accept a Vector2 value.";
                        }

                        QueueValue(vectorControl, new Vector2(x, y));
                        TouchedControls[control] = "vector2";
                        break;
                    default:
                        return $"Unsupported input valueType '{valueType}'.";
                }

                return string.Empty;
            }
            catch (Exception exception)
            {
                return exception.GetBaseException().Message;
            }
        }

        public static string Reset()
        {
            try
            {
                foreach (var pair in TouchedControls)
                {
                    if (pair.Key == null || pair.Key.device == null || !pair.Key.device.added)
                    {
                        continue;
                    }

                    if (pair.Value == "vector2" && pair.Key is InputControl<Vector2> vectorControl)
                    {
                        QueueValue(vectorControl, Vector2.zero);
                    }
                    else if (pair.Key is InputControl<float> scalarControl)
                    {
                        QueueValue(scalarControl, 0f);
                    }
                }

                InputSystem.Update();
                foreach (var device in Devices.Values)
                {
                    if (device != null && device.added)
                    {
                        InputSystem.RemoveDevice(device);
                    }
                }

                TouchedControls.Clear();
                Devices.Clear();
                return string.Empty;
            }
            catch (Exception exception)
            {
                TouchedControls.Clear();
                Devices.Clear();
                return exception.GetBaseException().Message;
            }
        }

        private static InputDevice GetOrCreateDevice(string rawDeviceName)
        {
            var deviceName = (rawDeviceName ?? string.Empty).Trim().ToLowerInvariant();
            if (Devices.TryGetValue(deviceName, out var existing) && existing != null && existing.added)
            {
                return existing;
            }

            var layout = deviceName switch
            {
                "keyboard" => "Keyboard",
                "mouse" => "Mouse",
                "gamepad" => "Gamepad",
                _ => throw new InvalidOperationException($"Unsupported input device '{rawDeviceName}'.")
            };
            var device = InputSystem.AddDevice(layout, "UnityAI" + layout);
            Devices[deviceName] = device;
            return device;
        }

        private static unsafe void QueueValue<TValue>(InputControl<TValue> control, TValue value)
            where TValue : struct
        {
            using (DeltaStateEvent.From(control, out var eventPtr))
            {
                control.WriteValueIntoEvent(value, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }
    }
}
