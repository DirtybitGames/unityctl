using UnityCtl.Cli;
using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class VersionCheckTests
{
    private static HealthResult MakeHealth(string? bridgeVersion, string? pluginVersion) => new()
    {
        Status = "ok",
        ProjectId = "test",
        UnityConnected = true,
        EditorReady = true,
        BridgeVersion = bridgeVersion,
        UnityPluginVersion = pluginVersion
    };

    [Fact]
    public void AllVersionsMatch_NoMismatch()
    {
        var result = VersionCheck.Check(MakeHealth("1.0.0", "1.0.0"), "1.0.0");

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void PluginNull_NoMismatch()
    {
        var result = VersionCheck.Check(MakeHealth("1.0.0", null), "1.0.0");

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void PluginAhead_OfCli()
    {
        var result = VersionCheck.Check(MakeHealth("1.0.0", "1.1.0"), "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void PluginAhead_OfBridge()
    {
        var result = VersionCheck.Check(MakeHealth("0.9.0", "1.0.0"), "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void CliAndBridgeAhead_PluginNotAhead()
    {
        var result = VersionCheck.Check(MakeHealth("2.0.0", "1.0.0"), "2.0.0");

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void BuildMetadata_StrippedBeforeComparison()
    {
        var result = VersionCheck.Check(MakeHealth("1.0.0+abc", "1.0.0+def"), "1.0.0+ghi");

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void PreReleaseSuffix_StrippedForVersionComparison()
    {
        // Plugin 1.1.0-beta is ahead of CLI 1.0.0
        var result = VersionCheck.Check(MakeHealth("1.0.0", "1.1.0-beta"), "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void UnparseablePluginVersion_NoPluginAhead()
    {
        var result = VersionCheck.Check(MakeHealth("1.0.0", "garbage"), "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void UnparseableCliVersion_StillChecksAgainstBridge()
    {
        // CLI version unparseable, but plugin 2.0.0 > bridge 1.0.0 → PluginAhead
        var result = VersionCheck.Check(MakeHealth("1.0.0", "2.0.0"), "not-a-version");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void UnparseableCliVersion_PluginMatchesBridge_NoPluginAhead()
    {
        var result = VersionCheck.Check(MakeHealth("1.0.0", "1.0.0"), "not-a-version");

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void BridgeMismatch_PluginNull_Detected()
    {
        var result = VersionCheck.Check(MakeHealth("1.1.0", null), "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void ResultContainsBaseVersions()
    {
        var result = VersionCheck.Check(MakeHealth("1.0.0+build1", "1.2.0+build2"), "1.1.0+build3");

        Assert.Equal("1.1.0", result.CliVersion);
        Assert.Equal("1.0.0", result.BridgeVersion);
        Assert.Equal("1.2.0", result.PluginVersion);
    }
}
