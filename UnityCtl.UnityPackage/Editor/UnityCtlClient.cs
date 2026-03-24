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
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
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
        private bool _connecting;

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

                // Skip if another connection attempt is already in-flight
                if (_connecting)
                {
                    DebugLog("[UnityCtl] Connection attempt already in progress, skipping");
                    return;
                }

                _connecting = true;

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
                // Timeout the connection attempt so it can't hang indefinitely
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(newCts.Token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                await newSocket.ConnectAsync(uri, connectCts.Token);

                lock (_connectionLock)
                {
                    _isConnected = true;
                    _connecting = false;
                }
                _reconnectAttempts = 0; // Reset reconnection counter on successful connection

                DebugLog($"[UnityCtl] ✓ Connected to bridge at port {port}");

                // Send hello message
                await SendHelloMessageAsync();

                // Flush any logs that were buffered before connection
                FlushLogBuffer();

                // Start receive loop (pass socket ref so finally block can check staleness)
                var socketRef = newSocket;
                _ = Task.Run(() => ReceiveLoopAsync(socketRef, newCts.Token));
            }
            catch (Exception ex)
            {
                DebugLogWarning($"[UnityCtl] ✗ Connection failed: {ex.Message}");
                lock (_connectionLock)
                {
                    // Only update state if we're still the current attempt
                    if (_webSocket == newSocket)
                    {
                        _isConnected = false;
                    }
                    _connecting = false;
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
                Capabilities = new[] { "console", "asset", "scene", "play", "compile", "menu", "test", "screenshot" },
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

        private async Task ReceiveLoopAsync(ClientWebSocket ownSocket, CancellationToken cancellationToken)
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
                    // Only clear connected state if we're still the active connection
                    if (_webSocket == ownSocket)
                    {
                        _isConnected = false;
                    }
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
                            State = EditorApplication.isPlaying
                                ? (EditorApplication.isPaused ? PlayModeState.Paused : PlayModeState.Playing)
                                : PlayModeState.Stopped,
                            PauseOnPlay = EditorApplication.isPaused && !EditorApplication.isPlaying
                        };
                        break;

                    case UnityCtlCommands.PlayPause:
                        EditorApplication.isPaused = !EditorApplication.isPaused;
                        result = new PlayModeResult
                        {
                            State = EditorApplication.isPlaying
                                ? (EditorApplication.isPaused ? PlayModeState.Paused : PlayModeState.Playing)
                                : PlayModeState.Stopped,
                            PauseOnPlay = EditorApplication.isPaused && !EditorApplication.isPlaying
                        };
                        break;

                    case UnityCtlCommands.PlayStep:
                        if (!EditorApplication.isPlaying)
                        {
                            SendResponseError(request.RequestId, "NOT_PLAYING",
                                "Cannot step: not in play mode");
                            return;
                        }
                        EditorApplication.Step();
                        result = new PlayModeResult { State = PlayModeState.Paused };
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

                    case UnityCtlCommands.ScreenshotListWindows:
                        result = HandleScreenshotListWindows(request);
                        break;

                    case UnityCtlCommands.ScreenshotWindow:
                        result = HandleScreenshotWindow(request);
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

                    case UnityCtlCommands.Snapshot:
                        result = HandleSnapshot(request);
                        break;

                    case UnityCtlCommands.SnapshotQuery:
                        result = HandleSnapshotQuery(request);
                        break;

                    case UnityCtlCommands.UIClick:
                        result = HandleUIClick(request);
                        break;

                    case UnityCtlCommands.PrefabOpen:
                        result = HandlePrefabOpen(request);
                        break;

                    case UnityCtlCommands.PrefabClose:
                        result = HandlePrefabClose(request);
                        break;

                    case UnityCtlCommands.EditorPing:
                        result = new { status = "pong" };
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
            else if (source == "loaded")
            {
                var count = UnityEngine.SceneManagement.SceneManager.sceneCount;
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var scenes = new SceneInfo[count];
                for (int i = 0; i < count; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    scenes[i] = new SceneInfo
                    {
                        Path = scene.path,
                        Name = scene.name,
                        IsActive = scene == activeScene
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

            if (EditorApplication.isPlaying)
            {
                var loadMode = mode == "additive" ? ", LoadSceneMode.Additive" : "";
                throw new InvalidOperationException(
                    $"scene load cannot be used during play mode. Use this instead: " +
                    $"unityctl script eval -u UnityEngine.SceneManagement " +
                    $"'SceneManager.LoadScene(\"{path}\"{loadMode}); return \"loaded\";'");
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

        private static double? GetDoubleArgument(RequestMessage request, string key)
        {
            if (request.Args == null) return null;

            try
            {
                if (request.Args is System.Collections.IDictionary dict && dict.Contains(key))
                {
                    var value = dict[key];
                    if (value == null) return null;

                    if (value is double d) return d;
                    if (value is float f) return f;
                    if (value is int i) return i;
                    if (value is long l) return l;

                    if (value is JToken jtoken)
                        return jtoken.Value<double>();

                    if (double.TryParse(value.ToString(), out var parsed))
                        return parsed;

                    DebugLog($"[UnityCtl] Could not parse argument '{key}' as double: {value}");
                }
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Failed to get double argument '{key}': {ex.Message}");
            }

            return null;
        }

        private static bool GetBoolArgument(RequestMessage request, string key)
        {
            if (request.Args == null) return false;

            try
            {
                if (request.Args is System.Collections.IDictionary dict && dict.Contains(key))
                {
                    var value = dict[key];
                    if (value == null) return false;

                    if (value is bool b) return b;

                    if (value is JToken jtoken && jtoken.Type == JTokenType.Boolean)
                        return jtoken.Value<bool>();

                    if (bool.TryParse(value.ToString(), out var parsed))
                        return parsed;
                }
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Failed to get bool argument '{key}': {ex.Message}");
            }

            return false;
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
            var filename = GetStringArgument(request, "filename");
            var width = GetIntArgument(request, "width");
            var height = GetIntArgument(request, "height");

            // Generate default filename with timestamp if not provided
            if (string.IsNullOrEmpty(filename))
            {
                var timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                filename = $"screenshot_{timestamp}.png";
            }

            // Ensure .png extension
            if (!filename.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) &&
                !filename.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase))
            {
                filename += ".png";
            }

            // Always capture to Screenshots/<filename> — CaptureScreenshot requires
            // project-relative paths (silently fails with absolute paths).
            var capturePath = $"Screenshots/{filename}";

            // Ensure Screenshots directory exists
            if (!System.IO.Directory.Exists("Screenshots"))
            {
                System.IO.Directory.CreateDirectory("Screenshots");
                DebugLog("[UnityCtl] Created Screenshots directory");
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

            // Capture screenshot using project-relative path
            if (width.HasValue && height.HasValue)
            {
                // Calculate supersize multiplier based on desired width
                int superSize = System.Math.Max(1, width.Value / actualWidth);
                UnityEngine.ScreenCapture.CaptureScreenshot(capturePath, superSize);
                actualWidth = width.Value;
                actualHeight = height.Value;
                DebugLog($"[UnityCtl] Capturing screenshot with supersize {superSize} to: {capturePath}");
            }
            else if (width.HasValue)
            {
                int superSize = System.Math.Max(1, width.Value / actualWidth);
                UnityEngine.ScreenCapture.CaptureScreenshot(capturePath, superSize);
                actualWidth = width.Value;
                actualHeight = actualHeight * superSize;
                DebugLog($"[UnityCtl] Capturing screenshot with width {width.Value} to: {capturePath}");
            }
            else if (height.HasValue)
            {
                int superSize = System.Math.Max(1, height.Value / actualHeight);
                UnityEngine.ScreenCapture.CaptureScreenshot(capturePath, superSize);
                actualWidth = actualWidth * superSize;
                actualHeight = height.Value;
                DebugLog($"[UnityCtl] Capturing screenshot with height {height.Value} to: {capturePath}");
            }
            else
            {
                UnityEngine.ScreenCapture.CaptureScreenshot(capturePath);
                DebugLog($"[UnityCtl] Capturing screenshot at game view resolution to: {capturePath}");
            }

            // Return project-relative capture path. CLI resolves against project root,
            // waits for the file, and moves it to the user's desired destination.
            return new ScreenshotCaptureResult
            {
                Path = capturePath,
                Width = actualWidth,
                Height = actualHeight
            };
        }

        private object HandleScreenshotListWindows(RequestMessage request)
        {
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var windowInfos = new System.Collections.Generic.List<EditorWindowInfo>();

            foreach (var window in allWindows)
            {
                if (window == null) continue;
                try
                {
                    var pos = window.position;
                    windowInfos.Add(new EditorWindowInfo
                    {
                        Type = window.GetType().FullName,
                        Title = window.titleContent?.text ?? "",
                        Width = (int)pos.width,
                        Height = (int)pos.height,
                        Docked = window.docked
                    });
                }
                catch (Exception ex)
                {
                    DebugLog($"[UnityCtl] Skipping window: {ex.Message}");
                }
            }

            return new ScreenshotListWindowsResult { Windows = windowInfos.ToArray() };
        }

        private object HandleScreenshotWindow(RequestMessage request)
        {
            var windowArg = GetStringArgument(request, "window");
            var filename = GetStringArgument(request, "filename");

            if (string.IsNullOrEmpty(windowArg))
            {
                throw new System.ArgumentException("Required argument 'window' not provided");
            }

            // Find the target window by type name or title
            var targetWindow = FindEditorWindow(windowArg);
            if (targetWindow == null)
            {
                throw new System.ArgumentException($"No open editor window matching '{windowArg}'. Use screenshot.listWindows to see available windows.");
            }

            // Generate default filename with timestamp if not provided
            if (string.IsNullOrEmpty(filename))
            {
                var timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var sanitizedType = targetWindow.GetType().Name;
                filename = $"window_{sanitizedType}_{timestamp}.png";
            }

            // Ensure .png extension
            if (!filename.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) &&
                !filename.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase))
            {
                filename += ".png";
            }

            var capturePath = $"Screenshots/{filename}";

            // Ensure Screenshots directory exists
            if (!System.IO.Directory.Exists("Screenshots"))
            {
                System.IO.Directory.CreateDirectory("Screenshots");
                DebugLog("[UnityCtl] Created Screenshots directory");
            }

            targetWindow.Focus();
            targetWindow.Repaint();

            var pos = targetWindow.position;
            int width = (int)pos.width;
            int height = (int)pos.height;

            if (!CaptureWindowViaGrabPixels(targetWindow, capturePath, width, height))
            {
                throw new System.InvalidOperationException($"Failed to capture window '{windowArg}'");
            }

            var windowType = targetWindow.GetType().FullName;
            var windowTitle = targetWindow.titleContent?.text ?? "";

            DebugLog($"[UnityCtl] Captured window '{windowType}' ({width}x{height}) to: {capturePath}");

            return new ScreenshotWindowResult
            {
                Path = capturePath,
                Width = width,
                Height = height,
                WindowType = windowType,
                WindowTitle = windowTitle
            };
        }

        private static EditorWindow FindEditorWindow(string identifier)
        {
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            // 1. Exact type name match (full name)
            foreach (var w in allWindows)
            {
                if (w != null && w.GetType().FullName == identifier)
                    return w;
            }

            // 2. Suffix match on type name (e.g. "MyWindow" matches "MyNamespace.MyWindow")
            foreach (var w in allWindows)
            {
                if (w == null) continue;
                var fullName = w.GetType().FullName;
                if (fullName != null && fullName.EndsWith("." + identifier, System.StringComparison.Ordinal))
                    return w;
            }

            // 3. Short type name match (no namespace)
            foreach (var w in allWindows)
            {
                if (w != null && w.GetType().Name == identifier)
                    return w;
            }

            // 4. Window title match (case-insensitive)
            foreach (var w in allWindows)
            {
                if (w != null && string.Equals(w.titleContent?.text, identifier, System.StringComparison.OrdinalIgnoreCase))
                    return w;
            }

            return null;
        }

        private static readonly System.Reflection.FieldInfo ParentViewField =
            typeof(EditorWindow).GetField("m_Parent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static readonly System.Type GuiViewType =
            typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");

        private static readonly System.Reflection.MethodInfo GrabPixelsMethod =
            GuiViewType?.GetMethod("GrabPixels",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static readonly System.Reflection.MethodInfo RepaintImmediatelyMethod =
            GuiViewType?.GetMethod("RepaintImmediately",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static bool CaptureWindowViaGrabPixels(EditorWindow window, string capturePath, int width, int height)
        {
            UnityEngine.RenderTexture rt = null;
            UnityEngine.Texture2D texture = null;
            try
            {
                if (ParentViewField == null || GrabPixelsMethod == null)
                {
                    DebugLogError("[UnityCtl] GrabPixels capture not available (missing internal API)");
                    return false;
                }

                var hostView = ParentViewField.GetValue(window);
                if (hostView == null)
                {
                    DebugLogError("[UnityCtl] Window has no parent HostView");
                    return false;
                }

                // Force synchronous repaint — call twice so IMGUI windows complete
                // both their layout and repaint passes
                RepaintImmediatelyMethod?.Invoke(hostView, null);
                RepaintImmediatelyMethod?.Invoke(hostView, null);

                rt = new UnityEngine.RenderTexture(width, height, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.Linear);
                rt.Create();

                GrabPixelsMethod.Invoke(hostView, new object[] { rt, new UnityEngine.Rect(0, 0, width, height) });

                UnityEngine.RenderTexture.active = rt;
                texture = new UnityEngine.Texture2D(width, height, UnityEngine.TextureFormat.RGBA32, false, true);
                texture.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                UnityEngine.RenderTexture.active = null;

                // Flip vertically — GrabPixels uses bottom-left origin
                var pixels = texture.GetPixels();
                var flipped = new UnityEngine.Color[pixels.Length];
                for (int y = 0; y < height; y++)
                {
                    System.Array.Copy(pixels, y * width, flipped, (height - 1 - y) * width, width);
                }
                texture.SetPixels(flipped);
                texture.Apply();

                byte[] bytes;
                if (capturePath.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase))
                    bytes = UnityEngine.ImageConversion.EncodeToJPG(texture);
                else
                    bytes = UnityEngine.ImageConversion.EncodeToPNG(texture);

                System.IO.File.WriteAllBytes(capturePath, bytes);
                return true;
            }
            catch (Exception ex)
            {
                DebugLogError($"[UnityCtl] Window capture failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }
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

        private object HandleSnapshot(RequestMessage request)
        {
            var depth = GetIntArgument(request, "depth") ?? 2;
            var targetId = GetIntArgument(request, "id");
            var includeComponents = GetBoolArgument(request, "components");
            var screen = GetBoolArgument(request, "screen");
            var filter = GetStringArgument(request, "filter");
            var scenePath = GetStringArgument(request, "scenePath");
            var prefabPath = GetStringArgument(request, "prefabPath");

            // Validate mutual exclusivity
            if (!string.IsNullOrEmpty(scenePath) && !string.IsNullOrEmpty(prefabPath))
                throw new ArgumentException("Cannot use both --scene and --prefab");

            Protocol.SnapshotResult result;

            if (!string.IsNullOrEmpty(prefabPath))
                result = SnapshotPrefabAsset(prefabPath, targetId, filter, depth, includeComponents, screen);
            else if (!string.IsNullOrEmpty(scenePath))
                result = SnapshotSpecificScene(scenePath, targetId, filter, depth, includeComponents, screen);
            else if (targetId.HasValue)
                result = SnapshotDrillDown(targetId.Value, filter, depth, includeComponents, screen);
            else
                result = SnapshotCurrentStage(filter, depth, includeComponents, screen);

            if (screen)
            {
                result.ScreenWidth = Screen.width;
                result.ScreenHeight = Screen.height;
            }

            return result;
        }

        private Protocol.SnapshotResult SnapshotPrefabAsset(string prefabPath, int? targetId, string filter, int depth, bool includeComponents, bool screen)
        {
            if (!prefabPath.EndsWith(".prefab"))
                throw new ArgumentException($"Not a prefab asset: {prefabPath} (must end with .prefab)");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new ArgumentException($"Prefab not found: {prefabPath}");

            if (targetId.HasValue)
            {
                var go = FindInHierarchy(prefab, targetId.Value);
                if (go == null)
                    throw new ArgumentException($"No GameObject with instance ID {targetId} in prefab {prefabPath}");
                prefab = go;
            }

            var prefabRoots = new[] { prefab };
            if (!string.IsNullOrEmpty(filter))
                prefabRoots = prefabRoots.Where(go => MatchesFilter(go, filter)).ToArray();

            return new Protocol.SnapshotResult
            {
                Stage = null,
                PrefabAssetPath = prefabPath,
                IsPlaying = EditorApplication.isPlaying,
                RootObjectCount = 1,
                Objects = prefabRoots.Select(go => SerializeGameObject(go, depth, includeComponents, screen)).ToArray()
            };
        }

        private Protocol.SnapshotResult SnapshotSpecificScene(string scenePath, int? targetId, string filter, int depth, bool includeComponents, bool screen)
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Cannot snapshot other scenes during play mode. Use snapshot without --scene for the active scene.");

            var scene = SceneManager.GetSceneByPath(scenePath);
            bool weLoaded = false;
            if (!scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                weLoaded = true;
            }

            try
            {
                if (targetId.HasValue)
                {
                    var go = EditorUtility.InstanceIDToObject(targetId.Value) as GameObject;
                    if (go == null || go.scene != scene)
                        throw new ArgumentException($"No GameObject with instance ID {targetId} in scene {scenePath}");

                    var sceneIdRoots = new[] { go };
                    if (!string.IsNullOrEmpty(filter))
                        sceneIdRoots = sceneIdRoots.Where(g => MatchesFilter(g, filter)).ToArray();

                    return new Protocol.SnapshotResult
                    {
                        Stage = "scene (editing)",
                        SceneName = scene.name,
                        ScenePath = scene.path,
                        IsPlaying = false,
                        RootObjectCount = 1,
                        Objects = sceneIdRoots.Select(g => SerializeGameObject(g, depth, includeComponents, screen)).ToArray()
                    };
                }

                var roots = scene.GetRootGameObjects();
                var filteredRoots = string.IsNullOrEmpty(filter)
                    ? roots
                    : roots.Where(go => MatchesFilter(go, filter)).ToArray();

                return new Protocol.SnapshotResult
                {
                    Stage = "scene (editing)",
                    SceneName = scene.name,
                    ScenePath = scene.path,
                    IsPlaying = false,
                    RootObjectCount = roots.Length,
                    Objects = filteredRoots
                        .Select(go => SerializeGameObject(go, depth, includeComponents, screen))
                        .ToArray()
                };
            }
            finally
            {
                if (weLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        private Protocol.SnapshotResult SnapshotDrillDown(int targetId, string filter, int depth, bool includeComponents, bool screen)
        {
            var go = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            if (go == null)
                throw new ArgumentException($"No GameObject with instance ID {targetId}");

            var matches = string.IsNullOrEmpty(filter) || MatchesFilter(go, filter);
            var stageInfo = GetStageInfo();
            var goScene = go.scene;
            return new Protocol.SnapshotResult
            {
                Stage = stageInfo.Stage,
                SceneName = goScene.IsValid() ? goScene.name : null,
                ScenePath = goScene.IsValid() ? goScene.path : null,
                PrefabAssetPath = stageInfo.PrefabAssetPath,
                HasUnsavedChanges = stageInfo.HasUnsavedChanges,
                OpenedFromInstanceId = stageInfo.OpenedFromInstanceId,
                IsPlaying = EditorApplication.isPlaying,
                RootObjectCount = 1,
                Objects = matches
                    ? new[] { SerializeGameObject(go, depth, includeComponents, screen) }
                    : Array.Empty<Protocol.SnapshotObject>()
            };
        }

        private Protocol.SnapshotResult SnapshotCurrentStage(string filter, int depth, bool includeComponents, bool screen)
        {
            var currentStage = GetStageInfo();

            if (currentStage.PrefabAssetPath != null)
            {
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                var root = prefabStage.prefabContentsRoot;

                var prefabRoots = new[] { root };
                if (!string.IsNullOrEmpty(filter))
                    prefabRoots = prefabRoots.Where(go => MatchesFilter(go, filter)).ToArray();

                return new Protocol.SnapshotResult
                {
                    Stage = currentStage.Stage,
                    PrefabAssetPath = currentStage.PrefabAssetPath,
                    HasUnsavedChanges = currentStage.HasUnsavedChanges,
                    OpenedFromInstanceId = currentStage.OpenedFromInstanceId,
                    IsPlaying = false,
                    RootObjectCount = 1,
                    Objects = prefabRoots.Select(go => SerializeGameObject(go, depth, includeComponents, screen)).ToArray()
                };
            }

            // Normal scene mode — snapshot all loaded scenes
            var activeScene = SceneManager.GetActiveScene();
            var isPlaying = EditorApplication.isPlaying;
            var sceneCount = SceneManager.sceneCount;

            var allScenes = new List<Protocol.SnapshotSceneInfo>();
            var allObjects = new List<Protocol.SnapshotObject>();
            var totalRootCount = 0;

            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                var roots = scene.GetRootGameObjects();
                var filteredRoots = string.IsNullOrEmpty(filter)
                    ? roots
                    : roots.Where(go => MatchesFilter(go, filter)).ToArray();

                var serialized = filteredRoots
                    .Select(go => SerializeGameObject(go, depth, includeComponents, screen))
                    .ToArray();

                allScenes.Add(new Protocol.SnapshotSceneInfo
                {
                    SceneName = scene.name,
                    ScenePath = scene.path,
                    IsActive = scene == activeScene,
                    RootObjectCount = roots.Length,
                    Objects = serialized
                });

                allObjects.AddRange(serialized);
                totalRootCount += roots.Length;
            }

            return new Protocol.SnapshotResult
            {
                Stage = currentStage.Stage,
                SceneName = activeScene.name,
                ScenePath = activeScene.path,
                IsPlaying = isPlaying,
                RootObjectCount = totalRootCount,
                Objects = allObjects.ToArray(),
                Scenes = allScenes.Count > 1 ? allScenes.ToArray() : null
            };
        }

        private static GameObject FindInHierarchy(GameObject root, int instanceId)
        {
            if (root.GetInstanceID() == instanceId) return root;
            foreach (Transform child in root.transform)
            {
                var found = FindInHierarchy(child.gameObject, instanceId);
                if (found != null) return found;
            }
            return null;
        }

        private struct StageInfo
        {
            public string Stage;
            public string PrefabAssetPath;
            public bool? HasUnsavedChanges;
            public int? OpenedFromInstanceId;
        }

        private StageInfo GetStageInfo()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                var isInContext = prefabStage.mode == PrefabStage.Mode.InContext;
                int? openedFrom = null;
                if (isInContext)
                {
                    var instanceRoot = prefabStage.openedFromInstanceRoot;
                    if (instanceRoot != null)
                        openedFrom = instanceRoot.GetInstanceID();
                }

                return new StageInfo
                {
                    Stage = isInContext ? "prefab (in-context)" : "prefab (isolated)",
                    PrefabAssetPath = prefabStage.assetPath,
                    HasUnsavedChanges = prefabStage.scene.isDirty,
                    OpenedFromInstanceId = openedFrom
                };
            }

            return new StageInfo
            {
                Stage = EditorApplication.isPlaying ? "scene (playing)" : "scene (editing)",
                PrefabAssetPath = null,
                HasUnsavedChanges = null,
                OpenedFromInstanceId = null
            };
        }

        private object HandlePrefabOpen(RequestMessage request)
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Cannot open prefab stage during play mode");

            var path = GetStringArgument(request, "path");
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Prefab path is required");
            if (!path.EndsWith(".prefab"))
                throw new ArgumentException($"Not a prefab file: {path}");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
                throw new ArgumentException($"Prefab not found: {path}");

            var contextInstanceId = GetIntArgument(request, "context");

            if (contextInstanceId.HasValue)
            {
                var instance = EditorUtility.InstanceIDToObject(contextInstanceId.Value) as GameObject;
                if (instance == null)
                    throw new ArgumentException($"Context instance not found: {contextInstanceId.Value}");
                if (!PrefabUtility.IsPartOfPrefabInstance(instance))
                    throw new ArgumentException($"Object {contextInstanceId.Value} is not a prefab instance");

                PrefabStageUtility.OpenPrefab(path, instance);
            }
            else
            {
                PrefabStageUtility.OpenPrefab(path);
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var stageType = (stage != null && stage.mode == PrefabStage.Mode.InContext)
                ? "prefab (in-context)" : "prefab (isolated)";

            return new Protocol.PrefabOpenResult
            {
                PrefabAssetPath = path,
                Stage = stageType
            };
        }

        private object HandlePrefabClose(RequestMessage request)
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Cannot close prefab stage during play mode");

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null)
                throw new InvalidOperationException("Not currently in prefab editing mode");

            var save = GetBoolArgument(request, "save");
            var discard = GetBoolArgument(request, "discard");
            var saved = false;

            if (save)
            {
                // Save all prefab stage changes to the asset on disk.
                // We use SaveAsPrefabAsset (not EditorSceneManager.SaveScene) because
                // SaveScene can trigger rename dialogs if the root object was renamed.
                // ClearDirtiness() prevents the "save changes?" dialog on close and also
                // prevents auto-save from re-saving after GoToMainStage.
                PrefabUtility.SaveAsPrefabAsset(prefabStage.prefabContentsRoot, prefabStage.assetPath);
                prefabStage.ClearDirtiness();
                saved = true;
            }
            else if (discard)
            {
                // Clear the dirty flag so Unity doesn't prompt to save.
                // This also prevents auto-save from persisting changes on close.
                prefabStage.ClearDirtiness();
            }

            StageUtility.GoToMainStage();

            var scene = SceneManager.GetActiveScene();
            return new Protocol.PrefabCloseResult { ReturnedToScene = scene.name, Saved = saved };
        }

        private static Protocol.SnapshotObject SerializeGameObject(
            GameObject go, int depth, bool includeComponents, bool screen)
        {
            var t = go.transform;
            var obj = new Protocol.SnapshotObject
            {
                InstanceId = go.GetInstanceID(),
                Name = go.name,
                Active = go.activeSelf,
                Tag = go.tag != "Untagged" ? go.tag : null,
                Layer = go.layer != 0 ? LayerMask.LayerToName(go.layer) : null,
                Components = go.GetComponents<Component>()
                    .Where(c => c != null && !(c is Transform))
                    .Select(c => includeComponents
                        ? SerializeComponentFull(c)
                        : new Protocol.SnapshotComponent { TypeName = c.GetType().Name })
                    .ToArray(),
                ChildCount = t.childCount,
            };

            // Prefab instance annotations
            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                obj.PrefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                obj.IsPrefabInstanceRoot = PrefabUtility.IsAnyPrefabInstanceRoot(go);
                var prefabType = PrefabUtility.GetPrefabAssetType(go);
                if (prefabType == PrefabAssetType.Regular)
                    obj.PrefabAssetType = "Regular";
                else if (prefabType == PrefabAssetType.Variant)
                    obj.PrefabAssetType = "Variant";
                else if (prefabType == PrefabAssetType.Model)
                    obj.PrefabAssetType = "Model";
            }

            // Auto-detect UI vs 3D: RectTransform → show rect layout; otherwise → world position
            if (t is RectTransform rt)
            {
                obj.Rect = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "rect({0:F0}, {1:F0}, {2:F0}, {3:F0})", rt.rect.x, rt.rect.y, rt.rect.width, rt.rect.height);
                obj.Anchors = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "anchor({0:F1}-{1:F1}, {2:F1}-{3:F1})", rt.anchorMin.x, rt.anchorMax.x, rt.anchorMin.y, rt.anchorMax.y);
                obj.Pivot = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "({0:F1}, {1:F1})", rt.pivot.x, rt.pivot.y);
            }
            else
            {
                obj.Position = FormatVector3(t.localPosition);
                if (t.localScale != Vector3.one)
                    obj.Scale = FormatVector3(t.localScale);
                if (t.localRotation != Quaternion.identity)
                    obj.Rotation = FormatQuaternion(t.localRotation);
            }

            // Always emit UI text and interactable state (no flag needed)
            obj.Text = GetUIText(go);
            obj.Interactable = GetInteractable(go);

            // Screen-space info
            if (screen)
            {
                ComputeScreenSpaceInfo(go, obj);
            }

            if (depth > 0 && t.childCount > 0)
            {
                obj.Children = new Protocol.SnapshotObject[t.childCount];
                for (int i = 0; i < t.childCount; i++)
                    obj.Children[i] = SerializeGameObject(
                        t.GetChild(i).gameObject, depth - 1, includeComponents, screen);
            }

            return obj;
        }

        private static Protocol.SnapshotComponent SerializeComponentFull(Component c)
        {
            var comp = new Protocol.SnapshotComponent
            {
                TypeName = c.GetType().Name,
                Properties = new Dictionary<string, object>()
            };

            try
            {
                var so = new SerializedObject(c);
                var prop = so.GetIterator();
                bool enterChildren = true;

                while (prop.NextVisible(enterChildren))
                {
                    enterChildren = false;

                    // Skip the script reference itself
                    if (prop.name == "m_Script") continue;

                    var value = ReadSerializedProperty(prop);
                    if (value != null)
                        comp.Properties[prop.displayName] = value;
                }

                so.Dispose();
            }
            catch (Exception)
            {
                // Some components may not be serializable
            }

            return comp;
        }

        private static object ReadSerializedProperty(SerializedProperty prop, int maxDepth = 4)
        {
            // Leaf types
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue;
                case SerializedPropertyType.Boolean: return prop.boolValue;
                case SerializedPropertyType.Float: return prop.floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Enum:
                    return prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return FormatVector2(prop.vector2Value);
                case SerializedPropertyType.Vector3: return FormatVector3(prop.vector3Value);
                case SerializedPropertyType.Vector4: return FormatVector4(prop.vector4Value);
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F2}, {1:F2}, {2:F2}, {3:F2})", c.r, c.g, c.b, c.a);
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? $"\"{prop.objectReferenceValue.name}\"" : "null";
                case SerializedPropertyType.Quaternion: return FormatQuaternion(prop.quaternionValue);
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F1}, {1:F1}, {2:F1}, {3:F1})", r.x, r.y, r.width, r.height);
                case SerializedPropertyType.Bounds: return prop.boundsValue.ToString();
                case SerializedPropertyType.LayerMask: return prop.intValue;
                case SerializedPropertyType.Hash128: return prop.hash128Value.ToString();
                case SerializedPropertyType.AnimationCurve:
                    var curve = prop.animationCurveValue;
                    return curve != null ? $"AnimationCurve({curve.length} keys)" : "null";
            }

            // Arrays / Lists
            if (prop.isArray && prop.propertyType != SerializedPropertyType.String)
            {
                var count = prop.arraySize;
                if (count == 0) return "[]";
                // Cap to avoid huge output
                var cap = System.Math.Min(count, 20);
                var items = new List<object>(cap);
                for (int i = 0; i < cap; i++)
                {
                    var elem = prop.GetArrayElementAtIndex(i);
                    var val = ReadSerializedProperty(elem, maxDepth - 1);
                    items.Add(val ?? "?");
                }
                if (count > cap) items.Add($"... +{count - cap} more");
                return items;
            }

            // Compound types (nested serializable structs/classes, ManagedReference)
            if (maxDepth > 0 && prop.hasVisibleChildren)
            {
                var dict = new Dictionary<string, object>();
                var child = prop.Copy();
                var end = prop.Copy();
                bool endIsValid = end.Next(false); // move end past this property's subtree

                if (child.Next(true)) // enter children
                {
                    do
                    {
                        if (endIsValid && SerializedProperty.EqualContents(child, end)) break;
                        var val = ReadSerializedProperty(child, maxDepth - 1);
                        if (val != null)
                            dict[child.displayName] = val;
                    }
                    while (child.Next(false)); // siblings only
                }

                return dict.Count > 0 ? dict : null;
            }

            return null;
        }

        private static bool MatchesFilter(GameObject go, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;

            var parts = filter.Split(':');
            if (parts.Length != 2) return true;

            var filterType = parts[0].Trim().ToLowerInvariant();
            var filterValue = parts[1].Trim();

            switch (filterType)
            {
                case "type":
                    return go.GetComponents<Component>().Any(c => c != null && c.GetType().Name == filterValue)
                        || CheckChildrenForType(go.transform, filterValue);
                case "name":
                    var nameMode = GetNameMatchMode(filterValue, out var namePattern);
                    return MatchesName(go.name, namePattern, nameMode)
                        || CheckChildrenForName(go.transform, namePattern, nameMode);
                case "tag":
                    return go.CompareTag(filterValue)
                        || CheckChildrenForTag(go.transform, filterValue);
                default:
                    return true;
            }
        }

        private static bool CheckChildrenForType(Transform parent, string typeName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.GetComponents<Component>().Any(c => c != null && c.GetType().Name == typeName))
                    return true;
                if (CheckChildrenForType(child, typeName))
                    return true;
            }
            return false;
        }

        private static bool CheckChildrenForTag(Transform parent, string tag)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.CompareTag(tag))
                    return true;
                if (CheckChildrenForTag(child, tag))
                    return true;
            }
            return false;
        }

        private enum NameMatchMode { Exact, Prefix, Suffix, Contains }

        private static NameMatchMode GetNameMatchMode(string filterValue, out string pattern)
        {
            bool startWild = filterValue.StartsWith("*");
            bool endWild = filterValue.EndsWith("*");
            if (startWild && endWild)
            {
                pattern = filterValue.Trim('*');
                return NameMatchMode.Contains;
            }
            if (endWild)
            {
                pattern = filterValue.TrimEnd('*');
                return NameMatchMode.Prefix;
            }
            if (startWild)
            {
                pattern = filterValue.TrimStart('*');
                return NameMatchMode.Suffix;
            }
            pattern = filterValue;
            return NameMatchMode.Exact;
        }

        private static bool MatchesName(string name, string pattern, NameMatchMode mode)
        {
            switch (mode)
            {
                case NameMatchMode.Contains:
                    return name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                case NameMatchMode.Prefix:
                    return name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
                case NameMatchMode.Suffix:
                    return name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
                default:
                    return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool CheckChildrenForName(Transform parent, string pattern, NameMatchMode mode)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (MatchesName(child.name, pattern, mode))
                    return true;
                if (CheckChildrenForName(child, pattern, mode))
                    return true;
            }
            return false;
        }

        private static string GetUIText(GameObject go)
        {
            // Try UnityEngine.UI.Text
            var textComponent = go.GetComponent("Text");
            if (textComponent != null)
            {
                var textProp = textComponent.GetType().GetProperty("text");
                if (textProp != null)
                    return textProp.GetValue(textComponent) as string;
            }

            // Try TextMeshPro (TMP_Text is the base class for both TextMeshProUGUI and TextMeshPro)
            var tmpComponent = go.GetComponent("TextMeshProUGUI") ?? go.GetComponent("TextMeshPro");
            if (tmpComponent != null)
            {
                var textProp = tmpComponent.GetType().GetProperty("text");
                if (textProp != null)
                    return textProp.GetValue(tmpComponent) as string;
            }

            return null;
        }

        private static bool? GetInteractable(GameObject go)
        {
            // Check for Selectable (Button, Toggle, Slider, etc.)
            var selectable = go.GetComponent<Selectable>();
            if (selectable != null)
                return selectable.interactable;

            // Check for IPointerClickHandler/IPointerDownHandler (catches custom interactive elements)
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var type = c.GetType();
                if (typeof(IPointerClickHandler).IsAssignableFrom(type) ||
                    typeof(IPointerDownHandler).IsAssignableFrom(type))
                    return true;
            }

            return null;
        }

        /// <summary>
        /// Projects a RectTransform's world corners to screen space and returns the bounding box.
        /// Returns null if the RectTransform has no parent Canvas.
        /// </summary>
        private static (float minX, float minY, float maxX, float maxY)? GetScreenBounds(RectTransform rt)
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return null;

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);

            Camera canvasCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.worldCamera ?? Camera.main;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < 4; i++)
            {
                Vector2 sp = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? (Vector2)corners[i]
                    : RectTransformUtility.WorldToScreenPoint(canvasCam, corners[i]);
                if (sp.x < minX) minX = sp.x;
                if (sp.y < minY) minY = sp.y;
                if (sp.x > maxX) maxX = sp.x;
                if (sp.y > maxY) maxY = sp.y;
            }

            return (minX, minY, maxX, maxY);
        }

        private static void ComputeScreenSpaceInfo(GameObject go, Protocol.SnapshotObject obj)
        {
            if (go.transform is not RectTransform rt) return;

            var bounds = GetScreenBounds(rt);
            if (bounds == null) return;
            var (minX, minY, maxX, maxY) = bounds.Value;

            var w = maxX - minX;
            var h = maxY - minY;
            obj.ScreenRect = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "screen({0:F0}, {1:F0}, {2:F0}, {3:F0})", minX, minY, w, h);

            // Visible if on screen
            obj.Visible = minX < Screen.width && maxX > 0 && minY < Screen.height && maxY > 0;

            // Hittability: play mode only (EventSystem gives ground truth; edit-mode approximation is unreliable)
            if (obj.Visible == true && obj.Interactable != null && EditorApplication.isPlaying)
            {
                var centerX = (minX + maxX) / 2f;
                var centerY = (minY + maxY) / 2f;
                var hitId = GetUIHitAtPoint(new Vector2(centerX, centerY));
                if (hitId == go.GetInstanceID() || IsDescendantOf(hitId, go))
                {
                    obj.Hittable = true;
                }
                else if (hitId != 0)
                {
                    obj.Hittable = false;
                    obj.BlockedBy = hitId;
                }
                else
                {
                    obj.Hittable = false;
                }
            }
        }

        /// <summary>
        /// Returns the instance ID of the top UI element at the given screen point, or 0 if nothing.
        /// Requires play mode with an active EventSystem.
        /// </summary>
        private static int GetUIHitAtPoint(Vector2 screenPoint)
        {
            if (EventSystem.current == null) return 0;

            var pointerData = new PointerEventData(EventSystem.current) { position = screenPoint };
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointerData, results);
            return results.Count > 0 ? results[0].gameObject.GetInstanceID() : 0;
        }

        /// <summary>
        /// Computes a pre-order traversal index for a transform within a Canvas.
        /// Unity Canvas renders children depth-first in sibling order — later in pre-order = renders on top.
        /// Used by the edit-mode snapshot query path.
        /// </summary>
        private static int GetCanvasRenderOrder(Transform t, Transform canvasRoot)
        {
            int index = 0;
            if (PreOrderFind(canvasRoot, t, ref index))
                return index;
            return -1;
        }

        private static bool PreOrderFind(Transform current, Transform target, ref int index)
        {
            if (current == target) return true;
            for (int i = 0; i < current.childCount; i++)
            {
                index++;
                if (PreOrderFind(current.GetChild(i), target, ref index))
                    return true;
            }
            return false;
        }

        private object HandleSnapshotQuery(RequestMessage request)
        {
            var x = GetIntArgument(request, "x") ?? throw new ArgumentException("x coordinate is required");
            var y = GetIntArgument(request, "y") ?? throw new ArgumentException("y coordinate is required");

            var screenPoint = new Vector2(x, y);
            var uiHits = new List<Protocol.SnapshotQueryHit>();

            // UI raycast
            if (EditorApplication.isPlaying && EventSystem.current != null)
            {
                var pointerData = new PointerEventData(EventSystem.current) { position = screenPoint };
                var results = new List<RaycastResult>();
                EventSystem.current.RaycastAll(pointerData, results);
                foreach (var r in results)
                {
                    uiHits.Add(new Protocol.SnapshotQueryHit
                    {
                        InstanceId = r.gameObject.GetInstanceID(),
                        Name = r.gameObject.name,
                        Path = GetHierarchyPath(r.gameObject),
                        Text = GetUIText(r.gameObject),
                        Interactable = GetInteractable(r.gameObject)
                    });
                }
            }
            else
            {
                // Edit mode: manually check all raycast-target Graphics, sorted across canvases
                // Only iterate root canvases to avoid duplicates from nested sub-canvases
                var allHits = new List<(Graphic g, int canvasSortOrder, int renderOrder)>();

                foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                {
                    if (canvas.rootCanvas != canvas) continue;

                    Camera canvasCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    var graphics = canvas.GetComponentsInChildren<Graphic>();

                    foreach (var g in graphics)
                    {
                        if (!g.raycastTarget || !g.gameObject.activeInHierarchy) continue;
                        if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, screenPoint, canvasCam)) continue;
                        allHits.Add((g, canvas.sortingOrder, GetCanvasRenderOrder(g.transform, canvas.transform)));
                    }
                }

                // Sort by canvas sorting order descending, then render order descending (top-most first)
                allHits.Sort((a, b) =>
                {
                    int c = b.canvasSortOrder.CompareTo(a.canvasSortOrder);
                    return c != 0 ? c : b.renderOrder.CompareTo(a.renderOrder);
                });

                foreach (var (g, _, _) in allHits)
                {
                    uiHits.Add(new Protocol.SnapshotQueryHit
                    {
                        InstanceId = g.gameObject.GetInstanceID(),
                        Name = g.gameObject.name,
                        Path = GetHierarchyPath(g.gameObject),
                        Text = GetUIText(g.gameObject),
                        Interactable = GetInteractable(g.gameObject)
                    });
                }
            }

            return new Protocol.SnapshotQueryResult
            {
                X = x,
                Y = y,
                Mode = EditorApplication.isPlaying ? "play" : "edit-approximate",
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height,
                UiHits = uiHits.Count > 0 ? uiHits.ToArray() : null
            };
        }

        private object HandleUIClick(RequestMessage request)
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("ui.click requires play mode");
            if (EventSystem.current == null)
                throw new InvalidOperationException("No EventSystem found in scene");

            var targetId = GetIntArgument(request, "id");
            var x = GetIntArgument(request, "x");
            var y = GetIntArgument(request, "y");

            GameObject target;
            Vector2 screenPoint;

            if (targetId.HasValue)
            {
                // Resolve by instance ID
                target = EditorUtility.InstanceIDToObject(targetId.Value) as GameObject;
                if (target == null)
                    throw new ArgumentException($"No GameObject with instance ID {targetId.Value}");

                var rt = target.GetComponent<RectTransform>();
                if (rt == null)
                    throw new ArgumentException($"'{target.name}' has no RectTransform — ui.click only works with UI elements");

                // Compute screen-space center
                screenPoint = GetRectScreenCenter(rt);

                // Check hittability: is this element the top hit at its center?
                var pointerData = new PointerEventData(EventSystem.current) { position = screenPoint };
                var results = new List<RaycastResult>();
                EventSystem.current.RaycastAll(pointerData, results);

                if (results.Count > 0)
                {
                    var topHit = results[0].gameObject;
                    if (topHit != target && !topHit.transform.IsChildOf(target.transform))
                    {
                        throw new InvalidOperationException(
                            $"'{target.name}' [i:{targetId.Value}] is blocked by '{topHit.name}' [i:{topHit.GetInstanceID()}]");
                    }
                }
            }
            else if (x.HasValue && y.HasValue)
            {
                // Resolve by screen coordinates
                screenPoint = new Vector2(x.Value, y.Value);
                var pointerData = new PointerEventData(EventSystem.current) { position = screenPoint };
                var results = new List<RaycastResult>();
                EventSystem.current.RaycastAll(pointerData, results);

                if (results.Count == 0)
                    throw new ArgumentException($"No UI element at ({x.Value}, {y.Value})");

                target = results[0].gameObject;

                // Walk up to nearest interactable ancestor (e.g., Text child inside Button)
                var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
                if (handler != null)
                    target = handler;
            }
            else
            {
                throw new ArgumentException("Provide either 'id' or 'x'+'y' coordinates");
            }

            // Check interactable (reject both null=no handler and false=disabled)
            if (GetInteractable(target) != true)
                throw new ArgumentException($"'{target.name}' is not interactable");


            // Dispatch full pointer event sequence
            var eventData = new PointerEventData(EventSystem.current)
            {
                position = screenPoint,
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                pointerPress = target
            };

            ExecuteEvents.Execute(target, eventData, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(target, eventData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, eventData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(target, eventData, ExecuteEvents.pointerClickHandler);
            ExecuteEvents.Execute(target, eventData, ExecuteEvents.pointerExitHandler);

            return new Protocol.UIClickResult
            {
                InstanceId = target.GetInstanceID(),
                Name = target.name,
                Path = GetHierarchyPath(target),
                ScreenPosition = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "({0:F0}, {1:F0})", screenPoint.x, screenPoint.y),
                Text = GetUIText(target)
            };
        }

        /// <summary>
        /// Returns the screen-space center of a RectTransform.
        /// </summary>
        private static Vector2 GetRectScreenCenter(RectTransform rt)
        {
            var bounds = GetScreenBounds(rt);
            if (bounds == null) return Vector2.zero;
            var (minX, minY, maxX, maxY) = bounds.Value;
            return new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
        }

        /// <summary>
        /// Returns true if the object with the given instance ID is a descendant of parent.
        /// </summary>
        private static bool IsDescendantOf(int instanceId, GameObject parent)
        {
            var obj = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (obj == null) return false;
            var t = obj.transform;
            while (t != null)
            {
                if (t.gameObject == parent) return true;
                t = t.parent;
            }
            return false;
        }

        private static string GetHierarchyPath(GameObject go)
        {
            var parts = new List<string>();
            var current = go.transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string FormatVector2(Vector2 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F1}, {1:F1})", v.x, v.y);
        private static string FormatVector3(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F1}, {1:F1}, {2:F1})", v.x, v.y, v.z);
        private static string FormatVector4(Vector4 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F1}, {1:F1}, {2:F1}, {3:F1})", v.x, v.y, v.z, v.w);
        private static string FormatQuaternion(Quaternion q)
        {
            var euler = q.eulerAngles;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F1}, {1:F1}, {2:F1})", euler.x, euler.y, euler.z);
        }

        private object HandleRecordStart(RequestMessage request)
        {
            var outputName = GetStringArgument(request, "outputName");
            var fps = GetIntArgument(request, "fps") ?? 30;
            var width = GetIntArgument(request, "width");
            var height = GetIntArgument(request, "height");
            var duration = GetDoubleArgument(request, "duration");
            var frames = GetIntArgument(request, "frames");

            return Editor.RecordingManager.Instance.Start(outputName, duration, frames, width, height, fps, payload =>
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
