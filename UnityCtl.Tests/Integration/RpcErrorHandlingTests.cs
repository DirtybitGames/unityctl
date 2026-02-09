using System.Net;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for error handling in the RPC endpoint:
/// - Unity offline (503)
/// - Command errors from Unity
/// - Unknown commands (default handler)
/// </summary>
public class RpcErrorHandlingTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.FakeUnity
            .OnCommandError(UnityCtlCommands.MenuExecute, "MENU_NOT_FOUND", "Menu item not found: Invalid/Path")
            .OnCommand(UnityCtlCommands.SceneList, _ => new SceneListResult
            {
                Scenes = new[] { new SceneInfo { Path = "Assets/Scenes/Main.unity", EnabledInBuild = true } }
            });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Rpc_UnityOffline_Returns503()
    {
        // Disconnect Unity
        await _fixture.FakeUnity.DisconnectAsync();
        await Task.Delay(200);

        var response = await _fixture.SendRpcAsync(UnityCtlCommands.SceneList);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Rpc_CommandError_ReturnsErrorResponse()
    {
        var args = new Dictionary<string, object?> { ["path"] = "Invalid/Path" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.MenuExecute, args);

        Assert.Equal(ResponseStatus.Error, response.Status);
        Assert.NotNull(response.Error);
        Assert.Equal("MENU_NOT_FOUND", response.Error.Code);
    }

    [Fact]
    public async Task Rpc_UnregisteredCommand_ReturnsDefaultOk()
    {
        // Commands not registered with FakeUnity get a default empty OK response
        var response = await _fixture.SendRpcAndParseAsync("some.unknown.command");

        AssertExtensions.IsOk(response);
    }

    [Fact]
    public async Task Rpc_AgentId_ForwardedToUnity()
    {
        var response = await _fixture.SendRpcAndParseAsync(
            UnityCtlCommands.SceneList, agentId: "agent-42");

        AssertExtensions.IsOk(response);

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.SceneList);
        Assert.Equal("agent-42", received.AgentId);
    }
}
