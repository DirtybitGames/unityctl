using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

/// <summary>
/// Discovers executables named "unityctl-{name}" on PATH and in plugin directories,
/// and registers them as top-level CLI commands (git/kubectl convention).
/// </summary>
public static class ExecutablePluginLoader
{
    private const string ExecutablePrefix = "unityctl-";

    /// <summary>
    /// Discovers all executable plugins from PATH and plugin directories.
    /// Returns deduplicated list; plugin directories take precedence over PATH.
    /// </summary>
    public static List<ExecutablePlugin> DiscoverExecutablePlugins(ISet<string>? excludeNames = null)
    {
        var plugins = new Dictionary<string, ExecutablePlugin>(StringComparer.OrdinalIgnoreCase);

        // PATH first (lower precedence)
        foreach (var plugin in ScanPath())
        {
            if (excludeNames != null && excludeNames.Contains(plugin.Name))
                continue;
            plugins[plugin.Name] = plugin;
        }

        // Plugin directories second (higher precedence, overwrites PATH)
        foreach (var plugin in ScanPluginDirectories())
        {
            if (excludeNames != null && excludeNames.Contains(plugin.Name))
                continue;
            plugins[plugin.Name] = plugin;
        }

        return plugins.Values.ToList();
    }

    /// <summary>
    /// Creates a System.CommandLine command for an executable plugin.
    /// All unrecognized tokens are passed through as arguments to the executable.
    /// </summary>
    public static Command CreateCommandFromExecutablePlugin(ExecutablePlugin plugin)
    {
        var command = new Command(plugin.Name, plugin.Description ?? $"Executable plugin: {plugin.Name}");

        // Catch-all argument to pass everything through to the executable
        var argsArgument = new Argument<string[]>("args")
        {
            Description = "Arguments to pass to the plugin executable",
            Arity = ArgumentArity.ZeroOrMore
        };
        command.AddArgument(argsArgument);

        command.SetHandler(async (InvocationContext context) =>
        {
            var passThrough = context.ParseResult.GetValueForArgument(argsArgument);
            context.ExitCode = await ExecutePluginAsync(context, plugin, passThrough);
        });

        return command;
    }

    /// <summary>
    /// Generates a SKILL.md section for an executable plugin from its companion .skill.md file.
    /// Returns null if no companion file exists.
    /// </summary>
    public static string? GetSkillSection(ExecutablePlugin plugin)
    {
        // Look for companion skill file: unityctl-foo.skill.md next to the executable
        var dir = Path.GetDirectoryName(plugin.Path);
        if (dir == null) return null;

        var skillFileName = $"{ExecutablePrefix}{plugin.Name}.skill.md";
        var skillPath = Path.Combine(dir, skillFileName);
        if (File.Exists(skillPath))
            return File.ReadAllText(skillPath);

        // Auto-generate minimal section
        var lines = new List<string>
        {
            $"### Plugin: {plugin.Name} (executable)",
        };
        if (!string.IsNullOrEmpty(plugin.Description))
            lines.Add($"\n{plugin.Description}");
        lines.Add("");
        lines.Add($"- `unityctl {plugin.Name} [args...]`");
        lines.Add($"  Runs external executable: {Path.GetFileName(plugin.Path)}");
        lines.Add("");

        return string.Join("\n", lines);
    }

