using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class ScriptCommands
{
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
                return;
            }

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var scriptArgs = context.ParseResult.GetValueForArgument(scriptArgsArgument);

            var args = new Dictionary<string, object?>
            {
                { "code", csharpCode },
                { "className", className },
                { "methodName", methodName },
                { "scriptArgs", scriptArgs }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptExecute, args);
            if (response == null) return;

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(response.Result));
            }
            else
            {
                var result = JsonConvert.DeserializeObject<ScriptExecuteResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

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
        });

        scriptCommand.AddCommand(executeCommand);
        return scriptCommand;
    }
}
