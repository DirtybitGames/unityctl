using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;

namespace UnityCtl.Tests.Fakes;

/// <summary>
/// A programmable WebSocket client that simulates Unity Editor behavior.
/// Connects to the Bridge's /unity WebSocket endpoint and responds to commands
/// with configurable responses and events.
/// </summary>
public class FakeUnityClient : IAsyncDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    private readonly ConcurrentDictionary<string, CommandHandler> _commandHandlers = new();
    private readonly ConcurrentQueue<Action<FakeUnityClient>> _postResponseActions = new();
    private readonly ConcurrentQueue<ReceivedRequest> _receivedRequests = new();
    private readonly SemaphoreSlim _requestReceived = new(0);

    private string _projectId = "proj-test1234";
    private string _unityVersion = "6000.0.0f1";
    private string _protocolVersion = "1.0.0";
    private string _pluginVersion = "0.3.6";

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>
    /// All requests received by this fake Unity client, in order.
    /// </summary>
    public IReadOnlyCollection<ReceivedRequest> ReceivedRequests =>
        _receivedRequests.ToArray();

    public FakeUnityClient WithProjectId(string projectId)
    {
        _projectId = projectId;
        return this;
    }

    /// <summary>
    /// Register a handler for a specific command. The handler receives the request
    /// and returns the result object for the response.
    /// </summary>
    public FakeUnityClient OnCommand(string command, Func<RequestMessage, object?> handler)
    {
        _commandHandlers[command] = new CommandHandler
        {
            ResultFactory = handler,
            DelayBefore = TimeSpan.Zero,
            EventsAfter = new List<ScheduledEvent>()
        };
        return this;
    }

    /// <summary>
    /// Register a handler that returns a result and emits events after a delay.
    /// </summary>
    public FakeUnityClient OnCommand(string command, Func<RequestMessage, object?> handler,
        params ScheduledEvent[] eventsAfter)
    {
        _commandHandlers[command] = new CommandHandler
        {
            ResultFactory = handler,
            DelayBefore = TimeSpan.Zero,
            EventsAfter = eventsAfter.ToList()
        };
        return this;
    }

    /// <summary>
    /// Register a handler that delays before responding (simulates slow Unity operations).
    /// </summary>
    public FakeUnityClient OnCommandWithDelay(string command, TimeSpan delay,
        Func<RequestMessage, object?> handler, params ScheduledEvent[] eventsAfter)
    {
        _commandHandlers[command] = new CommandHandler
        {
            ResultFactory = handler,
            DelayBefore = delay,
            EventsAfter = eventsAfter.ToList()
        };
        return this;
    }

    /// <summary>
    /// Register a handler that returns an error response.
    /// </summary>
    public FakeUnityClient OnCommandError(string command, string errorCode, string errorMessage)
    {
        _commandHandlers[command] = new CommandHandler
        {
            IsError = true,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ResultFactory = _ => null,
            EventsAfter = new List<ScheduledEvent>()
        };
        return this;
    }

    /// <summary>
    /// Connect to the Bridge's WebSocket endpoint and send a HelloMessage.
    /// </summary>
    public async Task ConnectAsync(Uri bridgeUri, CancellationToken ct = default)
    {
        var wsUri = new Uri(bridgeUri, "/unity");
        // Convert http:// to ws://
        var wsUriBuilder = new UriBuilder(wsUri) { Scheme = wsUri.Scheme == "https" ? "wss" : "ws" };

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(wsUriBuilder.Uri, ct);

        // Send hello message
        var hello = new HelloMessage
        {
            Origin = MessageOrigin.Unity,
            ProjectId = _projectId,
            UnityVersion = _unityVersion,
            ProtocolVersion = _protocolVersion,
            PluginVersion = _pluginVersion,
            Capabilities = new[] { "scene", "play", "asset", "test", "screenshot", "script", "menu" }
        };

        await SendMessageAsync(hello, ct);

        // Start receive loop
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

        // Wait briefly for the hello response
        await Task.Delay(100, ct);
    }

    /// <summary>
    /// Send an event message to the Bridge (simulates Unity firing an event).
    /// </summary>
    public async Task SendEventAsync(string eventName, object payload, CancellationToken ct = default)
    {
        var eventMessage = new EventMessage
        {
            Origin = MessageOrigin.Unity,
            Event = eventName,
            Payload = payload
        };
        await SendMessageAsync(eventMessage, ct);
    }

    /// <summary>
    /// Simulate a domain reload: disconnect, wait, then reconnect.
    /// </summary>
    public async Task SimulateDomainReloadAsync(Uri bridgeUri, TimeSpan disconnectDuration,
        CancellationToken ct = default)
    {
        // First send the domain reload starting event
        await SendEventAsync(UnityCtlEvents.DomainReloadStarting, new { }, ct);
        await Task.Delay(50, ct);

        // Disconnect
        await DisconnectAsync();

        // Wait (simulates Unity reloading assemblies)
        await Task.Delay(disconnectDuration, ct);

        // Reconnect
        await ConnectAsync(bridgeUri, ct);
    }

    /// <summary>
    /// Gracefully disconnect from the Bridge.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting",
                    CancellationToken.None);
            }
            catch
            {
                // Ignore close errors
            }
        }

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }

        _webSocket?.Dispose();
        _webSocket = null;
    }

    /// <summary>
    /// Wait until a request with the given command is received.
    /// </summary>
    public async Task<ReceivedRequest> WaitForRequestAsync(string command,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTime.UtcNow < deadline)
        {
            // Check existing requests
            foreach (var req in _receivedRequests)
            {
                if (req.Command == command && !req.Claimed)
                {
                    req.Claimed = true;
                    return req;
                }
            }

            // Wait for new request
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(deadline - DateTime.UtcNow);
            try
            {
                await _requestReceived.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for command '{command}'");
    }

    private async Task SendMessageAsync(BaseMessage message, CancellationToken ct)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var json = JsonHelper.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task SendRawJsonAsync(string json, CancellationToken ct)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 16];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var json = messageBuilder.ToString();
                        messageBuilder.Clear();
                        await HandleIncomingMessageAsync(json, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task HandleIncomingMessageAsync(string json, CancellationToken ct)
    {
        var jObject = JObject.Parse(json);
        var type = jObject["type"]?.ToString();

        if (type == "request")
        {
            var request = JsonHelper.Deserialize<RequestMessage>(json);
            if (request == null) return;

            var received = new ReceivedRequest
            {
                RequestId = request.RequestId,
                Command = request.Command,
                Args = request.Args,
                ReceivedAt = DateTime.UtcNow
            };
            _receivedRequests.Enqueue(received);
            _requestReceived.Release();

            await HandleCommandAsync(request, ct);
        }
        // Ignore response messages (hello response from bridge)
    }

    private async Task HandleCommandAsync(RequestMessage request, CancellationToken ct)
    {
        if (_commandHandlers.TryGetValue(request.Command, out var handler))
        {
            // Simulate processing delay
            if (handler.DelayBefore > TimeSpan.Zero)
            {
                await Task.Delay(handler.DelayBefore, ct);
            }

            if (handler.IsError)
            {
                var errorResponse = new ResponseMessage
                {
                    Origin = MessageOrigin.Unity,
                    RequestId = request.RequestId,
                    Status = ResponseStatus.Error,
                    Error = new ErrorPayload
                    {
                        Code = handler.ErrorCode!,
                        Message = handler.ErrorMessage!
                    }
                };
                await SendMessageAsync(errorResponse, ct);
            }
            else
            {
                var result = handler.ResultFactory(request);
                var response = new ResponseMessage
                {
                    Origin = MessageOrigin.Unity,
                    RequestId = request.RequestId,
                    Status = ResponseStatus.Ok,
                    Result = result
                };
                await SendMessageAsync(response, ct);
            }

            // Fire scheduled events
            foreach (var scheduledEvent in handler.EventsAfter)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(scheduledEvent.Delay, ct);
                    await SendEventAsync(scheduledEvent.EventName, scheduledEvent.Payload, ct);
                }, ct);
            }
        }
        else
        {
            // Default: return ok with empty result
            var response = new ResponseMessage
            {
                Origin = MessageOrigin.Unity,
                RequestId = request.RequestId,
                Status = ResponseStatus.Ok,
                Result = new { }
            };
            await SendMessageAsync(response, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _receiveCts?.Dispose();
    }
}

/// <summary>
/// A command handler registered with FakeUnityClient.
/// </summary>
internal class CommandHandler
{
    public required Func<RequestMessage, object?> ResultFactory { get; init; }
    public TimeSpan DelayBefore { get; init; }
    public required List<ScheduledEvent> EventsAfter { get; init; }
    public bool IsError { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// An event scheduled to fire after a command response.
/// </summary>
public class ScheduledEvent
{
    public required string EventName { get; init; }
    public required object Payload { get; init; }
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(50);

    public static ScheduledEvent After(TimeSpan delay, string eventName, object payload) => new()
    {
        EventName = eventName,
        Payload = payload,
        Delay = delay
    };

    public static ScheduledEvent Immediate(string eventName, object payload) => new()
    {
        EventName = eventName,
        Payload = payload,
        Delay = TimeSpan.FromMilliseconds(10)
    };
}

/// <summary>
/// Record of a request received by the fake Unity client.
/// </summary>
public class ReceivedRequest
{
    public required string RequestId { get; init; }
    public required string Command { get; init; }
    public Dictionary<string, object?>? Args { get; init; }
    public DateTime ReceivedAt { get; init; }
    internal bool Claimed { get; set; }
}
