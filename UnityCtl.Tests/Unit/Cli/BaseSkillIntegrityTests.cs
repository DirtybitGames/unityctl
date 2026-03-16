using System.IO;
using System.Reflection;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

/// <summary>
/// Verifies that the base SKILL.md files (embedded as resources and committed to the repo)
/// are clean base versions without composed plugin sections. This prevents accidentally
/// committing a composed SKILL.md (from 'skill rebuild') as the source of truth.
/// </summary>
public class BaseSkillIntegrityTests
{
    private const string PluginSectionMarker = "## Plugin Commands";

    [Fact]
    public void EmbeddedSkillMd_DoesNotContainPluginSections()
    {
        var assembly = typeof(UnityCtl.Cli.SkillCommands).Assembly;
        using var stream = assembly.GetManifestResourceStream("UnityCtl.Cli.Resources.SKILL.md");

        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();

        Assert.DoesNotContain(PluginSectionMarker, content);
    }

    [Fact]
    public void EmbeddedSkillMd_HasFrontmatter()
    {
        var assembly = typeof(UnityCtl.Cli.SkillCommands).Assembly;
        using var stream = assembly.GetManifestResourceStream("UnityCtl.Cli.Resources.SKILL.md");

        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();

        Assert.StartsWith("---", content);
        Assert.Contains("name: unity-editor", content);
    }

    [Fact]
    public void EmbeddedPluginSkillMd_Exists()
    {
        var assembly = typeof(UnityCtl.Cli.SkillCommands).Assembly;
        using var stream = assembly.GetManifestResourceStream("UnityCtl.Cli.Resources.SKILL.plugins.md");

        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();

        Assert.Contains("unityctl-plugins", content);
    }

    [Fact]
    public void SourceSkillMd_OnDisk_DoesNotContainPluginSections()
    {
        // The base skill source lives in UnityCtl.Cli/Resources/SKILL.md
        var assemblyDir = Path.GetDirectoryName(typeof(BaseSkillIntegrityTests).Assembly.Location)!;

        // Walk up to find repo root (look for .git)
        var dir = new DirectoryInfo(assemblyDir);
        string? repoRoot = null;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        // Skip if we can't find the repo root (e.g., running from a package)
        Skip.If(repoRoot == null, "Could not find repo root");

        var skillPath = Path.Combine(repoRoot!, "UnityCtl.Cli", "Resources", "SKILL.md");
        Skip.IfNot(File.Exists(skillPath), $"SKILL.md not found at {skillPath}");

        var content = File.ReadAllText(skillPath);

        Assert.DoesNotContain(PluginSectionMarker, content);
    }
}
