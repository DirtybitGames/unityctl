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

    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
    public string? Name { get; init; }

    [JsonProperty("enabledInBuild")]
    public bool EnabledInBuild { get; init; }

    [JsonProperty("isActive", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool IsActive { get; init; }
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

    [JsonProperty("pauseOnPlay", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool PauseOnPlay { get; init; }
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
    /// <summary>
    /// Project-relative path where CaptureScreenshot writes the file (e.g. "Screenshots/shot.png").
    /// CLI resolves this against the project root, waits for the file, and moves it if needed.
    /// </summary>
    [JsonProperty("path")]
    public required string Path { get; init; }

    [JsonProperty("width")]
    public required int Width { get; init; }

    [JsonProperty("height")]
    public required int Height { get; init; }
}

public class EditorWindowInfo
{
    [JsonProperty("type")]
    public required string Type { get; init; }

    [JsonProperty("title")]
    public required string Title { get; init; }

    [JsonProperty("width")]
    public required int Width { get; init; }

    [JsonProperty("height")]
    public required int Height { get; init; }

    [JsonProperty("docked")]
    public required bool Docked { get; init; }
}

public class ScreenshotListWindowsResult
{
    [JsonProperty("windows")]
    public required EditorWindowInfo[] Windows { get; init; }
}

public class ScreenshotWindowResult
{
    [JsonProperty("path")]
    public required string Path { get; init; }

    [JsonProperty("width")]
    public required int Width { get; init; }

    [JsonProperty("height")]
    public required int Height { get; init; }

    [JsonProperty("windowType")]
    public required string WindowType { get; init; }

    [JsonProperty("windowTitle")]
    public required string WindowTitle { get; init; }
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

    [JsonProperty("hints")]
    public string[]? Hints { get; init; }
}

public class ScriptLookupTypeMatch
{
    [JsonProperty("fullName")]
    public required string FullName { get; init; }

    [JsonProperty("namespace")]
    public string? Namespace { get; init; }

    [JsonProperty("assembly")]
    public required string Assembly { get; init; }

    [JsonProperty("kind")]
    public required string Kind { get; init; } // "class", "struct", "interface", "enum", "delegate"

    [JsonProperty("isStatic")]
    public bool IsStatic { get; init; }
}

public class ScriptLookupTypeResult
{
    [JsonProperty("query")]
    public required string Query { get; init; }

    [JsonProperty("matches")]
    public required ScriptLookupTypeMatch[] Matches { get; init; }

    [JsonProperty("truncated")]
    public bool Truncated { get; init; }
}

public class ScriptMemberDto
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("kind")]
    public required string Kind { get; init; } // property|method|field|event|nested-type

    [JsonProperty("signature")]
    public required string Signature { get; init; }

    [JsonProperty("isStatic")]
    public bool IsStatic { get; init; }

    [JsonProperty("declaringType")]
    public string? DeclaringType { get; init; }
}

public class ScriptMembersResult
{
    [JsonProperty("query")]
    public required string Query { get; init; }

    [JsonProperty("resolvedType")]
    public string? ResolvedType { get; init; }

    [JsonProperty("assembly")]
    public string? Assembly { get; init; }

    [JsonProperty("members")]
    public required ScriptMemberDto[] Members { get; init; }

    [JsonProperty("truncated")]
    public bool Truncated { get; init; }
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

public class ProfileStatInfo
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("category")]
    public required string Category { get; init; }

    [JsonProperty("unit")]
    public required string Unit { get; init; }

    [JsonProperty("dataType")]
    public required string DataType { get; init; }
}

public class ProfileListStatsResult
{
    [JsonProperty("count")]
    public required int Count { get; init; }

    [JsonProperty("stats")]
    public required ProfileStatInfo[] Stats { get; init; }
}

public class ProfileStartResult
{
    [JsonProperty("sessionId")]
    public required string SessionId { get; init; }

    [JsonProperty("stats")]
    public required string[] Stats { get; init; }

    [JsonProperty("startedAt")]
    public required string StartedAt { get; init; }

    [JsonProperty("maxDurationSeconds")]
    public double? MaxDurationSeconds { get; init; }

    [JsonProperty("target")]
    public string? Target { get; init; }

    [JsonProperty("targetIsRemote")]
    public bool TargetIsRemote { get; init; }

    [JsonProperty("savePath", NullValueHandling = NullValueHandling.Ignore)]
    public string? SavePath { get; init; }
}

