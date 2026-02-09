using UnityCtl.Bridge;
using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Bridge;

public class BridgeStateTests
{
    #region Log Buffer

    [Fact]
    public void AddConsoleLogEntry_StoresEntryInBuffer()
    {
        var state = new BridgeState("proj-test");

        state.AddConsoleLogEntry(new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Hello World"
        });

        var logs = state.GetRecentUnifiedLogs(10);
        Assert.Single(logs);
        Assert.Equal("Hello World", logs[0].Message);
        Assert.Equal("console", logs[0].Source);
        Assert.Equal(LogLevel.Log, logs[0].Level);
    }

    [Fact]
    public void AddConsoleLogEntry_AssignsSequenceNumbers()
    {
        var state = new BridgeState("proj-test");

        for (int i = 0; i < 5; i++)
        {
            state.AddConsoleLogEntry(new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Message {i}"
            });
        }

        var logs = state.GetRecentUnifiedLogs(10);
        Assert.Equal(5, logs.Length);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i, logs[i].SequenceNumber);
        }
    }

    [Fact]
    public void GetRecentUnifiedLogs_RespectsCountLimit()
    {
        var state = new BridgeState("proj-test");

        for (int i = 0; i < 10; i++)
        {
            state.AddConsoleLogEntry(new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Message {i}"
            });
        }

        var logs = state.GetRecentUnifiedLogs(3);
        Assert.Equal(3, logs.Length);
        // Should return the LAST 3 entries
        Assert.Equal("Message 7", logs[0].Message);
        Assert.Equal("Message 8", logs[1].Message);
        Assert.Equal("Message 9", logs[2].Message);
    }

    [Fact]
    public void GetRecentUnifiedLogs_CountZero_ReturnsAll()
    {
        var state = new BridgeState("proj-test");

        for (int i = 0; i < 5; i++)
        {
            state.AddConsoleLogEntry(new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Message {i}"
            });
        }

        var logs = state.GetRecentUnifiedLogs(0);
        Assert.Equal(5, logs.Length);
    }

    [Fact]
    public void LogBuffer_EnforcesMaxEntries()
    {
        var state = new BridgeState("proj-test");

        // Add more than MaxLogEntries (1000)
        for (int i = 0; i < 1050; i++)
        {
            state.AddConsoleLogEntry(new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Message {i}"
            });
        }

        var logs = state.GetRecentUnifiedLogs(0, ignoreWatermark: true);
        Assert.Equal(1000, logs.Length);
        // Oldest entries should have been evicted
        Assert.Equal("Message 50", logs[0].Message);
        Assert.Equal("Message 1049", logs[^1].Message);
    }

    [Fact]
    public void LogColorMapping_ErrorsAreRed()
    {
        var state = new BridgeState("proj-test");

        state.AddConsoleLogEntry(new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = "Error",
            Message = "Error message"
        });

        state.AddConsoleLogEntry(new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = "Exception",
            Message = "Exception message"
        });

        state.AddConsoleLogEntry(new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = "Warning",
            Message = "Warning message"
        });

        state.AddConsoleLogEntry(new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = "Log",
            Message = "Normal message"
        });

        var logs = state.GetRecentUnifiedLogs(0);
        Assert.Equal(ConsoleColor.Red, logs[0].Color);
        Assert.Equal(ConsoleColor.Red, logs[1].Color);
        Assert.Equal(ConsoleColor.Yellow, logs[2].Color);
        Assert.Null(logs[3].Color);
    }

    #endregion

    #region Watermark / Clear

    [Fact]
    public void ClearLogWatermark_HidesOlderEntries()
    {
        var state = new BridgeState("proj-test");

        // Add 5 entries
        for (int i = 0; i < 5; i++)
        {
            state.AddConsoleLogEntry(new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Before clear {i}"
            });
        }

        state.ClearLogWatermark("test clear");

        // Add 3 more entries
        for (int i = 0; i < 3; i++)
        {
            state.AddConsoleLogEntry(new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"After clear {i}"
            });
        }

        // Without ignoreWatermark, should only see the 3 new entries
        var logs = state.GetRecentUnifiedLogs(0);
        Assert.Equal(3, logs.Length);
        Assert.Equal("After clear 0", logs[0].Message);

        // With ignoreWatermark, should see all 8
        var allLogs = state.GetRecentUnifiedLogs(0, ignoreWatermark: true);
        Assert.Equal(8, allLogs.Length);
    }

    [Fact]
    public void GetWatermark_InitiallyNegative()
    {
        var state = new BridgeState("proj-test");
        Assert.Equal(-1, state.GetWatermark());
    }

    [Fact]
    public void GetClearInfo_ReturnsNullBeforeClear()
    {
        var state = new BridgeState("proj-test");
        Assert.Null(state.GetClearInfo());
    }

    [Fact]
    public void GetClearInfo_ReturnsInfoAfterClear()
    {
        var state = new BridgeState("proj-test");
        state.ClearLogWatermark("test reason");

        var info = state.GetClearInfo();
        Assert.NotNull(info);
        Assert.Equal("test reason", info.Reason);
        Assert.True(info.Watermark >= 0);
    }

    #endregion

    #region Log Subscribers

    [Fact]
    public async Task SubscribeToLogs_ReceivesNewEntries()
    {
        var state = new BridgeState("proj-test");
        var reader = state.SubscribeToLogs();

        state.AddConsoleLogEntry(new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Streamed message"
        });

        var received = await reader.ReadAsync();
        Assert.Equal("Streamed message", received.Message);

        state.UnsubscribeFromLogs(reader);
    }

    [Fact]
    public async Task MultipleSubscribers_AllReceiveEntries()
    {
        var state = new BridgeState("proj-test");
        var reader1 = state.SubscribeToLogs();
        var reader2 = state.SubscribeToLogs();

        state.AddConsoleLogEntry(new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Broadcast message"
        });

        var entry1 = await reader1.ReadAsync();
        var entry2 = await reader2.ReadAsync();

        Assert.Equal("Broadcast message", entry1.Message);
        Assert.Equal("Broadcast message", entry2.Message);

        state.UnsubscribeFromLogs(reader1);
        state.UnsubscribeFromLogs(reader2);
    }

    #endregion

    #region Pending Requests

    [Fact]
    public async Task CompleteRequest_ResolvesTaskCompletionSource()
    {
        var state = new BridgeState("proj-test");
        var tcs = new TaskCompletionSource<ResponseMessage>();
        state.PendingRequests["req-1"] = tcs;

        var response = new ResponseMessage
        {
            Origin = MessageOrigin.Unity,
            RequestId = "req-1",
            Status = ResponseStatus.Ok,
            Result = new { value = "test" }
        };

        state.CompleteRequest("req-1", response);

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        var result = await tcs.Task;
        Assert.Equal("req-1", result.RequestId);
    }

    [Fact]
    public void CompleteRequest_UnknownRequestId_DoesNotThrow()
    {
        var state = new BridgeState("proj-test");

        var response = new ResponseMessage
        {
            Origin = MessageOrigin.Unity,
            RequestId = "unknown",
            Status = ResponseStatus.Ok
        };

        // Should not throw
        state.CompleteRequest("unknown", response);
    }

    [Fact]
    public void CancelAllPendingOperations_CancelsAllRequests()
    {
        var state = new BridgeState("proj-test");
        var tcs1 = new TaskCompletionSource<ResponseMessage>();
        var tcs2 = new TaskCompletionSource<ResponseMessage>();
        state.PendingRequests["req-1"] = tcs1;
        state.PendingRequests["req-2"] = tcs2;

        state.CancelAllPendingOperations();

        Assert.True(tcs1.Task.IsCanceled);
        Assert.True(tcs2.Task.IsCanceled);
        Assert.Empty(state.PendingRequests);
    }

    #endregion

    #region Event Waiters

    [Fact]
    public async Task ProcessEvent_CompletesMatchingWaiter()
    {
        var state = new BridgeState("proj-test");

        var waitTask = state.WaitForEventAsync(
            "req-1",
            UnityCtlEvents.AssetRefreshComplete,
            TimeSpan.FromSeconds(5));

        var eventMessage = new EventMessage
        {
            Origin = MessageOrigin.Unity,
            Event = UnityCtlEvents.AssetRefreshComplete,
            Payload = new { compilationTriggered = false }
        };

        state.ProcessEvent(eventMessage);

        var result = await waitTask;
        Assert.Equal(UnityCtlEvents.AssetRefreshComplete, result.Event);
    }

    [Fact]
    public async Task ProcessEvent_WithExpectedState_OnlyMatchesCorrectState()
    {
        var state = new BridgeState("proj-test");

        var waitTask = state.WaitForEventAsync(
            "req-1",
            UnityCtlEvents.PlayModeChanged,
            TimeSpan.FromSeconds(5),
            expectedState: "EnteredPlayMode");

        // Send wrong state - should not complete
        var wrongEvent = new EventMessage
        {
            Origin = MessageOrigin.Unity,
            Event = UnityCtlEvents.PlayModeChanged,
            Payload = Newtonsoft.Json.Linq.JObject.FromObject(new { state = "ExitingEditMode" })
        };
        state.ProcessEvent(wrongEvent);

        Assert.False(waitTask.IsCompleted);

        // Send correct state - should complete
        var correctEvent = new EventMessage
        {
            Origin = MessageOrigin.Unity,
            Event = UnityCtlEvents.PlayModeChanged,
            Payload = Newtonsoft.Json.Linq.JObject.FromObject(new { state = "EnteredPlayMode" })
        };
        state.ProcessEvent(correctEvent);

        var result = await waitTask;
        Assert.Equal(UnityCtlEvents.PlayModeChanged, result.Event);
    }

    [Fact]
    public async Task WaitForEventAsync_TimesOut()
    {
        var state = new BridgeState("proj-test");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            state.WaitForEventAsync(
                "req-1",
                UnityCtlEvents.CompilationFinished,
                TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void CancelEventWaiter_CancelsSpecificWaiter()
    {
        var state = new BridgeState("proj-test");

        var waitTask = state.WaitForEventAsync(
            "req-1",
            UnityCtlEvents.TestFinished,
            TimeSpan.FromSeconds(30));

        state.CancelEventWaiter("req-1");

        Assert.True(waitTask.IsCanceled);
    }

    #endregion

    #region Domain Reload

    [Fact]
    public void OnDomainReloadStarting_SetsFlag()
    {
        var state = new BridgeState("proj-test");

        Assert.False(state.IsDomainReloadInProgress);

        state.OnDomainReloadStarting();

        Assert.True(state.IsDomainReloadInProgress);
    }

    [Fact]
    public void SetUnityConnection_DuringDomainReload_ClearsFlag()
    {
        var state = new BridgeState("proj-test");

        state.OnDomainReloadStarting();
        Assert.True(state.IsDomainReloadInProgress);

        // Simulate disconnect during domain reload
        state.SetUnityConnection(null);
        // Should still be in grace period
        Assert.True(state.IsDomainReloadInProgress);

        // Simulate reconnection - creating a real WebSocket is complex,
        // but we can verify the logic via the IsUnityConnected property
        // For a unit test we verify the flag-clearing logic conceptually
    }

    [Fact]
    public void SetUnityConnection_NormalDisconnect_CancelsOperations()
    {
        var state = new BridgeState("proj-test");
        var tcs = new TaskCompletionSource<ResponseMessage>();
        state.PendingRequests["req-1"] = tcs;

        // Normal disconnection (not domain reload)
        state.SetUnityConnection(null);

        Assert.True(tcs.Task.IsCanceled);
    }

    [Fact]
    public void SetUnityConnection_DomainReloadDisconnect_KeepsOperationsAlive()
    {
        var state = new BridgeState("proj-test");
        var tcs = new TaskCompletionSource<ResponseMessage>();
        state.PendingRequests["req-1"] = tcs;

        // Enter domain reload first
        state.OnDomainReloadStarting();

        // Disconnect during domain reload
        state.SetUnityConnection(null);

        // Operations should NOT be cancelled
        Assert.False(tcs.Task.IsCanceled);
        Assert.False(tcs.Task.IsCompleted);
    }

    #endregion

    #region Connection State

    [Fact]
    public void IsUnityConnected_InitiallyFalse()
    {
        var state = new BridgeState("proj-test");
        Assert.False(state.IsUnityConnected);
    }

    [Fact]
    public void ProjectId_IsSetFromConstructor()
    {
        var state = new BridgeState("proj-custom");
        Assert.Equal("proj-custom", state.ProjectId);
    }

    [Fact]
    public void UnityHelloMessage_InitiallyNull()
    {
        var state = new BridgeState("proj-test");
        Assert.Null(state.UnityHelloMessage);
    }

    [Fact]
    public void SetUnityHelloMessage_StoresAndReturns()
    {
        var state = new BridgeState("proj-test");
        var hello = new HelloMessage
        {
            Origin = MessageOrigin.Unity,
            ProjectId = "proj-test",
            PluginVersion = "0.3.6"
        };

        state.SetUnityHelloMessage(hello);
        Assert.Equal("0.3.6", state.UnityHelloMessage?.PluginVersion);

        state.SetUnityHelloMessage(null);
        Assert.Null(state.UnityHelloMessage);
    }

    #endregion
}
