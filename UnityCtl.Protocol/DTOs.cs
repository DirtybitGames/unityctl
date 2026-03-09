using System;
using System.Collections.Generic;
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

    [JsonProperty("bridgeVersion")]
    public string? BridgeVersion { get; init; }

    [JsonProperty("unityPluginVersion")]
    public string? UnityPluginVersion { get; init; }

    [JsonProperty("editorReady")]
    public required bool EditorReady { get; init; }
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

public class RecordStartResult
{
    [JsonProperty("recordingId")]
    public required string RecordingId { get; init; }

    [JsonProperty("outputPath")]
    public required string OutputPath { get; init; }

    [JsonProperty("state")]
    public required string State { get; init; }
}

public class RecordStopResult
{
    [JsonProperty("outputPath")]
    public required string OutputPath { get; init; }

    [JsonProperty("duration")]
    public required double Duration { get; init; }

    [JsonProperty("frameCount")]
    public required int FrameCount { get; init; }
}

public class RecordStatusResult
{
    [JsonProperty("isRecording")]
    public required bool IsRecording { get; init; }

    [JsonProperty("recordingId")]
    public string? RecordingId { get; init; }

    [JsonProperty("outputPath")]
    public string? OutputPath { get; init; }

    [JsonProperty("elapsed")]
    public double? Elapsed { get; init; }

    [JsonProperty("frameCount")]
    public int? FrameCount { get; init; }
}

public class RecordFinishedPayload
{
    [JsonProperty("recordingId")]
    public required string RecordingId { get; init; }

    [JsonProperty("outputPath")]
    public required string OutputPath { get; init; }

    [JsonProperty("duration")]
    public required double Duration { get; init; }

    [JsonProperty("frameCount")]
    public required int FrameCount { get; init; }
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

public class SnapshotResult
{
    [JsonProperty("stage")]
    public string? Stage { get; init; }  // "scene (editing)", "scene (playing)", "prefab (isolated)", "prefab (in-context)"

    [JsonProperty("sceneName", NullValueHandling = NullValueHandling.Ignore)]
    public string? SceneName { get; init; }

    [JsonProperty("scenePath", NullValueHandling = NullValueHandling.Ignore)]
    public string? ScenePath { get; init; }

    [JsonProperty("prefabAssetPath", NullValueHandling = NullValueHandling.Ignore)]
    public string? PrefabAssetPath { get; init; }

    [JsonProperty("hasUnsavedChanges", NullValueHandling = NullValueHandling.Ignore)]
    public bool? HasUnsavedChanges { get; init; }

    [JsonProperty("openedFromInstanceId", NullValueHandling = NullValueHandling.Ignore)]
    public int? OpenedFromInstanceId { get; init; }

    [JsonProperty("isPlaying")]
    public required bool IsPlaying { get; init; }

    [JsonProperty("rootObjectCount")]
    public required int RootObjectCount { get; init; }

    [JsonProperty("objects")]
    public required SnapshotObject[] Objects { get; init; }
}

public class SnapshotObject
{
    [JsonProperty("instanceId")]
    public int InstanceId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("active")]
    public bool Active { get; set; } = true;

    [JsonProperty("tag")]
    public string? Tag { get; set; }

    [JsonProperty("layer")]
    public string? Layer { get; set; }

    [JsonProperty("components")]
    public SnapshotComponent[]? Components { get; set; }

    [JsonProperty("position")]
    public string? Position { get; set; }

    [JsonProperty("scale")]
    public string? Scale { get; set; }

    [JsonProperty("rotation")]
    public string? Rotation { get; set; }

    [JsonProperty("rect")]
    public string? Rect { get; set; }

    [JsonProperty("anchors")]
    public string? Anchors { get; set; }

    [JsonProperty("pivot")]
    public string? Pivot { get; set; }

    [JsonProperty("text")]
    public string? Text { get; set; }

    [JsonProperty("interactable")]
    public bool? Interactable { get; set; }

    [JsonProperty("prefabAssetPath", NullValueHandling = NullValueHandling.Ignore)]
    public string? PrefabAssetPath { get; set; }

    [JsonProperty("prefabAssetType", NullValueHandling = NullValueHandling.Ignore)]
    public string? PrefabAssetType { get; set; }  // "Regular", "Variant", "Model"

    [JsonProperty("isPrefabInstanceRoot", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsPrefabInstanceRoot { get; set; }

    [JsonProperty("childCount")]
    public int ChildCount { get; set; }

    [JsonProperty("children")]
    public SnapshotObject[]? Children { get; set; }
}

public class PrefabOpenResult
{
    [JsonProperty("prefabAssetPath")]
    public required string PrefabAssetPath { get; init; }

    [JsonProperty("stage")]
    public required string Stage { get; init; }
}

public class PrefabCloseResult
{
    [JsonProperty("returnedToScene")]
    public required string ReturnedToScene { get; init; }
}

public class SnapshotComponent
{
    [JsonProperty("typeName")]
    public string TypeName { get; set; } = "";

    [JsonProperty("properties")]
    public Dictionary<string, object>? Properties { get; set; }
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
