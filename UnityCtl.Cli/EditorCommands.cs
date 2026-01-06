using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityCtl.Cli.LogFormatting;
using UnityCtl.Cli.LogTailing;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class EditorCommands
{
    public static Command CreateCommand()
    {
        var editorCommand = new Command("editor", "Unity Editor operations");

        var runCommand = new Command("run", "Launch Unity Editor and stream logs");

        var noBridgeOption = new Option<bool>(
            "--no-bridge",
            "Don't auto-start the bridge");

        var killOnExitOption = new Option<bool>(
            "--kill-on-exit",
            "Kill Unity when exiting (Ctrl+C)");

        var noColorOption = new Option<bool>(
            "--no-color",
            "Disable log coloring");

        var unityPathOption = new Option<string?>(
            "--unity-path",
            "Override Unity executable path");

        runCommand.AddOption(noBridgeOption);
        runCommand.AddOption(killOnExitOption);
        runCommand.AddOption(noColorOption);
        runCommand.AddOption(unityPathOption);

        runCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var noBridge = context.ParseResult.GetValueForOption(noBridgeOption);
            var killOnExit = context.ParseResult.GetValueForOption(killOnExitOption);
            var noColor = context.ParseResult.GetValueForOption(noColorOption);
            var unityPath = context.ParseResult.GetValueForOption(unityPathOption);

            await RunEditorAsync(projectPath, noBridge, killOnExit, noColor, unityPath);
        });

        editorCommand.AddCommand(runCommand);
        return editorCommand;
    }

    private static async Task RunEditorAsync(
        string? projectPath,
        bool noBridge,
        bool killOnExit,
        bool noColor,
        string? unityPath)
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
            Console.Error.WriteLine("  Close the existing Unity instance first, or use 'unityctl' commands to control it.");
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

        // 4. Auto-start bridge if not disabled
        if (!noBridge)
        {
            Console.WriteLine("Starting bridge...");
            var bridgeStarted = await BridgeClient.StartBridgeAsync(projectRoot);
            if (!bridgeStarted)
            {
                Console.Error.WriteLine("Warning: Failed to start bridge. Continuing without bridge.");
            }
        }

        // 5. Set up cancellation for Ctrl+C
        using var cts = new CancellationTokenSource();
        Process? unityProcess = null;

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Prevent immediate termination
            Console.WriteLine("\nShutting down...");

            if (killOnExit && unityProcess != null && !unityProcess.HasExited)
            {
                Console.WriteLine("Killing Unity process...");
                try
                {
                    unityProcess.Kill();
                }
                catch
                {
                    // Ignore kill errors
                }
            }

            cts.Cancel();
        };

        // 6. Launch Unity with project-specific log file
        Console.WriteLine("Launching Unity Editor...");

        // Use project-specific log file to avoid conflicts with other editors
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
                // Ignore rotation errors - just continue with fresh log
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = unityPath,
            Arguments = $"-projectPath \"{projectRoot}\" -logFile \"{logPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            unityProcess = Process.Start(startInfo);
            if (unityProcess == null)
            {
                Console.Error.WriteLine("Error: Failed to start Unity process.");
                return;
            }

            Console.WriteLine($"Unity started (PID: {unityProcess.Id})");
            Console.WriteLine($"Log file: {logPath}");
            Console.WriteLine("Streaming logs... (Ctrl+C to exit)");
            Console.WriteLine(new string('-', 60));

            // 7. Start log tailing
            var formatter = new SimpleLogFormatter(!noColor);

            using var tailer = new FileLogTailer(logPath);

            var tailTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in tailer.TailAsync(cts.Token))
                    {
                        formatter.WriteFormatted(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown
                }
            }, cts.Token);

            // 8. Wait for Unity to exit or cancellation
            while (!unityProcess.HasExited && !cts.Token.IsCancellationRequested)
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            // Give tailer a moment to catch up
            await Task.Delay(500).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cts.Cancel();

            try
            {
                await tailTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            Console.WriteLine(new string('-', 60));

            if (unityProcess.HasExited)
            {
                Console.WriteLine($"Unity exited with code: {unityProcess.ExitCode}");
            }
            else
            {
                Console.WriteLine("Log streaming stopped. Unity is still running.");
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
    }
}
