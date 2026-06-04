using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ScreenshotCaptureResult
    {
        public bool available;
        public string source;
        public string path;
        public string message;
        public string capturedAtUtc;
    }

    public static class ScreenshotCapture
    {
        private const string ArtifactDirectory = "UnityAIArtifacts/Screenshots";

        public static ScreenshotCaptureResult CaptureGameView()
        {
            var path = CreateScreenshotPath("game-view");
            ScreenCapture.CaptureScreenshot(path);
            AssetDatabase.Refresh();

            return new ScreenshotCaptureResult
            {
                available = true,
                source = "game",
                path = path,
                message = "Game View screenshot capture requested. Unity may complete the file write asynchronously.",
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static ScreenshotCaptureResult CaptureSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView;
            var camera = sceneView != null ? sceneView.camera : null;

            if (camera == null)
            {
                return new ScreenshotCaptureResult
                {
                    available = false,
                    source = "scene",
                    message = "No active Scene View camera is available.",
                    capturedAtUtc = DateTime.UtcNow.ToString("O")
                };
            }

            var width = Mathf.Max(1, (int)sceneView.position.width);
            var height = Mathf.Max(1, (int)sceneView.position.height);
            var path = CreateScreenshotPath("scene-view");

            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            var previousTargetTexture = camera.targetTexture;

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;

                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                File.WriteAllBytes(path, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
            }
            finally
            {
                camera.targetTexture = previousTargetTexture;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }

            AssetDatabase.Refresh();

            return new ScreenshotCaptureResult
            {
                available = true,
                source = "scene",
                path = path,
                message = "Scene View screenshot captured.",
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static string CreateScreenshotPath(string source)
        {
            var directory = Path.Combine(Application.dataPath, ArtifactDirectory);
            Directory.CreateDirectory(directory);

            var fileName = $"{source}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png";
            return Path.Combine(directory, fileName);
        }
    }
}
