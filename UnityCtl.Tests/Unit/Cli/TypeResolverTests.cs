using System.Linq;
using UnityCtl.Editor;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class TypeResolverTests
{
    // ---------------- ExtractUnknownNames ----------------

    [Fact]
    public void ExtractUnknownNames_Cs0103_ReturnsName()
    {
        var diag = "(11,31): error CS0103: The name 'Storage' does not exist in the current context";

        var names = TypeResolver.ExtractUnknownNames(new[] { diag }).ToArray();

        Assert.Equal(new[] { "Storage" }, names);
    }

    [Fact]
    public void ExtractUnknownNames_Cs0234_ReturnsMissingSegment()
    {
        var diag = "(11,16): error CS0234: The type or namespace name 'Common' does not exist in the namespace 'Dirtybit' (are you missing an assembly reference?)";

        var names = TypeResolver.ExtractUnknownNames(new[] { diag }).ToArray();

        Assert.Equal(new[] { "Common" }, names);
    }

    [Fact]
    public void ExtractUnknownNames_Cs0246_ReturnsTypeName()
    {
        var diag = "(5,16): error CS0246: The type or namespace name 'FireButton' could not be found (are you missing a using directive or an assembly reference?)";

        var names = TypeResolver.ExtractUnknownNames(new[] { diag }).ToArray();

        Assert.Equal(new[] { "FireButton" }, names);
    }

    [Fact]
    public void ExtractUnknownNames_MultipleDistinctNames_AllExtractedOnce()
    {
        var diags = new[]
        {
            "(11,31): error CS0103: The name 'Storage' does not exist in the current context",
            "(12,50): error CS0103: The name 'Storage' does not exist in the current context", // dup
            "(13,20): error CS0246: The type or namespace name 'FireButton' could not be found"
        };

        var names = TypeResolver.ExtractUnknownNames(diags).ToArray();

        Assert.Equal(new[] { "Storage", "FireButton" }, names);
    }

    [Fact]
    public void ExtractUnknownNames_NonMatchingDiagnostic_ReturnsEmpty()
    {
        var diag = "(11,5): error CS1002: ; expected";

        var names = TypeResolver.ExtractUnknownNames(new[] { diag }).ToArray();

        Assert.Empty(names);
    }

    // ---------------- Find ----------------

    [Fact]
    public void Find_KnownPublicType_ReturnsMatch()
    {
        // xunit's Assert is public, should resolve in loaded assemblies
        var matches = TypeResolver.Find("Assert");

        Assert.Contains(matches, m => m.FullName == "Xunit.Assert");
    }

    [Fact]
    public void Find_UnknownType_ReturnsEmpty()
    {
        var matches = TypeResolver.Find("ThisTypeCannotPossiblyExistAnywhere_zzz42");

        Assert.Empty(matches);
    }

    [Fact]
    public void Find_EmptyName_ReturnsEmpty()
    {
        Assert.Empty(TypeResolver.Find(""));
        Assert.Empty(TypeResolver.Find("  "));
    }

    [Fact]
    public void Find_Storage_UsesShortNameFromDottedInput()
    {
        // Passing a fully-qualified name should also resolve by short name
        var matches = TypeResolver.Find("System.Xml.XmlNode");
        // Whether the type is actually loaded depends on tests; just verify no crash
        // and if found, at least one match has Name == XmlNode.
        foreach (var m in matches)
            Assert.EndsWith(".XmlNode", m.FullName);
    }

    // ---------------- BuildHints ----------------

    [Fact]
    public void BuildHints_UnknownNameNoMatches_ReturnsEmpty()
    {
        var diags = new[]
        {
            "(11,31): error CS0103: The name 'ThisTypeCannotPossiblyExistAnywhere_zzz42' does not exist in the current context"
        };

        var hints = TypeResolver.BuildHints(diags);

        Assert.Empty(hints);
    }

    [Fact]
    public void BuildHints_KnownName_ReturnsHint()
    {
        var diags = new[]
        {
            "(11,31): error CS0103: The name 'Assert' does not exist in the current context"
        };

        var hints = TypeResolver.BuildHints(diags);

        Assert.NotEmpty(hints);
        // At least one hint line should mention Xunit.Assert
        Assert.Contains(hints, h => h.Contains("Xunit.Assert"));
    }

    [Fact]
    public void BuildHints_MultipleMatches_ListsThem()
    {
        // "Assembly" is a very common type name across System.Reflection, etc. — likely multiple matches.
        var diags = new[]
        {
            "(11,31): error CS0103: The name 'Assembly' does not exist in the current context"
        };

        var hints = TypeResolver.BuildHints(diags);

        Assert.NotEmpty(hints);
    }
}
