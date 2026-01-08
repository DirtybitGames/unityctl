using System;
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

public class CompilationFinishedPayload
{
    [JsonProperty("success")]
    public required bool Success { get; init; }

    [JsonProperty("errors")]
    public CompilationMessageInfo[]? Errors { get; init; }

    [JsonProperty("warnings")]
    public CompilationMessageInfo[]? Warnings { get; init; }
}

public class CompilationMessageInfo
{
    [JsonProperty("file")]
    public string? File { get; init; }

    [JsonProperty("line")]
    public int Line { get; init; }

    [JsonProperty("column")]
    public int Column { get; init; }

    [JsonProperty("message")]
    public string? Message { get; init; }
}

public class PlayModeChangedPayload
{
    [JsonProperty("state")]
    public required string State { get; init; }

    [JsonProperty("compilationTriggered")]
    public bool CompilationTriggered { get; init; }
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

public class AssetRefreshCompletePayload
{
    [JsonProperty("compilationTriggered")]
    public bool CompilationTriggered { get; init; }

    [JsonProperty("hasCompilationErrors")]
    public bool HasCompilationErrors { get; init; }
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

public class TestRunResult
{
    [JsonProperty("started")]
    public required bool Started { get; init; }

    [JsonProperty("testRunId")]
    public string? TestRunId { get; init; }
}

public class TestFailureInfo
{
    [JsonProperty("testName")]
    public required string TestName { get; init; }

    [JsonProperty("message")]
    public required string Message { get; init; }

    [JsonProperty("stackTrace")]
    public string? StackTrace { get; init; }
}

public class TestFinishedPayload
{
    [JsonProperty("testRunId")]
    public required string TestRunId { get; init; }

    [JsonProperty("passed")]
    public required int Passed { get; init; }

    [JsonProperty("failed")]
    public required int Failed { get; init; }

    [JsonProperty("skipped")]
    public required int Skipped { get; init; }

    [JsonProperty("duration")]
    public required double Duration { get; init; }

    [JsonProperty("failures")]
    public TestFailureInfo[]? Failures { get; init; }
}

public class ScreenshotCaptureResult
{
    [JsonProperty("path")]
    public required string Path { get; init; }

    [JsonProperty("width")]
    public required int Width { get; init; }

    [JsonProperty("height")]
    public required int Height { get; init; }
}

public class ScriptExecuteResult
{
    [JsonProperty("success")]
    public required bool Success { get; init; }

    [JsonProperty("result")]
    public string? Result { get; init; }

    [JsonProperty("error")]
    public string? Error { get; init; }

    [JsonProperty("diagnostics")]
    public string[]? Diagnostics { get; init; }
}

public class ProjectStatusResult
{
    [JsonProperty("projectPath")]
    public required string ProjectPath { get; init; }

    [JsonProperty("projectId")]
    public required string ProjectId { get; init; }

    [JsonProperty("unityEditorRunning")]
    public required bool UnityEditorRunning { get; init; }

    [JsonProperty("unityEditorStatus")]
    public required string UnityEditorStatus { get; init; }

    [JsonProperty("bridgeConfigured")]
    public required bool BridgeConfigured { get; init; }

    [JsonProperty("bridgeRunning")]
    public required bool BridgeRunning { get; init; }

    [JsonProperty("bridgePort")]
    public int? BridgePort { get; init; }

    [JsonProperty("bridgePid")]
    public int? BridgePid { get; init; }

    [JsonProperty("unityConnectedToBridge")]
    public required bool UnityConnectedToBridge { get; init; }
}

/// <summary>
/// Unified log entry that can come from either editor.log or console (Debug.Log)
/// </summary>
public class UnifiedLogEntry
{
    [JsonProperty("sequenceNumber")]
    public long SequenceNumber { get; set; }

    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonProperty("source")]
    public string Source { get; set; } = "";  // "editor" or "console"

    [JsonProperty("level")]
    public string Level { get; set; } = "";   // "Log", "Warning", "Error", "Exception", or "Info" for editor

    [JsonProperty("message")]
    public string Message { get; set; } = "";

    [JsonProperty("stackTrace")]
    public string? StackTrace { get; set; }

    [JsonProperty("color")]
    public ConsoleColor? Color { get; set; }
}
