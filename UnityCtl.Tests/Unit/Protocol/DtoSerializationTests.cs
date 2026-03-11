using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Protocol;

public class DtoSerializationTests
{
    [Fact]
    public void HealthResult_Serializes_CamelCase()
    {
        var health = new HealthResult
        {
            Status = "ok",
            ProjectId = "proj-abc",
            UnityConnected = true,
            EditorReady = true,
            BridgeVersion = "0.3.6",
            UnityPluginVersion = "0.3.6"
        };

        var json = JsonHelper.Serialize(health);
        var jObj = JObject.Parse(json);

        Assert.Equal("ok", jObj["status"]?.ToString());
        Assert.Equal("proj-abc", jObj["projectId"]?.ToString());
        Assert.True(jObj["unityConnected"]?.Value<bool>());
        Assert.True(jObj["editorReady"]?.Value<bool>());
        Assert.Equal("0.3.6", jObj["bridgeVersion"]?.ToString());
    }

    [Fact]
    public void SceneListResult_Roundtrips()
    {
        var result = new SceneListResult
        {
            Scenes = new[]
            {
                new SceneInfo { Path = "Assets/Scenes/Main.unity", EnabledInBuild = true },
                new SceneInfo { Path = "Assets/Scenes/Menu.unity", EnabledInBuild = false }
            }
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<SceneListResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Scenes.Length);
        Assert.Equal("Assets/Scenes/Main.unity", deserialized.Scenes[0].Path);
        Assert.True(deserialized.Scenes[0].EnabledInBuild);
        Assert.False(deserialized.Scenes[1].EnabledInBuild);
    }

    [Fact]
    public void PlayModeResult_Roundtrips()
    {
        var result = new PlayModeResult { State = PlayModeState.Playing };
        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<PlayModeResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("playing", deserialized.State);
    }

    [Fact]
    public void TestFinishedPayload_Roundtrips_WithFailures()
    {
        var payload = new TestFinishedPayload
        {
            TestRunId = "run-001",
            Passed = 10,
            Failed = 2,
            Skipped = 1,
            Duration = 5.5,
            Failures = new[]
            {
                new TestFailureInfo
                {
                    TestName = "MyTest.TestFoo",
                    Message = "Expected true but was false",
                    StackTrace = "at MyTest.cs:42"
                }
            }
        };

        var json = JsonHelper.Serialize(payload);
        var deserialized = JsonHelper.Deserialize<TestFinishedPayload>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("run-001", deserialized.TestRunId);
        Assert.Equal(10, deserialized.Passed);
        Assert.Equal(2, deserialized.Failed);
        Assert.Equal(1, deserialized.Skipped);
        Assert.Equal(5.5, deserialized.Duration);
        Assert.Single(deserialized.Failures!);
        Assert.Equal("MyTest.TestFoo", deserialized.Failures![0].TestName);
    }

