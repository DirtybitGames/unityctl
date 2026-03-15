using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class PluginCommands
{
    private static readonly Regex ValidPluginName = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled);

    /// <summary>
    /// Built-in command names, populated at startup from rootCommand.Children
    /// so it stays in sync automatically when new commands are added.
    /// </summary>
    internal static ISet<string> BuiltInCommandNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static Command CreateCommand()
    {
        var pluginCommand = new Command("plugin", "Plugin management");

        pluginCommand.AddCommand(CreateListCommand());
        pluginCommand.AddCommand(CreateCreateCommand());
        pluginCommand.AddCommand(CreateRemoveCommand());

        return pluginCommand;
    }

    private static Command CreateListCommand()
    {
        var listCommand = new Command("list", "List installed plugins");
        listCommand.AddAlias("ls");

        listCommand.SetHandler((InvocationContext context) =>
        {
            var json = ContextHelper.GetJson(context);
            var (scriptPlugins, excludeNames) = PluginLoader.DiscoverWithExclusions(BuiltInCommandNames);
            var executablePlugins = ExecutablePluginLoader.DiscoverExecutablePlugins(excludeNames, includePath: true);

            if (json)
            {
                var scriptOutput = scriptPlugins.Select(p => new
                {
                    name = p.Manifest.Name,
                    type = "script",
                    version = p.Manifest.Version,
                    description = p.Manifest.Description,
                    commands = p.Manifest.Commands.Select(c => c.Name).ToArray(),
                    source = p.Source,
                    directory = p.Directory
                });
                var execOutput = executablePlugins.Select(p => new
                {
                    name = p.Name,
                    type = "executable",
                    version = (string?)null,
                    description = p.Description,
                    commands = Array.Empty<string>(),
                    source = p.Source,
                    directory = Path.GetDirectoryName(p.Path)
                });
                Console.WriteLine(JsonHelper.Serialize(scriptOutput.Concat<object>(execOutput)));
            }
            else
            {
                var totalCount = scriptPlugins.Count + executablePlugins.Count;
                if (totalCount == 0)
                {
                    Console.WriteLine("No plugins installed.");
                    Console.WriteLine();
                    Console.WriteLine("Create one with: unityctl plugin create <name>");
                    Console.WriteLine("Or place a 'unityctl-<name>' executable on PATH.");
                    return;
                }

                Console.WriteLine($"Installed plugins ({totalCount}):");
                Console.WriteLine();

                foreach (var plugin in scriptPlugins)
                {
                    var version = plugin.Manifest.Version != null ? $" v{plugin.Manifest.Version}" : "";
                    var cmds = string.Join(", ", plugin.Manifest.Commands.Select(c => c.Name));
                    Console.WriteLine($"  {plugin.Manifest.Name}{version} [script] ({plugin.Source})");
                    if (!string.IsNullOrEmpty(plugin.Manifest.Description))
                        Console.WriteLine($"    {plugin.Manifest.Description}");
                    if (cmds.Length > 0)
                        Console.WriteLine($"    Commands: {cmds}");
                    Console.WriteLine($"    Path: {ContextHelper.FormatPath(plugin.Directory)}");
                    Console.WriteLine();
                }

                foreach (var plugin in executablePlugins)
                {
                    Console.WriteLine($"  {plugin.Name} [executable] ({plugin.Source})");
                    if (!string.IsNullOrEmpty(plugin.Description))
                        Console.WriteLine($"    {plugin.Description}");
                    Console.WriteLine($"    Path: {ContextHelper.FormatPath(plugin.Path)}");
                    Console.WriteLine();
                }
            }
        });

        return listCommand;
    }

    private static Command CreateCreateCommand()
    {
        var createCommand = new Command("create", "Scaffold a new plugin");

        var nameArgument = new Argument<string>("name", "Plugin name");

        var globalOption = new Option<bool>(
            "--global",
            "Create in user-level ~/.unityctl/plugins/ instead of project-level"
        );
        globalOption.AddAlias("-g");

        createCommand.AddArgument(nameArgument);
        createCommand.AddOption(globalOption);

        createCommand.SetHandler((InvocationContext context) =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var global = context.ParseResult.GetValueForOption(globalOption);
            var json = ContextHelper.GetJson(context);

            if (!ValidPluginName.IsMatch(name))
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new
                    {
                        success = false,
                        error = "invalid_name",
                        name,
                        message = "Plugin name must contain only lowercase letters, digits, and hyphens, and must start and end with a letter or digit."
                    }));
                }
                else
                {
                    Console.Error.WriteLine($"Error: Invalid plugin name '{name}'.");
                    Console.Error.WriteLine("Plugin name must contain only lowercase letters, digits, and hyphens,");
                    Console.Error.WriteLine("and must start and end with a letter or digit.");
                }
                context.ExitCode = 1;
                return;
            }

            if (BuiltInCommandNames.Contains(name))
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new
                    {
                        success = false,
                        error = "name_conflict",
                        name,
                        message = $"'{name}' conflicts with a built-in command."
                    }));
                }
                else
                {
                    Console.Error.WriteLine($"Error: '{name}' conflicts with a built-in command.");
                }
                context.ExitCode = 1;
                return;
            }

            var pluginsDir = global
                ? PluginLoader.GetUserPluginsDirectory()
                : PluginLoader.GetProjectPluginsDirectoryOrDefault();

            var pluginDir = Path.Combine(pluginsDir, name);

            if (Directory.Exists(pluginDir))
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new
                    {
                        success = false,
                        error = "already_exists",
                        path = pluginDir
                    }));
                }
                else
                {
                    Console.Error.WriteLine($"Error: Plugin '{name}' already exists at: {pluginDir}");
                }
                context.ExitCode = 1;
                return;
            }

            Directory.CreateDirectory(pluginDir);

            // Write plugin.json
            var manifest = new PluginManifest
            {
                Name = name,
                Version = "1.0.0",
                Description = $"{name} plugin",
                Commands = new[]
                {
                    new PluginCommandDefinition
                    {
                        Name = "hello",
                        Description = "Say hello from Unity",
                        Handler = new PluginHandler { Type = "script", File = "hello.cs" }
                    }
                }
            };

            var manifestJson = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), manifestJson);

            // Write example script
            var exampleScript = @"using UnityEngine;

