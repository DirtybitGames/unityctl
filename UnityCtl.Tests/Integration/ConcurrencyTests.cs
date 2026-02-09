using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for concurrent request handling. Multiple CLI clients may send
/// requests to the same Bridge simultaneously.
/// </summary>
public class ConcurrencyTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.FakeUnity
            .OnCommand(UnityCtlCommands.SceneList, _ =>
                new SceneListResult
                {
                    Scenes = new[] { new SceneInfo { Path = "Assets/Scenes/Main.unity", EnabledInBuild = true } }
                })
            .OnCommand(UnityCtlCommands.PlayStatus, _ =>
                new PlayModeResult { State = PlayModeState.Stopped })
            .OnCommandWithDelay(UnityCtlCommands.ScreenshotCapture,
                TimeSpan.FromMilliseconds(100),
                _ => new ScreenshotCaptureResult { Path = "/tmp/shot.png", Width = 1920, Height = 1080 });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ConcurrentRequests_AllReturnCorrectly()
    {
        // Send 10 concurrent requests
        var tasks = Enumerable.Range(0, 10).Select(i =>
            _fixture.SendRpcAndParseAsync(
                UnityCtlCommands.SceneList,
                agentId: $"agent-{i}"));

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => AssertExtensions.IsOk(r));
        Assert.Equal(10, responses.Length);
    }

    [Fact]
    public async Task ConcurrentDifferentCommands_EachResolvedCorrectly()
    {
        var sceneTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneList);
        var statusTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);
        var screenshotTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScreenshotCapture);

        await Task.WhenAll(sceneTask, statusTask, screenshotTask);

        var sceneResult = AssertExtensions.GetResultJObject(sceneTask.Result);
        Assert.NotNull(sceneResult["scenes"]);

        var statusResult = AssertExtensions.GetResultJObject(statusTask.Result);
        Assert.Equal("stopped", statusResult["state"]?.ToString());

        var shotResult = AssertExtensions.GetResultJObject(screenshotTask.Result);
        Assert.Equal("/tmp/shot.png", shotResult["path"]?.ToString());
    }

    [Fact]
    public async Task MultipleLogSubscribers_AllReceiveEvents()
    {
        var reader1 = _fixture.BridgeState.SubscribeToLogs();
        var reader2 = _fixture.BridgeState.SubscribeToLogs();
        var reader3 = _fixture.BridgeState.SubscribeToLogs();

        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Broadcast"
        });

        var entry1 = await reader1.ReadAsync();
        var entry2 = await reader2.ReadAsync();
        var entry3 = await reader3.ReadAsync();

        Assert.Equal("Broadcast", entry1.Message);
        Assert.Equal("Broadcast", entry2.Message);
        Assert.Equal("Broadcast", entry3.Message);

        _fixture.BridgeState.UnsubscribeFromLogs(reader1);
        _fixture.BridgeState.UnsubscribeFromLogs(reader2);
        _fixture.BridgeState.UnsubscribeFromLogs(reader3);
    }
}
