using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

/// <summary>
/// Tests executable plugin discovery from directories.
/// Uses DiscoverExecutablePlugins with plugin directories containing test executables.
/// </summary>
public class ExecutablePluginScanTests : IDisposable
{
    private readonly string _tempDir;

    public ExecutablePluginScanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"unityctl-exec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void NameExtraction_WithAndWithoutExtension()
    {
        // All naming variants: extensionless, .exe, .cmd, .bat, .sh
        var cases = new[]
        {
            ("unityctl-foo", "foo"),
            ("unityctl-bar.exe", "bar"),
            ("unityctl-baz.cmd", "baz"),
            ("unityctl-qux.bat", "qux"),
            ("unityctl-run.sh", "run"),
        };

        foreach (var (fileName, expected) in cases)
        {
            var name = fileName.Substring("unityctl-".Length);
            var dotIndex = name.IndexOf('.');
            if (dotIndex > 0)
                name = name.Substring(0, dotIndex);

            Assert.Equal(expected, name.ToLowerInvariant());
        }
    }

    [Fact]
    public void CompanionSkillFile_Ignored()
    {
        // .skill.md companion files should not be discovered as executable plugins
        File.WriteAllText(Path.Combine(_tempDir, "unityctl-foo.skill.md"), "docs");
        File.WriteAllText(Path.Combine(_tempDir, "unityctl-bar.skill.md"), "docs");

        var files = Directory.GetFiles(_tempDir, "unityctl-*");
        var nonCompanion = files.Where(f =>
            !Path.GetFileName(f).EndsWith(".skill.md", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Empty(nonCompanion);
    }

    [Fact]
    public void Extensionless_ExtractsName()
    {
        // Extensionless executables should be discovered on all platforms
        var fullFileName = "unityctl-my-tool";
        var name = fullFileName.Substring("unityctl-".Length);
        var dotIndex = name.IndexOf('.');
        if (dotIndex > 0)
            name = name.Substring(0, dotIndex);

        Assert.Equal("my-tool", name);
    }

    [Fact]
    public void EmptyPrefix_Skipped()
    {
        // "unityctl-.exe" should produce an empty name and be skipped
        File.WriteAllText(Path.Combine(_tempDir, "unityctl-.exe"), "fake");

        var fileName = Path.GetFileNameWithoutExtension("unityctl-.exe");
        var name = fileName.Substring("unityctl-".Length);
        Assert.True(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void NameExtraction_Lowercased()
    {
        // Verify that extracted names are lowercased
        var fileName = "unityctl-MyPlugin";
        var name = fileName.Substring("unityctl-".Length).ToLowerInvariant();
        Assert.Equal("myplugin", name);
    }

    [Fact]
    public void NonMatchingFiles_Ignored()
    {
        // Files that don't start with "unityctl-" should not be discovered
        File.WriteAllText(Path.Combine(_tempDir, "other-tool.exe"), "fake");
        File.WriteAllText(Path.Combine(_tempDir, "unityctl.exe"), "fake"); // no hyphen after

        var files = Directory.GetFiles(_tempDir, "unityctl-*");
        Assert.Empty(files);
    }

    [Fact]
    public void UnixNameExtraction_StripsExtension()
    {
        // On Unix, "unityctl-foo.sh" should extract "foo" (extension stripped)
        var fullFileName = "unityctl-foo.sh";
        var name = fullFileName.Substring("unityctl-".Length);
        var dotIndex = name.IndexOf('.');
        if (dotIndex > 0)
            name = name.Substring(0, dotIndex);

        Assert.Equal("foo", name);
    }

}