    private static async Task<int> ExecutePluginAsync(
        InvocationContext context,
        ExecutablePlugin plugin,
        IReadOnlyList<string> passThrough)
    {
        var projectPath = ContextHelper.GetProjectPath(context);
        var agentId = ContextHelper.GetAgentId(context);
        var json = ContextHelper.GetJson(context);

        // Resolve project root and bridge config for environment variables
        var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
        BridgeConfig? bridgeConfig = null;
        if (projectRoot != null)
        {
            bridgeConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = plugin.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Pass through all arguments
        foreach (var token in passThrough)
            startInfo.ArgumentList.Add(token);

        // Set environment variables for plugin discovery
        if (projectRoot != null)
            startInfo.Environment["UNITYCTL_PROJECT_PATH"] = projectRoot;

        if (bridgeConfig != null)
        {
            startInfo.Environment["UNITYCTL_BRIDGE_PORT"] = bridgeConfig.Port.ToString();
            startInfo.Environment["UNITYCTL_BRIDGE_URL"] = $"http://localhost:{bridgeConfig.Port}";
        }

        if (agentId != null)
            startInfo.Environment["UNITYCTL_AGENT_ID"] = agentId;

        if (json)
            startInfo.Environment["UNITYCTL_JSON"] = "1";

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine($"Error: Failed to start plugin executable: {plugin.Path}");
                return 1;
            }

            // Stream stdout and stderr concurrently
            var stdoutTask = StreamOutputAsync(process.StandardOutput, Console.Out);
            var stderrTask = StreamOutputAsync(process.StandardError, Console.Error);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to execute plugin '{plugin.Name}': {ex.Message}");
            return 1;
        }
    }

    private static async Task StreamOutputAsync(StreamReader reader, TextWriter writer)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await writer.WriteAsync(buffer, 0, read);
        }
    }

    private static IEnumerable<ExecutablePlugin> ScanPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            yield break;

        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var dirs = pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var plugin in ScanDirectoryForExecutables(dir, "path"))
            {
                if (seen.Add(plugin.Name))
                    yield return plugin;
            }
        }
    }

    private static IEnumerable<ExecutablePlugin> ScanPluginDirectories()
    {
        // User-level: ~/.unityctl/plugins/
        var userDir = PluginLoader.GetUserPluginsDirectory();
        if (Directory.Exists(userDir))
        {
            foreach (var plugin in ScanDirectoryForExecutables(userDir, "user"))
                yield return plugin;
        }

        // Project-level: .unityctl/plugins/
        var projectDir = PluginLoader.GetProjectPluginsDirectory();
        if (projectDir != null && Directory.Exists(projectDir))
        {
            foreach (var plugin in ScanDirectoryForExecutables(projectDir, "project"))
                yield return plugin;
        }
    }

    private static IEnumerable<ExecutablePlugin> ScanDirectoryForExecutables(string directory, string source)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // On Windows, also match files with executable extensions
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Match: unityctl-foo.exe, unityctl-foo.cmd, unityctl-foo.bat, unityctl-foo.ps1
                var executableExtensions = new[] { ".exe", ".cmd", ".bat", ".ps1" };

                if (!fileName.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!executableExtensions.Contains(extension))
                    continue;

                var name = fileName.Substring(ExecutablePrefix.Length);
                if (string.IsNullOrEmpty(name))
                    continue;

                yield return new ExecutablePlugin
                {
                    Name = name.ToLowerInvariant(),
                    Path = filePath,
                    Source = source
                };
            }
            else
            {
                // On Unix: match any executable file named unityctl-*
                var fullFileName = Path.GetFileName(filePath);
                if (!fullFileName.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check if file is executable (has any execute bit)
                if (!IsUnixExecutable(filePath))
                    continue;

                var name = fullFileName.Substring(ExecutablePrefix.Length);
                // Strip extension if present (e.g., unityctl-foo.sh → foo)
                var dotIndex = name.IndexOf('.');
                if (dotIndex > 0)
                    name = name.Substring(0, dotIndex);

                if (string.IsNullOrEmpty(name))
                    continue;

                yield return new ExecutablePlugin
                {
                    Name = name.ToLowerInvariant(),
                    Path = filePath,
                    Source = source
                };
            }
        }
    }

    private static bool IsUnixExecutable(string filePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            return (File.GetUnixFileMode(filePath) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            // Fallback: assume executable if on Unix and we can't determine
            return true;
        }
    }
}

public class ExecutablePlugin
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required string Source { get; set; }
    public string? Description { get; set; }
}
