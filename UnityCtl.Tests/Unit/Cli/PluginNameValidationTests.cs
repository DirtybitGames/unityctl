using System.Text.RegularExpressions;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

/// <summary>
/// Tests the plugin name validation regex used by 'plugin create'.
/// </summary>
public class PluginNameValidationTests
{
    private static readonly Regex ValidPluginName = PluginCommands.ValidPluginName;

    [Theory]
    [InlineData("foo")]
    [InlineData("my-tool")]
    [InlineData("a")]
    [InlineData("a1")]
    [InlineData("1a")]
    [InlineData("my-long-plugin-name")]
    [InlineData("a-b-c")]
    [InlineData("tool2")]
    [InlineData("123")]
    public void ValidNames_Match(string name)
    {
        Assert.True(ValidPluginName.IsMatch(name), $"Expected '{name}' to be valid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("-foo")]           // leading hyphen
    [InlineData("foo-")]           // trailing hyphen
    [InlineData("-")]              // just a hyphen
    [InlineData("Foo")]            // uppercase
    [InlineData("My-Tool")]        // mixed case
    [InlineData("foo bar")]        // space
    [InlineData("foo_bar")]        // underscore
    [InlineData("foo.bar")]        // dot
    [InlineData("foo/bar")]        // slash
    [InlineData("foo\\bar")]       // backslash
    public void InvalidNames_DoNotMatch(string name)
    {
        Assert.False(ValidPluginName.IsMatch(name), $"Expected '{name}' to be invalid");
    }

    [Fact]
    public void SingleCharacterName_Valid()
    {
        Assert.Matches(ValidPluginName, "x");
        Assert.Matches(ValidPluginName, "0");
    }

    [Fact]
    public void TwoCharacterName_Valid()
    {
        Assert.Matches(ValidPluginName, "ab");
        Assert.Matches(ValidPluginName, "a1");
    }
}
