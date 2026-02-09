using UnityCtl.Protocol;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the Bridge /health endpoint.
/// </summary>
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Health_ReturnsOk_WhenUnityConnected()
    {
        var response = await _fixture.HttpClient.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var health = JsonHelper.Deserialize<HealthResult>(json);

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(_fixture.ProjectId, health.ProjectId);
        Assert.True(health.UnityConnected);
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
}
