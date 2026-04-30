using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityCtl.Cli;

// Use invariant culture for argument parsing and output. Avoids locale-specific
// decimal separators (e.g. "0,5" vs "0.5") that break --duration / threshold flags.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

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
rootCommand.AddCommand(ProfileCommands.CreateCommand());
rootCommand.AddCommand(ScriptCommands.CreateCommand());
rootCommand.AddCommand(SnapshotCommand.CreateCommand());
rootCommand.AddCommand(PrefabCommand.CreateCommand());
rootCommand.AddCommand(UICommands.CreateCommand());

// Derive built-in command names dynamically from registered commands
var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var cmd in rootCommand.Children.OfType<Command>())
    registeredNames.Add(cmd.Name);
PluginCommands.InitializeBuiltInCommandNames(registeredNames);

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

// Register executable plugins from plugin directories only (cheap scan).
// PATH executables are resolved lazily at invocation time (git-style).
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

// If the first non-option arg doesn't match any registered command, try to run it as
// "unityctl-<name>" on PATH (like git resolves "git foo" → "git-foo").
// Use System.CommandLine's parser to extract global options so they're forwarded correctly.
if (args.Length > 0)
{
    var parseResult = rootCommand.Parse(args);
    var unmatchedCommand = parseResult.UnmatchedTokens.FirstOrDefault();
    if (unmatchedCommand != null && !unmatchedCommand.StartsWith("-"))
    {
        var passThrough = parseResult.UnmatchedTokens.Skip(1).ToArray();
        var exitCode = await ExecutablePluginLoader.TryExecuteByName(
            unmatchedCommand, passThrough,
            parseResult.GetValueForOption(projectOption),
            parseResult.GetValueForOption(agentIdOption),
            parseResult.GetValueForOption(jsonOption),
            parseResult.GetValueForOption(timeoutOption));
        if (exitCode.HasValue)
            return exitCode.Value;
    }
}

return await rootCommand.InvokeAsync(args);
