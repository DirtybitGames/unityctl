using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for prefab.open and prefab.close commands through the full Bridge pipeline.
/// </summary>
public class PrefabCommandTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- prefab.open ---

    [Fact]
    public async Task PrefabOpen_Isolated_ReturnsStageInfo()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabOpen, req =>
        {
            var path = req.Args?["path"]?.ToString();
            return new PrefabOpenResult
            {
                PrefabAssetPath = path ?? "unknown",
                Stage = "prefab (isolated)"
            };
        });

        var args = new Dictionary<string, object?> { ["path"] = "Assets/Prefabs/Player.prefab" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Assets/Prefabs/Player.prefab", result["prefabAssetPath"]?.ToString());
        Assert.Equal("prefab (isolated)", result["stage"]?.ToString());
    }

    [Fact]
    public async Task PrefabOpen_SendsPathToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabOpen, _ => new PrefabOpenResult
        {
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            Stage = "prefab (isolated)"
        });

        var args = new Dictionary<string, object?> { ["path"] = "Assets/Prefabs/Player.prefab" };
        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, args);

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.PrefabOpen);
        Assert.Equal("Assets/Prefabs/Player.prefab", received.Args?["path"]?.ToString());
    }

    [Fact]
    public async Task PrefabOpen_WithContext_SendsContextIdToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabOpen, req =>
        {
            return new PrefabOpenResult
            {
                PrefabAssetPath = "Assets/Prefabs/Player.prefab",
                Stage = "prefab (in-context)"
            };
        });

        var args = new Dictionary<string, object?>
        {
            ["path"] = "Assets/Prefabs/Player.prefab",
            ["context"] = 14200
        };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("prefab (in-context)", result["stage"]?.ToString());

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.PrefabOpen);
        // Context ID should be forwarded as-is
        var contextId = received.Args?["context"];
        Assert.NotNull(contextId);
    }

    [Fact]
    public async Task PrefabOpen_InvalidPath_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.PrefabOpen, "command_failed",
            "Prefab not found: Assets/Prefabs/DoesNotExist.prefab");

        var args = new Dictionary<string, object?> { ["path"] = "Assets/Prefabs/DoesNotExist.prefab" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, args);

        AssertExtensions.IsError(response);
        Assert.Contains("not found", response.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrefabOpen_NotAPrefabFile_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.PrefabOpen, "command_failed",
            "Not a prefab file: Assets/Scenes/MainScene.unity");

        var args = new Dictionary<string, object?> { ["path"] = "Assets/Scenes/MainScene.unity" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, args);

        AssertExtensions.IsError(response);
        Assert.Contains("Not a prefab", response.Error?.Message ?? "");
    }

    [Fact]
    public async Task PrefabOpen_DuringPlayMode_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.PrefabOpen, "command_failed",
            "Cannot open prefab stage during play mode");

        var args = new Dictionary<string, object?> { ["path"] = "Assets/Prefabs/Player.prefab" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, args);

        AssertExtensions.IsError(response);
        Assert.Contains("play mode", response.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrefabOpen_MissingPath_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.PrefabOpen, "command_failed",
            "Prefab path is required");

        var args = new Dictionary<string, object?>();
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, args);

        AssertExtensions.IsError(response);
    }

    // --- prefab.close ---

    [Fact]
    public async Task PrefabClose_ReturnsToScene()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabClose, _ => new PrefabCloseResult
        {
            ReturnedToScene = "MainScene"
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("MainScene", result["returnedToScene"]?.ToString());
    }

    [Fact]
    public async Task PrefabClose_NotInPrefabMode_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.PrefabClose, "command_failed",
            "Not currently in prefab editing mode");

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose);

        AssertExtensions.IsError(response);
        Assert.Contains("Not currently in prefab", response.Error?.Message ?? "");
    }

    [Fact]
    public async Task PrefabClose_DuringPlayMode_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.PrefabClose, "command_failed",
            "Cannot close prefab stage during play mode");

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose);

        AssertExtensions.IsError(response);
        Assert.Contains("play mode", response.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrefabClose_SendsCommandToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabClose, _ => new PrefabCloseResult
        {
            ReturnedToScene = "MainScene"
        });

        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose);

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.PrefabClose);
        Assert.Equal(UnityCtlCommands.PrefabClose, received.Command);
    }

    [Fact]
    public async Task PrefabClose_WithSave_SendsSaveArgToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabClose, _ => new PrefabCloseResult
        {
            ReturnedToScene = "MainScene",
            Saved = true
        });

        var args = new Dictionary<string, object?> { ["save"] = true, ["discard"] = false };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("MainScene", result["returnedToScene"]?.ToString());
        Assert.True(result["saved"]?.Value<bool>());

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.PrefabClose);
        Assert.Equal("True", received.Args?["save"]?.ToString());
    }

    [Fact]
    public async Task PrefabClose_WithDiscard_SendsDiscardArgToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabClose, _ => new PrefabCloseResult
        {
            ReturnedToScene = "MainScene",
            Saved = false
        });

        var args = new Dictionary<string, object?> { ["save"] = false, ["discard"] = true };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.False(result["saved"]?.Value<bool>());

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.PrefabClose);
        Assert.Equal("True", received.Args?["discard"]?.ToString());
    }

    [Fact]
    public async Task PrefabClose_WithoutFlags_DefaultBehavior()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.PrefabClose, _ => new PrefabCloseResult
        {
            ReturnedToScene = "MainScene",
            Saved = false
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("MainScene", result["returnedToScene"]?.ToString());
        Assert.False(result["saved"]?.Value<bool>());
    }

    // --- Workflow: open → snapshot → close ---

    [Fact]
    public async Task PrefabWorkflow_OpenSnapshotClose()
    {
        // Set up handlers for the full workflow
        _fixture.FakeUnity
            .OnCommand(UnityCtlCommands.PrefabOpen, _ => new PrefabOpenResult
            {
                PrefabAssetPath = "Assets/Prefabs/Weapon.prefab",
                Stage = "prefab (isolated)"
            })
            .OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
            {
                Stage = "prefab (isolated)",
                PrefabAssetPath = "Assets/Prefabs/Weapon.prefab",
                HasUnsavedChanges = false,
                IsPlaying = false,
                RootObjectCount = 1,
                Objects = new[]
                {
                    new SnapshotObject
                    {
                        InstanceId = 99001,
                        Name = "Weapon",
                        Components = new[]
                        {
                            new SnapshotComponent { TypeName = "MeshRenderer" },
                            new SnapshotComponent { TypeName = "BoxCollider" }
                        }
                    }
                }
            })
            .OnCommand(UnityCtlCommands.PrefabClose, _ => new PrefabCloseResult
            {
                ReturnedToScene = "MainScene"
            });

        // Step 1: Open prefab
        var openArgs = new Dictionary<string, object?> { ["path"] = "Assets/Prefabs/Weapon.prefab" };
        var openResponse = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabOpen, openArgs);
        AssertExtensions.IsOk(openResponse);

        // Step 2: Snapshot in prefab mode
        var snapshotResponse = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);
        AssertExtensions.IsOk(snapshotResponse);
        var snapResult = AssertExtensions.GetResultJObject(snapshotResponse);
        Assert.Equal("prefab (isolated)", snapResult["stage"]?.ToString());
        Assert.Equal("Assets/Prefabs/Weapon.prefab", snapResult["prefabAssetPath"]?.ToString());

        // Step 3: Close prefab
        var closeResponse = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PrefabClose);
        AssertExtensions.IsOk(closeResponse);
        var closeResult = AssertExtensions.GetResultJObject(closeResponse);
        Assert.Equal("MainScene", closeResult["returnedToScene"]?.ToString());
    }
}
