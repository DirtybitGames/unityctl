using System;
using System.IO;
using UnityCtl.Protocol;

namespace UnityCtl.Bridge;

/// <summary>
/// Resolves the correct editor log path to use based on how Unity was launched.
/// </summary>
public static class EditorLogPathResolver
{
    /// <summary>
    /// Result of resolving the log path.
    /// </summary>
    public class ResolveResult
    {
        public required string LogPath { get; init; }
        public bool IsDefaultGlobalLog { get; init; }
        public string? WarningMessage { get; init; }
    }

    /// <summary>
    /// Resolves the editor log path for a Unity project.
    ///
    /// Logic:
    /// 1. Find Unity process for this project
    /// 2. If found, check command line for -logFile flag
    /// 3. If -logFile present, use that path
    /// 4. If no -logFile, use platform-specific default global log
    /// 5. If no Unity process found, use custom .unityctl/editor.log path (will wait for file)
    /// </summary>
    public static ResolveResult Resolve(string projectRoot)
    {
        var customLogPath = EditorLogPaths.GetCustomLogPath(projectRoot);
        var defaultLogPath = EditorLogPaths.GetDefaultEditorLogPath();

        // Try to find Unity process for this project
        var unityProcess = UnityProcessFinder.FindUnityProcessForProject(projectRoot);

        if (unityProcess == null)
        {
            // No Unity process found - use custom path (will wait for file)
            return new ResolveResult
            {
                LogPath = customLogPath,
                IsDefaultGlobalLog = false
            };
        }

        // Unity is running - check its command line
        string? commandLine;
        try
        {
            commandLine = ProcessCommandLine.GetCommandLine(unityProcess.Id);
        }
        catch
        {
            commandLine = null;
        }

        if (commandLine == null)
        {
            // Can't read command line - fall back to default
            return new ResolveResult
            {
                LogPath = defaultLogPath,
                IsDefaultGlobalLog = true,
                WarningMessage = "Could not read Unity command line. Using default global log path."
            };
        }

        // Check for -logFile flag
        var logFilePath = ProcessCommandLine.ExtractLogFilePath(commandLine);
        if (logFilePath != null)
        {
            // Unity was launched with custom log file
            return new ResolveResult
            {
                LogPath = Path.GetFullPath(logFilePath),
                IsDefaultGlobalLog = false
            };
        }

        // No -logFile flag - use default global log
        return new ResolveResult
        {
            LogPath = defaultLogPath,
            IsDefaultGlobalLog = true,
            WarningMessage = "Unity was not launched with -logFile flag. Using default global log.\n" +
                           "Warning: This log is shared across all Unity instances - logs may be mixed if multiple editors are running.\n" +
                           "For project-specific logs, use 'unityctl editor run' to launch Unity."
        };
    }
}
