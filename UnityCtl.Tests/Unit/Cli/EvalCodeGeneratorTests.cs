using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class EvalCodeGeneratorTests
{
    [Fact]
    public void ExpressionMode_WrapsWithReturn()
    {
        var code = ScriptCommands.BuildEvalCode("Application.version", [], hasArgs: false);

        Assert.Contains("return Application.version;", code);
    }

    [Fact]
    public void BodyMode_UsesCodeAsIs()
    {
        var code = ScriptCommands.BuildEvalCode("var x = 1; return x;", [], hasArgs: false);

        Assert.Contains("var x = 1; return x;", code);
        Assert.DoesNotContain("return var x", code);
    }

    [Fact]
    public void DefaultUsings_AllPresent()
    {
        var code = ScriptCommands.BuildEvalCode("1", [], hasArgs: false);

        Assert.Contains("using System;", code);
        Assert.Contains("using System.Collections.Generic;", code);
        Assert.Contains("using System.Linq;", code);
        Assert.Contains("using UnityEngine;", code);
        Assert.Contains("using UnityEditor;", code);
    }

    [Fact]
    public void ExtraUsings_Added()
    {
        var code = ScriptCommands.BuildEvalCode("1", ["UnityEngine.UI"], hasArgs: false);

        Assert.Contains("using UnityEngine.UI;", code);
    }

    [Fact]
    public void DuplicateUsing_NotDuplicated()
    {
        var code = ScriptCommands.BuildEvalCode("1", ["System"], hasArgs: false);

        // Count occurrences of "using System;" (exact, not "using System.Collections")
        var lines = code.Split('\n');
        var count = lines.Count(l => l.Trim() == "using System;");
        Assert.Equal(1, count);
    }

    [Fact]
    public void WithArgs_UsesArgsSignature()
    {
        var code = ScriptCommands.BuildEvalCode("args[0]", [], hasArgs: true);

        Assert.Contains("public static object Main(string[] args)", code);
    }

    [Fact]
    public void WithoutArgs_UsesParameterlessSignature()
    {
        var code = ScriptCommands.BuildEvalCode("1", [], hasArgs: false);

        Assert.Contains("public static object Main()", code);
        Assert.DoesNotContain("string[] args", code);
    }

    [Fact]
    public void GeneratedCode_HasClassStructure()
    {
        var code = ScriptCommands.BuildEvalCode("Application.version", [], hasArgs: false);

        Assert.Contains("public class Script", code);
        Assert.Contains("public static object Main()", code);
    }

    [Fact]
    public void ExpressionWithSemicolonInString_TreatedAsExpression()
    {
        var code = ScriptCommands.BuildEvalCode("\"hello; world\".Length", [], hasArgs: false);

        Assert.Contains("return \"hello; world\".Length;", code);
    }

    [Fact]
    public void ExpressionWithTrailingSemicolon_TreatedAsBody()
    {
        var code = ScriptCommands.BuildEvalCode("Debug.Log(\"test\");", [], hasArgs: false);

        Assert.Contains("Debug.Log(\"test\");", code);
        Assert.DoesNotContain("return Debug.Log", code);
    }
}
