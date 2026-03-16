using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

/// <summary>
/// Tests PluginLoader.IsPathWithin, which guards against path traversal
/// in plugin handler file references.
/// </summary>
public class PluginPathTraversalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _pluginDir;

    public PluginPathTraversalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"unityctl-test-{Guid.NewGuid():N}");
        _pluginDir = Path.Combine(_tempDir, "plugins", "my-plugin");
        Directory.CreateDirectory(_pluginDir);

        // Create a handler file inside the plugin directory
        File.WriteAllText(Path.Combine(_pluginDir, "handler.cs"), "// test");
        Directory.CreateDirectory(Path.Combine(_pluginDir, "subdir"));
        File.WriteAllText(Path.Combine(_pluginDir, "subdir", "nested.cs"), "// test");

        // Create a file outside the plugin directory (the attacker's target)
        File.WriteAllText(Path.Combine(_tempDir, "secret.cs"), "// secret");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("handler.cs")]
    [InlineData("subdir/nested.cs")]
    public void ValidHandlerPaths_Allowed(string handlerFile)
    {
        Assert.True(PluginLoader.IsPathWithin(_pluginDir, handlerFile));
    }

    [Theory]
    [InlineData("../../secret.cs")]
    [InlineData("../secret.cs")]
    [InlineData("subdir/../../secret.cs")]
    public void TraversalPaths_Rejected(string handlerFile)
    {
        Assert.False(PluginLoader.IsPathWithin(_pluginDir, handlerFile));
    }

    [SkippableFact]
    public void BackslashTraversal_OnWindows_Rejected()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        Assert.False(PluginLoader.IsPathWithin(_pluginDir, "..\\..\\secret.cs"));
    }

    [Fact]
    public void AbsolutePath_OutsidePluginDir_Rejected()
    {
        var absolutePath = Path.Combine(_tempDir, "secret.cs");
        Assert.False(PluginLoader.IsPathWithin(_pluginDir, absolutePath));
    }

    [Fact]
    public void AbsolutePath_InsidePluginDir_Allowed()
    {
        var absolutePath = Path.Combine(_pluginDir, "handler.cs");
        Assert.True(PluginLoader.IsPathWithin(_pluginDir, absolutePath));
    }

    [Fact]
    public void PluginDirWithTrailingSlash_TraversalStillRejected()
    {
        var dirWithSlash = _pluginDir + Path.DirectorySeparatorChar;
        Assert.False(PluginLoader.IsPathWithin(dirWithSlash, "../../secret.cs"));
    }

    [Fact]
    public void PluginDirWithTrailingSlash_ValidFile_Allowed()
    {
        var dirWithSlash = _pluginDir + Path.DirectorySeparatorChar;
        Assert.True(PluginLoader.IsPathWithin(dirWithSlash, "handler.cs"));
    }

    [SkippableFact]
    public void CaseVariation_OnWindows_StillRejected()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        Assert.False(PluginLoader.IsPathWithin(_pluginDir, "..\\..\\SECRET.CS"));
    }

    [SkippableFact]
    public void CaseVariation_OnWindows_InsideDir_Allowed()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        Assert.True(PluginLoader.IsPathWithin(_pluginDir, "HANDLER.CS"));
    }
}
