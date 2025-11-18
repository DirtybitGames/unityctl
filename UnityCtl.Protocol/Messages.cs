using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UnityCtl.Protocol;

[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(RequestMessage), "request")]
[JsonDerivedType(typeof(ResponseMessage), "response")]
[JsonDerivedType(typeof(EventMessage), "event")]
public abstract class BaseMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("origin")]
    public required string Origin { get; init; }
}

public class HelloMessage : BaseMessage
{
    public override string Type => "hello";

    [JsonPropertyName("projectId")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("unityVersion")]
    public string? UnityVersion { get; init; }

    [JsonPropertyName("editorInstanceId")]
    public string? EditorInstanceId { get; init; }

    [JsonPropertyName("capabilities")]
    public string[]? Capabilities { get; init; }

    [JsonPropertyName("protocolVersion")]
    public string? ProtocolVersion { get; init; }
}

public class RequestMessage : BaseMessage
{
    public override string Type => "request";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("args")]
    public Dictionary<string, object?>? Args { get; init; }
}

public class ResponseMessage : BaseMessage
{
    public override string Type => "response";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("result")]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    public ErrorPayload? Error { get; init; }
}

public class EventMessage : BaseMessage
{
    public override string Type => "event";

    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("payload")]
    public required object Payload { get; init; }
}

public class ErrorPayload
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("details")]
    public Dictionary<string, object?>? Details { get; init; }
}
