using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Linq;
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
                Console.Error.WriteLine("  Use --project to specify project root, or run:");
                Console.Error.WriteLine("  unityctl config set project-path <path-to-unity-project>");
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
            HealthResult? health = null;

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
                catch (System.ComponentModel.Win32Exception)
                {
                    // Access denied - PID reused by a process we can't query
                    bridgeRunning = false;
                }

                // If bridge is running, check if Unity is connected
                if (bridgeRunning)
                {
                    try
                    {
                        var client = new BridgeClient($"http://localhost:{bridgeConfig.Port}");
                        health = await client.GetAsync<HealthResult>("/health");
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

            // Detect popup dialogs (including progress bars) if Unity is running
            DialogInfo[]? detectedDialogs = null;
            if (result.UnityEditorRunning)
            {
                var unityProcess = EditorCommands.FindUnityProcessForProject(
                    System.IO.Path.GetFullPath(projectRoot));
                if (unityProcess != null)
                {
                    var dialogs = DialogDetector.DetectDialogs(unityProcess.Id);
                    if (dialogs.Count > 0)
                    {
                        detectedDialogs = dialogs.Select(d => new DialogInfo
                        {
                            Title = d.Title,
                            Buttons = d.Buttons.Select(b => b.Text).ToArray(),
                            Description = d.Description,
                            Progress = d.Progress
                        }).ToArray();
                    }
                }
            }

            // Resolve plugin version: prefer live value from bridge, fall back to manifest
            var pluginVersionFromBridge = health?.UnityPluginVersion;
            var pluginVersion = pluginVersionFromBridge
                ?? VersionCheck.ReadPluginVersionFromManifest(projectRoot);
            var pluginVersionSource = pluginVersionFromBridge != null ? "connected" : "manifest";
            var enforced = VersionCheck.IsEnforced(projectRoot);
            var versionCheck = VersionCheck.Check(
                VersionInfo.Version,
                health?.BridgeVersion,
                pluginVersion);

            if (json)
            {
                var jsonResult = new
                {
                    result.ProjectPath,
                    result.ProjectId,
                    result.UnityEditorRunning,
                    result.UnityEditorStatus,
                    result.BridgeConfigured,
                    result.BridgeRunning,
                    result.BridgePort,
                    result.BridgePid,
                    result.UnityConnectedToBridge,
                    Dialogs = detectedDialogs,
                    Versions = new
                    {
                        Cli = VersionInfo.Version,
                        Bridge = health?.BridgeVersion,
                        UnityPlugin = pluginVersion,
                        UnityPluginSource = pluginVersion != null ? pluginVersionSource : null
                    },
                    VersionMismatch = versionCheck.HasMismatch,
                    PluginAhead = versionCheck.PluginAhead,
                    EnforceVersionMatch = enforced
                };
                Console.WriteLine(JsonHelper.Serialize(jsonResult));

                if (enforced && versionCheck.PluginAhead)
                {
                    context.ExitCode = 1;
                }
            }
            else
            {
                PrintHumanReadableStatus(result, unityStatus.Message, health, detectedDialogs);

                Console.WriteLine();
                var versionResult = PrintVersionInfo(versionCheck, enforced, health != null, pluginVersionSource);

                if (enforced && versionResult.PluginAhead)
                {
                    context.ExitCode = 1;
                }
            }
        });

        return statusCommand;
    }

    private static void PrintHumanReadableStatus(ProjectStatusResult status, string? unityStatusMessage, HealthResult? health, DialogInfo[]? dialogs = null)
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
                    Console.WriteLine("  Note: The Unity plugin uses exponential backoff (max 15s) when reconnecting.");
                }
            }
        }

        // Popup dialogs (including progress bars)
        if (dialogs != null && dialogs.Length > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"Popups: [!] {dialogs.Length} dialog{(dialogs.Length > 1 ? "s" : "")} detected");
            Console.ResetColor();
            Console.WriteLine();
            foreach (var dialog in dialogs)
            {
                Console.Write($"  \"{dialog.Title}\"");
                if (dialog.Buttons.Length > 0)
                {
                    var buttonLabels = dialog.Buttons.Select(b => $"[{b}]");
                    Console.Write($" {string.Join(" ", buttonLabels)}");
                }
                if (dialog.Progress.HasValue)
                {
                    var pct = (int)(dialog.Progress.Value * 100);
                    Console.Write($" ({pct}%)");
                }
                if (dialog.Description != null)
                    Console.Write($" - {dialog.Description}");
                Console.WriteLine();
            }
            if (dialogs.Any(d => d.Buttons.Length > 0))
                Console.WriteLine("  Use 'unityctl dialog dismiss' to dismiss");
        }

    }

    private static VersionCheck.VersionCheckResult PrintVersionInfo(VersionCheck.VersionCheckResult result, bool enforced, bool bridgeConnected, string pluginSource)
    {
        if (!result.HasMismatch)
        {
            var suffix = result.PluginVersion != null ? "all match" : "plugin not installed";
            Console.WriteLine($"Versions: {result.CliVersion} ({suffix})");
        }
        else if (enforced && result.PluginAhead)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Unity plugin is newer than CLI/Bridge (enforce-version-match is enabled)!");
            Console.ResetColor();
            PrintVersionDetails(result, bridgeConnected, pluginSource);
            Console.WriteLine("  Run 'unityctl update' to sync all components.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNING: Version mismatch!");
            Console.ResetColor();
            PrintVersionDetails(result, bridgeConnected, pluginSource);
            Console.WriteLine("  Run 'unityctl update' to sync all components to the same version.");
        }

        return result;
    }

    private static void PrintVersionDetails(VersionCheck.VersionCheckResult result, bool bridgeConnected, string pluginSource)
    {
        var bridgeLabel = bridgeConnected ? (result.BridgeVersion ?? "N/A") : "not running";
        var pluginLabel = result.PluginVersion != null
            ? $"{result.PluginVersion} (from {pluginSource})"
            : "N/A";
        Console.WriteLine($"  CLI: {result.CliVersion ?? "N/A"}, Bridge: {bridgeLabel}, Plugin: {pluginLabel}");
    }

}
