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

    public bool IsUnityConnected => UnityConnection?.State == WebSocketState.Open;

    public void SetUnityConnection(WebSocket? connection)
    {
        lock (_lock)
        {
            UnityConnection = connection;
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

    public async Task<ResponseMessage> SendCommandToUnityAsync(RequestMessage request, TimeSpan timeout)
    {
        if (!IsUnityConnected || UnityConnection == null)
        {
            throw new InvalidOperationException("Unity is not connected");
        }

        var tcs = new TaskCompletionSource<ResponseMessage>();
        PendingRequests[request.RequestId] = tcs;

        try
        {
            // Send request
            var json = JsonHelper.Serialize(request);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await UnityConnection.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );

            // Wait for response with timeout
            using var cts = new CancellationTokenSource(timeout);
            var timeoutTask = Task.Delay(timeout, cts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
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
}
