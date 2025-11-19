using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class BridgeCommands
{
    public static Command CreateCommand()
    {
        var bridgeCommand = new Command("bridge", "Bridge management operations");

        // bridge status
        var statusCommand = new Command("status", "Check bridge status");

        statusCommand.SetHandler(async (InvocationContext context) =>
        {
            // Get project path from global options
            string? projectPath = null;
            var parseResult = context.ParseResult;
            var rootCommand = parseResult.RootCommandResult.Command;
            var projectOption = rootCommand.Options.FirstOrDefault(o => o.Name == "project") as Option<string>;
            if (projectOption != null)
            {
                projectPath = parseResult.GetValueForOption(projectOption);
            }

            var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
            if (projectRoot == null)
            {
                Console.Error.WriteLine("Error: Not in a Unity project. Use --project to specify project root.");
                return;
            }

            var config = ProjectLocator.ReadBridgeConfig(projectRoot);
            if (config == null)
            {
                Console.WriteLine("Bridge: Not configured");
                Console.WriteLine($"Run 'unityctl bridge start' to start the bridge");
                return;
            }

            Console.WriteLine($"Bridge Configuration:");
            Console.WriteLine($"  Project ID: {config.ProjectId}");
            Console.WriteLine($"  Port: {config.Port}");
            Console.WriteLine($"  PID: {config.Pid}");

            // Try to check health
            var client = new BridgeClient($"http://localhost:{config.Port}");
            var health = await client.GetAsync<HealthResult>("/health");

            if (health != null)
            {
                Console.WriteLine($"\nBridge Status: {health.Status}");
                Console.WriteLine($"  Unity Connected: {health.UnityConnected}");
            }
            else
            {
                Console.WriteLine("\nBridge Status: Not responding (may be offline)");
            }
        });

        // bridge start
        var startCommand = new Command("start", "Start the bridge daemon");

        startCommand.SetHandler(async (InvocationContext context) =>
        {
            // Get project path from global options
            string? projectPath = null;
            var parseResult = context.ParseResult;
            var rootCommand = parseResult.RootCommandResult.Command;
            var projectOption = rootCommand.Options.FirstOrDefault(o => o.Name == "project") as Option<string>;
            if (projectOption != null)
            {
                projectPath = parseResult.GetValueForOption(projectOption);
            }

            await BridgeClient.StartBridgeAsync(projectPath);
        });

        // bridge stop
        var stopCommand = new Command("stop", "Stop the bridge daemon");

        stopCommand.SetHandler(async (InvocationContext context) =>
        {
            // Get project path from global options
            string? projectPath = null;
            var parseResult = context.ParseResult;
            var rootCommand = parseResult.RootCommandResult.Command;
            var projectOption = rootCommand.Options.FirstOrDefault(o => o.Name == "project") as Option<string>;
            if (projectOption != null)
            {
                projectPath = parseResult.GetValueForOption(projectOption);
            }

            await BridgeClient.StopBridgeAsync(projectPath);
        });

        bridgeCommand.AddCommand(statusCommand);
        bridgeCommand.AddCommand(startCommand);
        bridgeCommand.AddCommand(stopCommand);
        return bridgeCommand;
    }
}
