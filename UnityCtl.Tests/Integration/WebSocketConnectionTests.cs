using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the WebSocket connection lifecycle between Bridge and Unity.
/// Covers connection, hello handshake, disconnection, and reconnection.
/// </summary>
public class WebSocketConnectionTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task FakeUnity_Connects_BridgeShowsConnected()
    {
        Assert.True(_fixture.BridgeState.IsUnityConnected);
    }

    [Fact]
    public async Task FakeUnity_SendsHello_BridgeStoresIt()
    {
        // The FakeUnity sends hello on connect
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.UnityHelloMessage != null);

        var hello = _fixture.BridgeState.UnityHelloMessage;
        Assert.NotNull(hello);
        Assert.Equal(_fixture.ProjectId, hello.ProjectId);
        Assert.Equal("6000.0.0f1", hello.UnityVersion);
        Assert.Equal("1.0.0", hello.ProtocolVersion);
        Assert.Equal("0.3.6", hello.PluginVersion);
    }

    [Fact]
    public async Task FakeUnity_Disconnects_BridgeDetects()
    {
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(
            () => !_fixture.BridgeState.IsUnityConnected);

        Assert.Null(_fixture.BridgeState.UnityHelloMessage);
    }

    [Fact]
    public async Task FakeUnity_Reconnects_BridgeResumes()
    {
        // Disconnect
        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(
            () => !_fixture.BridgeState.IsUnityConnected);

        // Reconnect with new client
        var newFake = _fixture.CreateFakeUnity();
        newFake.OnCommand(UnityCtlCommands.PlayStatus, _ =>
            new PlayModeResult { State = PlayModeState.Stopped });
        await newFake.ConnectAsync(_fixture.BaseUri);
        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.IsUnityConnected);

        // Commands should work again
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);
        AssertExtensions.IsOk(response);

        await newFake.DisposeAsync();
    }

    [Fact]
    public async Task MultipleEvents_AllProcessedByBridge()
    {
        // Send a burst of log events
        for (int i = 0; i < 20; i++)
        {
            await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Burst log {i}"
            });
        }

        await AssertExtensions.WaitUntilAsync(
            () => _fixture.BridgeState.GetRecentUnifiedLogs(0, ignoreWatermark: true).Length == 20);

        var logs = _fixture.BridgeState.GetRecentUnifiedLogs(0, ignoreWatermark: true);
        Assert.Equal(20, logs.Length);
    }
}
