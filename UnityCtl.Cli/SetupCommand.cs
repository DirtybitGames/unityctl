using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class SetupCommand
{
    public static Command CreateCommand()
    {
        var setupCommand = new Command("setup", "Set up unityctl for a Unity project (config + package + skill)");

        var methodOption = new Option<string>(
            "--method",
            getDefaultValue: () => "upm",
            "Package installation method: upm (git URL) or local (file path)"
        );
        methodOption.AddAlias("-m");

        var skipPackageOption = new Option<bool>(
            "--skip-package",
            "Skip Unity package installation"
        );

        var skipSkillOption = new Option<bool>(
            "--skip-skill",
            "Skip Claude Code skill installation"
        );

        var globalSkillOption = new Option<bool>(
            "--global-skill",
            "Install skill globally (~/.claude/skills/) instead of locally"
        );

        var yesOption = new Option<bool>(
            "--yes",
            "Skip confirmation prompts (non-interactive mode)"
        );
        yesOption.AddAlias("-y");

        setupCommand.AddOption(methodOption);
        setupCommand.AddOption(skipPackageOption);
        setupCommand.AddOption(skipSkillOption);
        setupCommand.AddOption(globalSkillOption);
        setupCommand.AddOption(yesOption);

        setupCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var method = context.ParseResult.GetValueForOption(methodOption)!;
            var skipPackage = context.ParseResult.GetValueForOption(skipPackageOption);
            var skipSkill = context.ParseResult.GetValueForOption(skipSkillOption);
            var globalSkill = context.ParseResult.GetValueForOption(globalSkillOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            var json = ContextHelper.GetJson(context);

            await ExecuteAsync(projectPath, method, skipPackage, skipSkill, globalSkill, yes, json);
        });

        return setupCommand;
    }

    private static bool IsUnityProject(string path)
    {
        var projectSettings = Path.Combine(path, "ProjectSettings", "ProjectVersion.txt");
        return File.Exists(projectSettings);
    }

    private static async Task ExecuteAsync(
        string? projectPath,
        string method,
        bool skipPackage,
        bool skipSkill,
        bool globalSkill,
        bool yes,
        bool json)
    {
        var results = new List<SetupStepResult>();
        var currentDir = Directory.GetCurrentDirectory();
        string unityProjectPath;

        // Determine Unity project path
        if (!string.IsNullOrEmpty(projectPath))
        {
            // User specified --project
            unityProjectPath = Path.GetFullPath(projectPath);
            if (!IsUnityProject(unityProjectPath))
            {
                Console.Error.WriteLine($"Error: '{unityProjectPath}' is not a Unity project.");
                Console.Error.WriteLine("Unity projects must contain a ProjectSettings/ProjectVersion.txt file.");
                return;
            }
        }
        else if (IsUnityProject(currentDir))
        {
            // Current directory is a Unity project
            unityProjectPath = currentDir;
        }
        else
        {
            // Not in a Unity project - need to ask for path
            if (yes)
            {
                Console.Error.WriteLine("Error: Current directory is not a Unity project.");
                Console.Error.WriteLine("Use --project <path> to specify the Unity project path.");
                return;
            }

            Console.WriteLine("Current directory is not a Unity project.");
            Console.Write("Enter path to Unity project: ");
            var inputPath = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(inputPath))
            {
                Console.Error.WriteLine("No path provided. Setup cancelled.");
                return;
            }

            unityProjectPath = Path.GetFullPath(inputPath);
            if (!IsUnityProject(unityProjectPath))
            {
                Console.Error.WriteLine($"Error: '{unityProjectPath}' is not a Unity project.");
                Console.Error.WriteLine("Unity projects must contain a ProjectSettings/ProjectVersion.txt file.");
                return;
            }
        }

        // Determine if we need to create a config file (when running from outside Unity project)
        var needsConfig = !PathsEqual(currentDir, unityProjectPath);

        Console.WriteLine("Setting up unityctl...");
        Console.WriteLine($"  Unity project: {unityProjectPath}");
        if (needsConfig)
        {
            Console.WriteLine($"  Config location: {currentDir}");
        }
        Console.WriteLine();

        var totalSteps = (needsConfig ? 1 : 0) + (skipPackage ? 0 : 1) + (skipSkill ? 0 : 1);
        var currentStep = 0;

        // Step: Create config (only if running from outside Unity project)
        if (needsConfig)
        {
            currentStep++;
            Console.WriteLine($"Step {currentStep}/{totalSteps}: Creating project configuration...");
            try
            {
                var configDir = Path.Combine(currentDir, ProjectLocator.BridgeConfigDir);
                var configPath = Path.Combine(configDir, ProjectLocator.ConfigFile);

                Directory.CreateDirectory(configDir);

                // Store relative path for portability
                var relativePath = Path.GetRelativePath(currentDir, unityProjectPath);
                var config = new UnityCtlConfig { ProjectPath = relativePath };
                await File.WriteAllTextAsync(configPath, JsonHelper.Serialize(config));

                Console.WriteLine($"  Created {configPath}");
                Console.WriteLine($"  Project path: {relativePath}");
                results.Add(new SetupStepResult { Step = "config", Success = true });
            }
            catch (Exception ex)
            {
                results.Add(new SetupStepResult { Step = "config", Success = false, Error = ex.Message });
                Console.Error.WriteLine($"  Error: {ex.Message}");
            }
            Console.WriteLine();
        }

        // Step: Install Unity package
        if (!skipPackage)
        {
            currentStep++;
            Console.WriteLine($"Step {currentStep}/{totalSteps}: Installing Unity package...");
            try
            {
                await PackageCommands.AddPackageAsync(unityProjectPath, method, version: null, localPath: null, json: false);
                results.Add(new SetupStepResult { Step = "package", Success = true });
            }
            catch (Exception ex)
            {
                results.Add(new SetupStepResult { Step = "package", Success = false, Error = ex.Message });
                Console.Error.WriteLine($"  Error: {ex.Message}");
            }
            Console.WriteLine();
        }

        // Step: Install Claude skill
        if (!skipSkill)
        {
            currentStep++;
            Console.WriteLine($"Step {currentStep}/{totalSteps}: Installing Claude Code skill...");
            try
            {
                await SkillCommands.AddSkillAsync(globalSkill, claudeDir: null, force: yes, json: false);
                results.Add(new SetupStepResult { Step = "skill", Success = true });
            }
            catch (Exception ex)
            {
                results.Add(new SetupStepResult { Step = "skill", Success = false, Error = ex.Message });
                Console.Error.WriteLine($"  Error: {ex.Message}");
            }
            Console.WriteLine();
        }

        // Summary
        if (results.Count > 0)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("Setup Summary:");
            Console.WriteLine();

            var allSuccess = results.All(r => r.Success);
            foreach (var result in results)
            {
                var icon = result.Success ? "+" : "x";
                var status = result.Success ? "OK" : "FAILED";
                Console.WriteLine($"  [{icon}] {result.Step}: {status}");
                if (!result.Success && result.Error != null)
                {
                    Console.WriteLine($"      Error: {result.Error}");
                }
            }

            Console.WriteLine();

            if (allSuccess)
            {
                Console.WriteLine("Setup complete!");
                Console.WriteLine();
                Console.WriteLine("Next steps:");
                Console.WriteLine("  1. Start the bridge:  unityctl bridge start");
                Console.WriteLine("  2. Launch Unity:      unityctl editor run");
                Console.WriteLine("  3. Verify connection: unityctl status");
            }
            else
            {
                Console.WriteLine("Setup completed with errors. Please review the issues above.");
            }

            if (json)
            {
                Console.WriteLine();
                Console.WriteLine(JsonHelper.Serialize(new { success = allSuccess, results }));
            }
        }
        else
        {
            Console.WriteLine("Nothing to do. Use --help to see available options.");
        }
    }

    private static bool PathsEqual(string path1, string path2)
    {
        var normalized1 = Path.GetFullPath(path1).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized2 = Path.GetFullPath(path2).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Use case-insensitive comparison on Windows, case-sensitive on Unix
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalized1, normalized2, comparison);
    }

    private class SetupStepResult
    {
        public required string Step { get; init; }
        public bool Success { get; init; }
        public string? Error { get; init; }
    }
}
