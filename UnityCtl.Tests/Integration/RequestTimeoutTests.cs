using System.Net;
using System.Text;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the request-level timeout override on RpcRequest.
/// </summary>
public class RequestTimeoutTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync()
    {
        // Set a very short default so we can verify the override works
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_DEFAULT", "2");
        return _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_DEFAULT", null);
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task ScriptExecute_WithTimeoutOverride_UsesRequestTimeout()
    {
        // Default timeout is 2s, but the request asks for 10s.
        // Unity responds in 4s — should succeed with override, fail without.
        _fixture.FakeUnity.OnCommandWithDelay(
            UnityCtlCommands.ScriptExecute,
            TimeSpan.FromSeconds(4),
            _ => new ScriptExecuteResult { Success = true, Result = "build done" });

        var response = await SendRpcWithTimeoutAsync(
            UnityCtlCommands.ScriptExecute,
            new Dictionary<string, object?>
            {
                { "code", "public class Script { public static object Main() { return \"ok\"; } }" },
                { "className", "Script" },
                { "methodName", "Main" }
            },
            timeoutSeconds: 10);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseMessage = await ParseResponseAsync(response);
        Assert.Equal(ResponseStatus.Ok, responseMessage.Status);
    }

    [Fact]
    public async Task ScriptExecute_WithoutTimeoutOverride_UsesDefault()
    {
        // Default timeout is 2s, Unity responds in 4s — should time out
        _fixture.FakeUnity.OnCommandWithDelay(
            UnityCtlCommands.ScriptExecute,
            TimeSpan.FromSeconds(4),
            _ => new ScriptExecuteResult { Success = true, Result = "build done" });

        var response = await SendRpcWithTimeoutAsync(
            UnityCtlCommands.ScriptExecute,
            new Dictionary<string, object?>
            {
                { "code", "public class Script { public static object Main() { return \"ok\"; } }" },
                { "className", "Script" },
                { "methodName", "Main" }
            },
            timeoutSeconds: null);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task ScriptExecute_ErrorResult_PropagatesWithTimeout()
    {
        _fixture.FakeUnity.OnCommandError(
            UnityCtlCommands.ScriptExecute,
            "command_failed",
            "Build failed: missing scenes");

        var response = await SendRpcWithTimeoutAsync(
            UnityCtlCommands.ScriptExecute,
            new Dictionary<string, object?>
            {
                { "code", "public class Script { public static object Main() { return null; } }" },
                { "className", "Script" },
                { "methodName", "Main" }
            },
            timeoutSeconds: 600);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseMessage = await ParseResponseAsync(response);
        Assert.Equal(ResponseStatus.Error, responseMessage.Status);
        Assert.Equal("command_failed", responseMessage.Error?.Code);
    }

    [Fact]
    public async Task ScriptExecute_TimeoutOverride_StillTimesOut()
    {
        // Request timeout is 3s, Unity responds in 10s — should time out
        _fixture.FakeUnity.OnCommandWithDelay(
            UnityCtlCommands.ScriptExecute,
            TimeSpan.FromSeconds(10),
            _ => new ScriptExecuteResult { Success = true, Result = "done" });

        var response = await SendRpcWithTimeoutAsync(
            UnityCtlCommands.ScriptExecute,
            new Dictionary<string, object?>
            {
                { "code", "public class Script { public static object Main() { return null; } }" },
                { "className", "Script" },
                { "methodName", "Main" }
            },
            timeoutSeconds: 3);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    /// <summary>
    /// Send an RPC with an optional timeout override — matches what the CLI does.
    /// </summary>
    private async Task<HttpResponseMessage> SendRpcWithTimeoutAsync(
        string command, Dictionary<string, object?>? args, int? timeoutSeconds)
    {
        var request = new { command, args, timeout = timeoutSeconds };
        var json = JsonHelper.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.HttpClient.PostAsync("/rpc", content);
    }

    private static async Task<ResponseMessage> ParseResponseAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonHelper.Deserialize<ResponseMessage>(json)!;
    }
}
