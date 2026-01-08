using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UnityCtl.Cli;

/// <summary>
/// Utilities for detecting Unity versions and finding Hub installations.
/// </summary>
public static class UnityInstallation
{
    /// <summary>
    /// Reads the Unity version from ProjectSettings/ProjectVersion.txt
    /// </summary>
    public static string? GetProjectUnityVersion(string projectRoot)
    {
        var versionFile = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile))
            return null;

        try
        {
            foreach (var line in File.ReadLines(versionFile))
            {
                // Format: m_EditorVersion: 6000.0.23f1
                if (line.StartsWith("m_EditorVersion:"))
                {
                    var version = line.Substring("m_EditorVersion:".Length).Trim();
                    return version;
                }
            }
        }
        catch
        {
            // Ignore read errors
        }

        return null;
    }

    /// <summary>
    /// Finds the Unity executable for the specified version in Unity Hub installation paths.
    /// </summary>
    public static string? FindUnityExecutable(string version)
    {
        foreach (var hubPath in GetUnityHubPaths())
        {
            var versionPath = Path.Combine(hubPath, version);
            if (Directory.Exists(versionPath))
            {
                var exePath = GetUnityExecutablePath(versionPath);
                if (File.Exists(exePath))
                    return exePath;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets platform-specific Unity Hub installation paths.
    /// </summary>
    public static string[] GetUnityHubPaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new[]
            {
                @"C:\Program Files\Unity\Hub\Editor",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor")
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[]
            {
                "/Applications/Unity/Hub/Editor"
            };
        }
        else // Linux
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor")
            };
        }
    }

    /// <summary>
    /// Gets the path to Unity executable within a version installation folder.
    /// </summary>
    public static string GetUnityExecutablePath(string versionPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(versionPath, "Editor", "Unity.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(versionPath, "Unity.app", "Contents", "MacOS", "Unity");
        }
        else // Linux
        {
            return Path.Combine(versionPath, "Editor", "Unity");
        }
    }

}
