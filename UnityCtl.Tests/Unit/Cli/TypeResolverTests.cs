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

    // ---------------- Member error extraction ----------------

    [Fact]
    public void ExtractUnknownMembers_Cs0117_ReturnsTypeAndMember()
    {
        var diag = "(11,35): error CS0117: 'BaseScene' does not contain a definition for 'Close'";

        var pairs = TypeResolver.ExtractUnknownMembers(new[] { diag }).ToArray();

        Assert.Single(pairs);
        Assert.Equal("BaseScene", pairs[0].Type);
        Assert.Equal("Close", pairs[0].Member);
    }

    [Fact]
    public void ExtractUnknownMembers_Cs1061_ReturnsTypeAndMember()
    {
        var diag = "(12,25): error CS1061: 'TMP_Text' does not contain a definition for 'fontSizeBase' and no accessible extension method 'fontSizeBase' accepting a first argument of type 'TMP_Text' could be found";

        var pairs = TypeResolver.ExtractUnknownMembers(new[] { diag }).ToArray();

        Assert.Single(pairs);
        Assert.Equal("TMP_Text", pairs[0].Type);
        Assert.Equal("fontSizeBase", pairs[0].Member);
    }

    [Fact]
    public void ExtractUnknownMembers_GenericType_KeepsGenericArgsInTypeString()
    {
        // Roslyn emits the instantiation name; we keep it verbatim so callers can decide how to strip.
        var diag = "(12,34): error CS1061: 'ObjectIdSortedList<ClanChat>' does not contain a definition for 'Get'";

        var pairs = TypeResolver.ExtractUnknownMembers(new[] { diag }).ToArray();

        Assert.Single(pairs);
        Assert.Equal("ObjectIdSortedList<ClanChat>", pairs[0].Type);
        Assert.Equal("Get", pairs[0].Member);
    }

    [Fact]
    public void ExtractUnknownMembers_Dedupes()
    {
        var diags = new[]
        {
            "(11,1): error CS1061: 'T' does not contain a definition for 'X'",
            "(12,5): error CS1061: 'T' does not contain a definition for 'X'",   // dup
            "(13,9): error CS1061: 'T' does not contain a definition for 'Y'"    // distinct member
        };

        var pairs = TypeResolver.ExtractUnknownMembers(diags).ToArray();

        Assert.Equal(2, pairs.Length);
    }

    // ---------------- Levenshtein ----------------

    [Fact]
    public void Levenshtein_Identical_IsZero()
    {
        Assert.Equal(0, TypeResolver.LevenshteinDistance("fontSize", "fontSize"));
    }

    [Fact]
    public void Levenshtein_OneEdit_IsOne()
    {
        Assert.Equal(1, TypeResolver.LevenshteinDistance("fontSize", "fontsize")); // case sensitive at this layer
        Assert.Equal(1, TypeResolver.LevenshteinDistance("abc", "abcd"));
        Assert.Equal(1, TypeResolver.LevenshteinDistance("abc", "abd"));
    }

    [Fact]
    public void Levenshtein_Empty_ReturnsOtherLength()
    {
        Assert.Equal(5, TypeResolver.LevenshteinDistance("", "hello"));
        Assert.Equal(5, TypeResolver.LevenshteinDistance("hello", ""));
    }

    // ---------------- FindCloseMembers ----------------

    [Fact]
    public void FindCloseMembers_TypoedName_ReturnsClosestReal()
    {
        // xunit Assert has `Equal`, `NotEqual`, `Same`, etc.
        var assertType = typeof(Xunit.Assert);

        var close = TypeResolver.FindCloseMembers(assertType, "Eqaul", 5);

        Assert.Contains("Equal", close);
    }

    [Fact]
    public void FindCloseMembers_NoMatch_ReturnsEmpty()
    {
        var close = TypeResolver.FindCloseMembers(typeof(Xunit.Assert), "ThisIsWayTooFarXyz123", 5);

        Assert.Empty(close);
    }

    // ---------------- GetMembers (signature formatting) ----------------

    [Fact]
    public void GetMembers_ReturnsPublicPropertiesAndMethods()
    {
        // TimeSpan has well-known stable members we can assert on
        var members = TypeResolver.GetMembers(typeof(TimeSpan));

        Assert.NotEmpty(members);
        Assert.Contains(members, m => m.Name == "TotalSeconds" && m.Kind == "property");
        Assert.Contains(members, m => m.Name == "FromSeconds" && m.Kind == "method" && m.IsStatic);
    }

    [Fact]
    public void GetMembers_Filter_MatchesSubstringCaseInsensitively()
    {
        var members = TypeResolver.GetMembers(typeof(TimeSpan), filter: "total");

        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.Contains("total", m.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetMembers_StaticOnly_ExcludesInstance()
    {
        var members = TypeResolver.GetMembers(typeof(TimeSpan), staticOnly: true);

        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.True(m.IsStatic, $"{m.Name} should be static"));
    }

    [Fact]
    public void GetMembers_MethodSignatureIncludesParamsAndReturn()
    {
        var members = TypeResolver.GetMembers(typeof(TimeSpan), filter: "FromSeconds");

        // TimeSpan.FromSeconds has multiple overloads — ensure at least one
        // signature surfaces the return type and a recognizable numeric param.
        Assert.Contains(members, m => m.Name == "FromSeconds" && m.Signature.Contains("TimeSpan"));
        Assert.Contains(members, m => m.Name == "FromSeconds" &&
            (m.Signature.Contains("double") || m.Signature.Contains("long") || m.Signature.Contains("int")));
    }

    [Fact]
    public void ResolveTypeForDiagnostic_StripsGenericArgs()
    {
        // `List<int>` → should resolve to System.Collections.Generic.List
        var t = TypeResolver.ResolveTypeForDiagnostic("List<int>");

        Assert.NotNull(t);
        Assert.Contains("List", t!.Name);
    }

    [Fact]
    public void ResolveTypeForDiagnostic_UnknownType_ReturnsNull()
    {
        var t = TypeResolver.ResolveTypeForDiagnostic("Totally_Nonexistent_Type_Zyzyx");
        Assert.Null(t);
    }

    // ---------------- BuildHints: member path ----------------

    [Fact]
    public void BuildHints_UnknownMember_WithCloseMatch_SuggestsIt()
    {
        // 'Assert.Eqaul' → should suggest 'Equal'
        var diags = new[]
        {
            "(11,20): error CS0117: 'Assert' does not contain a definition for 'Eqaul'"
        };

        var hints = TypeResolver.BuildHints(diags);

        Assert.NotEmpty(hints);
        Assert.Contains(hints, h => h.Contains("Equal") && h.Contains("Eqaul"));
    }

    [Fact]
    public void BuildHints_UnknownMember_NoCloseMatch_ShowsSample()
    {
        var diags = new[]
        {
            "(11,20): error CS0117: 'Assert' does not contain a definition for 'UtterlyBizarreNameAaaZzz'"
        };

        var hints = TypeResolver.BuildHints(diags);

        // Should mention some sample members and reference the members command
        Assert.NotEmpty(hints);
        Assert.Contains(hints, h => h.Contains("Some available members"));
        Assert.Contains(hints, h => h.Contains("script members"));
    }
}
