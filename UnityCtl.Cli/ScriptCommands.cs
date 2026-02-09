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

        var codeOption = new Option<string?>(
            aliases: ["--code", "-c"],
            description: "C# code to execute (must define a class with a static method)"
        );

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

        executeCommand.AddOption(codeOption);
        executeCommand.AddOption(fileOption);
        executeCommand.AddOption(classOption);
        executeCommand.AddOption(methodOption);
        executeCommand.AddArgument(scriptArgsArgument);

        executeCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var code = context.ParseResult.GetValueForOption(codeOption);
            var file = context.ParseResult.GetValueForOption(fileOption);
            var className = context.ParseResult.GetValueForOption(classOption) ?? "Script";
            var methodName = context.ParseResult.GetValueForOption(methodOption) ?? "Main";

            // Determine code source: --code, --file, or stdin
            string? csharpCode = null;

            if (!string.IsNullOrEmpty(code))
            {
                csharpCode = code;
            }
            else if (file != null)
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
                Console.Error.WriteLine("Error: No C# code provided. Use --code, --file, or pipe code via stdin.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Example:");
                Console.Error.WriteLine("  unityctl script execute -c \"public class Script { public static object Main() { return 42; } }\"");
                Console.Error.WriteLine("  unityctl script execute -f ./MyScript.cs");
                Console.Error.WriteLine("  unityctl script execute -f ./MyScript.cs -- arg1 arg2 \"arg with spaces\"");
                Console.Error.WriteLine("  cat MyScript.cs | unityctl script execute");
                context.ExitCode = 1;
                return;
            }

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var scriptArgs = context.ParseResult.GetValueForArgument(scriptArgsArgument);

            var args = new Dictionary<string, object?>
            {
                { "code", csharpCode },
                { "className", className },
                { "methodName", methodName },
                { "scriptArgs", scriptArgs }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptExecute, args);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            DisplayScriptResult(context, response, json);
        });

        scriptCommand.AddCommand(executeCommand);

        // script eval
        var evalCommand = new Command("eval", "Evaluate a C# expression in the Unity Editor");

        var expressionArgument = new Argument<string>(
            name: "expression",
            description: "C# expression or method body to evaluate"
        );

        var usingOption = new Option<string[]>(
            aliases: ["--using", "-u"],
            description: "Additional using directives (e.g., UnityEngine.UI)",
            getDefaultValue: () => Array.Empty<string>()
        );

        var evalScriptArgsArgument = new Argument<string[]>(
            name: "script-args",
            description: "Arguments to pass to Main (after --)",
            getDefaultValue: () => Array.Empty<string>()
        );

        evalCommand.AddArgument(expressionArgument);
        evalCommand.AddOption(usingOption);
        evalCommand.AddArgument(evalScriptArgsArgument);

        evalCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var expression = context.ParseResult.GetValueForArgument(expressionArgument);
            var extraUsings = context.ParseResult.GetValueForOption(usingOption) ?? Array.Empty<string>();
            var scriptArgs = context.ParseResult.GetValueForArgument(evalScriptArgsArgument);

            if (string.IsNullOrWhiteSpace(expression))
            {
                Console.Error.WriteLine("Error: Expression cannot be empty.");
                context.ExitCode = 1;
                return;
            }

            var hasArgs = scriptArgs.Length > 0;
            var csharpCode = BuildEvalCode(expression, extraUsings, hasArgs);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "code", csharpCode },
                { "className", "Script" },
                { "methodName", "Main" },
                { "scriptArgs", scriptArgs }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptExecute, args);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            DisplayScriptResult(context, response, json);
        });

        scriptCommand.AddCommand(evalCommand);

        return scriptCommand;
    }

    internal static string BuildEvalCode(string expression, string[] extraUsings, bool hasArgs)
    {
        var usings = new List<string>(DefaultUsings);
        foreach (var u in extraUsings)
        {
            if (!usings.Contains(u))
                usings.Add(u);
        }

        var usingBlock = string.Join("\n", usings.Select(u => $"using {u};"));
        var signature = hasArgs ? "public static object Main(string[] args)" : "public static object Main()";
        var isBodyMode = expression.Contains(';');
        var body = isBodyMode ? expression : $"return {expression};";

        return usingBlock + "\n\npublic class Script\n{\n    " + signature + "\n    {\n        " + body + "\n    }\n}\n";
    }

    private static void DisplayScriptResult(InvocationContext context, ResponseMessage response, bool json)
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
                    }
                }
            }
        }
    }
}
