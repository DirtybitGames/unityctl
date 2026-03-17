using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class ConfigCommands
{
    private record ConfigOption(string Key, string JsonKey, string Type, string Description, string? Default = null);

    private static readonly ConfigOption[] Options =
    {
        new("project-path", "projectPath", "path", "Path to Unity project (for monorepo setups)"),
        new("bridge-port", "bridgePort", "integer (1-65535)", "Override bridge listening port", "random"),
        new("wait-timeout", "waitTimeout", "integer (seconds)", "Default timeout for 'unityctl wait'", "120"),
        new("enforce-version-match", "enforceVersionMatch", "boolean", "Error if Unity plugin version differs from CLI/Bridge", "false"),
    };

    private static readonly string[] ValidKeys = Options.Select(o => o.Key).ToArray();

    public static Command CreateCommand()
    {
        var configCommand = new Command("config", "Configuration management");

        configCommand.AddCommand(CreateSetCommand());
        configCommand.AddCommand(CreateGetCommand());
        configCommand.AddCommand(CreateListCommand());
        configCommand.AddCommand(CreateOptionsCommand());

        return configCommand;
    }

    private static Command CreateSetCommand()
    {
        var setCommand = new Command("set", "Set a configuration value");

        var keyArg = new Argument<string>("key", $"Configuration key ({string.Join(", ", ValidKeys)})");
        var valueArg = new Argument<string>("value", "Configuration value");

        setCommand.AddArgument(keyArg);
        setCommand.AddArgument(valueArg);

        setCommand.SetHandler(async (InvocationContext context) =>
        {
            var key = context.ParseResult.GetValueForArgument(keyArg);
            var value = context.ParseResult.GetValueForArgument(valueArg);
            var json = ContextHelper.GetJson(context);

            await SetConfigAsync(key, value, json);
        });

        return setCommand;
    }

    private static Command CreateGetCommand()
    {
        var getCommand = new Command("get", "Get a configuration value");

        var keyArg = new Argument<string>("key", $"Configuration key ({string.Join(", ", ValidKeys)})");

        getCommand.AddArgument(keyArg);

        getCommand.SetHandler(async (InvocationContext context) =>
        {
            var key = context.ParseResult.GetValueForArgument(keyArg);
            var json = ContextHelper.GetJson(context);

            await GetConfigAsync(key, json);
        });

        return getCommand;
    }

    private static Command CreateListCommand()
    {
        var listCommand = new Command("list", "List all configuration values");

        listCommand.SetHandler(async (InvocationContext context) =>
        {
            var json = ContextHelper.GetJson(context);

            await ListConfigAsync(json);
        });

        return listCommand;
    }

    private static Command CreateOptionsCommand()
    {
        var optionsCommand = new Command("options", "List all available configuration options");

        optionsCommand.SetHandler((InvocationContext context) =>
        {
            var json = ContextHelper.GetJson(context);

            if (json)
            {
                var items = Options.Select(o => new
                {
                    key = o.Key,
                    type = o.Type,
                    description = o.Description,
                    @default = o.Default,
                });
                Console.WriteLine(JsonHelper.Serialize(items));
            }
            else
            {
                Console.WriteLine("Available configuration options:");
                Console.WriteLine();
                foreach (var option in Options)
                {
                    Console.WriteLine($"  {option.Key}");
                    Console.WriteLine($"    Type:        {option.Type}");
                    Console.WriteLine($"    Description: {option.Description}");
                    if (option.Default != null)
                        Console.WriteLine($"    Default:     {option.Default}");
                    Console.WriteLine();
                }
                Console.WriteLine("Usage: unityctl config set <key> <value>");
            }
        });

        return optionsCommand;
    }

    private static string ToJsonKey(string cliKey) =>
        Options.FirstOrDefault(o => o.Key == cliKey)?.JsonKey ?? cliKey;

    private static string ToCliKey(string jsonKey) =>
        Options.FirstOrDefault(o => o.JsonKey == jsonKey)?.Key ?? jsonKey;

    private static string GetConfigDir()
    {
        return ProjectLocator.FindConfigDirectory()
            ?? ProjectLocator.FindDotUnityctlDirectory()
            ?? Path.Combine(Directory.GetCurrentDirectory(), ProjectLocator.BridgeConfigDir);
    }

    private static string GetConfigPath()
    {
        return Path.Combine(GetConfigDir(), ProjectLocator.ConfigFile);
    }

    private static async Task<JsonObject> ReadConfigObjectAsync()
    {
        var configPath = GetConfigPath();
        if (!File.Exists(configPath))
        {
            return new JsonObject();
        }

        try
        {
            var content = await File.ReadAllTextAsync(configPath);
            return JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static async Task WriteConfigObjectAsync(JsonObject config)
    {
        var configDir = GetConfigDir();
        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, ProjectLocator.ConfigFile);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var content = config.ToJsonString(options);
        await File.WriteAllTextAsync(configPath, content);
    }

    private static async Task SetConfigAsync(string key, string value, bool json)
    {
        // Validate key
        var normalizedKey = key.ToLowerInvariant();
        if (!Array.Exists(ValidKeys, k => k == normalizedKey))
        {
            Console.Error.WriteLine($"Error: Unknown configuration key '{key}'");
            Console.Error.WriteLine($"Valid keys: {string.Join(", ", ValidKeys)}");
            return;
        }

        var config = await ReadConfigObjectAsync();

        // Handle special keys
        switch (normalizedKey)
        {
            case "project-path":
                // Validate path is a Unity project
                var fullPath = Path.GetFullPath(value);
                var projectSettings = Path.Combine(fullPath, "ProjectSettings", "ProjectVersion.txt");
                if (!File.Exists(projectSettings))
                {
                    Console.Error.WriteLine($"Error: '{value}' is not a valid Unity project.");
                    Console.Error.WriteLine("Unity projects must contain ProjectSettings/ProjectVersion.txt");
                    return;
                }

                // Store relative path if possible for portability
                var configDir = GetConfigDir();
                var configDirParent = Path.GetDirectoryName(configDir) ?? configDir;
                var relativePath = Path.GetRelativePath(configDirParent, fullPath);
                config["projectPath"] = relativePath;
                break;

            case "bridge-port":
                if (!int.TryParse(value, out var port) || port < 1 || port > 65535)
                {
                    Console.Error.WriteLine($"Error: Invalid port number '{value}'");
                    return;
                }
                config["bridgePort"] = port;
                break;

            case "wait-timeout":
                if (!int.TryParse(value, out var timeout) || timeout < 1)
                {
                    Console.Error.WriteLine($"Error: Invalid timeout '{value}'. Must be a positive number of seconds.");
                    return;
                }
                config["waitTimeout"] = timeout;
                break;

            case "enforce-version-match":
                if (!bool.TryParse(value, out var enforce))
                {
                    Console.Error.WriteLine($"Error: Invalid value '{value}'. Must be 'true' or 'false'.");
                    return;
                }
                config["enforceVersionMatch"] = enforce;
                break;
        }

        await WriteConfigObjectAsync(config);

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new { success = true, key = normalizedKey, value }));
        }
        else
        {
            Console.WriteLine($"Set {normalizedKey} = {value}");
        }
    }

    private static async Task GetConfigAsync(string key, bool json)
    {
        var normalizedKey = key.ToLowerInvariant();
        if (!Array.Exists(ValidKeys, k => k == normalizedKey))
        {
            Console.Error.WriteLine($"Error: Unknown configuration key '{key}'");
            Console.Error.WriteLine($"Valid keys: {string.Join(", ", ValidKeys)}");
            return;
        }

        var config = await ReadConfigObjectAsync();

        var jsonKey = ToJsonKey(normalizedKey);

        var value = config[jsonKey]?.ToString();

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new { key = normalizedKey, value }));
        }
        else
        {
            if (value != null)
            {
                Console.WriteLine(value);
            }
            else
            {
                Console.Error.WriteLine($"Config key '{normalizedKey}' is not set");
            }
        }
    }

    private static async Task ListConfigAsync(bool json)
    {
        var config = await ReadConfigObjectAsync();

        if (json)
        {
            Console.WriteLine(config.ToJsonString());
        }
        else
        {
            var configPath = GetConfigPath();
            if (!File.Exists(configPath))
            {
                Console.WriteLine("No configuration file found.");
                Console.WriteLine("Run 'unityctl setup' to initialize, or 'unityctl config set <key> <value>' to create config.");
                return;
            }

            Console.WriteLine($"Configuration ({configPath}):");

            if (config.Count == 0)
            {
                Console.WriteLine("  (empty)");
            }
            else
            {
                foreach (var kvp in config)
                {
                    var displayKey = ToCliKey(kvp.Key);
                    Console.WriteLine($"  {displayKey} = {kvp.Value}");
                }
            }
        }
    }
}
