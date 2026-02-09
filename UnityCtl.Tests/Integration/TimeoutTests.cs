using System.Net;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for timeout behavior when Unity is slow to respond or events never arrive.
/// Uses environment variables to set short timeouts for fast test execution.
/// </summary>
public class TimeoutTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync()
    {
        // Set very short timeouts for testing
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_DEFAULT", "2");
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_REFRESH", "2");
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_TEST", "2");

        return _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        // Clean up environment variables
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_DEFAULT", null);
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_REFRESH", null);
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_TEST", null);

        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task Rpc_SlowUnityResponse_TimesOut()
    {
        // Unity takes forever to respond
        _fixture.FakeUnity.OnCommandWithDelay(
            UnityCtlCommands.SceneList,
            TimeSpan.FromSeconds(10),
            _ => new SceneListResult { Scenes = Array.Empty<SceneInfo>() });

        var response = await _fixture.SendRpcAsync(UnityCtlCommands.SceneList);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task Rpc_EventNeverArrives_TimesOut()
    {
        // asset.refresh responds immediately but the completion event never arrives
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ => new { });
        // No AssetRefreshComplete event scheduled

        var response = await _fixture.SendRpcAsync(UnityCtlCommands.AssetRefresh);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task Rpc_TestRunEventNeverArrives_TimesOut()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.TestRun, _ =>
            new TestRunResult { Started = true, TestRunId = "run-timeout" });
        // No TestFinished event

        var response = await _fixture.SendRpcAsync(UnityCtlCommands.TestRun);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }
}
