using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class CheckpointFileEntry
    {
        public string path;
        public long byteLength;
        public string sha256;
    }

    [Serializable]
    public sealed class CheckpointManifest
    {
        public string checkpointId;
        public string label;
        public string createdAtUtc;
        public string unityVersion;
        public string[] includedPaths = Array.Empty<string>();
        public string[] missingPaths = Array.Empty<string>();
        public int fileCount;
        public long totalBytes;
        public CheckpointFileEntry[] files = Array.Empty<CheckpointFileEntry>();
    }

    [Serializable]
    public sealed class CheckpointCreateRequest
    {
        public CheckpointCreateInput input = new();
    }

    [Serializable]
    public sealed class CheckpointCreateInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string label;
        public string[] paths = Array.Empty<string>();
        public int maxFiles = 20000;
        public long maxBytes = 1073741824;
    }

    [Serializable]
    public sealed class CheckpointRestoreRequest
    {
        public CheckpointRestoreInput input = new();
    }

    [Serializable]
    public sealed class CheckpointRestoreInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string checkpointId;
        public bool createSafetyCheckpoint = true;
    }

    [Serializable]
    public sealed class CheckpointDeleteRequest
    {
        public CheckpointDeleteInput input = new();
    }

    [Serializable]
    public sealed class CheckpointDeleteInput
    {
        public bool dryRun = true;
        public bool confirm = false;
        public string checkpointId;
    }

    [Serializable]
    public sealed class CheckpointOperationResult
    {
        public bool dryRun;
        public bool created;
        public bool restored;
        public bool deleted;
        public bool refused;
        public bool requiresConfirmation;
        public string checkpointId;
        public string safetyCheckpointId;
        public string path;
        public int fileCount;
        public long totalBytes;
        public string message;
        public string verificationStatus;
        public string[] verificationSignals = Array.Empty<string>();
        public string timestampUtc;
    }

    [Serializable]
    public sealed class CheckpointListResult
    {
        public int totalFound;
        public CheckpointManifest[] checkpoints = Array.Empty<CheckpointManifest>();
        public string capturedAtUtc;
    }

    public static class DurableCheckpointStore
    {
        private const string CheckpointRoot = "UnityAIArtifacts/Checkpoints";
        private const string DataDirectory = "data";
        private const string ManifestFile = "manifest.json";

        public static CheckpointOperationResult Create(string requestBody)
        {
            var request = ParseCreateRequest(requestBody);
            var input = request.input ?? new CheckpointCreateInput();
            var paths = NormalizePaths(input.paths);

            if (paths.Length == 0)
            {
                paths = new[] { "Assets", "ProjectSettings", "Packages/manifest.json", "Packages/packages-lock.json" };
            }

            if (!ValidatePaths(paths, out var error))
            {
                return Refused(input.dryRun, error);
            }

            if (input.dryRun)
            {
                return new CheckpointOperationResult
                {
                    dryRun = true,
                    checkpointId = string.Empty,
                    path = CheckpointRoot,
                    message = $"DRY RUN: would create a durable checkpoint for {paths.Length} path(s).",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return NeedsConfirmation("Creating a durable checkpoint writes a potentially large artifact set.");
            }

            try
            {
                var manifest = CreateInternal(input.label, paths, input.maxFiles, input.maxBytes);
                return new CheckpointOperationResult
                {
                    dryRun = false,
                    created = true,
                    checkpointId = manifest.checkpointId,
                    path = GetRelativeCheckpointPath(manifest.checkpointId),
                    fileCount = manifest.fileCount,
                    totalBytes = manifest.totalBytes,
                    message = $"Created durable checkpoint '{manifest.checkpointId}' with {manifest.fileCount} files.",
                    verificationStatus = Verify(manifest) ? "passed" : "failed",
                    verificationSignals = new[] { "checkpoint_created", "checkpoint_verified" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }
            catch (Exception exception)
            {
                return Refused(false, exception.Message);
            }
        }

        public static CheckpointOperationResult Restore(string requestBody)
        {
            var request = ParseRestoreRequest(requestBody);
            var input = request.input ?? new CheckpointRestoreInput();
            var manifest = Load(input.checkpointId);
            if (manifest == null)
            {
                return Refused(input.dryRun, "Checkpoint was not found.");
            }

            if (input.dryRun)
            {
                return new CheckpointOperationResult
                {
                    dryRun = true,
                    checkpointId = manifest.checkpointId,
                    fileCount = manifest.fileCount,
                    totalBytes = manifest.totalBytes,
                    message = $"DRY RUN: would restore {manifest.fileCount} files from checkpoint '{manifest.checkpointId}'.",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return NeedsConfirmation("Restoring a durable checkpoint overwrites project files.");
            }

            try
            {
                var safetyId = string.Empty;
                if (input.createSafetyCheckpoint)
                {
                    safetyId = CreateInternal("pre-restore-" + manifest.checkpointId, manifest.includedPaths, 20000, 1073741824).checkpointId;
                }

                var projectRoot = GetProjectRoot();
                var dataRoot = Path.Combine(GetCheckpointDirectory(manifest.checkpointId), DataDirectory);
                foreach (var file in manifest.files)
                {
                    var source = ResolveUnder(dataRoot, file.path);
                    var destination = ResolveProjectPath(file.path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? projectRoot);
                    File.Copy(source, destination, true);
                }

                foreach (var missingPath in manifest.missingPaths ?? Array.Empty<string>())
                {
                    var target = ResolveProjectPath(missingPath);
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    else if (Directory.Exists(target))
                    {
                        Directory.Delete(target, true);
                    }
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                var verified = manifest.files.All(file => File.Exists(ResolveProjectPath(file.path)) && ComputeSha256(ResolveProjectPath(file.path)) == file.sha256)
                    && (manifest.missingPaths ?? Array.Empty<string>()).All(path => !File.Exists(ResolveProjectPath(path)) && !Directory.Exists(ResolveProjectPath(path)));
                return new CheckpointOperationResult
                {
                    restored = true,
                    checkpointId = manifest.checkpointId,
                    safetyCheckpointId = safetyId,
                    path = GetRelativeCheckpointPath(manifest.checkpointId),
                    fileCount = manifest.fileCount,
                    totalBytes = manifest.totalBytes,
                    message = verified ? "Checkpoint restored and hashes verified." : "Checkpoint restored but hash verification failed.",
                    verificationStatus = verified ? "passed" : "failed",
                    verificationSignals = verified
                        ? new[] { "checkpoint_restored", "checkpoint_verified" }
                        : new[] { "checkpoint_restored" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }
            catch (Exception exception)
            {
                return Refused(false, exception.Message);
            }
        }

        public static CheckpointOperationResult Delete(string requestBody)
        {
            var request = ParseDeleteRequest(requestBody);
            var input = request.input ?? new CheckpointDeleteInput();
            var manifest = Load(input.checkpointId);
            if (manifest == null)
            {
                return Refused(input.dryRun, "Checkpoint was not found.");
            }

            if (input.dryRun)
            {
                return new CheckpointOperationResult
                {
                    dryRun = true,
                    checkpointId = manifest.checkpointId,
                    message = $"DRY RUN: would delete checkpoint '{manifest.checkpointId}'.",
                    verificationStatus = "passed",
                    verificationSignals = new[] { "structured_observation" },
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            if (!input.confirm)
            {
                return NeedsConfirmation("Deleting a durable checkpoint is irreversible.");
            }

            Directory.Delete(GetCheckpointDirectory(manifest.checkpointId), true);
            return new CheckpointOperationResult
            {
                deleted = true,
                checkpointId = manifest.checkpointId,
                message = $"Deleted checkpoint '{manifest.checkpointId}'.",
                verificationStatus = Directory.Exists(GetCheckpointDirectory(manifest.checkpointId)) ? "failed" : "passed",
                verificationSignals = new[] { "checkpoint_deleted" },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static CheckpointListResult List()
        {
            var root = GetCheckpointRoot();
            var manifests = Directory.Exists(root)
                ? Directory.GetDirectories(root)
                    .Select(directory => Load(Path.GetFileName(directory)))
                    .Where(manifest => manifest != null)
                    .OrderByDescending(manifest => manifest.createdAtUtc)
                    .ToArray()
                : Array.Empty<CheckpointManifest>();

            return new CheckpointListResult
            {
                totalFound = manifests.Length,
                checkpoints = manifests,
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        public static CheckpointManifest CreateInternal(string label, string[] paths, int maxFiles = 20000, long maxBytes = 1073741824)
        {
            paths = NormalizePaths(paths);
            if (!ValidatePaths(paths, out var error))
            {
                throw new InvalidOperationException(error);
            }

            maxFiles = Math.Max(1, Math.Min(maxFiles, 100000));
            maxBytes = Math.Max(1, Math.Min(maxBytes, 10L * 1024 * 1024 * 1024));
            var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var checkpointDirectory = GetCheckpointDirectory(id);
            var dataDirectory = Path.Combine(checkpointDirectory, DataDirectory);
            Directory.CreateDirectory(dataDirectory);
            var entries = new List<CheckpointFileEntry>();
            var missingPaths = paths.Where(path =>
            {
                var absolute = ResolveProjectPath(path);
                return !File.Exists(absolute) && !Directory.Exists(absolute);
            }).ToArray();
            long totalBytes = 0;

            foreach (var path in ExpandFiles(paths))
            {
                if (entries.Count >= maxFiles)
                {
                    throw new InvalidOperationException($"Checkpoint exceeds the maxFiles limit of {maxFiles}.");
                }

                var absolute = ResolveProjectPath(path);
                var length = new FileInfo(absolute).Length;
                totalBytes += length;
                if (totalBytes > maxBytes)
                {
                    throw new InvalidOperationException($"Checkpoint exceeds the maxBytes limit of {maxBytes}.");
                }

                var destination = ResolveUnder(dataDirectory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? dataDirectory);
                File.Copy(absolute, destination, true);
                entries.Add(new CheckpointFileEntry
                {
                    path = path,
                    byteLength = length,
                    sha256 = ComputeSha256(destination)
                });
            }

            var manifest = new CheckpointManifest
            {
                checkpointId = id,
                label = SanitizeLabel(label),
                createdAtUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                includedPaths = paths,
                missingPaths = missingPaths,
                fileCount = entries.Count,
                totalBytes = totalBytes,
                files = entries.ToArray()
            };
            File.WriteAllText(Path.Combine(checkpointDirectory, ManifestFile), JsonUtility.ToJson(manifest, true));

            if (!Verify(manifest))
            {
                throw new InvalidOperationException("Checkpoint file hash verification failed.");
            }

            return manifest;
        }

        public static CheckpointManifest Load(string checkpointId)
        {
            if (!IsSafeCheckpointId(checkpointId))
            {
                return null;
            }

            var path = Path.Combine(GetCheckpointDirectory(checkpointId), ManifestFile);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<CheckpointManifest>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static bool Verify(CheckpointManifest manifest)
        {
            if (manifest == null)
            {
                return false;
            }

            var dataRoot = Path.Combine(GetCheckpointDirectory(manifest.checkpointId), DataDirectory);
            return manifest.files.All(file =>
            {
                var path = ResolveUnder(dataRoot, file.path);
                return File.Exists(path)
                    && new FileInfo(path).Length == file.byteLength
                    && ComputeSha256(path) == file.sha256;
            });
        }

        private static IEnumerable<string> ExpandFiles(string[] paths)
        {
            var projectRoot = GetProjectRoot();
            var output = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var relative in paths)
            {
                var absolute = ResolveProjectPath(relative);
                if (File.Exists(absolute))
                {
                    output.Add(relative);
                    continue;
                }

                if (!Directory.Exists(absolute))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(absolute, "*", SearchOption.AllDirectories))
                {
                    var normalized = GetRelativeProjectPath(file);
                    if (normalized.StartsWith(CheckpointRoot + "/", StringComparison.Ordinal)
                        || normalized.Contains("/Library/")
                        || normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    output.Add(normalized);
                }
            }

            return output;
        }

        private static bool ValidatePaths(string[] paths, out string error)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)
                    || Path.IsPathRooted(path)
                    || path.Contains("..")
                    || path.StartsWith("Library", StringComparison.Ordinal)
                    || path.StartsWith("Temp", StringComparison.Ordinal)
                    || path.StartsWith("Logs", StringComparison.Ordinal)
                    || path.StartsWith(CheckpointRoot, StringComparison.Ordinal))
                {
                    error = $"Unsafe checkpoint path '{path}'.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static string[] NormalizePaths(string[] paths)
        {
            return (paths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim().Replace('\\', '/').TrimEnd('/'))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string ResolveProjectPath(string relativePath)
        {
            return ResolveUnder(GetProjectRoot(), relativePath);
        }

        private static string ResolveUnder(string root, string relativePath)
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(fullPath, fullRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Path escapes its allowed root.");
            }

            return fullPath;
        }

        private static string GetRelativeProjectPath(string absolutePath)
        {
            var root = GetProjectRoot().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(absolutePath).Substring(root.Length).Replace('\\', '/');
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        private static string GetCheckpointRoot()
        {
            return Path.Combine(GetProjectRoot(), CheckpointRoot);
        }

        private static string GetCheckpointDirectory(string checkpointId)
        {
            return Path.Combine(GetCheckpointRoot(), checkpointId);
        }

        private static string GetRelativeCheckpointPath(string checkpointId)
        {
            return CheckpointRoot + "/" + checkpointId;
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool IsSafeCheckpointId(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 64
                && value.All(character => char.IsLetterOrDigit(character) || character == '-');
        }

        private static string SanitizeLabel(string value)
        {
            return new string((value ?? string.Empty)
                .Take(80)
                .Select(character => char.IsControl(character) ? ' ' : character)
                .ToArray()).Trim();
        }

        private static CheckpointOperationResult Refused(bool dryRun, string message)
        {
            return new CheckpointOperationResult
            {
                dryRun = dryRun,
                refused = true,
                message = message,
                verificationStatus = "refused",
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static CheckpointOperationResult NeedsConfirmation(string message)
        {
            return new CheckpointOperationResult
            {
                requiresConfirmation = true,
                message = message,
                verificationStatus = "needs_confirmation",
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static CheckpointCreateRequest ParseCreateRequest(string body)
        {
            try { return JsonUtility.FromJson<CheckpointCreateRequest>(body) ?? new CheckpointCreateRequest(); }
            catch { return new CheckpointCreateRequest(); }
        }

        private static CheckpointRestoreRequest ParseRestoreRequest(string body)
        {
            try { return JsonUtility.FromJson<CheckpointRestoreRequest>(body) ?? new CheckpointRestoreRequest(); }
            catch { return new CheckpointRestoreRequest(); }
        }

        private static CheckpointDeleteRequest ParseDeleteRequest(string body)
        {
            try { return JsonUtility.FromJson<CheckpointDeleteRequest>(body) ?? new CheckpointDeleteRequest(); }
            catch { return new CheckpointDeleteRequest(); }
        }
    }
}
