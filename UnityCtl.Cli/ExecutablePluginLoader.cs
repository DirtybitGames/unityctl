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
    private static readonly string[] CompanionExtensions = [".skill.md"];

    /// <summary>
    /// Discovers all executable plugins from plugin directories and optionally PATH.
    /// Plugin directories take precedence over PATH.
    /// </summary>
    /// <param name="excludeNames">Command names to skip (built-in + script plugins).</param>
    /// <param name="includePath">
    /// When true, also scans PATH directories (expensive). Only used by `plugin list`.
    /// At startup, PATH executables are resolved lazily via <see cref="TryExecuteByName"/>.
    /// </param>
    public static List<ExecutablePlugin> DiscoverExecutablePlugins(ISet<string>? excludeNames = null, bool includePath = false)
    {
        var plugins = new Dictionary<string, ExecutablePlugin>(StringComparer.OrdinalIgnoreCase);

        // PATH first (lower precedence) — only when explicitly requested
        if (includePath)
        {
            foreach (var plugin in ScanPath())
            {
                if (excludeNames != null && excludeNames.Contains(plugin.Name))
                    continue;
                plugins[plugin.Name] = plugin;
            }
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
    /// Attempts to execute an unrecognized command as "unityctl-{name}" on PATH,
    /// similar to how git resolves "git foo" → "git-foo". The OS performs the PATH
    /// lookup during process start, so no upfront scanning is needed.
    /// Returns null if no such executable exists, or the exit code if it ran.
    /// </summary>
    public static async Task<int?> TryExecuteByName(
        string name, string[] passThrough,
        string? projectPath = null, string? agentId = null, bool json = false, int? timeout = null)
    {
        var executableName = $"{ExecutablePrefix}{name}";

        var startInfo = CreateStartInfo(executableName, passThrough);
        SetPluginEnvironment(startInfo, projectPath, agentId, json, timeout);

        // Try to start "unityctl-<name>" directly. On Unix the OS resolves PATH.
        // On Windows with UseShellExecute=false, this finds .exe files on PATH.
        var result = await RunAndStreamAsync(startInfo);
        if (result.HasValue)
            return result.Value;

        // On Windows, .bat/.cmd scripts need cmd.exe /c to run.
        // Use PATHEXT-aware lookup via "where" to find the exact path first,
        // so we don't blindly invoke cmd.exe for non-existent commands.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var resolvedPath = await ResolveViaWhere(executableName);
            if (resolvedPath != null)
            {
                var cmdInfo = CreateStartInfo("cmd.exe", ["/c", resolvedPath, .. passThrough]);
                SetPluginEnvironment(cmdInfo, projectPath, agentId, json, timeout);
                result = await RunAndStreamAsync(cmdInfo);
                if (result.HasValue)
                    return result.Value;
            }
        }

        return null;
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
        var startInfo = CreateStartInfo(plugin.Path, passThrough);
        SetPluginEnvironment(startInfo,
            ContextHelper.GetProjectPath(context),
            ContextHelper.GetAgentId(context),
            ContextHelper.GetJson(context),
            ContextHelper.GetTimeout(context));

        var result = await RunAndStreamAsync(startInfo);
        if (result.HasValue)
            return result.Value;

        Console.Error.WriteLine($"Error: Failed to start plugin executable: {plugin.Path}");
        return 1;
    }

    /// <summary>
    /// Sets bridge connection and global option environment variables on a ProcessStartInfo.
    /// </summary>
    private static void SetPluginEnvironment(
        ProcessStartInfo startInfo,
        string? projectPath = null, string? agentId = null, bool json = false, int? timeout = null)
    {
        var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
        if (projectRoot != null)
        {
            startInfo.Environment["UNITYCTL_PROJECT_PATH"] = projectRoot;
            var bridgeConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
            if (bridgeConfig != null)
            {
                startInfo.Environment["UNITYCTL_BRIDGE_PORT"] = bridgeConfig.Port.ToString();
                startInfo.Environment["UNITYCTL_BRIDGE_URL"] = $"http://localhost:{bridgeConfig.Port}";
            }
        }

        if (agentId != null)
            startInfo.Environment["UNITYCTL_AGENT_ID"] = agentId;

        if (json)
            startInfo.Environment["UNITYCTL_JSON"] = "1";

        if (timeout.HasValue)
            startInfo.Environment["UNITYCTL_TIMEOUT"] = timeout.Value.ToString();
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var token in arguments)
            startInfo.ArgumentList.Add(token);

        return startInfo;
    }

    /// <summary>
    /// Starts a process, streams its stdout/stderr to the console, and returns the exit code.
    /// Returns null if the process could not be started (e.g., executable not found).
    /// </summary>
    private static async Task<int?> RunAndStreamAsync(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            var stderrTask = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Uses "where.exe" to resolve an executable name via PATHEXT on Windows.
    /// </summary>
    private static async Task<string?> ResolveViaWhere(string executableName)
    {
        try
        {
            var whereInfo = CreateStartInfo("where.exe", [executableName]);
            using var whereProc = Process.Start(whereInfo);
            if (whereProc == null)
                return null;

            var resolvedPath = (await whereProc.StandardOutput.ReadLineAsync())?.Trim();
            await whereProc.WaitForExitAsync();
            return whereProc.ExitCode == 0 ? resolvedPath : null;
        }
        catch
        {
            return null;
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
            files = Directory.EnumerateFiles(directory, "unityctl-*");
        }
        catch
        {
            yield break;
        }

        foreach (var filePath in files)
        {
            var fullFileName = Path.GetFileName(filePath);

            if (!fullFileName.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip companion files (e.g., unityctl-foo.skill.md)
            if (CompanionExtensions.Any(ext => fullFileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                continue;

            // On Unix, require execute permission
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !IsUnixExecutable(filePath))
                continue;

            // Extract command name: strip prefix, then strip extension
            var name = fullFileName.Substring(ExecutablePrefix.Length);
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
            return false;
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
