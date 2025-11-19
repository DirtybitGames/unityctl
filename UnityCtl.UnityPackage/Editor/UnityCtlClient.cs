using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityCtl.Protocol;

namespace UnityCtl
{
    public class UnityCtlClient
    {
        private static UnityCtlClient _instance;
        public static UnityCtlClient Instance => _instance ??= new UnityCtlClient();

        private ClientWebSocket _webSocket;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();
        private CancellationTokenSource _receiveCts;
        private bool _isConnected;
        private string _projectRoot;
        private string _projectId;

        // Automatic reconnection tracking
        private float _lastConnectionCheck;
        private float _lastReconnectAttempt;
        private int _reconnectAttempts;

        // Conditional debug logging helpers
        private static void DebugLog(string message)
        {
            if (Editor.UnityCtlSettings.Instance.ShowDebugMessages)
            {
                Debug.Log(message);
            }
        }

        private static void DebugLogWarning(string message)
        {
            if (Editor.UnityCtlSettings.Instance.ShowDebugMessages)
            {
                Debug.LogWarning(message);
            }
        }

        private static void DebugLogError(string message)
        {
            if (Editor.UnityCtlSettings.Instance.ShowDebugMessages)
            {
                Debug.LogError(message);
            }
        }

        // Log buffering for early logs before connection
        private const int MAX_BUFFERED_LOGS = 1000;
        private readonly System.Collections.Generic.Queue<EventMessage> _logBuffer = new();
        private readonly object _bufferLock = new object();

        public void TryConnectIfBridgePresent()
        {
            try
            {
                // Find project root
                _projectRoot = FindProjectRoot();
                if (string.IsNullOrEmpty(_projectRoot))
                {
                    DebugLogWarning("[UnityCtl] Cannot find project root");
                    return;
                }

                // Read bridge config
                var config = ProjectLocator.ReadBridgeConfig(_projectRoot);
                if (config == null)
                {
                    DebugLog("[UnityCtl] No bridge config found (.unityctl/bridge.json missing)");
                    return;
                }

                _projectId = config.ProjectId;

                DebugLog($"[UnityCtl] Attempting to connect to bridge on port {config.Port}...");

                // Connect to bridge
                ConnectAsync(config.Port).ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        DebugLogWarning($"[UnityCtl] Failed to connect to bridge: {task.Exception?.GetBaseException().Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] Error during connection attempt: {ex.Message}");
            }
        }

        private string FindProjectRoot()
        {
            var dataPath = Application.dataPath; // e.g., "C:/Project/Assets"
            var projectRoot = Directory.GetParent(dataPath)?.FullName;
            return projectRoot;
        }

        private async Task ConnectAsync(int port)
        {
            // Skip if already connected with a healthy WebSocket
            if (_isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                DebugLog("[UnityCtl] Already connected, skipping connection attempt");
                return;
            }

            // Clean up any existing WebSocket
            if (_webSocket != null)
            {
                try
                {
                    _webSocket.Dispose();
                }
                catch (Exception disposeEx)
                {
                    DebugLogWarning($"[UnityCtl] Error disposing old WebSocket: {disposeEx.Message}");
                }
            }

            _webSocket = new ClientWebSocket();
            _receiveCts?.Cancel();
            _receiveCts = new CancellationTokenSource();

            var uri = new Uri($"ws://localhost:{port}/unity");

            try
            {
                await _webSocket.ConnectAsync(uri, _receiveCts.Token);
                _isConnected = true;
                _reconnectAttempts = 0; // Reset reconnection counter on successful connection

                DebugLog($"[UnityCtl] ✓ Connected to bridge at port {port}");

                // Send hello message
                await SendHelloMessageAsync();

                // Flush any logs that were buffered before connection
                FlushLogBuffer();

                // Start receive loop
                _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] ✗ Connection failed: {ex.Message}");
                _isConnected = false;
            }
        }

