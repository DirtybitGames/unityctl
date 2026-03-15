using System;
using System.IO;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class PluginSkillGenerationTests : IDisposable
{
    private readonly string _tempDir;

    public PluginSkillGenerationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"unityctl-skill-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void GenerateSkillSection_WithCommands_ProducesMarkdown()
    {
        var plugin = new LoadedPlugin
        {
            Manifest = new PluginManifest
            {
                Name = "my-tool",
                Description = "A useful tool",
                Commands = new[]
                {
                    new PluginCommandDefinition
                    {
                        Name = "run",
                        Description = "Run the tool",
                        Arguments = new[]
                        {
                            new PluginArgumentDefinition { Name = "target", Required = true },
                            new PluginArgumentDefinition { Name = "extra", Required = false }
                        },
                        Options = new[]
                        {
                            new PluginOptionDefinition { Name = "verbose", Type = "bool", Description = "Verbose" },
                            new PluginOptionDefinition { Name = "format", Type = "string", Description = "Format" }
                        },
                        Handler = new PluginHandler { File = "run.cs" }
                    }
                }
            },
            Directory = _tempDir,
            Source = "project"
        };

        var section = PluginLoader.GenerateSkillSection(plugin);

        Assert.Contains("### Plugin: my-tool", section);
        Assert.Contains("A useful tool", section);
        Assert.Contains("`unityctl my-tool run <target> [extra] [--verbose] [--format <value>]`", section);
        Assert.Contains("Run the tool", section);
    }

    [Fact]
    public void GenerateSkillSection_NoDescription_OmitsDescriptionLine()
    {
        var plugin = new LoadedPlugin
        {
            Manifest = new PluginManifest
            {
                Name = "bare",
                Commands = new[]
                {
                    new PluginCommandDefinition
                    {
                        Name = "do",
                        Handler = new PluginHandler { File = "do.cs" }
                    }
                }
            },
            Directory = _tempDir,
            Source = "project"
        };

        var section = PluginLoader.GenerateSkillSection(plugin);

        Assert.Contains("### Plugin: bare", section);
        Assert.Contains("`unityctl bare do`", section);
    }

    [Fact]
    public void GenerateSkillSection_CustomSkillFile_ReadsFromFile()
    {
        var customContent = "### My Custom Skill Section\n\nCustom docs here.";
        File.WriteAllText(Path.Combine(_tempDir, "MY-SKILL.md"), customContent);

        var plugin = new LoadedPlugin
        {
            Manifest = new PluginManifest
            {
                Name = "custom",
                Skill = new PluginSkillSection { File = "MY-SKILL.md" },
                Commands = new[]
                {
                    new PluginCommandDefinition
                    {
                        Name = "unused",
                        Handler = new PluginHandler { File = "unused.cs" }
                    }
                }
            },
            Directory = _tempDir,
            Source = "project"
        };

        var section = PluginLoader.GenerateSkillSection(plugin);

        Assert.Equal(customContent, section);
    }

    [Fact]
    public void GenerateSkillSection_CustomSkillFile_TraversalPath_FallsBackToAutoGenerate()
    {
        // If the skill file path escapes the plugin dir, it should fall back to auto-generation
        var plugin = new LoadedPlugin
        {
            Manifest = new PluginManifest
            {
                Name = "sneaky",
                Description = "Auto-generated fallback",
                Skill = new PluginSkillSection { File = "../../etc/passwd" },
                Commands = new[]
                {
                    new PluginCommandDefinition
                    {
                        Name = "cmd",
                        Handler = new PluginHandler { File = "cmd.cs" }
                    }
                }
            },
            Directory = _tempDir,
            Source = "project"
        };

        var section = PluginLoader.GenerateSkillSection(plugin);

        // Should auto-generate, not read the traversal path
        Assert.Contains("### Plugin: sneaky", section);
        Assert.Contains("Auto-generated fallback", section);
    }

    [Fact]
    public void GenerateSkillSection_OptionWithDashPrefix_NotDoubled()
    {
        var plugin = new LoadedPlugin
        {
            Manifest = new PluginManifest
            {
                Name = "opt-test",
                Commands = new[]
                {
                    new PluginCommandDefinition
                    {
                        Name = "cmd",
                        Options = new[]
                        {
                            new PluginOptionDefinition { Name = "--already-prefixed", Type = "string" }
                        },
                        Handler = new PluginHandler { File = "cmd.cs" }
                    }
                }
            },
            Directory = _tempDir,
            Source = "project"
        };

        var section = PluginLoader.GenerateSkillSection(plugin);

        // Should not produce "----already-prefixed"
        Assert.Contains("[--already-prefixed <value>]", section);
        Assert.DoesNotContain("----", section);
    }

    [Fact]
    public void ExecutablePlugin_GetSkillSection_WithCompanionFile_ReadsFile()
    {
        var companionContent = "### Plugin: my-exec\n\nCustom executable docs.";
        File.WriteAllText(Path.Combine(_tempDir, "unityctl-my-exec.skill.md"), companionContent);

        var plugin = new ExecutablePlugin
        {
            Name = "my-exec",
            Path = Path.Combine(_tempDir, "unityctl-my-exec.exe"),
            Source = "project"
        };

        var section = ExecutablePluginLoader.GetSkillSection(plugin);

        Assert.Equal(companionContent, section);
    }

    [Fact]
    public void ExecutablePlugin_GetSkillSection_NoCompanionFile_AutoGenerates()
    {
        var plugin = new ExecutablePlugin
        {
            Name = "my-exec",
            Path = Path.Combine(_tempDir, "unityctl-my-exec.exe"),
            Source = "project",
            Description = "Does stuff"
        };

        var section = ExecutablePluginLoader.GetSkillSection(plugin);

        Assert.NotNull(section);
        Assert.Contains("### Plugin: my-exec (executable)", section!);
        Assert.Contains("Does stuff", section);
        Assert.Contains("`unityctl my-exec [args...]`", section);
    }
}
