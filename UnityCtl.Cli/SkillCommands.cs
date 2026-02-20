using System;
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

    public static Command CreateCommand()
    {
        var skillCommand = new Command("skill", "Claude Code skill management");

        skillCommand.AddCommand(CreateAddCommand());
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

            await AddSkillAsync(global, claudeDir, force, json);
        });

        return addCommand;
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

            RemoveSkill(global, claudeDir, json);
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
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream == null)
        {
            // Fallback: try to find SKILL.md in development environment
            var possiblePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".claude", "skills", "unity-editor", "SKILL.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".claude", "skills", "unity-editor", "SKILL.md"),
                Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills", "unity-editor", "SKILL.md")
            };

            foreach (var path in possiblePaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    return File.ReadAllText(fullPath);
                }
            }

            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Updates the skill file at the given path with the latest embedded content.
    /// Returns true if successful, false if embedded content was not found.
    /// </summary>
    public static async Task<bool> UpdateSkillFileAsync(string skillPath)
    {
        var content = GetEmbeddedSkillContent();
        if (content == null) return false;

        var dir = Path.GetDirectoryName(skillPath);
        if (dir != null) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(skillPath, content);
        return true;
    }

    public static async Task AddSkillAsync(bool global, string? claudeDir, bool force, bool json)
    {
        var skillsDir = GetSkillsDirectory(global, claudeDir);
        var skillFolderPath = Path.Combine(skillsDir, SkillFolderName);
        var skillPath = Path.Combine(skillFolderPath, SkillFileName);

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
            }
            else
            {
                Console.WriteLine($"Skill already exists at: {skillPath}");
                Console.Write("Overwrite? [y/N]: ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    Console.WriteLine("Aborted.");
                    return;
                }
            }
        }

        // Get skill content
        var skillContent = GetEmbeddedSkillContent();
        if (skillContent == null)
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
            return;
        }

        // Create directory if needed
        Directory.CreateDirectory(skillFolderPath);

        // Write skill file
        await File.WriteAllTextAsync(skillPath, skillContent);

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
            Console.WriteLine($"Installed Claude Code skill to: {skillPath}");
            Console.WriteLine();
            if (global)
            {
                Console.WriteLine("The skill is now available globally for all projects.");
            }
            else
            {
                Console.WriteLine("The skill is now available for this project.");
                Console.WriteLine("Use --global to install for all projects.");
            }
            Console.WriteLine();
            Console.WriteLine("Restart Claude Code to load the new skill.");
        }
    }

    private static void RemoveSkill(bool global, string? claudeDir, bool json)
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
            return;
        }

        File.Delete(skillPath);

        // Try to clean up empty directories
        try
        {
            if (Directory.Exists(skillFolderPath) && !Directory.EnumerateFileSystemEntries(skillFolderPath).Any())
            {
                Directory.Delete(skillFolderPath);
            }
            if (Directory.Exists(skillsDir) && !Directory.EnumerateFileSystemEntries(skillsDir).Any())
            {
                Directory.Delete(skillsDir);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new { success = true, path = skillPath }));
        }
        else
        {
            Console.WriteLine($"Removed skill from: {skillPath}");
        }
    }

    private static void ShowSkillStatus(bool json)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSkillsDir = Path.Combine(home, ".claude", "skills");
        var localSkillsDir = Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills");

        var globalPath = Path.Combine(globalSkillsDir, SkillFolderName, SkillFileName);
        var localPath = Path.Combine(localSkillsDir, SkillFolderName, SkillFileName);

        var globalExists = File.Exists(globalPath);
        var localExists = File.Exists(localPath);

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new
            {
                global = new { installed = globalExists, path = globalPath },
                local = new { installed = localExists, path = localPath }
            }));
        }
        else
        {
            Console.WriteLine("Claude Code Skill Status:");
            Console.WriteLine();
            Console.WriteLine($"Global ({globalPath}):");
            Console.WriteLine($"  Installed: {(globalExists ? "Yes" : "No")}");
            Console.WriteLine();
            Console.WriteLine($"Local ({localPath}):");
            Console.WriteLine($"  Installed: {(localExists ? "Yes" : "No")}");

            if (!globalExists && !localExists)
            {
                Console.WriteLine();
                Console.WriteLine("Run 'unityctl skill add' to add the skill locally,");
                Console.WriteLine("or 'unityctl skill add --global' to add globally.");
            }
        }
    }
}
