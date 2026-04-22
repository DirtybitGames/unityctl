using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

[assembly: InternalsVisibleTo("UnityCtl.Tests")]

namespace UnityCtl.Cli;

public static class ScriptCommands
{
    private static readonly string[] DefaultUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "UnityEngine",
        "UnityEditor"
    ];

    public static Command CreateCommand()
    {
        var scriptCommand = new Command("script", "C# script execution operations");

        // script execute
        var executeCommand = new Command("execute", "Execute C# code in the Unity Editor");
        executeCommand.AddAlias("run");

        var fileOption = new Option<FileInfo?>(
            aliases: ["--file", "-f"],
            description: "Read C# code from a file"
        );

        var classOption = new Option<string>(
            aliases: ["--class"],
            getDefaultValue: () => "Script",
            description: "Name of the class containing the method to execute"
        );

        var methodOption = new Option<string>(
            aliases: ["--method"],
            getDefaultValue: () => "Main",
            description: "Name of the static method to execute"
        );

        var scriptArgsArgument = new Argument<string[]>(
            name: "script-args",
            description: "Arguments to pass to the script's Main method (after --)",
            getDefaultValue: () => Array.Empty<string>()
        );

        executeCommand.AddOption(fileOption);
        executeCommand.AddOption(classOption);
        executeCommand.AddOption(methodOption);
        executeCommand.AddArgument(scriptArgsArgument);

        executeCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var file = context.ParseResult.GetValueForOption(fileOption);
            var className = context.ParseResult.GetValueForOption(classOption) ?? "Script";
            var methodName = context.ParseResult.GetValueForOption(methodOption) ?? "Main";
            var scriptArgs = context.ParseResult.GetValueForArgument(scriptArgsArgument);

            // If no -f given, check if first positional arg is a .cs file
            (file, scriptArgs) = ResolvePositionalFile(file, scriptArgs);

            // Determine code source: file or stdin
            string? csharpCode = null;

            if (file != null)
            {
                if (!file.Exists)
                {
                    Console.Error.WriteLine($"Error: File not found: {file.FullName}");
                    context.ExitCode = 1;
                    return;
                }
                csharpCode = await File.ReadAllTextAsync(file.FullName);
            }
            else if (Console.IsInputRedirected)
            {
                // Read from stdin
                csharpCode = await Console.In.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(csharpCode))
            {
                Console.Error.WriteLine("Error: No C# code provided. Pass a .cs file or pipe code via stdin.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Example:");
                Console.Error.WriteLine("  unityctl script execute ./MyScript.cs");
                Console.Error.WriteLine("  unityctl script execute ./MyScript.cs -- arg1 arg2 \"arg with spaces\"");
                Console.Error.WriteLine("  cat MyScript.cs | unityctl script execute");
                context.ExitCode = 1;
                return;
            }

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "code", csharpCode },
                { "className", className },
                { "methodName", methodName },
                { "scriptArgs", scriptArgs }
            };

            var timeout = ContextHelper.GetTimeout(context);

            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptExecute, args, timeout);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            DisplayScriptResult(context, response, json, isEval: false);
        });

        scriptCommand.AddCommand(executeCommand);

        // script eval
        var evalCommand = new Command("eval", "Evaluate a C# expression in the Unity Editor");

        var expressionArgument = new Argument<string>(
            name: "expression",
            description: "C# expression or method body to evaluate",
            getDefaultValue: () => string.Empty
        );

        var usingOption = new Option<string[]>(
            aliases: ["--using", "-u"],
            description: "Additional using directives (e.g., UnityEngine.UI)",
            getDefaultValue: () => Array.Empty<string>()
        );

        var idOption = new Option<string?>(
            "--id",
            "Target object by instance ID (from snapshot). Injects 'target'. Comma-separated IDs inject 'targets[]' array."
        );

        var evalScriptArgsArgument = new Argument<string[]>(
            name: "script-args",
            description: "Arguments to pass to Main (after --)",
            getDefaultValue: () => Array.Empty<string>()
        );

        evalCommand.AddArgument(expressionArgument);
        evalCommand.AddOption(usingOption);
        evalCommand.AddOption(idOption);
        evalCommand.AddArgument(evalScriptArgsArgument);

        evalCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var expression = context.ParseResult.GetValueForArgument(expressionArgument);
            var extraUsings = context.ParseResult.GetValueForOption(usingOption) ?? Array.Empty<string>();
            var scriptArgs = context.ParseResult.GetValueForArgument(evalScriptArgsArgument);

            if (string.IsNullOrWhiteSpace(expression) && Console.IsInputRedirected)
            {
                expression = await Console.In.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(expression))
            {
                Console.Error.WriteLine("Error: Expression cannot be empty. Pass as argument or pipe via stdin.");
                context.ExitCode = 1;
                return;
            }

            // Fix shell mangling: MINGW bash escapes ! to \! even in single quotes.
            if (OperatingSystem.IsWindows())
                expression = expression.Replace("\\!", "!");

            var instanceIds = context.ParseResult.GetValueForOption(idOption);

            var hasArgs = scriptArgs.Length > 0;
            var csharpCode = BuildEvalCode(expression, extraUsings, hasArgs, instanceIds);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "code", csharpCode },
                { "className", "Script" },
                { "methodName", "Main" },
                { "scriptArgs", scriptArgs }
            };

            var timeout = ContextHelper.GetTimeout(context);

            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptExecute, args, timeout);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            DisplayScriptResult(context, response, json, isEval: true, generatedCode: csharpCode);
        });

        scriptCommand.AddCommand(evalCommand);

        // script lookup-type
        var lookupCommand = new Command("lookup-type", "Find loaded types by short name (returns FullName, namespace, assembly)");
        var lookupNameArg = new Argument<string>(
            name: "name",
            description: "Short or partial type name (e.g. 'Storage', 'ClanFeedOSA')"
        );
        var lookupLimitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 10,
            description: "Maximum matches to return (1–100)"
        );
        lookupCommand.AddArgument(lookupNameArg);
        lookupCommand.AddOption(lookupLimitOption);
        lookupCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var name = context.ParseResult.GetValueForArgument(lookupNameArg);
            var limit = context.ParseResult.GetValueForOption(lookupLimitOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "name", name },
                { "limit", limit }
            };

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptLookupType, args, timeout);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            DisplayLookupTypeResult(context, response, json);
        });

        scriptCommand.AddCommand(lookupCommand);

        // script members
        var membersCommand = new Command("members", "List public members of a type (properties, methods, fields, events)");
        var membersNameArg = new Argument<string>(
            name: "type",
            description: "Type name — short (e.g. 'Transform') or fully-qualified ('UnityEngine.Transform')"
        );
        var membersFilterOption = new Option<string?>(
            aliases: ["--filter", "-f"],
            description: "Case-insensitive substring filter on member names"
        );
        var membersStaticOption = new Option<bool>(
            aliases: ["--static", "-s"],
            description: "Only static members"
        );
        var membersLimitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 50,
            description: "Maximum members to return (1–500)"
        );
        membersCommand.AddArgument(membersNameArg);
        membersCommand.AddOption(membersFilterOption);
        membersCommand.AddOption(membersStaticOption);
        membersCommand.AddOption(membersLimitOption);
        membersCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var name = context.ParseResult.GetValueForArgument(membersNameArg);
            var filter = context.ParseResult.GetValueForOption(membersFilterOption);
            var staticOnly = context.ParseResult.GetValueForOption(membersStaticOption);
            var limit = context.ParseResult.GetValueForOption(membersLimitOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "name", name },
                { "filter", filter },
                { "staticOnly", staticOnly },
                { "limit", limit }
            };

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptMembers, args, timeout);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            DisplayMembersResult(context, response, json);
        });

        scriptCommand.AddCommand(membersCommand);

        return scriptCommand;
    }

    internal static void DisplayMembersResult(InvocationContext context, ResponseMessage response, bool json)
    {
        var result = JsonConvert.DeserializeObject<ScriptMembersResult>(
            JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
            JsonHelper.Settings
        );

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(response.Result));
            return;
        }

        if (result == null || string.IsNullOrEmpty(result.ResolvedType))
        {
            Console.WriteLine($"No loaded type matches '{result?.Query ?? "?"}'. Try `unityctl script lookup-type <name>` to find the full name.");
            context.ExitCode = 1;
            return;
        }

        Console.WriteLine($"{result.ResolvedType} (assembly: {result.Assembly}):");

        var byKind = result.Members
            .GroupBy(m => m.Kind)
            .OrderBy(g => KindSortOrder(g.Key));

        foreach (var group in byKind)
        {
            Console.WriteLine($"  {CapitalizeKind(group.Key)}:");
            foreach (var m in group.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var staticTag = m.IsStatic ? " [static]" : "";
                Console.WriteLine($"    {m.Signature}{staticTag}");
            }
        }

        if (result.Members.Length == 0)
        {
            Console.WriteLine("  (no public members matched)");
        }
        if (result.Truncated)
        {
            Console.WriteLine($"  ... (more members — raise --limit or add --filter)");
        }
    }

    // Display order + section title for each member kind. Single source of truth
    // so adding a new kind updates sorting and heading in one place.
    private static readonly Dictionary<string, (int Order, string Title)> MemberKindDisplay = new()
    {
        ["property"] = (0, "Properties"),
        ["field"] = (1, "Fields"),
        ["method"] = (2, "Methods"),
        ["event"] = (3, "Events"),
        ["nested-type"] = (4, "Nested types"),
    };

    private static int KindSortOrder(string kind) =>
        MemberKindDisplay.TryGetValue(kind, out var info) ? info.Order : int.MaxValue;

    private static string CapitalizeKind(string kind) =>
        MemberKindDisplay.TryGetValue(kind, out var info) ? info.Title : kind;

    internal static void DisplayLookupTypeResult(InvocationContext context, ResponseMessage response, bool json)
    {
        var result = JsonConvert.DeserializeObject<ScriptLookupTypeResult>(
            JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
            JsonHelper.Settings
        );

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(response.Result));
            return;
        }

        if (result == null || result.Matches.Length == 0)
        {
            Console.WriteLine($"No loaded type matches '{result?.Query ?? "?"}'.");
            context.ExitCode = 1;
            return;
        }

        Console.WriteLine($"Matches for '{result.Query}' ({result.Matches.Length}{(result.Truncated ? "+" : "")}):");
        foreach (var m in result.Matches)
        {
            var ns = string.IsNullOrEmpty(m.Namespace) ? "(global)" : m.Namespace;
            var staticTag = m.IsStatic ? " static" : "";
            Console.WriteLine($"  {m.FullName} [{m.Kind}{staticTag}]  assembly={m.Assembly}  ns={ns}");
        }
        if (result.Truncated)
        {
            Console.WriteLine($"  ... (more results — raise --limit to see them)");
        }
    }

    internal static (FileInfo? file, string[] scriptArgs) ResolvePositionalFile(FileInfo? file, string[] scriptArgs)
    {
        if (file == null && scriptArgs.Length > 0 && scriptArgs[0].EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return (new FileInfo(scriptArgs[0]), scriptArgs.Skip(1).ToArray());
        }
        return (file, scriptArgs);
    }

    internal static string BuildEvalCode(string expression, string[] extraUsings, bool hasArgs, string? instanceIds = null)
    {
        var usings = new List<string>(DefaultUsings);
        foreach (var u in extraUsings)
        {
            // Support both multiple -u flags and comma-separated values
            foreach (var part in u.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0 && !usings.Contains(trimmed))
                    usings.Add(trimmed);
            }
        }

        // Lift leading `using X.Y;` lines out of the expression and into the
        // generated using block. Agents routinely prepend these to eval
        // expressions; without extraction they end up inside Main() as
        // using-statements and fail to compile.
        expression = ExtractLeadingUsingDirectives(expression, usings);

        var usingBlock = string.Join("\n", usings.Select(u => $"using {u};"));
        // Alias `Object` to UnityEngine.Object so bare `Object.FindFirstObjectByType<T>()`
        // compiles — otherwise it's ambiguous between UnityEngine.Object and System.Object.
        usingBlock += "\nusing Object = UnityEngine.Object;";
        var signature = hasArgs ? "public static object Main(string[] args)" : "public static object Main()";

        var body = BuildEvalBody(expression);

        var preamble = "";
        if (!string.IsNullOrWhiteSpace(instanceIds))
        {
            var ids = ParseInstanceIds(instanceIds);
            preamble = BuildInstanceIdPreamble(ids);
        }

        return usingBlock + "\n\npublic class Script\n{\n    " + signature + "\n    {\n        " + preamble + body + "\n    }\n}\n";
    }

    /// <summary>
    /// Decide whether to auto-wrap an eval expression as <c>return &lt;expr&gt;;</c>
    /// or inline it as a statement body. The rules, in priority order:
    ///   1. If it starts with `return ` — already explicit, inline as body and
    ///      ensure a trailing `;`. Prevents `return return ...;` when the agent
    ///      writes an explicit return without a trailing semicolon.
    ///   2. If it contains any `;` in code context (not inside a string or char
    ///      literal or line comment) — treat as a multi-statement body. Inline
    ///      and ensure a trailing `;`. Catches `var p = X; try { ... }` and
    ///      similar mixes that the old "ends-with-;" heuristic missed.
    ///   3. Otherwise it's a single expression — wrap as `return &lt;expr&gt;;`.
    /// </summary>
    internal static string BuildEvalBody(string expression)
    {
        var trimmedStart = expression.TrimStart();

        // Rule 1: agent already wrote `return ...`
        if (StartsWithReturnKeyword(trimmedStart))
        {
            return EnsureTrailingSemicolon(expression);
        }

        // Rule 2: any semicolon in code context → body mode
        if (ContainsCodeSemicolon(expression))
        {
            return EnsureTrailingSemicolon(expression);
        }

        // Rule 3: single expression
        return "return " + expression + ";";
    }

    private static bool StartsWithReturnKeyword(string trimmedStart)
    {
        const string kw = "return";
        if (trimmedStart.Length < kw.Length) return false;
        if (!trimmedStart.StartsWith(kw, StringComparison.Ordinal)) return false;
        // Must be followed by whitespace, '(', or end-of-string — otherwise
        // `returnValue` (an identifier that happens to start with "return")
        // would match.
        if (trimmedStart.Length == kw.Length) return true;
        var next = trimmedStart[kw.Length];
        return char.IsWhiteSpace(next) || next == '(';
    }

    private static string EnsureTrailingSemicolon(string expression)
    {
        var trimmed = expression.TrimEnd();
        if (trimmed.EndsWith(";", StringComparison.Ordinal)) return expression;
        return trimmed + ";";
    }

    /// <summary>
    /// Scan <paramref name="expression"/> for a `;` in code context, ignoring
    /// semicolons inside string literals (regular, verbatim, interpolated),
    /// char literals, and line comments. Used to distinguish a single
    /// expression (no code-context `;`) from a multi-statement body.
    /// </summary>
    internal static bool ContainsCodeSemicolon(string expression)
    {
        // State machine over the characters. Modes:
        //   N  — normal code
        //   S  — regular string "..."
        //   V  — verbatim string @"..."
        //   C  — char literal '...'
        //   L  — line comment //...
        // Interpolated strings ($ and $@) are tokenised like their
        // non-interpolated counterparts — sub-expressions inside {…} are
        // rare in eval, and anything there wouldn't legally contain `;`
        // anyway.
        const char N = 'N', S = 'S', V = 'V', C = 'C', L = 'L';
        var mode = N;
        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            var next = i + 1 < expression.Length ? expression[i + 1] : '\0';
            switch (mode)
            {
                case N:
                    if (ch == ';') return true;
                    if (ch == '"') mode = S;
                    else if (ch == '@' && next == '"') { mode = V; i++; }
                    else if (ch == '$' && next == '"') { mode = S; i++; }
                    else if (ch == '$' && next == '@' && i + 2 < expression.Length && expression[i + 2] == '"') { mode = V; i += 2; }
                    else if (ch == '\'') mode = C;
                    else if (ch == '/' && next == '/') { mode = L; i++; }
                    break;
                case S:
                    if (ch == '\\' && next != '\0') i++; // skip escape
                    else if (ch == '"') mode = N;
                    break;
                case V:
                    if (ch == '"' && next == '"') i++; // escaped quote in verbatim
                    else if (ch == '"') mode = N;
                    break;
                case C:
                    if (ch == '\\' && next != '\0') i++;
                    else if (ch == '\'') mode = N;
                    break;
                case L:
                    if (ch == '\n') mode = N;
                    break;
            }
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex LeadingUsingLine =
        new(@"^\s*using\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*;",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Extract leading <c>using X.Y;</c> directives from <paramref name="expression"/>
    /// and append them to <paramref name="usings"/>. Aliased usings
    /// (<c>using X = Y;</c>), using-statements for disposables
    /// (<c>using (var x = ...)</c>), and any other forms are left in place.
    /// Returns the expression with the extracted lines removed.
    /// </summary>
    internal static string ExtractLeadingUsingDirectives(string expression, List<string> usings)
    {
        while (true)
        {
            var m = LeadingUsingLine.Match(expression);
            if (!m.Success) break;
            var ns = m.Groups[1].Value;
            if (!usings.Contains(ns)) usings.Add(ns);
            expression = expression.Substring(m.Length);
        }
        return expression;
    }

    internal static int[] ParseInstanceIds(string idArg)
    {
        var parts = idArg.Split(',');
        var ids = new List<int>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            if (!int.TryParse(trimmed, out var id))
                throw new ArgumentException($"Invalid instance ID: '{trimmed}' — must be an integer");
            ids.Add(id);
        }
        if (ids.Count == 0)
            throw new ArgumentException("No valid instance IDs provided");
        return ids.ToArray();
    }

    internal static string BuildInstanceIdPreamble(int[] ids)
    {
        const string pad = "\n        ";
        var sb = new System.Text.StringBuilder();

        if (ids.Length == 1)
        {
            sb.Append($"var target = (GameObject)UnityEditor.EditorUtility.InstanceIDToObject({ids[0]});");
            sb.Append($"{pad}if (target == null) throw new System.Exception(\"Object {ids[0]} not found (destroyed?)\");");
        }
        else
        {
            sb.Append($"var targets = new GameObject[{ids.Length}];");
            for (int i = 0; i < ids.Length; i++)
            {
                sb.Append($"{pad}targets[{i}] = (GameObject)UnityEditor.EditorUtility.InstanceIDToObject({ids[i]});");
                sb.Append($"{pad}if (targets[{i}] == null) throw new System.Exception(\"Object {ids[i]} not found (destroyed?)\");");
            }
        }

        sb.Append(pad);
        return sb.ToString();
    }

    internal static void DisplayScriptResult(InvocationContext context, ResponseMessage response, bool json, bool isEval = false, string? generatedCode = null)
    {
        var result = JsonConvert.DeserializeObject<ScriptExecuteResult>(
            JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
            JsonHelper.Settings
        );

        if (result != null && !result.Success)
        {
            context.ExitCode = 1;
        }

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(response.Result));
        }
        else
        {
            if (result != null)
            {
                if (result.Success)
                {
                    Console.WriteLine($"Result: {result.Result ?? "(void)"}");
                }
                else
                {
                    Console.Error.WriteLine($"Execution failed: {result.Error}");
                    if (result.Diagnostics != null && result.Diagnostics.Length > 0)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Diagnostics:");
                        foreach (var diagnostic in result.Diagnostics)
                        {
                            Console.Error.WriteLine($"  {diagnostic}");
                        }

                        if (generatedCode != null)
                        {
                            Console.Error.WriteLine();
                            Console.Error.WriteLine("Generated source:");
                            var lines = generatedCode.Split('\n');
                            for (var i = 0; i < lines.Length; i++)
                            {
                                Console.Error.WriteLine($"  {i + 1,3}| {lines[i].TrimEnd('\r')}");
                            }
                        }
                    }

                    if (result.Hints != null && result.Hints.Length > 0)
                    {
                        Console.Error.WriteLine();
                        foreach (var hint in result.Hints)
                        {
                            Console.Error.WriteLine(hint);
                        }
                    }

                    if (isEval)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Hint: Expressions are wrapped in 'return <expr>;' automatically.");
                        Console.Error.WriteLine("  For multi-statement code (ending with ;), use explicit 'return' to return a value.");
                        Console.Error.WriteLine("  Example: unityctl script eval 'var x = 42; return x;'");
                    }
                }
            }
        }
    }
}
