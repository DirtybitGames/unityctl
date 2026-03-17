using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class VersionCheckTests
{
    [Fact]
    public void AllVersionsMatch_NoMismatch()
    {
        var result = VersionCheck.Check("1.0.0", "1.0.0", "1.0.0");

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void PluginNull_NoMismatch()
    {
        var result = VersionCheck.Check("1.0.0", "1.0.0", null);

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void PluginAhead_OfCli()
    {
        var result = VersionCheck.Check("1.0.0", "1.0.0", "1.1.0");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void PluginAhead_OfBridge()
    {
        var result = VersionCheck.Check("1.0.0", "0.9.0", "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void CliAndBridgeAhead_PluginNotAhead()
    {
        var result = VersionCheck.Check("2.0.0", "2.0.0", "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void BuildMetadata_StrippedBeforeComparison()
    {
        var result = VersionCheck.Check("1.0.0+ghi", "1.0.0+abc", "1.0.0+def");

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void PreReleaseSuffix_StrippedForVersionComparison()
    {
        var result = VersionCheck.Check("1.0.0", "1.0.0", "1.1.0-beta");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void UnparseablePluginVersion_NoPluginAhead()
    {
        var result = VersionCheck.Check("1.0.0", "1.0.0", "garbage");

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void UnparseableCliVersion_StillChecksAgainstBridge()
    {
        var result = VersionCheck.Check("not-a-version", "1.0.0", "2.0.0");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void UnparseableCliVersion_PluginMatchesBridge_NoPluginAhead()
    {
        var result = VersionCheck.Check("not-a-version", "1.0.0", "1.0.0");

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void BridgeMismatch_PluginNull_Detected()
    {
        var result = VersionCheck.Check("1.0.0", "1.1.0", null);

        Assert.True(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void ResultContainsBaseVersions()
    {
        var result = VersionCheck.Check("1.1.0+build3", "1.0.0+build1", "1.2.0+build2");

        Assert.Equal("1.1.0", result.CliVersion);
        Assert.Equal("1.0.0", result.BridgeVersion);
        Assert.Equal("1.2.0", result.PluginVersion);
    }

    [Fact]
    public void BridgeNull_PluginAhead_OfCli()
    {
        var result = VersionCheck.Check("1.0.0", null, "1.1.0");

        Assert.True(result.HasMismatch);
        Assert.True(result.PluginAhead);
    }

    [Fact]
    public void BridgeNull_PluginMatchesCli_NoMismatch()
    {
        var result = VersionCheck.Check("1.0.0", null, "1.0.0");

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }

    [Fact]
    public void BridgeAndPluginNull_NoMismatch()
    {
        var result = VersionCheck.Check("1.0.0", null, null);

        Assert.False(result.HasMismatch);
        Assert.False(result.PluginAhead);
    }
}
