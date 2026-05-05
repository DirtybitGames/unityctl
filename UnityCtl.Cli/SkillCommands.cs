using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class SkillCommands
{
    private const string SkillFolderName = "unity-editor";
    private const string SkillFileName = "SKILL.md";
    private const string EmbeddedResourceName = "UnityCtl.Cli.Resources.SKILL.md";

    private const string SkillExtraFileName = "skill-extra.md";

    /// <summary>
    /// Additional skills that are installed/updated alongside the main unity-editor skill.
    /// Each entry is (folderName, embeddedResourceName).
    /// </summary>
    private static readonly (string Folder, string Resource)[] AdditionalSkills =
    [
        ("unityctl-plugins", "UnityCtl.Cli.Resources.SKILL.plugins.md")
    ];

    /// <summary>
    /// Sidecar files written alongside the main SKILL.md inside the unity-editor skill folder.
    /// Not auto-loaded — the base skill mentions them by relative path so agents can read them
    /// when the topic is relevant. Each entry is (fileName, embeddedResourceName).
    /// </summary>
    private static readonly (string FileName, string Resource)[] Sidecars =
    [
        ("profiling.md", "UnityCtl.Cli.Resources.profiling.md")
    ];

    public static Command CreateCommand()
    {
        var skillCommand = new Command("skill", "Claude Code skill management");

        skillCommand.AddCommand(CreateAddCommand());
        skillCommand.AddCommand(CreateRebuildCommand());
        skillCommand.AddCommand(CreateRemoveCommand());
        skillCommand.AddCommand(CreateStatusCommand());

        return skillCommand;
    }

    private static Command CreateAddCommand()
    {
        var addCommand = new Command("add", "Add Claude Code skill");

        var globalOption = new Option<bool>(
            "--global",
            "Add to global ~/.claude/skills/ instead of local .claude/skills/"
        );
        globalOption.AddAlias("-g");

        var claudeDirOption = new Option<string?>(
            "--claude-dir",
            "Custom Claude directory (default: ~/.claude or ./.claude)"
        );

        var forceOption = new Option<bool>(
            "--force",
            "Overwrite existing skill file without prompting"
        );
        forceOption.AddAlias("-f");

        addCommand.AddOption(globalOption);
        addCommand.AddOption(claudeDirOption);
        addCommand.AddOption(forceOption);

        addCommand.SetHandler(async (InvocationContext context) =>
        {
            var global = context.ParseResult.GetValueForOption(globalOption);
            var claudeDir = context.ParseResult.GetValueForOption(claudeDirOption);
            var force = context.ParseResult.GetValueForOption(forceOption);
            var json = ContextHelper.GetJson(context);

            if (!await AddSkillAsync(global, claudeDir, force, json))
                context.ExitCode = 1;
        });

        return addCommand;
    }

    private static Command CreateRebuildCommand()
    {
        var rebuildCommand = new Command("rebuild", "Rebuild composed SKILL.md from base + plugins + user extra");

        var globalOption = new Option<bool>(
            "--global",
            "Rebuild global ~/.claude/skills/ instead of local .claude/skills/"
        );
        globalOption.AddAlias("-g");

        var claudeDirOption = new Option<string?>(
            "--claude-dir",
            "Custom Claude directory (default: ~/.claude or ./.claude)"
        );

        rebuildCommand.AddOption(globalOption);
        rebuildCommand.AddOption(claudeDirOption);

        rebuildCommand.SetHandler(async (InvocationContext context) =>
        {
            var global = context.ParseResult.GetValueForOption(globalOption);
            var claudeDir = context.ParseResult.GetValueForOption(claudeDirOption);
            var json = ContextHelper.GetJson(context);

            var skillsDir = GetSkillsDirectory(global, claudeDir);
            if (!await WriteComposedSkillAsync(skillsDir, json))
            {
                context.ExitCode = 1;
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = true,
                    path = Path.Combine(skillsDir, SkillFolderName, SkillFileName),
                    global,
                    composed = true
                }));
            }
            else
            {
                Console.WriteLine($"Rebuilt skill at: {Path.Combine(skillsDir, SkillFolderName, SkillFileName)}");
                Console.WriteLine("Restart Claude Code to load the updated skill.");
            }
        });

        return rebuildCommand;
    }

    /// <summary>
    /// Composes the final SKILL.md from: base embedded skill + plugin sections + user extra.
    /// The base skill lives in UnityCtl.Cli/Resources/ and is never overwritten by compose output.
    /// </summary>
    public static string? ComposeSkillContent()
    {
        // 1. Base skill content (from embedded resource in Resources/SKILL.md)
        var baseContent = GetEmbeddedSkillContent();
        if (baseContent == null)
            return null;

        // 2. Plugin sections (script + executable)
        var (plugins, excludeNames) = PluginLoader.DiscoverWithExclusions(PluginCommands.BuiltInCommandNames);
        var executablePlugins = ExecutablePluginLoader.DiscoverExecutablePlugins(excludeNames);
        string? pluginSections = null;
        if (plugins.Count > 0 || executablePlugins.Count > 0)
        {
            var sections = new List<string>();
            sections.Add("## Plugin Commands");
            sections.Add("");
            foreach (var plugin in plugins)
            {
                sections.Add(PluginLoader.GenerateSkillSection(plugin));
            }
            foreach (var execPlugin in executablePlugins)
            {
                var section = ExecutablePluginLoader.GetSkillSection(execPlugin);
                if (section != null)
                    sections.Add(section);
            }
            pluginSections = string.Join("\n", sections);
        }

        // 3. User extra (.unityctl/skill-extra.md)
        string? userExtra = null;
        var extraPath = FindSkillExtraFile();
        if (extraPath != null && File.Exists(extraPath))
        {
            userExtra = File.ReadAllText(extraPath);
        }

        // Compose
        if (pluginSections == null && userExtra == null)
            return baseContent;

        var composed = baseContent.TrimEnd();

        if (pluginSections != null)
        {
            composed += "\n\n" + pluginSections.TrimEnd();
        }

        if (userExtra != null)
        {
            composed += "\n\n" + userExtra.TrimEnd();
        }

        return composed + "\n";
    }

    private static string? FindSkillExtraFile()
    {
        var dotUnityctl = PluginLoader.FindDotUnityctlDirectory();
        if (dotUnityctl == null) return null;

        var extraPath = Path.Combine(dotUnityctl, SkillExtraFileName);
        return File.Exists(extraPath) ? extraPath : null;
    }

    private static Command CreateRemoveCommand()
    {
        var removeCommand = new Command("remove", "Remove Claude Code skill");

        var globalOption = new Option<bool>(
            "--global",
            "Remove from global ~/.claude/skills/ instead of local .claude/skills/"
        );
        globalOption.AddAlias("-g");

        var claudeDirOption = new Option<string?>(
            "--claude-dir",
            "Custom Claude directory"
        );

        removeCommand.AddOption(globalOption);
        removeCommand.AddOption(claudeDirOption);

        removeCommand.SetHandler((InvocationContext context) =>
        {
            var global = context.ParseResult.GetValueForOption(globalOption);
            var claudeDir = context.ParseResult.GetValueForOption(claudeDirOption);
            var json = ContextHelper.GetJson(context);

            if (!RemoveSkill(global, claudeDir, json))
                context.ExitCode = 1;
        });

        return removeCommand;
    }

    private static Command CreateStatusCommand()
    {
        var statusCommand = new Command("status", "Show skill installation status");

        statusCommand.SetHandler((InvocationContext context) =>
        {
            var json = ContextHelper.GetJson(context);

            ShowSkillStatus(json);
        });

        return statusCommand;
    }

    private static string GetSkillsDirectory(bool global, string? customClaudeDir)
    {
        string claudeDir;

        if (!string.IsNullOrEmpty(customClaudeDir))
        {
            claudeDir = customClaudeDir;
        }
        else if (global)
        {
            // Global: ~/.claude
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            claudeDir = Path.Combine(home, ".claude");
        }
        else
        {
            // Local: .claude in current directory
            claudeDir = Path.Combine(Directory.GetCurrentDirectory(), ".claude");
        }

        return Path.Combine(claudeDir, "skills");
    }

    private static string? GetEmbeddedSkillContent()
    {
        return GetEmbeddedResourceContent(EmbeddedResourceName);
    }

    private static string? GetEmbeddedResourceContent(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
#if DEBUG
            // Fallback: try to find file in Resources/ during development when the
            // embedded resource may not be available (e.g. running via `dotnet run`).
            var prefix = "UnityCtl.Cli.Resources.";
            var fileName = resourceName.StartsWith(prefix)
                ? resourceName.Substring(prefix.Length)
                : "SKILL.md";

            var possiblePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "UnityCtl.Cli", "Resources", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "UnityCtl.Cli", "Resources", fileName),
            };

            foreach (var path in possiblePaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                    return File.ReadAllText(fullPath);
            }
