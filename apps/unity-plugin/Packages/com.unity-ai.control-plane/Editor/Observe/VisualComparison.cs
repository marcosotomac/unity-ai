using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class VisualCompareRequest
    {
        public VisualCompareInput input = new();
    }

    [Serializable]
    public sealed class VisualCompareInput
    {
        public string beforePath;
        public string afterPath;
        public float pixelThreshold = 0.1f;
        public float maxChangedPixelRatio = 0.01f;
        public float maxMeanAbsoluteError = 0.02f;
        public bool ignoreAlpha = true;
        public bool generateDiff = true;
        public string label;
    }

    [Serializable]
    public sealed class VisualCompareThresholds
    {
        public float pixelThreshold;
        public float maxChangedPixelRatio;
        public float maxMeanAbsoluteError;
        public bool ignoreAlpha;
    }

    [Serializable]
    public sealed class VisualCompareResult
    {
        public bool compared;
        public bool regressionDetected;
        public bool dimensionsMatch;
        public string beforePath;
        public string afterPath;
        public string diffPath;
        public bool diffReady;
        public int width;
        public int height;
        public int totalPixels;
        public int changedPixels;
        public float changedPixelRatio;
        public float meanAbsoluteError;
        public float rootMeanSquareError;
        public float maxChannelError;
        public VisualCompareThresholds thresholds;
        public string[] regressionReasons;
        public string[] verificationSignals;
        public string message;
        public string comparedAtUtc;
    }

    public static class VisualComparison
    {
        public static VisualCompareResult Compare(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new VisualCompareInput();
            var thresholds = BuildThresholds(input);
            Texture2D before = null;
            Texture2D after = null;
            Texture2D diff = null;

            try
            {
                before = VisualArtifactStore.LoadPng(input.beforePath);
                after = VisualArtifactStore.LoadPng(input.afterPath);
                var dimensionsMatch = before.width == after.width && before.height == after.height;

                if (!dimensionsMatch)
                {
                    return new VisualCompareResult
                    {
                        compared = true,
                        regressionDetected = true,
                        dimensionsMatch = false,
                        beforePath = SafeResponsePath(input.beforePath),
                        afterPath = SafeResponsePath(input.afterPath),
                        width = after.width,
                        height = after.height,
                        thresholds = thresholds,
                        regressionReasons = new[] { $"Image dimensions changed from {before.width}x{before.height} to {after.width}x{after.height}." },
                        verificationSignals = new[] { "visual_diff_checked", "visual_regression_detected" },
                        message = "Visual regression detected because image dimensions differ.",
                        comparedAtUtc = DateTime.UtcNow.ToString("O")
                    };
                }

                var beforePixels = before.GetPixels32();
                var afterPixels = after.GetPixels32();
                var diffPixels = input.generateDiff ? new Color32[beforePixels.Length] : null;
                var channelCount = thresholds.ignoreAlpha ? 3 : 4;
                var changedPixels = 0;
                double absoluteErrorSum = 0;
                double squaredErrorSum = 0;
                float maxChannelError = 0;

                for (var index = 0; index < beforePixels.Length; index++)
                {
                    var beforePixel = beforePixels[index];
                    var afterPixel = afterPixels[index];
                    var redError = Math.Abs(beforePixel.r - afterPixel.r) / 255f;
                    var greenError = Math.Abs(beforePixel.g - afterPixel.g) / 255f;
                    var blueError = Math.Abs(beforePixel.b - afterPixel.b) / 255f;
                    var alphaError = thresholds.ignoreAlpha ? 0 : Math.Abs(beforePixel.a - afterPixel.a) / 255f;
                    var pixelError = Math.Max(Math.Max(redError, greenError), Math.Max(blueError, alphaError));

                    if (pixelError > thresholds.pixelThreshold)
                    {
                        changedPixels++;
                    }

                    Accumulate(redError, ref absoluteErrorSum, ref squaredErrorSum, ref maxChannelError);
                    Accumulate(greenError, ref absoluteErrorSum, ref squaredErrorSum, ref maxChannelError);
                    Accumulate(blueError, ref absoluteErrorSum, ref squaredErrorSum, ref maxChannelError);
                    if (!thresholds.ignoreAlpha)
                    {
                        Accumulate(alphaError, ref absoluteErrorSum, ref squaredErrorSum, ref maxChannelError);
                    }

                    if (diffPixels != null)
                    {
                        var intensity = (byte)Mathf.Clamp(Mathf.RoundToInt(pixelError * 255f * 3f), 0, 255);
                        diffPixels[index] = intensity == 0
                            ? new Color32(0, 0, 0, 255)
                            : new Color32(intensity, 0, (byte)(255 - intensity), 255);
                    }
                }

                var totalPixels = beforePixels.Length;
                var totalChannels = Math.Max(1, totalPixels * channelCount);
                var changedPixelRatio = totalPixels > 0 ? (float)changedPixels / totalPixels : 0;
                var meanAbsoluteError = (float)(absoluteErrorSum / totalChannels);
                var rootMeanSquareError = (float)Math.Sqrt(squaredErrorSum / totalChannels);
                var reasons = new List<string>();

                if (changedPixelRatio > thresholds.maxChangedPixelRatio)
                {
                    reasons.Add($"Changed pixel ratio {changedPixelRatio:F6} exceeds {thresholds.maxChangedPixelRatio:F6}.");
                }

                if (meanAbsoluteError > thresholds.maxMeanAbsoluteError)
                {
                    reasons.Add($"Mean absolute error {meanAbsoluteError:F6} exceeds {thresholds.maxMeanAbsoluteError:F6}.");
                }

                var regressionDetected = reasons.Count > 0;
                var diffPath = string.Empty;
                var diffReady = false;

                if (diffPixels != null)
                {
                    diff = new Texture2D(before.width, before.height, TextureFormat.RGBA32, false);
                    diff.SetPixels32(diffPixels);
                    diff.Apply();
                    diffPath = VisualArtifactStore.CreateRelativePath("visual-diff", input.label, ".png");
                    VisualArtifactStore.WritePng(diffPath, diff);
                    diffReady = VisualArtifactStore.TryInspectPng(diffPath, out _, out _);
                }

                return new VisualCompareResult
                {
                    compared = true,
                    regressionDetected = regressionDetected,
                    dimensionsMatch = true,
                    beforePath = SafeResponsePath(input.beforePath),
                    afterPath = SafeResponsePath(input.afterPath),
                    diffPath = diffPath,
                    diffReady = diffReady,
                    width = before.width,
                    height = before.height,
                    totalPixels = totalPixels,
                    changedPixels = changedPixels,
                    changedPixelRatio = changedPixelRatio,
                    meanAbsoluteError = meanAbsoluteError,
                    rootMeanSquareError = rootMeanSquareError,
                    maxChannelError = maxChannelError,
                    thresholds = thresholds,
                    regressionReasons = reasons.ToArray(),
                    verificationSignals = regressionDetected
                        ? new[] { "visual_diff_checked", "visual_regression_detected" }
                        : new[] { "visual_diff_checked", "visual_regression_absent" },
                    message = regressionDetected ? "Visual regression detected." : "No visual regression detected within configured thresholds.",
                    comparedAtUtc = DateTime.UtcNow.ToString("O")
                };
            }
            catch (Exception exception)
            {
                return new VisualCompareResult
                {
                    compared = false,
                    regressionDetected = false,
                    dimensionsMatch = false,
                    beforePath = SafeResponsePath(input.beforePath),
                    afterPath = SafeResponsePath(input.afterPath),
                    thresholds = thresholds,
                    regressionReasons = Array.Empty<string>(),
                    verificationSignals = Array.Empty<string>(),
                    message = $"Visual comparison failed: {exception.Message}",
                    comparedAtUtc = DateTime.UtcNow.ToString("O")
                };
            }
            finally
            {
                Destroy(before);
                Destroy(after);
                Destroy(diff);
            }
        }

        private static VisualCompareThresholds BuildThresholds(VisualCompareInput input)
        {
            return new VisualCompareThresholds
            {
                pixelThreshold = Mathf.Clamp01(input.pixelThreshold),
                maxChangedPixelRatio = Mathf.Clamp01(input.maxChangedPixelRatio),
                maxMeanAbsoluteError = Mathf.Clamp01(input.maxMeanAbsoluteError),
                ignoreAlpha = input.ignoreAlpha
            };
        }

        private static void Accumulate(float error, ref double absoluteErrorSum, ref double squaredErrorSum, ref float maxChannelError)
        {
            absoluteErrorSum += error;
            squaredErrorSum += error * error;
            maxChannelError = Math.Max(maxChannelError, error);
        }

        private static void Destroy(UnityEngine.Object value)
        {
            if (value != null)
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }

        private static string SafeResponsePath(string path)
        {
            var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
            return !System.IO.Path.IsPathRooted(normalized)
                && !normalized.Contains("..")
                && normalized.StartsWith(VisualArtifactStore.ArtifactDirectory + "/", StringComparison.Ordinal)
                    ? normalized
                    : string.Empty;
        }

        private static VisualCompareRequest ParseRequest(string requestBody)
        {
            try
            {
                return string.IsNullOrWhiteSpace(requestBody)
                    ? new VisualCompareRequest()
                    : JsonUtility.FromJson<VisualCompareRequest>(requestBody) ?? new VisualCompareRequest();
            }
            catch
            {
                return new VisualCompareRequest();
            }
        }
    }
}
