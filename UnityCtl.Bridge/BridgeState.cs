using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Bridge;

/// <summary>
/// Unified log entry that can come from either editor.log or console (Debug.Log)
/// </summary>
public class UnifiedLogEntry
{
    public required string Timestamp { get; init; }
    public required string Source { get; init; }  // "editor" or "console"
    public required string Level { get; init; }   // "Log", "Warning", "Error", "Exception", or "Info" for editor
    public required string Message { get; init; }
    public string? StackTrace { get; init; }
    public ConsoleColor? Color { get; init; }
}

public class BridgeState
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _logBuffer = new();  // Console logs (backward compat)
    private readonly List<UnifiedLogEntry> _unifiedLogBuffer = new();  // Unified logs
    private const int MaxLogEntries = 1000;

    // Channels for log streaming (multiple subscribers supported)
    private readonly List<Channel<UnifiedLogEntry>> _logSubscribers = new();

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
            _unifiedLogBuffer.Clear();
        }
    }

    #region Unified Logging

    /// <summary>
    /// Add an editor log entry (from editor.log file)
    /// </summary>
    public void AddEditorLogEntry(string message, string level = "Info", ConsoleColor? color = null)
    {
        var entry = new UnifiedLogEntry
        {
            Timestamp = DateTime.Now.ToString("o"),
            Source = "editor",
            Level = level,
            Message = message,
            Color = color
        };

        AddUnifiedLogEntry(entry);
    }

    /// <summary>
    /// Add a console log entry (from Unity Debug.Log) - also adds to unified buffer
    /// </summary>
    public void AddConsoleLogEntry(LogEntry logEntry)
    {
        // Add to legacy buffer for backward compatibility
        lock (_lock)
        {
            _logBuffer.Add(logEntry);
            if (_logBuffer.Count > MaxLogEntries)
            {
                _logBuffer.RemoveAt(0);
            }
        }

        // Also add to unified buffer
        var unified = new UnifiedLogEntry
        {
            Timestamp = logEntry.Timestamp,
            Source = "console",
            Level = logEntry.Level,
            Message = logEntry.Message,
            StackTrace = logEntry.StackTrace,
            Color = logEntry.Level switch
            {
                "Error" or "Exception" => ConsoleColor.Red,
                "Warning" => ConsoleColor.Yellow,
                _ => null
            }
        };

        AddUnifiedLogEntry(unified);
    }

    private void AddUnifiedLogEntry(UnifiedLogEntry entry)
    {
        lock (_lock)
        {
            _unifiedLogBuffer.Add(entry);
            if (_unifiedLogBuffer.Count > MaxLogEntries)
            {
                _unifiedLogBuffer.RemoveAt(0);
            }

            // Notify all subscribers
            foreach (var channel in _logSubscribers.ToArray())
            {
                // Non-blocking write - if channel is full, skip this entry for that subscriber
                channel.Writer.TryWrite(entry);
            }
        }
    }

    /// <summary>
    /// Get recent unified logs, optionally filtered by source
    /// </summary>
    public UnifiedLogEntry[] GetRecentUnifiedLogs(int count, string? source = null)
    {
        lock (_lock)
        {
            IEnumerable<UnifiedLogEntry> filtered = _unifiedLogBuffer;

            if (!string.IsNullOrEmpty(source) && source != "all")
            {
                filtered = filtered.Where(e => e.Source == source);
            }

            return filtered.TakeLast(count).ToArray();
        }
    }

    /// <summary>
    /// Subscribe to log stream. Returns a channel reader that yields new log entries.
    /// </summary>
    public ChannelReader<UnifiedLogEntry> SubscribeToLogs()
    {
        var channel = Channel.CreateBounded<UnifiedLogEntry>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_lock)
        {
            _logSubscribers.Add(channel);
        }

        return channel.Reader;
    }

    /// <summary>
    /// Unsubscribe from log stream
    /// </summary>
    public void UnsubscribeFromLogs(ChannelReader<UnifiedLogEntry> reader)
    {
        lock (_lock)
        {
            var channel = _logSubscribers.FirstOrDefault(c => c.Reader == reader);
            if (channel != null)
            {
                _logSubscribers.Remove(channel);
                channel.Writer.Complete();
            }
        }
    }

    #endregion

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

    #region Log-Based Completion Detection

    // Track requests waiting for log-based completion events
    private readonly ConcurrentDictionary<string, PendingLogEventWaiter> _pendingLogEventWaiters = new();

    /// <summary>
    /// Pre-register a waiter for a log event. Returns a handle that can be awaited later.
    /// Use this when you need to register BEFORE triggering the action that will emit the event.
    /// </summary>
    public LogEventWaiterHandle RegisterLogEventWaiter(string eventType)
    {
        var tcs = new TaskCompletionSource<LogEvent>();
        var waiterId = Guid.NewGuid().ToString();
        var waiter = new PendingLogEventWaiter
        {
            EventType = eventType,
            CompletionSource = tcs
        };

        _pendingLogEventWaiters[waiterId] = waiter;

        return new LogEventWaiterHandle(waiterId, tcs.Task, () => _pendingLogEventWaiters.TryRemove(waiterId, out _));
    }

    /// <summary>
    /// Wait for a specific log event to be detected from editor.log
    /// </summary>
    public async Task<LogEvent> WaitForLogEventAsync(string eventType, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var handle = RegisterLogEventWaiter(eventType);
        return await handle.WaitAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Notify that a log event was detected. Completes any waiters waiting for this event type.
    /// </summary>
    public void NotifyLogEvent(LogEvent logEvent)
    {
        var completedWaiters = new List<string>();

        foreach (var kvp in _pendingLogEventWaiters)
        {
            if (kvp.Value.EventType == logEvent.Type)
            {
                kvp.Value.CompletionSource.TrySetResult(logEvent);
                completedWaiters.Add(kvp.Key);
            }
        }

        // Remove completed waiters
        foreach (var waiterId in completedWaiters)
        {
            _pendingLogEventWaiters.TryRemove(waiterId, out _);
        }
    }

    #endregion
}

/// <summary>
/// Represents a request waiting for a completion event
/// </summary>
internal class PendingEventWaiter
{
    public required string EventName { get; init; }
    public required TaskCompletionSource<EventMessage> CompletionSource { get; init; }
}

/// <summary>
/// Represents a request waiting for a log-based completion event
/// </summary>
internal class PendingLogEventWaiter
{
    public required string EventType { get; init; }
    public required TaskCompletionSource<LogEvent> CompletionSource { get; init; }
}

/// <summary>
/// Handle for a pre-registered log event waiter. Dispose to cancel/cleanup.
/// </summary>
public class LogEventWaiterHandle : IDisposable
{
    private readonly string _waiterId;
    private readonly Task<LogEvent> _task;
    private readonly Action _cleanup;
    private bool _disposed;

    internal LogEventWaiterHandle(string waiterId, Task<LogEvent> task, Action cleanup)
    {
        _waiterId = waiterId;
        _task = task;
        _cleanup = cleanup;
    }

    public async Task<LogEvent> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var registration = cancellationToken.Register(() =>
        {
            // If cancelled, we need to handle it
        });

        var timeoutTask = Task.Delay(timeout, cts.Token);
        var completedTask = await Task.WhenAny(_task, timeoutTask);

        if (completedTask == timeoutTask && !_task.IsCompleted)
        {
            throw new TimeoutException($"Waiting for log event timed out after {timeout.TotalSeconds}s");
        }

        return await _task;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cleanup();
        }
    }
}
