using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the Bridge /health endpoint and related status queries.
/// </summary>
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Configure play.status and scene.list handlers for status-related tests
        _fixture.FakeUnity
            .OnCommand(UnityCtlCommands.PlayStatus, _ =>
                new PlayModeResult { State = PlayModeState.Stopped })
            .OnCommand(UnityCtlCommands.SceneList, req =>
            {
                var source = req.Args?["source"]?.ToString() ?? "buildSettings";
                if (source == "loaded")
                {
                    return new SceneListResult
                    {
                        Scenes = new[]
                        {
                            new SceneInfo { Path = "Assets/Scenes/SampleScene.unity", Name = "SampleScene", IsActive = true }
                        }
                    };
                }
                return new SceneListResult { Scenes = Array.Empty<SceneInfo>() };
            });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Health_ReturnsOk_WhenUnityConnected()
    {
        // EditorReady is set asynchronously after hello handshake via editor.ping probe.
        // FakeUnity auto-responds OK to unknown commands, so the probe completes quickly.
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsEditorReady,
            timeout: TimeSpan.FromSeconds(10));

        var response = await _fixture.HttpClient.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var health = JsonHelper.Deserialize<HealthResult>(json);

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(_fixture.ProjectId, health.ProjectId);
        Assert.True(health.UnityConnected);
        Assert.True(health.EditorReady);
        Assert.NotNull(health.BridgeVersion);
        Assert.Equal("0.3.6", health.UnityPluginVersion);
    }

    [Fact]
    public async Task Health_ShowsDisconnected_AfterUnityDisconnects()
    {
        // Disconnect fake Unity
        await _fixture.FakeUnity.DisconnectAsync();
        await Task.Delay(100); // Give bridge time to detect disconnect

        var response = await _fixture.HttpClient.GetAsync("/health");
        var json = await response.Content.ReadAsStringAsync();
        var health = JsonHelper.Deserialize<HealthResult>(json);

        Assert.NotNull(health);
        Assert.False(health.UnityConnected);
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
    public async Task SceneListLoaded_ReturnsLoadedScenes()
    {
        var args = new Dictionary<string, object?> { ["source"] = "loaded" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneList, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        var scenes = result["scenes"] as JArray;
        Assert.NotNull(scenes);
        Assert.Single(scenes);
        Assert.Equal("SampleScene", scenes[0]?["name"]?.ToString());
        Assert.True(scenes[0]?["isActive"]?.Value<bool>());
    }
}