    [Fact]
    public void CompilationFinishedPayload_Roundtrips()
    {
        var payload = new CompilationFinishedPayload
        {
            Success = false,
            Errors = new[]
            {
                new CompilationMessageInfo
                {
                    File = "Assets/Scripts/Foo.cs",
                    Line = 42,
                    Column = 10,
                    Message = "CS1002: ; expected"
                }
            },
            Warnings = new[]
            {
                new CompilationMessageInfo
                {
                    File = "Assets/Scripts/Bar.cs",
                    Line = 5,
                    Column = 1,
                    Message = "CS0168: unused variable"
                }
            }
        };

        var json = JsonHelper.Serialize(payload);
        var deserialized = JsonHelper.Deserialize<CompilationFinishedPayload>(json);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Success);
        Assert.Single(deserialized.Errors!);
        Assert.Equal(42, deserialized.Errors![0].Line);
        Assert.Single(deserialized.Warnings!);
    }

    [Fact]
    public void AssetRefreshCompletePayload_Roundtrips()
    {
        var payload = new AssetRefreshCompletePayload
        {
            CompilationTriggered = true,
            HasCompilationErrors = false
        };

        var json = JsonHelper.Serialize(payload);
        var deserialized = JsonHelper.Deserialize<AssetRefreshCompletePayload>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.CompilationTriggered);
        Assert.False(deserialized.HasCompilationErrors);
    }

    [Fact]
    public void ScreenshotCaptureResult_Roundtrips()
    {
        var result = new ScreenshotCaptureResult
        {
            Path = "Screenshots/screenshot.png",
            Width = 1920,
            Height = 1080
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<ScreenshotCaptureResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Screenshots/screenshot.png", deserialized.Path);
        Assert.Equal(1920, deserialized.Width);
        Assert.Equal(1080, deserialized.Height);
    }

    [Fact]
    public void BridgeConfig_Roundtrips()
    {
        var config = new BridgeConfig
        {
            ProjectId = "proj-abc12345",
            Port = 49521,
            Pid = 12345
        };

        var json = JsonHelper.Serialize(config);
        var deserialized = JsonHelper.Deserialize<BridgeConfig>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("proj-abc12345", deserialized.ProjectId);
        Assert.Equal(49521, deserialized.Port);
        Assert.Equal(12345, deserialized.Pid);
    }

    [Fact]
    public void ErrorPayload_Roundtrips_WithDetails()
    {
        var error = new ErrorPayload
        {
            Code = "TIMEOUT",
            Message = "Operation timed out",
            Details = new Dictionary<string, object?>
            {
                ["command"] = "play.enter",
                ["timeoutSeconds"] = 30
            }
        };

        var json = JsonHelper.Serialize(error);
        var deserialized = JsonHelper.Deserialize<ErrorPayload>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("TIMEOUT", deserialized.Code);
        Assert.Equal("Operation timed out", deserialized.Message);
        Assert.NotNull(deserialized.Details);
        Assert.Equal("play.enter", deserialized.Details["command"]?.ToString());
    }

    [Fact]
    public void UnifiedLogEntry_Serializes_CamelCase()
    {
        var entry = new UnifiedLogEntry
        {
            SequenceNumber = 42,
            Timestamp = "2024-01-01T12:00:00Z",
            Source = "console",
            Level = "Error",
            Message = "NullReferenceException",
            StackTrace = "at Foo.Bar()"
        };

        var json = JsonHelper.Serialize(entry);
        var jObj = JObject.Parse(json);

        Assert.Equal(42, jObj["sequenceNumber"]?.Value<long>());
        Assert.Equal("console", jObj["source"]?.ToString());
        Assert.Equal("Error", jObj["level"]?.ToString());
    }

    [Fact]
    public void ScriptExecuteResult_Roundtrips()
    {
        var result = new ScriptExecuteResult
        {
            Success = true,
            Result = "42",
            Diagnostics = new[] { "warning CS0219" }
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<ScriptExecuteResult>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal("42", deserialized.Result);
        Assert.Single(deserialized.Diagnostics!);
    }

    [Fact]
    public void SnapshotResult_WithStageContext_Roundtrips()
    {
        var result = new SnapshotResult
        {
            Stage = "scene (editing)",
            SceneName = "MainScene",
            ScenePath = "Assets/Scenes/MainScene.unity",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 100, Name = "Camera" } }
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<SnapshotResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("scene (editing)", deserialized.Stage);
        Assert.Equal("MainScene", deserialized.SceneName);
        Assert.Null(deserialized.PrefabAssetPath);
    }

    [Fact]
    public void SnapshotResult_PrefabMode_OmitsSceneFields()
    {
        var result = new SnapshotResult
        {
            Stage = "prefab (isolated)",
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            HasUnsavedChanges = true,
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 99001, Name = "Player" } }
        };

        var json = JsonHelper.Serialize(result);
        var jObj = JObject.Parse(json);

        Assert.Equal("prefab (isolated)", jObj["stage"]?.ToString());
        Assert.Equal("Assets/Prefabs/Player.prefab", jObj["prefabAssetPath"]?.ToString());
        Assert.True(jObj["hasUnsavedChanges"]?.Value<bool>());
        // sceneName/scenePath should be absent (NullValueHandling.Ignore)
        Assert.Null(jObj["sceneName"]);
        Assert.Null(jObj["scenePath"]);
    }

    [Fact]
    public void SnapshotResult_PrefabAssetInspection_NoStage()
    {
        var result = new SnapshotResult
        {
            Stage = null,
            PrefabAssetPath = "Assets/Prefabs/TestCube.prefab",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 50000, Name = "TestCube" } }
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<SnapshotResult>(json);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.Stage);
        Assert.Equal("Assets/Prefabs/TestCube.prefab", deserialized.PrefabAssetPath);
    }

    [Fact]
    public void SnapshotObject_PrefabAnnotations_Roundtrip()
    {
        var obj = new SnapshotObject
        {
            InstanceId = 14200,
            Name = "Player",
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            PrefabAssetType = "Regular",
            IsPrefabInstanceRoot = true,
            ChildCount = 2
        };

        var json = JsonHelper.Serialize(obj);
        var deserialized = JsonHelper.Deserialize<SnapshotObject>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Assets/Prefabs/Player.prefab", deserialized.PrefabAssetPath);
        Assert.Equal("Regular", deserialized.PrefabAssetType);
        Assert.True(deserialized.IsPrefabInstanceRoot);
    }

    [Fact]
    public void SnapshotObject_NoPrefab_OmitsPrefabFields()
    {
        var obj = new SnapshotObject
        {
            InstanceId = 14300,
            Name = "Ground"
        };

        var json = JsonHelper.Serialize(obj);
        var jObj = JObject.Parse(json);

        // Prefab fields should be omitted (NullValueHandling.Ignore)
        Assert.Null(jObj["prefabAssetPath"]);
        Assert.Null(jObj["prefabAssetType"]);
        Assert.Null(jObj["isPrefabInstanceRoot"]);
    }

    [Fact]
    public void PrefabOpenResult_Roundtrips()
    {
        var result = new PrefabOpenResult
        {
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            Stage = "prefab (isolated)"
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<PrefabOpenResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Assets/Prefabs/Player.prefab", deserialized.PrefabAssetPath);
        Assert.Equal("prefab (isolated)", deserialized.Stage);
    }

    [Fact]
    public void PrefabCloseResult_Roundtrips()
    {
        var result = new PrefabCloseResult
        {
            ReturnedToScene = "MainScene"
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<PrefabCloseResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("MainScene", deserialized.ReturnedToScene);
    }
}
