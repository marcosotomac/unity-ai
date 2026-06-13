using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class VisionCaptureRequest
    {
        public VisionCaptureInput input = new();
    }

    [Serializable]
    public sealed class VisionCaptureInput
    {
        public string source = "scene";
        public int width = 640;
        public int height = 360;
        public string label;
        public string cameraPath;
    }

    [Serializable]
    public sealed class ScreenshotCaptureResult
    {
        public bool available;
        public bool ready;
        public string source;
        public string cameraPath;
        public string path;
        public int width;
        public int height;
        public long byteLength;
        public string sha256;
        public string message;
        public string[] verificationSignals;
        public string capturedAtUtc;
    }

    public static class ScreenshotCapture
    {
        public static ScreenshotCaptureResult Capture(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new VisionCaptureInput();
            var source = string.Equals(input.source, "game", StringComparison.OrdinalIgnoreCase) ? "game" : "scene";
            var width = Math.Max(1, Math.Min(input.width, 4096));
            var height = Math.Max(1, Math.Min(input.height, 4096));

            return source == "game"
                ? CaptureGameView(width, height, input.label, input.cameraPath)
                : CaptureSceneView(width, height, input.label);
        }

        public static ScreenshotCaptureResult CaptureGameView(int width = 640, int height = 360, string label = null, string cameraPath = null)
        {
            var camera = FindGameCamera(cameraPath);
            if (camera == null)
            {
                return Unavailable("game", string.IsNullOrWhiteSpace(cameraPath)
                    ? "No scene camera is available for synchronous Game View capture."
                    : $"No Camera component was found at hierarchy path '{cameraPath}'.");
            }

            return CaptureCamera(camera, "game", "game-view", width, height, label);
        }

        public static ScreenshotCaptureResult CaptureSceneView(int width = 640, int height = 360, string label = null)
        {
            var sceneView = SceneView.lastActiveSceneView;
            var camera = sceneView != null ? sceneView.camera : null;

            if (camera == null)
            {
                return Unavailable("scene", "No active Scene View camera is available.");
            }

            return CaptureCamera(camera, "scene", "scene-view", width, height, label);
        }

        private static ScreenshotCaptureResult CaptureCamera(Camera camera, string source, string filePrefix, int width, int height, string label)
        {
            width = Math.Max(1, Math.Min(width, 4096));
            height = Math.Max(1, Math.Min(height, 4096));
            var relativePath = VisualArtifactStore.CreateRelativePath(filePrefix, label, ".png");
            var absolutePath = VisualArtifactStore.ResolveRelativePath(relativePath);
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            var previousTargetTexture = camera.targetTexture;
            Texture2D texture = null;

            try
            {
                renderTexture.Create();
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                GL.Clear(true, true, camera.backgroundColor);
                camera.Render();

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            }
            catch (Exception exception)
            {
                return Unavailable(source, $"Screenshot capture failed: {exception.Message}");
            }
            finally
            {
                camera.targetTexture = previousTargetTexture;
                RenderTexture.active = previousActive;

                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                UnityEngine.Object.DestroyImmediate(renderTexture);
            }

            if (!VisualArtifactStore.TryInspectPng(relativePath, out var artifact, out var error))
            {
                return Unavailable(source, $"Screenshot file was written but failed readiness verification: {error}");
            }

            return new ScreenshotCaptureResult
            {
                available = true,
                ready = true,
                source = source,
                cameraPath = GetGameObjectPath(camera.gameObject),
                path = relativePath,
                width = artifact.width,
                height = artifact.height,
                byteLength = artifact.byteLength,
                sha256 = artifact.sha256,
                message = $"{source} screenshot captured synchronously and verified ready.",
                verificationSignals = new[] { "screenshot_available", "screenshot_ready" },
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static Camera FindGameCamera(string requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                var requestedObject = FindByPath(requestedPath.Trim().Replace('\\', '/'));
                return requestedObject != null ? requestedObject.GetComponent<Camera>() : null;
            }

            if (Camera.main != null && Camera.main.gameObject.scene.IsValid())
            {
                return Camera.main;
            }

            return Resources.FindObjectsOfTypeAll<Camera>()
                .Where(camera => camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene == EditorSceneManager.GetActiveScene())
                .OrderByDescending(camera => camera.enabled)
                .ThenByDescending(camera => camera.depth)
                .FirstOrDefault();
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("..") || path.Contains("//"))
            {
                return null;
            }

            var segments = path.Split('/');
            GameObject current = null;
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == segments[0])
                {
                    current = root;
                    break;
                }
            }

            if (current == null)
            {
                return null;
            }

            for (var index = 1; index < segments.Length; index++)
            {
                var child = current.transform.Find(segments[index]);
                if (child == null)
                {
                    return null;
                }

                current = child.gameObject;
            }

            return current;
        }

        private static string GetGameObjectPath(GameObject gameObject)
        {
            var path = gameObject.name;
            var current = gameObject.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static ScreenshotCaptureResult Unavailable(string source, string message)
        {
            return new ScreenshotCaptureResult
            {
                available = false,
                ready = false,
                source = source,
                path = string.Empty,
                message = message,
                verificationSignals = Array.Empty<string>(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static VisionCaptureRequest ParseRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody)
                    ? new VisionCaptureRequest()
                    : JsonUtility.FromJson<VisionCaptureRequest>(requestBody) ?? new VisionCaptureRequest();
            }
            catch
            {
                return new VisionCaptureRequest();
            }
        }
    }

    public sealed class VisualArtifactInfo
    {
        public int width;
        public int height;
        public long byteLength;
        public string sha256;
    }

    public static class VisualArtifactStore
    {
        public const string ArtifactDirectory = "UnityAIArtifacts/Screenshots";

        public static string CreateRelativePath(string prefix, string label, string extension)
        {
            var safePrefix = SanitizeSegment(prefix, "capture");
            var safeLabel = SanitizeSegment(label, string.Empty);
            var labelPart = string.IsNullOrEmpty(safeLabel) ? string.Empty : "-" + safeLabel;
            var fileName = $"{safePrefix}{labelPart}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{extension}";
            var relativePath = Path.Combine(ArtifactDirectory, fileName).Replace('\\', '/');
            ResolveRelativePath(relativePath);
            return relativePath;
        }

        public static string ResolveRelativePath(string relativePath)
        {
            var normalized = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized)
                || normalized.Contains("..")
                || !normalized.StartsWith(ArtifactDirectory + "/", StringComparison.Ordinal)
                || normalized.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Visual artifact path must stay under {ArtifactDirectory}.");
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, ArtifactDirectory));
            var absolutePath = Path.GetFullPath(Path.Combine(projectRoot, normalized));
            var rootPrefix = artifactRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? artifactRoot
                : artifactRoot + Path.DirectorySeparatorChar;

            if (!absolutePath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Visual artifact path escapes the artifact directory.");
            }

            Directory.CreateDirectory(artifactRoot);
            return absolutePath;
        }

        public static bool TryInspectPng(string relativePath, out VisualArtifactInfo artifact, out string error)
        {
            artifact = null;
            error = string.Empty;
            Texture2D texture = null;

            try
            {
                var absolutePath = ResolveRelativePath(relativePath);
                if (!File.Exists(absolutePath))
                {
                    error = "File does not exist.";
                    return false;
                }

                var bytes = File.ReadAllBytes(absolutePath);
                if (bytes.Length == 0)
                {
                    error = "File is empty.";
                    return false;
                }

                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes, false) || texture.width <= 0 || texture.height <= 0)
                {
                    error = "File is not a decodable PNG image.";
                    return false;
                }

                artifact = new VisualArtifactInfo
                {
                    width = texture.width,
                    height = texture.height,
                    byteLength = bytes.LongLength,
                    sha256 = ComputeSha256(bytes)
                };
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        public static Texture2D LoadPng(string relativePath)
        {
            var absolutePath = ResolveRelativePath(relativePath);
            if (!File.Exists(absolutePath))
            {
                throw new InvalidOperationException($"Visual artifact '{relativePath}' does not exist.");
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(absolutePath), false))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException($"Visual artifact '{relativePath}' is not a decodable image.");
            }

            return texture;
        }

        public static void WritePng(string relativePath, Texture2D texture)
        {
            File.WriteAllBytes(ResolveRelativePath(relativePath), texture.EncodeToPNG());
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string SanitizeSegment(string value, string fallback)
        {
            var raw = (value ?? string.Empty).Trim();
            var characters = raw
                .Take(50)
                .Select(character => char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '-')
                .ToArray();
            var sanitized = new string(characters).Trim('-');
            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }
    }
}
