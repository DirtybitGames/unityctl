namespace UnityCtl.Protocol;

public static class UnityCtlCommands
{
    // Console
    public const string ConsoleTail = "console.tail";

    // Assets & compilation
    public const string AssetImport = "asset.import";
    public const string AssetReimportAll = "asset.reimportAll";
    public const string AssetRefresh = "asset.refresh";

    // Scenes
    public const string SceneList = "scene.list";
    public const string SceneLoad = "scene.load";

    // Play mode
    public const string PlayEnter = "play.enter";
    public const string PlayExit = "play.exit";
    public const string PlayToggle = "play.toggle";
    public const string PlayStatus = "play.status";

    // Menu items
    public const string MenuList = "menu.list";
    public const string MenuExecute = "menu.execute";

    // Tests
    public const string TestRun = "test.run";

    // Screenshots
    public const string ScreenshotCapture = "screenshot.capture";

    // Script execution
    public const string ScriptExecute = "script.execute";
}

public static class UnityCtlEvents
{
    public const string Log = "log";
    public const string PlayModeChanged = "playModeChanged";
    public const string CompilationStarted = "compilation.started";
    public const string CompilationFinished = "compilation.finished";
    public const string AssetImportComplete = "asset.importComplete";
    public const string AssetReimportAllComplete = "asset.reimportAllComplete";
    public const string AssetRefreshComplete = "asset.refreshComplete";
    public const string TestStarted = "test.started";
    public const string TestFinished = "test.finished";
    public const string DomainReloadStarting = "domain.reloadStarting";
}

public static class MessageOrigin
{
    public const string Unity = "unity";
    public const string Bridge = "bridge";
}

public static class ResponseStatus
{
    public const string Ok = "ok";
    public const string Error = "error";
}

public static class PlayModeState
{
    public const string Playing = "playing";
    public const string Stopped = "stopped";
    public const string Transitioning = "transitioning";
}

public static class LogLevel
{
    public const string Log = "Log";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Exception = "Exception";
}

/// <summary>
/// Log-based events detected from editor.log patterns.
/// These are defined in log_rules.json with emit_event.
/// </summary>
public static class LogEvents
{
    public const string RefreshComplete = "refresh.complete";
    public const string CompilerError = "compiler.error";
    public const string CompilerWarning = "compiler.warning";
    public const string CompileRequested = "compile.requested";
    public const string CompileSuccess = "compile.success";
    public const string CompileFail = "compile.fail";
    public const string PlayModeEnter = "playmode.enter";
    public const string PlayModeExit = "playmode.exit";
}