        private async Task SendHelloMessageAsync()
        {
            var hello = new HelloMessage
            {
                Origin = MessageOrigin.Unity,
                ProjectId = _projectId,
                UnityVersion = Application.unityVersion,
                EditorInstanceId = SystemInfo.deviceUniqueIdentifier,
                Capabilities = new[] { "console", "asset", "scene", "play", "compile", "menu", "test" },
                ProtocolVersion = "1.0.0"
            };

            await SendMessageAsync(hello);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 16];
            var messageBuilder = new System.Text.StringBuilder();

            try
            {
                while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        DebugLog("[UnityCtl] Bridge closed connection gracefully");
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // Append the received chunk to the message builder
                        var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(chunk);

                        // Only process when we have the complete message
                        if (result.EndOfMessage)
                        {
                            var json = messageBuilder.ToString();
                            messageBuilder.Clear();
                            HandleMessageOnBackgroundThread(json);
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    DebugLog("[UnityCtl] Receive loop cancelled");
                }
                else if (_webSocket.State != WebSocketState.Open)
                {
                    DebugLogWarning($"[UnityCtl] WebSocket closed unexpectedly (state: {_webSocket.State})");
                }
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] Receive loop error: {ex.Message}");
            }
            finally
            {
                _isConnected = false;
                DebugLog("[UnityCtl] Disconnected from bridge - will attempt to reconnect");
            }
        }

        private void HandleMessageOnBackgroundThread(string json)
        {
            try
            {
                // Simple type extraction without System.Text.Json dependency
                var messageType = ExtractMessageType(json);

                if (messageType == "request")
                {
                    var request = JsonHelper.Deserialize<RequestMessage>(json);
                    if (request != null)
                    {
                        // Enqueue command to be executed on main thread
                        _mainThreadActions.Enqueue(() => HandleCommand(request));
                    }
                }
                else if (messageType == "response")
                {
                    // Bridge responded to our hello
                    DebugLog("[UnityCtl] Handshake complete");
                }
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Failed to handle message: {ex.Message}");
            }
        }

        private string ExtractMessageType(string json)
        {
            // Simple JSON parsing to extract "type" field
            var typeIndex = json.IndexOf("\"type\"", StringComparison.Ordinal);
            if (typeIndex < 0) return null;

            var colonIndex = json.IndexOf(":", typeIndex);
            if (colonIndex < 0) return null;

            var startQuote = json.IndexOf("\"", colonIndex);
            if (startQuote < 0) return null;

            var endQuote = json.IndexOf("\"", startQuote + 1);
            if (endQuote < 0) return null;

            return json.Substring(startQuote + 1, endQuote - startQuote - 1);
        }

        public void Pump()
        {
            // Execute queued main-thread actions
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    DebugLogError($"[UnityCtl] Error executing command: {ex.Message}");
                }
            }

            // Periodic connection health check and automatic reconnection
            var currentTime = Time.realtimeSinceStartup;

            // Check connection health every 3 seconds
            if (currentTime - _lastConnectionCheck > 3f)
            {
                _lastConnectionCheck = currentTime;

                // If disconnected or WebSocket is not in Open state, attempt reconnection
                if (!_isConnected || _webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    // Calculate exponential backoff delay: 1s, 2s, 4s, 8s, 16s, 30s (max)
                    var backoffDelay = Mathf.Min(1f * Mathf.Pow(2, _reconnectAttempts), 30f);

                    // Calculate time since last reconnection attempt
                    var timeSinceLastReconnect = currentTime - _lastReconnectAttempt;

                    // Attempt reconnection if:
                    // 1. This is the first attempt (_reconnectAttempts == 0), OR
                    // 2. Enough time has passed according to backoff delay
                    if (_reconnectAttempts == 0 || timeSinceLastReconnect >= backoffDelay)
                    {
                        _lastReconnectAttempt = currentTime;
                        _reconnectAttempts++;

                        DebugLog($"[UnityCtl] Reconnection attempt #{_reconnectAttempts} (backoff: {backoffDelay:F1}s)");
                        TryConnectIfBridgePresent();
                    }
                }
            }
        }

        private void HandleCommand(RequestMessage request)
        {
            try
            {
                object result = null;

                switch (request.Command)
                {
                    case UnityCtlCommands.ConsoleTail:
                        // Console logs are buffered in the bridge
                        result = new { };
                        break;

                    case UnityCtlCommands.SceneList:
                        result = HandleSceneList(request);
                        break;

                    case UnityCtlCommands.SceneLoad:
                        result = HandleSceneLoad(request);
                        break;

                    case UnityCtlCommands.PlayEnter:
                        EditorApplication.isPlaying = true;
                        result = new PlayModeResult { State = PlayModeState.Playing };
                        break;

                    case UnityCtlCommands.PlayExit:
                        EditorApplication.isPlaying = false;
                        result = new PlayModeResult { State = PlayModeState.Stopped };
                        break;

                    case UnityCtlCommands.PlayToggle:
                        EditorApplication.isPlaying = !EditorApplication.isPlaying;
                        result = new PlayModeResult
                        {
                            State = EditorApplication.isPlaying ? PlayModeState.Playing : PlayModeState.Stopped
                        };
                        break;

                    case UnityCtlCommands.PlayStatus:
                        result = new PlayModeResult
                        {
                            State = EditorApplication.isPlaying ? PlayModeState.Playing : PlayModeState.Stopped
                        };
                        break;

                    case UnityCtlCommands.AssetImport:
                        result = HandleAssetImport(request);
                        break;

                    case UnityCtlCommands.CompileScripts:
                        // Refresh asset database first to pick up new scripts
                        AssetDatabase.Refresh();
                        CompilationPipeline.RequestScriptCompilation();
                        result = new CompileResult { Started = true };
                        break;

                    case UnityCtlCommands.MenuList:
                        result = HandleMenuList(request);
                        break;

                    case UnityCtlCommands.MenuExecute:
                        result = HandleMenuExecute(request);
                        break;

                    case UnityCtlCommands.TestRun:
                        result = HandleTestRun(request);
                        break;

                    default:
                        SendResponseError(request.RequestId, "unknown_command", $"Unknown command: {request.Command}");
                        return;
                }

                SendResponseOk(request.RequestId, result);
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Command failed: {ex.Message}");
                SendResponseError(request.RequestId, "command_failed", ex.Message);
            }
        }

        private object HandleSceneList(RequestMessage request)
        {
            var source = GetStringArgument(request, "source") ?? "buildSettings";

            if (source == "buildSettings")
            {
                var buildScenes = EditorBuildSettings.scenes;
                var scenes = new SceneInfo[buildScenes.Length];
                for (int i = 0; i < buildScenes.Length; i++)
                {
                    scenes[i] = new SceneInfo
                    {
                        Path = buildScenes[i].path,
                        EnabledInBuild = buildScenes[i].enabled
                    };
                }
                return new SceneListResult { Scenes = scenes };
            }
            else
            {
                // Find all scenes in the project
                var sceneGuids = AssetDatabase.FindAssets("t:Scene");
                var scenes = new SceneInfo[sceneGuids.Length];
                for (int i = 0; i < sceneGuids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(sceneGuids[i]);
                    scenes[i] = new SceneInfo
                    {
                        Path = path,
                        EnabledInBuild = false
                    };
                }
                return new SceneListResult { Scenes = scenes };
            }
        }

        private object HandleSceneLoad(RequestMessage request)
        {
            var path = GetStringArgument(request, "path");
            var mode = GetStringArgument(request, "mode") ?? "single";

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Scene path is required");
            }

            var openMode = mode == "additive" ? OpenSceneMode.Additive : OpenSceneMode.Single;
            EditorSceneManager.OpenScene(path, openMode);

            return new SceneLoadResult { LoadedScenePath = path };
        }

        private object HandleAssetImport(RequestMessage request)
        {
            var path = GetStringArgument(request, "path");

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Asset path is required");
            }

            // Start the import - this will complete asynchronously
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            // Register callback to send completion event when done
            EditorApplication.delayCall += () =>
            {
                // Wait one frame for the import to fully complete
                EditorApplication.delayCall += () =>
                {
                    SendAssetImportCompleteEvent(path, true);
                };
            };

            return new AssetImportResult { Success = true };
        }

        private static string GetStringArgument(RequestMessage request, string key)
        {
            if (request.Args == null) return null;

            try
            {
                // Try to access as dictionary
                if (request.Args is System.Collections.IDictionary dict && dict.Contains(key))
                {
                    var value = dict[key];
                    if (value == null) return null;

                    var valueType = value.GetType().FullName;
                    DebugLog($"[UnityCtl] Argument '{key}' type: {valueType}, value: {value}");

                    // Direct string
                    if (value is string str)
                    {
                        return str;
                    }

                    // Handle Newtonsoft.Json types
                    if (value is JToken jtoken)
                    {
                        if (jtoken.Type == JTokenType.String)
                        {
                            return jtoken.Value<string>();
                        }
                        else
                        {
                            // For non-string tokens, convert to string
                            return jtoken.ToString();
                        }
                    }

                    // Check for System.Text.Json JsonElement
                    if (valueType.Contains("JsonElement"))
                    {
                        // Use reflection to get the value
                        var getStringMethod = value.GetType().GetMethod("GetString");
                        if (getStringMethod != null)
                        {
                            return (string)getStringMethod.Invoke(value, null);
                        }
                    }

                    // Last resort: try ToString()
                    var result = value.ToString();
                    DebugLog($"[UnityCtl] ToString result: {result}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Failed to get argument '{key}': {ex.Message}");
            }

            return null;
        }

        private object HandleMenuList(RequestMessage request)
        {
            var menuItems = new List<MenuItemInfo>();

            // Scan all loaded assemblies for methods with [MenuItem] attribute
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        foreach (var method in methods)
                        {
                            try
                            {
                                var attributes = method.GetCustomAttributes(typeof(MenuItem), false);
                                if (attributes.Length > 0)
                                {
                                    var menuItem = (MenuItem)attributes[0];
                                    var menuPath = menuItem.menuItem;

                                    // Skip invalid menu items
                                    if (string.IsNullOrEmpty(menuPath))
                                    {
                                        continue;
                                    }

                                    // Skip context menu items (they start with specific prefixes)
                                    if (menuPath.StartsWith("CONTEXT/") || menuPath.StartsWith("internal:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    menuItems.Add(new MenuItemInfo
                                    {
                                        Path = menuPath,
                                        Priority = menuItem.priority
                                    });
                                }
                            }
                            catch (Exception methodEx)
                            {
                                // Skip individual methods that fail
                                DebugLogWarning($"[UnityCtl] Skipped method in {type.FullName}: {methodEx.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Some assemblies may throw when calling GetTypes(), skip them
                    DebugLogWarning($"[UnityCtl] Skipped assembly {assembly.FullName}: {ex.Message}");
                }
            }

            // Remove duplicates and sort by path for consistent output
            menuItems = menuItems
                .GroupBy(m => m.Path)
                .Select(g => g.First())
                .OrderBy(m => m.Path)
                .ToList();

            return new MenuListResult { MenuItems = menuItems.ToArray() };
        }

        private object HandleMenuExecute(RequestMessage request)
        {
            var menuPath = GetStringArgument(request, "menuPath");

            if (string.IsNullOrEmpty(menuPath))
            {
                throw new ArgumentException("Menu path is required");
            }

            // Execute the menu item
            bool success = EditorApplication.ExecuteMenuItem(menuPath);

            if (success)
            {
                return new MenuExecuteResult
                {
                    Success = true,
                    MenuPath = menuPath,
                    Message = $"Menu item '{menuPath}' executed successfully"
                };
            }
            else
            {
                return new MenuExecuteResult
                {
                    Success = false,
                    MenuPath = menuPath,
                    Message = $"Menu item '{menuPath}' does not exist or could not be executed"
                };
            }
        }

        private object HandleTestRun(RequestMessage request)
        {
            var mode = GetStringArgument(request, "mode") ?? "editmode";
            var filter = GetStringArgument(request, "filter");
            var testRunId = System.Guid.NewGuid().ToString();

            // Use Unity's TestRunner API
            var testRunnerApi = ScriptableObject.CreateInstance<UnityEditor.TestTools.TestRunner.Api.TestRunnerApi>();

            // Configure test filter
            var testMode = mode.ToLower() == "playmode"
                ? UnityEditor.TestTools.TestRunner.Api.TestMode.PlayMode
                : UnityEditor.TestTools.TestRunner.Api.TestMode.EditMode;

            var testFilter = new UnityEditor.TestTools.TestRunner.Api.Filter
            {
                testMode = testMode
            };

            // Apply name filter if provided
            if (!string.IsNullOrEmpty(filter))
            {
                testFilter.testNames = new[] { filter };
            }

            // Create callback handler that will send event when done
            var callback = new TestRunnerCallback(testRunId, this);
            testRunnerApi.RegisterCallbacks(callback);

            // Run tests (this starts the test run asynchronously and returns immediately)
            testRunnerApi.Execute(new UnityEditor.TestTools.TestRunner.Api.ExecutionSettings(testFilter));

            // Return immediately - results will be sent via TestFinished event
            return new TestRunResult
            {
                Started = true,
                TestRunId = testRunId
            };
        }

        // Nested class to handle test runner callbacks
        private class TestRunnerCallback : UnityEditor.TestTools.TestRunner.Api.ICallbacks
        {
            private readonly string _testRunId;
            private readonly UnityCtlClient _client;
            private readonly System.Collections.Generic.List<TestFailureInfo> _failures = new System.Collections.Generic.List<TestFailureInfo>();
            private int _passed = 0;
            private int _failed = 0;
            private int _skipped = 0;
            private System.DateTime _startTime;

            public TestRunnerCallback(string testRunId, UnityCtlClient client)
            {
                _testRunId = testRunId;
                _client = client;
            }

            public void RunStarted(UnityEditor.TestTools.TestRunner.Api.ITestAdaptor testsToRun)
            {
                _startTime = System.DateTime.UtcNow;
                _failures.Clear();
                _passed = 0;
                _failed = 0;
                _skipped = 0;
            }

            public void RunFinished(UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor result)
            {
                // Send test finished event
                _client.SendTestFinishedEvent(GetTestFinishedPayload());
            }

            public void TestStarted(UnityEditor.TestTools.TestRunner.Api.ITestAdaptor test)
            {
                // No action needed
            }

            public void TestFinished(UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor result)
            {
                // Only count leaf nodes (actual test methods), not parent containers
                if (result.Test.HasChildren)
                {
                    return;
                }

                if (result.TestStatus == UnityEditor.TestTools.TestRunner.Api.TestStatus.Passed)
                {
                    _passed++;
                }
                else if (result.TestStatus == UnityEditor.TestTools.TestRunner.Api.TestStatus.Failed)
                {
                    _failed++;
                    _failures.Add(new TestFailureInfo
                    {
                        TestName = result.Test.FullName,
                        Message = result.Message ?? "Test failed",
                        StackTrace = result.StackTrace
                    });
                }
                else if (result.TestStatus == UnityEditor.TestTools.TestRunner.Api.TestStatus.Skipped ||
                         result.TestStatus == UnityEditor.TestTools.TestRunner.Api.TestStatus.Inconclusive)
                {
                    _skipped++;
                }
            }

            private TestFinishedPayload GetTestFinishedPayload()
            {
                var duration = (System.DateTime.UtcNow - _startTime).TotalSeconds;
                return new TestFinishedPayload
                {
                    TestRunId = _testRunId,
                    Passed = _passed,
                    Failed = _failed,
                    Skipped = _skipped,
                    Duration = duration,
                    Failures = _failures.ToArray()
                };
            }
        }

        public void SendTestFinishedEvent(TestFinishedPayload payload)
        {
            if (!_isConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.TestFinished,
                Payload = payload
            };

            _ = SendMessageAsync(eventMessage);
        }

        private void SendResponseOk(string requestId, object result)
        {
            var response = new ResponseMessage
            {
                Origin = MessageOrigin.Unity,
                RequestId = requestId,
                Status = ResponseStatus.Ok,
                Result = result
            };

            _ = SendMessageAsync(response);
        }

        private void SendResponseError(string requestId, string code, string message)
        {
            var response = new ResponseMessage
            {
                Origin = MessageOrigin.Unity,
                RequestId = requestId,
                Status = ResponseStatus.Error,
                Error = new ErrorPayload
                {
                    Code = code,
                    Message = message
                }
            };

            _ = SendMessageAsync(response);
        }

        public void SendLogEvent(string message, string stackTrace, LogType type)
        {
            var level = type switch
            {
                LogType.Error => LogLevel.Error,
                LogType.Assert => LogLevel.Error,
                LogType.Warning => LogLevel.Warning,
                LogType.Exception => LogLevel.Exception,
                _ => LogLevel.Log
            };

            var logEntry = new LogEntry
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                Level = level,
                Message = message,
                StackTrace = string.IsNullOrEmpty(stackTrace) ? null : stackTrace
            };

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.Log,
                Payload = logEntry
            };

            // If not connected, buffer the log for later
            if (!_isConnected)
            {
                lock (_bufferLock)
                {
                    // Respect buffer size limit - drop oldest logs if at capacity
                    if (_logBuffer.Count >= MAX_BUFFERED_LOGS)
                    {
                        _logBuffer.Dequeue();
                    }
                    _logBuffer.Enqueue(eventMessage);
                }
                return;
            }

            _ = SendMessageAsync(eventMessage);
        }

        private void FlushLogBuffer()
        {
            if (!_isConnected)
            {
                return;
            }

            EventMessage[] bufferedLogs;
            lock (_bufferLock)
            {
                if (_logBuffer.Count == 0)
                {
                    return;
                }

                // Copy buffered logs to array and clear buffer
                bufferedLogs = _logBuffer.ToArray();
                _logBuffer.Clear();
            }

            // Send all buffered logs
            foreach (var logMessage in bufferedLogs)
            {
                _ = SendMessageAsync(logMessage);
            }

            if (bufferedLogs.Length > 0)
            {
                DebugLog($"[UnityCtl] Flushed {bufferedLogs.Length} buffered log(s)");
            }
        }

        public void SendPlayModeChangedEvent(PlayModeStateChange stateChange)
        {
            if (!_isConnected) return;

            var state = stateChange switch
            {
                PlayModeStateChange.EnteredPlayMode => "EnteredPlayMode",
                PlayModeStateChange.ExitingPlayMode => "ExitingPlayMode",
                PlayModeStateChange.EnteredEditMode => "EnteredEditMode",
                PlayModeStateChange.ExitingEditMode => "ExitingEditMode",
                _ => stateChange.ToString()
            };

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.PlayModeChanged,
                Payload = new PlayModeChangedPayload { State = state }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendCompilationStartedEvent()
        {
            if (!_isConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.CompilationStarted,
                Payload = new { }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendCompilationFinishedEvent(bool success)
        {
            if (!_isConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.CompilationFinished,
                Payload = new CompilationFinishedPayload { Success = success }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendDomainReloadStartingEvent()
        {
            if (!_isConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.DomainReloadStarting,
                Payload = new { }
            };

            try
            {
                // Use synchronous wait to ensure delivery before domain unloads
                // This is critical - the callback is synchronous so Unity waits for us
                SendMessageAsync(eventMessage).Wait(100);
            }
            catch (System.Exception ex)
            {
                // Log but don't throw - domain reload will happen anyway
                DebugLogWarning($"[UnityCtl] Failed to send domain reload event: {ex.Message}");
            }
        }

        public void SendAssetImportCompleteEvent(string path, bool success)
        {
            if (!_isConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.AssetImportComplete,
                Payload = new AssetImportCompletePayload { Path = path, Success = success }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendAssetReimportAllCompleteEvent(bool success)
        {
            if (!_isConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.AssetReimportAllComplete,
                Payload = new AssetReimportCompletePayload { Success = success }
            };

            _ = SendMessageAsync(eventMessage);
        }

        private async Task SendMessageAsync(BaseMessage message)
        {
            if (!_isConnected || _webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                var json = JsonHelper.Serialize(message);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] Failed to send message: {ex.Message}");
                _isConnected = false;
            }
        }
    }
}
