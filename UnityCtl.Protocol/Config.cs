using Newtonsoft.Json;

namespace UnityCtl.Protocol;

public class BridgeConfig
{
    [JsonProperty("projectId")]
    public required string ProjectId { get; init; }

    [JsonProperty("port")]
    public required int Port { get; init; }

    [JsonProperty("pid")]
    public required int Pid { get; init; }
}
