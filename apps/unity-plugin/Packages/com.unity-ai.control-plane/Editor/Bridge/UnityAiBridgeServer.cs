using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class BridgeResponse
    {
        public bool ok;
        public string capability;
        public string requestId;
        public string correlationId;
        public string resultJson;
        public string error;
    }

    [Serializable]
    public sealed class BridgeRequestEnvelope
    {
        public string requestId;
        public string correlationId;
    }

    [Serializable]
    internal sealed class BridgeWorkItem
    {
        public string capability;
        public string requestBody;
        public string cacheKey;
        public bool persistResponse;
        public TaskCompletionSource<BridgeResponse> completion;
    }

    [Serializable]
    internal sealed class BridgeCachedResponse
    {
        public string cachedAtUtc;
        public BridgeResponse response;
    }

    [InitializeOnLoad]
    public static class UnityAiBridgeServer
    {
        public const int DefaultPort = 39071;
        private const string SessionEnabledKey = "UnityAI.ControlPlane.BridgeEnabled";
        private const string SessionTokenKey = "UnityAI.ControlPlane.BridgeToken";
        private const int MaximumMemoryCacheEntries = 1024;
        private const int MaximumPersistentCacheEntries = 2048;

        private static readonly ConcurrentQueue<BridgeWorkItem> WorkQueue = new();
        private static readonly ConcurrentDictionary<string, Task<BridgeResponse>> InFlightRequests = new();
        private static readonly ConcurrentDictionary<string, BridgeResponse> CompletedResponses = new();
        private static HttpListener _listener;
        private static CancellationTokenSource _cancellation;
        private static Task _serverTask;
        private static string _bridgeToken = string.Empty;
        private static int _restoreAttempts;
        private static double _nextRestoreAttemptAt;
        private static double _restoreDeadlineAt;

        public static bool IsRunning => _listener != null && _listener.IsListening;
        public static string Url => $"http://127.0.0.1:{DefaultPort}/";

        static UnityAiBridgeServer()
        {
            EditorApplication.update -= ProcessQueuedWork;
            EditorApplication.update += ProcessQueuedWork;
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.delayCall += RestoreAfterDomainReload;
        }

        public static void Start(string bridgeToken = null)
        {
            StartInternal(bridgeToken, true);
        }

        private static void StartInternal(string bridgeToken, bool persistSession)
        {
            if (IsRunning)
            {
                return;
            }

            _bridgeToken = bridgeToken ?? string.Empty;
            _cancellation = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url);
            try
            {
                _listener.Start();
            }
            catch
            {
                _listener.Close();
                _listener = null;
                _cancellation.Dispose();
                _cancellation = null;
                throw;
            }

            _serverTask = Task.Run(() => ListenLoop(_cancellation.Token));
            CleanupPersistentResponseCache();
            if (persistSession)
            {
                SessionState.SetBool(SessionEnabledKey, true);
                SessionState.SetString(SessionTokenKey, _bridgeToken);
            }

            _restoreAttempts = 0;
            _nextRestoreAttemptAt = 0;
            _restoreDeadlineAt = 0;
            EditorApplication.update -= RetryRestoreWhenReady;
            Debug.Log($"Unity AI bridge listening on {Url}");
        }

        public static void Stop()
        {
            StopInternal(true);
        }

        private static void StopInternal(bool clearSession)
        {
            _cancellation?.Cancel();

            if (_listener != null)
            {
                if (_listener.IsListening)
                {
                    _listener.Stop();
                }

                _listener.Close();
                _listener = null;
            }

            _serverTask = null;
            _cancellation = null;
            _bridgeToken = string.Empty;
            EditorApplication.update -= RetryRestoreWhenReady;
            if (clearSession)
            {
                SessionState.EraseBool(SessionEnabledKey);
                SessionState.EraseString(SessionTokenKey);
            }
        }

        private static void RestoreAfterDomainReload()
        {
            if (IsRunning)
            {
                EditorApplication.update -= RetryRestoreWhenReady;
                return;
            }

            var sessionEnabled = SessionState.GetBool(SessionEnabledKey, false);
            var token = sessionEnabled
                ? SessionState.GetString(SessionTokenKey, string.Empty)
                : ReadDefaultDesktopToken();
            if (!sessionEnabled && string.IsNullOrWhiteSpace(token))
            {
                EditorApplication.update -= RetryRestoreWhenReady;
                return;
            }

            if (_restoreDeadlineAt <= 0)
            {
                _restoreDeadlineAt = EditorApplication.timeSinceStartup + 90;
            }

            try
            {
                StartInternal(token, !sessionEnabled);
            }
            catch (Exception exception)
            {
                _restoreAttempts++;
                if (EditorApplication.timeSinceStartup < _restoreDeadlineAt)
                {
                    var backoffSeconds = Math.Min(3, 0.1 * Math.Pow(1.5, Math.Min(_restoreAttempts, 12)));
                    _nextRestoreAttemptAt = EditorApplication.timeSinceStartup + backoffSeconds;
                    EditorApplication.update -= RetryRestoreWhenReady;
                    EditorApplication.update += RetryRestoreWhenReady;
                    return;
                }

                Debug.LogError($"Unity AI bridge could not resume within 90 seconds after domain reload: {exception.Message}");
                StopInternal(true);
            }
        }

        private static string ReadDefaultDesktopToken()
        {
            if (Application.isBatchMode)
            {
                return string.Empty;
            }

            try
            {
                var tokenPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config",
                    "unity-ai",
                    "bridge-token");
                return File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : string.Empty;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Unity AI bridge could not read the desktop token: {exception.Message}");
                return string.Empty;
            }
        }

        private static void RetryRestoreWhenReady()
        {
            if (EditorApplication.timeSinceStartup < _nextRestoreAttemptAt)
            {
                return;
            }

            EditorApplication.update -= RetryRestoreWhenReady;
            RestoreAfterDomainReload();
        }

        private static async Task ListenLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                HttpListenerContext context;

                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }

                _ = Task.Run(() => HandleContext(context), cancellationToken);
            }
        }

        private static async Task HandleContext(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/health")
            {
                await WriteResponse(context, 200, new BridgeResponse
                {
                    ok = true,
                    capability = "health",
                    resultJson = "{\"status\":\"ok\"}"
                });
                return;
            }

            var capability = ExtractCapability(context.Request.Url?.AbsolutePath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(capability))
            {
                await WriteResponse(context, 404, new BridgeResponse
                {
                    ok = false,
                    capability = string.Empty,
                    error = "Expected /capabilities/{capabilityName}."
                });
                return;
            }

            if (IsMutatingCapability(capability) && !IsAuthorized(context.Request))
            {
                await WriteResponse(context, 403, new BridgeResponse
                {
                    ok = false,
                    capability = capability,
                    error = string.IsNullOrWhiteSpace(_bridgeToken)
                        ? "Mutating capabilities require a bridge token. Start the bridge with a token."
                        : "Invalid or missing bridge token for mutating capability."
                });
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                await WriteResponse(context, 405, new BridgeResponse
                {
                    ok = false,
                    capability = capability,
                    error = "Only POST requests are supported."
                });
                return;
            }

            var body = await ReadRequestBody(context.Request);
            var envelope = ParseEnvelope(body);
            var cacheKey = CreateCacheKey(capability, envelope.requestId);
            if (TryGetCompletedResponse(cacheKey, envelope.requestId, capability, out var completed))
            {
                await WriteResponse(context, completed.ok ? 200 : 500, completed);
                return;
            }

            var response = await EnqueueOrJoin(capability, body, cacheKey, IsMutatingCapability(capability));
            await WriteResponse(context, response.ok ? 200 : 500, response);
        }

        private static Task<BridgeResponse> EnqueueOrJoin(string capability, string requestBody, string cacheKey, bool persistResponse)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                return Enqueue(capability, requestBody, string.Empty, false);
            }

            var completion = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (InFlightRequests.TryGetValue(cacheKey, out var existing))
            {
                return existing;
            }

            if (!InFlightRequests.TryAdd(cacheKey, completion.Task))
            {
                return InFlightRequests.TryGetValue(cacheKey, out existing)
                    ? existing
                    : EnqueueOrJoin(capability, requestBody, cacheKey, persistResponse);
            }

            WorkQueue.Enqueue(new BridgeWorkItem
            {
                capability = capability,
                requestBody = requestBody,
                cacheKey = cacheKey,
                persistResponse = persistResponse,
                completion = completion
            });
            return completion.Task;
        }

        private static Task<BridgeResponse> Enqueue(string capability, string requestBody, string cacheKey, bool persistResponse)
        {
            var completion = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkQueue.Enqueue(new BridgeWorkItem
            {
                capability = capability,
                requestBody = requestBody,
                cacheKey = cacheKey,
                persistResponse = persistResponse,
                completion = completion
            });
            return completion.Task;
        }

        private static void ProcessQueuedWork()
        {
            while (WorkQueue.TryDequeue(out var item))
            {
                BridgeResponse response;
                try
                {
                    response = ExecuteCapability(item.capability, item.requestBody);
                }
                catch (Exception exception)
                {
                    var envelope = ParseEnvelope(item.requestBody);
                    response = new BridgeResponse
                    {
                        ok = false,
                        capability = item.capability,
                        requestId = envelope.requestId,
                        correlationId = envelope.correlationId,
                        error = exception.Message
                    };
                }

                if (!string.IsNullOrEmpty(item.cacheKey))
                {
                    CompletedResponses[item.cacheKey] = response;
                    TrimMemoryCache();
                    if (item.persistResponse)
                    {
                        PersistCompletedResponse(response);
                    }

                    InFlightRequests.TryRemove(item.cacheKey, out _);
                }

                item.completion.TrySetResult(response);
            }
        }

        private static bool TryGetCompletedResponse(string cacheKey, string requestId, string capability, out BridgeResponse response)
        {
            response = null;
            if (string.IsNullOrEmpty(cacheKey))
            {
                return false;
            }

            if (CompletedResponses.TryGetValue(cacheKey, out response))
            {
                return true;
            }

            if (!IsMutatingCapability(capability) || !TryLoadPersistentResponse(requestId, capability, out response))
            {
                response = null;
                return false;
            }

            CompletedResponses[cacheKey] = response;
            TrimMemoryCache();
            return true;
        }

        private static string CreateCacheKey(string capability, string requestId)
        {
            return IsSafeRequestId(requestId) ? capability + "|" + requestId : string.Empty;
        }

        private static bool IsSafeRequestId(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            {
                return false;
            }

            return requestId.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_');
        }

        private static void PersistCompletedResponse(BridgeResponse response)
        {
            if (response == null || !IsSafeRequestId(response.requestId))
            {
                return;
            }

            try
            {
                var directory = GetPersistentResponseCacheDirectory();
                Directory.CreateDirectory(directory);
                var record = new BridgeCachedResponse
                {
                    cachedAtUtc = DateTime.UtcNow.ToString("O"),
                    response = response
                };
                var path = Path.Combine(directory, response.requestId + ".json");
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, JsonUtility.ToJson(record, true), Encoding.UTF8);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temporaryPath, path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Unity AI bridge could not persist idempotency response: {exception.Message}");
            }
        }

        private static bool TryLoadPersistentResponse(string requestId, string capability, out BridgeResponse response)
        {
            response = null;
            if (!IsSafeRequestId(requestId))
            {
                return false;
            }

            try
            {
                var path = Path.Combine(GetPersistentResponseCacheDirectory(), requestId + ".json");
                if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
                {
                    return false;
                }

                var record = JsonUtility.FromJson<BridgeCachedResponse>(File.ReadAllText(path, Encoding.UTF8));
                if (record?.response == null
                    || !string.Equals(record.response.requestId, requestId, StringComparison.Ordinal)
                    || !string.Equals(record.response.capability, capability, StringComparison.Ordinal))
                {
                    return false;
                }

                response = record.response;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Unity AI bridge could not read idempotency response: {exception.Message}");
                return false;
            }
        }

        private static void TrimMemoryCache()
        {
            if (CompletedResponses.Count <= MaximumMemoryCacheEntries)
            {
                return;
            }

            foreach (var key in CompletedResponses.Keys.Take(CompletedResponses.Count - MaximumMemoryCacheEntries))
            {
                CompletedResponses.TryRemove(key, out _);
            }
        }

        private static void CleanupPersistentResponseCache()
        {
            try
            {
                var directory = GetPersistentResponseCacheDirectory();
                if (!Directory.Exists(directory))
                {
                    return;
                }

                var files = new DirectoryInfo(directory)
                    .GetFiles("*.json")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();
                for (var index = 0; index < files.Length; index += 1)
                {
                    if (index >= MaximumPersistentCacheEntries || DateTime.UtcNow - files[index].LastWriteTimeUtc > TimeSpan.FromDays(1))
                    {
                        files[index].Delete();
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Unity AI bridge could not clean idempotency responses: {exception.Message}");
            }
        }

        private static string GetPersistentResponseCacheDirectory()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectRoot, "Library", "UnityAIControlPlane", "BridgeResponses");
        }

        private static BridgeResponse ExecuteCapability(string capability, string requestBody)
        {
            var envelope = ParseEnvelope(requestBody);

            switch (capability)
            {
                case "unity.project.inspect":
                    return JsonResult(capability, envelope, ProjectInspector.InspectActiveProject());
                case "unity.project.snapshot":
                    return JsonResult(capability, envelope, ProjectSnapshotObserver.Capture());
                case "unity.audit.report":
                    return JsonResult(capability, envelope, AuditReportGenerator.Generate(requestBody));
                case "unity.console.read":
                    return JsonResult(capability, envelope, ConsoleLogBridge.GetSummary());
                case "unity.console.diagnose":
                    return JsonResult(capability, envelope, ConsoleLogBridge.Diagnose());
                case "unity.console.plan_fix":
                    return JsonResult(capability, envelope, ConsoleLogBridge.PlanFix());
                case "unity.console.apply_fix":
                    return JsonResult(capability, envelope, ConsoleLogBridge.ApplyFix(requestBody));
                case "unity.assets.list":
                    return JsonResult(capability, envelope, AssetListObserver.ListAssets(requestBody));
                case "unity.scenes.list":
                    return JsonResult(capability, envelope, SceneListObserver.ListScenes());
                case "unity.scene.inspect":
                    return JsonResult(capability, envelope, SceneInspector.InspectActiveScene(requestBody));
                case "unity.scene.inspect_game_object":
                    return JsonResult(capability, envelope, GameObjectInspector.Inspect(requestBody));
                case "unity.physics.inspect":
                    return JsonResult(capability, envelope, PhysicsInspector.Inspect(requestBody));
                case "unity.scene.upsert_game_object":
                    return JsonResult(capability, envelope, SceneUpsertGameObjectOperation.Execute(requestBody));
                case "unity.scene.batch":
                    return JsonResult(capability, envelope, SceneBatchOperation.Execute(requestBody));
                case "unity.gameplay.compose":
                    return JsonResult(capability, envelope, GameplayComposeOperation.Execute(requestBody));
                case "unity.prefabs.list":
                    return JsonResult(capability, envelope, PrefabObserver.ListPrefabs(requestBody));
                case "unity.prefab.inspect":
                    return JsonResult(capability, envelope, PrefabObserver.InspectPrefab(requestBody));
                case "unity.asset.dependencies":
                    return JsonResult(capability, envelope, AssetDependencyObserver.InspectDependencies(requestBody));
                case "unity.scripts.list":
                    return JsonResult(capability, envelope, ScriptAndAssemblyObserver.ListScripts(requestBody));
                case "unity.scripts.author":
                    return JsonResult(capability, envelope, ScriptAuthoringOperation.Start(requestBody));
                case "unity.assemblies.list":
                    return JsonResult(capability, envelope, ScriptAndAssemblyObserver.ListAssemblies(requestBody));
                case "unity.packages.list":
                    return JsonResult(capability, envelope, PackageListObserver.ListPackages());
                case "unity.project.settings.inspect":
                    return JsonResult(capability, envelope, ProjectSettingsInspector.Inspect());
                case "unity.project.settings.update":
                    return JsonResult(capability, envelope, ProjectSettingsUpdateOperation.Execute(requestBody));
                case "unity.packages.change":
                    return JsonResult(capability, envelope, PackageOperations.Start(requestBody));
                case "unity.jobs.get":
                    return JsonResult(capability, envelope, UnityAiJobStore.GetFromRequest(requestBody));
                case "unity.jobs.list":
                    return JsonResult(capability, envelope, UnityAiJobStore.ListFromRequest(requestBody));
                case "unity.jobs.cancel":
                    return JsonResult(capability, envelope, UnityAiJobStore.CancelFromRequest(requestBody));
                case "unity.tests.run":
                    return JsonResult(capability, envelope, TestOperation.Start(requestBody));
                case "unity.playmode.status":
                    return JsonResult(capability, envelope, PlayModeController.GetStatus());
                case "unity.playmode.control":
                    return JsonResult(capability, envelope, PlayModeController.Start(requestBody));
                case "unity.compilation.status":
                    return JsonResult(capability, envelope, CompilationController.GetStatus());
                case "unity.compilation.wait":
                    return JsonResult(capability, envelope, CompilationController.Start(requestBody));
                case "unity.build.validate_android_quest":
                    return JsonResult(capability, envelope, BuildOperations.ValidateAndroidQuest());
                case "unity.build.android":
                    return JsonResult(capability, envelope, BuildOperations.StartAndroidBuild(requestBody));
                case "unity.assets.author":
                    return JsonResult(capability, envelope, AssetAuthoringOperation.Execute(requestBody));
                case "unity.assets.import":
                    return JsonResult(capability, envelope, AssetImportOperation.Execute(requestBody));
                case "unity.assets.import_from_catalog":
                    return JsonResult(capability, envelope, AssetImportOperation.Execute(requestBody, capability));
                case "unity.prefab.manage":
                    return JsonResult(capability, envelope, PrefabAssetOperation.Execute(requestBody));
                case "unity.checkpoints.create":
                    return JsonResult(capability, envelope, DurableCheckpointStore.Create(requestBody));
                case "unity.checkpoints.list":
                    return JsonResult(capability, envelope, DurableCheckpointStore.List());
                case "unity.checkpoints.restore":
                    return JsonResult(capability, envelope, DurableCheckpointStore.Restore(requestBody));
                case "unity.checkpoints.delete":
                    return JsonResult(capability, envelope, DurableCheckpointStore.Delete(requestBody));
                case "unity.vision.capture":
                    return JsonResult(capability, envelope, ScreenshotCapture.Capture(requestBody));
                case "unity.vision.compare":
                    return JsonResult(capability, envelope, VisualComparison.Compare(requestBody));
                case "unity.meta_xr.validate_setup":
                    return JsonResult(capability, envelope, MetaXrValidator.Validate());
                case "unity.meta_xr.configure":
                    return JsonResult(capability, envelope, MetaXrConfigurationController.Start(requestBody));
                case "unity.editor.create_empty_game_object":
                    return JsonResult(capability, envelope, CreateEmptyGameObjectOperation.Execute(requestBody));
                case "unity.editor.undo_last_operation":
                    return JsonResult(capability, envelope, UndoLastOperation.Execute(requestBody));
                default:
                    return new BridgeResponse
                    {
                        ok = false,
                        capability = capability,
                        error = $"Unsupported capability: {capability}"
                    };
            }
        }

        private static BridgeRequestEnvelope ParseEnvelope(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new BridgeRequestEnvelope();
            }

            try
            {
                return JsonUtility.FromJson<BridgeRequestEnvelope>(requestBody) ?? new BridgeRequestEnvelope();
            }
            catch
            {
                return new BridgeRequestEnvelope();
            }
        }

        private static BridgeResponse JsonResult(string capability, BridgeRequestEnvelope envelope, object result)
        {
            return new BridgeResponse
            {
                ok = true,
                capability = capability,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                resultJson = JsonUtility.ToJson(result, true)
            };
        }

        private static string ExtractCapability(string path)
        {
            const string prefix = "/capabilities/";

            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return Uri.UnescapeDataString(path.Substring(prefix.Length));
        }

        private static bool IsMutatingCapability(string capability)
        {
            switch (capability)
            {
                case "unity.console.apply_fix":
                case "unity.scene.upsert_game_object":
                case "unity.scene.batch":
                case "unity.gameplay.compose":
                case "unity.project.settings.update":
                case "unity.packages.change":
                case "unity.jobs.cancel":
                case "unity.tests.run":
                case "unity.playmode.control":
                case "unity.compilation.wait":
                case "unity.build.android":
                case "unity.assets.author":
                case "unity.assets.import":
                case "unity.assets.import_from_catalog":
                case "unity.scripts.author":
                case "unity.prefab.manage":
                case "unity.checkpoints.create":
                case "unity.checkpoints.restore":
                case "unity.checkpoints.delete":
                case "unity.meta_xr.configure":
                case "unity.editor.create_empty_game_object":
                case "unity.editor.undo_last_operation":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsAuthorized(HttpListenerRequest request)
        {
            if (string.IsNullOrWhiteSpace(_bridgeToken))
            {
                return false;
            }

            var provided = request.Headers["x-unity-ai-bridge-token"];
            return string.Equals(provided, _bridgeToken, StringComparison.Ordinal);
        }

        private static async Task<string> ReadRequestBody(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        private static async Task WriteResponse(HttpListenerContext context, int statusCode, BridgeResponse response)
        {
            var json = JsonUtility.ToJson(response, true);
            var bytes = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;

            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            context.Response.Close();
        }
    }
}
