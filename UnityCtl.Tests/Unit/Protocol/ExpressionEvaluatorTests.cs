using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Protocol;

public class ExpressionEvaluatorTests
{
    // --- Literal evaluation ---

    [Fact]
    public void IntLiteral_ReturnsInt()
    {
        var result = ExpressionEvaluator.Evaluate("42");
        Assert.True(result.Success);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void NegativeIntLiteral_ReturnsNegativeInt()
    {
        var result = ExpressionEvaluator.Evaluate("-7");
        Assert.True(result.Success);
        Assert.Equal(-7, result.Value);
    }

    [Fact]
    public void FloatLiteral_ReturnsFloat()
    {
        var result = ExpressionEvaluator.Evaluate("3.14f");
        Assert.True(result.Success);
        Assert.Equal(3.14f, result.Value);
    }

    [Fact]
    public void DoubleLiteral_ReturnsDouble()
    {
        var result = ExpressionEvaluator.Evaluate("2.718");
        Assert.True(result.Success);
        Assert.Equal(2.718, result.Value);
    }

    [Fact]
    public void StringLiteral_ReturnsString()
    {
        var result = ExpressionEvaluator.Evaluate("\"hello world\"");
        Assert.True(result.Success);
        Assert.Equal("hello world", result.Value);
    }

    [Fact]
    public void StringLiteral_WithEscapes()
    {
        var result = ExpressionEvaluator.Evaluate("\"line1\\nline2\"");
        Assert.True(result.Success);
        Assert.Equal("line1\nline2", result.Value);
    }

    [Fact]
    public void BoolLiteral_True()
    {
        var result = ExpressionEvaluator.Evaluate("true");
        Assert.True(result.Success);
        Assert.Equal(true, result.Value);
    }

    [Fact]
    public void BoolLiteral_False()
    {
        var result = ExpressionEvaluator.Evaluate("false");
        Assert.True(result.Success);
        Assert.Equal(false, result.Value);
    }

    [Fact]
    public void NullLiteral_ReturnsNull()
    {
        var result = ExpressionEvaluator.Evaluate("null");
        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public void CharLiteral_ReturnsChar()
    {
        var result = ExpressionEvaluator.Evaluate("'x'");
        Assert.True(result.Success);
        Assert.Equal('x', result.Value);
    }

    [Fact]
    public void LongLiteral_ReturnsLong()
    {
        var result = ExpressionEvaluator.Evaluate("9999999999L");
        Assert.True(result.Success);
        Assert.Equal(9999999999L, result.Value);
    }

    // --- Static property access (using System types) ---

    [Fact]
    public void StaticProperty_MathPI()
    {
        var result = ExpressionEvaluator.Evaluate("Math.PI");
        Assert.True(result.Success);
        Assert.Equal(Math.PI, result.Value);
    }

    [Fact]
    public void StaticProperty_MathE()
    {
        var result = ExpressionEvaluator.Evaluate("Math.E");
        Assert.True(result.Success);
        Assert.Equal(Math.E, result.Value);
    }

    [Fact]
    public void StaticProperty_StringEmpty()
    {
        var result = ExpressionEvaluator.Evaluate("String.Empty");
        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Value);
    }

    [Fact]
    public void StaticProperty_IntMaxValue()
    {
        var result = ExpressionEvaluator.Evaluate("Int32.MaxValue");
        Assert.True(result.Success);
        Assert.Equal(int.MaxValue, result.Value);
    }

    [Fact]
    public void StaticProperty_EnvironmentNewLine()
    {
        var result = ExpressionEvaluator.Evaluate("Environment.NewLine");
        Assert.True(result.Success);
        Assert.Equal(Environment.NewLine, result.Value);
    }

    // --- Static method calls ---

    [Fact]
    public void StaticMethod_MathMax()
    {
        var result = ExpressionEvaluator.Evaluate("Math.Max(3, 7)");
        Assert.True(result.Success);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public void StaticMethod_MathAbs()
    {
        var result = ExpressionEvaluator.Evaluate("Math.Abs(-42)");
        Assert.True(result.Success);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void StaticMethod_IntParse()
    {
        var result = ExpressionEvaluator.Evaluate("Int32.Parse(\"123\")");
        Assert.True(result.Success);
        Assert.Equal(123, result.Value);
    }

    [Fact]
    public void StaticMethod_StringIsNullOrEmpty()
    {
        var result = ExpressionEvaluator.Evaluate("String.IsNullOrEmpty(\"\")");
        Assert.True(result.Success);
        Assert.Equal(true, result.Value);
    }

    // --- Member chaining ---

    [Fact]
    public void MemberChain_StringLength()
    {
        // "hello".Length - but we can't call methods on literals directly in our simple parser
        // Instead test via a static call that returns an instance
        var result = ExpressionEvaluator.Evaluate("Environment.NewLine.Length");
        Assert.True(result.Success);
        Assert.IsType<int>(result.Value);
    }

    [Fact]
    public void MemberChain_DateTimeNow()
    {
        var result = ExpressionEvaluator.Evaluate("DateTime.Now.Year");
        Assert.True(result.Success);
        Assert.Equal(DateTime.Now.Year, result.Value);
    }

    // --- Instance method calls on chained results ---

    [Fact]
    public void InstanceMethod_ToString()
    {
        var result = ExpressionEvaluator.Evaluate("Math.PI.ToString()");
        Assert.True(result.Success);
        Assert.IsType<string>(result.Value);
    }

    [Fact]
    public void InstanceMethod_GetType()
    {
        var result = ExpressionEvaluator.Evaluate("Int32.MaxValue.GetType()");
        Assert.True(result.Success);
        Assert.Equal(typeof(int), result.Value);
    }

    // --- typeof ---

    [Fact]
    public void Typeof_SystemType()
    {
        var result = ExpressionEvaluator.Evaluate("typeof(Int32)");
        Assert.True(result.Success);
        Assert.Equal(typeof(int), result.Value);
    }

    [Fact]
    public void Typeof_FullyQualified()
    {
        var result = ExpressionEvaluator.Evaluate("typeof(System.String)");
        Assert.True(result.Success);
        Assert.Equal(typeof(string), result.Value);
    }

    // --- Enum values ---

    [Fact]
    public void EnumValue_DayOfWeek()
    {
        var result = ExpressionEvaluator.Evaluate("DayOfWeek.Monday");
        Assert.True(result.Success);
        Assert.Equal(DayOfWeek.Monday, result.Value);
    }

    // --- Error cases ---

    [Fact]
    public void EmptyExpression_ReturnsError()
    {
        var result = ExpressionEvaluator.Evaluate("");
        Assert.False(result.Success);
        Assert.Contains("empty", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownType_ReturnsError()
    {
        var result = ExpressionEvaluator.Evaluate("NonExistentType.Foo");
        Assert.False(result.Success);
        Assert.Contains("Unknown identifier", result.Error!);
    }

    [Fact]
    public void UnknownMember_ReturnsError()
    {
        var result = ExpressionEvaluator.Evaluate("Math.NonExistentMember");
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error!);
    }

    [Fact]
    public void UnterminatedString_ReturnsError()
    {
        var result = ExpressionEvaluator.Evaluate("\"unterminated");
        Assert.False(result.Success);
        Assert.Contains("Unterminated", result.Error!);
    }

    [Fact]
    public void NullMemberAccess_ReturnsError()
    {
        var result = ExpressionEvaluator.Evaluate("null.ToString()");
        Assert.False(result.Success);
        Assert.Contains("null", result.Error!);
    }

    // --- Additional namespaces ---

    [Fact]
    public void AdditionalNamespaces_ResolvesTypes()
    {
        // System.IO.Path is in the System.IO namespace which isn't in defaults
        var result = ExpressionEvaluator.Evaluate("Path.DirectorySeparatorChar", new[] { "System.IO" });
        Assert.True(result.Success);
        Assert.Equal(System.IO.Path.DirectorySeparatorChar, result.Value);
    }

    // --- Tokenizer tests ---

    [Fact]
    public void Tokenizer_SimpleExpression()
    {
        var tokens = ExpressionEvaluator.Tokenize("Screen.width");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(ExpressionEvaluator.TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("Screen", tokens[0].Value);
        Assert.Equal(ExpressionEvaluator.TokenKind.Dot, tokens[1].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("width", tokens[2].Value);
    }

    [Fact]
    public void Tokenizer_MethodCallWithArgs()
    {
        var tokens = ExpressionEvaluator.Tokenize("Find(\"Player\")");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(ExpressionEvaluator.TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.LParen, tokens[1].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.StringLiteral, tokens[2].Kind);
        Assert.Equal("Player", tokens[2].Value);
        Assert.Equal(ExpressionEvaluator.TokenKind.RParen, tokens[3].Kind);
    }

    [Fact]
    public void Tokenizer_NumericLiterals()
    {
        var tokens = ExpressionEvaluator.Tokenize("42 3.14f 2.718");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(ExpressionEvaluator.TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.FloatLiteral, tokens[1].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.DoubleLiteral, tokens[2].Kind);
    }

    [Fact]
    public void Tokenizer_Keywords()
    {
        var tokens = ExpressionEvaluator.Tokenize("true false null typeof");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(ExpressionEvaluator.TokenKind.BoolLiteral, tokens[0].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.BoolLiteral, tokens[1].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.NullLiteral, tokens[2].Kind);
        Assert.Equal(ExpressionEvaluator.TokenKind.Typeof, tokens[3].Kind);
    }

    // --- SerializeValue ---

    [Fact]
    public void SerializeValue_IntResult()
    {
        var result = ExpressionEvaluator.Evaluate("42");
        Assert.Equal("42", result.SerializeValue());
    }

    [Fact]
    public void SerializeValue_NullResult()
    {
        var result = ExpressionEvaluator.Evaluate("null");
        Assert.Null(result.SerializeValue());
    }

    // --- ResultType ---

    [Fact]
    public void ResultType_Int()
    {
        var result = ExpressionEvaluator.Evaluate("42");
        Assert.Equal("System.Int32", result.ResultType);
    }

    [Fact]
    public void ResultType_String()
    {
        var result = ExpressionEvaluator.Evaluate("\"hello\"");
        Assert.Equal("System.String", result.ResultType);
    }

    [Fact]
    public void ResultType_Null()
    {
        var result = ExpressionEvaluator.Evaluate("null");
        Assert.Null(result.ResultType);
    }

    // --- Parenthesized expressions ---

    [Fact]
    public void Parenthesized_Expression()
    {
        var result = ExpressionEvaluator.Evaluate("(42)");
        Assert.True(result.Success);
        Assert.Equal(42, result.Value);
    }

    // --- Complex real-world-ish expressions ---

    [Fact]
    public void Complex_MathRoundWithDouble()
    {
        var result = ExpressionEvaluator.Evaluate("Math.Round(3.7)");
        Assert.True(result.Success);
        Assert.Equal(4.0, result.Value);
    }

    [Fact]
    public void Complex_EnvProcessorCount()
    {
        var result = ExpressionEvaluator.Evaluate("Environment.ProcessorCount");
        Assert.True(result.Success);
        Assert.IsType<int>(result.Value);
        Assert.True((int)result.Value! > 0);
    }

    [Fact]
    public void Complex_GuidNewGuid()
    {
        var result = ExpressionEvaluator.Evaluate("Guid.NewGuid().ToString()");
        Assert.True(result.Success);
        Assert.IsType<string>(result.Value);
        // Should be a valid GUID string
        Assert.True(Guid.TryParse((string)result.Value!, out _));
    }
}
