using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace UnityCtl.Bridge;

/// <summary>
/// Utilities for inspecting process command lines.
/// Used to detect if Unity was launched with custom -logFile flag.
/// </summary>
internal static class ProcessCommandLine
{
    /// <summary>
    /// Get the command line for a process. Platform-specific implementation.
    /// </summary>
    public static string? GetCommandLine(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsCommandLine(processId);
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return GetUnixCommandLine(processId);
        }

        return null;
    }

    /// <summary>
    /// Extract the -logFile path from Unity's command line arguments.
    /// </summary>
    public static string? ExtractLogFilePath(string commandLine)
    {
        // Look for -logFile "path" or -logFile path
        var marker = "-logFile";
        var idx = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var afterMarker = commandLine.Substring(idx + marker.Length).TrimStart();
        if (afterMarker.Length == 0) return null;

        // Handle quoted path
        if (afterMarker[0] == '"')
        {
            var endQuote = afterMarker.IndexOf('"', 1);
            if (endQuote > 0)
            {
                return afterMarker.Substring(1, endQuote - 1);
            }
        }

        // Handle unquoted path (ends at next space or end of string)
        var endSpace = afterMarker.IndexOf(' ');
        return endSpace > 0 ? afterMarker.Substring(0, endSpace) : afterMarker;
    }

    /// <summary>
    /// Extract the -projectPath from Unity's command line arguments.
    /// </summary>
    public static string? ExtractProjectPath(string commandLine)
    {
        // Look for -projectPath "path" or -projectPath path
        var marker = "-projectPath";
        var idx = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var afterMarker = commandLine.Substring(idx + marker.Length).TrimStart();
        if (afterMarker.Length == 0) return null;

        // Handle quoted path
        if (afterMarker[0] == '"')
        {
            var endQuote = afterMarker.IndexOf('"', 1);
            if (endQuote > 0)
            {
                return afterMarker.Substring(1, endQuote - 1);
            }
        }

        // Handle unquoted path (ends at next space or end of string)
        var endSpace = afterMarker.IndexOf(' ');
        return endSpace > 0 ? afterMarker.Substring(0, endSpace) : afterMarker;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsCommandLine(int processId)
    {
        try
        {
            // Use WMI to get command line on Windows
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");

            foreach (var obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch
        {
            // WMI not available or access denied
        }

        return null;
    }

    private static string? GetUnixCommandLine(int processId)
    {
        try
        {
            // Read from /proc/{pid}/cmdline on Linux
            var cmdlinePath = $"/proc/{processId}/cmdline";
            if (File.Exists(cmdlinePath))
            {
                var cmdline = File.ReadAllText(cmdlinePath);
                // Arguments are null-separated, join with spaces
                return cmdline.Replace('\0', ' ').Trim();
            }

            // On macOS, use ps command
            if (OperatingSystem.IsMacOS())
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-p {processId} -o args=",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var ps = Process.Start(startInfo);
                if (ps != null)
                {
                    var output = ps.StandardOutput.ReadToEnd().Trim();
                    ps.WaitForExit();
                    return output;
                }
            }
        }
        catch
        {
            // Access denied or process not found
        }

        return null;
    }
}
