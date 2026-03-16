using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

/// <summary>
/// Tests executable plugin discovery by calling ExecutablePluginLoader.ScanDirectoryForExecutables
/// against temp directories with real files.
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

    [SkippableFact]
    public void Discovers_Extensionless_Executable()
    {
        CreateExecutable("unityctl-foo");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Single(plugins);
        Assert.Equal("foo", plugins[0].Name);
    }

    [SkippableFact]
    public void Discovers_Exe_Extension()
    {
        CreateExecutable("unityctl-bar.exe");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Single(plugins);
        Assert.Equal("bar", plugins[0].Name);
    }

    [SkippableFact]
    public void Discovers_Cmd_Extension()
    {
        CreateExecutable("unityctl-baz.cmd");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Single(plugins);
        Assert.Equal("baz", plugins[0].Name);
    }

    [Fact]
    public void CompanionSkillFile_NotDiscovered()
    {
        File.WriteAllText(Path.Combine(_tempDir, "unityctl-foo.skill.md"), "docs");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Empty(plugins);
    }

    [SkippableFact]
    public void EmptyNameAfterPrefix_Skipped()
    {
        CreateExecutable("unityctl-.exe");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Empty(plugins);
    }

    [SkippableFact]
    public void Name_IsLowercased()
    {
        CreateExecutable("unityctl-MyPlugin");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Single(plugins);
        Assert.Equal("myplugin", plugins[0].Name);
    }

    [Fact]
    public void NonMatchingFiles_Ignored()
    {
        File.WriteAllText(Path.Combine(_tempDir, "other-tool.exe"), "fake");
        File.WriteAllText(Path.Combine(_tempDir, "unityctl.exe"), "fake"); // no hyphen after prefix

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Empty(plugins);
    }

    [SkippableFact]
    public void Source_IsPassedThrough()
    {
        CreateExecutable("unityctl-test-tool");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Single(plugins);
        Assert.Equal("project", plugins[0].Source);
    }

    [SkippableFact]
    public void Path_IsFullFilePath()
    {
        CreateExecutable("unityctl-check");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Single(plugins);
        Assert.StartsWith(_tempDir, plugins[0].Path);
    }

    [SkippableFact]
    public void MultipleExecutables_AllDiscovered()
    {
        CreateExecutable("unityctl-alpha.exe");
        CreateExecutable("unityctl-beta.cmd");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Equal(2, plugins.Count);
        Assert.Contains(plugins, p => p.Name == "alpha");
        Assert.Contains(plugins, p => p.Name == "beta");
    }

    [Fact]
    public void EmptyDirectory_ReturnsEmpty()
    {
        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(_tempDir, "project").ToList();

        Assert.Empty(plugins);
    }

    [Fact]
    public void NonexistentDirectory_ReturnsEmpty()
    {
        var nonexistent = Path.Combine(_tempDir, "does-not-exist");

        var plugins = ExecutablePluginLoader.ScanDirectoryForExecutables(nonexistent, "project").ToList();

        Assert.Empty(plugins);
    }

    [Fact]
    public void ReadShebang_EnvBash_ReturnsBash()
    {
        var path = CreateScript("#!/usr/bin/env bash\necho hello");
        Assert.Equal("bash", ExecutablePluginLoader.ReadShebangInterpreter(path));
    }

    [Fact]
    public void ReadShebang_AbsolutePath_ReturnsFileName()
    {
        var path = CreateScript("#!/bin/bash\necho hello");
        Assert.Equal("bash", ExecutablePluginLoader.ReadShebangInterpreter(path));
    }

    [Fact]
    public void ReadShebang_EnvPython_ReturnsPython()
    {
        var path = CreateScript("#!/usr/bin/env python3\nimport sys");
        Assert.Equal("python3", ExecutablePluginLoader.ReadShebangInterpreter(path));
    }

    [Fact]
    public void ReadShebang_NoShebang_ReturnsNull()
    {
        var path = CreateScript("echo hello\n");
        Assert.Null(ExecutablePluginLoader.ReadShebangInterpreter(path));
    }

    [Fact]
    public void ReadShebang_EmptyFile_ReturnsNull()
    {
        var path = CreateScript("");
        Assert.Null(ExecutablePluginLoader.ReadShebangInterpreter(path));
    }

    [Fact]
    public void ReadShebang_NonexistentFile_ReturnsNull()
    {
        Assert.Null(ExecutablePluginLoader.ReadShebangInterpreter(Path.Combine(_tempDir, "nope")));
    }

    private string CreateScript(string content)
    {
        var path = Path.Combine(_tempDir, $"script-{Guid.NewGuid():N}");
        File.WriteAllText(path, content);
        return path;
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
