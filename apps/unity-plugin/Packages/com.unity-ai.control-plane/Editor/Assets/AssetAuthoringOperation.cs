using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class AssetAuthoringRequest
    {
        public AssetAuthoringInput input = new();
    }

    [Serializable]
    public sealed class AssetAuthoringInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string kind;
        public string path;
        public string shaderSource;
        public string shaderName;
        public string shaderPath;
        public MaterialPropertyInput[] materialProperties = Array.Empty<MaterialPropertyInput>();
        public string[] enabledKeywords = Array.Empty<string>();
        public int renderQueue = -1;
        public bool clearExistingCurves;
        public float frameRate = 60f;
        public AnimationCurveInput[] animationCurves = Array.Empty<AnimationCurveInput>();
        public AudioToneInput audioTone = new();
        public AudioImportInput audioImport = new();
    }

    [Serializable]
    public sealed class MaterialPropertyInput
    {
        public string name;
        public string kind;
        public float numberValue;
        public int integerValue;
        public string assetPath;
        public float x;
        public float y;
        public float z;
        public float w;
    }

    [Serializable]
    public sealed class AnimationCurveInput
    {
        public string relativePath;
        public string componentType;
        public string propertyName;
        public AnimationKeyframeInput[] keyframes = Array.Empty<AnimationKeyframeInput>();
    }

    [Serializable]
    public sealed class AnimationKeyframeInput
    {
        public float time;
        public float value;
        public float inTangent;
        public float outTangent;
    }

    [Serializable]
    public sealed class AudioToneInput
    {
        public float frequencyHz = 440f;
        public float durationSeconds = 1f;
        public int sampleRate = 44100;
        public int channels = 1;
        public float amplitude = 0.5f;
    }

    [Serializable]
    public sealed class AudioImportInput
    {
        public bool forceToMono;
        public bool loadInBackground;
        public bool preloadAudioData = true;
        public string loadType = "decompress_on_load";
        public string compressionFormat = "vorbis";
        public float quality = 0.7f;
        public int sampleRateOverride;
    }

    [Serializable]
    public sealed class AssetAuthoringResult
    {
        public bool dryRun;
        public bool created;
        public bool updated;
        public bool refused;
        public bool requiresConfirmation;
        public string kind;
        public string path;
        public string assetType;
        public string checkpointId;
        public string sha256;
        public string message;
        public string verificationStatus;
        public string[] verificationSignals = Array.Empty<string>();
        public string timestampUtc;
    }

    public static class AssetAuthoringOperation
    {
        public static AssetAuthoringResult Execute(string requestBody)
        {
            var input = ParseRequest(requestBody).input ?? new AssetAuthoringInput();
            var kind = (input.kind ?? string.Empty).Trim().ToLowerInvariant();
            var path = NormalizeAssetPath(input.path);

            if (!ValidateKindAndPath(kind, path, out var validationError))
            {
                return Refused(input.dryRun, kind, path, validationError);
            }

            if (input.dryRun)
            {
                return new AssetAuthoringResult
                {
                    dryRun = true,
                    kind = kind,
                    path = path,
                    message = $"DRY RUN: would author {kind} asset '{path}' with a durable checkpoint.",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return new AssetAuthoringResult
                {
                    kind = kind,
                    path = path,
                    requiresConfirmation = true,
                    message = "Asset authoring requires confirm=true.",
                    verificationStatus = "needs_confirmation",
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            try
            {
                var existed = File.Exists(ResolveAssetPath(path)) || AssetDatabase.LoadMainAssetAtPath(path) != null;
                var checkpoint = DurableCheckpointStore.CreateInternal("asset-" + kind, new[] { path, path + ".meta" });
                EnsureParentDirectory(path);

                switch (kind)
                {
                    case "shader":
                        AuthorShader(path, input);
                        break;
                    case "material":
                        AuthorMaterial(path, input);
                        break;
                    case "animation_clip":
                        AuthorAnimationClip(path, input);
                        break;
                    case "audio_tone":
                        AuthorAudioTone(path, input);
                        break;
                    case "audio_import":
                        AuthorAudioImport(path, input.audioImport);
                        break;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                var verified = VerifyAsset(kind, asset);
                return new AssetAuthoringResult
                {
                    created = !existed,
                    updated = existed,
                    kind = kind,
                    path = path,
                    assetType = asset != null ? asset.GetType().FullName : string.Empty,
                    checkpointId = checkpoint.checkpointId,
                    sha256 = File.Exists(ResolveAssetPath(path)) ? ComputeSha256(ResolveAssetPath(path)) : string.Empty,
                    message = verified ? $"Authored and verified '{path}'." : $"Authored '{path}', but type verification failed.",
                    verificationStatus = verified ? "passed" : "failed",
                    verificationSignals = verified
                        ? new[] { "checkpoint_created", "asset_mutation_verified" }
                        : new[] { "checkpoint_created" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }
            catch (Exception exception)
            {
                return Refused(false, kind, path, exception.GetBaseException().Message);
            }
        }

        private static void AuthorShader(string path, AssetAuthoringInput input)
        {
            var source = input.shaderSource ?? string.Empty;
            if (source.Length == 0 || source.Length > 1024 * 1024 || !source.Contains("Shader \""))
            {
                throw new InvalidOperationException("shaderSource must contain a Shader declaration and be at most 1 MiB.");
            }

            File.WriteAllText(ResolveAssetPath(path), source, new UTF8Encoding(false));
        }

        private static void AuthorMaterial(string path, AssetAuthoringInput input)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = !string.IsNullOrWhiteSpace(input.shaderPath)
                ? AssetDatabase.LoadAssetAtPath<Shader>(NormalizeAssetPath(input.shaderPath))
                : Shader.Find(string.IsNullOrWhiteSpace(input.shaderName) ? "Universal Render Pipeline/Lit" : input.shaderName.Trim());
            shader ??= Shader.Find("Standard");
            shader ??= Shader.Find("Unlit/Color");
            if (shader == null)
            {
                throw new InvalidOperationException("The requested material shader could not be resolved.");
            }

            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            foreach (var property in input.materialProperties ?? Array.Empty<MaterialPropertyInput>())
            {
                ApplyMaterialProperty(material, property);
            }

            material.shaderKeywords = (input.enabledKeywords ?? Array.Empty<string>())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword) && keyword.Length <= 128)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (input.renderQueue >= 0)
            {
                material.renderQueue = Mathf.Clamp(input.renderQueue, 0, 5000);
            }

            EditorUtility.SetDirty(material);
        }

        private static void ApplyMaterialProperty(Material material, MaterialPropertyInput property)
        {
            if (property == null || string.IsNullOrWhiteSpace(property.name) || !material.HasProperty(property.name))
            {
                throw new InvalidOperationException($"Material property '{property?.name}' does not exist on shader '{material.shader.name}'.");
            }

            switch ((property.kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "float":
                    material.SetFloat(property.name, property.numberValue);
                    break;
                case "int":
                    material.SetInt(property.name, property.integerValue);
                    break;
                case "color":
                    material.SetColor(property.name, new Color(property.x, property.y, property.z, property.w));
                    break;
                case "vector":
                    material.SetVector(property.name, new Vector4(property.x, property.y, property.z, property.w));
                    break;
                case "texture":
                    var texturePath = NormalizeAssetPath(property.assetPath);
                    var texture = string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                    if (!string.IsNullOrEmpty(texturePath) && texture == null)
                    {
                        throw new InvalidOperationException($"Texture asset '{texturePath}' was not found.");
                    }

                    material.SetTexture(property.name, texture);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported material property kind '{property.kind}'.");
            }
        }

        private static void AuthorAnimationClip(string path, AssetAuthoringInput input)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, path);
            }

            clip.frameRate = Mathf.Clamp(input.frameRate, 1f, 240f);
            if (input.clearExistingCurves)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                }
            }

            foreach (var curveInput in input.animationCurves ?? Array.Empty<AnimationCurveInput>())
            {
                if (curveInput == null || string.IsNullOrWhiteSpace(curveInput.propertyName))
                {
                    throw new InvalidOperationException("Each animation curve requires propertyName.");
                }

                var componentType = ResolveAnimationType(curveInput.componentType);
                var keyframes = (curveInput.keyframes ?? Array.Empty<AnimationKeyframeInput>())
                    .OrderBy(key => key.time)
                    .Select(key => new Keyframe(key.time, key.value, key.inTangent, key.outTangent))
                    .ToArray();
                if (keyframes.Length == 0)
                {
                    throw new InvalidOperationException("Each animation curve requires at least one keyframe.");
                }

                var binding = EditorCurveBinding.FloatCurve(
                    NormalizeRelativeHierarchyPath(curveInput.relativePath),
                    componentType,
                    curveInput.propertyName.Trim());
                AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(keyframes));
            }

            EditorUtility.SetDirty(clip);
        }

        private static void AuthorAudioTone(string path, AssetAuthoringInput input)
        {
            var tone = input.audioTone ?? new AudioToneInput();
            var sampleRate = Mathf.Clamp(tone.sampleRate, 8000, 192000);
            var channels = Mathf.Clamp(tone.channels, 1, 2);
            var duration = Mathf.Clamp(tone.durationSeconds, 0.01f, 300f);
            var frequency = Mathf.Clamp(tone.frequencyHz, 1f, sampleRate * 0.45f);
            var amplitude = Mathf.Clamp01(tone.amplitude);
            var frameCount = Mathf.CeilToInt(duration * sampleRate);
            WritePcm16Wave(ResolveAssetPath(path), frameCount, channels, sampleRate, index =>
            {
                var frame = index / channels;
                return Mathf.Sin(2f * Mathf.PI * frequency * frame / sampleRate) * amplitude;
            });
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AuthorAudioImport(path, input.audioImport);
        }

        private static void AuthorAudioImport(string path, AudioImportInput input)
        {
            input ??= new AudioImportInput();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null)
            {
                throw new InvalidOperationException($"Asset '{path}' is not importable audio.");
            }

            importer.forceToMono = input.forceToMono;
            importer.loadInBackground = input.loadInBackground;
            var settings = importer.defaultSampleSettings;
            settings.preloadAudioData = input.preloadAudioData;
            settings.loadType = ParseLoadType(input.loadType);
            settings.compressionFormat = ParseCompressionFormat(input.compressionFormat);
            settings.quality = Mathf.Clamp01(input.quality);
            if (input.sampleRateOverride > 0)
            {
                settings.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                settings.sampleRateOverride = (uint)Mathf.Clamp(input.sampleRateOverride, 8000, 192000);
            }
            else
            {
                settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            }

            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
        }

        private static AudioClipLoadType ParseLoadType(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "compressed_in_memory": return AudioClipLoadType.CompressedInMemory;
                case "streaming": return AudioClipLoadType.Streaming;
                default: return AudioClipLoadType.DecompressOnLoad;
            }
        }

        private static AudioCompressionFormat ParseCompressionFormat(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "pcm": return AudioCompressionFormat.PCM;
                case "adpcm": return AudioCompressionFormat.ADPCM;
                default: return AudioCompressionFormat.Vorbis;
            }
        }

        private static Type ResolveAnimationType(string typeName)
        {
            var normalized = string.IsNullOrWhiteSpace(typeName) ? "UnityEngine.Transform" : typeName.Trim();
            var type = Type.GetType(normalized, false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(normalized, false))
                    .FirstOrDefault(candidate => candidate != null);
            if (type == null || (!typeof(Component).IsAssignableFrom(type) && type != typeof(GameObject)))
            {
                throw new InvalidOperationException($"Animation component type '{normalized}' was not found or is not animatable.");
            }

            return type;
        }

        private static void WritePcm16Wave(string path, int frames, int channels, int sampleRate, Func<int, float> sample)
        {
            var sampleCount = checked(frames * channels);
            var dataLength = checked(sampleCount * 2);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            for (var index = 0; index < sampleCount; index++)
            {
                writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample(index), -1f, 1f) * short.MaxValue));
            }
        }

        private static bool ValidateKindAndPath(string kind, string path, out string error)
        {
            var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shader"] = ".shader",
                ["material"] = ".mat",
                ["animation_clip"] = ".anim",
                ["audio_tone"] = ".wav"
            };
            if (kind == "audio_import")
            {
                if (!IsSafeAssetPath(path) || !new[] { ".wav", ".mp3", ".ogg", ".aif", ".aiff" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    error = "audio_import path must reference a supported audio file under Assets.";
                    return false;
                }

                error = string.Empty;
                return true;
            }

            if (!extensions.TryGetValue(kind, out var extension))
            {
                error = "kind must be shader, material, animation_clip, audio_tone, or audio_import.";
                return false;
            }

            if (!IsSafeAssetPath(path) || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                error = $"{kind} path must end with {extension} and remain under Assets.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool VerifyAsset(string kind, UnityEngine.Object asset)
        {
            return kind switch
            {
                "shader" => asset is Shader,
                "material" => asset is Material,
                "animation_clip" => asset is AnimationClip,
                "audio_tone" => asset is AudioClip,
                "audio_import" => asset is AudioClip,
                _ => false
            };
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static bool IsSafeAssetPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && path.StartsWith("Assets/", StringComparison.Ordinal)
                && !path.Contains("..")
                && !Path.IsPathRooted(path)
                && path.Length <= 512;
        }

        private static string NormalizeRelativeHierarchyPath(string path)
        {
            var normalized = (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (normalized.Contains(".."))
            {
                throw new InvalidOperationException("Animation relativePath cannot contain '..'.");
            }

            return normalized;
        }

        private static void EnsureParentDirectory(string path)
        {
            var parent = Path.GetDirectoryName(ResolveAssetPath(path));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        private static string ResolveAssetPath(string path)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            var assetsRoot = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(assetsRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Asset path escapes Assets.");
            }

            return absolute;
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static AssetAuthoringResult Refused(bool dryRun, string kind, string path, string message)
        {
            return new AssetAuthoringResult
            {
                dryRun = dryRun,
                refused = true,
                kind = kind,
                path = path,
                message = message,
                verificationStatus = "failed",
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static AssetAuthoringRequest ParseRequest(string requestBody)
        {
            try { return JsonUtility.FromJson<AssetAuthoringRequest>(requestBody) ?? new AssetAuthoringRequest(); }
            catch { return new AssetAuthoringRequest(); }
        }
    }
}