public class Script
{
    public static object Main(string[] args)
    {
        Debug.Log(""Hello from " + name + @" plugin!"");
        return ""Hello from Unity!"";
    }
}
";
            File.WriteAllText(Path.Combine(pluginDir, "hello.cs"), exampleScript);

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = true,
                    name,
                    path = pluginDir,
                    source = global ? "user" : "project"
                }));
            }
            else
            {
                Console.WriteLine($"Created plugin '{name}' at: {ContextHelper.FormatPath(pluginDir)}");
                Console.WriteLine();
                Console.WriteLine("Files created:");
                Console.WriteLine($"  plugin.json  - Plugin manifest");
                Console.WriteLine($"  hello.cs     - Example command script");
                Console.WriteLine();
                Console.WriteLine($"Try it: unityctl {name} hello");
                Console.WriteLine();
                Console.WriteLine("Run 'unityctl skill rebuild' to update the Claude Code skill.");
            }
        });

        return createCommand;
    }

    private static Command CreateRemoveCommand()
    {
        var removeCommand = new Command("remove", "Remove a plugin");
        removeCommand.AddAlias("rm");

        var nameArgument = new Argument<string>("name", "Plugin name to remove");

        removeCommand.AddArgument(nameArgument);

        removeCommand.SetHandler((InvocationContext context) =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var json = ContextHelper.GetJson(context);

            // Find the plugin
            var plugins = PluginLoader.DiscoverPlugins();
            var plugin = plugins.FirstOrDefault(p =>
                string.Equals(p.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));

            if (plugin == null)
            {
                // Check if it's an executable plugin (which can't be removed automatically)
                var execPlugins = ExecutablePluginLoader.DiscoverExecutablePlugins(includePath: true);
                var execMatch = execPlugins.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

                if (json)
                {
                    if (execMatch != null)
                    {
                        Console.WriteLine(JsonHelper.Serialize(new
                        {
                            success = false,
                            error = "executable_plugin",
                            name,
                            path = execMatch.Path,
                            message = "Executable plugins must be removed manually."
                        }));
                    }
                    else
                    {
                        Console.WriteLine(JsonHelper.Serialize(new
                        {
                            success = false,
                            error = "not_found",
                            name
                        }));
                    }
                }
                else
                {
                    if (execMatch != null)
                    {
                        Console.Error.WriteLine($"Error: '{name}' is an executable plugin and must be removed manually.");
                        Console.Error.WriteLine($"  Path: {ContextHelper.FormatPath(execMatch.Path)}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Error: Plugin '{name}' not found.");
                    }
                }
                context.ExitCode = 1;
                return;
            }

            // Delete plugin directory
            Directory.Delete(plugin.Directory, recursive: true);

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = true,
                    name = plugin.Manifest.Name,
                    path = plugin.Directory
                }));
            }
            else
            {
                Console.WriteLine($"Removed plugin '{plugin.Manifest.Name}' from: {ContextHelper.FormatPath(plugin.Directory)}");
                Console.WriteLine();
                Console.WriteLine("Run 'unityctl skill rebuild' to update the Claude Code skill.");
            }
        });

        return removeCommand;
    }
}
