using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class EditorCommands
{
    public static Command CreateCommand()
    {
        var editorCommand = new Command("editor", "Unity Editor operations");

        // editor run
        var runCommand = new Command("run", "Launch Unity Editor for this project");

        var waitOption = new Option<bool>(
            "--wait",
            "Wait for Unity to exit (default: launch and exit immediately)");

        var unityPathOption = new Option<string?>(
            "--unity-path",
            "Override Unity executable path");

        runCommand.AddOption(waitOption);
        runCommand.AddOption(unityPathOption);

        runCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var wait = context.ParseResult.GetValueForOption(waitOption);
            var unityPath = context.ParseResult.GetValueForOption(unityPathOption);

            await RunEditorAsync(projectPath, unityPath, wait);
        });

        // editor stop
        var stopCommand = new Command("stop", "Stop the Unity Editor for this project");

        stopCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            await StopEditorAsync(projectPath);
        });

        editorCommand.AddCommand(runCommand);
        editorCommand.AddCommand(stopCommand);
        return editorCommand;
    }

    private static async Task RunEditorAsync(string? projectPath, string? unityPath, bool wait)
    {
        // 1. Find project root
        var projectRoot = projectPath != null
            ? System.IO.Path.GetFullPath(projectPath)
            : ProjectLocator.FindProjectRoot();

        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project.");
            Console.Error.WriteLine("  Use --project to specify project root");
            return;
        }

        Console.WriteLine($"Project: {projectRoot}");

        // 2. Check if Unity is already running
        var unityStatus = ProjectLocator.CheckUnityEditorStatus(projectRoot);
        if (unityStatus.Status == UnityEditorStatus.Running)
        {
            Console.Error.WriteLine("Error: Unity Editor is already running for this project.");
            Console.Error.WriteLine("  Use 'unityctl editor stop' to stop it first.");
            return;
        }

        // 3. Resolve Unity executable
        if (string.IsNullOrEmpty(unityPath))
        {
            var version = UnityInstallation.GetProjectUnityVersion(projectRoot);
            if (version == null)
            {
                Console.Error.WriteLine("Error: Could not read Unity version from ProjectSettings/ProjectVersion.txt");
                return;
            }

            Console.WriteLine($"Unity version: {version}");

            unityPath = UnityInstallation.FindUnityExecutable(version);
            if (unityPath == null)
            {
                Console.Error.WriteLine($"Error: Unity {version} not found in Unity Hub.");
                Console.Error.WriteLine("  Expected locations:");
                foreach (var hubPath in UnityInstallation.GetUnityHubPaths())
                {
                    var expectedPath = System.IO.Path.Combine(hubPath, version);
                    Console.Error.WriteLine($"    {expectedPath}");
                }
                Console.Error.WriteLine("  Use --unity-path to specify the Unity executable manually.");
                return;
            }
        }

        Console.WriteLine($"Unity path: {unityPath}");

        // 4. Set up log file path
        var unityCtlDir = System.IO.Path.Combine(projectRoot, ".unityctl");
        System.IO.Directory.CreateDirectory(unityCtlDir);
        var logPath = System.IO.Path.Combine(unityCtlDir, "editor.log");
        var prevLogPath = System.IO.Path.Combine(unityCtlDir, "editor-prev.log");

        // Rotate previous log file (like Unity does with Editor-prev.log)
        if (System.IO.File.Exists(logPath))
        {
            try
            {
                System.IO.File.Move(logPath, prevLogPath, overwrite: true);
            }
            catch
            {
                // Ignore rotation errors
            }
        }

        // 5. Launch Unity
        Console.WriteLine("Launching Unity Editor...");

        var startInfo = new ProcessStartInfo
        {
            FileName = unityPath,
            Arguments = $"-projectPath \"{projectRoot}\" -logFile \"{logPath}\"",
            UseShellExecute = true  // Detach Unity from our process tree
        };

        try
        {
            var unityProcess = Process.Start(startInfo);
            if (unityProcess == null)
            {
                Console.Error.WriteLine("Error: Failed to start Unity process.");
                return;
            }

            Console.WriteLine($"Unity started (PID: {unityProcess.Id})");
            Console.WriteLine($"Log file: {logPath}");

            if (wait)
            {
                Console.WriteLine("Waiting for Unity to exit... (Ctrl+C to detach)");

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                try
                {
                    await unityProcess.WaitForExitAsync(cts.Token);
                    Console.WriteLine($"Unity exited with code: {unityProcess.ExitCode}");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Detached. Unity is still running.");
                }
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Use 'unityctl logs -f' to stream logs (requires bridge)");
                Console.WriteLine("Use 'unityctl editor stop' to stop the editor");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
    }

    private static Task StopEditorAsync(string? projectPath)
    {
        // 1. Find project root
        var projectRoot = projectPath != null
            ? System.IO.Path.GetFullPath(projectPath)
            : ProjectLocator.FindProjectRoot();

        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project.");
            Console.Error.WriteLine("  Use --project to specify project root");
            return Task.CompletedTask;
        }

        Console.WriteLine($"Project: {projectRoot}");

        // 2. Find Unity process for this project
        var unityProcess = FindUnityProcessForProject(projectRoot);
        if (unityProcess == null)
        {
            Console.Error.WriteLine("Error: No Unity Editor found running for this project.");
            return Task.CompletedTask;
        }

        Console.WriteLine($"Found Unity process (PID: {unityProcess.Id})");

        // 3. Kill the process
        try
        {
            unityProcess.Kill();
            Console.WriteLine("Unity Editor stopped.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error stopping Unity: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Find the Unity process running for a specific project by checking command line arguments.
    /// </summary>
    private static Process? FindUnityProcessForProject(string projectRoot)
    {
        // Normalize the project path for comparison
        var normalizedProjectRoot = System.IO.Path.GetFullPath(projectRoot).TrimEnd('\\', '/');

        try
        {
            // Get all Unity processes
            var unityProcesses = Process.GetProcessesByName("Unity");

            foreach (var process in unityProcesses)
            {
                try
                {
                    var commandLine = GetProcessCommandLine(process);
                    if (commandLine == null) continue;

                    // Look for -projectPath argument
                    var projectPathArg = ExtractProjectPath(commandLine);
                    if (projectPathArg == null) continue;

                    // Normalize and compare
                    var normalizedArg = System.IO.Path.GetFullPath(projectPathArg).TrimEnd('\\', '/');
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

    /// <summary>
    /// Extract the project path from Unity's command line arguments.
    /// </summary>
    private static string? ExtractProjectPath(string commandLine)
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

    /// <summary>
    /// Get the command line for a process. Platform-specific implementation.
    /// </summary>
    private static string? GetProcessCommandLine(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsCommandLine(process.Id);
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return GetUnixCommandLine(process.Id);
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
            if (System.IO.File.Exists(cmdlinePath))
            {
                var cmdline = System.IO.File.ReadAllText(cmdlinePath);
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
