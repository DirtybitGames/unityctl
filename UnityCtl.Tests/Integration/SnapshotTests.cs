using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for snapshot command extensions: stage context, --scene, --prefab, prefab annotations.
/// </summary>
public class SnapshotTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- Stage context ---

    [Fact]
    public async Task Snapshot_SceneEditing_IncludesStageContext()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "scene (editing)",
            SceneName = "MainScene",
            ScenePath = "Assets/Scenes/MainScene.unity",
            IsPlaying = false,
            RootObjectCount = 2,
            Objects = new[]
            {
                new SnapshotObject { InstanceId = 100, Name = "Camera" },
                new SnapshotObject { InstanceId = 200, Name = "Player" }
            }
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("scene (editing)", result["stage"]?.ToString());
        Assert.Equal("MainScene", result["sceneName"]?.ToString());
        Assert.Null(result["prefabAssetPath"]?.Value<string>());
    }

    [Fact]
    public async Task Snapshot_PlayMode_IncludesPlayingStage()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "scene (playing)",
            SceneName = "MainScene",
            ScenePath = "Assets/Scenes/MainScene.unity",
            IsPlaying = true,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 100, Name = "Camera" } }
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("scene (playing)", result["stage"]?.ToString());
        Assert.True(result["isPlaying"]?.Value<bool>());
    }

    [Fact]
    public async Task Snapshot_PrefabStageIsolated_IncludesPrefabContext()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "prefab (isolated)",
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            HasUnsavedChanges = false,
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject { InstanceId = 99001, Name = "Player" }
            }
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("prefab (isolated)", result["stage"]?.ToString());
        Assert.Equal("Assets/Prefabs/Player.prefab", result["prefabAssetPath"]?.ToString());
        Assert.False(result["hasUnsavedChanges"]?.Value<bool>());
        // sceneName should be absent when in prefab mode
        Assert.Null(result["sceneName"]?.Value<string>());
    }

    [Fact]
    public async Task Snapshot_PrefabStageInContext_IncludesOpenedFromInstance()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "prefab (in-context)",
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            HasUnsavedChanges = true,
            OpenedFromInstanceId = 14200,
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject { InstanceId = 99001, Name = "Player" }
            }
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("prefab (in-context)", result["stage"]?.ToString());
        Assert.True(result["hasUnsavedChanges"]?.Value<bool>());
        Assert.Equal(14200, result["openedFromInstanceId"]?.Value<int>());
    }

    // --- Prefab instance annotations ---

    [Fact]
    public async Task Snapshot_PrefabInstances_IncludePrefabAnnotations()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "scene (editing)",
            SceneName = "MainScene",
            ScenePath = "Assets/Scenes/MainScene.unity",
            IsPlaying = false,
            RootObjectCount = 2,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 14200,
                    Name = "Player",
                    PrefabAssetPath = "Assets/Prefabs/Player.prefab",
                    PrefabAssetType = "Regular",
                    IsPrefabInstanceRoot = true,
                    ChildCount = 1,
                    Children = new[]
                    {
                        new SnapshotObject
                        {
                            InstanceId = 14210,
                            Name = "Weapon",
                            PrefabAssetPath = "Assets/Prefabs/Weapon.prefab",
                            PrefabAssetType = "Regular",
                            IsPrefabInstanceRoot = true
                        }
                    }
                },
                new SnapshotObject
                {
                    InstanceId = 14300,
                    Name = "Ground"
                    // Not a prefab instance — no annotations
                }
            }
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        var objects = result["objects"] as JArray;
        Assert.NotNull(objects);
        Assert.Equal(2, objects!.Count);

        // Player is a prefab instance
        var player = objects[0] as JObject;
        Assert.Equal("Assets/Prefabs/Player.prefab", player?["prefabAssetPath"]?.ToString());
        Assert.Equal("Regular", player?["prefabAssetType"]?.ToString());
        Assert.True(player?["isPrefabInstanceRoot"]?.Value<bool>());

        // Nested prefab instance
        var weapon = (player?["children"] as JArray)?[0] as JObject;
        Assert.Equal("Assets/Prefabs/Weapon.prefab", weapon?["prefabAssetPath"]?.ToString());
        Assert.True(weapon?["isPrefabInstanceRoot"]?.Value<bool>());

        // Ground has no prefab annotations (fields omitted via NullValueHandling.Ignore)
        var ground = objects[1] as JObject;
        Assert.Null(ground?["prefabAssetPath"]);
        Assert.Null(ground?["isPrefabInstanceRoot"]);
    }

    [Fact]
    public async Task Snapshot_PrefabVariant_AnnotatesAsVariant()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "scene (editing)",
            SceneName = "MainScene",
            ScenePath = "Assets/Scenes/MainScene.unity",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 15000,
                    Name = "EnemyPlayer",
                    PrefabAssetPath = "Assets/Prefabs/EnemyVariant.prefab",
                    PrefabAssetType = "Variant",
                    IsPrefabInstanceRoot = true
                }
            }
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        var obj = (result["objects"] as JArray)?[0] as JObject;
        Assert.Equal("Variant", obj?["prefabAssetType"]?.ToString());
    }

    // --- --scene and --prefab flags ---

    [Fact]
    public async Task Snapshot_WithScenePath_PassesArgToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, req =>
        {
            var scenePath = req.Args?["scenePath"]?.ToString();
            return new SnapshotResult
            {
                Stage = "scene (editing)",
                SceneName = "Level2",
                ScenePath = scenePath ?? "unknown",
                IsPlaying = false,
                RootObjectCount = 0,
                Objects = Array.Empty<SnapshotObject>()
            };
        });

        var args = new Dictionary<string, object?> { ["scenePath"] = "Assets/Scenes/Level2.unity" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Assets/Scenes/Level2.unity", result["scenePath"]?.ToString());

        // Verify the arg was forwarded to Unity
        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.Snapshot);
        Assert.Equal("Assets/Scenes/Level2.unity", received.Args?["scenePath"]?.ToString());
    }

    [Fact]
    public async Task Snapshot_WithPrefabPath_PassesArgToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, req =>
        {
            var prefabPath = req.Args?["prefabPath"]?.ToString();
            return new SnapshotResult
            {
                PrefabAssetPath = prefabPath,
                IsPlaying = false,
                RootObjectCount = 1,
                Objects = new[]
                {
                    new SnapshotObject { InstanceId = 50000, Name = "TestPrefab" }
                }
            };
        });

        var args = new Dictionary<string, object?> { ["prefabPath"] = "Assets/Prefabs/TestCube.prefab" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Assets/Prefabs/TestCube.prefab", result["prefabAssetPath"]?.ToString());

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.Snapshot);
        Assert.Equal("Assets/Prefabs/TestCube.prefab", received.Args?["prefabPath"]?.ToString());
    }

    [Fact]
    public async Task Snapshot_SceneAndPrefab_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.Snapshot, "command_failed",
            "Cannot use both --scene and --prefab");

        var args = new Dictionary<string, object?>
        {
            ["scenePath"] = "Assets/Scenes/X.unity",
            ["prefabPath"] = "Assets/Prefabs/Y.prefab"
        };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot, args);

        AssertExtensions.IsError(response);
    }

    [Fact]
    public async Task Snapshot_SceneDuringPlayMode_ReturnsError()
    {
        _fixture.FakeUnity.OnCommandError(UnityCtlCommands.Snapshot, "command_failed",
            "Cannot snapshot other scenes during play mode. Use snapshot without --scene for the active scene.");

        var args = new Dictionary<string, object?> { ["scenePath"] = "Assets/Scenes/Level2.unity" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot, args);

        AssertExtensions.IsError(response);
    }

    // --- Drill-down with stage context ---

    [Fact]
    public async Task Snapshot_DrillDown_IncludesStageContext()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "prefab (isolated)",
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            HasUnsavedChanges = false,
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 99001,
                    Name = "Player",
                    Components = new[]
                    {
                        new SnapshotComponent
                        {
                            TypeName = "PlayerController",
                            Properties = new Dictionary<string, object> { { "speed", 5.0 } }
                        }
                    }
                }
            }
        });

        var args = new Dictionary<string, object?> { ["id"] = 99001, ["components"] = true };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("prefab (isolated)", result["stage"]?.ToString());
        Assert.Equal("Assets/Prefabs/Player.prefab", result["prefabAssetPath"]?.ToString());
    }

    // --- Existing flags compose with new args ---

    [Fact]
    public async Task Snapshot_PrefabWithDepthAndFilter_PassesAllArgs()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            PrefabAssetPath = "Assets/Prefabs/TestGroup.prefab",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 60000, Name = "TestGroup" } }
        });

        var args = new Dictionary<string, object?>
        {
            ["prefabPath"] = "Assets/Prefabs/TestGroup.prefab",
            ["depth"] = 4,
            ["filter"] = "type:MeshRenderer"
        };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot, args);

        AssertExtensions.IsOk(response);

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.Snapshot);
        Assert.Equal("Assets/Prefabs/TestGroup.prefab", received.Args?["prefabPath"]?.ToString());
    }

    // --- Multi-scene support ---

    [Fact]
    public async Task Snapshot_MultipleScenes_IncludesScenesArray()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "scene (editing)",
            SceneName = "MainScene",
            ScenePath = "Assets/Scenes/MainScene.unity",
            IsPlaying = false,
            RootObjectCount = 3,
            Objects = new[]
            {
                new SnapshotObject { InstanceId = 100, Name = "Camera" },
                new SnapshotObject { InstanceId = 200, Name = "Player" },
                new SnapshotObject { InstanceId = 300, Name = "Enemy" }
            },
            Scenes = new[]
            {
                new SnapshotSceneInfo
                {
                    SceneName = "MainScene",
                    ScenePath = "Assets/Scenes/MainScene.unity",
                    IsActive = true,
                    RootObjectCount = 2,
                    Objects = new[]
                    {
                        new SnapshotObject { InstanceId = 100, Name = "Camera" },
                        new SnapshotObject { InstanceId = 200, Name = "Player" }
                    }
                },
                new SnapshotSceneInfo
                {
                    SceneName = "Level2",
                    ScenePath = "Assets/Scenes/Level2.unity",
                    IsActive = false,
                    RootObjectCount = 1,
                    Objects = new[]
                    {
                        new SnapshotObject { InstanceId = 300, Name = "Enemy" }
                    }
                }
            }
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);

        // Top-level fields still present for backward compat
        Assert.Equal("MainScene", result["sceneName"]?.ToString());
        Assert.Equal(3, result["rootObjectCount"]?.Value<int>());
        Assert.Equal(3, (result["objects"] as JArray)?.Count);

        // Scenes array present with per-scene breakdown
        var scenes = result["scenes"] as JArray;
        Assert.NotNull(scenes);
        Assert.Equal(2, scenes!.Count);

        var scene1 = scenes[0] as JObject;
        Assert.Equal("MainScene", scene1?["sceneName"]?.ToString());
        Assert.True(scene1?["isActive"]?.Value<bool>());
        Assert.Equal(2, scene1?["rootObjectCount"]?.Value<int>());
        Assert.Equal(2, (scene1?["objects"] as JArray)?.Count);

        var scene2 = scenes[1] as JObject;
        Assert.Equal("Level2", scene2?["sceneName"]?.ToString());
        Assert.False(scene2?["isActive"]?.Value<bool>());
        Assert.Equal(1, scene2?["rootObjectCount"]?.Value<int>());
    }

    [Fact]
    public async Task Snapshot_SingleScene_OmitsScenesArray()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.Snapshot, _ => new SnapshotResult
        {
            Stage = "scene (editing)",
            SceneName = "MainScene",
            ScenePath = "Assets/Scenes/MainScene.unity",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 100, Name = "Camera" } }
            // Scenes is null — single scene
        });

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.Snapshot);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Null(result["scenes"]); // Not emitted for single scene
        Assert.Equal("MainScene", result["sceneName"]?.ToString());
    }
}
