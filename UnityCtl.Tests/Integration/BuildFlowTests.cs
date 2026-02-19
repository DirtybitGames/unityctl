using System.Net;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

public class BuildFlowTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_BUILD", "5");
        return _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("UNITYCTL_TIMEOUT_BUILD", null);
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task BuildPlayer_ForwardsAsScriptExecute()
    {
        // The bridge should forward build.player as script.execute to Unity
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptExecute, request =>
        {
            // Verify the args contain the build code
            var args = request.Args;
            Assert.NotNull(args);
            Assert.True(args!.ContainsKey("code"));

            return new ScriptExecuteResult
            {
                Success = true,
                Result = "{ \"result\": \"Succeeded\" }"
            };
        });

        var response = await _fixture.SendRpcAsync(UnityCtlCommands.BuildPlayer, new Dictionary<string, object?>
        {
            { "code", "public class Script { public static object Main() { return \"ok\"; } }" },
            { "className", "Script" },
            { "methodName", "Main" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseMessage = await ParseResponseAsync(response);
        Assert.Equal(ResponseStatus.Ok, responseMessage.Status);
    }

    [Fact]
    public async Task BuildPlayer_UnityReturnsError_PropagatesError()
    {
        _fixture.FakeUnity.OnCommandError(
            UnityCtlCommands.ScriptExecute,
            "command_failed",
            "Build failed: missing scenes");

        var response = await _fixture.SendRpcAsync(UnityCtlCommands.BuildPlayer, new Dictionary<string, object?>
        {
            { "code", "public class Script { public static object Main() { return null; } }" },
            { "className", "Script" },
            { "methodName", "Main" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseMessage = await ParseResponseAsync(response);
        Assert.Equal(ResponseStatus.Error, responseMessage.Status);
        Assert.Equal("command_failed", responseMessage.Error?.Code);
    }

    [Fact]
    public async Task BuildPlayer_TimesOutOnSlowBuild()
    {
        // Build takes longer than the 5-second test timeout
        _fixture.FakeUnity.OnCommandWithDelay(
            UnityCtlCommands.ScriptExecute,
            TimeSpan.FromSeconds(10),
            _ => new ScriptExecuteResult { Success = true, Result = "done" });

        var response = await _fixture.SendRpcAsync(UnityCtlCommands.BuildPlayer, new Dictionary<string, object?>
        {
            { "code", "public class Script { public static object Main() { return null; } }" },
            { "className", "Script" },
            { "methodName", "Main" }
        });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task BuildPlayer_CommandReceivedByUnityAsScriptExecute()
    {
        // Verify Unity sees script.execute, not build.player
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptExecute, _ =>
            new ScriptExecuteResult { Success = true, Result = "ok" });

        await _fixture.SendRpcAsync(UnityCtlCommands.BuildPlayer, new Dictionary<string, object?>
        {
            { "code", "test code" },
            { "className", "Script" },
            { "methodName", "Main" }
        });

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.ScriptExecute);
        Assert.Equal(UnityCtlCommands.ScriptExecute, received.Command);
    }

    [Fact]
    public async Task BuildPlayer_PreservesCodeArgs()
    {
        var buildCode = "public class Script { public static object Main() { return BuildPipeline.BuildPlayer(new string[0], \"out\", BuildTarget.WebGL, BuildOptions.None); } }";

        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptExecute, _ =>
            new ScriptExecuteResult { Success = true, Result = "ok" });

        await _fixture.SendRpcAsync(UnityCtlCommands.BuildPlayer, new Dictionary<string, object?>
        {
            { "code", buildCode },
            { "className", "Script" },
            { "methodName", "Main" }
        });

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.ScriptExecute);
        var codeArg = (received.Args?["code"] as JValue)?.Value<string>()
                   ?? received.Args?["code"]?.ToString();
        Assert.Equal(buildCode, codeArg);
    }

    private static async Task<ResponseMessage> ParseResponseAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonHelper.Deserialize<ResponseMessage>(json)!;
    }
}
