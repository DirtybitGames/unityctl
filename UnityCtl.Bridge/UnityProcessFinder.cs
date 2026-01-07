using System;
using System.Diagnostics;
using System.IO;

namespace UnityCtl.Bridge;

/// <summary>
/// Finds Unity Editor processes for a specific project.
/// </summary>
internal static class UnityProcessFinder
{
    /// <summary>
    /// Find the Unity process running for a specific project by checking command line arguments.
    /// </summary>
    public static Process? FindUnityProcessForProject(string projectRoot)
    {
        // Normalize the project path for comparison
        var normalizedProjectRoot = Path.GetFullPath(projectRoot).TrimEnd('\\', '/');

        try
        {
            // Get all Unity processes
            var unityProcesses = Process.GetProcessesByName("Unity");

            foreach (var process in unityProcesses)
            {
                try
                {
                    var commandLine = ProcessCommandLine.GetCommandLine(process.Id);
                    if (commandLine == null) continue;

                    // Look for -projectPath argument
                    var projectPathArg = ProcessCommandLine.ExtractProjectPath(commandLine);
                    if (projectPathArg == null) continue;

                    // Normalize and compare
                    var normalizedArg = Path.GetFullPath(projectPathArg).TrimEnd('\\', '/');
                    if (string.Equals(normalizedProjectRoot, normalizedArg, StringComparison.OrdinalIgnoreCase))
                    {
                        return process;
                    }
                }
                catch
                {
                    // Skip processes we can't inspect
                }
            }
        }
        catch
        {
            // Process enumeration failed
        }

        return null;
    }
}
