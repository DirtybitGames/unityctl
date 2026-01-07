using System;
using System.Collections.Generic;
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
    // Command timeout configuration (in seconds, configurable via environment variables)
    private static readonly Dictionary<string, CommandConfig> CommandConfigs = new()
    {
        [UnityCtlCommands.PlayEnter] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_PLAYMODE", 30),
            CompletionEvent = UnityCtlEvents.PlayModeChanged
        },
        [UnityCtlCommands.PlayExit] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_PLAYMODE", 30),
            CompletionEvent = UnityCtlEvents.PlayModeChanged
        },
        [UnityCtlCommands.CompileScripts] = new CommandConfig
        {
            Timeout = GetTimeoutFromEnv("UNITYCTL_TIMEOUT_COMPILE", 30),
            CompletionEvent = UnityCtlEvents.CompilationFinished
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
            LogCompletionEvent = LogEvents.RefreshComplete
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

        // Console tail endpoint
        app.MapGet("/console/tail", ([FromQuery] int lines = 50) =>
        {
            var entries = state.GetRecentLogs(lines);
            return new ConsoleTailResult { Entries = entries };
        });

        // Console clear endpoint
        app.MapPost("/console/clear", () =>
        {
            state.ClearLogs();
            return new { success = true, message = "Console cleared" };
        });

        // Unified logs tail endpoint
        app.MapGet("/logs/tail", ([FromQuery] int lines = 50, [FromQuery] string source = "editor") =>
        {
            var entries = state.GetRecentUnifiedLogs(lines, source);
            return new { entries };
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

                // Pre-register log event waiter BEFORE sending command to avoid race condition
                // (the event might fire before we start waiting)
                LogEventWaiterHandle? logEventWaiter = null;
                if (hasConfig && config!.LogCompletionEvent != null)
                {
                    logEventWaiter = state.RegisterLogEventWaiter(config.LogCompletionEvent);
                }

                try
                {
                    // Send command to Unity with cancellation support
                    var response = await state.SendCommandToUnityAsync(requestMessage, timeout, context.RequestAborted);

                    // If command has a WebSocket completion event, wait for it
                    if (hasConfig && config!.CompletionEvent != null)
                    {
                        var eventMessage = await state.WaitForEventAsync(requestMessage.RequestId, config.CompletionEvent, timeout, context.RequestAborted);

                        // Create new response with event payload as the result
                        response = new ResponseMessage
                        {
                            Origin = response.Origin,
                            RequestId = response.RequestId,
                            Status = response.Status,
                            Result = eventMessage.Payload,
                            Error = response.Error
                        };
                    }
                    // If command has a log-based completion event, wait for it
                    else if (logEventWaiter != null)
                    {
                        var logEvent = await logEventWaiter.WaitAsync(timeout, context.RequestAborted);

                        // Create new response with log event data as the result
                        response = new ResponseMessage
                        {
                            Origin = response.Origin,
                            RequestId = response.RequestId,
                            Status = response.Status,
                            Result = new { completed = true, eventType = logEvent.Type, data = logEvent.Data },
                            Error = response.Error
                        };
                    }

                    var json = JsonConvert.SerializeObject(response, JsonHelper.Settings);
                    return Results.Content(json, "application/json");
                }
                finally
                {
                    logEventWaiter?.Dispose();
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
                Console.WriteLine($"[Event] Play mode changed");
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
    public string? CompletionEvent { get; init; }        // WebSocket event from Unity
    public string? LogCompletionEvent { get; init; }     // Log-based event from editor.log
}
