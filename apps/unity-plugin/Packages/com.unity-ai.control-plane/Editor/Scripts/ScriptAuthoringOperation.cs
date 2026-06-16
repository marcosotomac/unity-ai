using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class ScriptAuthoringInput
    {
        public bool dryRun = true;
        public bool confirm;
        public string path;
        public string source;
        public string expectedSourceSha256;
        public string expectedClassName;
        public string expectedNamespace;
        public bool overwrite;
        public string attachToObjectPath;
        public bool autoRollbackOnCompileError = true;
        public int timeoutSeconds = 300;
    }

    [Serializable]
    public sealed class ScriptAuthoringState
    {
        public string checkpointId;
        public string sourceSha256;
        public string fullTypeName;
        public bool rollbackStarted;
        public string failureMessage;
        public int targetCompilerErrorCount;
    }

    [Serializable]
    public sealed class ScriptAuthoringRequest
    {
        public string requestId;
        public string correlationId;
        public ScriptAuthoringInput input = new();
        public ScriptAuthoringState state = new();
    }

    [Serializable]
    public sealed class ScriptAuthoringStartResult
    {
        public bool accepted;
        public bool dryRun;
        public bool refused;
        public bool requiresConfirmation;
        public string jobId;
        public string path;
        public string sourceSha256;
        public string expectedClassName;
        public string fullTypeName;
        public string message;
        public string[] validationRules = Array.Empty<string>();
        public string[] requiredPermissions = Array.Empty<string>();
        public string[] verificationSignals = Array.Empty<string>();
        public string timestampUtc;
    }

    [Serializable]
    public sealed class ScriptAuthoringJobResult
    {
        public bool written;
        public bool compiled;
        public bool attached;
        public bool rolledBack;
        public string path;
        public string sourceSha256;
        public string className;
        public string namespaceName;
        public string fullTypeName;
        public string assemblyName;
        public string checkpointId;
        public string attachedObjectPath;
        public int targetCompilerErrorCount;
        public string message;
        public string timestampUtc;
    }

    [InitializeOnLoad]
    public static class ScriptAuthoringOperation
    {
        private const string Capability = "unity.scripts.author";
        private const string JobKind = "script_authoring";
        private const int MaxSourceBytes = 262144;
        private static readonly Dictionary<string, int> StableFrames = new();
        private static readonly string[] ForbiddenPatterns =
        {
            @"\busing\s+UnityEditor\b",
            @"\bUnityEditor\.",
            @"\bInitializeOnLoad\b",
            @"\bInitializeOnLoadMethod\b",
            @"\bAssetPostprocessor\b",
            @"\bMenuItem\b",
            @"\bCustomEditor\b",
            @"\bExecuteAlways\b",
            @"\bExecuteInEditMode\b",
            @"\bOnValidate\s*\(",
            @"\bReset\s*\(",
            @"\bOnEnable\s*\(",
            @"\bOnDisable\s*\(",
            @"\bOnDestroy\s*\(",
            @"\bSystem\.Diagnostics\.Process\b",
            @"\bProcess\.Start\s*\(",
            @"\bDllImport\b",
            @"\bLibraryImport\b",
            @"\bunsafe\b",
            @"\bstackalloc\b",
            @"\bSystem\.Reflection\b",
            @"\bAssembly\.Load",
            @"\bActivator\.CreateInstance\b",
            @"\bSystem\.Net\b",
            @"\bHttpClient\b",
            @"\bUnityWebRequest\b",
            @"\bWebRequest\b",
            @"\bSocket\b",
            @"\bSystem\.IO\b",
            @"\bFile\.",
            @"\bDirectory\.",
            @"\bFileStream\b",
            @"\bApplication\.Quit\s*\(",
            @"\bEnvironment\.Exit\s*\(",
            @"^\s*#\s*(r|load)\b",
            @"\bModuleInitializer\b"
        };

        static ScriptAuthoringOperation()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
        }

        public static ScriptAuthoringStartResult Start(string requestBody)
        {
            var request = ParseRequest(requestBody);
            var input = request.input ?? new ScriptAuthoringInput();
            var path = NormalizeAssetPath(input.path);
            var source = NormalizeSource(input.source);
            var sourceSha256 = ComputeSha256(source);
            var timestamp = DateTime.UtcNow.ToString("O");

            if (!ValidateInput(input, path, source, sourceSha256, out var fullTypeName, out var error))
            {
                return new ScriptAuthoringStartResult
                {
                    dryRun = input.dryRun,
                    refused = true,
                    path = path,
                    sourceSha256 = sourceSha256,
                    expectedClassName = input.expectedClassName,
                    fullTypeName = fullTypeName,
                    message = error,
                    validationRules = ValidationRules(),
                    timestampUtc = timestamp
                };
            }

            if (input.dryRun)
            {
                return new ScriptAuthoringStartResult
                {
                    dryRun = true,
                    path = path,
                    sourceSha256 = sourceSha256,
                    expectedClassName = input.expectedClassName,
                    fullTypeName = fullTypeName,
                    message = $"DRY RUN: validated runtime MonoBehaviour source for '{path}'. Confirm this exact SHA-256 to write and compile it.",
                    validationRules = ValidationRules(),
                    requiredPermissions = new[] { "modify_assets", "execute_editor_script" },
                    verificationSignals = new[] { "script_source_validated", "structured_observation" },
                    timestampUtc = timestamp
                };
            }

            if (!input.confirm)
            {
                return new ScriptAuthoringStartResult
                {
                    requiresConfirmation = true,
                    path = path,
                    sourceSha256 = sourceSha256,
                    expectedClassName = input.expectedClassName,
                    fullTypeName = fullTypeName,
                    message = "Script authoring requires confirm=true and expectedSourceSha256 from the validated dry run.",
                    validationRules = ValidationRules(),
                    requiredPermissions = new[] { "modify_assets", "execute_editor_script" },
                    timestampUtc = timestamp
                };
            }

            try
            {
                var checkpointPaths = new List<string> { path, path + ".meta" };
                var scene = EditorSceneManager.GetActiveScene();
                if (!string.IsNullOrWhiteSpace(input.attachToObjectPath) && scene.IsValid() && !string.IsNullOrWhiteSpace(scene.path))
                {
                    checkpointPaths.Add(scene.path);
                    checkpointPaths.Add(scene.path + ".meta");
                }

                var checkpoint = DurableCheckpointStore.CreateInternal("script-authoring", checkpointPaths.Distinct().ToArray());
                var absolutePath = ResolveAssetPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? Application.dataPath);
                File.WriteAllText(absolutePath, source, new UTF8Encoding(false));

                request.input.path = path;
                request.input.source = source;
                request.input.timeoutSeconds = Math.Max(10, Math.Min(input.timeoutSeconds, 1800));
                request.state = new ScriptAuthoringState
                {
                    checkpointId = checkpoint.checkpointId,
                    sourceSha256 = sourceSha256,
                    fullTypeName = fullTypeName
                };

                var envelope = new UnityAiRequestEnvelope
                {
                    requestId = request.requestId,
                    correlationId = request.correlationId
                };
                var job = UnityAiJobStore.Create(
                    Capability,
                    JobKind,
                    envelope,
                    JsonUtility.ToJson(request),
                    $"Writing and compiling runtime component '{fullTypeName}'.");
                UnityAiJobStore.MarkRunning(job.jobId, "importing_script", "Importing generated runtime component.", 0.2f);

                var auditEvent = new UnityAiAuditEvent
                {
                    timestamp = DateTime.UtcNow.ToString("O"),
                    capability = Capability,
                    requestId = request.requestId,
                    correlationId = request.correlationId,
                    message = $"Wrote runtime component source '{path}' with SHA-256 {sourceSha256}; compilation job '{job.jobId}' started.",
                    effects = new[] { "code_change", "asset_change", "write_checkpoint", "write_audit_log" }
                };
                PersistAudit(auditEvent);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                CompilationPipeline.RequestScriptCompilation();

                return new ScriptAuthoringStartResult
                {
                    accepted = true,
                    jobId = job.jobId,
                    path = path,
                    sourceSha256 = sourceSha256,
                    expectedClassName = input.expectedClassName,
                    fullTypeName = fullTypeName,
                    message = "Script source written; persistent compilation and attachment verification started.",
                    requiredPermissions = string.IsNullOrWhiteSpace(input.attachToObjectPath)
                        ? new[] { "modify_assets", "execute_editor_script" }
                        : new[] { "modify_assets", "modify_scenes", "execute_editor_script" },
                    verificationSignals = new[] { "script_source_validated", "checkpoint_created", "operation_audited" },
                    timestampUtc = timestamp
                };
            }
            catch (Exception exception)
            {
                return new ScriptAuthoringStartResult
                {
                    refused = true,
                    path = path,
                    sourceSha256 = sourceSha256,
                    expectedClassName = input.expectedClassName,
                    fullTypeName = fullTypeName,
                    message = exception.GetBaseException().Message,
                    validationRules = ValidationRules(),
                    timestampUtc = timestamp
                };
            }
        }

        private static void Tick()
        {
            foreach (var job in UnityAiJobStore.List("running", JobKind, 100))
            {
                var request = ParseRequest(job.requestJson);
                var input = request.input ?? new ScriptAuthoringInput();
                var state = request.state ?? new ScriptAuthoringState();

                if (job.cancelRequested)
                {
                    RollbackOrFail(job, request, "Script authoring was cancelled.");
                    continue;
                }

                if (IsTimedOut(job, input.timeoutSeconds))
                {
                    RollbackOrFail(job, request, $"Script authoring did not settle within {input.timeoutSeconds} seconds.");
                    continue;
                }

                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    StableFrames[job.jobId] = 0;
                    UnityAiJobStore.UpdateProgress(job.jobId, EditorApplication.isCompiling ? "compiling" : "importing", "Unity is compiling or importing the generated script.", 0.55f);
                    continue;
                }

                StableFrames.TryGetValue(job.jobId, out var stableFrames);
                stableFrames++;
                StableFrames[job.jobId] = stableFrames;
                if (stableFrames < 3)
                {
                    continue;
                }

                if (state.rollbackStarted)
                {
                    var rollbackResult = new ScriptAuthoringJobResult
                    {
                        written = false,
                        compiled = false,
                        rolledBack = true,
                        path = input.path,
                        sourceSha256 = state.sourceSha256,
                        className = input.expectedClassName,
                        namespaceName = input.expectedNamespace,
                        fullTypeName = state.fullTypeName,
                        checkpointId = state.checkpointId,
                        targetCompilerErrorCount = state.targetCompilerErrorCount,
                        message = state.failureMessage,
                        timestampUtc = DateTime.UtcNow.ToString("O")
                    };
                    UnityAiJobStore.Fail(
                        job.jobId,
                        state.failureMessage,
                        rollbackResult,
                        "script_source_validated",
                        "checkpoint_created",
                        "checkpoint_restored",
                        "compilation_completed",
                        "console_snapshot");
                    StableFrames.Remove(job.jobId);
                    continue;
                }

                VerifyCompiledJob(job, request);
                StableFrames.Remove(job.jobId);
            }
        }

        private static void VerifyCompiledJob(UnityAiJobRecord job, ScriptAuthoringRequest request)
        {
            var input = request.input ?? new ScriptAuthoringInput();
            var state = request.state ?? new ScriptAuthoringState();
            var targetErrors = CountTargetCompilerErrors(input.path);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(input.path);
            var scriptClass = script != null ? script.GetClass() : null;
            var validClass = scriptClass != null
                && scriptClass.Name == input.expectedClassName
                && string.Equals(scriptClass.Namespace ?? string.Empty, input.expectedNamespace ?? string.Empty, StringComparison.Ordinal)
                && typeof(MonoBehaviour).IsAssignableFrom(scriptClass)
                && !scriptClass.IsAbstract;

            if (targetErrors > 0 || !validClass)
            {
                var reason = targetErrors > 0
                    ? $"Generated script '{input.path}' produced {targetErrors} compiler error(s)."
                    : $"Generated script '{input.path}' did not resolve to concrete MonoBehaviour '{state.fullTypeName}'.";
                state.targetCompilerErrorCount = targetErrors;
                request.state = state;
                RollbackOrFail(job, request, reason);
                return;
            }

            var attached = false;
            var attachedPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(input.attachToObjectPath))
            {
                var target = FindByPath(input.attachToObjectPath);
                if (target == null)
                {
                    RollbackOrFail(job, request, $"Attachment target '{input.attachToObjectPath}' was not found after compilation.");
                    return;
                }

                var existing = target.GetComponent(scriptClass);
                var component = existing ?? Undo.AddComponent(target, scriptClass);
                attached = component != null && target.GetComponent(scriptClass) != null;
                attachedPath = GetGameObjectPath(target);
                if (!attached)
                {
                    RollbackOrFail(job, request, $"Compiled component '{state.fullTypeName}' could not be attached to '{attachedPath}'.");
                    return;
                }

                EditorUtility.SetDirty(component);
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            }

            var result = new ScriptAuthoringJobResult
            {
                written = File.Exists(ResolveAssetPath(input.path)),
                compiled = true,
                attached = attached,
                path = input.path,
                sourceSha256 = state.sourceSha256,
                className = scriptClass.Name,
                namespaceName = scriptClass.Namespace ?? string.Empty,
                fullTypeName = scriptClass.FullName ?? scriptClass.Name,
                assemblyName = scriptClass.Assembly.GetName().Name,
                checkpointId = state.checkpointId,
                attachedObjectPath = attachedPath,
                targetCompilerErrorCount = targetErrors,
                message = attached
                    ? $"Compiled and attached runtime component '{scriptClass.FullName}'."
                    : $"Compiled runtime component '{scriptClass.FullName}'.",
                timestampUtc = DateTime.UtcNow.ToString("O")
            };
            var signals = new List<string>
            {
                "script_source_validated",
                "checkpoint_created",
                "compilation_completed",
                "console_snapshot",
                "script_compilation_verified",
                "operation_audited"
            };
            if (attached)
            {
                signals.Add("component_state_verified");
                signals.Add("scene_mutation_verified");
            }

            UnityAiJobStore.Complete(job.jobId, result, result.message, signals.ToArray());
        }

        private static void RollbackOrFail(UnityAiJobRecord job, ScriptAuthoringRequest request, string failureMessage)
        {
            var input = request.input ?? new ScriptAuthoringInput();
            var state = request.state ?? new ScriptAuthoringState();
            if (!input.autoRollbackOnCompileError || string.IsNullOrWhiteSpace(state.checkpointId))
            {
                var result = new ScriptAuthoringJobResult
                {
                    written = File.Exists(ResolveAssetPath(input.path)),
                    compiled = false,
                    path = input.path,
                    sourceSha256 = state.sourceSha256,
                    className = input.expectedClassName,
                    namespaceName = input.expectedNamespace,
                    fullTypeName = state.fullTypeName,
                    checkpointId = state.checkpointId,
                    targetCompilerErrorCount = CountTargetCompilerErrors(input.path),
                    message = failureMessage,
                    timestampUtc = DateTime.UtcNow.ToString("O")
                };
                UnityAiJobStore.Fail(job.jobId, failureMessage, result, "script_source_validated", "checkpoint_created", "compilation_completed", "console_snapshot");
                StableFrames.Remove(job.jobId);
                return;
            }

            state.rollbackStarted = true;
            state.failureMessage = failureMessage;
            state.targetCompilerErrorCount = Math.Max(state.targetCompilerErrorCount, CountTargetCompilerErrors(input.path));
            request.state = state;
            job.requestJson = JsonUtility.ToJson(request);
            job.stage = "verifying_rollback";
            job.message = "Compilation failed; restoring the pre-write checkpoint.";
            job.progress = 0.9f;
            UnityAiJobStore.Save(job);

            var restoreRequest = new CheckpointRestoreRequest
            {
                input = new CheckpointRestoreInput
                {
                    dryRun = false,
                    confirm = true,
                    checkpointId = state.checkpointId,
                    createSafetyCheckpoint = false
                }
            };
            var restore = DurableCheckpointStore.Restore(JsonUtility.ToJson(restoreRequest));
            if (!restore.restored)
            {
                UnityAiJobStore.Fail(
                    job.jobId,
                    failureMessage + " Automatic checkpoint restoration also failed: " + restore.message,
                    restore,
                    "script_source_validated",
                    "checkpoint_created",
                    "compilation_completed",
                    "console_snapshot");
            }
        }

        private static bool ValidateInput(
            ScriptAuthoringInput input,
            string path,
            string source,
            string sourceSha256,
            out string fullTypeName,
            out string error)
        {
            var className = (input.expectedClassName ?? string.Empty).Trim();
            var namespaceName = (input.expectedNamespace ?? string.Empty).Trim();
            fullTypeName = string.IsNullOrEmpty(namespaceName) ? className : namespaceName + "." + className;

            if (!IsSafeScriptPath(path))
            {
                error = "path must be a .cs file under Assets and cannot contain parent traversal.";
                return false;
            }

            if (!IsIdentifier(className) || Path.GetFileNameWithoutExtension(path) != className)
            {
                error = "expectedClassName must be a valid C# identifier and match the script filename.";
                return false;
            }

            if (!string.IsNullOrEmpty(namespaceName) && !namespaceName.Split('.').All(IsIdentifier))
            {
                error = "expectedNamespace must contain only valid dot-separated C# identifiers.";
                return false;
            }

            var sourceBytes = Encoding.UTF8.GetByteCount(source);
            if (sourceBytes == 0 || sourceBytes > MaxSourceBytes)
            {
                error = $"source must contain between 1 and {MaxSourceBytes} UTF-8 bytes.";
                return false;
            }

            if (!Regex.IsMatch(source, $@"\bclass\s+{Regex.Escape(className)}\b[^{{:]*:\s*(?:[\w.]+\s*,\s*)*(?:UnityEngine\.)?MonoBehaviour\b"))
            {
                error = $"source must declare class '{className}' deriving from MonoBehaviour.";
                return false;
            }

            if (!string.IsNullOrEmpty(namespaceName)
                && !Regex.IsMatch(source, $@"\bnamespace\s+{Regex.Escape(namespaceName)}\b"))
            {
                error = $"source does not declare expectedNamespace '{namespaceName}'.";
                return false;
            }

            if (Regex.IsMatch(source, $@"\bstatic\s+{Regex.Escape(className)}\s*\("))
            {
                error = "Static constructors are not allowed in generated runtime components.";
                return false;
            }

            foreach (var pattern in ForbiddenPatterns)
            {
                if (Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    error = $"source contains a blocked API or lifecycle pattern: {pattern}";
                    return false;
                }
            }

            var absolutePath = ResolveAssetPath(path);
            if (!input.overwrite && File.Exists(absolutePath))
            {
                error = "Target script already exists and overwrite=false.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(input.attachToObjectPath)
                && string.IsNullOrEmpty(NormalizeHierarchyPath(input.attachToObjectPath)))
            {
                error = "attachToObjectPath is invalid.";
                return false;
            }

            if (!input.dryRun)
            {
                var expectedHash = (input.expectedSourceSha256 ?? string.Empty).Trim().ToLowerInvariant();
                if (expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
                {
                    error = "Real script authoring requires expectedSourceSha256 from the dry-run validation.";
                    return false;
                }

                if (!string.Equals(expectedHash, sourceSha256, StringComparison.Ordinal))
                {
                    error = "expectedSourceSha256 does not match the exact normalized source being written.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static int CountTargetCompilerErrors(string path)
        {
            var normalizedPath = NormalizeAssetPath(path);
            return ConsoleLogBridge.Diagnose().diagnostics.Count(diagnostic =>
                diagnostic.severity == "error"
                && diagnostic.category == "compiler_error"
                && IsSameAssetPath(diagnostic.file, normalizedPath));
        }

        private static bool IsSameAssetPath(string diagnosticPath, string assetPath)
        {
            var normalizedDiagnostic = NormalizeAssetPath(diagnosticPath);
            if (string.Equals(normalizedDiagnostic, assetPath, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(normalizedDiagnostic),
                    ResolveAssetPath(assetPath),
                    Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTimedOut(UnityAiJobRecord job, int timeoutSeconds)
        {
            return DateTime.TryParse(job.createdAtUtc, out var created)
                && DateTime.UtcNow > created.ToUniversalTime().AddSeconds(Math.Max(10, Math.Min(timeoutSeconds, 1800)));
        }

        private static void OnCompilationStarted(object context)
        {
            foreach (var job in UnityAiJobStore.List("running", JobKind, 100))
            {
                StableFrames[job.jobId] = 0;
                UnityAiJobStore.UpdateProgress(job.jobId, "compiling", "Unity compilation started for the generated runtime component.", 0.5f);
            }
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
                Debug.LogError($"Failed to persist script authoring audit event: {exception.Message}");
                return false;
            }
        }

        private static string[] ValidationRules()
        {
            return new[]
            {
                "Runtime MonoBehaviour only; filename must match class name.",
                "Exact source hash confirmation is required before writing.",
                "Editor APIs, edit-time callbacks, process, network, file, reflection, native interop, and unsafe code are blocked.",
                "A durable checkpoint is created before writing.",
                "Compilation and optional component attachment are verified by a persistent job.",
                "Compilation or attachment failure restores the checkpoint by default."
            };
        }

        private static bool IsSafeScriptPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && path.StartsWith("Assets/", StringComparison.Ordinal)
                && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("..")
                && !Path.IsPathRooted(path)
                && path.Length <= 512;
        }

        private static bool IsIdentifier(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && Regex.IsMatch(value, @"^[_\p{L}][_\p{L}\p{Nd}]*$");
        }

        private static string NormalizeSource(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string NormalizeAssetPath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static string NormalizeHierarchyPath(string value)
        {
            var path = (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            return path.Length > 0 && path.Length <= 512 && !path.Contains("..") && !path.Contains("//")
                ? path
                : string.Empty;
        }

        private static string ResolveAssetPath(string path)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, NormalizeAssetPath(path).Replace('/', Path.DirectorySeparatorChar)));
            var assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!absolute.StartsWith(assetsRoot, comparison))
            {
                throw new InvalidOperationException("Script path escapes Assets.");
            }

            return absolute;
        }

        private static string ComputeSha256(string value)
        {
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(item => item.ToString("x2")));
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
            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static ScriptAuthoringRequest ParseRequest(string body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body)
                    ? new ScriptAuthoringRequest()
                    : JsonUtility.FromJson<ScriptAuthoringRequest>(body) ?? new ScriptAuthoringRequest();
            }
            catch
            {
                return new ScriptAuthoringRequest();
            }
        }
    }
}
