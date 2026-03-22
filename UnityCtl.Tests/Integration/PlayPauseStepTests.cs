using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for play.pause and play.step commands.
/// Both are simple pass-through commands that return immediately.
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
    public async Task PlayPause_WhenNotPlaying_ReturnsNotPlaying()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayPause, _ =>
            new PlayModeResult { State = "NotPlaying" });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayPause);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("NotPlaying", result["state"]?.ToString());
    }

    // ── play.step ───────────────────────────────────────────────────────

    [Fact]
    public async Task PlayStep_WhenPlaying_ReturnsStepped()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStep, _ =>
            new PlayModeResult { State = "stepped" });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStep);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("stepped", result["state"]?.ToString());
    }

    [Fact]
    public async Task PlayStep_WhenNotPlaying_ReturnsNotPlaying()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStep, _ =>
            new PlayModeResult { State = "NotPlaying" });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStep);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("NotPlaying", result["state"]?.ToString());
    }

    // ── play.status reports paused ──────────────────────────────────────

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
}
