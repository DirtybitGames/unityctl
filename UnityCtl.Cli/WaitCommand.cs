using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class WaitCommand
{
    private const int DefaultTimeout = 120;

    public static Command CreateCommand()
    {
        var waitCommand = new Command("wait", "Wait until Unity is connected to the bridge");

        var timeoutOption = new Option<int?>(
            "--timeout",
            description: "Maximum seconds to wait (default: 120, or wait-timeout from config)");

        waitCommand.AddOption(timeoutOption);

        waitCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var json = ContextHelper.GetJson(context);
            var timeoutArg = context.ParseResult.GetValueForOption(timeoutOption);
            var timeout = timeoutArg ?? ReadConfiguredTimeout() ?? DefaultTimeout;

            var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
            if (projectRoot == null)
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new { error = "Project not found" }));
                }
                else
                {
                    Console.Error.WriteLine("Error: Not in a Unity project.");
                    Console.Error.WriteLine("  Use --project to specify project root, or run:");
                    Console.Error.WriteLine("  unityctl config set project-path <path-to-unity-project>");
                }
                context.ExitCode = 1;
                return;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            if (!json)
            {
                Console.WriteLine("Waiting for Unity to connect...");
            }

            var bridgeFound = false;
            var elapsed = 0;

            while (elapsed < timeout)
            {
                if (cts.Token.IsCancellationRequested)
                    break;

                var bridgeConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
                if (bridgeConfig != null)
                {
                    try
                    {
                        var client = new BridgeClient($"http://localhost:{bridgeConfig.Port}");
                        var health = await client.GetAsync<HealthResult>("/health");
                        if (health != null)
                        {
                            bridgeFound = true;
                            if (health.UnityConnected)
                            {
                                if (json)
                                {
                                    Console.WriteLine(JsonHelper.Serialize(new
                                    {
                                        unityConnected = true,
                                        bridgeRunning = true
                                    }));
                                }
                                else
                                {
                                    Console.WriteLine("Connected!");
                                }
                                return;
                            }
                        }
                    }
                    catch
                    {
                        // Bridge not responding yet, continue waiting
                    }
                }

                try
                {
                    await Task.Delay(1000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                elapsed++;
            }

            // Timeout or cancelled
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    unityConnected = false,
                    bridgeRunning = bridgeFound
                }));
            }
            else
            {
                if (cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine("Cancelled.");
                }
                else if (!bridgeFound)
                {
                    Console.Error.WriteLine($"Timed out after {timeout}s. Bridge not found.");
                    Console.Error.WriteLine("  Run 'unityctl bridge start' first.");
                }
                else
                {
                    Console.Error.WriteLine($"Timed out after {timeout}s. Unity is not connected to the bridge.");
                }
            }
            context.ExitCode = 1;
        });

        return waitCommand;
    }

    private static int? ReadConfiguredTimeout()
    {
        try
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                var configPath = Path.Combine(current.FullName, ProjectLocator.BridgeConfigDir, ProjectLocator.ConfigFile);
                if (File.Exists(configPath))
                {
                    var content = File.ReadAllText(configPath);
                    var config = JsonNode.Parse(content);
                    var value = config?["waitTimeout"];
                    if (value != null)
                        return (int)value;
                    return null;
                }
                current = current.Parent;
            }
        }
        catch
        {
            // Config read failure is not fatal
        }
        return null;
    }
}
