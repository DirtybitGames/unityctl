#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityCtl.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityCtl.Runtime
{
    /// <summary>
    /// Lightweight TCP server for player builds that enables remote script evaluation
    /// and inspection via ADB port forwarding or direct network connection.
    ///
    /// Add this component to a GameObject in your scene to enable device eval.
    /// Only active in Development builds by default.
    ///
    /// Protocol: Length-prefixed JSON messages over TCP.
    /// Frame format: [4 bytes little-endian length][JSON payload]
    /// </summary>
    [AddComponentMenu("UnityCtl/Dev Server")]
    public class UnityCtlDevServer : MonoBehaviour
    {
        [Tooltip("TCP port to listen on. Use ADB forward to connect: adb forward tcp:7400 tcp:7400")]
        [SerializeField] private int port = 7400;

        [Tooltip("Only start in Development builds. Disable to allow in Release (not recommended).")]
        [SerializeField] private bool developmentBuildsOnly = true;

        [Tooltip("Additional namespaces for expression evaluation (e.g., 'MyGame.Core')")]
        [SerializeField] private string[] additionalNamespaces = Array.Empty<string>();

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();
        private bool _isRunning;

        /// <summary>Whether the server is currently listening for connections.</summary>
        public bool IsRunning => _isRunning;

        /// <summary>The port the server is listening on.</summary>
        public int Port => port;

        void Awake()
        {
            if (developmentBuildsOnly && !Debug.isDebugBuild)
            {
                Debug.Log("[UnityCtl] Dev server disabled (not a development build)");
                Destroy(this);
                return;
            }

            DontDestroyOnLoad(gameObject);
            StartServer();
        }

        void OnDestroy()
        {
            StopServer();
        }

        void Update()
        {
            // Process actions that need to run on the main thread
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError($"[UnityCtl] Main thread action error: {ex.Message}"); }
            }
        }

        private void StartServer()
        {
            if (_isRunning) return;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _isRunning = true;

                Debug.Log($"[UnityCtl] Dev server listening on port {port}");
                Debug.Log($"[UnityCtl] Connect via: adb forward tcp:{port} tcp:{port}");

                _ = AcceptClientsAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCtl] Failed to start dev server on port {port}: {ex.Message}");
                _isRunning = false;
            }
        }

        private void StopServer()
        {
            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();
            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync();
                    Debug.Log($"[UnityCtl] Client connected from {client.Client.RemoteEndPoint}");
                    _ = HandleClientAsync(client, ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        Debug.LogError($"[UnityCtl] Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                var stream = client.GetStream();

                try
                {
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        // Read length prefix (4 bytes, little-endian)
                        var lengthBuf = new byte[4];
                        var bytesRead = await ReadExactAsync(stream, lengthBuf, 0, 4, ct);
                        if (bytesRead < 4) break; // Client disconnected

                        var messageLength = BitConverter.ToInt32(lengthBuf, 0);
                        if (messageLength <= 0 || messageLength > 10 * 1024 * 1024) // 10MB max
                        {
                            Debug.LogWarning($"[UnityCtl] Invalid message length: {messageLength}");
                            break;
                        }

                        // Read message body
                        var messageBuf = new byte[messageLength];
                        bytesRead = await ReadExactAsync(stream, messageBuf, 0, messageLength, ct);
                        if (bytesRead < messageLength) break;

                        var json = Encoding.UTF8.GetString(messageBuf);
                        var response = await ProcessMessageAsync(json);

                        // Write response with length prefix
                        var responseBytes = Encoding.UTF8.GetBytes(response);
                        var responseLengthBuf = BitConverter.GetBytes(responseBytes.Length);
                        await stream.WriteAsync(responseLengthBuf, 0, 4, ct);
                        await stream.WriteAsync(responseBytes, 0, responseBytes.Length, ct);
                        await stream.FlushAsync(ct);
                    }
                }
                catch (IOException) { /* Client disconnected */ }
                catch (OperationCanceledException) { /* Server shutting down */ }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        Debug.LogError($"[UnityCtl] Client error: {ex.Message}");
                }

                Debug.Log("[UnityCtl] Client disconnected");
            }
        }

        private async Task<string> ProcessMessageAsync(string json)
        {
            try
            {
                var request = JObject.Parse(json);
                var command = request["command"]?.ToString();
                var requestId = request["requestId"]?.ToString() ?? Guid.NewGuid().ToString();
                var args = request["args"] as JObject;

                object? result;

                switch (command)
                {
                    case UnityCtlCommands.DeviceHealth:
                        result = HandleHealth();
                        break;

                    case UnityCtlCommands.DeviceEval:
                        var expression = args?["expression"]?.ToString();
                        var extraNs = args?["namespaces"]?.ToObject<string[]>();
                        result = await HandleEvalAsync(expression, extraNs);
                        break;

                    case UnityCtlCommands.DeviceExecute:
                        var assemblyBytes = args?["assemblyBytes"]?.ToString();
                        var className = args?["className"]?.ToString() ?? "Script";
                        var methodName = args?["methodName"]?.ToString() ?? "Main";
                        var scriptArgs = args?["scriptArgs"]?.ToObject<string[]>();
                        result = HandleExecuteCompiled(assemblyBytes, className, methodName, scriptArgs);
                        break;

                    default:
                        result = new DeviceEvalResult
                        {
                            Success = false,
                            Error = $"Unknown command: '{command}'"
                        };
                        break;
                }

                var response = new JObject
                {
                    ["requestId"] = requestId,
                    ["status"] = "ok",
                    ["result"] = JToken.FromObject(result!)
                };
                return response.ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                var error = new JObject
                {
                    ["requestId"] = "error",
                    ["status"] = "error",
                    ["error"] = new JObject
                    {
                        ["code"] = "INTERNAL_ERROR",
                        ["message"] = ex.Message
                    }
                };
                return error.ToString(Formatting.None);
            }
        }

        private DeviceHealthResult HandleHealth()
        {
            var scriptingBackend = "Unknown";
#if ENABLE_IL2CPP
            scriptingBackend = "IL2CPP";
#elif ENABLE_MONO
            scriptingBackend = "Mono";
#endif
            return new DeviceHealthResult
            {
                Status = "ok",
                Platform = Application.platform.ToString(),
                ScriptingBackend = scriptingBackend,
                UnityVersion = Application.unityVersion,
                DeviceModel = SystemInfo.deviceModel,
                SupportsFullEval = scriptingBackend == "Mono"
            };
        }

        private Task<DeviceEvalResult> HandleEvalAsync(string? expression, string[]? extraNamespaces)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return Task.FromResult(new DeviceEvalResult
                {
                    Success = false,
                    Error = "Expression cannot be empty"
                });
            }

            // Expression evaluation needs to happen on the main thread for Unity API access
            var tcs = new TaskCompletionSource<DeviceEvalResult>();

            _mainThreadActions.Enqueue(() =>
            {
                try
                {
                    var allNamespaces = extraNamespaces;
                    if (additionalNamespaces.Length > 0)
                    {
                        var merged = new string[additionalNamespaces.Length + (extraNamespaces?.Length ?? 0)];
                        Array.Copy(additionalNamespaces, merged, additionalNamespaces.Length);
                        if (extraNamespaces != null)
                            Array.Copy(extraNamespaces, 0, merged, additionalNamespaces.Length, extraNamespaces.Length);
                        allNamespaces = merged;
                    }

                    var evalResult = ExpressionEvaluator.Evaluate(expression!, allNamespaces);

                    tcs.SetResult(new DeviceEvalResult
                    {
                        Success = evalResult.Success,
                        Result = evalResult.SerializeValue(),
                        Error = evalResult.Error,
                        ResultType = evalResult.ResultType
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new DeviceEvalResult
                    {
                        Success = false,
                        Error = $"Evaluation error: {ex.Message}"
                    });
                }
            });

            return tcs.Task;
        }

        private DeviceEvalResult HandleExecuteCompiled(string? assemblyBytesBase64, string className, string methodName, string[]? scriptArgs)
        {
#if ENABLE_IL2CPP
            return new DeviceEvalResult
            {
                Success = false,
                Error = "Full C# eval is not supported on IL2CPP builds. " +
                        "Use expression eval instead (e.g., 'Screen.width', 'Application.version'). " +
                        "For full eval support, build with Mono scripting backend."
            };
#else
            if (string.IsNullOrEmpty(assemblyBytesBase64))
            {
                return new DeviceEvalResult
                {
                    Success = false,
                    Error = "Assembly bytes cannot be empty"
                };
            }

            try
            {
                var bytes = Convert.FromBase64String(assemblyBytesBase64);
                var assembly = Assembly.Load(bytes);
                var type = assembly.GetType(className);

                if (type == null)
                {
                    return new DeviceEvalResult
                    {
                        Success = false,
                        Error = $"Class '{className}' not found in compiled assembly"
                    };
                }

                // Find method - try with string[] parameter first, then parameterless
                var methodWithArgs = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(string[]) }, null);
                var methodNoArgs = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, Type.EmptyTypes, null);

                MethodInfo? method;
                object?[]? invokeArgs;

                if (methodWithArgs != null)
                {
                    method = methodWithArgs;
                    invokeArgs = new object?[] { scriptArgs ?? Array.Empty<string>() };
                }
                else if (methodNoArgs != null)
                {
                    method = methodNoArgs;
                    invokeArgs = null;
                }
                else
                {
                    return new DeviceEvalResult
                    {
                        Success = false,
                        Error = $"Static method '{methodName}' not found in class '{className}'"
                    };
                }

                var result = method.Invoke(null, invokeArgs);
                string? resultString = null;
                if (result != null)
                {
                    try { resultString = JsonConvert.SerializeObject(result, Formatting.Indented); }
                    catch { resultString = result.ToString(); }
                }

                return new DeviceEvalResult
                {
                    Success = true,
                    Result = resultString,
                    ResultType = result?.GetType().FullName
                };
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                return new DeviceEvalResult
                {
                    Success = false,
                    Error = $"Runtime error: {inner.Message}\n{inner.StackTrace}"
                };
            }
            catch (Exception ex)
            {
                return new DeviceEvalResult
                {
                    Success = false,
                    Error = $"Execution error: {ex.Message}"
                };
            }
#endif
        }

        private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                var read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct);
                if (read == 0) return totalRead; // Connection closed
                totalRead += read;
            }
            return totalRead;
        }
    }
}