public class ProfileStatSummary
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("unit")]
    public required string Unit { get; init; }

    [JsonProperty("frames")]
    public required int Frames { get; init; }

    [JsonProperty("avg")]
    public required double Avg { get; init; }

    [JsonProperty("min")]
    public required double Min { get; init; }

    [JsonProperty("max")]
    public required double Max { get; init; }

    [JsonProperty("p50")]
    public required double P50 { get; init; }

    [JsonProperty("p95")]
    public required double P95 { get; init; }

    [JsonProperty("p99")]
    public required double P99 { get; init; }

    [JsonProperty("samples", NullValueHandling = NullValueHandling.Ignore)]
    public double[]? Samples { get; init; }
}

public class ProfileHitch
{
    [JsonProperty("frameIndex")]
    public required int FrameIndex { get; init; }

    [JsonProperty("frameTimeMs")]
    public required double FrameTimeMs { get; init; }

    /// <summary>
    /// Frame index in the profiler buffer (usable with `profile explain`).
    /// Null for remote captures or when the profiler buffer wasn't driven for this session.
    /// </summary>
    [JsonProperty("absoluteFrameIndex", NullValueHandling = NullValueHandling.Ignore)]
    public int? AbsoluteFrameIndex { get; init; }
}

/// <summary>
/// Descendant of a driver where the wall-clock is actually spent. The driver itself is usually
/// an intermediate node (selfMs ~ 0); `hot` points at the marker inside its subtree with the
/// highest selfMs — the "what's slow" answer for the agent without a separate `profile frame` call.
/// </summary>
public class ProfileFrameHotLeaf
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("selfMs")]
    public required double SelfMs { get; init; }

    [JsonProperty("totalMs")]
    public required double TotalMs { get; init; }
}

/// <summary>
/// Top-level system marker driving a frame. "Drivers" are descendants of the hierarchy root past
/// dominant-single-child levels, ranked by total time — answers "which subsystem is responsible
/// for this frame's cost", not "which leaf has the highest self-time".
/// </summary>
public class ProfileFrameDriver
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("totalMs")]
    public required double TotalMs { get; init; }

    [JsonProperty("selfMs")]
    public required double SelfMs { get; init; }

    [JsonProperty("calls")]
    public required int Calls { get; init; }

    [JsonProperty("gcKb", NullValueHandling = NullValueHandling.Ignore)]
    public double? GcKb { get; init; }

    /// <summary>
    /// Hottest descendant by self time inside this driver's subtree. Null when the driver itself
    /// is the leaf or no descendant has meaningful self time.
    /// </summary>
    [JsonProperty("hot", NullValueHandling = NullValueHandling.Ignore)]
    public ProfileFrameHotLeaf? Hot { get; init; }
}

/// <summary>
/// One of the worst frames in a capture. Carries both the relative index (into samples arrays)
/// and the absolute index (into the profiler buffer, usable with `profile frame` / `profile explain`).
///
/// Ranking metric depends on what's available: when the capture was taken in play mode (PlayerLoop
/// is in the hierarchy), frames are ranked by `playerLoopMs` — the time spent in actual game work.
/// Otherwise ranked by `cpuMainMs` (full main thread including editor IMGUI). Both fields are
/// always populated when available so consumers can see both signals.
/// </summary>
public class ProfileTopFrame
{
    [JsonProperty("frameIndex")]
    public required int FrameIndex { get; init; }

    [JsonProperty("absoluteFrameIndex")]
    public required int AbsoluteFrameIndex { get; init; }

    [JsonProperty("cpuMainMs")]
    public required double CpuMainMs { get; init; }

    [JsonProperty("frameTimeMs")]
    public required double FrameTimeMs { get; init; }

    /// <summary>
    /// Time spent in PlayerLoop on this frame (gameplay-only, excluding editor IMGUI/repaint).
    /// Null when PlayerLoop isn't in the hierarchy (editor mode without play, or top-level
    /// remote captures where the hierarchy is rooted differently).
    /// </summary>
    [JsonProperty("playerLoopMs", NullValueHandling = NullValueHandling.Ignore)]
    public double? PlayerLoopMs { get; init; }

    [JsonProperty("drivers")]
    public required ProfileFrameDriver[] Drivers { get; init; }
}

public class ProfileStopResult
{
    [JsonProperty("sessionId")]
    public required string SessionId { get; init; }

    [JsonProperty("durationSeconds")]
    public required double DurationSeconds { get; init; }

    [JsonProperty("frames")]
    public required int Frames { get; init; }

    [JsonProperty("summaries")]
    public required ProfileStatSummary[] Summaries { get; init; }

    [JsonProperty("hitches", NullValueHandling = NullValueHandling.Ignore)]
    public ProfileHitch[]? Hitches { get; init; }

