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

    private static int KindSortOrder(string kind) => kind switch
    {
        "property" => 0,
        "field" => 1,
        "method" => 2,
        "event" => 3,
        "nested-type" => 4,
        _ => 5
    };

    private static string CapitalizeKind(string kind) => kind switch
    {
        "property" => "Properties",
        "field" => "Fields",
        "method" => "Methods",
        "event" => "Events",
        "nested-type" => "Nested types",
        _ => kind
    };

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

        var usingBlock = string.Join("\n", usings.Select(u => $"using {u};"));
        // Alias `Object` to UnityEngine.Object so bare `Object.FindFirstObjectByType<T>()`
        // compiles — otherwise it's ambiguous between UnityEngine.Object and System.Object.
        usingBlock += "\nusing Object = UnityEngine.Object;";
        var signature = hasArgs ? "public static object Main(string[] args)" : "public static object Main()";
        var isBodyMode = expression.TrimEnd().EndsWith(';');
        var body = isBodyMode ? expression : $"return {expression};";

        var preamble = "";
        if (!string.IsNullOrWhiteSpace(instanceIds))
        {
            var ids = ParseInstanceIds(instanceIds);
            preamble = BuildInstanceIdPreamble(ids);
        }

        return usingBlock + "\n\npublic class Script\n{\n    " + signature + "\n    {\n        " + preamble + body + "\n    }\n}\n";
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
