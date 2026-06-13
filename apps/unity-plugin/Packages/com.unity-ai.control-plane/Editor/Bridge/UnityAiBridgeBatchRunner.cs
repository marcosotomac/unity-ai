using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    public static class UnityAiBridgeBatchRunner
    {
        private const string ReadyFileArgument = "-unityAiReadyFile";
        private const string DurationSecondsArgument = "-unityAiDurationSeconds";
        private const string BridgeTokenFileArgument = "-unityAiBridgeTokenFile";
        private const int DefaultDurationSeconds = 60;

        public static void Run()
        {
            var readyFile = GetArgumentValue(ReadyFileArgument);
            var durationSeconds = GetDurationSeconds();
            var bridgeToken = GetBridgeToken();

            EnsureE2ePrefabFixture();
            EnsureE2eVisualFixture();
            EnsureE2eSceneFixture();

            UnityAiBridgeServer.Start(bridgeToken);

            if (!string.IsNullOrWhiteSpace(readyFile))
            {
                var directory = Path.GetDirectoryName(readyFile);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(readyFile, UnityAiBridgeServer.Url);
            }

            Debug.Log($"Unity AI bridge batch runner ready at {UnityAiBridgeServer.Url} for {durationSeconds} seconds.");

            var stopAt = DateTime.UtcNow.AddSeconds(durationSeconds);
            EditorApplication.update += Tick;

            void Tick()
            {
                if (DateTime.UtcNow < stopAt)
                {
                    return;
                }

                EditorApplication.update -= Tick;
                UnityAiBridgeServer.Stop();
                EditorApplication.Exit(0);
            }
        }

        private static int GetDurationSeconds()
        {
            var raw = GetArgumentValue(DurationSecondsArgument);

            if (int.TryParse(raw, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            return DefaultDurationSeconds;
        }

        private static string GetBridgeToken()
        {
            var tokenFile = GetArgumentValue(BridgeTokenFileArgument);

            if (!string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile))
            {
                return File.ReadAllText(tokenFile).Trim();
            }

            return string.Empty;
        }

        private static void EnsureE2ePrefabFixture()
        {
            const string prefabPath = "Assets/UnityAiE2E.prefab";

            if (File.Exists(prefabPath))
            {
                return;
            }

            var root = new GameObject("UnityAiE2EPrefabRoot");
            root.AddComponent<BoxCollider>();
            var child = new GameObject("UnityAiE2EPrefabChild");
            child.transform.SetParent(root.transform);

            try
            {
                Directory.CreateDirectory("Assets");
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.Refresh();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void EnsureE2eVisualFixture()
        {
            const string cameraName = "UnityAiE2EVisualCamera";
            WriteSolidVisualFixture("UnityAIArtifacts/Screenshots/e2e-before.png", Color.black);
            WriteSolidVisualFixture("UnityAIArtifacts/Screenshots/e2e-after.png", Color.red);

            if (GameObject.Find(cameraName) != null)
            {
                return;
            }

            var cameraObject = new GameObject(cameraName);
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;
            camera.depth = 100;
            camera.enabled = false;
        }

        private static void EnsureE2eSceneFixture()
        {
            const string scenePath = "Assets/UnityAiE2E.unity";
            var scene = EditorSceneManager.GetActiveScene();
            if (string.IsNullOrWhiteSpace(scene.path))
            {
                Directory.CreateDirectory("Assets");
                EditorSceneManager.SaveScene(scene, scenePath);
            }

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(scenePath, true)
            };
        }

        private static void WriteSolidVisualFixture(string relativePath, Color color)
        {
            const int width = 32;
            const int height = 18;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            var pixel = (Color32)color;

            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = pixel;
            }

            try
            {
                texture.SetPixels32(pixels);
                texture.Apply();
                File.WriteAllBytes(VisualArtifactStore.ResolveRelativePath(relativePath), texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static string GetArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();

            for (var index = 0; index < args.Length - 1; index++)
            {
                if (args[index] == name)
                {
                    return args[index + 1];
                }
            }

            return string.Empty;
        }
    }
}