    /// <summary>
    /// Top frames by CPU main thread time, with the top-3 driver markers attached. Always
    /// populated for non-empty captures so agents can see spikes without dumping samples.
    /// Distinct from `hitches` (which gates against an absolute threshold and uses total
    /// frame time — useful for CI but vsync-blind).
    /// </summary>
    [JsonProperty("topFrames", NullValueHandling = NullValueHandling.Ignore)]
    public ProfileTopFrame[]? TopFrames { get; init; }

    [JsonProperty("savedPath", NullValueHandling = NullValueHandling.Ignore)]
    public string? SavedPath { get; init; }

    [JsonProperty("target", NullValueHandling = NullValueHandling.Ignore)]
    public string? Target { get; init; }

    [JsonProperty("targetIsRemote")]
    public bool TargetIsRemote { get; init; }
}

public class ProfileStatusEntry
{
    [JsonProperty("sessionId")]
    public required string SessionId { get; init; }

    [JsonProperty("startedAt")]
    public required string StartedAt { get; init; }

    [JsonProperty("elapsedSeconds")]
    public required double ElapsedSeconds { get; init; }

    [JsonProperty("frames")]
    public required int Frames { get; init; }

    [JsonProperty("stats")]
    public required string[] Stats { get; init; }

    [JsonProperty("maxDurationSeconds")]
    public double? MaxDurationSeconds { get; init; }

    [JsonProperty("target", NullValueHandling = NullValueHandling.Ignore)]
    public string? Target { get; init; }

    [JsonProperty("targetIsRemote")]
    public bool TargetIsRemote { get; init; }
}

public class ProfileStatusResult
{
    [JsonProperty("sessions")]
    public required ProfileStatusEntry[] Sessions { get; init; }
}

public class ProfileSnapshotResult
{
    [JsonProperty("path")]
    public required string Path { get; init; }

    [JsonProperty("sizeBytes")]
    public long SizeBytes { get; init; }
}

public class ProfileTargetInfo
{
    [JsonProperty("id")]
    public required string Id { get; init; }

    [JsonProperty("displayName")]
    public required string DisplayName { get; init; }

    [JsonProperty("kind")]
    public required string Kind { get; init; } // "editor" | "player"

    [JsonProperty("isCurrent")]
    public bool IsCurrent { get; init; }
}

public class ProfileTargetsResult
{
    [JsonProperty("targets")]
    public required ProfileTargetInfo[] Targets { get; init; }
}

public class ProfileMarkerEntry
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("selfTimeMs")]
    public required double SelfTimeMs { get; init; }

    [JsonProperty("calls")]
    public required int Calls { get; init; }

    [JsonProperty("gcAllocBytes", NullValueHandling = NullValueHandling.Ignore)]
    public long? GcAllocBytes { get; init; }
}

public class ProfileExplainResult
{
    [JsonProperty("frameIndex")]
    public required int FrameIndex { get; init; }

    [JsonProperty("threadIndex")]
    public required int ThreadIndex { get; init; }

    [JsonProperty("threadName")]
    public required string ThreadName { get; init; }

    [JsonProperty("frameTimeMs")]
    public required double FrameTimeMs { get; init; }

    [JsonProperty("topMarkers")]
    public required ProfileMarkerEntry[] TopMarkers { get; init; }
}

public class ProfileHotspotsResult
{
    [JsonProperty("startFrame")]
    public required int StartFrame { get; init; }

    [JsonProperty("endFrame")]
    public required int EndFrame { get; init; }

    [JsonProperty("frameCount")]
    public required int FrameCount { get; init; }

    [JsonProperty("threadIndex")]
    public required int ThreadIndex { get; init; }

    [JsonProperty("threadName")]
    public required string ThreadName { get; init; }

    [JsonProperty("topMarkers")]
    public required ProfileMarkerEntry[] TopMarkers { get; init; }
}

public class ProfileFrameNode
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("selfMs")]
    public required double SelfMs { get; init; }

    [JsonProperty("totalMs")]
    public required double TotalMs { get; init; }

    [JsonProperty("calls")]
    public required int Calls { get; init; }

    [JsonProperty("gcKb", NullValueHandling = NullValueHandling.Ignore)]
    public double? GcKb { get; init; }

    [JsonProperty("children", NullValueHandling = NullValueHandling.Ignore)]
    public ProfileFrameNode[]? Children { get; init; }
}

public class ProfileFrameResult
{
    [JsonProperty("frameIndex")]
    public required int FrameIndex { get; init; }

    [JsonProperty("threadIndex")]
    public required int ThreadIndex { get; init; }

    [JsonProperty("threadName")]
    public required string ThreadName { get; init; }

