using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;

namespace UnityCtl.Bridge;

public static class BridgeEndpoints
{
    // Internal timeout constants (not user-configurable)
    private static readonly TimeSpan StatusCheckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortEventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompilationWaitTimeout = TimeSpan.FromSeconds(2);

    // Command timeout configuration (in seconds, configurable via environment variables)
    private static readonly Dictionary<string, CommandConfig> CommandConfigs = new()
    {
        [UnityCtlCommands.PlayEnter] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_PLAYMODE", 30),
            CompletionEvent = UnityCtlEvents.PlayModeChanged,
            CompletionEventState = "EnteredPlayMode"
        },
        [UnityCtlCommands.PlayExit] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_PLAYMODE", 30),
            CompletionEvent = UnityCtlEvents.PlayModeChanged
            // Note: Don't filter by state for play exit - domain reload can cause event loss
        },
        [UnityCtlCommands.AssetImport] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_ASSET", 30),
            CompletionEvent = UnityCtlEvents.AssetImportComplete
        },
        [UnityCtlCommands.AssetReimportAll] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_ASSET", 30),
            CompletionEvent = UnityCtlEvents.AssetReimportAllComplete
        },
        [UnityCtlCommands.AssetRefresh] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_REFRESH", 60),
            CompletionEvent = UnityCtlEvents.AssetRefreshComplete
        },
        [UnityCtlCommands.TestRun] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_TEST", 300),
            CompletionEvent = UnityCtlEvents.TestFinished
        }
    };

    private static TimeSpan GetTimeoutFromEnv(string envVar, int defaultSeconds)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (int.TryParse(value, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return TimeSpan.FromSeconds(defaultSeconds);
    }

    private static TimeSpan GetDefaultTimeout()
    {
        return GetTimeoutFromEnv("UNITYCTL_TIMEOUT_DEFAULT", 30);
    }

    public static void MapEndpoints(WebApplication app)
    {
        var state = app.Services.GetRequiredService<BridgeState>();

        // Health endpoint
        app.MapGet("/health", () =>
        {
            return new HealthResult
            {
                Status = "ok",
                ProjectId = state.ProjectId,
                UnityConnected = state.IsUnityConnected
            };
        });

        // Unified logs tail endpoint
        app.MapGet("/logs/tail", ([FromQuery] int lines = 50, [FromQuery] string source = "editor", [FromQuery] bool full = false) =>
        {
            var entries = state.GetRecentUnifiedLogs(lines, source, ignoreWatermark: full);
            var clearInfo = full ? null : state.GetClearInfo();
            return new
            {
                entries,
                watermark = state.GetWatermark(),
                clearedAt = clearInfo?.Timestamp.ToString("o"),
                clearReason = clearInfo?.Reason
            };
        });

        // Unified logs clear endpoint (sets watermark)
        app.MapPost("/logs/clear", ([FromQuery] string? reason = null) =>
        {
            var watermark = state.ClearLogWatermark(reason ?? "manual clear");
            return new { success = true, watermark, message = "Logs cleared" };
        });

        // Unified logs stream endpoint (SSE)
        app.MapGet("/logs/stream", async (HttpContext context, [FromQuery] string source = "editor") =>
        {
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";

            var reader = state.SubscribeToLogs();

            try
            {
                await foreach (var entry in reader.ReadAllAsync(context.RequestAborted))
                {
                    // Filter by source if specified
                    if (source != "all" && entry.Source != source)
                        continue;

                    var json = JsonConvert.SerializeObject(entry, JsonHelper.Settings);
                    await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            finally
            {
                state.UnsubscribeFromLogs(reader);
            }
        });

        // RPC endpoint
        app.MapPost("/rpc", async (HttpContext context, [FromBody] RpcRequest request) =>
        {
            if (!state.IsUnityConnected)
            {
                return Results.Problem(
                    statusCode: 503,
                    title: "Unity Offline",
                    detail: "Unity Editor is not connected to the bridge"
                );
            }

            // Convert Args to JToken to avoid System.Text.Json JsonElement issues
            Dictionary<string, object?>? convertedArgs = null;
            if (request.Args != null)
            {
                // Round-trip through JSON to convert System.Text.Json types to Newtonsoft.Json types
                var argsJson = System.Text.Json.JsonSerializer.Serialize(request.Args);
                convertedArgs = JsonConvert.DeserializeObject<Dictionary<string, object?>>(argsJson, JsonHelper.Settings);
            }

            var requestMessage = new RequestMessage
            {
                Origin = MessageOrigin.Bridge,
                RequestId = Guid.NewGuid().ToString(),
                AgentId = request.AgentId,
                Command = request.Command,
                Args = convertedArgs
            };

            try
            {
                // Get command configuration (timeout and completion event)
                var hasConfig = CommandConfigs.TryGetValue(request.Command, out var config);
                var timeout = hasConfig ? config!.Timeout : GetDefaultTimeout();

                // Special handling for play.enter - do asset refresh BEFORE entering play mode
                if (request.Command == UnityCtlCommands.PlayEnter)
                {
                    // First check if already playing by sending play.status
                    var statusRequest = new RequestMessage
                    {
                        Origin = MessageOrigin.Bridge,
                        RequestId = Guid.NewGuid().ToString(),
                        AgentId = request.AgentId,
                        Command = UnityCtlCommands.PlayStatus,
                        Args = null
                    };
                    var statusResponse = await state.SendCommandToUnityAsync(statusRequest, timeout, context.RequestAborted);
                    var currentState = (statusResponse.Result as JObject)?["state"]?.ToString();

                    if (currentState == "playing")
                    {
                        // Already in play mode - return immediately
                        Console.WriteLine($"[Bridge] Play enter: already playing, returning immediately");
                        var alreadyPlayingResponse = new ResponseMessage
                        {
                            Origin = MessageOrigin.Unity,
                            RequestId = requestMessage.RequestId,
                            Status = ResponseStatus.Ok,
                            Result = new { state = "AlreadyPlaying" }
                        };
                        return Results.Json(alreadyPlayingResponse, statusCode: 200);
                    }

                    Console.WriteLine($"[Bridge] Play enter: doing asset refresh first to catch pending changes");

                    // Do asset.refresh FIRST to pick up pending changes and compile
                    var refreshRequest = new RequestMessage
                    {
                        Origin = MessageOrigin.Bridge,
                        RequestId = Guid.NewGuid().ToString(),
                        AgentId = request.AgentId,
                        Command = UnityCtlCommands.AssetRefresh,
                        Args = null
                    };

                    // Pre-register waiter for asset.refreshComplete
                    var refreshWaitTask = state.WaitForEventAsync(
                        refreshRequest.RequestId,
                        UnityCtlEvents.AssetRefreshComplete,
                        timeout,
                        context.RequestAborted);

                    // Send refresh command to Unity
                    await state.SendCommandToUnityAsync(refreshRequest, timeout, context.RequestAborted);

                    // Wait for asset.refreshComplete
                    var refreshEvent = await refreshWaitTask;
                    var refreshPayload = refreshEvent.Payload as JObject;
                    var refreshCompilationTriggered = refreshPayload?["compilationTriggered"]?.Value<bool>() ?? false;
                    var hasCompilationErrors = refreshPayload?["hasCompilationErrors"]?.Value<bool>() ?? false;

                    Console.WriteLine($"[Bridge] Asset refresh complete, compilationTriggered: {refreshCompilationTriggered}, hasCompilationErrors: {hasCompilationErrors}");

                    // If there are existing compilation errors (but no new compilation triggered), fail immediately
                    if (hasCompilationErrors && !refreshCompilationTriggered)
                    {
                        Console.WriteLine($"[Bridge] Existing compilation errors detected - aborting play enter");

                        var errorResponse = new ResponseMessage
                        {
                            Origin = MessageOrigin.Unity,
                            RequestId = requestMessage.RequestId,
                            Status = ResponseStatus.Error,
                            Result = new
                            {
                                state = "CompilationFailed",
                                compilationTriggered = false,
                                compilationSuccess = false,
                                hasCompilationErrors = true
                            },
                            Error = new ErrorPayload { Code = "COMPILATION_ERROR", Message = "Cannot enter play mode - compilation errors exist. Fix the errors and try again." }
                        };
                        return Results.Json(errorResponse, statusCode: 200);
                    }

                    // If compilation was triggered, wait for compilation.finished
                    if (refreshCompilationTriggered)
                    {
                        Console.WriteLine($"[Bridge] Waiting for compilation to finish...");

                        var compileWaitTask = state.WaitForEventAsync(
                            refreshRequest.RequestId + "_compile",
                            UnityCtlEvents.CompilationFinished,
                            timeout,
                            context.RequestAborted);

                        var compileEvent = await compileWaitTask;
                        var compilePayload = compileEvent.Payload as JObject;
                        var compilationSuccess = compilePayload?["success"]?.Value<bool>() ?? true;
                        var errors = compilePayload?["errors"]?.ToObject<CompilationMessageInfo[]>();
                        var warnings = compilePayload?["warnings"]?.ToObject<CompilationMessageInfo[]>();

                        Console.WriteLine($"[Bridge] Compilation finished, success: {compilationSuccess}");

                        // If compilation failed, return error immediately - don't wait for play mode
                        if (!compilationSuccess)
                        {
                            var errorCount = errors?.Length ?? 0;
                            Console.WriteLine($"[Bridge] Compilation failed with {errorCount} error(s) - aborting play enter");

                            var errorResponse = new ResponseMessage
                            {
                                Origin = MessageOrigin.Unity,
                                RequestId = requestMessage.RequestId,
                                Status = ResponseStatus.Error,
                                Result = new
                                {
                                    state = "CompilationFailed",
                                    compilationTriggered = true,
                                    compilationSuccess = false,
                                    errors = errors,
                                    warnings = warnings
                                },
                                Error = new ErrorPayload { Code = "COMPILATION_ERROR", Message = $"Compilation failed with {errorCount} error(s)" }
                            };
                            return Results.Json(errorResponse, statusCode: 200);
                        }
                    }

                    // Now proceed to enter play mode
                    // For domain reload handling: use a loop that can recover from lost events
                    var playEnterStartTime = DateTime.UtcNow;
                    var playEnterTimeout = timeout;

                    while (DateTime.UtcNow - playEnterStartTime < playEnterTimeout)
                    {
                        // Ensure Unity is connected before sending command
                        if (!state.IsUnityConnected)
                        {
                            Console.WriteLine($"[Bridge] Waiting for Unity to reconnect...");
                            while (!state.IsUnityConnected && DateTime.UtcNow - playEnterStartTime < playEnterTimeout)
                            {
                                await Task.Delay(100, context.RequestAborted);
                            }
                            if (!state.IsUnityConnected)
                            {
                                throw new TimeoutException("Unity did not reconnect after domain reload");
                            }
                            Console.WriteLine($"[Bridge] Unity reconnected");
                        }

                        // Check current play status (may fail if Unity disconnects during domain reload)
                        try
                        {
                            var loopStatusRequest = new RequestMessage
                            {
                                Origin = MessageOrigin.Bridge,
                                RequestId = Guid.NewGuid().ToString(),
                                AgentId = request.AgentId,
                                Command = UnityCtlCommands.PlayStatus,
                                Args = null
                            };
                            var loopStatusResponse = await state.SendCommandToUnityAsync(loopStatusRequest, StatusCheckTimeout, context.RequestAborted);
                            var currentPlayState = (loopStatusResponse.Result as JObject)?["state"]?.ToString();

                            Console.WriteLine($"[Bridge] Current play status: {currentPlayState}");

                            if (currentPlayState == "playing")
                            {
                                // Already in play mode (from previous attempt or domain reload)
                                var successResponse = new ResponseMessage
                                {
                                    Origin = MessageOrigin.Unity,
                                    RequestId = requestMessage.RequestId,
                                    Status = ResponseStatus.Ok,
                                    Result = new
                                    {
                                        state = "EnteredPlayMode",
                                        compilationTriggered = refreshCompilationTriggered
                                    }
                                };
                                return Results.Json(successResponse, statusCode: 200);
                            }
                        }
                        catch (TimeoutException)
                        {
                            Console.WriteLine($"[Bridge] Play status check timed out - Unity may have disconnected, retrying...");
                            continue;
                        }

                        // Pre-register event waiter for play mode changes
                        var shortTimeout = ShortEventTimeout;
                        var playEnterWaitTask = state.WaitForEventAsync(
                            requestMessage.RequestId + "_" + Guid.NewGuid().ToString("N")[..8],
                            UnityCtlEvents.PlayModeChanged,
                            shortTimeout,
                            context.RequestAborted);

                        // Send play.enter command (may fail if Unity disconnects)
                        Console.WriteLine($"[Bridge] Sending play.enter command");
                        ResponseMessage playEnterResponse;
                        try
                        {
                            playEnterResponse = await state.SendCommandToUnityAsync(requestMessage, StatusCheckTimeout, context.RequestAborted);
                        }
                        catch (TimeoutException)
                        {
                            Console.WriteLine($"[Bridge] Play enter command timed out - Unity may have disconnected, retrying...");
                            continue;
                        }

                        var playEnterState = (playEnterResponse.Result as JObject)?["state"]?.ToString();

                        if (playEnterState == "AlreadyPlaying")
                        {
                            var alreadyResponse = new ResponseMessage
                            {
                                Origin = MessageOrigin.Unity,
                                RequestId = requestMessage.RequestId,
                                Status = ResponseStatus.Ok,
                                Result = new { state = "AlreadyPlaying" }
                            };
                            return Results.Json(alreadyResponse, statusCode: 200);
                        }

                        // Wait for play mode event with short timeout
                        EventMessage? firstEvent = null;
                        try
                        {
                            firstEvent = await playEnterWaitTask;
                        }
                        catch (TimeoutException)
                        {
                            // Short timeout expired - check if domain reload happened
                            if (state.IsDomainReloadInProgress || !state.IsUnityConnected)
                            {
                                Console.WriteLine($"[Bridge] Domain reload detected during play enter - will retry after reconnect");
                                continue; // Retry the loop
                            }
                            // No domain reload - just timeout, continue to check status
                            continue;
                        }

                        var firstPayload = firstEvent.Payload as JObject;
                        var firstState = firstPayload?["state"]?.ToString();

                        Console.WriteLine($"[Bridge] Play mode event received: {firstState}");

                        if (firstState == "ExitingEditMode")
                        {
                            // Wait for the second event
                            var secondWaitTask = state.WaitForEventAsync(
                                requestMessage.RequestId + "_second_" + Guid.NewGuid().ToString("N")[..8],
                                UnityCtlEvents.PlayModeChanged,
                                shortTimeout,
                                context.RequestAborted);

                            EventMessage? secondEvent = null;
                            try
                            {
                                secondEvent = await secondWaitTask;
                            }
                            catch (TimeoutException)
                            {
                                // Timeout on second event - check status
                                Console.WriteLine($"[Bridge] Timeout waiting for second event - checking status");
                                continue;
                            }

                            var secondPayload = secondEvent.Payload as JObject;
                            var secondState = secondPayload?["state"]?.ToString();

                            Console.WriteLine($"[Bridge] Second play mode event: {secondState}");

                            if (secondState == "EnteredEditMode")
                            {
                                // Bounced back - play mode entry failed
                                Console.WriteLine($"[Bridge] Play mode entry failed - bounced back to edit mode");

                                var errorResponse = new ResponseMessage
                                {
                                    Origin = MessageOrigin.Unity,
                                    RequestId = requestMessage.RequestId,
                                    Status = ResponseStatus.Error,
                                    Result = new
                                    {
                                        state = "PlayModeEntryFailed",
                                        compilationTriggered = refreshCompilationTriggered
                                    },
                                    Error = new ErrorPayload { Code = "PLAY_MODE_FAILED", Message = "Cannot enter play mode - check for compilation errors or other issues in the Unity console" }
                                };
                                return Results.Json(errorResponse, statusCode: 200);
                            }

                            if (secondState == "EnteredPlayMode")
                            {
                                var finalResponse = new ResponseMessage
                                {
                                    Origin = MessageOrigin.Unity,
                                    RequestId = requestMessage.RequestId,
                                    Status = ResponseStatus.Ok,
                                    Result = new
                                    {
                                        state = secondState,
                                        compilationTriggered = refreshCompilationTriggered
                                    }
                                };
                                return Results.Json(finalResponse, statusCode: 200);
                            }
                        }
                        else if (firstState == "EnteredPlayMode")
                        {
                            var directResponse = new ResponseMessage
                            {
                                Origin = MessageOrigin.Unity,
                                RequestId = requestMessage.RequestId,
                                Status = ResponseStatus.Ok,
                                Result = new
                                {
                                    state = firstState,
                                    compilationTriggered = refreshCompilationTriggered
                                }
                            };
                            return Results.Json(directResponse, statusCode: 200);
                        }
                    }

                    // Overall timeout
                    throw new TimeoutException($"Play mode entry timed out after {timeout.TotalSeconds}s");
                }

                // Pre-register WebSocket event waiter for commands that have a completion event
                Task<EventMessage>? eventWaitTask = null;
                if (hasConfig && config!.CompletionEvent != null)
                {
                    eventWaitTask = state.WaitForEventAsync(requestMessage.RequestId, config.CompletionEvent, timeout, context.RequestAborted, config.CompletionEventState);
                }

                // For play exit, also pre-register a waiter for compilation.finished
                // (in case domain reload happens due to pending script changes)
                Task<EventMessage>? compilationWaitTask = null;
                if (request.Command == UnityCtlCommands.PlayExit)
                {
                    compilationWaitTask = state.WaitForEventAsync(
                        requestMessage.RequestId + "_compile_preregister",
                        UnityCtlEvents.CompilationFinished,
                        timeout,
                        context.RequestAborted);
                }

                try
                {
                    // Send command to Unity with cancellation support
                    var response = await state.SendCommandToUnityAsync(requestMessage, timeout, context.RequestAborted);

                    // Check if Unity indicated the command was a no-op (already in desired state)
                    var resultState = (response.Result as Newtonsoft.Json.Linq.JObject)?["state"]?.ToString();
                    var isAlreadyInState = resultState == "AlreadyPlaying" || resultState == "AlreadyStopped";

                    // If command has a WebSocket completion event, wait for it (unless already in state)
                    if (eventWaitTask != null && !isAlreadyInState)
                    {
                        var eventMessage = await eventWaitTask;

                        // Check if compilation was triggered (asset.refreshComplete or playModeChanged entering edit mode)
                        var compilationTriggered = false;
                        if (eventMessage.Event == UnityCtlEvents.AssetRefreshComplete ||
                            eventMessage.Event == UnityCtlEvents.PlayModeChanged)
                        {
                            var payload = eventMessage.Payload as JObject;
                            compilationTriggered = payload?["compilationTriggered"]?.Value<bool>() ?? false;
                        }

                        // If compilation was triggered, wait for compilation.finished
                        object? finalResult = eventMessage.Payload;
                        var eventPayload = eventMessage.Payload as JObject;

                        // For play exit: wait briefly to see if compilation.finished arrives
                        // (compilation events come AFTER ExitingPlayMode but BEFORE DomainReloadStarting)
                        if (!compilationTriggered && compilationWaitTask != null)
                        {
                            // Wait briefly for compilation.finished to arrive
                            // (compilation events come shortly after ExitingPlayMode)
                            var waitTask = Task.Delay(CompilationWaitTimeout, context.RequestAborted);
                            var completedTask = await Task.WhenAny(compilationWaitTask, waitTask);

                            if (completedTask == compilationWaitTask && compilationWaitTask.IsCompletedSuccessfully)
                            {
                                compilationTriggered = true;
                                Console.WriteLine($"[Bridge] Compilation detected during play exit - returning compilation results");
                            }
                        }

                        if (compilationTriggered)
                        {
                            try
                            {
                                // Use pre-registered compilation waiter if available (for play exit),
                                // otherwise create a new one
                                var compileEvent = compilationWaitTask != null
                                    ? await compilationWaitTask
                                    : await state.WaitForEventAsync(
                                        requestMessage.RequestId + "_compile",
                                        UnityCtlEvents.CompilationFinished,
                                        timeout,
                                        context.RequestAborted);

                                var compilePayload = compileEvent.Payload as JObject;
                                var success = compilePayload?["success"]?.Value<bool>() ?? true;
                                var errors = compilePayload?["errors"]?.ToObject<CompilationMessageInfo[]>();
                                var warnings = compilePayload?["warnings"]?.ToObject<CompilationMessageInfo[]>();

                                // Include state for play mode changes
                                var playModeState = eventPayload?["state"]?.Value<string>();
                                if (playModeState != null)
                                {
                                    finalResult = new
                                    {
                                        state = playModeState,
                                        compilationTriggered = true,
                                        compilationSuccess = success,
                                        errors = errors,
                                        warnings = warnings
                                    };
                                }
                                else
                                {
                                    finalResult = new
                                    {
                                        compilationTriggered = true,
                                        compilationSuccess = success,
                                        errors = errors,
                                        warnings = warnings
                                    };
                                }

                                if (!success)
                                {
                                    var errorCount = errors?.Length ?? 0;
                                    response = new ResponseMessage
                                    {
                                        Origin = response.Origin,
                                        RequestId = response.RequestId,
                                        Status = ResponseStatus.Error,
                                        Result = finalResult,
                                        Error = new ErrorPayload { Code = "COMPILATION_ERROR", Message = $"Compilation failed with {errorCount} error(s)" }
                                    };
                                    return Results.Json(response, statusCode: 200);
                                }
                            }
                            catch (TimeoutException)
                            {
                                // Compilation timed out - this can happen during domain reload
                                // Return what we know so far
                                var playModeState = eventPayload?["state"]?.Value<string>();
                                if (playModeState != null)
                                {
                                    finalResult = new
                                    {
                                        state = playModeState,
                                        compilationTriggered = true,
                                        compilationSuccess = (bool?)null,
                                        note = "Compilation may still be in progress (domain reload)"
                                    };
                                }
                                else
                                {
                                    finalResult = new
                                    {
                                        compilationTriggered = true,
                                        compilationSuccess = (bool?)null,
                                        note = "Compilation may still be in progress (domain reload)"
                                    };
                                }
                            }
                        }

                        // Create new response with event payload as the result
                        response = new ResponseMessage
                        {
                            Origin = response.Origin,
                            RequestId = response.RequestId,
                            Status = response.Status,
                            Result = finalResult,
                            Error = response.Error
                        };
                    }

                    var json = JsonConvert.SerializeObject(response, JsonHelper.Settings);
                    return Results.Content(json, "application/json");
                }
                finally
                {
                    // Clean up pre-registered compilation waiter if not used
                    if (compilationWaitTask != null && !compilationWaitTask.IsCompleted)
                    {
                        state.CancelEventWaiter(requestMessage.RequestId + "_compile_preregister");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return Results.Problem(
                    statusCode: 499,
                    title: "Request Cancelled",
                    detail: "Request was cancelled (client disconnected or server shutting down)"
                );
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(
                    statusCode: 504,
                    title: "Request Timeout",
                    detail: ex.Message
                );
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    statusCode: 500,
                    title: "Request Failed",
                    detail: ex.Message
                );
            }
        });

        // WebSocket endpoint for Unity
        app.Map("/unity", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Unity connected");

            state.SetUnityConnection(webSocket);

            try
            {
                await HandleUnityConnectionAsync(webSocket, state, context.RequestAborted);
            }
            finally
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Unity disconnected");
                state.SetUnityConnection(null);
            }
        });
    }

    private static async Task HandleUnityConnectionAsync(WebSocket webSocket, BridgeState state, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 16];
        var messageBuilder = new System.Text.StringBuilder();

        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
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
                        await HandleUnityMessageAsync(json, webSocket, state, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown detected - forcefully abort the WebSocket connection
            // We use Abort() instead of CloseAsync() because we need immediate termination
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent)
            {
                try
                {
                    webSocket.Abort();
                }
                catch
                {
                    // Ignore errors during abort - we're shutting down anyway
                }
            }
        }
        catch (WebSocketException)
        {
            // WebSocket was already closed or aborted - this is fine during shutdown
        }
    }

    private static async Task HandleUnityMessageAsync(string json, WebSocket webSocket, BridgeState state, CancellationToken cancellationToken)
    {
        try
        {
            var jObject = JObject.Parse(json);

            if (!jObject.TryGetValue("type", out var typeToken))
            {
                Console.WriteLine($"[Warning] Received message without 'type' field");
                return;
            }

            var messageType = typeToken.ToString();

            switch (messageType)
            {
                case "hello":
                    await HandleHelloAsync(json, webSocket, state, cancellationToken);
                    break;

                case "response":
                    HandleResponse(json, state);
                    break;

                case "event":
                    HandleEvent(json, state);
                    break;

                default:
                    Console.WriteLine($"[Warning] Unknown message type: {messageType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to handle Unity message: {ex.Message}");
        }
    }

    private static async Task HandleHelloAsync(string json, WebSocket webSocket, BridgeState state, CancellationToken cancellationToken)
    {
        var hello = JsonHelper.Deserialize<HelloMessage>(json);
        if (hello == null)
        {
            Console.WriteLine("[Error] Failed to deserialize hello message");
            return;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Hello from Unity");
        Console.WriteLine($"  Project ID: {hello.ProjectId}");
        Console.WriteLine($"  Unity Version: {hello.UnityVersion}");
        Console.WriteLine($"  Protocol Version: {hello.ProtocolVersion}");

        // Send hello response
        var response = new ResponseMessage
        {
            Origin = MessageOrigin.Bridge,
            RequestId = "hello",
            Status = ResponseStatus.Ok,
            Result = new
            {
                bridgeVersion = VersionInfo.Version,
                projectId = state.ProjectId,
                protocolVersion = "1.0.0"
            }
        };

        var responseJson = JsonHelper.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        );
    }

    private static void HandleResponse(string json, BridgeState state)
    {
        var response = JsonHelper.Deserialize<ResponseMessage>(json);
        if (response != null)
        {
            state.CompleteRequest(response.RequestId, response);
        }
    }

    private static void HandleEvent(string json, BridgeState state)
    {
        var eventMessage = JsonHelper.Deserialize<EventMessage>(json);
        if (eventMessage == null) return;

        // Process event for any waiters first
        state.ProcessEvent(eventMessage);

        // Then handle specific events
        switch (eventMessage.Event)
        {
            case UnityCtlEvents.Log:
                var logEntry = JsonConvert.DeserializeObject<LogEntry>(
                    JsonConvert.SerializeObject(eventMessage.Payload, JsonHelper.Settings),
                    JsonHelper.Settings
                );
                if (logEntry != null)
                {
                    state.AddConsoleLogEntry(logEntry);
                }
                break;

            case UnityCtlEvents.PlayModeChanged:
                // Extract play mode state from payload
                var playModePayload = eventMessage.Payload as Newtonsoft.Json.Linq.JObject;
                var playModeState = playModePayload?["state"]?.ToString();
                Console.WriteLine($"[Event] Play mode changed: {playModeState}");

                // Auto-clear on entering play mode
                if (playModeState == "EnteredPlayMode")
                {
                    state.ClearLogWatermark("entered play mode");
                    Console.WriteLine($"[Bridge] Auto-cleared logs on play mode enter");
                }
                break;

            case UnityCtlEvents.CompilationStarted:
                Console.WriteLine($"[Event] Compilation started");
                break;

            case UnityCtlEvents.CompilationFinished:
                Console.WriteLine($"[Event] Compilation finished");
                break;

            case UnityCtlEvents.AssetImportComplete:
                Console.WriteLine($"[Event] Asset import complete");
                break;

            case UnityCtlEvents.AssetReimportAllComplete:
                Console.WriteLine($"[Event] Asset reimport all complete");
                break;

            case UnityCtlEvents.TestFinished:
                Console.WriteLine($"[Event] Tests finished");
                break;

            case UnityCtlEvents.DomainReloadStarting:
                Console.WriteLine($"[Event] Domain reload starting");
                state.OnDomainReloadStarting();
                break;
        }
    }
}

public class RpcRequest
{
    public string? AgentId { get; set; }
    public required string Command { get; set; }
    public Dictionary<string, object?>? Args { get; set; }
}

internal class CommandConfig
{
    public required TimeSpan Timeout { get; init; }
    public string? CompletionEvent { get; init; }               // WebSocket event from Unity
    public string? CompletionEventState { get; init; }          // Expected state in payload to consider event complete (e.g., "EnteredPlayMode")
}
