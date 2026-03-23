using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for play.pause and play.step commands.
/// play.pause is a pass-through toggle; play.step returns an error when not playing.
/// </summary>
public class PlayPauseStepTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // ── play.pause ──────────────────────────────────────────────────────

    [Fact]
    public async Task PlayPause_WhenPlaying_ReturnsPaused()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayPause, _ =>
            new PlayModeResult { State = PlayModeState.Paused });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayPause);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("paused", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayPause_WhenPaused_ReturnsPlaying()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayPause, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayPause);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("playing", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayPause_InEditMode_ArmsPauseOnPlay()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayPause, _ =>
            new PlayModeResult { State = PlayModeState.Stopped, PauseOnPlay = true });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayPause);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("stopped", result["state"]?.ToString());
        Assert.True(result["pauseOnPlay"]?.Value<bool>());
    }

    [Fact]
    public async Task PlayPause_InEditModeAlreadyArmed_DisarmsPauseOnPlay()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayPause, _ =>
            new PlayModeResult { State = PlayModeState.Stopped });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayPause);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("stopped", result["state"]?.ToString());
        // pauseOnPlay omitted when false (DefaultValueHandling.Ignore)
        Assert.Null(result["pauseOnPlay"]);
    }

    // ── play.step ───────────────────────────────────────────────────────

    [Fact]
    public async Task PlayStep_WhenPaused_ReturnsPaused()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStep, _ =>
            new PlayModeResult { State = PlayModeState.Paused });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStep);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("paused", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayStep_WhenNotPlaying_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.PlayStep,
            "NOT_PLAYING", "Cannot step: not in play mode");

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStep);

        AssertExtensions.IsError(response, "NOT_PLAYING");
    }

    // ── play.status reports paused + pauseOnPlay ────────────────────────

    [Fact]
    public async Task PlayStatus_WhenPaused_ReturnsPaused()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Paused });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("paused", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayStatus_WhenStoppedWithPauseArmed_ReportsPauseOnPlay()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Stopped, PauseOnPlay = true });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("stopped", result["state"]?.ToString());
        Assert.True(result["pauseOnPlay"]?.Value<bool>());
    }
}
