using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for command retry after domain reload. When a command is in-flight
/// and domain reload kills the WebSocket connection, the bridge should retry
/// the command after Unity reconnects instead of timing out.
/// </summary>
public class DomainReloadRetryTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Command_DomainReloadDuringExecution_RetriesAfterReconnection()
    {
        // Simulate: command is sent to Unity but domain reload kills the connection
        // before Unity can respond. The bridge should retry after reconnection.

        // Configure asset.refresh with a very long delay (simulates main thread
        // blocked by compilation - command never gets processed before domain reload)
        _fixture.FakeUnity.OnCommandWithDelay(UnityCtlCommands.AssetRefresh,
            TimeSpan.FromSeconds(60),
            _ => new { started = true, compilationTriggered = false, hasCompilationErrors = false },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));

        // Start asset.refresh in background (using raw HTTP to inspect response)
        var rpcTask = _fixture.SendRpcAsync(UnityCtlCommands.AssetRefresh);

        // Wait for asset.refresh to arrive at FakeUnity
        await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.AssetRefresh);

        // Simulate domain reload: event → disconnect → reconnect
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.DomainReloadStarting, new { });
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress);
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        // Request should still be pending (not timed out yet)
        Assert.False(rpcTask.IsCompleted, "RPC should be pending during domain reload");

        // Reconnect with new FakeUnity that responds to asset.refresh normally
        var newFakeUnity = _fixture.CreateFakeUnity();
        newFakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { started = true, compilationTriggered = false, hasCompilationErrors = false },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);

        // RPC should succeed after retry on the new connection
        var httpResponse = await rpcTask;
        var body = await httpResponse.Content.ReadAsStringAsync();
        Assert.True(httpResponse.IsSuccessStatusCode,
            $"Expected 2xx but got {(int)httpResponse.StatusCode}: {body}");

        var response = JsonHelper.Deserialize<ResponseMessage>(body)!;
        AssertExtensions.IsOk(response);

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task PlayExitThenAssetRefresh_LateCompilationCausesDomainReload_AssetRefreshSucceeds()
    {
        // Scenario: play.exit completes, but late compilation triggers domain reload.
        // asset.refresh arrives and gets sent to Unity, but domain reload kills the
        // connection before Unity responds. Bridge should retry asset.refresh.

        // Configure simple play.exit (no compilation detected within 2s window)
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayExit, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingPlayMode", compilationTriggered = false }));

        // play.exit returns
        var playExitResponse = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayExit);
        AssertExtensions.IsOk(playExitResponse);

        // Now configure asset.refresh with long delay (main thread blocked by late compilation)
        _fixture.FakeUnity.OnCommandWithDelay(UnityCtlCommands.AssetRefresh,
            TimeSpan.FromSeconds(60),
            _ => new { started = true, compilationTriggered = false, hasCompilationErrors = false },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));

        // Start asset.refresh
        var refreshTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        // Wait for it to arrive at FakeUnity
        await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.AssetRefresh);

        // Late domain reload from play mode exit compilation
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.DomainReloadStarting, new { });
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress);
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        Assert.False(refreshTask.IsCompleted, "asset.refresh should be pending during domain reload");

        // Reconnect
        var newFakeUnity = _fixture.CreateFakeUnity();
        newFakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { started = true, compilationTriggered = false, hasCompilationErrors = false },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);

        // asset.refresh should succeed after retry
        var response = await refreshTask;
        AssertExtensions.IsOk(response);

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task Command_DomainReloadDuringEventWait_RetriesAfterReconnection()
    {
        // Command response received, but domain reload during event wait.
        // Bridge should retry.

        // Configure asset.refresh: responds immediately but event waiter delayed
        // (simulates domain reload happening between response and event)
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { started = true, compilationTriggered = false, hasCompilationErrors = false });
        // Note: no AssetRefreshComplete event scheduled — we'll trigger domain reload instead

        // Start asset.refresh
        var rpcTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        // Wait for it to arrive
        await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.AssetRefresh);

        // Give bridge time to receive the response (but NOT the event)
        await Task.Delay(200);

        // Domain reload before AssetRefreshComplete event arrives
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.DomainReloadStarting, new { });
        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress);
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        Assert.False(rpcTask.IsCompleted, "RPC should be waiting for event");

        // Reconnect with full handler
        var newFakeUnity = _fixture.CreateFakeUnity();
        newFakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { started = true, compilationTriggered = false, hasCompilationErrors = false },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);

        // Should succeed after retry
        var response = await rpcTask;
        AssertExtensions.IsOk(response);

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task Command_NormalDisconnect_DoesNotRetry()
    {
        // When Unity disconnects without domain reload, commands should fail, not retry.

        _fixture.FakeUnity.OnCommandWithDelay(UnityCtlCommands.SceneList,
            TimeSpan.FromSeconds(60),
            _ => new SceneListResult { Scenes = Array.Empty<SceneInfo>() });

        var rpcTask = _fixture.SendRpcAsync(UnityCtlCommands.SceneList);
        await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.SceneList);

        // Normal disconnect (no domain reload event)
        await _fixture.FakeUnity.DisconnectAsync();

        // Should complete with error (not hang)
        var response = await rpcTask;
        Assert.False(response.IsSuccessStatusCode);
    }
}
