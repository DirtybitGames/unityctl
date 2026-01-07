using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UnityCtl.Protocol;

/// <summary>
/// Platform-specific Unity Editor log paths.
/// </summary>
public static class EditorLogPaths
{
    /// <summary>
    /// Gets the default Unity Editor log path for the current platform.
    /// This is the global log used when Unity is not launched with -logFile.
    /// </summary>
    public static string GetDefaultEditorLogPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // %LOCALAPPDATA%\Unity\Editor\Editor.log
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // ~/Library/Logs/Unity/Editor.log
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
        }
        else // Linux
        {
            // ~/.config/unity3d/Editor.log
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "unity3d", "Editor.log");
        }
    }

    /// <summary>
    /// Gets the custom log path used by unityctl editor run.
    /// </summary>
    public static string GetCustomLogPath(string projectRoot)
    {
        return Path.Combine(projectRoot, ".unityctl", "editor.log");
    }
}
