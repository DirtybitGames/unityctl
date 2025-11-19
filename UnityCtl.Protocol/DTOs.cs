using Newtonsoft.Json;

namespace UnityCtl.Protocol;

public class HealthResult
{
    [JsonProperty("status")]
    public required string Status { get; init; }

    [JsonProperty("projectId")]
    public required string ProjectId { get; init; }

    [JsonProperty("unityConnected")]
    public required bool UnityConnected { get; init; }
}

public class ConsoleTailResult
{
    [JsonProperty("entries")]
    public required LogEntry[] Entries { get; init; }
}

public class LogEntry
{
    [JsonProperty("timestamp")]
    public required string Timestamp { get; init; }

    [JsonProperty("level")]
    public required string Level { get; init; }

    [JsonProperty("message")]
    public required string Message { get; init; }

    [JsonProperty("stackTrace")]
    public string? StackTrace { get; init; }
}

public class SceneInfo
{
    [JsonProperty("path")]
    public required string Path { get; init; }

    [JsonProperty("enabledInBuild")]
    public bool EnabledInBuild { get; init; }
}

public class SceneListResult
{
    [JsonProperty("scenes")]
    public required SceneInfo[] Scenes { get; init; }
}

public class SceneLoadResult
{
    [JsonProperty("loadedScenePath")]
    public required string LoadedScenePath { get; init; }
}

public class PlayModeResult
{
    [JsonProperty("state")]
    public required string State { get; init; }
}

public class AssetImportResult
{
    [JsonProperty("success")]
    public required bool Success { get; init; }
}

public class CompileResult
{
    [JsonProperty("started")]
    public required bool Started { get; init; }
}

public class CompilationFinishedPayload
{
    [JsonProperty("success")]
    public required bool Success { get; init; }
}

public class PlayModeChangedPayload
{
    [JsonProperty("state")]
    public required string State { get; init; }
}

public class AssetImportCompletePayload
{
    [JsonProperty("path")]
    public required string Path { get; init; }

    [JsonProperty("success")]
    public required bool Success { get; init; }
}

public class AssetReimportCompletePayload
{
    [JsonProperty("success")]
    public required bool Success { get; init; }
}

public class MenuItemInfo
{
    [JsonProperty("path")]
    public required string Path { get; init; }

    [JsonProperty("priority")]
    public int Priority { get; init; }
}

public class MenuListResult
{
    [JsonProperty("menuItems")]
    public required MenuItemInfo[] MenuItems { get; init; }
}

public class MenuExecuteResult
{
    [JsonProperty("success")]
    public required bool Success { get; init; }

    [JsonProperty("menuPath")]
    public required string MenuPath { get; init; }

    [JsonProperty("message")]
    public string? Message { get; init; }
}
