using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the play.enter and play.exit flows, which are the most complex
/// command flows in the Bridge. play.enter involves:
/// 1. Check play status
/// 2. asset.refresh (auto)
/// 3. Wait for compilation if triggered
/// 4. Enter play mode
/// 5. Wait for play mode events
/// </summary>
public class PlayModeFlowTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        ConfigureDefaultHandlers(_fixture.FakeUnity);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static void ConfigureDefaultHandlers(FakeUnityClient fakeUnity)
    {
        // play.status: stopped
        fakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Stopped });

        // asset.refresh: no compilation triggered
        fakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));

        // play.enter: transitioning, then fire two events (ExitingEditMode → EnteredPlayMode)
        fakeUnity.OnCommand(UnityCtlCommands.PlayEnter, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingEditMode" }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(150),
                UnityCtlEvents.PlayModeChanged,
                new { state = "EnteredPlayMode" }));

        // play.exit
        fakeUnity.OnCommand(UnityCtlCommands.PlayExit, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingPlayMode", compilationTriggered = false }));
    }

    [Fact]
    public async Task PlayEnter_FullFlow_ReturnsEnteredPlayMode()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("EnteredPlayMode", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayEnter_PerformsAssetRefreshFirst()
    {
        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        // Verify asset.refresh was received before play.enter
        var refreshReq = _fixture.FakeUnity.ReceivedRequests
            .FirstOrDefault(r => r.Command == UnityCtlCommands.AssetRefresh);
        var playReq = _fixture.FakeUnity.ReceivedRequests
            .FirstOrDefault(r => r.Command == UnityCtlCommands.PlayEnter);

        Assert.NotNull(refreshReq);
        Assert.NotNull(playReq);
        Assert.True(refreshReq.ReceivedAt < playReq.ReceivedAt,
            "asset.refresh should be sent before play.enter");
    }

    [Fact]
    public async Task PlayEnter_AlreadyPlaying_ReturnsImmediately()
    {
        // Override play.status to return "playing"
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("AlreadyPlaying", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayEnter_WithCompilation_WaitsForCompilationFinished()
    {
        // Override asset.refresh to trigger compilation
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = true, hasCompilationErrors = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.CompilationFinished,
                new { success = true, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("EnteredPlayMode", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayEnter_CompilationFails_ReturnsError()
    {
        // Override asset.refresh to trigger compilation that fails
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = true, hasCompilationErrors = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.CompilationFinished,
                new
                {
                    success = false,
                    errors = new[]
                    {
                        new { file = "Assets/Scripts/Foo.cs", line = 10, column = 5, message = "CS1002: ; expected" }
                    },
                    warnings = Array.Empty<object>()
                }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        AssertExtensions.IsError(response, "COMPILATION_ERROR");
    }

    [Fact]
    public async Task PlayEnter_ExistingCompilationErrors_ReturnsError()
    {
        // Override asset.refresh to report existing compilation errors
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = true }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        AssertExtensions.IsError(response, "COMPILATION_ERROR");
        Assert.Contains("compilation errors exist", response.Error!.Message);
    }

    [Fact]
    public async Task PlayExit_SimpleFlow_ReturnsOk()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayExit);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("ExitingPlayMode", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayStatus_ReturnsCurrentState()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("stopped", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayEnter_DomainReloadDuringEntry_RetriesAndSucceeds()
    {
        // Configure play.enter to respond with transitioning but NO follow-up events
        // (simulates: Unity starts transitioning, then domain reload kills the connection
        //  before EnteredPlayMode event can be sent)
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayEnter, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning });

        // Start play.enter RPC (will go through asset.refresh, then enter the retry loop)
        var rpcTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        // Wait for play.enter to be received by FakeUnity
        await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.PlayEnter);

        // Simulate domain reload: disconnect + reconnect with a new FakeUnity
        await _fixture.FakeUnity.SendEventAsync(
            UnityCtlEvents.DomainReloadStarting, new { });
        await Task.Delay(50);
        await _fixture.FakeUnity.DisconnectAsync();
        await Task.Delay(200);

        // Reconnect with new FakeUnity that reports "playing" on status check
        var newFakeUnity = _fixture.CreateFakeUnity();
        newFakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);

        // The Bridge's retry loop should detect "playing" on status re-check
        var response = await rpcTask;

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("EnteredPlayMode", result["state"]?.ToString());

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task PlayEnter_BounceBack_ReturnsPlayModeFailed()
    {
        // Configure play.enter to emit ExitingEditMode then EnteredEditMode (bounce-back)
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayEnter, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingEditMode" }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(150),
                UnityCtlEvents.PlayModeChanged,
                new { state = "EnteredEditMode" }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayEnter);

        Assert.Equal(ResponseStatus.Error, response.Status);
        Assert.NotNull(response.Error);
        Assert.Equal("PLAY_MODE_FAILED", response.Error.Code);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("PlayModeEntryFailed", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayExit_CompilationTriggered_WaitsForCompilationFinished()
    {
        // Real Unity behavior: ExitingPlayMode has compilationTriggered=false,
        // then compilation.started fires quickly, then compilation.finished later
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayExit, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingPlayMode", compilationTriggered = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.CompilationStarted,
                new { }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(300),
                UnityCtlEvents.CompilationFinished,
                new { success = true, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayExit);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.True(result["compilationTriggered"]?.Value<bool>());
        Assert.True(result["compilationSuccess"]?.Value<bool>());
    }

    [Fact]
    public async Task PlayExit_DomainReload_WaitsForReconnection()
    {
        // Reproduce the exact bug from conversation logs: play.exit triggers compilation
        // which triggers domain reload. The next command should NOT get a 503.
        //
        // Real Unity sequence: ExitingPlayMode -> compilation.started -> compilation.finished
        //   -> DomainReloadStarting -> Unity disconnects -> Unity reconnects
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayExit, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingPlayMode", compilationTriggered = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.CompilationStarted,
                new { }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(200),
                UnityCtlEvents.CompilationFinished,
                new { success = true, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(300),
                UnityCtlEvents.DomainReloadStarting,
                new { }));

        // Start play.exit RPC in background
        var rpcTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayExit);

        // Wait for bridge to detect domain reload
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!_fixture.BridgeState.IsDomainReloadInProgress && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(_fixture.BridgeState.IsDomainReloadInProgress);

        // Simulate Unity disconnecting during domain reload
        await _fixture.FakeUnity.DisconnectAsync();
        await Task.Delay(200);

        // play.exit should still be waiting (not returned yet, because Fix B waits for reconnection)
        Assert.False(rpcTask.IsCompleted, "play.exit should wait for domain reload to complete");

        // Reconnect with new FakeUnity
        var newFakeUnity = _fixture.CreateFakeUnity();
        ConfigureDefaultHandlers(newFakeUnity);
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);

        // play.exit should now complete
        var response = await rpcTask;
        AssertExtensions.IsOk(response);

        // The next command should succeed immediately (Unity is connected)
        var nextResponse = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);
        AssertExtensions.IsOk(nextResponse);

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task PlayExit_NoDomainReload_ReturnsWithoutDelay()
    {
        // When play.exit does NOT trigger compilation/domain reload,
        // it should return promptly without waiting for the detection timeout.
        // The default handler has compilationTriggered=false and no DomainReloadStarting.

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayExit);
        sw.Stop();

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("ExitingPlayMode", result["state"]?.ToString());

        // play.exit has an existing 2s CompilationWaitTimeout for detecting late compilations.
        // Our domain reload wait should NOT add latency when compilation wasn't triggered.
        Assert.True(sw.ElapsedMilliseconds < 3500,
            $"play.exit without domain reload took {sw.ElapsedMilliseconds}ms — should be under 3.5s (2s compilation detection + margin)");
    }
}
