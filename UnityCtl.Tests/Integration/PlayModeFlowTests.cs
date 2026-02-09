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
    }

    [Fact]
    public async Task PlayStatus_ReturnsCurrentState()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("stopped", result["state"]?.ToString());
    }
}
