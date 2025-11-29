using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class StatusCommand
{
    public static Command CreateCommand()
    {
        var statusCommand = new Command("status", "Show project status including Unity Editor and bridge state");

        statusCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var json = ContextHelper.GetJson(context);

            var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
            if (projectRoot == null)
            {
                Console.Error.WriteLine("Error: Not in a Unity project.");
                Console.Error.WriteLine("  Use --project to specify project root, or create .unityctl/config.json");
                Console.Error.WriteLine("  with: { \"projectPath\": \"path/to/unity/project\" }");
                context.ExitCode = 1;
                return;
            }

            var projectId = ProjectLocator.ComputeProjectId(projectRoot);

            // Check Unity Editor status
            var unityStatus = ProjectLocator.CheckUnityEditorStatus(projectRoot);

            // Check bridge configuration
            var bridgeConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
            var bridgeConfigured = bridgeConfig != null;
            var bridgeRunning = false;
            var unityConnected = false;

            if (bridgeConfig != null)
            {
                // Check if bridge process is running
                try
                {
                    var process = Process.GetProcessById(bridgeConfig.Pid);
                    bridgeRunning = !process.HasExited;
                }
                catch (ArgumentException)
                {
                    // Process not found
                    bridgeRunning = false;
                }
                catch (InvalidOperationException)
                {
                    // Process has exited
                    bridgeRunning = false;
                }

                // If bridge is running, check if Unity is connected
                if (bridgeRunning)
                {
                    try
                    {
                        var client = new BridgeClient($"http://localhost:{bridgeConfig.Port}");
                        var health = await client.GetAsync<HealthResult>("/health");
                        if (health != null)
                        {
                            unityConnected = health.UnityConnected;
                        }
                    }
                    catch
                    {
                        // Bridge not responding, treat as not running
                        bridgeRunning = false;
                    }
                }
            }

            var result = new ProjectStatusResult
            {
                ProjectPath = projectRoot,
                ProjectId = projectId,
                UnityEditorRunning = unityStatus.Status == UnityEditorStatus.Running,
                UnityEditorStatus = unityStatus.Status.ToString(),
                BridgeConfigured = bridgeConfigured,
                BridgeRunning = bridgeRunning,
                BridgePort = bridgeConfig?.Port,
                BridgePid = bridgeConfig?.Pid,
                UnityConnectedToBridge = unityConnected
            };

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(result));
            }
            else
            {
                PrintHumanReadableStatus(result, unityStatus.Message);
            }
        });

        return statusCommand;
    }

    private static void PrintHumanReadableStatus(ProjectStatusResult status, string? unityStatusMessage)
    {
        Console.WriteLine("Project Status:");
        Console.WriteLine($"  Path: {status.ProjectPath}");
        Console.WriteLine($"  ID: {status.ProjectId}");
        Console.WriteLine();

        // Unity Editor status
        var unityIcon = status.UnityEditorRunning ? "[+]" : "[-]";
        Console.ForegroundColor = status.UnityEditorRunning ? ConsoleColor.Green : ConsoleColor.Gray;
        Console.Write($"Unity Editor: {unityIcon} ");
        Console.ResetColor();
        Console.WriteLine(status.UnityEditorRunning ? "Running" : "Not running");
        Console.WriteLine();

        // Bridge status
        if (!status.BridgeConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write("Bridge: [-] ");
            Console.ResetColor();
            Console.WriteLine("Not configured");
            Console.WriteLine("  Run 'unityctl bridge start' to start the bridge");
        }
        else if (!status.BridgeRunning)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Bridge: [-] ");
            Console.ResetColor();
            Console.WriteLine("Not running (stale config)");
            Console.WriteLine($"  Config PID: {status.BridgePid}");
            Console.WriteLine("  Run 'unityctl bridge start' to restart the bridge");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Bridge: [+] ");
            Console.ResetColor();
            Console.WriteLine("Running");
            Console.WriteLine($"  PID: {status.BridgePid}");
            Console.WriteLine($"  Port: {status.BridgePort}");
        }
        Console.WriteLine();

        // Connection status
        if (status.BridgeRunning)
        {
            var connIcon = status.UnityConnectedToBridge ? "[+]" : "[-]";
            Console.ForegroundColor = status.UnityConnectedToBridge ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write($"Connection: {connIcon} ");
            Console.ResetColor();
            Console.WriteLine(status.UnityConnectedToBridge ? "Unity connected to bridge" : "Unity not connected");

            if (!status.UnityConnectedToBridge)
            {
                if (!status.UnityEditorRunning)
                {
                    Console.WriteLine("  Start Unity Editor to connect");
                }
                else
                {
                    Console.WriteLine("  Unity is running but not connected. Ensure UnityCtl package is installed.");
                }
            }
        }
    }
}
