using System;
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

    [Fact]
    public void InstanceId_SingleId_InjectsTarget()
    {
        var code = ScriptCommands.BuildEvalCode("target.name", [], hasArgs: false, instanceIds: "14200");

        Assert.Contains("InstanceIDToObject(14200)", code);
        Assert.Contains("var target =", code);
    }

    [Fact]
    public void InstanceId_MultipleIds_InjectsTargetsArray()
    {
        var code = ScriptCommands.BuildEvalCode("targets[0].name", [], hasArgs: false, instanceIds: "14200,14210");

        Assert.Contains("var targets = new GameObject[2]", code);
        Assert.Contains("InstanceIDToObject(14200)", code);
        Assert.Contains("InstanceIDToObject(14210)", code);
    }

    [Fact]
    public void InstanceId_NegativeId_Accepted()
    {
        var code = ScriptCommands.BuildEvalCode("target.name", [], hasArgs: false, instanceIds: "-1290");

        Assert.Contains("InstanceIDToObject(-1290)", code);
    }

    [Fact]
    public void ParseInstanceIds_InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScriptCommands.ParseInstanceIds("abc"));
        Assert.Throws<ArgumentException>(() => ScriptCommands.ParseInstanceIds("123,abc"));
        Assert.Throws<ArgumentException>(() => ScriptCommands.ParseInstanceIds("1; malicious code"));
    }

    [Fact]
    public void ParseInstanceIds_ValidInput_ReturnsInts()
    {
        var ids = ScriptCommands.ParseInstanceIds("14200,-1290,0");

        Assert.Equal([14200, -1290, 0], ids);
    }

    [Fact]
    public void ExtraUsings_CommaSeparated_SplitIntoMultiple()
    {
        var code = ScriptCommands.BuildEvalCode("1", ["UnityEngine.UI,UnityEngine.SceneManagement"], hasArgs: false);

        Assert.Contains("using UnityEngine.UI;", code);
        Assert.Contains("using UnityEngine.SceneManagement;", code);
    }

    [Fact]
    public void ExtraUsings_CommaSeparatedWithSpaces_Trimmed()
    {
        var code = ScriptCommands.BuildEvalCode("1", ["UnityEngine.UI , UnityEngine.SceneManagement"], hasArgs: false);

        Assert.Contains("using UnityEngine.UI;", code);
        Assert.Contains("using UnityEngine.SceneManagement;", code);
    }

    [Fact]
    public void ExtraUsings_MultipleFlags_AllAdded()
    {
        var code = ScriptCommands.BuildEvalCode("1", ["UnityEngine.UI", "UnityEngine.SceneManagement"], hasArgs: false);

        Assert.Contains("using UnityEngine.UI;", code);
        Assert.Contains("using UnityEngine.SceneManagement;", code);
    }

    [Fact]
    public void ExtraUsings_CommaSeparatedWithDuplicate_Deduplicated()
    {
        var code = ScriptCommands.BuildEvalCode("1", ["System,UnityEngine.UI"], hasArgs: false);

        Assert.Contains("using UnityEngine.UI;", code);
        var lines = code.Split('\n');
        var count = lines.Count(l => l.Trim() == "using System;");
        Assert.Equal(1, count);
    }

    [Fact]
    public void ObjectAlias_EmittedToDisambiguate()
    {
        // Without this alias, bare `Object` is ambiguous between UnityEngine.Object
        // and System.Object because both `using System;` and `using UnityEngine;` are
        // in the default set. See log-analysis for recurring CS0104 failures.
        var code = ScriptCommands.BuildEvalCode("Object.FindFirstObjectByType<Camera>()", [], hasArgs: false);

        Assert.Contains("using Object = UnityEngine.Object;", code);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ContainsCodeSemicolon — string-literal-aware `;` detector
    // The eval wrapper uses this to decide between expression mode
    // (wrap as `return <expr>;`) and body mode (inline as statements).
    // Getting this wrong produces compile errors like `return return ...;`
    // or double-wraps multi-statement bodies. Cover every C# literal
    // shape the agent is likely to write in an eval expression.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ContainsCodeSemicolon_NoSemicolon_False()
    {
        Assert.False(ScriptCommands.ContainsCodeSemicolon("Application.version"));
        Assert.False(ScriptCommands.ContainsCodeSemicolon("x + y * z"));
        Assert.False(ScriptCommands.ContainsCodeSemicolon(""));
    }

    [Fact]
    public void ContainsCodeSemicolon_BareSemicolon_True()
    {
        Assert.True(ScriptCommands.ContainsCodeSemicolon("var x = 1; return x"));
        Assert.True(ScriptCommands.ContainsCodeSemicolon("Debug.Log(42);"));
    }

    [Fact]
    public void ContainsCodeSemicolon_InStringLiteral_False()
    {
        Assert.False(ScriptCommands.ContainsCodeSemicolon("\"hello; world\".Length"));
        Assert.False(ScriptCommands.ContainsCodeSemicolon("\"a;b;c;d\""));
    }

    [Fact]
    public void ContainsCodeSemicolon_OutsideStringButStringHasSemi_True()
    {
        // String has `;` but there's also a real `;` outside.
        Assert.True(ScriptCommands.ContainsCodeSemicolon("var s = \"a;b\"; return s"));
    }

    [Fact]
    public void ContainsCodeSemicolon_EscapedQuoteDoesNotEndString()
    {
        // `"a\";b"` — escaped quote, string continues, semicolon inside.
        // The only way we'd think the string ended and the `;` is in code
        // is if we mis-handle the backslash escape.
        Assert.False(ScriptCommands.ContainsCodeSemicolon("\"a\\\";b\".Length"));
    }

    [Fact]
    public void ContainsCodeSemicolon_BackslashBackslashClosesString()
    {
        // `"a\\"; x` — first `\\` is escaped backslash, then the quote
        // closes the string. `;` after is a real code semicolon.
        Assert.True(ScriptCommands.ContainsCodeSemicolon("\"a\\\\\"; x"));
    }

    [Fact]
    public void ContainsCodeSemicolon_InVerbatimString_False()
    {
        // @"...;..." — verbatim string, semicolon is literal.
        Assert.False(ScriptCommands.ContainsCodeSemicolon("@\"line1;line2\""));
    }

    [Fact]
    public void ContainsCodeSemicolon_VerbatimStringEscapedQuote_False()
    {
        // @"a""b;c" — `""` is an escaped quote in verbatim form, string continues,
        // `;` is still inside.
        Assert.False(ScriptCommands.ContainsCodeSemicolon("@\"a\"\"b;c\""));
    }

    [Fact]
    public void ContainsCodeSemicolon_InInterpolatedString_False()
    {
        // $"foo;bar" — interpolated but no {…} with code. Treated as string.
        Assert.False(ScriptCommands.ContainsCodeSemicolon("$\"foo;bar\""));
    }

    [Fact]
    public void ContainsCodeSemicolon_InInterpolatedVerbatim_False()
    {
        Assert.False(ScriptCommands.ContainsCodeSemicolon("$@\"line;one\""));
    }

    [Fact]
    public void ContainsCodeSemicolon_InCharLiteral_False()
    {
        Assert.False(ScriptCommands.ContainsCodeSemicolon("';'"));
    }

    [Fact]
    public void ContainsCodeSemicolon_EscapedCharLiteral_False()
    {
        // `'\''` is a four-char C# char literal for a single quote.
        // The escape must not prematurely close the char state.
        Assert.False(ScriptCommands.ContainsCodeSemicolon(@"'\''"));
    }

    [Fact]
    public void ContainsCodeSemicolon_EscapedCharLiteralThenRealSemi_True()
    {
        // `'\''; x` — char literal, then real `;` in code context.
        Assert.True(ScriptCommands.ContainsCodeSemicolon(@"'\''; x"));
    }

    [Fact]
    public void ContainsCodeSemicolon_InLineComment_False()
    {
        Assert.False(ScriptCommands.ContainsCodeSemicolon("x // y;z"));
    }

    [Fact]
    public void ContainsCodeSemicolon_LineCommentEndsAtNewline()
    {
        // `// y` then newline, then `;` is real code.
        Assert.True(ScriptCommands.ContainsCodeSemicolon("x // comment\n var q = 1;"));
    }

    [Fact]
    public void ContainsCodeSemicolon_MixedLiteralsOneRealSemi_True()
    {
        // Two literals, then a real statement terminator.
        Assert.True(ScriptCommands.ContainsCodeSemicolon("var s = \"a;b\" + @\"c;d\" + $\"e;f\"; return s"));
    }

    [Fact]
    public void ContainsCodeSemicolon_UnclosedString_DoesNotCrash()
    {
        // An unclosed string has a `;` after — but we're in string mode so
        // we won't see it. Just checking we don't crash.
        var result = ScriptCommands.ContainsCodeSemicolon("\"unclosed; still in string");
        Assert.False(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // BuildEvalBody — the high-level wrap decision
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildEvalBody_SingleExpression_WrapsWithReturn()
    {
        var body = ScriptCommands.BuildEvalBody("Application.version");
        Assert.Equal("return Application.version;", body);
    }

    [Fact]
    public void BuildEvalBody_TrailingSemiBody_InlinesAsIs()
    {
        var body = ScriptCommands.BuildEvalBody("var x = 1; return x;");
        Assert.Equal("var x = 1; return x;", body);
    }

    [Fact]
    public void BuildEvalBody_MultiStatementWithoutTrailingSemi_AddsSemi()
    {
        // Historical #40: agent wrote var/try-catch with no trailing `;`.
        // Old wrapper wrapped the whole thing in `return ...;` which is
        // nonsensical. New wrapper: semicolons present → body mode, append `;`.
        var expr = "var x = 1; return x";
        var body = ScriptCommands.BuildEvalBody(expr);
        Assert.Equal("var x = 1; return x;", body);
        Assert.DoesNotContain("return var", body);
    }

    [Fact]
    public void BuildEvalBody_TryCatchWithoutTrailingSemi_BodyMode()
    {
        var expr = "var p = Load(); try { p.Apply(); return \"ok\"; } catch (System.Exception e) { return \"ERR: \" + e.Message; }";
        var body = ScriptCommands.BuildEvalBody(expr);

        Assert.StartsWith("var p =", body);
        Assert.DoesNotContain("return var", body);
        Assert.EndsWith(";", body.TrimEnd());
    }

    [Fact]
    public void BuildEvalBody_StartsWithReturn_NoDoubleReturn()
    {
        // Historical #30: agent wrote `return X ? Y : Z` (no trailing `;`).
        // Old wrapper produced `return return X ? Y : Z;` — CS1525.
        var body = ScriptCommands.BuildEvalBody("return X ? Y : Z");

        Assert.Equal("return X ? Y : Z;", body);
        Assert.DoesNotContain("return return", body);
    }

    [Fact]
    public void BuildEvalBody_StartsWithReturnAlreadyTerminated_Unchanged()
    {
        var body = ScriptCommands.BuildEvalBody("return 42;");
        Assert.Equal("return 42;", body);
    }

    [Fact]
    public void BuildEvalBody_ReturnWithParen_RecognisedAsReturn()
    {
        // `return(x)` (no space) is still a return keyword usage.
        var body = ScriptCommands.BuildEvalBody("return(42)");
        Assert.Equal("return(42);", body);
    }

    [Fact]
    public void BuildEvalBody_ReturnPrefixedIdentifier_NotTreatedAsReturn()
    {
        // `returnValue` is an identifier — do not strip/inline. Wrap as expression.
        var body = ScriptCommands.BuildEvalBody("returnValue");
        Assert.Equal("return returnValue;", body);
    }

    [Fact]
    public void BuildEvalBody_SemicolonInStringLiteral_ExpressionMode()
    {
        // The `;` is inside a string — should NOT trigger body mode.
        var body = ScriptCommands.BuildEvalBody("\"a;b\".Length");
        Assert.Equal("return \"a;b\".Length;", body);
    }

    [Fact]
    public void BuildEvalBody_LeadingWhitespaceWithReturn_Handled()
    {
        var body = ScriptCommands.BuildEvalBody("   return 42");
        Assert.EndsWith(";", body.TrimEnd());
        Assert.DoesNotContain("return return", body);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ExtractLeadingUsingDirectives — lifts `using X;` into the usings block
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractLeadingUsings_SingleDirective_LiftedAndStripped()
    {
        var usings = new List<string>();
        var remaining = ScriptCommands.ExtractLeadingUsingDirectives(
            "using Dirtybit.Game.Model; return Storage.X;", usings);

        Assert.Contains("Dirtybit.Game.Model", usings);
        Assert.Equal(" return Storage.X;", remaining);
    }

    [Fact]
    public void ExtractLeadingUsings_MultipleDirectives_AllLifted()
    {
        var usings = new List<string>();
        var remaining = ScriptCommands.ExtractLeadingUsingDirectives(
            "using A.B; using C.D; return X;", usings);

        Assert.Contains("A.B", usings);
        Assert.Contains("C.D", usings);
        Assert.Equal(" return X;", remaining);
    }

    [Fact]
    public void ExtractLeadingUsings_WithLeadingNewlines_Lifted()
    {
        // Agents often paste eval expressions with a leading newline
        // (historical #07: `unityctl script eval '\nusing X;\nreturn Y;'`)
        var usings = new List<string>();
        var remaining = ScriptCommands.ExtractLeadingUsingDirectives(
            "\nusing Dirtybit.Game.Model;\nreturn Storage.X;", usings);

        Assert.Contains("Dirtybit.Game.Model", usings);
        Assert.DoesNotContain("using Dirtybit", remaining);
    }

    [Fact]
    public void ExtractLeadingUsings_NoDirective_ExpressionUnchanged()
    {
        var usings = new List<string>();
        var remaining = ScriptCommands.ExtractLeadingUsingDirectives(
            "return Application.version;", usings);

        Assert.Empty(usings);
        Assert.Equal("return Application.version;", remaining);
    }

    [Fact]
    public void ExtractLeadingUsings_AliasForm_NotExtracted()
    {
        // `using X = Y;` is a type alias, not a namespace import. Leave it alone —
        // the alias might be valid as an inline statement.
        var usings = new List<string>();
        var remaining = ScriptCommands.ExtractLeadingUsingDirectives(
            "using X = System.Int32; return X.MaxValue;", usings);

        Assert.Empty(usings);
        Assert.StartsWith("using X = System.Int32;", remaining);
    }

    [Fact]
    public void ExtractLeadingUsings_UsingStatement_NotExtracted()
    {
        // `using (var x = ...)` is a resource disposal block, not a directive.
        var usings = new List<string>();
        var remaining = ScriptCommands.ExtractLeadingUsingDirectives(
            "using (var r = Open()) { r.Use(); }", usings);

        Assert.Empty(usings);
        Assert.StartsWith("using (", remaining);
    }

    [Fact]
    public void ExtractLeadingUsings_DuplicateAlreadyPresent_NotAdded()
    {
        var usings = new List<string> { "Foo.Bar" };
        ScriptCommands.ExtractLeadingUsingDirectives("using Foo.Bar; return 1;", usings);

        Assert.Single(usings);
    }

    // ═══════════════════════════════════════════════════════════════════
    // BuildEvalCode — end-to-end wrapper output for the full historical cases
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildEvalCode_HistoricalCase_UsingDirectiveInBody_Lifted()
    {
        // Replays historical #07 from log-analysis.
        var expr = "\nusing Dirtybit.Game.Model;\nusing UnityEngine.SceneManagement;\nreturn Storage.Clan.MyClanDetails;";
        var code = ScriptCommands.BuildEvalCode(expr, [], hasArgs: false);

        // `using` directives should have been lifted out of Main() …
        Assert.Contains("using Dirtybit.Game.Model;", code);
        Assert.Contains("using UnityEngine.SceneManagement;", code);
        // … and the body should no longer have inline `using` statements.
        var mainBodyStart = code.IndexOf("public static object Main()", StringComparison.Ordinal);
        var mainBody = code.Substring(mainBodyStart);
        Assert.DoesNotContain("using Dirtybit.Game.Model;", mainBody);
        Assert.DoesNotContain("using UnityEngine.SceneManagement;", mainBody);
    }

    [Fact]
    public void BuildEvalCode_HistoricalCase_ExplicitReturnWithoutSemi_NoDoubleReturn()
    {
        // Replays historical #30.
        var expr = "return System.AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == \"Foo\") ? \"yes\" : \"no\"";
        var code = ScriptCommands.BuildEvalCode(expr, [], hasArgs: false);

        Assert.DoesNotContain("return return", code);
        // And the single return we emit has a trailing `;`.
        Assert.Contains("? \"yes\" : \"no\";", code);
    }

    [Fact]
    public void BuildEvalCode_HistoricalCase_VarTryCatchWithoutSemi_BodyMode()
    {
        // Replays historical #40 (shape).
        var expr = "var p = Load(); try { p.Apply(); return \"ok\"; } catch (System.Exception e) { return \"ERR\"; }";
        var code = ScriptCommands.BuildEvalCode(expr, [], hasArgs: false);

        Assert.DoesNotContain("return var", code);
        // The var/try-catch landed intact in the body.
        Assert.Contains("var p = Load();", code);
        Assert.Contains("try {", code);
    }

    [Fact]
    public void BuildEvalCode_SemicolonInsideString_StillExpressionMode()
    {
        // Regression: a `;` inside a string literal must NOT force body mode.
        var code = ScriptCommands.BuildEvalCode("\"hello; world\".Length", [], hasArgs: false);

        // Expression mode → wrapped with `return ... ;`
        Assert.Contains("return \"hello; world\".Length;", code);
    }
}