#endif
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Updates the skill file at the given path with the latest embedded content.
    /// Also updates any additional skills installed in the same skills directory.
    /// Returns true if successful, false if embedded content was not found.
    /// </summary>
    public static async Task<bool> UpdateSkillFileAsync(string skillPath)
    {
        var content = ComposeSkillContent();
        if (content == null) return false;

        var dir = Path.GetDirectoryName(skillPath);
        if (dir != null) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(skillPath, content);

        // Update additional skills if they exist alongside the main skill
        var skillsDir = Path.GetDirectoryName(dir); // up from unity-editor/ to skills/
        if (skillsDir != null)
        {
            await InstallAdditionalSkillsAsync(skillsDir, onlyIfExists: true);
        }

        return true;
    }

    public static async Task<bool> AddSkillAsync(bool global, string? claudeDir, bool force, bool json)
    {
        var skillsDir = GetSkillsDirectory(global, claudeDir);
        var skillPath = Path.Combine(skillsDir, SkillFolderName, SkillFileName);

        // Check if skill already exists
        if (File.Exists(skillPath) && !force)
        {
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = false,
                    error = "already_exists",
                    path = skillPath
                }));
                return false;
            }
            else
            {
                Console.WriteLine($"Skill already exists at: {skillPath}");
                Console.Write("Overwrite? [y/N]: ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    Console.WriteLine("Aborted.");
                    return false;
                }
            }
        }

        if (!await WriteComposedSkillAsync(skillsDir, json))
            return false;

        // Install additional skills
        await InstallAdditionalSkillsAsync(skillsDir, onlyIfExists: false);

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new
            {
                success = true,
                path = skillPath,
                global,
                location = global ? "global" : "local"
            }));
        }
        else
        {
            Console.WriteLine($"Installed Claude Code skills to: {skillsDir}");
            Console.WriteLine();
            if (global)
            {
                Console.WriteLine("The skills are now available globally for all projects.");
            }
            else
            {
                Console.WriteLine("The skills are now available for this project.");
                Console.WriteLine("Use --global to install for all projects.");
            }
            Console.WriteLine();
            Console.WriteLine("Restart Claude Code to load the new skills.");
        }

        return true;
    }

    /// <summary>
    /// Composes the skill content and writes it to the main skill file in the given skills directory.
    /// Returns false and prints an error if the base skill resource is missing.
    /// </summary>
    private static async Task<bool> WriteComposedSkillAsync(string skillsDir, bool json)
    {
        var content = ComposeSkillContent();
        if (content == null)
        {
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = false,
                    error = "skill_not_found",
                    message = "Could not find embedded SKILL.md resource"
                }));
            }
            else
            {
                Console.Error.WriteLine("Error: Could not find skill file.");
                Console.Error.WriteLine("The SKILL.md resource may not be embedded in this build.");
            }
            return false;
        }

        var skillFolderPath = Path.Combine(skillsDir, SkillFolderName);
        Directory.CreateDirectory(skillFolderPath);
        await File.WriteAllTextAsync(Path.Combine(skillFolderPath, SkillFileName), content);

        // Write sidecar files (e.g. profiling.md). They aren't @-referenced by SKILL.md so they
        // stay out of the auto-loaded context, but agents can read them when the topic is relevant.
        foreach (var (fileName, resource) in Sidecars)
        {
            var sidecarContent = GetEmbeddedResourceContent(resource);
            if (sidecarContent == null) continue;
            await File.WriteAllTextAsync(Path.Combine(skillFolderPath, fileName), sidecarContent);
        }
        return true;
    }

    private static bool RemoveSkill(bool global, string? claudeDir, bool json)
    {
        var skillsDir = GetSkillsDirectory(global, claudeDir);
        var skillFolderPath = Path.Combine(skillsDir, SkillFolderName);
        var skillPath = Path.Combine(skillFolderPath, SkillFileName);

        if (!File.Exists(skillPath))
        {
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = false,
                    error = "not_installed",
                    path = skillPath
                }));
            }
            else
            {
                Console.Error.WriteLine($"Skill not found at: {skillPath}");
            }
            return false;
        }

        File.Delete(skillPath);

        // Remove sidecars co-located with the main skill.
        foreach (var (fileName, _) in Sidecars)
        {
            var sidecarPath = Path.Combine(skillFolderPath, fileName);
            if (File.Exists(sidecarPath))
                File.Delete(sidecarPath);
        }

        // Remove additional skills
        foreach (var (folder, _) in AdditionalSkills)
        {
            var additionalPath = Path.Combine(skillsDir, folder, SkillFileName);
            if (File.Exists(additionalPath))
                File.Delete(additionalPath);
        }

        // Try to clean up empty directories
        try
        {
            CleanupEmptyDirectory(skillFolderPath);
            foreach (var (folder, _) in AdditionalSkills)
                CleanupEmptyDirectory(Path.Combine(skillsDir, folder));
            CleanupEmptyDirectory(skillsDir);
        }
        catch
        {
            // Ignore cleanup errors
        }

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new { success = true, path = skillsDir }));
        }
        else
        {
            Console.WriteLine($"Removed skills from: {skillsDir}");
        }
        return true;
    }

    private static void CleanupEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    /// <summary>
    /// Installs additional skill files alongside the main unity-editor skill.
    /// When onlyIfExists is true, only updates skills that already have a folder.
    /// </summary>
    private static async Task InstallAdditionalSkillsAsync(string skillsDir, bool onlyIfExists)
    {
        foreach (var (folder, resource) in AdditionalSkills)
        {
            var folderPath = Path.Combine(skillsDir, folder);
            var filePath = Path.Combine(folderPath, SkillFileName);

            if (onlyIfExists && !File.Exists(filePath))
                continue;

            var content = GetEmbeddedResourceContent(resource);
            if (content == null)
                continue;

            Directory.CreateDirectory(folderPath);
            await File.WriteAllTextAsync(filePath, content);
        }
    }

    private static void ShowSkillStatus(bool json)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSkillsDir = Path.Combine(home, ".claude", "skills");
        var localSkillsDir = Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills");

        // All skill folders to check
        var allFolders = new[] { SkillFolderName }.Concat(AdditionalSkills.Select(s => s.Folder)).ToArray();

        if (json)
        {
            var skills = allFolders.Select(folder => new
            {
                name = folder,
                global = new { installed = File.Exists(Path.Combine(globalSkillsDir, folder, SkillFileName)), path = Path.Combine(globalSkillsDir, folder, SkillFileName) },
                local = new { installed = File.Exists(Path.Combine(localSkillsDir, folder, SkillFileName)), path = Path.Combine(localSkillsDir, folder, SkillFileName) }
            });
            Console.WriteLine(JsonHelper.Serialize(skills));
        }
        else
        {
            Console.WriteLine("Claude Code Skill Status:");

            var anyInstalled = false;
            foreach (var folder in allFolders)
            {
                var globalPath = Path.Combine(globalSkillsDir, folder, SkillFileName);
                var localPath = Path.Combine(localSkillsDir, folder, SkillFileName);
                var globalExists = File.Exists(globalPath);
                var localExists = File.Exists(localPath);
                if (globalExists || localExists) anyInstalled = true;

                Console.WriteLine();
                Console.WriteLine($"  {folder}:");
                Console.WriteLine($"    Global: {(globalExists ? "Yes" : "No")}");
                Console.WriteLine($"    Local:  {(localExists ? "Yes" : "No")}");
            }

            if (!anyInstalled)
            {
                Console.WriteLine();
                Console.WriteLine("Run 'unityctl skill add' to add skills locally,");
                Console.WriteLine("or 'unityctl skill add --global' to add globally.");
            }
        }
    }
}
