using System.Text.Json.Serialization;

namespace UnityCtl.Protocol;

public class HealthResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("projectId")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("unityConnected")]
    public required bool UnityConnected { get; init; }
}

public class ConsoleTailResult
{
    [JsonPropertyName("entries")]
    public required LogEntry[] Entries { get; init; }
}

public class LogEntry
{
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }
}

public class SceneInfo
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("enabledInBuild")]
    public bool EnabledInBuild { get; init; }
}

public class SceneListResult
{
    [JsonPropertyName("scenes")]
    public required SceneInfo[] Scenes { get; init; }
}

public class SceneLoadResult
{
    [JsonPropertyName("loadedScenePath")]
    public required string LoadedScenePath { get; init; }
}

public class PlayModeResult
{
    [JsonPropertyName("state")]
    public required string State { get; init; }
}

public class AssetImportResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }
}

public class CompileResult
{
    [JsonPropertyName("started")]
    public required bool Started { get; init; }
}

public class CompilationFinishedPayload
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }
}

public class PlayModeChangedPayload
{
    [JsonPropertyName("state")]
    public required string State { get; init; }
}
