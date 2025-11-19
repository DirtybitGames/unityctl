using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Bridge;

public class BridgeState
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _logBuffer = new();
    private const int MaxLogEntries = 1000;

    public BridgeState(string projectId)
    {
        ProjectId = projectId;
    }

    public string ProjectId { get; }

    public WebSocket? UnityConnection { get; private set; }

    public ConcurrentDictionary<string, TaskCompletionSource<ResponseMessage>> PendingRequests { get; } = new();

    // Track requests waiting for completion events
    private readonly ConcurrentDictionary<string, PendingEventWaiter> _pendingEventWaiters = new();

    // Domain reload grace period tracking
    private bool _isDomainReloadInProgress = false;
    private DateTime _domainReloadGracePeriodEnd = DateTime.MinValue;
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(60);

    public bool IsUnityConnected => UnityConnection?.State == WebSocketState.Open;

    public void SetUnityConnection(WebSocket? connection)
    {
        lock (_lock)
        {
            UnityConnection = connection;

            if (connection == null)
            {
                // Unity disconnected - check if we're in domain reload grace period
                if (_isDomainReloadInProgress)
                {
                    Console.WriteLine($"[Bridge] Unity disconnected during domain reload grace period - keeping operations alive");
                    // Don't cancel operations - wait for reconnection
                }
                else
                {
                    // Normal disconnection - cancel all pending operations
                    Console.WriteLine($"[Bridge] Unity disconnected - canceling all pending operations");
                    CancelAllPendingOperations();
                }
            }
            else
            {
                // Unity connected/reconnected
                if (_isDomainReloadInProgress)
                {
                    Console.WriteLine($"[Bridge] Unity reconnected after domain reload - resuming operations");
                    _isDomainReloadInProgress = false;
                    _domainReloadGracePeriodEnd = DateTime.MinValue;
                }
                else
                {
                    Console.WriteLine($"[Bridge] Unity connected");
                }
            }
        }
    }

    /// <summary>
    /// Forcefully abort the Unity WebSocket connection (used during shutdown)
    /// </summary>
    public void AbortUnityConnection()
    {
        WebSocket? connection;
        lock (_lock)
        {
            connection = UnityConnection;
            UnityConnection = null;
        }

        if (connection != null)
        {
            try
            {
                // Abort forcefully terminates the WebSocket without waiting for graceful closure
                connection.Abort();
            }
            catch
            {
                // Ignore errors during abort - we're shutting down anyway
            }
        }

        // Cancel all pending operations
        CancelAllPendingOperations();
    }

    /// <summary>
    /// Cancel all pending requests and event waiters
    /// </summary>
    public void CancelAllPendingOperations()
    {
        // Cancel all pending requests
        foreach (var kvp in PendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        PendingRequests.Clear();

        // Cancel all pending event waiters
        foreach (var kvp in _pendingEventWaiters)
        {
            kvp.Value.CompletionSource.TrySetCanceled();
        }
        _pendingEventWaiters.Clear();
    }

    /// <summary>
    /// Called when Unity sends DomainReloadStarting event - enter grace period
    /// </summary>
    public void OnDomainReloadStarting()
    {
        lock (_lock)
        {
            _isDomainReloadInProgress = true;
            _domainReloadGracePeriodEnd = DateTime.UtcNow.Add(DefaultGracePeriod);
            Console.WriteLine($"[Bridge] Domain reload starting - entering grace period for {DefaultGracePeriod.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Check if grace period has expired and cancel operations if so
    /// Call this periodically or when operations timeout
    /// </summary>
    public void CheckGracePeriodExpired()
    {
        lock (_lock)
        {
            if (_isDomainReloadInProgress && DateTime.UtcNow > _domainReloadGracePeriodEnd)
            {
                Console.WriteLine($"[Bridge] Domain reload grace period expired - Unity did not reconnect in time");
                _isDomainReloadInProgress = false;
                _domainReloadGracePeriodEnd = DateTime.MinValue;
                CancelAllPendingOperations();
            }
        }
    }

    public void AddLogEntry(LogEntry entry)
    {
        lock (_lock)
        {
            _logBuffer.Add(entry);
            if (_logBuffer.Count > MaxLogEntries)
            {
                _logBuffer.RemoveAt(0);
            }
        }
    }

    public LogEntry[] GetRecentLogs(int count)
    {
        lock (_lock)
        {
            var skip = Math.Max(0, _logBuffer.Count - count);
            return _logBuffer.Skip(skip).ToArray();
        }
    }

    public void ClearLogs()
    {
        lock (_lock)
        {
            _logBuffer.Clear();
        }
    }

    public async Task<ResponseMessage> SendCommandToUnityAsync(RequestMessage request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsUnityConnected || UnityConnection == null)
        {
            throw new InvalidOperationException("Unity is not connected");
        }

        var tcs = new TaskCompletionSource<ResponseMessage>();
        PendingRequests[request.RequestId] = tcs;

        // Register cancellation callback to cancel the TCS
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            // Send request
            var json = JsonHelper.Serialize(request);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await UnityConnection.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken
            );

            // Wait for response with timeout and cancellation
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var timeoutTask = Task.Delay(timeout, cts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask && !tcs.Task.IsCompleted)
            {
                throw new TimeoutException($"Command '{request.Command}' timed out after {timeout.TotalSeconds}s");
            }

            return await tcs.Task;
        }
        finally
        {
            PendingRequests.TryRemove(request.RequestId, out _);
        }
    }

    public void CompleteRequest(string requestId, ResponseMessage response)
    {
        if (PendingRequests.TryGetValue(requestId, out var tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    /// <summary>
    /// Wait for a specific event to be received from Unity
    /// </summary>
    public async Task<EventMessage> WaitForEventAsync(string requestId, string eventName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<EventMessage>();
        var waiter = new PendingEventWaiter
        {
            EventName = eventName,
            CompletionSource = tcs
        };

        _pendingEventWaiters[requestId] = waiter;

        // Register cancellation callback to cancel the TCS
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var timeoutTask = Task.Delay(timeout, cts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask && !tcs.Task.IsCompleted)
            {
                throw new TimeoutException($"Waiting for event '{eventName}' timed out after {timeout.TotalSeconds}s");
            }

            return await tcs.Task;
        }
        finally
        {
            _pendingEventWaiters.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Process an incoming event and complete any waiters waiting for it
    /// </summary>
    public void ProcessEvent(EventMessage eventMessage)
    {
        // Complete any pending waiters for this event
        var completedWaiters = new List<string>();

        foreach (var kvp in _pendingEventWaiters)
        {
            if (kvp.Value.EventName == eventMessage.Event)
            {
                kvp.Value.CompletionSource.TrySetResult(eventMessage);
                completedWaiters.Add(kvp.Key);
            }
        }

        // Remove completed waiters
        foreach (var requestId in completedWaiters)
        {
            _pendingEventWaiters.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Cancel waiting for an event (used when request is cancelled)
    /// </summary>
    public void CancelEventWaiter(string requestId)
    {
        if (_pendingEventWaiters.TryRemove(requestId, out var waiter))
        {
            waiter.CompletionSource.TrySetCanceled();
        }
    }
}

/// <summary>
/// Represents a request waiting for a completion event
/// </summary>
internal class PendingEventWaiter
{
    public required string EventName { get; init; }
    public required TaskCompletionSource<EventMessage> CompletionSource { get; init; }
}
