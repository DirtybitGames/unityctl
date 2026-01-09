using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityCtl.Protocol;

[JsonConverter(typeof(BaseMessageConverter))]
public abstract class BaseMessage
{
    [JsonProperty("type")]
    public abstract string Type { get; }

    [JsonProperty("origin")]
    public required string Origin { get; init; }
}

public class HelloMessage : BaseMessage
{
    public override string Type => "hello";

    [JsonProperty("projectId")]
    public required string ProjectId { get; init; }

    [JsonProperty("unityVersion")]
    public string? UnityVersion { get; init; }

    [JsonProperty("editorInstanceId")]
    public string? EditorInstanceId { get; init; }

    [JsonProperty("capabilities")]
    public string[]? Capabilities { get; init; }

    [JsonProperty("protocolVersion")]
    public string? ProtocolVersion { get; init; }

    [JsonProperty("pluginVersion")]
    public string? PluginVersion { get; init; }
}

public class RequestMessage : BaseMessage
{
    public override string Type => "request";

    [JsonProperty("requestId")]
    public required string RequestId { get; init; }

    [JsonProperty("agentId")]
    public string? AgentId { get; init; }

    [JsonProperty("command")]
    public required string Command { get; init; }

    [JsonProperty("args")]
    public Dictionary<string, object?>? Args { get; init; }
}

public class ResponseMessage : BaseMessage
{
    public override string Type => "response";

    [JsonProperty("requestId")]
    public required string RequestId { get; init; }

    [JsonProperty("status")]
    public required string Status { get; init; }

    [JsonProperty("result")]
    public object? Result { get; init; }

    [JsonProperty("error")]
    public ErrorPayload? Error { get; init; }
}

public class EventMessage : BaseMessage
{
    public override string Type => "event";

    [JsonProperty("event")]
    public required string Event { get; init; }

    [JsonProperty("payload")]
    public required object Payload { get; init; }
}

public class ErrorPayload
{
    [JsonProperty("code")]
    public required string Code { get; init; }

    [JsonProperty("message")]
    public required string Message { get; init; }

    [JsonProperty("details")]
    public Dictionary<string, object?>? Details { get; init; }
}

public class BaseMessageConverter : JsonConverter<BaseMessage>
{
    public override BaseMessage? ReadJson(JsonReader reader, Type objectType, BaseMessage? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var jObject = JObject.Load(reader);
        var type = jObject["type"]?.ToString();

        BaseMessage? message = type switch
        {
            "hello" => new HelloMessage
            {
                Origin = jObject["origin"]!.ToString(),
                ProjectId = jObject["projectId"]!.ToString(),
                UnityVersion = jObject["unityVersion"]?.ToString(),
                EditorInstanceId = jObject["editorInstanceId"]?.ToString(),
                Capabilities = jObject["capabilities"]?.ToObject<string[]>(),
                ProtocolVersion = jObject["protocolVersion"]?.ToString(),
                PluginVersion = jObject["pluginVersion"]?.ToString()
            },
            "request" => new RequestMessage
            {
                Origin = jObject["origin"]!.ToString(),
                RequestId = jObject["requestId"]!.ToString(),
                AgentId = jObject["agentId"]?.ToString(),
                Command = jObject["command"]!.ToString(),
                Args = jObject["args"]?.ToObject<Dictionary<string, object?>>()
            },
            "response" => new ResponseMessage
            {
                Origin = jObject["origin"]!.ToString(),
                RequestId = jObject["requestId"]!.ToString(),
                Status = jObject["status"]!.ToString(),
                Result = jObject["result"]?.ToObject<object>(),
                Error = jObject["error"]?.ToObject<ErrorPayload>()
            },
            "event" => new EventMessage
            {
                Origin = jObject["origin"]!.ToString(),
                Event = jObject["event"]!.ToString(),
                Payload = jObject["payload"]!.ToObject<object>()!
            },
            _ => throw new JsonSerializationException($"Unknown message type: {type}")
        };

        return message;
    }

    public override void WriteJson(JsonWriter writer, BaseMessage? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        // Manually serialize to avoid infinite recursion
        writer.WriteStartObject();

        // Write type property
        writer.WritePropertyName("type");
        writer.WriteValue(value.Type);

        // Write origin property
        writer.WritePropertyName("origin");
        writer.WriteValue(value.Origin);

        // Write type-specific properties
        switch (value)
        {
            case HelloMessage hello:
                writer.WritePropertyName("projectId");
                writer.WriteValue(hello.ProjectId);
                if (hello.UnityVersion != null)
                {
                    writer.WritePropertyName("unityVersion");
                    writer.WriteValue(hello.UnityVersion);
                }
                if (hello.EditorInstanceId != null)
                {
                    writer.WritePropertyName("editorInstanceId");
                    writer.WriteValue(hello.EditorInstanceId);
                }
                if (hello.Capabilities != null)
                {
                    writer.WritePropertyName("capabilities");
                    serializer.Serialize(writer, hello.Capabilities);
                }
                if (hello.ProtocolVersion != null)
                {
                    writer.WritePropertyName("protocolVersion");
                    writer.WriteValue(hello.ProtocolVersion);
                }
                if (hello.PluginVersion != null)
                {
                    writer.WritePropertyName("pluginVersion");
                    writer.WriteValue(hello.PluginVersion);
                }
                break;

            case RequestMessage request:
                writer.WritePropertyName("requestId");
                writer.WriteValue(request.RequestId);
                if (request.AgentId != null)
                {
                    writer.WritePropertyName("agentId");
                    writer.WriteValue(request.AgentId);
                }
                writer.WritePropertyName("command");
                writer.WriteValue(request.Command);
                if (request.Args != null)
                {
                    writer.WritePropertyName("args");
                    serializer.Serialize(writer, request.Args);
                }
                break;

            case ResponseMessage response:
                writer.WritePropertyName("requestId");
                writer.WriteValue(response.RequestId);
                writer.WritePropertyName("status");
                writer.WriteValue(response.Status);
                if (response.Result != null)
                {
                    writer.WritePropertyName("result");
                    serializer.Serialize(writer, response.Result);
                }
                if (response.Error != null)
                {
                    writer.WritePropertyName("error");
                    serializer.Serialize(writer, response.Error);
                }
                break;

            case EventMessage eventMsg:
                writer.WritePropertyName("event");
                writer.WriteValue(eventMsg.Event);
                writer.WritePropertyName("payload");
                serializer.Serialize(writer, eventMsg.Payload);
                break;
        }

        writer.WriteEndObject();
    }
}
