using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Protocol;

public class MessageSerializationTests
{
    #region HelloMessage

    [Fact]
    public void HelloMessage_Serializes_WithCorrectType()
    {
        var hello = new HelloMessage
        {
            Origin = MessageOrigin.Unity,
            ProjectId = "proj-abc12345",
            UnityVersion = "6000.0.0f1",
            ProtocolVersion = "1.0.0",
            PluginVersion = "0.3.6",
            Capabilities = new[] { "scene", "play" }
        };

        var json = JsonHelper.Serialize(hello);
        var jObj = JObject.Parse(json);

        Assert.Equal("hello", jObj["type"]?.ToString());
        Assert.Equal("unity", jObj["origin"]?.ToString());
        Assert.Equal("proj-abc12345", jObj["projectId"]?.ToString());
        Assert.Equal("6000.0.0f1", jObj["unityVersion"]?.ToString());
        Assert.Equal("1.0.0", jObj["protocolVersion"]?.ToString());
        Assert.Equal("0.3.6", jObj["pluginVersion"]?.ToString());
        Assert.Equal(2, jObj["capabilities"]?.Count());
    }

    [Fact]
    public void HelloMessage_Roundtrips_ThroughJson()
    {
        var original = new HelloMessage
        {
            Origin = MessageOrigin.Unity,
            ProjectId = "proj-abc12345",
            UnityVersion = "6000.0.0f1",
            ProtocolVersion = "1.0.0",
            PluginVersion = "0.3.6",
            Capabilities = new[] { "scene", "play", "test" }
        };

        var json = JsonHelper.Serialize(original);
        var deserialized = JsonHelper.Deserialize<BaseMessage>(json) as HelloMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.ProjectId, deserialized.ProjectId);
        Assert.Equal(original.UnityVersion, deserialized.UnityVersion);
        Assert.Equal(original.ProtocolVersion, deserialized.ProtocolVersion);
        Assert.Equal(original.PluginVersion, deserialized.PluginVersion);
        Assert.Equal(original.Capabilities, deserialized.Capabilities);
        Assert.Equal(original.Origin, deserialized.Origin);
    }

    [Fact]
    public void HelloMessage_OmitsNullOptionalFields()
    {
        var hello = new HelloMessage
        {
            Origin = MessageOrigin.Unity,
            ProjectId = "proj-abc12345"
        };

        var json = JsonHelper.Serialize(hello);
        var jObj = JObject.Parse(json);

        Assert.Null(jObj["unityVersion"]);
        Assert.Null(jObj["editorInstanceId"]);
        Assert.Null(jObj["capabilities"]);
    }

    #endregion

    #region RequestMessage

    [Fact]
    public void RequestMessage_Serializes_WithCorrectType()
    {
        var request = new RequestMessage
        {
            Origin = MessageOrigin.Bridge,
            RequestId = "req-001",
            Command = UnityCtlCommands.SceneLoad,
            Args = new Dictionary<string, object?> { ["path"] = "Assets/Scenes/Main.unity" },
            AgentId = "agent-1"
        };

        var json = JsonHelper.Serialize(request);
        var jObj = JObject.Parse(json);

        Assert.Equal("request", jObj["type"]?.ToString());
        Assert.Equal("bridge", jObj["origin"]?.ToString());
        Assert.Equal("req-001", jObj["requestId"]?.ToString());
        Assert.Equal("scene.load", jObj["command"]?.ToString());
        Assert.Equal("agent-1", jObj["agentId"]?.ToString());
        Assert.Equal("Assets/Scenes/Main.unity", jObj["args"]?["path"]?.ToString());
    }

    [Fact]
    public void RequestMessage_Roundtrips_ThroughJson()
    {
        var original = new RequestMessage
        {
            Origin = MessageOrigin.Bridge,
            RequestId = "req-002",
            Command = UnityCtlCommands.PlayEnter,
            Args = new Dictionary<string, object?> { ["force"] = true }
        };

        var json = JsonHelper.Serialize(original);
        var deserialized = JsonHelper.Deserialize<BaseMessage>(json) as RequestMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.RequestId, deserialized.RequestId);
        Assert.Equal(original.Command, deserialized.Command);
        Assert.Equal(original.Origin, deserialized.Origin);
        Assert.Null(deserialized.AgentId);
    }

    [Fact]
    public void RequestMessage_OmitsNullArgs()
    {
        var request = new RequestMessage
        {
            Origin = MessageOrigin.Bridge,
            RequestId = "req-003",
            Command = UnityCtlCommands.PlayStatus
        };

        var json = JsonHelper.Serialize(request);
        var jObj = JObject.Parse(json);

        Assert.Null(jObj["args"]);
        Assert.Null(jObj["agentId"]);
    }

    #endregion

    #region ResponseMessage

    [Fact]
    public void ResponseMessage_Ok_Serializes()
    {
        var response = new ResponseMessage
        {
            Origin = MessageOrigin.Unity,
            RequestId = "req-001",
            Status = ResponseStatus.Ok,
            Result = new { state = "playing" }
        };

        var json = JsonHelper.Serialize(response);
        var jObj = JObject.Parse(json);

        Assert.Equal("response", jObj["type"]?.ToString());
        Assert.Equal("ok", jObj["status"]?.ToString());
        Assert.Equal("playing", jObj["result"]?["state"]?.ToString());
        Assert.Null(jObj["error"]);
    }

    [Fact]
    public void ResponseMessage_Error_Serializes()
    {
        var response = new ResponseMessage
        {
            Origin = MessageOrigin.Unity,
            RequestId = "req-001",
            Status = ResponseStatus.Error,
            Error = new ErrorPayload
            {
                Code = "COMPILATION_ERROR",
                Message = "Compilation failed with 3 error(s)",
                Details = new Dictionary<string, object?> { ["errorCount"] = 3 }
            }
        };

        var json = JsonHelper.Serialize(response);
        var jObj = JObject.Parse(json);

        Assert.Equal("error", jObj["status"]?.ToString());
        Assert.Equal("COMPILATION_ERROR", jObj["error"]?["code"]?.ToString());
        Assert.Equal("Compilation failed with 3 error(s)", jObj["error"]?["message"]?.ToString());
    }

    [Fact]
    public void ResponseMessage_Roundtrips_ThroughJson()
    {
        var original = new ResponseMessage
        {
            Origin = MessageOrigin.Unity,
            RequestId = "req-001",
            Status = ResponseStatus.Ok,
            Result = new { loaded = true, path = "Assets/Scenes/Main.unity" }
        };

        var json = JsonHelper.Serialize(original);
        var deserialized = JsonHelper.Deserialize<BaseMessage>(json) as ResponseMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.RequestId, deserialized.RequestId);
        Assert.Equal(original.Status, deserialized.Status);
        Assert.Null(deserialized.Error);
    }

    #endregion

    #region EventMessage

    [Fact]
    public void EventMessage_Serializes_WithCorrectType()
    {
        var eventMsg = new EventMessage
        {
            Origin = MessageOrigin.Unity,
            Event = UnityCtlEvents.PlayModeChanged,
            Payload = new { state = "EnteredPlayMode" }
        };

        var json = JsonHelper.Serialize(eventMsg);
        var jObj = JObject.Parse(json);

        Assert.Equal("event", jObj["type"]?.ToString());
        Assert.Equal("playModeChanged", jObj["event"]?.ToString());
        Assert.Equal("EnteredPlayMode", jObj["payload"]?["state"]?.ToString());
    }

    [Fact]
    public void EventMessage_Roundtrips_ThroughJson()
    {
        var original = new EventMessage
        {
            Origin = MessageOrigin.Unity,
            Event = UnityCtlEvents.CompilationFinished,
            Payload = new { success = true, errors = new object[0] }
        };

        var json = JsonHelper.Serialize(original);
        var deserialized = JsonHelper.Deserialize<BaseMessage>(json) as EventMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.Event, deserialized.Event);
        Assert.Equal(original.Origin, deserialized.Origin);
    }

    #endregion

    #region Type Discrimination

    [Fact]
    public void Deserialize_DiscriminatesByTypeField()
    {
        var helloJson = """{"type":"hello","origin":"unity","projectId":"proj-test"}""";
        var requestJson = """{"type":"request","origin":"bridge","requestId":"r1","command":"play.enter"}""";
        var responseJson = """{"type":"response","origin":"unity","requestId":"r1","status":"ok"}""";
        var eventJson = """{"type":"event","origin":"unity","event":"log","payload":{"message":"test"}}""";

        Assert.IsType<HelloMessage>(JsonHelper.Deserialize<BaseMessage>(helloJson));
        Assert.IsType<RequestMessage>(JsonHelper.Deserialize<BaseMessage>(requestJson));
        Assert.IsType<ResponseMessage>(JsonHelper.Deserialize<BaseMessage>(responseJson));
        Assert.IsType<EventMessage>(JsonHelper.Deserialize<BaseMessage>(eventJson));
    }

    [Fact]
    public void Deserialize_UnknownType_ThrowsJsonSerializationException()
    {
        var json = """{"type":"unknown","origin":"test"}""";

        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() =>
            JsonHelper.Deserialize<BaseMessage>(json));
    }

    #endregion
}