    [JsonProperty("frameTimeMs")]
    public required double FrameTimeMs { get; init; }

    [JsonProperty("depth")]
    public required int Depth { get; init; }

    [JsonProperty("thresholdMs")]
    public required double ThresholdMs { get; init; }

    [JsonProperty("prunedNodes")]
    public required int PrunedNodes { get; init; }

    [JsonProperty("tree")]
    public required ProfileFrameNode[] Tree { get; init; }
}

public class ProfileMarkResult
{
    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("repeat")]
    public required int Repeat { get; init; }

    [JsonProperty("meanMs")]
    public required double MeanMs { get; init; }

    [JsonProperty("minMs")]
    public required double MinMs { get; init; }

    [JsonProperty("maxMs")]
    public required double MaxMs { get; init; }

    [JsonProperty("p50Ms")]
    public required double P50Ms { get; init; }

    [JsonProperty("p95Ms")]
    public required double P95Ms { get; init; }

    [JsonProperty("gcBytes")]
    public required long GcBytes { get; init; }

    [JsonProperty("gcBytesPerCall")]
    public required long GcBytesPerCall { get; init; }

    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
    public string? Result { get; init; }
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

public class DialogInfo
{
    [JsonProperty("title")]
    public required string Title { get; init; }

    [JsonProperty("buttons")]
    public required string[] Buttons { get; init; }

    [JsonProperty("description")]
    public string? Description { get; init; }

    [JsonProperty("progress")]
    public float? Progress { get; init; }
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

    [JsonProperty("scenes", NullValueHandling = NullValueHandling.Ignore)]
    public SnapshotSceneInfo[]? Scenes { get; init; }

    [JsonProperty("matchCount", NullValueHandling = NullValueHandling.Ignore)]
    public int? MatchCount { get; init; }

    [JsonProperty("screenWidth", NullValueHandling = NullValueHandling.Ignore)]
    public int? ScreenWidth { get; set; }

    [JsonProperty("screenHeight", NullValueHandling = NullValueHandling.Ignore)]
    public int? ScreenHeight { get; set; }
}

public class SnapshotSceneInfo
{
    [JsonProperty("sceneName")]
    public string SceneName { get; set; } = "";

    [JsonProperty("scenePath")]
    public string ScenePath { get; set; } = "";

    [JsonProperty("isActive")]
    public bool IsActive { get; set; }

    [JsonProperty("rootObjectCount")]
    public int RootObjectCount { get; set; }

    [JsonProperty("objects")]
    public SnapshotObject[] Objects { get; set; } = Array.Empty<SnapshotObject>();

    [JsonProperty("matchCount", NullValueHandling = NullValueHandling.Ignore)]
    public int? MatchCount { get; set; }
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

    // Screen-space info (populated when --screen is used)

    [JsonProperty("screenRect", NullValueHandling = NullValueHandling.Ignore)]
    public string? ScreenRect { get; set; }

    [JsonProperty("visible", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Visible { get; set; }

    [JsonProperty("hittable", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Hittable { get; set; }

    [JsonProperty("blockedBy", NullValueHandling = NullValueHandling.Ignore)]
    public int? BlockedBy { get; set; }

    [JsonProperty("childCount")]
    public int ChildCount { get; set; }

    [JsonProperty("children")]
    public SnapshotObject[]? Children { get; set; }

    [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
    public string? Path { get; set; }
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

    [JsonProperty("saved")]
    public bool Saved { get; init; }
}

public class SnapshotComponent
{
    [JsonProperty("typeName")]
    public string TypeName { get; set; } = "";

    [JsonProperty("properties")]
    public Dictionary<string, object>? Properties { get; set; }
}

public class SnapshotQueryResult
{
    [JsonProperty("x")]
    public int X { get; set; }

    [JsonProperty("y")]
    public int Y { get; set; }

    [JsonProperty("mode")]
    public string Mode { get; set; } = "";

    [JsonProperty("screenWidth")]
    public int ScreenWidth { get; set; }

    [JsonProperty("screenHeight")]
    public int ScreenHeight { get; set; }

    [JsonProperty("uiHits", NullValueHandling = NullValueHandling.Ignore)]
    public SnapshotQueryHit[]? UiHits { get; set; }
}

public class SnapshotQueryHit
{
    [JsonProperty("instanceId")]
    public int InstanceId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
    public string? Text { get; set; }

    [JsonProperty("interactable", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Interactable { get; set; }
}

public class UIClickResult
{
    [JsonProperty("instanceId")]
    public int InstanceId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("screenPosition")]
    public string ScreenPosition { get; set; } = "";

    [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
    public string? Text { get; set; }
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
