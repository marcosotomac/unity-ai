using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class AssetImportRequest
    {
        public string requestId;
        public string correlationId;
        public AssetImportInput input = new();
    }

    [Serializable]
    public sealed class AssetImportInput
    {
        public bool dryRun = true;
        public bool confirm;
        public string sourceKind = "local";
        public string sourcePath;
        public string url;
        public string destinationPath;
        public bool overwrite;
        public string expectedSha256;
        public long maxBytes = 268435456;
        public int timeoutSeconds = 120;
        public bool allowInsecureLocalhost;
        public ModelImportSettingsInput model = new();
        public TextureImportSettingsInput texture = new();
        public AudioImportInput audio = new();
        public bool instantiate;
        public string objectName;
        public string parentPath;
        public ImportedAssetTransformInput transform = new();
        public string saveAsPrefabPath;
    }

    [Serializable]
    public sealed class ModelImportSettingsInput
    {
        public float globalScale = 1f;
        public bool useFileScale = true;
        public bool importBlendShapes = true;
        public bool importVisibility = true;
        public bool importCameras;
        public bool importLights;
        public bool addCollider;
        public bool importAnimation = true;
        public string animationType = "generic";
        public bool isReadable;
        public string meshCompression = "off";
    }

    [Serializable]
    public sealed class TextureImportSettingsInput
    {
        public string textureType = "default";
        public bool sRgb = true;
        public bool alphaIsTransparency;
        public bool mipmapEnabled = true;
        public bool isReadable;
        public int maxTextureSize = 2048;
        public string compression = "compressed";
    }

    [Serializable]
    public sealed class ImportedAssetTransformInput
    {
        public SceneAuthoringVector3 position = new();
        public SceneAuthoringVector3 rotationEuler = new();
        public SceneAuthoringVector3 scale = new() { x = 1f, y = 1f, z = 1f };
    }

    [Serializable]
    public sealed class AssetImportResult
    {
        public bool dryRun;
        public bool imported;
        public bool downloaded;
        public bool copied;
        public bool instantiated;
        public bool prefabCreated;
        public bool refused;
        public bool requiresConfirmation;
        public string requestId;
        public string correlationId;
        public string sourceKind;
        public string destinationPath;
        public string assetType;
        public string importerType;
        public string guid;
        public long byteLength;
        public string sha256;
        public string checkpointId;
        public string objectPath;
        public string prefabPath;
        public string message;
        public string verificationStatus;
        public string[] verificationSignals = Array.Empty<string>();
        public bool auditPersisted;
        public UnityAiAuditEvent auditEvent;
        public string auditLogPath;
        public string timestampUtc;
    }

    public static class AssetImportOperation
    {
        private const string Capability = "unity.assets.import";
        private const long MaximumBytes = 2L * 1024 * 1024 * 1024;
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".obj", ".dae", ".3ds", ".dxf", ".glb", ".gltf",
            ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".psd", ".exr", ".hdr",
            ".wav", ".mp3", ".ogg", ".aif", ".aiff"
        };

        public static AssetImportResult Execute(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var input = request.input ?? new AssetImportInput();
            var timestamp = DateTime.UtcNow.ToString("O");
            var sourceKind = Normalize(input.sourceKind);
            var destinationPath = NormalizeAssetPath(input.destinationPath);
            var prefabPath = NormalizeAssetPath(input.saveAsPrefabPath);

            if (!Validate(input, sourceKind, destinationPath, prefabPath, out var validationError))
            {
                return Refused(request, sourceKind, destinationPath, validationError, timestamp);
            }

            if (input.dryRun)
            {
                return new AssetImportResult
                {
                    dryRun = true,
                    requestId = request.requestId,
                    correlationId = request.correlationId,
                    sourceKind = sourceKind,
                    destinationPath = destinationPath,
                    prefabPath = prefabPath,
                    message = $"DRY RUN: would import {DescribeSource(input, sourceKind)} to '{destinationPath}'.",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = timestamp
                };
            }

            if (!input.confirm)
            {
                return new AssetImportResult
                {
                    requestId = request.requestId,
                    correlationId = request.correlationId,
                    sourceKind = sourceKind,
                    destinationPath = destinationPath,
                    prefabPath = prefabPath,
                    requiresConfirmation = true,
                    message = "Asset import requires confirm=true.",
                    verificationStatus = "needs_confirmation",
                    timestampUtc = timestamp
                };
            }

            var destinationAbsolutePath = ResolveAssetPath(destinationPath);
            if (!input.overwrite && File.Exists(destinationAbsolutePath))
            {
                return Refused(request, sourceKind, destinationPath, "Destination already exists and overwrite=false.", timestamp);
            }

            var checkpointPaths = new List<string> { destinationPath, destinationPath + ".meta" };
            if (!string.IsNullOrEmpty(prefabPath))
            {
                checkpointPaths.Add(prefabPath);
                checkpointPaths.Add(prefabPath + ".meta");
            }

            var activeScene = EditorSceneManager.GetActiveScene();
            if (input.instantiate && activeScene.IsValid() && !string.IsNullOrWhiteSpace(activeScene.path))
            {
                checkpointPaths.Add(activeScene.path);
                checkpointPaths.Add(activeScene.path + ".meta");
            }

            var undoGroup = -1;
            try
            {
                var checkpoint = DurableCheckpointStore.CreateInternal("asset-import", checkpointPaths.Distinct().ToArray());
                if (input.instantiate)
                {
                    Undo.IncrementCurrentGroup();
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Unity AI Import And Instantiate Asset");
                }

                EnsureParentDirectory(destinationAbsolutePath);
                var maxBytes = Math.Max(1, Math.Min(input.maxBytes, MaximumBytes));
                var stagedPath = CreateStagedPath();
                try
                {
                    if (sourceKind == "url")
                    {
                        Download(input.url, stagedPath, maxBytes, Math.Max(5, Math.Min(input.timeoutSeconds, 1800)), input.allowInsecureLocalhost);
                    }
                    else
                    {
                        CopyLocal(input.sourcePath, stagedPath, maxBytes);
                    }

                    var byteLength = new FileInfo(stagedPath).Length;
                    var sha256 = ComputeSha256(stagedPath);
                    var expectedHash = NormalizeHash(input.expectedSha256);
                    if (!string.IsNullOrEmpty(expectedHash) && !string.Equals(expectedHash, sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Downloaded/copied asset SHA-256 '{sha256}' does not match expectedSha256.");
                    }

                    File.Copy(stagedPath, destinationAbsolutePath, true);
                    AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    ApplyImporterSettings(destinationPath, input);
                    AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                    var asset = AssetDatabase.LoadMainAssetAtPath(destinationPath);
                    if (asset == null)
                    {
                        throw new InvalidOperationException($"Unity did not import a main asset at '{destinationPath}'.");
                    }

                    var objectPath = string.Empty;
                    var instantiated = false;
                    var prefabCreated = false;
                    if (input.instantiate)
                    {
                        if (!(asset is GameObject model))
                        {
                            throw new InvalidOperationException("instantiate=true requires a model asset whose main asset is a GameObject.");
                        }

                        var instance = InstantiateModel(model, input);
                        objectPath = GetGameObjectPath(instance);
                        instantiated = true;
                        if (!string.IsNullOrEmpty(prefabPath))
                        {
                            EnsureParentDirectory(ResolveAssetPath(prefabPath));
                            var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                            prefabCreated = saved != null;
                            if (!prefabCreated)
                            {
                                throw new InvalidOperationException($"Unity did not create prefab '{prefabPath}'.");
                            }
                        }

                        EditorSceneManager.MarkSceneDirty(activeScene);
                    }

                    var importer = AssetImporter.GetAtPath(destinationPath);
                    var verifiedHash = ComputeSha256(destinationAbsolutePath);
                    var verified = asset != null
                        && string.Equals(sha256, verifiedHash, StringComparison.Ordinal)
                        && (!input.instantiate || FindByPath(objectPath) != null)
                        && (string.IsNullOrEmpty(prefabPath) || AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null);
                    var auditEvent = new UnityAiAuditEvent
                    {
                        timestamp = DateTime.UtcNow.ToString("O"),
                        capability = Capability,
                        requestId = request.requestId,
                        correlationId = request.correlationId,
                        message = $"Imported '{destinationPath}' from {sourceKind}; instantiated={instantiated}; prefabCreated={prefabCreated}.",
                        effects = BuildEffects(instantiated, prefabCreated, true)
                    };
                    var auditPersisted = PersistAudit(auditEvent);
                    if (!auditPersisted)
                    {
                        auditEvent.effects = BuildEffects(instantiated, prefabCreated, false);
                    }

                    var signals = new List<string> { "checkpoint_created", "asset_import_verified" };
                    if (instantiated)
                    {
                        signals.Add("scene_mutation_verified");
                    }

                    if (prefabCreated)
                    {
                        signals.Add("prefab_mutation_verified");
                    }

                    if (auditPersisted)
                    {
                        signals.Add("operation_audited");
                    }

                    if (undoGroup >= 0)
                    {
                        Undo.CollapseUndoOperations(undoGroup);
                    }

                    return new AssetImportResult
                    {
                        imported = verified,
                        downloaded = sourceKind == "url",
                        copied = sourceKind == "local",
                        instantiated = instantiated,
                        prefabCreated = prefabCreated,
                        requestId = request.requestId,
                        correlationId = request.correlationId,
                        sourceKind = sourceKind,
                        destinationPath = destinationPath,
                        assetType = asset.GetType().FullName,
                        importerType = importer != null ? importer.GetType().FullName : string.Empty,
                        guid = AssetDatabase.AssetPathToGUID(destinationPath),
                        byteLength = byteLength,
                        sha256 = verifiedHash,
                        checkpointId = checkpoint.checkpointId,
                        objectPath = objectPath,
                        prefabPath = prefabPath,
                        message = verified ? $"Imported and verified '{destinationPath}'." : $"Imported '{destinationPath}', but verification failed.",
                        verificationStatus = verified ? "passed" : "failed",
                        verificationSignals = signals.ToArray(),
                        auditPersisted = auditPersisted,
                        auditEvent = auditEvent,
                        auditLogPath = AuditLogStore.AuditLogRelativePath.Replace('\\', '/'),
                        timestampUtc = timestamp
                    };
                }
                finally
                {
                    if (File.Exists(stagedPath))
                    {
                        File.Delete(stagedPath);
                    }
                }
            }
            catch (Exception exception)
            {
                if (undoGroup >= 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                }

                return Refused(request, sourceKind, destinationPath, exception.GetBaseException().Message, timestamp);
            }
        }

        private static void ApplyImporterSettings(string assetPath, AssetImportInput input)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            switch (importer)
            {
                case ModelImporter modelImporter:
                    ApplyModelSettings(modelImporter, input.model ?? new ModelImportSettingsInput());
                    modelImporter.SaveAndReimport();
                    return;
                case TextureImporter textureImporter:
                    ApplyTextureSettings(textureImporter, input.texture ?? new TextureImportSettingsInput());
                    textureImporter.SaveAndReimport();
                    return;
                case AudioImporter audioImporter:
                    ApplyAudioSettings(audioImporter, input.audio ?? new AudioImportInput());
                    audioImporter.SaveAndReimport();
                    return;
            }
        }

        private static void ApplyModelSettings(ModelImporter importer, ModelImportSettingsInput input)
        {
            importer.globalScale = Mathf.Clamp(input.globalScale, 0.0001f, 10000f);
            importer.useFileScale = input.useFileScale;
            importer.importBlendShapes = input.importBlendShapes;
            importer.importVisibility = input.importVisibility;
            importer.importCameras = input.importCameras;
            importer.importLights = input.importLights;
            importer.addCollider = input.addCollider;
            importer.importAnimation = input.importAnimation;
            importer.animationType = ParseAnimationType(input.animationType);
            importer.isReadable = input.isReadable;
            importer.meshCompression = ParseMeshCompression(input.meshCompression);
        }

        private static void ApplyTextureSettings(TextureImporter importer, TextureImportSettingsInput input)
        {
            importer.textureType = ParseTextureType(input.textureType);
            importer.sRGBTexture = input.sRgb;
            importer.alphaIsTransparency = input.alphaIsTransparency;
            importer.mipmapEnabled = input.mipmapEnabled;
            importer.isReadable = input.isReadable;
            importer.maxTextureSize = ClampTextureSize(input.maxTextureSize);
            importer.textureCompression = ParseTextureCompression(input.compression);
        }

        private static void ApplyAudioSettings(AudioImporter importer, AudioImportInput input)
        {
            importer.forceToMono = input.forceToMono;
            importer.loadInBackground = input.loadInBackground;
            var settings = importer.defaultSampleSettings;
            settings.preloadAudioData = input.preloadAudioData;
            settings.loadType = ParseAudioLoadType(input.loadType);
            settings.compressionFormat = ParseAudioCompression(input.compressionFormat);
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
        }

        private static GameObject InstantiateModel(GameObject model, AssetImportInput input)
        {
            var instance = PrefabUtility.InstantiatePrefab(model) as GameObject;
            if (instance == null)
            {
                instance = UnityEngine.Object.Instantiate(model);
            }

            instance.name = string.IsNullOrWhiteSpace(input.objectName)
                ? Path.GetFileNameWithoutExtension(input.destinationPath)
                : RequireSafeName(input.objectName);
            Undo.RegisterCreatedObjectUndo(instance, "Unity AI Import Asset");
            var parent = FindByPath(NormalizeHierarchyPath(input.parentPath));
            if (!string.IsNullOrWhiteSpace(input.parentPath) && parent == null)
            {
                throw new InvalidOperationException($"Parent GameObject '{input.parentPath}' was not found.");
            }

            instance.transform.SetParent(parent != null ? parent.transform : null, false);
            var transform = input.transform ?? new ImportedAssetTransformInput();
            instance.transform.localPosition = ToVector3(transform.position, Vector3.zero);
            instance.transform.localEulerAngles = ToVector3(transform.rotationEuler, Vector3.zero);
            instance.transform.localScale = ToVector3(transform.scale, Vector3.one);
            return instance;
        }

        private static void CopyLocal(string sourcePath, string destinationPath, long maxBytes)
        {
            var source = (sourcePath ?? string.Empty).Trim();
            if (!Path.IsPathRooted(source) || !File.Exists(source))
            {
                throw new InvalidOperationException("sourcePath must be an existing absolute file path.");
            }

            var file = new FileInfo(source);
            if (file.Length <= 0 || file.Length > maxBytes)
            {
                throw new InvalidOperationException($"Local source size must be between 1 and {maxBytes} bytes.");
            }

            File.Copy(source, destinationPath, true);
        }

        private static void Download(string rawUrl, string destinationPath, long maxBytes, int timeoutSeconds, bool allowInsecureLocalhost)
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var current))
            {
                throw new InvalidOperationException("url must be an absolute HTTPS URL.");
            }

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UnityAI-ControlPlane/0.1");

            for (var redirect = 0; redirect <= 5; redirect++)
            {
                ValidateDownloadUri(current, allowInsecureLocalhost);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                {
                    if (redirect == 5 || response.Headers.Location == null)
                    {
                        throw new InvalidOperationException("Asset download exceeded the redirect limit or returned an invalid redirect.");
                    }

                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > maxBytes)
                {
                    throw new InvalidOperationException($"Remote asset exceeds maxBytes ({maxBytes}).");
                }

                using var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        throw new InvalidOperationException($"Remote asset exceeds maxBytes ({maxBytes}).");
                    }

                    destination.Write(buffer, 0, read);
                }

                if (total == 0)
                {
                    throw new InvalidOperationException("Remote asset response was empty.");
                }

                return;
            }
        }

        private static void ValidateDownloadUri(Uri uri, bool allowInsecureLocalhost)
        {
            var loopback = uri.IsLoopback
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            if (uri.Scheme == Uri.UriSchemeHttp && !(allowInsecureLocalhost && loopback))
            {
                throw new InvalidOperationException("Remote imports require HTTPS; HTTP is only allowed for explicit localhost testing.");
            }

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            {
                throw new InvalidOperationException("Remote imports support only HTTPS URLs.");
            }

            if (loopback)
            {
                if (!allowInsecureLocalhost)
                {
                    throw new InvalidOperationException("Loopback asset URLs require allowInsecureLocalhost=true.");
                }

                return;
            }

            foreach (var address in Dns.GetHostAddresses(uri.DnsSafeHost))
            {
                if (IsPrivateAddress(address))
                {
                    throw new InvalidOperationException("Remote asset URLs cannot resolve to private or link-local addresses.");
                }
            }
        }

        private static bool IsPrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 10
                    || bytes[0] == 127
                    || (bytes[0] == 169 && bytes[1] == 254)
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168);
            }

            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.Equals(IPAddress.IPv6Loopback)
                || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
        }

        private static bool Validate(AssetImportInput input, string sourceKind, string destinationPath, string prefabPath, out string error)
        {
            if (sourceKind != "local" && sourceKind != "url")
            {
                error = "sourceKind must be local or url.";
                return false;
            }

            var extension = Path.GetExtension(destinationPath);
            if (!IsSafeAssetPath(destinationPath) || !SupportedExtensions.Contains(extension))
            {
                error = "destinationPath must use a supported model, texture, or audio extension under Assets.";
                return false;
            }

            if (sourceKind == "local" && string.IsNullOrWhiteSpace(input.sourcePath))
            {
                error = "sourcePath is required for local imports.";
                return false;
            }

            if (sourceKind == "url" && string.IsNullOrWhiteSpace(input.url))
            {
                error = "url is required for remote imports.";
                return false;
            }

            var expectedHash = (input.expectedSha256 ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(expectedHash) && (expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character))))
            {
                error = "expectedSha256 must be a 64-character hexadecimal value.";
                return false;
            }

            if (input.instantiate && !IsModelExtension(extension))
            {
                error = "instantiate=true requires a supported 3D model destination extension.";
                return false;
            }

            if (!string.IsNullOrEmpty(prefabPath) && (!input.instantiate || !IsSafePrefabPath(prefabPath)))
            {
                error = "saveAsPrefabPath requires instantiate=true and a .prefab path under Assets.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(input.objectName) && !IsSafeName(input.objectName))
            {
                error = "objectName is invalid.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(input.parentPath) && string.IsNullOrEmpty(NormalizeHierarchyPath(input.parentPath)))
            {
                error = "parentPath is invalid.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static string[] BuildEffects(bool instantiated, bool prefabCreated, bool auditPersisted)
        {
            var effects = new List<string> { "asset_change" };
            if (instantiated || prefabCreated)
            {
                effects.Add("scene_change");
            }

            if (auditPersisted)
            {
                effects.Add("write_audit_log");
            }

            return effects.Distinct().ToArray();
        }

        private static bool PersistAudit(UnityAiAuditEvent auditEvent)
        {
            try
            {
                AuditLogStore.Append(new[] { auditEvent });
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to persist asset import audit event: {exception.Message}");
                return false;
            }
        }

        private static ModelImporterAnimationType ParseAnimationType(string value)
        {
            return Normalize(value) switch
            {
                "none" => ModelImporterAnimationType.None,
                "legacy" => ModelImporterAnimationType.Legacy,
                "human" => ModelImporterAnimationType.Human,
                _ => ModelImporterAnimationType.Generic
            };
        }

        private static ModelImporterMeshCompression ParseMeshCompression(string value)
        {
            return Normalize(value) switch
            {
                "low" => ModelImporterMeshCompression.Low,
                "medium" => ModelImporterMeshCompression.Medium,
                "high" => ModelImporterMeshCompression.High,
                _ => ModelImporterMeshCompression.Off
            };
        }

        private static TextureImporterType ParseTextureType(string value)
        {
            return Normalize(value) switch
            {
                "normal_map" => TextureImporterType.NormalMap,
                "sprite" => TextureImporterType.Sprite,
                "cursor" => TextureImporterType.Cursor,
                "cookie" => TextureImporterType.Cookie,
                "lightmap" => TextureImporterType.Lightmap,
                "single_channel" => TextureImporterType.SingleChannel,
                _ => TextureImporterType.Default
            };
        }

        private static TextureImporterCompression ParseTextureCompression(string value)
        {
            return Normalize(value) switch
            {
                "uncompressed" => TextureImporterCompression.Uncompressed,
                "compressed_hq" => TextureImporterCompression.CompressedHQ,
                "compressed_lq" => TextureImporterCompression.CompressedLQ,
                _ => TextureImporterCompression.Compressed
            };
        }

        private static AudioClipLoadType ParseAudioLoadType(string value)
        {
            return Normalize(value) switch
            {
                "compressed_in_memory" => AudioClipLoadType.CompressedInMemory,
                "streaming" => AudioClipLoadType.Streaming,
                _ => AudioClipLoadType.DecompressOnLoad
            };
        }

        private static AudioCompressionFormat ParseAudioCompression(string value)
        {
            return Normalize(value) switch
            {
                "pcm" => AudioCompressionFormat.PCM,
                "adpcm" => AudioCompressionFormat.ADPCM,
                _ => AudioCompressionFormat.Vorbis
            };
        }

        private static int ClampTextureSize(int value)
        {
            var sizes = new[] { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };
            var requested = Math.Max(32, Math.Min(value, 16384));
            return sizes.OrderBy(size => Math.Abs(size - requested)).First();
        }

        private static Vector3 ToVector3(SceneAuthoringVector3 input, Vector3 fallback)
        {
            return input == null ? fallback : new Vector3(input.x, input.y, input.z);
        }

        private static string RequireSafeName(string value)
        {
            if (!IsSafeName(value))
            {
                throw new InvalidOperationException("objectName is invalid.");
            }

            return value.Trim();
        }

        private static bool IsSafeName(string value)
        {
            var name = (value ?? string.Empty).Trim();
            return name.Length > 0
                && name.Length <= 80
                && !name.Contains("/")
                && !name.Contains("\\")
                && !name.Contains("..")
                && name.All(character => !char.IsControl(character));
        }

        private static bool IsSafeAssetPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && path.StartsWith("Assets/", StringComparison.Ordinal)
                && !path.Contains("..")
                && !Path.IsPathRooted(path)
                && path.Length <= 512;
        }

        private static bool IsSafePrefabPath(string path)
        {
            return IsSafeAssetPath(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelExtension(string extension)
        {
            return new[] { ".fbx", ".obj", ".dae", ".3ds", ".dxf", ".glb", ".gltf" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeAssetPath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static string NormalizeHierarchyPath(string value)
        {
            var path = (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            return path.Length <= 512 && !path.Contains("..") && !path.Contains("//") ? path : string.Empty;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizeHash(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string ResolveAssetPath(string path)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            var assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!absolute.StartsWith(assetsRoot, comparison))
            {
                throw new InvalidOperationException("Asset path escapes Assets.");
            }

            return absolute;
        }

        private static void EnsureParentDirectory(string absolutePath)
        {
            var parent = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        private static string CreateStagedPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var directory = Path.Combine(projectRoot, "Library", "UnityAIControlPlane", "Downloads");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static GameObject FindByPath(string path)
        {
            var normalized = NormalizeHierarchyPath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == normalized)
                {
                    return root;
                }

                if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
                {
                    var child = root.transform.Find(normalized.Substring(root.name.Length + 1));
                    if (child != null)
                    {
                        return child.gameObject;
                    }
                }
            }

            return null;
        }

        private static string GetGameObjectPath(GameObject gameObject)
        {
            var path = gameObject.name;
            var parent = gameObject.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static string DescribeSource(AssetImportInput input, string sourceKind)
        {
            if (sourceKind == "url")
            {
                return "remote asset";
            }

            return string.IsNullOrWhiteSpace(input.sourcePath)
                ? "local asset"
                : $"local file '{Path.GetFileName(input.sourcePath)}'";
        }

        private static AssetImportResult Refused(
            AssetImportRequest request,
            string sourceKind,
            string destinationPath,
            string message,
            string timestamp)
        {
            return new AssetImportResult
            {
                dryRun = request.input != null && request.input.dryRun,
                refused = true,
                requestId = request.requestId,
                correlationId = request.correlationId,
                sourceKind = sourceKind,
                destinationPath = destinationPath,
                message = message,
                verificationStatus = "failed",
                timestampUtc = timestamp
            };
        }

        private static AssetImportRequest ParseRequest(string body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body)
                    ? new AssetImportRequest()
                    : JsonUtility.FromJson<AssetImportRequest>(body) ?? new AssetImportRequest();
            }
            catch
            {
                return new AssetImportRequest();
            }
        }
    }
}
