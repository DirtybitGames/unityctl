using System.IO;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class ScriptExecutePositionalFileTests
{
    [Fact]
    public void CsFileArg_ResolvedAsFile()
    {
        var (file, args) = ScriptCommands.ResolvePositionalFile(null, ["foo.cs"]);

        Assert.NotNull(file);
        Assert.EndsWith("foo.cs", file!.Name);
        Assert.Empty(args);
    }

    [Fact]
    public void CsFileArg_WithScriptArgs_SplitsCorrectly()
    {
        var (file, args) = ScriptCommands.ResolvePositionalFile(null, ["foo.cs", "arg1", "arg2"]);

        Assert.NotNull(file);
        Assert.EndsWith("foo.cs", file!.Name);
        Assert.Equal(["arg1", "arg2"], args);
    }

    [Fact]
    public void CsFileArg_CaseInsensitive()
    {
        var (file, _) = ScriptCommands.ResolvePositionalFile(null, ["Script.CS"]);

        Assert.NotNull(file);
        Assert.EndsWith("Script.CS", file!.Name);
    }

    [Fact]
    public void NonCsFirstArg_NotTreatedAsFile()
    {
        var (file, args) = ScriptCommands.ResolvePositionalFile(null, ["arg1", "arg2"]);

        Assert.Null(file);
        Assert.Equal(["arg1", "arg2"], args);
    }

    [Fact]
    public void ExplicitFileOption_NotOverriddenByPositionalArg()
    {
        var explicitFile = new FileInfo("explicit.cs");
        var (file, args) = ScriptCommands.ResolvePositionalFile(explicitFile, ["positional.cs", "arg1"]);

        Assert.Equal("explicit.cs", file!.Name);
        Assert.Equal(["positional.cs", "arg1"], args);
    }

    [Fact]
    public void EmptyArgs_ReturnsNull()
    {
        var (file, args) = ScriptCommands.ResolvePositionalFile(null, []);

        Assert.Null(file);
        Assert.Empty(args);
    }

    [Fact]
    public void PathWithDirectories_ResolvedAsFile()
    {
        var (file, args) = ScriptCommands.ResolvePositionalFile(null, ["/tmp/scripts/MyScript.cs", "arg1"]);

        Assert.NotNull(file);
        Assert.Equal("MyScript.cs", file!.Name);
        Assert.Equal(["arg1"], args);
    }
}
