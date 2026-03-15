using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

/// <summary>
/// Tests executable plugin discovery from directories using the actual ScanDirectoryForExecutables method.
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
        // Create test files
        CreateExecutable("unityctl-foo");
        CreateExecutable("unityctl-bar.exe");
        CreateExecutable("unityctl-baz.cmd");
        CreateExecutable("unityctl-qux.bat");
        CreateExecutable("unityctl-run.sh");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();

        Assert.Contains(plugins, p => p.Name == "foo");
        Assert.Contains(plugins, p => p.Name == "bar");
        Assert.Contains(plugins, p => p.Name == "baz");
        Assert.Contains(plugins, p => p.Name == "qux");
        Assert.Contains(plugins, p => p.Name == "run");
    }

    [Fact]
    public void CompanionSkillFile_Ignored()
    {
        // .skill.md companion files should not be discovered as executable plugins
        File.WriteAllText(Path.Combine(_tempDir, "unityctl-foo.skill.md"), "docs");
        File.WriteAllText(Path.Combine(_tempDir, "unityctl-bar.skill.md"), "docs");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();
        Assert.Empty(plugins);
    }

    [Fact]
    public void Extensionless_ExtractsName()
    {
        CreateExecutable("unityctl-my-tool");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();
        Assert.Single(plugins);
        Assert.Equal("my-tool", plugins[0].Name);
    }

    [Fact]
    public void EmptyPrefix_Skipped()
    {
        // "unityctl-.exe" should produce an empty name and be skipped
        CreateExecutable("unityctl-.exe");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();
        Assert.Empty(plugins);
    }

    [Fact]
    public void NameExtraction_Lowercased()
    {
        CreateExecutable("unityctl-MyPlugin");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();
        Assert.Single(plugins);
        Assert.Equal("myplugin", plugins[0].Name);
    }

    [Fact]
    public void NonMatchingFiles_Ignored()
    {
        // Files that don't start with "unityctl-" should not be discovered
        File.WriteAllText(Path.Combine(_tempDir, "other-tool.exe"), "fake");
        File.WriteAllText(Path.Combine(_tempDir, "unityctl.exe"), "fake"); // no hyphen after

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();
        Assert.Empty(plugins);
    }

    [Fact]
    public void UnixNameExtraction_StripsExtension()
    {
        CreateExecutable("unityctl-foo.sh");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();
        Assert.Single(plugins);
        Assert.Equal("foo", plugins[0].Name);
    }

    [Fact]
    public void Source_IsPassedThrough()
    {
        CreateExecutable("unityctl-test-tool");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();
        Assert.Single(plugins);
        Assert.Equal("project", plugins[0].Source);
    }

    [Fact]
    public void Path_IsFullFilePath()
    {
        CreateExecutable("unityctl-check");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "test").ToList();
        Assert.Single(plugins);
        Assert.StartsWith(_tempDir, plugins[0].Path);
    }

    private void CreateExecutable(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, "fake");

        // On Unix, set the executable permission
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            System.Diagnostics.Process.Start("chmod", ["+x", path])?.WaitForExit();
        }
    }
}
