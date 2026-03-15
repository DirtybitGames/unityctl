using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
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

var timeoutOption = new Option<int?>(
    aliases: ["--timeout", "-t"],
    description: "Timeout in seconds for commands sent to Unity (overrides the per-command default)"
);

rootCommand.AddGlobalOption(projectOption);
rootCommand.AddGlobalOption(agentIdOption);
rootCommand.AddGlobalOption(jsonOption);
rootCommand.AddGlobalOption(timeoutOption);

// Add subcommands - Setup & Configuration
rootCommand.AddCommand(SetupCommand.CreateCommand());
rootCommand.AddCommand(UpdateCommands.CreateCommand());
rootCommand.AddCommand(ConfigCommands.CreateCommand());
rootCommand.AddCommand(PackageCommands.CreateCommand());
rootCommand.AddCommand(SkillCommands.CreateCommand());
rootCommand.AddCommand(PluginCommands.CreateCommand());

// Add subcommands - Status & Logs
rootCommand.AddCommand(StatusCommand.CreateCommand());
rootCommand.AddCommand(WaitCommand.CreateCommand());
rootCommand.AddCommand(LogsCommand.CreateCommand());

// Add subcommands - Bridge & Editor
rootCommand.AddCommand(BridgeCommands.CreateCommand());
rootCommand.AddCommand(EditorCommands.CreateCommand());
rootCommand.AddCommand(DialogCommands.CreateCommand());

// Add subcommands - Unity Operations
rootCommand.AddCommand(SceneCommands.CreateCommand());
rootCommand.AddCommand(PlayCommands.CreateCommand());
rootCommand.AddCommand(AssetCommands.CreateCommand());
rootCommand.AddCommand(MenuCommands.CreateCommand());
rootCommand.AddCommand(TestCommands.CreateCommand());
rootCommand.AddCommand(ScreenshotCommands.CreateCommand());
rootCommand.AddCommand(RecordCommands.CreateCommand());
rootCommand.AddCommand(ScriptCommands.CreateCommand());
rootCommand.AddCommand(SnapshotCommand.CreateCommand());
rootCommand.AddCommand(PrefabCommand.CreateCommand());

// Derive built-in command names dynamically from registered commands
var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var cmd in rootCommand.Children.OfType<Command>())
    registeredNames.Add(cmd.Name);
PluginCommands.BuiltInCommandNames = registeredNames;

try
{
    var plugins = PluginLoader.DiscoverPlugins();
    foreach (var plugin in plugins)
    {
        if (registeredNames.Contains(plugin.Manifest.Name))
        {
            Console.Error.WriteLine($"Warning: Plugin '{plugin.Manifest.Name}' conflicts with a built-in command and was skipped.");
            continue;
        }
        rootCommand.AddCommand(PluginLoader.CreateCommandFromPlugin(plugin));
        registeredNames.Add(plugin.Manifest.Name);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Warning: Failed to load plugins: {ex.Message}");
}

// Dynamically register executable plugins (lowest precedence)
try
{
    var executablePlugins = ExecutablePluginLoader.DiscoverExecutablePlugins(registeredNames);
    foreach (var plugin in executablePlugins)
    {
        rootCommand.AddCommand(ExecutablePluginLoader.CreateCommandFromExecutablePlugin(plugin));
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Warning: Failed to load executable plugins: {ex.Message}");
}

// "Did you mean?" hints for common misses
CommandHints.Register(rootCommand);

return await rootCommand.InvokeAsync(args);
