using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class BuildCommands
{
    private static readonly string[] DefaultUsings =
    [
        "System",
        "System.Linq",
        "UnityEngine",
        "UnityEditor",
        "UnityEditor.Build.Reporting"
    ];

    public static Command CreateCommand()
    {
        var buildCommand = new Command("build", "Build operations");

        // build player
        var playerCommand = new Command("player", "Build the Unity player (standalone, mobile, WebGL, etc.)");

        var targetOption = new Option<string?>(
            aliases: ["--target", "-t"],
            description: "Build target (e.g., StandaloneWindows64, StandaloneLinux64, StandaloneOSX, Android, iOS, WebGL)"
        );

        var outputOption = new Option<string?>(
            aliases: ["--output", "-o"],
            description: "Output path for the build"
        );

        var scenesOption = new Option<string[]>(
            aliases: ["--scenes", "-s"],
            description: "Scene paths to include (e.g., Assets/Scenes/Main.unity). Defaults to scenes enabled in Build Settings.",
            getDefaultValue: () => Array.Empty<string>()
        );

        var codeOption = new Option<string?>(
            aliases: ["--code", "-c"],
            description: "Custom C# build code (replaces standard BuildPipeline.BuildPlayer call)"
        );

        var fileOption = new Option<FileInfo?>(
            aliases: ["--file", "-f"],
            description: "Read custom C# build code from a file"
        );

        playerCommand.AddOption(targetOption);
        playerCommand.AddOption(outputOption);
        playerCommand.AddOption(scenesOption);
        playerCommand.AddOption(codeOption);
        playerCommand.AddOption(fileOption);

        playerCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var target = context.ParseResult.GetValueForOption(targetOption);
            var output = context.ParseResult.GetValueForOption(outputOption);
            var scenes = context.ParseResult.GetValueForOption(scenesOption) ?? Array.Empty<string>();
            var code = context.ParseResult.GetValueForOption(codeOption);
            var file = context.ParseResult.GetValueForOption(fileOption);

            // Determine build code source
            string? buildCode = null;

            if (!string.IsNullOrEmpty(code))
            {
                buildCode = code;
            }
            else if (file != null)
            {
                if (!file.Exists)
                {
                    Console.Error.WriteLine($"Error: File not found: {file.FullName}");
                    context.ExitCode = 1;
                    return;
                }
                buildCode = await File.ReadAllTextAsync(file.FullName);
            }
            else if (Console.IsInputRedirected && string.IsNullOrEmpty(target))
            {
                // Read custom code from stdin only when no --target is specified
                buildCode = await Console.In.ReadToEndAsync();
            }

            string csharpCode;

            if (!string.IsNullOrWhiteSpace(buildCode))
            {
                // Custom build code — wrap if needed (same logic as script eval)
                csharpCode = BuildCustomCode(buildCode);
            }
            else
            {
                // Standard build — requires --target and --output
                if (string.IsNullOrEmpty(target))
                {
                    Console.Error.WriteLine("Error: --target is required for standard builds.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Example:");
                    Console.Error.WriteLine("  unityctl build player --target StandaloneLinux64 --output ./Builds/MyGame");
                    Console.Error.WriteLine("  unityctl build player --target WebGL --output ./Builds/WebGL --scenes Assets/Scenes/Main.unity");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("For custom builds:");
                    Console.Error.WriteLine("  unityctl build player --code \"var report = BuildPipeline.BuildPlayer(...); return report.summary.result.ToString();\"");
                    Console.Error.WriteLine("  unityctl build player --file ./Editor/MyBuildScript.cs");
                    context.ExitCode = 1;
                    return;
                }

                if (string.IsNullOrEmpty(output))
                {
                    Console.Error.WriteLine("Error: --output is required for standard builds.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Example:");
                    Console.Error.WriteLine("  unityctl build player --target StandaloneLinux64 --output ./Builds/MyGame");
                    context.ExitCode = 1;
                    return;
                }

                csharpCode = BuildStandardCode(target, output, scenes);
            }

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "code", csharpCode },
                { "className", "Script" },
                { "methodName", "Main" }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.ScriptExecute, args, timeoutSeconds: 600);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            DisplayBuildResult(context, response, json);
        });

        buildCommand.AddCommand(playerCommand);
        return buildCommand;
    }

    internal static string BuildStandardCode(string target, string output, string[] scenes)
    {
        var usingBlock = string.Join("\n", DefaultUsings.Select(u => $"using {u};"));

        // If no scenes specified, use scenes from Build Settings
        string scenesCode;
        if (scenes.Length > 0)
        {
            var sceneArray = string.Join(", ", scenes.Select(s => $"\"{EscapeCSharpString(s)}\""));
            scenesCode = $"var scenes = new[] {{ {sceneArray} }};";
        }
        else
        {
            scenesCode = "var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();";
        }

        return $@"{usingBlock}

public class Script
{{
    public static object Main()
    {{
        {scenesCode}

        var options = new BuildPlayerOptions
        {{
            scenes = scenes,
            locationPathName = ""{EscapeCSharpString(output)}"",
            target = BuildTarget.{target},
            options = BuildOptions.None
        }};

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        return new
        {{
            result = summary.result.ToString(),
            totalErrors = summary.totalErrors,
            totalWarnings = summary.totalWarnings,
            totalSize = summary.totalSize,
            totalTime = summary.totalTime.TotalSeconds,
            outputPath = summary.outputPath
        }};
    }}
}}
";
    }

    internal static string BuildCustomCode(string code)
    {
        // If the code already contains a class definition, use it as-is
        if (code.Contains("class "))
        {
            return code;
        }

        // Otherwise, wrap it like script eval does
        var usingBlock = string.Join("\n", DefaultUsings.Select(u => $"using {u};"));
        var isBodyMode = code.TrimEnd().EndsWith(';');
        var body = isBodyMode ? code : $"return {code};";

        return $@"{usingBlock}

public class Script
{{
    public static object Main()
    {{
        {body}
    }}
}}
";
    }

    private static string EscapeCSharpString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static void DisplayBuildResult(InvocationContext context, ResponseMessage response, bool json)
    {
        // The result is a ScriptExecuteResult wrapping the build output
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
            if (result == null) return;

            if (result.Success)
            {
                Console.WriteLine($"Build result: {result.Result ?? "(void)"}");
            }
            else
            {
                Console.Error.WriteLine($"Build failed: {result.Error}");
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
