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
            BridgeVersion = "0.3.6",
            UnityPluginVersion = "0.3.6"
        };

        var json = JsonHelper.Serialize(health);
        var jObj = JObject.Parse(json);

        Assert.Equal("ok", jObj["status"]?.ToString());
        Assert.Equal("proj-abc", jObj["projectId"]?.ToString());
        Assert.True(jObj["unityConnected"]?.Value<bool>());
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
            Path = "/tmp/screenshot.png",
            Width = 1920,
            Height = 1080
        };

        var json = JsonHelper.Serialize(result);
        var deserialized = JsonHelper.Deserialize<ScreenshotCaptureResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("/tmp/screenshot.png", deserialized.Path);
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
}
