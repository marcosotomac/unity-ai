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
    public sealed class VisionCaptureInput
    {
        public string source = "scene";
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

        private static readonly ConcurrentQueue<BridgeWorkItem> WorkQueue = new();
        private static HttpListener _listener;
        private static CancellationTokenSource _cancellation;
        private static Task _serverTask;
        private static string _bridgeToken = string.Empty;

        public static bool IsRunning => _listener != null && _listener.IsListening;
        public static string Url => $"http://127.0.0.1:{DefaultPort}/";

        static UnityAiBridgeServer()
        {
            EditorApplication.update -= ProcessQueuedWork;
            EditorApplication.update += ProcessQueuedWork;
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
        }

        public static void Start(string bridgeToken = null)
        {
            if (IsRunning)
            {
                return;
            }

            _bridgeToken = bridgeToken ?? string.Empty;
            _cancellation = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _serverTask = Task.Run(() => ListenLoop(_cancellation.Token));
            Debug.Log($"Unity AI bridge listening on {Url}");
        }

        public static void Stop()
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

            if (context.Request.HttpMethod != "POST")
            {
                await WriteResponse(context, 405, new BridgeResponse
                {
                    ok = false,
                    capability = string.Empty,
                    error = "Only POST requests are supported."
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
                case "unity.console.read":
                    return JsonResult(capability, envelope, ConsoleLogBridge.GetSummary());
                case "unity.console.diagnose":
                    return JsonResult(capability, envelope, ConsoleLogBridge.Diagnose());
                case "unity.console.plan_fix":
                    return JsonResult(capability, envelope, ConsoleLogBridge.PlanFix());
                case "unity.assets.list":
                    return JsonResult(capability, envelope, AssetListObserver.ListAssets(requestBody));
                case "unity.scenes.list":
                    return JsonResult(capability, envelope, SceneListObserver.ListScenes());
                case "unity.scene.inspect":
                    return JsonResult(capability, envelope, SceneInspector.InspectActiveScene(requestBody));
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
                case "unity.vision.capture":
                    return JsonResult(capability, envelope, CaptureVision(requestBody));
                case "unity.meta_xr.validate_setup":
                    return JsonResult(capability, envelope, MetaXrValidator.Validate());
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

        private static ScreenshotCaptureResult CaptureVision(string requestBody)
        {
            var input = ExtractVisionInput(requestBody);
            return input.source == "game"
                ? ScreenshotCapture.CaptureGameView()
                : ScreenshotCapture.CaptureSceneView();
        }

        private static VisionCaptureInput ExtractVisionInput(string requestBody)
        {
            if (requestBody.Contains("\"source\":\"game\""))
            {
                return new VisionCaptureInput { source = "game" };
            }

            return new VisionCaptureInput { source = "scene" };
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
            return capability.StartsWith("unity.editor.", StringComparison.Ordinal);
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
