using System;
using System.Collections.Concurrent;
using System.IO;
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
        public TaskCompletionSource<BridgeResponse> completion;
    }

    [InitializeOnLoad]
    public static class UnityAiBridgeServer
    {
        public const int DefaultPort = 39071;
        private const string SessionEnabledKey = "UnityAI.ControlPlane.BridgeEnabled";
        private const string SessionTokenKey = "UnityAI.ControlPlane.BridgeToken";

        private static readonly ConcurrentQueue<BridgeWorkItem> WorkQueue = new();
        private static HttpListener _listener;
        private static CancellationTokenSource _cancellation;
        private static Task _serverTask;
        private static string _bridgeToken = string.Empty;
        private static int _restoreAttempts;

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
            if (persistSession)
            {
                SessionState.SetBool(SessionEnabledKey, true);
                SessionState.SetString(SessionTokenKey, _bridgeToken);
            }

            _restoreAttempts = 0;
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
            if (clearSession)
            {
                SessionState.EraseBool(SessionEnabledKey);
                SessionState.EraseString(SessionTokenKey);
            }
        }

        private static void RestoreAfterDomainReload()
        {
            if (IsRunning || !SessionState.GetBool(SessionEnabledKey, false))
            {
                return;
            }

            try
            {
                StartInternal(SessionState.GetString(SessionTokenKey, string.Empty), false);
            }
            catch (HttpListenerException exception)
            {
                _restoreAttempts++;
                if (_restoreAttempts < 20)
                {
                    EditorApplication.delayCall += RestoreAfterDomainReload;
                    return;
                }

                Debug.LogError($"Unity AI bridge could not resume after domain reload: {exception.Message}");
                StopInternal(true);
            }
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
            var completion = new TaskCompletionSource<BridgeResponse>();

            WorkQueue.Enqueue(new BridgeWorkItem
            {
                capability = capability,
                requestBody = body,
                completion = completion
            });

            var response = await completion.Task;
            await WriteResponse(context, response.ok ? 200 : 500, response);
        }

        private static void ProcessQueuedWork()
        {
            while (WorkQueue.TryDequeue(out var item))
            {
                try
                {
                    item.completion.SetResult(ExecuteCapability(item.capability, item.requestBody));
                }
                catch (Exception exception)
                {
                    item.completion.SetResult(new BridgeResponse
                    {
                        ok = false,
                        capability = item.capability,
                        error = exception.Message
                    });
                }
            }
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
                case "unity.scene.upsert_game_object":
                    return JsonResult(capability, envelope, SceneUpsertGameObjectOperation.Execute(requestBody));
                case "unity.scene.batch":
                    return JsonResult(capability, envelope, SceneBatchOperation.Execute(requestBody));
                case "unity.prefabs.list":
                    return JsonResult(capability, envelope, PrefabObserver.ListPrefabs(requestBody));
                case "unity.prefab.inspect":
                    return JsonResult(capability, envelope, PrefabObserver.InspectPrefab(requestBody));
                case "unity.asset.dependencies":
                    return JsonResult(capability, envelope, AssetDependencyObserver.InspectDependencies(requestBody));
                case "unity.scripts.list":
                    return JsonResult(capability, envelope, ScriptAndAssemblyObserver.ListScripts(requestBody));
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
                case "unity.project.settings.update":
                case "unity.packages.change":
                case "unity.jobs.cancel":
                case "unity.tests.run":
                case "unity.playmode.control":
                case "unity.compilation.wait":
                case "unity.build.android":
                case "unity.assets.author":
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
