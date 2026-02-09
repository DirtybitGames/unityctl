using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for scene.list and scene.load commands through the full Bridge pipeline.
/// </summary>
public class SceneCommandTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync()
    {
        // Configure FakeUnity command handlers
        _fixture.FakeUnity
            .OnCommand(UnityCtlCommands.SceneList, _ => new SceneListResult
            {
                Scenes = new[]
                {
                    new SceneInfo { Path = "Assets/Scenes/Main.unity", EnabledInBuild = true },
                    new SceneInfo { Path = "Assets/Scenes/Menu.unity", EnabledInBuild = true },
                    new SceneInfo { Path = "Assets/Scenes/Debug.unity", EnabledInBuild = false }
                }
            })
            .OnCommand(UnityCtlCommands.SceneLoad, req =>
            {
                var args = req.Args;
                var path = args?["path"]?.ToString() ?? "unknown";
                return new SceneLoadResult { LoadedScenePath = path };
            });

        return _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task SceneList_ReturnsAllScenes()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneList);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        var scenes = result["scenes"] as JArray;
        Assert.NotNull(scenes);
        Assert.Equal(3, scenes.Count);
    }

    [Fact]
    public async Task SceneLoad_SendsPathToUnity()
    {
        var args = new Dictionary<string, object?> { ["path"] = "Assets/Scenes/Main.unity" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneLoad, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Assets/Scenes/Main.unity", result["loadedScenePath"]?.ToString());
    }

    [Fact]
    public async Task SceneList_FakeUnityReceivesCorrectCommand()
    {
        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.SceneList);

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.SceneList);
        Assert.Equal(UnityCtlCommands.SceneList, received.Command);
    }
}
