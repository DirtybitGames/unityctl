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

        // Public connection status
        public bool IsConnected
        {
            get
            {
                lock (_connectionLock)
                {
                    return _isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open;
                }
            }
        }

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
        private readonly object _connectionLock = new object();

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
            ClientWebSocket oldSocket;
            ClientWebSocket newSocket;
            CancellationTokenSource newCts;

            lock (_connectionLock)
            {
                // Skip if already connected with a healthy WebSocket
                if (_isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    DebugLog("[UnityCtl] Already connected, skipping connection attempt");
                    return;
                }

                oldSocket = _webSocket;
                newSocket = new ClientWebSocket();
                _webSocket = newSocket;

                _receiveCts?.Cancel();
                newCts = new CancellationTokenSource();
                _receiveCts = newCts;
            }

            // Dispose old socket outside lock
            if (oldSocket != null)
            {
                try
                {
                    oldSocket.Dispose();
                }
                catch (Exception disposeEx)
                {
                    DebugLogWarning($"[UnityCtl] Error disposing old WebSocket: {disposeEx.Message}");
                }
            }

            var uri = new Uri($"ws://localhost:{port}/unity");

            try
            {
                await newSocket.ConnectAsync(uri, newCts.Token);

                lock (_connectionLock)
                {
                    _isConnected = true;
                }
                _reconnectAttempts = 0; // Reset reconnection counter on successful connection

                DebugLog($"[UnityCtl] ✓ Connected to bridge at port {port}");

                // Send hello message
                await SendHelloMessageAsync();

                // Flush any logs that were buffered before connection
                FlushLogBuffer();

                // Start receive loop
                _ = Task.Run(() => ReceiveLoopAsync(newCts.Token));
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] ✗ Connection failed: {ex.Message}");
                lock (_connectionLock)
                {
                    _isConnected = false;
                }
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
                ProtocolVersion = "1.0.0",
                PluginVersion = GetPluginVersion()
            };

            await SendMessageAsync(hello);
        }

        private static string GetPluginVersion()
        {
            try
            {
                // Use Unity's PackageManager API to get the package version
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityCtlClient).Assembly);
                if (packageInfo != null)
                {
                    return packageInfo.version;
                }
            }
            catch
            {
                // Ignore errors
            }
            return "unknown";
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 16];
            var messageBuilder = new System.Text.StringBuilder();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ClientWebSocket socket;
                    lock (_connectionLock)
                    {
                        socket = _webSocket;
                        if (socket == null || socket.State != WebSocketState.Open) break;
                    }

                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        DebugLog("[UnityCtl] Bridge closed connection gracefully");
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
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
                else
                {
                    DebugLogWarning("[UnityCtl] WebSocket closed unexpectedly");
                }
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] Receive loop error: {ex.Message}");
            }
            finally
            {
                lock (_connectionLock)
                {
                    _isConnected = false;
                }
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
                if (!IsConnected)
                {
                    // Calculate exponential backoff delay: 1s, 2s, 4s, 8s, 15s (max)
                    var backoffDelay = Mathf.Min(1f * Mathf.Pow(2, _reconnectAttempts), 15f);

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
                    case UnityCtlCommands.SceneList:
                        result = HandleSceneList(request);
                        break;

                    case UnityCtlCommands.SceneLoad:
                        result = HandleSceneLoad(request);
                        break;

                    case UnityCtlCommands.PlayEnter:
                        if (EditorApplication.isPlaying)
                        {
                            // Already in play mode - return current state immediately
                            result = new PlayModeResult { State = "AlreadyPlaying" };
                        }
                        else
                        {
                            EditorApplication.isPlaying = true;
                            result = new PlayModeResult { State = PlayModeState.Transitioning };
                        }
                        break;

                    case UnityCtlCommands.PlayExit:
                        if (!EditorApplication.isPlaying)
                        {
                            // Already stopped - return current state immediately
                            result = new PlayModeResult { State = "AlreadyStopped" };
                        }
                        else
                        {
                            EditorApplication.isPlaying = false;
                            result = new PlayModeResult { State = PlayModeState.Transitioning };
                        }
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

                    case UnityCtlCommands.AssetRefresh:
                        // Force refresh to ensure assets are reimported even without editor focus
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                        // Check if compilation was triggered after refresh
                        var compilationTriggered = EditorApplication.isCompiling;
                        // Check if there are existing compilation errors
                        var hasCompilationErrors = EditorUtility.scriptCompilationFailed;
                        SendAssetRefreshCompleteEvent(compilationTriggered, hasCompilationErrors);
                        result = new { started = true, compilationTriggered, hasCompilationErrors };
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

                    case UnityCtlCommands.ScreenshotCapture:
                        result = HandleScreenshotCapture(request);
                        break;

                    case UnityCtlCommands.ScriptExecute:
                        result = HandleScriptExecute(request);
                        break;

                    case UnityCtlCommands.RecordStart:
                        result = HandleRecordStart(request);
                        break;

                    case UnityCtlCommands.RecordStop:
                        result = HandleRecordStop(request);
                        break;

                    case UnityCtlCommands.RecordStatus:
                        result = Editor.RecordingManager.Instance.GetStatus();
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

            // ImportAsset is synchronous - it blocks until complete
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            // Send completion event immediately since import is synchronous
            SendAssetImportCompleteEvent(path, true);

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

        private static int? GetIntArgument(RequestMessage request, string key)
        {
            if (request.Args == null) return null;

            try
            {
                // Try to access as dictionary
                if (request.Args is System.Collections.IDictionary dict && dict.Contains(key))
                {
                    var value = dict[key];
                    if (value == null) return null;

                    // Direct int
                    if (value is int intValue)
                    {
                        return intValue;
                    }

                    // Handle long (JavaScript numbers can be sent as long)
                    if (value is long longValue)
                    {
                        return (int)longValue;
                    }

                    // Handle Newtonsoft.Json types
                    if (value is JToken jtoken && jtoken.Type == JTokenType.Integer)
                    {
                        return jtoken.Value<int>();
                    }

                    // Try to parse as int
                    if (int.TryParse(value.ToString(), out var parsed))
                    {
                        return parsed;
                    }

                    DebugLog($"[UnityCtl] Could not parse argument '{key}' as int: {value}");
                }
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Failed to get int argument '{key}': {ex.Message}");
            }

            return null;
        }

        private static string[] GetStringArrayArgument(RequestMessage request, string key)
        {
            if (request.Args == null) return null;

            try
            {
                // Try to access as dictionary
                if (request.Args is System.Collections.IDictionary dict && dict.Contains(key))
                {
                    var value = dict[key];
                    if (value == null) return null;

                    // Direct string array
                    if (value is string[] strArray)
                    {
                        return strArray;
                    }

                    // Handle Newtonsoft.Json JArray
                    if (value is JArray jarray)
                    {
                        return jarray.Select(t => t.Value<string>()).ToArray();
                    }

                    // Handle generic IEnumerable (but not string itself)
                    if (value is System.Collections.IEnumerable enumerable && !(value is string))
                    {
                        var result = new System.Collections.Generic.List<string>();
                        foreach (var item in enumerable)
                        {
                            if (item is JToken jtoken)
                            {
                                result.Add(jtoken.Value<string>());
                            }
                            else
                            {
                                result.Add(item?.ToString());
                            }
                        }
                        return result.ToArray();
                    }

                    DebugLog($"[UnityCtl] Could not parse argument '{key}' as string array: {value.GetType().FullName}");
                }
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Failed to get string array argument '{key}': {ex.Message}");
            }

            return null;
        }

        private static void ForceGameViewUpdate()
        {
            try
            {
                // Forces the editor to process the loop immediately
                EditorApplication.QueuePlayerLoopUpdate();

                // Force the GameView to repaint specifically
                System.Reflection.Assembly assembly = typeof(EditorWindow).Assembly;
                System.Type type = assembly.GetType("UnityEditor.GameView");
                if (type != null)
                {
                    EditorWindow gameView = EditorWindow.GetWindow(type, false, null, false);
                    if (gameView != null)
                    {
                        gameView.Repaint();
                        DebugLog("[UnityCtl] Forced GameView update for screenshot");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Failed to force GameView update: {ex.Message}");
            }
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

            // Apply name filter if provided - supports partial matching
            if (!string.IsNullOrEmpty(filter))
            {
                var matchingTests = new System.Collections.Generic.List<string>();

                // Retrieve all tests for this mode using callback
                // Note: This callback is asynchronous - it runs later
                testRunnerApi.RetrieveTestList(testMode, (rootTest) =>
                {
                    // Recursively collect matching test names
                    CollectMatchingTests(rootTest, filter, matchingTests);

                    DebugLog($"[UnityCtl] Filter '{filter}' matched {matchingTests.Count} test(s)");

                    // Build the test filter with matched tests
                    var testFilter = new UnityEditor.TestTools.TestRunner.Api.Filter
                    {
                        testMode = testMode
                    };

                    if (matchingTests.Count > 0)
                    {
                        testFilter.testNames = matchingTests.ToArray();
                        DebugLog($"[UnityCtl] Running {matchingTests.Count} matched test(s)");
                    }
                    else
                    {
                        // If no matches found, still pass the filter as-is for exact match attempt
                        DebugLog($"[UnityCtl] No matches found, attempting exact match with: {filter}");
                        testFilter.testNames = new[] { filter };
                    }

                    // Create callback handler that will send event when done
                    var callback = new TestRunnerCallback(testRunId, this);
                    testRunnerApi.RegisterCallbacks(callback);

                    // Run tests (this starts the test run asynchronously and returns immediately)
                    testRunnerApi.Execute(new UnityEditor.TestTools.TestRunner.Api.ExecutionSettings(testFilter));
                });
            }
            else
            {
                // No filter - run all tests
                var testFilter = new UnityEditor.TestTools.TestRunner.Api.Filter
                {
                    testMode = testMode
                };

                var callback = new TestRunnerCallback(testRunId, this);
                testRunnerApi.RegisterCallbacks(callback);
                testRunnerApi.Execute(new UnityEditor.TestTools.TestRunner.Api.ExecutionSettings(testFilter));
            }

            // Return immediately - results will be sent via TestFinished event
            return new TestRunResult
            {
                Started = true,
                TestRunId = testRunId
            };
        }

        private object HandleScreenshotCapture(RequestMessage request)
        {
            var path = GetStringArgument(request, "path");
            var width = GetIntArgument(request, "width");
            var height = GetIntArgument(request, "height");

            // Generate default path with timestamp if not provided
            if (string.IsNullOrEmpty(path))
            {
                var timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                path = $"Screenshots/screenshot_{timestamp}.png";
            }

            // Ensure path uses forward slashes and has .png extension
            path = path.Replace("\\", "/");
            if (!path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase))
            {
                path += ".png";
            }

            // If path doesn't start with Screenshots/, prepend it (unless it's an absolute path)
            if (!path.StartsWith("Screenshots/") && !System.IO.Path.IsPathRooted(path))
            {
                path = "Screenshots/" + path;
            }

            // Ensure directory exists
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                DebugLog($"[UnityCtl] Created directory: {directory}");
            }

            // Get current game view resolution
            int actualWidth = UnityEngine.Screen.width;
            int actualHeight = UnityEngine.Screen.height;

            // Force GameView to update if not in play mode
            // This ensures the screenshot is captured even when the window isn't focused
            if (!EditorApplication.isPlaying)
            {
                ForceGameViewUpdate();
            }

            // Capture screenshot
            if (width.HasValue && height.HasValue)
            {
                // Calculate supersize multiplier based on desired width
                int superSize = System.Math.Max(1, width.Value / actualWidth);
                UnityEngine.ScreenCapture.CaptureScreenshot(path, superSize);
                actualWidth = width.Value;
                actualHeight = height.Value;
                DebugLog($"[UnityCtl] Capturing screenshot with supersize {superSize} to: {path}");
            }
            else if (width.HasValue)
            {
                int superSize = System.Math.Max(1, width.Value / actualWidth);
                UnityEngine.ScreenCapture.CaptureScreenshot(path, superSize);
                actualWidth = width.Value;
                actualHeight = actualHeight * superSize;
                DebugLog($"[UnityCtl] Capturing screenshot with width {width.Value} to: {path}");
            }
            else if (height.HasValue)
            {
                int superSize = System.Math.Max(1, height.Value / actualHeight);
                UnityEngine.ScreenCapture.CaptureScreenshot(path, superSize);
                actualWidth = actualWidth * superSize;
                actualHeight = height.Value;
                DebugLog($"[UnityCtl] Capturing screenshot with height {height.Value} to: {path}");
            }
            else
            {
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                DebugLog($"[UnityCtl] Capturing screenshot at game view resolution to: {path}");
            }

            return new ScreenshotCaptureResult
            {
                Path = path,
                Width = actualWidth,
                Height = actualHeight
            };
        }

        private object HandleScriptExecute(RequestMessage request)
        {
            var code = GetStringArgument(request, "code");
            var className = GetStringArgument(request, "className") ?? "Script";
            var methodName = GetStringArgument(request, "methodName") ?? "Main";
            var scriptArgs = GetStringArrayArgument(request, "scriptArgs");

            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException("C# code is required");
            }

            DebugLog($"[UnityCtl] Executing script: class={className}, method={methodName}, args={scriptArgs?.Length ?? 0}");

            var result = Editor.ScriptExecutor.Execute(code, className, methodName, scriptArgs);

            if (result.Success)
            {
                DebugLog($"[UnityCtl] Script executed successfully");
            }
            else
            {
                DebugLogError($"[UnityCtl] Script execution failed: {result.Error}");
            }

            return new Protocol.ScriptExecuteResult
            {
                Success = result.Success,
                Result = result.Result,
                Error = result.Error,
                Diagnostics = result.Diagnostics
            };
        }

        private object HandleRecordStart(RequestMessage request)
        {
            var outputName = GetStringArgument(request, "outputName");
            var fps = GetIntArgument(request, "fps") ?? 30;
            var width = GetIntArgument(request, "width");
            var height = GetIntArgument(request, "height");

            double? duration = null;
            if (request.Args is System.Collections.IDictionary dict && dict.Contains("duration"))
            {
                var durationVal = dict["duration"];
                if (durationVal != null)
                {
                    if (durationVal is Newtonsoft.Json.Linq.JToken jt)
                        duration = jt.Value<double>();
                    else if (durationVal is double d)
                        duration = d;
                    else if (double.TryParse(durationVal.ToString(), out var parsed))
                        duration = parsed;
                }
            }

            return Editor.RecordingManager.Instance.Start(outputName, duration, width, height, fps, payload =>
            {
                SendRecordFinishedEvent(payload);
            });
        }

        private object HandleRecordStop(RequestMessage request)
        {
            return Editor.RecordingManager.Instance.Stop();
        }

        public void SendRecordFinishedEvent(RecordFinishedPayload payload)
        {
            if (!IsConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.RecordFinished,
                Payload = payload
            };

            _ = SendMessageAsync(eventMessage);
        }

        private void CollectMatchingTests(UnityEditor.TestTools.TestRunner.Api.ITestAdaptor test, string filter, System.Collections.Generic.List<string> results)
        {
            if (test == null) return;

            // Check if this is a leaf test (actual test method, not a container)
            if (!test.HasChildren)
            {
                // Match against full name using case-insensitive contains
                if (test.FullName.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(test.FullName);
                    DebugLog($"[UnityCtl] Matched test: {test.FullName}");
                }
            }
            else
            {
                // Recursively check children
                if (test.Children != null)
                {
                    foreach (var child in test.Children)
                    {
                        CollectMatchingTests(child, filter, results);
                    }
                }
            }
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
            if (!IsConnected) return;

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
            if (!IsConnected)
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
            if (!IsConnected)
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
            if (!IsConnected) return;

            var state = stateChange switch
            {
                PlayModeStateChange.EnteredPlayMode => "EnteredPlayMode",
                PlayModeStateChange.ExitingPlayMode => "ExitingPlayMode",
                PlayModeStateChange.EnteredEditMode => "EnteredEditMode",
                PlayModeStateChange.ExitingEditMode => "ExitingEditMode",
                _ => stateChange.ToString()
            };

            // Check if compilation is triggered when entering edit mode (after exiting play mode with pending changes)
            var compilationTriggered = stateChange == PlayModeStateChange.EnteredEditMode && EditorApplication.isCompiling;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.PlayModeChanged,
                Payload = new PlayModeChangedPayload { State = state, CompilationTriggered = compilationTriggered }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendCompilationStartedEvent()
        {
            if (!IsConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.CompilationStarted,
                Payload = new { }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendCompilationFinishedEvent(bool success, System.Collections.Generic.List<UnityEditor.Compilation.CompilerMessage> messages = null)
        {
            if (!IsConnected) return;

            // Convert CompilerMessages to our DTO format
            CompilationMessageInfo[] errors = null;
            CompilationMessageInfo[] warnings = null;

            if (messages != null && messages.Count > 0)
            {
                var errorList = new System.Collections.Generic.List<CompilationMessageInfo>();
                var warningList = new System.Collections.Generic.List<CompilationMessageInfo>();

                foreach (var msg in messages)
                {
                    var info = new CompilationMessageInfo
                    {
                        File = msg.file,
                        Line = msg.line,
                        Column = msg.column,
                        Message = msg.message
                    };

                    if (msg.type == UnityEditor.Compilation.CompilerMessageType.Error)
                        errorList.Add(info);
                    else
                        warningList.Add(info);
                }

                if (errorList.Count > 0) errors = errorList.ToArray();
                if (warningList.Count > 0) warnings = warningList.ToArray();
            }

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.CompilationFinished,
                Payload = new CompilationFinishedPayload
                {
                    Success = success,
                    Errors = errors,
                    Warnings = warnings
                }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendDomainReloadStartingEvent()
        {
            if (!IsConnected) return;

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
            if (!IsConnected) return;

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
            if (!IsConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.AssetReimportAllComplete,
                Payload = new AssetReimportCompletePayload { Success = success }
            };

            _ = SendMessageAsync(eventMessage);
        }

        public void SendAssetRefreshCompleteEvent(bool compilationTriggered, bool hasCompilationErrors = false)
        {
            if (!IsConnected) return;

            var eventMessage = new EventMessage
            {
                Origin = MessageOrigin.Unity,
                Event = UnityCtlEvents.AssetRefreshComplete,
                Payload = new AssetRefreshCompletePayload
                {
                    CompilationTriggered = compilationTriggered,
                    HasCompilationErrors = hasCompilationErrors
                }
            };

            _ = SendMessageAsync(eventMessage);
        }

        private async Task SendMessageAsync(BaseMessage message)
        {
            ClientWebSocket socket;
            lock (_connectionLock)
            {
                if (!_isConnected || _webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    return;
                }
                socket = _webSocket;
            }

            try
            {
                var json = JsonHelper.Serialize(message);
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] Failed to send message: {ex.Message}");
                lock (_connectionLock)
                {
                    _isConnected = false;
                }
            }
        }
    }
}
