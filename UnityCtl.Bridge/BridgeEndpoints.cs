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

            var requestMessage = new RequestMessage
            {
                Origin = MessageOrigin.Bridge,
                RequestId = Guid.NewGuid().ToString(),
                AgentId = request.AgentId,
                Command = request.Command,
                Args = request.Args
            };

            try
            {
                // Get command configuration (timeout and completion event)
                var hasConfig = CommandConfigs.TryGetValue(request.Command, out var config);
                var timeout = hasConfig ? config!.Timeout : GetDefaultTimeout();

                // Send command to Unity with cancellation support
                var response = await state.SendCommandToUnityAsync(requestMessage, timeout, context.RequestAborted);

                // If command has a completion event, wait for it with cancellation support
                if (hasConfig && config!.CompletionEvent != null)
                {
                    await state.WaitForEventAsync(requestMessage.RequestId, config.CompletionEvent, timeout, context.RequestAborted);
                }

                var json = JsonConvert.SerializeObject(response, JsonHelper.Settings);
                return Results.Content(json, "application/json");
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
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleUnityMessageAsync(json, webSocket, state, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown - try to close the WebSocket cleanly
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Bridge shutting down",
                        CancellationToken.None // Use None here as we're already cancelled
                    );
                }
                catch
                {
                    // Ignore errors during cleanup
                }
            }
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
                bridgeVersion = "0.1.0",
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
                    state.AddLogEntry(logEntry);
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
    public string? CompletionEvent { get; init; }
}
