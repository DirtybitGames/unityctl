using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for domain reload resilience. When Unity recompiles scripts,
/// the WebSocket connection drops and reconnects. The Bridge should
/// handle this gracefully, keeping operations alive during the grace period.
/// </summary>
public class DomainReloadTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task DomainReloadEvent_KeepsOperationsAlive()
    {
        // Configure a command with a long delay so the request stays pending
        _fixture.FakeUnity.OnCommandWithDelay(UnityCtlCommands.SceneList,
            TimeSpan.FromSeconds(30), _ => new SceneListResult
            {
                Scenes = new[] { new SceneInfo { Path = "Assets/Scenes/Main.unity", EnabledInBuild = true } }
            });

        // Start a real RPC request that will be in-flight
        var rpcTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneList);

        // Wait for the request to arrive at FakeUnity (so it's truly in-flight)
        await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.SceneList);

        // Send domain reload starting event
        await _fixture.FakeUnity.SendEventAsync(
            UnityCtlEvents.DomainReloadStarting, new { });
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress);

        // Disconnect (simulates Unity destroying its objects)
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        // The real in-flight RPC should NOT be completed (grace period keeps it alive)
        Assert.False(rpcTask.IsCompleted);
    }

    [Fact]
    public async Task DomainReload_Reconnect_ClearsGraceFlag()
    {
        // Enter domain reload
        await _fixture.FakeUnity.SendEventAsync(
            UnityCtlEvents.DomainReloadStarting, new { });
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress);

        // Disconnect
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        Assert.True(_fixture.BridgeState.IsDomainReloadInProgress);

        // Reconnect with a new FakeUnity
        var newFakeUnity = _fixture.CreateFakeUnity();

        // Register handlers before connecting
        newFakeUnity.OnCommand(UnityCtlCommands.SceneList, _ =>
            new SceneListResult { Scenes = Array.Empty<SceneInfo>() });

        await newFakeUnity.ConnectAsync(_fixture.BaseUri);
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsUnityConnected);

        Assert.False(_fixture.BridgeState.IsDomainReloadInProgress);

        // Bridge should work normally after reconnection
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneList);
        AssertExtensions.IsOk(response);

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task NormalDisconnect_CancelsPendingRequests()
    {
        // Configure a command with a long delay so the request stays pending
        _fixture.FakeUnity.OnCommandWithDelay(UnityCtlCommands.SceneList,
            TimeSpan.FromSeconds(30), _ => new SceneListResult
            {
                Scenes = Array.Empty<SceneInfo>()
            });

        // Start a real RPC request that will be in-flight
        var rpcTask = _fixture.SendRpcAsync(UnityCtlCommands.SceneList);

        // Wait for the request to arrive at FakeUnity
        await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.SceneList);

        // Disconnect WITHOUT domain reload event
        await _fixture.FakeUnity.DisconnectAsync();

        // The HTTP response should complete (bridge returns error when Unity disconnects)
        await AssertExtensions.WaitUntilAsync(() => rpcTask.IsCompleted,
            message: "RPC should complete after normal disconnect cancels pending operations");
    }

    [Fact]
    public async Task DomainReload_LogBufferPreserved()
    {
        // Add logs before domain reload
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Before reload"
        });
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.GetRecentUnifiedLogs(0, ignoreWatermark: true).Length > 0);

        // Domain reload: disconnect and reconnect
        await _fixture.FakeUnity.SendEventAsync(
            UnityCtlEvents.DomainReloadStarting, new { });
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress);
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        // Reconnect
        var newFakeUnity = _fixture.CreateFakeUnity();
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsUnityConnected);

        // Logs should still be available
        var logs = _fixture.BridgeState.GetRecentUnifiedLogs(10, ignoreWatermark: true);
        Assert.Contains(logs, l => l.Message == "Before reload");

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task Rpc_DuringDomainReload_WaitsForReconnection()
    {
        // Trigger domain reload and disconnect
        await _fixture.FakeUnity.SendEventAsync(
            UnityCtlEvents.DomainReloadStarting, new { });
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress);
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        // Send RPC while Unity is disconnected — should NOT get 503 immediately.
        // The bridge's /rpc handler is now blocked on the domain reload TCS,
        // so once we confirm the state, the RPC is deterministically waiting.
        var rpcTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneList);
        await Task.Delay(50); // Allow HTTP request to reach the handler
        Assert.False(rpcTask.IsCompleted, "RPC should be waiting for reconnection, not failing with 503");

        // Reconnect with a new FakeUnity
        var newFakeUnity = _fixture.CreateFakeUnity();
        newFakeUnity.OnCommand(UnityCtlCommands.SceneList, _ =>
            new SceneListResult
            {
                Scenes = new[] { new SceneInfo { Path = "Assets/Scenes/Main.unity", EnabledInBuild = true } }
            });
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);

        // RPC should complete successfully now
        var response = await rpcTask;
        AssertExtensions.IsOk(response);

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task PlayModeChanged_EnteredPlayMode_AutoClearsLogs()
    {
        // Add some logs
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Old log"
        });
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.GetRecentUnifiedLogs(0, ignoreWatermark: true).Length > 0);

        // Fire EnteredPlayMode event (should auto-clear logs)
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.PlayModeChanged,
            new { state = "EnteredPlayMode" });
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.GetWatermark() >= 0);

        // New logs should be visible, old ones hidden by watermark
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:01:00Z",
            Level = LogLevel.Log,
            Message = "New log"
        });
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.GetRecentUnifiedLogs(0).Length > 0);

        var logs = _fixture.BridgeState.GetRecentUnifiedLogs(0);
        Assert.Single(logs);
        Assert.Equal("New log", logs[0].Message);
    }
}
