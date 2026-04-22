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
            if (string.IsNullOrWhiteSpace(name)) return new List<ScriptLookupTypeMatch>();

            // Accept both "Foo" and "Some.Namespace.Foo"; compare by short name.
            var shortName = name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name;

            var matches = new List<(ScriptLookupTypeMatch match, int rank)>();
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
                    if (t.Name != shortName) continue;
                    // Skip generic backticks mangling: only direct name match
                    if (t.Name.IndexOf('`') >= 0) continue;

                    var asmName = asm.GetName().Name ?? "";
                    // Nested types rank after non-nested peers in the same assembly tier.
                    // Otherwise `UnityEngine.RuleTile+...+Transform` (nested enum) would
                    // outrank `UnityEngine.Transform` alphabetically.
                    var rank = RankAssembly(asmName, t.Namespace) + (t.IsNested ? 5 : 0);
                    matches.Add((new ScriptLookupTypeMatch
                    {
                        FullName = t.FullName ?? t.Name,
                        Namespace = t.Namespace,
                        Assembly = asmName,
                        Kind = ClassifyKind(t),
                        IsStatic = t.IsClass && t.IsAbstract && t.IsSealed
                    }, rank));
                }
            }

            return matches
                .OrderBy(m => m.rank)
                .ThenBy(m => (m.match.Namespace ?? "").Length)
                .ThenBy(m => m.match.FullName.Length)
                .ThenBy(m => m.match.FullName, StringComparer.Ordinal)
                .Select(m => m.match)
                .Take(limit)
                .ToList();
        }

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
        /// Build hint strings from a diagnostic list. For each unknown name with
        /// matches in loaded assemblies, emit "Hint: '<name>' is defined as
        /// <FullName> in assembly <Assembly> (add `-u <namespace>`)." lines.
        /// Returns empty if no hints applicable.
        /// </summary>
        public static string[] BuildHints(IEnumerable<string> diagnostics, int perNameLimit = 3)
        {
            var hints = new List<string>();
            foreach (var name in ExtractUnknownNames(diagnostics))
            {
                var matches = Find(name, perNameLimit);
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
    }
}
