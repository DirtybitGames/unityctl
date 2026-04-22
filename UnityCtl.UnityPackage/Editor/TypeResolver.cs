#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityCtl.Protocol;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Searches loaded assemblies for types by simple/short name. Shared between
    /// the <c>script.lookupType</c> handler and the compile-diagnostic enricher
    /// so agents can discover fully-qualified names they don't know.
    /// </summary>
    public static class TypeResolver
    {
        /// <summary>
        /// Find types whose short name equals <paramref name="name"/> across all
        /// loaded non-dynamic assemblies. Results are ranked so that types in
        /// user-looking assemblies (Assembly-CSharp, Dirtybit.*, project-named)
        /// come before engine/editor assemblies, and shorter namespaces win ties.
        /// </summary>
        public static List<ScriptLookupTypeMatch> Find(string name, int limit = 10)
        {
            return FindTypesRanked(name)
                .Select(x => ToMatch(x.Type, x.Assembly))
                .Take(limit)
                .ToList();
        }

        // Single assembly-walk implementation shared by Find (metadata) and
        // ResolveTypeForDiagnostic (needs the actual Type). Returns ranked
        // (Type, Assembly) tuples in the same order Find publishes.
        private static IEnumerable<(Type Type, Assembly Assembly)> FindTypesRanked(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Enumerable.Empty<(Type, Assembly)>();

            // Accept both "Foo" and "Some.Namespace.Foo"; compare by short name.
            var shortName = name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name;

            var matches = new List<(Type Type, Assembly Assembly, int Rank)>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!t.IsPublic && !t.IsNestedPublic) continue;
                    // Generic type defs have names like `List`1` — strip the arity
                    // suffix so searching for "List" matches List<T>.
                    var tName = t.Name;
                    var tick = tName.IndexOf('`');
                    if (tick >= 0) tName = tName.Substring(0, tick);
                    if (tName != shortName) continue;

                    var asmName = asm.GetName().Name ?? "";
                    // Nested types rank after non-nested peers in the same assembly tier.
                    // Otherwise `UnityEngine.RuleTile+...+Transform` (nested enum) would
                    // outrank `UnityEngine.Transform` alphabetically.
                    var rank = RankAssembly(asmName, t.Namespace) + (t.IsNested ? 5 : 0);
                    matches.Add((t, asm, rank));
                }
            }

            return matches
                .OrderBy(m => m.Rank)
                .ThenBy(m => (m.Type.Namespace ?? "").Length)
                .ThenBy(m => (m.Type.FullName ?? m.Type.Name).Length)
                .ThenBy(m => m.Type.FullName ?? m.Type.Name, StringComparer.Ordinal)
                .Select(m => (m.Type, m.Assembly));
        }

        private static ScriptLookupTypeMatch ToMatch(Type t, Assembly asm) => new()
        {
            FullName = t.FullName ?? t.Name,
            Namespace = t.Namespace,
            Assembly = asm.GetName().Name ?? "",
            Kind = ClassifyKind(t),
            IsStatic = t.IsClass && t.IsAbstract && t.IsSealed
        };

        private static string ClassifyKind(Type t)
        {
            if (t.IsEnum) return "enum";
            if (t.IsInterface) return "interface";
            if (t.IsValueType) return "struct";
            if (typeof(Delegate).IsAssignableFrom(t) && t != typeof(Delegate) && t != typeof(MulticastDelegate)) return "delegate";
            return "class";
        }

        // Lower rank = listed first. Heuristic:
        //   0   — Assembly-CSharp / Assembly-CSharp-Editor (default project asmdefs)
        //   100 — UnityEngine.* / Unity.* (engine runtime)
        //   110 — UnityEditor.* (editor API)
        //   200 — anything else (custom project asmdefs OR vendor packages — we can't tell them apart)
        //   300 — stdlib (System.*, Microsoft.*, Mono.*, mscorlib, netstandard)
        //   400 — test frameworks (nunit, xunit)
        private static int RankAssembly(string asmName, string? ns)
        {
            if (string.IsNullOrEmpty(asmName)) return 500;
            if (asmName == "Assembly-CSharp" || asmName == "Assembly-CSharp-Editor") return 0;
            if (asmName.StartsWith("UnityEngine.", StringComparison.Ordinal) || asmName == "UnityEngine") return 100;
            if (asmName.StartsWith("Unity.", StringComparison.Ordinal)) return 100;
            if (asmName.StartsWith("UnityEditor.", StringComparison.Ordinal) || asmName == "UnityEditor") return 110;
            if (asmName.StartsWith("nunit.", StringComparison.Ordinal) ||
                asmName.StartsWith("xunit.", StringComparison.Ordinal) ||
                asmName.StartsWith("Microsoft.VisualStudio.", StringComparison.Ordinal)) return 400;
            if (asmName.StartsWith("System.", StringComparison.Ordinal) || asmName == "System" ||
                asmName == "mscorlib" || asmName == "netstandard" ||
                asmName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                asmName.StartsWith("Mono.", StringComparison.Ordinal)) return 300;
            // Everything else: custom asmdefs (project packages) and vendor packages.
            // We can't reliably distinguish them by name, so they all rank below Unity.
            return 200;
        }

        private static readonly Regex UnknownNameCs0103 = new(
            @"error CS0103: The name '([^']+)' does not exist",
            RegexOptions.Compiled);
        private static readonly Regex UnknownNameCs0234 = new(
            @"error CS0234: The type or namespace name '([^']+)' does not exist in the namespace '([^']+)'",
            RegexOptions.Compiled);
        private static readonly Regex UnknownNameCs0246 = new(
            @"error CS0246: The type or namespace name '([^']+)' could not be found",
            RegexOptions.Compiled);
        // CS0117: 'BaseScene' does not contain a definition for 'Close'
        private static readonly Regex UnknownMemberCs0117 = new(
            @"error CS0117: '([^']+)' does not contain a definition for '([^']+)'",
            RegexOptions.Compiled);
        // CS1061: 'TMP_Text' does not contain a definition for 'fontSizeBase' and no accessible extension method ...
        private static readonly Regex UnknownMemberCs1061 = new(
            @"error CS1061: '([^']+)' does not contain a definition for '([^']+)'",
            RegexOptions.Compiled);

        /// <summary>
        /// Extract distinct unknown identifier names referenced in Roslyn diagnostic
        /// strings (CS0103/CS0234/CS0246). Used by <see cref="BuildHints"/> to look
        /// up candidates for each unknown name.
        /// </summary>
        public static IEnumerable<string> ExtractUnknownNames(IEnumerable<string> diagnostics)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in diagnostics)
            {
                if (string.IsNullOrEmpty(d)) continue;
                foreach (Match m in UnknownNameCs0103.Matches(d))
                    if (seen.Add(m.Groups[1].Value)) yield return m.Groups[1].Value;
                foreach (Match m in UnknownNameCs0234.Matches(d))
                    if (seen.Add(m.Groups[1].Value)) yield return m.Groups[1].Value;
                foreach (Match m in UnknownNameCs0246.Matches(d))
                    if (seen.Add(m.Groups[1].Value)) yield return m.Groups[1].Value;
            }
        }

        /// <summary>
        /// Extract distinct (type, member) pairs referenced in CS0117/CS1061
        /// diagnostics. Used by <see cref="BuildHints"/> to suggest close-named
        /// members when the agent typos a method/property/field.
        /// </summary>
        public static IEnumerable<(string Type, string Member)> ExtractUnknownMembers(IEnumerable<string> diagnostics)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in diagnostics)
            {
                if (string.IsNullOrEmpty(d)) continue;
                foreach (Match m in UnknownMemberCs0117.Matches(d))
                {
                    var pair = (Type: m.Groups[1].Value, Member: m.Groups[2].Value);
                    if (seen.Add(pair.Type + "::" + pair.Member)) yield return pair;
                }
                foreach (Match m in UnknownMemberCs1061.Matches(d))
                {
                    var pair = (Type: m.Groups[1].Value, Member: m.Groups[2].Value);
                    if (seen.Add(pair.Type + "::" + pair.Member)) yield return pair;
                }
            }
        }

        // When an unknown type name matches multiple candidates, show up to this
        // many before collapsing to a "pick one and fully qualify" prompt.
        private const int MaxTypeSuggestionsPerDiagnostic = 3;

        /// <summary>
        /// Build hint strings from a diagnostic list. Produces hints for:
        ///   - unknown type names (CS0103/CS0234/CS0246) → suggest FQN
        ///   - unknown members (CS0117/CS1061) → suggest close-named members of the type
        /// Returns empty array if no hints applicable.
        /// </summary>
        public static string[] BuildHints(IEnumerable<string> diagnostics)
        {
            // Materialize once so we can iterate twice.
            var diagList = diagnostics as IReadOnlyList<string> ?? new List<string>(diagnostics);

            var hints = new List<string>();

            foreach (var name in ExtractUnknownNames(diagList))
            {
                var matches = Find(name, MaxTypeSuggestionsPerDiagnostic);
                if (matches.Count == 0) continue;

                if (matches.Count == 1)
                {
                    var m = matches[0];
                    hints.Add(FormatSingleHint(name, m));
                }
                else
                {
                    hints.Add($"Hint: '{name}' matches {matches.Count} types:");
                    foreach (var m in matches)
                        hints.Add("  " + FormatMatchLine(m));
                    hints.Add($"  (pick one and add `-u <namespace>`, or fully qualify)");
                }
            }

            foreach (var (typeExpr, member) in ExtractUnknownMembers(diagList))
            {
                var type = ResolveTypeForDiagnostic(typeExpr);
                if (type == null) continue;

                var close = FindCloseMembers(type, member, 5);
                if (close.Count == 0)
                {
                    // No close matches — show a small sample so the agent at least sees something.
                    var sample = GetMemberNameSample(type, 8);
                    if (sample.Count == 0) continue;
                    hints.Add($"Hint: '{typeExpr}' has no member '{member}'. Some available members: {string.Join(", ", sample)} (run `unityctl script members {type.FullName}` for the full list).");
                }
                else
                {
                    hints.Add($"Hint: '{typeExpr}' has no member '{member}'. Close matches: {string.Join(", ", close)}.");
                }
            }

            return hints.ToArray();
        }

        private static string FormatSingleHint(string name, ScriptLookupTypeMatch m)
        {
            if (string.IsNullOrEmpty(m.Namespace))
                return $"Hint: '{name}' is defined as {m.FullName} in assembly {m.Assembly} (no namespace — fully qualify as `{m.FullName}`).";
            return $"Hint: '{name}' is defined as {m.FullName} in assembly {m.Assembly} — add `-u {m.Namespace}` or fully qualify.";
        }

        private static string FormatMatchLine(ScriptLookupTypeMatch m)
        {
            var ns = string.IsNullOrEmpty(m.Namespace) ? "(global)" : m.Namespace;
            return $"{m.FullName} [{m.Kind}] in {m.Assembly} (ns: {ns})";
        }

        // ─── Member resolution ────────────────────────────────────────────

        /// <summary>
        /// Resolve a type name as it appears in a diagnostic (may include generic
        /// args like <c>ObjectIdSortedList&lt;ClanChat&gt;</c>) to its short-name
        /// top-ranked match in loaded assemblies. Strips generic args before
        /// lookup since member reflection ignores instantiation anyway.
        /// </summary>
        public static Type? ResolveTypeForDiagnostic(string typeExpr)
        {
            if (string.IsNullOrWhiteSpace(typeExpr)) return null;

            // Strip generic args: "ObjectIdSortedList<ClanChat>" → "ObjectIdSortedList"
            var bareName = typeExpr;
            var lt = bareName.IndexOf('<');
            if (lt > 0) bareName = bareName.Substring(0, lt);

            return FindTypesRanked(bareName).Select(x => x.Type).FirstOrDefault();
        }

        /// <summary>
        /// Return member names close to <paramref name="attempted"/> on <paramref name="type"/>,
        /// ranked by case-insensitive Levenshtein distance. Public members only;
        /// inherited members included. De-duplicated (overloads collapse to one name).
        /// </summary>
        public static List<string> FindCloseMembers(Type type, string attempted, int limit = 5)
        {
            if (type == null || string.IsNullOrEmpty(attempted)) return new List<string>();

            var names = GetPublicMemberNames(type);
            if (names.Count == 0) return new List<string>();

            // Threshold: allow one-third of the attempted-name length as edit distance, min 2.
            var threshold = Math.Max(2, attempted.Length / 3);

            var scored = new List<(string Name, int Dist)>();
            foreach (var n in names)
            {
                var d = LevenshteinDistance(attempted.ToLowerInvariant(), n.ToLowerInvariant());
                if (d <= threshold) scored.Add((n, d));
            }

            return scored
                .OrderBy(s => s.Dist)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => s.Name)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// Sample up to <paramref name="limit"/> member names from <paramref name="type"/> —
        /// alphabetical, de-duplicated. Used when no close matches exist so the agent
        /// still sees something resembling an API hint.
        /// </summary>
        public static List<string> GetMemberNameSample(Type type, int limit = 8)
        {
            return GetPublicMemberNames(type)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// All user-addressable public member names on the type (instance + static,
        /// including inherited). Excludes compiler-generated accessors, operators,
        /// and constructors. Overloads collapse to a single name entry.
        /// </summary>
        public static HashSet<string> GetPublicMemberNames(Type type)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (type == null) return result;
            MemberInfo[] members;
            try
            {
                members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                          BindingFlags.Static | BindingFlags.FlattenHierarchy);
            }
            catch { return result; }

            foreach (var mi in members)
            {
                if (mi is MethodInfo m && m.IsSpecialName) continue;       // property/event accessors, operators
                if (mi.MemberType == MemberTypes.Constructor) continue;
                if (mi.Name.StartsWith("_", StringComparison.Ordinal)) continue; // convention: private-ish
                result.Add(mi.Name);
            }
            return result;
        }

        /// <summary>
        /// Detailed member info for the members-listing command. Groups by kind
        /// and formats signatures. Filter is an optional case-insensitive substring.
        /// </summary>
        public static List<ScriptMemberInfo> GetMembers(Type type, string? filter = null, bool staticOnly = false, int limit = 200)
        {
            var result = new List<ScriptMemberInfo>();
            if (type == null) return result;

            var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy;
            flags |= staticOnly ? BindingFlags.Static : (BindingFlags.Instance | BindingFlags.Static);

            MemberInfo[] members;
            try { members = type.GetMembers(flags); }
            catch { return result; }

            var seen = new HashSet<string>(StringComparer.Ordinal); // dedupe signatures
            foreach (var mi in members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (mi is MethodInfo meth && meth.IsSpecialName) continue;
                if (mi.MemberType == MemberTypes.Constructor) continue;
                if (mi.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    mi.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var info = BuildMemberInfo(mi);
                if (info == null) continue;
                var dedupeKey = info.Kind + "::" + info.Signature;
                if (!seen.Add(dedupeKey)) continue;

                result.Add(info);
                if (result.Count >= limit) break;
            }
            return result;
        }

        private static ScriptMemberInfo? BuildMemberInfo(MemberInfo mi)
        {
            switch (mi.MemberType)
            {
                case MemberTypes.Property:
                {
                    var p = (PropertyInfo)mi;
                    var t = FriendlyTypeName(p.PropertyType);
                    var accessors = "";
                    if (p.CanRead && p.CanWrite) accessors = " { get; set; }";
                    else if (p.CanRead) accessors = " { get; }";
                    else if (p.CanWrite) accessors = " { set; }";
                    var isStatic = (p.CanRead && p.GetMethod!.IsStatic) || (p.CanWrite && p.SetMethod!.IsStatic);
                    return new ScriptMemberInfo
                    {
                        Name = p.Name,
                        Kind = "property",
                        Signature = $"{p.Name} : {t}{accessors}",
                        IsStatic = isStatic,
                        DeclaringType = p.DeclaringType?.FullName ?? p.DeclaringType?.Name ?? ""
                    };
                }
                case MemberTypes.Method:
                {
                    var m = (MethodInfo)mi;
                    var ret = FriendlyTypeName(m.ReturnType);
                    var ps = string.Join(", ", m.GetParameters().Select(p => FriendlyTypeName(p.ParameterType) + " " + p.Name));
                    var generics = m.IsGenericMethod ? "<" + string.Join(", ", m.GetGenericArguments().Select(g => g.Name)) + ">" : "";
                    return new ScriptMemberInfo
                    {
                        Name = m.Name,
                        Kind = "method",
                        Signature = $"{m.Name}{generics}({ps}) : {ret}",
                        IsStatic = m.IsStatic,
                        DeclaringType = m.DeclaringType?.FullName ?? m.DeclaringType?.Name ?? ""
                    };
                }
                case MemberTypes.Field:
                {
                    var f = (FieldInfo)mi;
                    return new ScriptMemberInfo
                    {
                        Name = f.Name,
                        Kind = "field",
                        Signature = $"{f.Name} : {FriendlyTypeName(f.FieldType)}",
                        IsStatic = f.IsStatic,
                        DeclaringType = f.DeclaringType?.FullName ?? f.DeclaringType?.Name ?? ""
                    };
                }
                case MemberTypes.Event:
                {
                    var e = (EventInfo)mi;
                    var handler = e.EventHandlerType != null ? FriendlyTypeName(e.EventHandlerType) : "?";
                    var isStatic = e.AddMethod?.IsStatic ?? false;
                    return new ScriptMemberInfo
                    {
                        Name = e.Name,
                        Kind = "event",
                        Signature = $"{e.Name} : {handler}",
                        IsStatic = isStatic,
                        DeclaringType = e.DeclaringType?.FullName ?? e.DeclaringType?.Name ?? ""
                    };
                }
                case MemberTypes.NestedType:
                {
                    var t = (Type)mi;
                    return new ScriptMemberInfo
                    {
                        Name = t.Name,
                        Kind = "nested-type",
                        Signature = t.Name + " [" + ClassifyKind(t) + "]",
                        IsStatic = t.IsAbstract && t.IsSealed && t.IsClass,
                        DeclaringType = t.DeclaringType?.FullName ?? t.DeclaringType?.Name ?? ""
                    };
                }
                default:
                    return null;
            }
        }

        internal static string FriendlyTypeName(Type t)
        {
            if (t == null) return "?";
            if (t.IsByRef) return FriendlyTypeName(t.GetElementType()!);
            if (t == typeof(void)) return "void";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "long";
            if (t == typeof(short)) return "short";
            if (t == typeof(byte)) return "byte";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(string)) return "string";
            if (t == typeof(object)) return "object";
            if (t == typeof(char)) return "char";
            if (t.IsArray) return FriendlyTypeName(t.GetElementType()!) + "[]";
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                var baseName = def.Name;
                var tick = baseName.IndexOf('`');
                if (tick >= 0) baseName = baseName.Substring(0, tick);
                var args = string.Join(", ", t.GetGenericArguments().Select(FriendlyTypeName));
                return baseName + "<" + args + ">";
            }
            return t.Name;
        }

        // ─── Edit distance ────────────────────────────────────────────────

        internal static int LevenshteinDistance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[b.Length];
        }
    }

    /// <summary>
    /// Info for a single member returned by the members-listing command.
    /// </summary>
    public class ScriptMemberInfo
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = ""; // property|method|field|event|nested-type
        public string Signature { get; set; } = "";
        public bool IsStatic { get; set; }
        public string DeclaringType { get; set; } = "";
    }
}
