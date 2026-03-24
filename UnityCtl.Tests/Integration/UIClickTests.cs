using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

public class UIClickTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task UIClick_ById_ReturnsClickedElement()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.UIClick, _ => new UIClickResult
        {
            InstanceId = 47088,
            Name = "PlayButton",
            Path = "Canvas/Panel/PlayButton",
            ScreenPosition = "(86, 382)",
            Text = "Play"
        });

        var args = new Dictionary<string, object?> { ["id"] = 47088 };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.UIClick, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal(47088, result["instanceId"]?.Value<int>());
        Assert.Equal("PlayButton", result["name"]?.ToString());
        Assert.Equal("Canvas/Panel/PlayButton", result["path"]?.ToString());
        Assert.Equal("Play", result["text"]?.ToString());
        Assert.Equal("(86, 382)", result["screenPosition"]?.ToString());
    }

    [Fact]
    public async Task UIClick_ByCoordinates_ReturnsClickedElement()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.UIClick, _ => new UIClickResult
        {
            InstanceId = 47088,
            Name = "CustomButton",
            Path = "Canvas/CustomButton",
            ScreenPosition = "(650, 455)"
        });

        var args = new Dictionary<string, object?> { ["x"] = 650, ["y"] = 455 };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.UIClick, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal(47088, result["instanceId"]?.Value<int>());
        Assert.Equal("CustomButton", result["name"]?.ToString());
    }

    [Fact]
    public async Task UIClick_ById_PassesIdArg()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.UIClick, _ => new UIClickResult
        {
            InstanceId = 100,
            Name = "Btn",
            Path = "Canvas/Btn",
            ScreenPosition = "(0, 0)"
        });

        var args = new Dictionary<string, object?> { ["id"] = 12345 };
        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.UIClick, args);

        var req = Assert.Single(_fixture.FakeUnity.ReceivedRequests,
            r => r.Command == UnityCtlCommands.UIClick);
        Assert.Equal(12345L, req.Args?["id"]);
    }

    [Fact]
    public async Task UIClick_ByCoordinates_PassesXYArgs()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.UIClick, _ => new UIClickResult
        {
            InstanceId = 100,
            Name = "Btn",
            Path = "Canvas/Btn",
            ScreenPosition = "(0, 0)"
        });

        var args = new Dictionary<string, object?> { ["x"] = 400, ["y"] = 300 };
        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.UIClick, args);

        var req = Assert.Single(_fixture.FakeUnity.ReceivedRequests,
            r => r.Command == UnityCtlCommands.UIClick);
        Assert.Equal(400L, req.Args?["x"]);
        Assert.Equal(300L, req.Args?["y"]);
    }

    [Fact]
    public async Task UIClick_Error_ReturnsErrorResponse()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.UIClick, _ =>
            throw new InvalidOperationException("ui.click requires play mode"));

        var args = new Dictionary<string, object?> { ["id"] = 100 };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.UIClick, args);

        AssertExtensions.IsError(response);
        Assert.Contains("play mode", response.Error?.Message);
    }

    [Fact]
    public async Task UIClick_Blocked_ReturnsErrorWithBlocker()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.UIClick, _ =>
            throw new InvalidOperationException("'PlayButton' [i:46994] is blocked by 'ModalOverlay' [i:46928]"));

        var args = new Dictionary<string, object?> { ["id"] = 46994 };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.UIClick, args);

        AssertExtensions.IsError(response);
        Assert.Contains("blocked by", response.Error?.Message);
        Assert.Contains("ModalOverlay", response.Error?.Message);
    }
}
