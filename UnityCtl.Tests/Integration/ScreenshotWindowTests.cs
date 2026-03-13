using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for screenshot.window and screenshot.listWindows commands through the full Bridge pipeline.
/// </summary>
public class ScreenshotWindowTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.FakeUnity
            .OnCommand(UnityCtlCommands.ScreenshotListWindows, _ => new ScreenshotListWindowsResult
            {
                Windows = new[]
                {
                    new EditorWindowInfo { Type = "UnityEditor.SceneView", Title = "Scene", Width = 1920, Height = 1080, Docked = true },
                    new EditorWindowInfo { Type = "UnityEditor.GameView", Title = "Game", Width = 1920, Height = 1080, Docked = true },
                    new EditorWindowInfo { Type = "MyNamespace.MyToolWindow", Title = "My Tool", Width = 400, Height = 300, Docked = false }
                }
            })
            .OnCommand(UnityCtlCommands.ScreenshotWindow, req =>
            {
                var window = req.Args?["window"]?.ToString() ?? "unknown";
                object? filenameObj = null;
                req.Args?.TryGetValue("filename", out filenameObj);
                var filename = filenameObj?.ToString();
                return new ScreenshotWindowResult
                {
                    Path = $"Screenshots/{filename ?? "window_capture.png"}",
                    Width = 400,
                    Height = 300,
                    WindowType = "MyNamespace.MyToolWindow",
                    WindowTitle = "My Tool"
                };
            });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ListWindows_ReturnsAllWindows()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScreenshotListWindows);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        var windows = result["windows"] as JArray;
        Assert.NotNull(windows);
        Assert.Equal(3, windows.Count);
    }

    [Fact]
    public async Task ListWindows_ContainsExpectedFields()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScreenshotListWindows);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        var windows = result["windows"] as JArray;
        Assert.NotNull(windows);

        var firstWindow = windows[0] as JObject;
        Assert.NotNull(firstWindow);
        Assert.Equal("UnityEditor.SceneView", firstWindow["type"]?.ToString());
        Assert.Equal("Scene", firstWindow["title"]?.ToString());
        Assert.Equal(1920, firstWindow["width"]?.Value<int>());
        Assert.Equal(1080, firstWindow["height"]?.Value<int>());
        Assert.True(firstWindow["docked"]?.Value<bool>());
    }

    [Fact]
    public async Task WindowCapture_SendsWindowArgToUnity()
    {
        var args = new Dictionary<string, object?>
        {
            ["window"] = "MyToolWindow",
            ["filename"] = "tool_shot.png"
        };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScreenshotWindow, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Screenshots/tool_shot.png", result["path"]?.ToString());
        Assert.Equal(400, result["width"]?.Value<int>());
        Assert.Equal(300, result["height"]?.Value<int>());
        Assert.Equal("MyNamespace.MyToolWindow", result["windowType"]?.ToString());
        Assert.Equal("My Tool", result["windowTitle"]?.ToString());
    }

    [Fact]
    public async Task WindowCapture_FakeUnityReceivesCorrectArgs()
    {
        var args = new Dictionary<string, object?>
        {
            ["window"] = "MyToolWindow",
            ["filename"] = "capture.png"
        };
        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScreenshotWindow, args);

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.ScreenshotWindow);
        Assert.Equal(UnityCtlCommands.ScreenshotWindow, received.Command);
        Assert.Equal("MyToolWindow", received.Args?["window"]?.ToString());
        Assert.Equal("capture.png", received.Args?["filename"]?.ToString());
    }

    [Fact]
    public async Task WindowCapture_WithoutFilename_Succeeds()
    {
        var args = new Dictionary<string, object?>
        {
            ["window"] = "MyToolWindow"
        };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScreenshotWindow, args);

        AssertExtensions.IsOk(response);
    }
}
