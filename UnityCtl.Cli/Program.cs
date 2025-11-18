using System;
using System.CommandLine;
using System.Threading.Tasks;
using UnityCtl.Cli;

var rootCommand = new RootCommand("UnityCtl - CLI tool for controlling Unity Editor");

// Global options
var projectOption = new Option<string?>(
    "--project",
    "Path to Unity project root (optional, will auto-detect if not specified)"
);

var agentIdOption = new Option<string?>(
    "--agent-id",
    "Agent ID for distinguishing multiple agents"
);

var jsonOption = new Option<bool>(
    "--json",
    "Output JSON responses instead of human-readable text"
);

rootCommand.AddGlobalOption(projectOption);
rootCommand.AddGlobalOption(agentIdOption);
rootCommand.AddGlobalOption(jsonOption);

// Add subcommands
rootCommand.AddCommand(ConsoleCommands.CreateCommand());
rootCommand.AddCommand(SceneCommands.CreateCommand());
rootCommand.AddCommand(PlayCommands.CreateCommand());
rootCommand.AddCommand(AssetCommands.CreateCommand());
rootCommand.AddCommand(CompileCommands.CreateCommand());
rootCommand.AddCommand(BridgeCommands.CreateCommand());

return await rootCommand.InvokeAsync(args);
