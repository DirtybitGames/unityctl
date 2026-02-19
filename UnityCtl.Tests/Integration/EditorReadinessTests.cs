using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the editor readiness probe (editor.ping) that verifies Unity's main thread
/// is responsive before reporting the editor as ready.
/// </summary>
public class EditorReadinessTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task EditorReady_BecomesTrue_AfterPingSucceeds()
    {
        // FakeUnity's default handler responds OK to editor.ping,
        // so the readiness probe should succeed shortly after connection.
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsEditorReady,
            timeout: TimeSpan.FromSeconds(10),
            message: "Editor should become ready after successful ping");
    }

    [Fact]
    public async Task HealthEndpoint_IncludesEditorReady()
    {
        // Wait for readiness probe to complete
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsEditorReady,
            timeout: TimeSpan.FromSeconds(10));

        var response = await _fixture.HttpClient.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var health = JsonHelper.Deserialize<HealthResult>(json);

        Assert.NotNull(health);
        Assert.True(health.UnityConnected);
        Assert.True(health.EditorReady);
    }

    [Fact]
    public async Task EditorReady_ResetToFalse_AfterDisconnect()
    {
        // Wait for readiness
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsEditorReady,
            timeout: TimeSpan.FromSeconds(10));

        // Disconnect
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(
            () => !_fixture.BridgeState.IsUnityConnected);

        Assert.False(_fixture.BridgeState.IsEditorReady);
    }

    [Fact]
    public async Task EditorReady_RestoredAfterReconnect()
    {
        // Wait for initial readiness
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsEditorReady,
            timeout: TimeSpan.FromSeconds(10));

        // Disconnect
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(
            () => !_fixture.BridgeState.IsUnityConnected);
        Assert.False(_fixture.BridgeState.IsEditorReady);

        // Reconnect with new client
        var newFake = _fixture.CreateFakeUnity();
        await newFake.ConnectAsync(_fixture.BaseUri);

        // Should become ready again after ping succeeds
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsEditorReady,
            timeout: TimeSpan.FromSeconds(10),
            message: "Editor should become ready again after reconnection");

        await newFake.DisposeAsync();
    }

    [Fact]
    public async Task EditorReady_ResetDuringDomainReload()
    {
        // Wait for initial readiness
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsEditorReady,
            timeout: TimeSpan.FromSeconds(10));

        // Simulate domain reload starting
        await _fixture.FakeUnity.SendEventAsync(
            UnityCtlEvents.DomainReloadStarting, new { });
        await Task.Delay(50);

        Assert.False(_fixture.BridgeState.IsEditorReady);
    }

    [Fact]
    public async Task HealthEndpoint_ShowsNotReady_WhenDisconnected()
    {
        await _fixture.FakeUnity.DisconnectAsync();
        await Task.Delay(100);

        var response = await _fixture.HttpClient.GetAsync("/health");
        var json = await response.Content.ReadAsStringAsync();
        var health = JsonHelper.Deserialize<HealthResult>(json);

        Assert.NotNull(health);
        Assert.False(health.UnityConnected);
        Assert.False(health.EditorReady);
    }
}
