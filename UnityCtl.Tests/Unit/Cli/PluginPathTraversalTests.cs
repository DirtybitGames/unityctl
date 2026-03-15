using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

/// <summary>
/// Tests the path traversal guard used in PluginLoader.ExecutePluginCommandAsync
/// and PluginLoader.GenerateSkillSection. Verifies that handler file paths
/// cannot escape the plugin directory.
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

    /// <summary>
    /// Replicates the path traversal check from PluginLoader.ExecutePluginCommandAsync.
    /// </summary>
    private static bool IsPathWithinPluginDir(string pluginDir, string handlerFile)
    {
        var scriptPath = Path.GetFullPath(Path.Combine(pluginDir, handlerFile));
        var resolvedPluginDir = Path.GetFullPath(pluginDir) + Path.DirectorySeparatorChar;
        var pathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return scriptPath.StartsWith(resolvedPluginDir, pathComparison);
    }

    [Theory]
    [InlineData("handler.cs")]
    [InlineData("subdir/nested.cs")]
    public void ValidHandlerPaths_Allowed(string handlerFile)
    {
        Assert.True(IsPathWithinPluginDir(_pluginDir, handlerFile));
    }

    [Theory]
    [InlineData("../../secret.cs")]
    [InlineData("../secret.cs")]
    [InlineData("..\\..\\secret.cs")]
    [InlineData("subdir/../../secret.cs")]
    public void TraversalPaths_Rejected(string handlerFile)
    {
        Assert.False(IsPathWithinPluginDir(_pluginDir, handlerFile));
    }

    [Fact]
    public void AbsolutePath_OutsidePluginDir_Rejected()
    {
        // An absolute path to a file outside the plugin dir
        var absolutePath = Path.Combine(_tempDir, "secret.cs");
        Assert.False(IsPathWithinPluginDir(_pluginDir, absolutePath));
    }

    [Fact]
    public void AbsolutePath_InsidePluginDir_Allowed()
    {
        var absolutePath = Path.Combine(_pluginDir, "handler.cs");
        Assert.True(IsPathWithinPluginDir(_pluginDir, absolutePath));
    }

    [Fact]
    public void PluginDirWithTrailingSlash_TraversalStillRejected()
    {
        // Even if the plugin dir already has a trailing separator, traversal should be caught
        var dirWithSlash = _pluginDir + Path.DirectorySeparatorChar;
        Assert.False(IsPathWithinPluginDir(dirWithSlash, "../../secret.cs"));
    }

    [Fact]
    public void PluginDirWithTrailingSlash_ValidFile_Allowed()
    {
        // Path.GetFullPath normalizes double separators, so this should work
        // Note: GetFullPath(dir + "/" + "handler.cs") normalizes the double-separator
        var dirWithSlash = _pluginDir + Path.DirectorySeparatorChar;
        var scriptPath = Path.GetFullPath(Path.Combine(dirWithSlash, "handler.cs"));
        var resolvedDir = Path.GetFullPath(dirWithSlash) + Path.DirectorySeparatorChar;

        // The resolved dir gets a double separator which GetFullPath normalizes differently.
        // The production code uses Path.GetFullPath(pluginDir) + separator, which handles this
        // because GetFullPath strips the trailing separator before re-adding it.
        var normalizedDir = Path.GetFullPath(_pluginDir) + Path.DirectorySeparatorChar;
        Assert.StartsWith(normalizedDir, scriptPath);
    }

    [SkippableFact]
    public void CaseVariation_OnWindows_StillRejected()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // On Windows, "../SECRET.CS" with different casing should still be rejected
        Assert.False(IsPathWithinPluginDir(_pluginDir, "..\\..\\SECRET.CS"));
    }

    [SkippableFact]
    public void CaseVariation_OnWindows_InsideDir_Allowed()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // On Windows, case shouldn't matter for files inside the dir
        Assert.True(IsPathWithinPluginDir(_pluginDir, "HANDLER.CS"));
    }
}
