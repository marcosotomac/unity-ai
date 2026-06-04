using System;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    public sealed class UnityAiControlPlaneWindow : EditorWindow
    {
        private string _lastResult = "Ready.";
        private string _bridgeToken = Guid.NewGuid().ToString("N");

        [MenuItem("Tools/Unity AI/Control Plane")]
        public static void Open()
        {
            GetWindow<UnityAiControlPlaneWindow>("Unity AI");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Unity AI Control Plane", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Local bridge", UnityAiBridgeServer.IsRunning ? "Running" : "Stopped");
            _bridgeToken = EditorGUILayout.TextField("Bridge token", _bridgeToken);

            if (!UnityAiBridgeServer.IsRunning && GUILayout.Button("Start Local Bridge"))
            {
                UnityAiBridgeServer.Start(_bridgeToken);
            }

            if (UnityAiBridgeServer.IsRunning && GUILayout.Button("Stop Local Bridge"))
            {
                UnityAiBridgeServer.Stop();
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Inspect Project"))
            {
                var snapshot = ProjectInspector.InspectActiveProject();
                _lastResult = JsonUtility.ToJson(snapshot, true);
            }

            if (GUILayout.Button("Print Console Summary"))
            {
                _lastResult = JsonUtility.ToJson(ConsoleLogBridge.GetSummary(), true);
            }

            if (GUILayout.Button("Capture Scene View"))
            {
                _lastResult = JsonUtility.ToJson(ScreenshotCapture.CaptureSceneView(), true);
            }

            if (GUILayout.Button("Capture Game View"))
            {
                _lastResult = JsonUtility.ToJson(ScreenshotCapture.CaptureGameView(), true);
            }

            if (GUILayout.Button("Validate Meta XR Setup"))
            {
                _lastResult = JsonUtility.ToJson(MetaXrValidator.Validate(), true);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Last result", EditorStyles.boldLabel);
            EditorGUILayout.TextArea(_lastResult, GUILayout.MinHeight(120));
        }
    }
}
