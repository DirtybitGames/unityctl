using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the record.start, record.stop, and record.status flows.
/// record.start is complex because it:
/// 1. Checks play status
/// 2. If not playing: asset.refresh + handle compilation + enter play mode
/// 3. Sends record.start to Unity
/// 4. If duration specified: waits for record.finished event
/// </summary>
public class RecordFlowTests : IAsyncLifetime
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
        // play.status: stopped (not in play mode)
        fakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Stopped });

        // asset.refresh: no compilation triggered
        fakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));

        // play.enter: transitioning, then events
        fakeUnity.OnCommand(UnityCtlCommands.PlayEnter, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingEditMode" }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(150),
                UnityCtlEvents.PlayModeChanged,
                new { state = "EnteredPlayMode" }));

        // record.start: returns recording info
        fakeUnity.OnCommand(UnityCtlCommands.RecordStart, _ =>
            new RecordStartResult
            {
                RecordingId = "test-recording-id",
                OutputPath = "Recordings/recording_2026-02-10_14-30-00.mp4",
                State = "recording"
            });

        // record.stop: returns recording result
        fakeUnity.OnCommand(UnityCtlCommands.RecordStop, _ =>
            new RecordStopResult
            {
                OutputPath = "Recordings/recording_2026-02-10_14-30-00.mp4",
                Duration = 5.0,
                FrameCount = 150
            });

        // record.status: not recording
        fakeUnity.OnCommand(UnityCtlCommands.RecordStatus, _ =>
            new RecordStatusResult
            {
                IsRecording = false,
                RecordingId = null,
                OutputPath = null,
                Elapsed = null,
                FrameCount = null
            });
    }

    // --- record.start tests ---

    [Fact]
    public async Task RecordStart_NotInPlayMode_EntersPlayModeFirst()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("recording", result["state"]?.ToString());
        Assert.Equal("Recordings/recording_2026-02-10_14-30-00.mp4", result["outputPath"]?.ToString());

        // Verify the sequence: play.status → asset.refresh → play.enter → record.start
        var requests = _fixture.FakeUnity.ReceivedRequests.ToArray();
        var statusReq = requests.FirstOrDefault(r => r.Command == UnityCtlCommands.PlayStatus);
        var refreshReq = requests.FirstOrDefault(r => r.Command == UnityCtlCommands.AssetRefresh);
        var playReq = requests.FirstOrDefault(r => r.Command == UnityCtlCommands.PlayEnter);
        var recordReq = requests.FirstOrDefault(r => r.Command == UnityCtlCommands.RecordStart);

        Assert.NotNull(statusReq);
        Assert.NotNull(refreshReq);
        Assert.NotNull(playReq);
        Assert.NotNull(recordReq);
        Assert.True(statusReq.ReceivedAt <= refreshReq.ReceivedAt, "play.status before asset.refresh");
        Assert.True(refreshReq.ReceivedAt <= playReq.ReceivedAt, "asset.refresh before play.enter");
        Assert.True(playReq.ReceivedAt <= recordReq.ReceivedAt, "play.enter before record.start");
    }

    [Fact]
    public async Task RecordStart_AlreadyInPlayMode_SkipsPlayModeEntry()
    {
        // Override play.status to return "playing"
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("recording", result["state"]?.ToString());

        // Verify NO asset.refresh or play.enter was sent
        var requests = _fixture.FakeUnity.ReceivedRequests.ToArray();
        Assert.DoesNotContain(requests, r => r.Command == UnityCtlCommands.AssetRefresh);
        Assert.DoesNotContain(requests, r => r.Command == UnityCtlCommands.PlayEnter);
    }

    [Fact]
    public async Task RecordStart_WithDuration_BlocksUntilRecordFinished()
    {
        // Configure record.start to fire record.finished event after delay
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.RecordStart, _ =>
            new RecordStartResult
            {
                RecordingId = "test-recording-id",
                OutputPath = "Recordings/recording.mp4",
                State = "recording"
            },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(200),
                UnityCtlEvents.RecordFinished,
                new RecordFinishedPayload
                {
                    RecordingId = "test-recording-id",
                    OutputPath = "Recordings/recording.mp4",
                    Duration = 2.0,
                    FrameCount = 60
                }));

        // Override play.status to already playing (skip play mode entry)
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        var args = new Dictionary<string, object?> { { "duration", 2.0 } };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);

        // When duration is specified, the response should contain the finished payload
        Assert.Equal("test-recording-id", result["recordingId"]?.ToString());
        Assert.Equal("Recordings/recording.mp4", result["outputPath"]?.ToString());
        Assert.Equal(2.0, result["duration"]?.Value<double>());
        Assert.Equal(60, result["frameCount"]?.Value<int>());
    }

    [Fact]
    public async Task RecordStart_WithoutDuration_ReturnsImmediately()
    {
        // Override play.status to already playing
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);

        // Without duration, should return the immediate start result
        Assert.Equal("recording", result["state"]?.ToString());
        Assert.Equal("test-recording-id", result["recordingId"]?.ToString());
    }

    [Fact]
    public async Task RecordStart_CompilationErrors_ReturnsError()
    {
        // Override asset.refresh to report existing compilation errors
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = true }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsError(response, "COMPILATION_ERROR");
        Assert.Contains("compilation errors exist", response.Error!.Message);
    }

    [Fact]
    public async Task RecordStart_CompilationFails_ReturnsError()
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

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsError(response, "COMPILATION_ERROR");
        var result = AssertExtensions.GetResultJObject(response);
        var errors = result["errors"]?.ToObject<CompilationMessageInfo[]>();
        Assert.NotNull(errors);
        Assert.Single(errors);
    }

    [Fact]
    public async Task RecordStart_WithCompilation_WaitsAndEntersPlayMode()
    {
        // Override asset.refresh to trigger compilation that succeeds
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = true, hasCompilationErrors = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.CompilationFinished,
                new { success = true, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("recording", result["state"]?.ToString());
    }

    [Fact]
    public async Task RecordStart_PlayModeBounceBack_ReturnsPlayModeFailed()
    {
        // Configure play.enter to bounce back (ExitingEditMode → EnteredEditMode)
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayEnter, _ =>
            new PlayModeResult { State = PlayModeState.Transitioning },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.PlayModeChanged,
                new { state = "ExitingEditMode" }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(150),
                UnityCtlEvents.PlayModeChanged,
                new { state = "EnteredEditMode" }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsError(response, "PLAY_MODE_FAILED");
    }

    [Fact]
    public async Task RecordStart_UnityReturnsError_PropagatesError()
    {
        // Override play.status to already playing
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        // Override record.start to return error
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.RecordStart,
            "RECORDER_NOT_INSTALLED",
            "Unity Recorder package not found. Install com.unity.recorder via Package Manager.");

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsError(response, "RECORDER_NOT_INSTALLED");
    }

    [Fact]
    public async Task RecordStart_PassesArgsToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        var args = new Dictionary<string, object?>
        {
            { "outputName", "my-test" },
            { "fps", 60 },
            { "width", 1920 },
            { "height", 1080 }
        };

        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart, args);

        var recordReq = _fixture.FakeUnity.ReceivedRequests
            .FirstOrDefault(r => r.Command == UnityCtlCommands.RecordStart);
        Assert.NotNull(recordReq);

        // Verify args were passed through
        var reqArgs = recordReq.Args;
        Assert.NotNull(reqArgs);
        Assert.Equal("my-test", reqArgs["outputName"]?.ToString());
    }

    [Fact]
    public async Task RecordStart_AlreadyRecording_ReturnsError()
    {
        // Override play.status to already playing
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        // Override record.start to return error (already recording)
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.RecordStart,
            "ALREADY_RECORDING", "Recording already in progress. Stop the current recording first.");

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);

        AssertExtensions.IsError(response, "ALREADY_RECORDING");
        Assert.Contains("already in progress", response.Error!.Message);
    }

    [Fact]
    public async Task RecordStart_WithDuration_FromEditMode_FullFlow()
    {
        // Full flow: edit mode → asset refresh → play mode → record with duration → finish
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.RecordStart, _ =>
            new RecordStartResult
            {
                RecordingId = "full-flow-id",
                OutputPath = "Recordings/full-flow.mp4",
                State = "recording"
            },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(200),
                UnityCtlEvents.RecordFinished,
                new RecordFinishedPayload
                {
                    RecordingId = "full-flow-id",
                    OutputPath = "Recordings/full-flow.mp4",
                    Duration = 5.0,
                    FrameCount = 150
                }));

        var args = new Dictionary<string, object?> { { "duration", 5.0 } };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);

        // Should return the finished payload (not the start result)
        Assert.Equal("full-flow-id", result["recordingId"]?.ToString());
        Assert.Equal(5.0, result["duration"]?.Value<double>());
        Assert.Equal(150, result["frameCount"]?.Value<int>());

        // Verify the full sequence: play.status → asset.refresh → play.enter → record.start
        var requests = _fixture.FakeUnity.ReceivedRequests.ToArray();
        Assert.Contains(requests, r => r.Command == UnityCtlCommands.PlayStatus);
        Assert.Contains(requests, r => r.Command == UnityCtlCommands.AssetRefresh);
        Assert.Contains(requests, r => r.Command == UnityCtlCommands.PlayEnter);
        Assert.Contains(requests, r => r.Command == UnityCtlCommands.RecordStart);
    }

    [Fact]
    public async Task RecordStart_PassesDurationArgToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        _fixture.FakeUnity.OnCommand(UnityCtlCommands.RecordStart, _ =>
            new RecordStartResult
            {
                RecordingId = "duration-arg-test",
                OutputPath = "Recordings/test.mp4",
                State = "recording"
            },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.RecordFinished,
                new RecordFinishedPayload
                {
                    RecordingId = "duration-arg-test",
                    OutputPath = "Recordings/test.mp4",
                    Duration = 10.0,
                    FrameCount = 300
                }));

        var args = new Dictionary<string, object?> { { "duration", 10.0 } };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart, args);

        AssertExtensions.IsOk(response);

        // Verify duration was forwarded to Unity
        var recordReq = _fixture.FakeUnity.ReceivedRequests
            .FirstOrDefault(r => r.Command == UnityCtlCommands.RecordStart);
        Assert.NotNull(recordReq);
        var reqArgs = recordReq.Args;
        Assert.NotNull(reqArgs);

        // Duration should be present in the args
        var durationVal = reqArgs["duration"];
        Assert.NotNull(durationVal);
        Assert.Equal(10.0, Convert.ToDouble(durationVal.ToString()));
    }

    [Fact]
    public async Task RecordStart_DirectlyEntersPlayMode_WhenAlreadyInPlayMode()
    {
        // When play.status returns "playing", the second play.status check in
        // the play enter loop should break immediately
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Playing });

        var startTime = DateTime.UtcNow;
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStart);
        var elapsed = DateTime.UtcNow - startTime;

        AssertExtensions.IsOk(response);

        // Should complete quickly (no play mode entry delay)
        Assert.True(elapsed.TotalSeconds < 5, $"Should complete quickly, took {elapsed.TotalSeconds}s");

        // Should only have play.status + record.start (no play.enter, no asset.refresh)
        var requests = _fixture.FakeUnity.ReceivedRequests.ToArray();
        Assert.Single(requests, r => r.Command == UnityCtlCommands.PlayStatus);
        Assert.Single(requests, r => r.Command == UnityCtlCommands.RecordStart);
        Assert.DoesNotContain(requests, r => r.Command == UnityCtlCommands.PlayEnter);
    }

    // --- record.stop tests ---

    [Fact]
    public async Task RecordStop_ReturnsRecordingDetails()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStop);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Recordings/recording_2026-02-10_14-30-00.mp4", result["outputPath"]?.ToString());
        Assert.Equal(5.0, result["duration"]?.Value<double>());
        Assert.Equal(150, result["frameCount"]?.Value<int>());
    }

    [Fact]
    public async Task RecordStop_NotRecording_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.RecordStop,
            "NOT_RECORDING", "No recording in progress.");

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStop);

        AssertExtensions.IsError(response, "NOT_RECORDING");
    }

    // --- record.status tests ---

    [Fact]
    public async Task RecordStatus_NotRecording_ReturnsIsRecordingFalse()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStatus);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.False(result["isRecording"]?.Value<bool>());
        Assert.Null(result["recordingId"]?.Value<string>());
    }

    [Fact]
    public async Task RecordStop_ReturnsAllExpectedFields()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.RecordStop, _ =>
            new RecordStopResult
            {
                OutputPath = "Recordings/custom-name.mp4",
                Duration = 12.5,
                FrameCount = 375
            });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStop);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);

        // Verify all expected fields are present
        Assert.NotNull(result["outputPath"]);
        Assert.NotNull(result["duration"]);
        Assert.NotNull(result["frameCount"]);
        Assert.Equal("Recordings/custom-name.mp4", result["outputPath"]?.ToString());
        Assert.Equal(12.5, result["duration"]?.Value<double>());
        Assert.Equal(375, result["frameCount"]?.Value<int>());
    }

    [Fact]
    public async Task RecordStatus_NotRecording_NullOptionalFields()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStatus);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.False(result["isRecording"]?.Value<bool>());

        // Optional fields should be null/absent when not recording
        Assert.True(
            result["recordingId"] == null || result["recordingId"]!.Type == JTokenType.Null,
            "recordingId should be null when not recording");
        Assert.True(
            result["outputPath"] == null || result["outputPath"]!.Type == JTokenType.Null,
            "outputPath should be null when not recording");
    }

    [Fact]
    public async Task RecordStatus_WhileRecording_ReturnsDetails()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.RecordStatus, _ =>
            new RecordStatusResult
            {
                IsRecording = true,
                RecordingId = "test-recording-id",
                OutputPath = "Recordings/test.mp4",
                Elapsed = 3.5,
                FrameCount = 105
            });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.RecordStatus);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.True(result["isRecording"]?.Value<bool>());
        Assert.Equal("test-recording-id", result["recordingId"]?.ToString());
        Assert.Equal("Recordings/test.mp4", result["outputPath"]?.ToString());
        Assert.Equal(3.5, result["elapsed"]?.Value<double>());
        Assert.Equal(105, result["frameCount"]?.Value<int>());
    }
}
