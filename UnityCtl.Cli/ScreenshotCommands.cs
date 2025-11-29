using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class ScreenshotCommands
{
    public static Command CreateCommand()
    {
        var screenshotCommand = new Command("screenshot", "Screenshot operations");

        // screenshot capture
        var captureCommand = new Command("capture", "Capture a screenshot of the game view");
        var pathArg = new Argument<string?>("path", () => null, "Output path (optional, defaults to Screenshots/screenshot_YYYY-MM-DD_HH-mm-ss.png)");
        var widthOption = new Option<int?>("--width", "Override width (default: game view width)");
        var heightOption = new Option<int?>("--height", "Override height (default: game view height)");

        captureCommand.AddArgument(pathArg);
        captureCommand.AddOption(widthOption);
        captureCommand.AddOption(heightOption);

        captureCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var path = context.ParseResult.GetValueForArgument(pathArg);
            var width = context.ParseResult.GetValueForOption(widthOption);
            var height = context.ParseResult.GetValueForOption(heightOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            // Resolve project path (same logic as BridgeClient.TryCreateFromProject)
            var resolvedProjectPath = projectPath ?? ProjectLocator.FindProjectRoot()!;

            var args = new Dictionary<string, object?>
            {
                { "path", path },
                { "width", width },
                { "height", height }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.ScreenshotCapture, args);
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
                var result = JsonConvert.DeserializeObject<ScreenshotCaptureResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    var absolutePath = Path.GetFullPath(Path.Combine(resolvedProjectPath, result.Path));
                    Console.WriteLine($"Screenshot captured: {absolutePath}");
                    Console.WriteLine($"Resolution: {result.Width}x{result.Height}");
                }
            }
        });

        screenshotCommand.AddCommand(captureCommand);
        return screenshotCommand;
    }
}
