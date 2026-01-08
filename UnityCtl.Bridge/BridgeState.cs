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

public class BridgeState
{
    private readonly object _lock = new();
    private readonly List<UnifiedLogEntry> _unifiedLogBuffer = new();
    private const int MaxLogEntries = 1000;

    // Sequence number and watermark for log clearing
    private long _nextSequenceNumber = 0;
    private long _clearWatermark = -1;  // -1 = no clear, show all logs
    private DateTime? _clearTimestamp;
    private string? _clearReason;

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

    public bool IsDomainReloadInProgress
    {
        get
        {
            lock (_lock)
            {
                return _isDomainReloadInProgress;
            }
        }
    }

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

    #region Unified Logging

    /// <summary>
    /// Add a console log entry (from Unity Debug.Log)
    /// </summary>
    public void AddConsoleLogEntry(LogEntry logEntry)
    {
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
            // Assign sequence number
            var sequencedEntry = new UnifiedLogEntry
            {
                SequenceNumber = _nextSequenceNumber++,
                Timestamp = entry.Timestamp,
                Source = entry.Source,
                Level = entry.Level,
                Message = entry.Message,
                StackTrace = entry.StackTrace,
                Color = entry.Color
            };

            _unifiedLogBuffer.Add(sequencedEntry);
            if (_unifiedLogBuffer.Count > MaxLogEntries)
            {
                _unifiedLogBuffer.RemoveAt(0);
            }

            // Notify all subscribers
            foreach (var channel in _logSubscribers.ToArray())
            {
                // Non-blocking write - if channel is full, skip this entry for that subscriber
                channel.Writer.TryWrite(sequencedEntry);
            }
        }
    }

    /// <summary>
    /// Get recent unified logs, optionally filtered by source.
    /// By default, only returns logs since the last clear (watermark).
    /// Set ignoreWatermark=true to get full history.
    /// </summary>
    public UnifiedLogEntry[] GetRecentUnifiedLogs(int count, string? source = null, bool ignoreWatermark = false)
    {
        lock (_lock)
        {
            IEnumerable<UnifiedLogEntry> filtered = _unifiedLogBuffer;

            // Apply watermark filter unless explicitly ignored
            if (!ignoreWatermark && _clearWatermark >= 0)
            {
                filtered = filtered.Where(e => e.SequenceNumber >= _clearWatermark);
            }

            if (!string.IsNullOrEmpty(source) && source != "all")
            {
                filtered = filtered.Where(e => e.Source == source);
            }

            return filtered.TakeLast(count).ToArray();
        }
    }

    #region Log Clearing

    /// <summary>
    /// Clear logs by setting watermark. Returns the new watermark value.
    /// Logs before this watermark will be hidden by default (but still available via ignoreWatermark).
    /// </summary>
    public long ClearLogWatermark(string? reason = null)
    {
        lock (_lock)
        {
            _clearWatermark = _nextSequenceNumber;
            _clearTimestamp = DateTime.Now;
            _clearReason = reason;
            return _clearWatermark;
        }
    }

    /// <summary>
    /// Get current watermark value. -1 means no clear applied.
    /// </summary>
    public long GetWatermark()
    {
        lock (_lock)
        {
            return _clearWatermark;
        }
    }

    /// <summary>
    /// Get information about the last clear operation.
    /// Returns null if logs have never been cleared.
    /// </summary>
    public LogClearInfo? GetClearInfo()
    {
        lock (_lock)
        {
            if (_clearWatermark < 0 || _clearTimestamp == null)
                return null;

            return new LogClearInfo
            {
                Watermark = _clearWatermark,
                Timestamp = _clearTimestamp.Value,
                Reason = _clearReason
            };
        }
    }

    #endregion

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
    public async Task<EventMessage> WaitForEventAsync(string requestId, string eventName, TimeSpan timeout, CancellationToken cancellationToken = default, string? expectedState = null)
    {
        var tcs = new TaskCompletionSource<EventMessage>();
        var waiter = new PendingEventWaiter
        {
            EventName = eventName,
            ExpectedState = expectedState,
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
                // If waiter has an expected state, check it matches
                if (kvp.Value.ExpectedState != null)
                {
                    var payload = eventMessage.Payload as Newtonsoft.Json.Linq.JObject;
                    var actualState = payload?["state"]?.ToString();
                    if (actualState != kvp.Value.ExpectedState)
                    {
                        // State doesn't match, skip this event
                        continue;
                    }
                }

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
    public string? ExpectedState { get; init; }  // Optional: only complete when payload.state matches
    public required TaskCompletionSource<EventMessage> CompletionSource { get; init; }
}

/// <summary>
/// Information about the last log clear operation
/// </summary>
public class LogClearInfo
{
    public long Watermark { get; init; }
    public DateTime Timestamp { get; init; }
    public string? Reason { get; init; }
}
