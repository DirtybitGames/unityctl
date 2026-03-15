using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

/// <summary>
/// Discovers and loads plugins from .unityctl/plugins/ (project-level) and ~/.unityctl/plugins/ (user-level).
/// Project-level plugins take precedence over user-level plugins with the same name.
/// </summary>
public static class PluginLoader
{
    public const string PluginDirName = "plugins";
    public const string ManifestFileName = "plugin.json";

    /// <summary>
    /// Discovers all plugins from both project-level and user-level directories.
    /// Project-level plugins override user-level plugins with the same name.
    /// </summary>
    public static List<LoadedPlugin> DiscoverPlugins()
    {
        var plugins = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);

        // User-level first (lower precedence)
        var userDir = GetUserPluginsDirectory();
        if (userDir != null)
        {
            foreach (var plugin in LoadPluginsFromDirectory(userDir, "user"))
                plugins[plugin.Manifest.Name] = plugin;
        }

        // Project-level second (higher precedence, overwrites user-level)
        var projectDir = GetProjectPluginsDirectory();
        if (projectDir != null)
        {
            foreach (var plugin in LoadPluginsFromDirectory(projectDir, "project"))
                plugins[plugin.Manifest.Name] = plugin;
        }

        return plugins.Values.ToList();
    }

    /// <summary>
    /// Creates System.CommandLine commands from a loaded plugin manifest.
    /// Each plugin becomes a top-level command with subcommands from the manifest.
    /// </summary>
    public static Command CreateCommandFromPlugin(LoadedPlugin plugin)
    {
        var pluginCommand = new Command(
            plugin.Manifest.Name,
            plugin.Manifest.Description ?? $"Plugin: {plugin.Manifest.Name}"
        );

        foreach (var cmdDef in plugin.Manifest.Commands)
        {
            var subCommand = new Command(cmdDef.Name, cmdDef.Description ?? cmdDef.Name);

            // Add arguments
            var argObjects = new List<Argument<string>>();
            foreach (var argDef in cmdDef.Arguments)
            {
                var arg = argDef.Required
                    ? new Argument<string>(argDef.Name, argDef.Description ?? argDef.Name)
                    : new Argument<string>(argDef.Name, getDefaultValue: () => string.Empty, description: argDef.Description ?? argDef.Name);
                subCommand.AddArgument(arg);
                argObjects.Add(arg);
            }

            // Add options
            var optObjects = new List<(string Name, Option Option, string Type)>();
            foreach (var optDef in cmdDef.Options)
            {
                var optName = optDef.Name.StartsWith('-') ? optDef.Name : $"--{optDef.Name}";

                if (optDef.Type == "bool")
                {
                    var opt = new Option<bool>(optName, optDef.Description ?? optDef.Name);
                    subCommand.AddOption(opt);
                    optObjects.Add((optDef.Name, opt, "bool"));
                }
                else
                {
                    var opt = new Option<string?>(optName, optDef.Description ?? optDef.Name);
                    subCommand.AddOption(opt);
                    optObjects.Add((optDef.Name, opt, "string"));
                }
            }

            // Capture for closure
            var handlerFile = cmdDef.Handler.File;
            var pluginDir = plugin.Directory;
            var capturedArgs = argObjects.ToList();
            var capturedOpts = optObjects.ToList();

            subCommand.SetHandler(async (InvocationContext context) =>
            {
                await ExecutePluginCommandAsync(context, pluginDir, handlerFile, capturedArgs, capturedOpts);
            });

            pluginCommand.AddCommand(subCommand);
        }

        return pluginCommand;
    }

    private static async Task ExecutePluginCommandAsync(
        InvocationContext context,
        string pluginDir,
        string handlerFile,
        List<Argument<string>> args,
        List<(string Name, Option Option, string Type)> opts)
    {
        var projectPath = ContextHelper.GetProjectPath(context);
        var agentId = ContextHelper.GetAgentId(context);
        var json = ContextHelper.GetJson(context);
        var timeout = ContextHelper.GetTimeout(context);

        // Read the script file
        var scriptPath = Path.Combine(pluginDir, handlerFile);
        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"Error: Plugin script not found: {scriptPath}");
            context.ExitCode = 1;
            return;
        }

        var csharpCode = await File.ReadAllTextAsync(scriptPath);

        // Build scriptArgs from plugin arguments and options
        var scriptArgs = new List<string>();
        foreach (var arg in args)
        {
            var value = context.ParseResult.GetValueForArgument(arg);
            if (!string.IsNullOrEmpty(value))
                scriptArgs.Add(value);
        }
        foreach (var (name, opt, type) in opts)
        {
            if (type == "bool")
            {
                var boolOpt = (Option<bool>)opt;
                if (context.ParseResult.GetValueForOption(boolOpt))
                    scriptArgs.Add($"--{name}");
            }
            else
            {
                var strOpt = (Option<string?>)opt;
                var value = context.ParseResult.GetValueForOption(strOpt);
                if (!string.IsNullOrEmpty(value))
                {
                    scriptArgs.Add($"--{name}");
                    scriptArgs.Add(value);
                }
            }
        }

        var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
        if (client == null) { context.ExitCode = 1; return; }

        var rpcArgs = new Dictionary<string, object?>
        {
            { "code", csharpCode },
            { "className", "Script" },
            { "methodName", "Main" },
            { "scriptArgs", scriptArgs.ToArray() }
        };

        var response = await client.SendCommandAsync(UnityCtlCommands.ScriptExecute, rpcArgs, timeout);
        if (response == null) { context.ExitCode = 1; return; }

        if (response.Status == ResponseStatus.Error)
        {
            Console.Error.WriteLine($"Error: {response.Error?.Message}");
            context.ExitCode = 1;
            return;
        }

        ScriptCommands.DisplayScriptResult(context, response, json);
    }

    /// <summary>
    /// Walks up from CWD to find the nearest .unityctl/ directory.
    /// Returns null if none found.
    /// </summary>
    public static string? FindDotUnityctlDirectory()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, ".unityctl");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        return null;
    }

    public static string? GetProjectPluginsDirectory()
    {
        var dotUnityctl = FindDotUnityctlDirectory();
        if (dotUnityctl == null) return null;

        var pluginsDir = Path.Combine(dotUnityctl, PluginDirName);
        return Directory.Exists(pluginsDir) ? pluginsDir : null;
    }

    public static string GetProjectPluginsDirectoryOrDefault()
    {
        var dotUnityctl = FindDotUnityctlDirectory()
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".unityctl");
        return Path.Combine(dotUnityctl, PluginDirName);
    }

    public static string GetUserPluginsDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".unityctl", PluginDirName);
    }

    private static List<LoadedPlugin> LoadPluginsFromDirectory(string pluginsDir, string source)
    {
        var plugins = new List<LoadedPlugin>();

        if (!Directory.Exists(pluginsDir))
            return plugins;

        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonConvert.DeserializeObject<PluginManifest>(json);
                if (manifest != null)
                {
                    plugins.Add(new LoadedPlugin
                    {
                        Manifest = manifest,
                        Directory = dir,
                        Source = source
                    });
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to load plugin from {dir}: {ex.Message}");
            }
        }

        return plugins;
    }

    /// <summary>
    /// Generates a SKILL.md section from a plugin manifest.
    /// </summary>
    public static string GenerateSkillSection(LoadedPlugin plugin)
    {
        // Check for custom skill section file
        if (plugin.Manifest.Skill != null)
        {
            var skillPath = Path.Combine(plugin.Directory, plugin.Manifest.Skill.File);
            if (File.Exists(skillPath))
                return File.ReadAllText(skillPath);
        }

        // Auto-generate from manifest
        var lines = new List<string>();
        lines.Add($"### Plugin: {plugin.Manifest.Name}");
        if (!string.IsNullOrEmpty(plugin.Manifest.Description))
            lines.Add($"\n{plugin.Manifest.Description}");
        lines.Add("");

        foreach (var cmd in plugin.Manifest.Commands)
        {
            var usage = $"unityctl {plugin.Manifest.Name} {cmd.Name}";

            // Add arguments to usage
            foreach (var arg in cmd.Arguments)
            {
                usage += arg.Required ? $" <{arg.Name}>" : $" [{arg.Name}]";
            }

            // Add options to usage
            foreach (var opt in cmd.Options)
            {
                var optName = opt.Name.StartsWith('-') ? opt.Name : $"--{opt.Name}";
                usage += opt.Type == "bool" ? $" [{optName}]" : $" [{optName} <value>]";
            }

            lines.Add($"- `{usage}`");
            if (!string.IsNullOrEmpty(cmd.Description))
                lines.Add($"  {cmd.Description}");
        }

        lines.Add("");
        return string.Join("\n", lines);
    }
}

public class LoadedPlugin
{
    public required PluginManifest Manifest { get; set; }
    public required string Directory { get; set; }
    public required string Source { get; set; }
}
