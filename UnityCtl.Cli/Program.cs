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

// Add subcommands - Setup & Configuration
rootCommand.AddCommand(SetupCommand.CreateCommand());
rootCommand.AddCommand(UpdateCommands.CreateCommand());
rootCommand.AddCommand(ConfigCommands.CreateCommand());
rootCommand.AddCommand(PackageCommands.CreateCommand());
rootCommand.AddCommand(SkillCommands.CreateCommand());

// Add subcommands - Status & Logs
rootCommand.AddCommand(StatusCommand.CreateCommand());
rootCommand.AddCommand(WaitCommand.CreateCommand());
rootCommand.AddCommand(LogsCommand.CreateCommand());

// Add subcommands - Bridge & Editor
rootCommand.AddCommand(BridgeCommands.CreateCommand());
rootCommand.AddCommand(EditorCommands.CreateCommand());

// Add subcommands - Unity Operations
rootCommand.AddCommand(SceneCommands.CreateCommand());
rootCommand.AddCommand(PlayCommands.CreateCommand());
rootCommand.AddCommand(AssetCommands.CreateCommand());
rootCommand.AddCommand(MenuCommands.CreateCommand());
rootCommand.AddCommand(TestCommands.CreateCommand());
rootCommand.AddCommand(ScreenshotCommands.CreateCommand());
rootCommand.AddCommand(ScriptCommands.CreateCommand());

return await rootCommand.InvokeAsync(args);
