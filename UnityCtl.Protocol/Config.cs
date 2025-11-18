using System.Text.Json.Serialization;

namespace UnityCtl.Protocol;

public class BridgeConfig
{
    [JsonPropertyName("projectId")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("pid")]
    public required int Pid { get; init; }
}
