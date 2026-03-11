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
        // --output/-o alias for the positional path, for agent discoverability (see #25)
        var outputOption = new Option<string?>(["-o", "--output"], "Output path (same as positional argument)");
        var widthOption = new Option<int?>("--width", "Override width (default: game view width)");
        var heightOption = new Option<int?>("--height", "Override height (default: game view height)");

        captureCommand.AddArgument(pathArg);
        captureCommand.AddOption(outputOption);
        captureCommand.AddOption(widthOption);
        captureCommand.AddOption(heightOption);

        captureCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var path = context.ParseResult.GetValueForOption(outputOption)
                       ?? context.ParseResult.GetValueForArgument(pathArg);
            var width = context.ParseResult.GetValueForOption(widthOption);
            var height = context.ParseResult.GetValueForOption(heightOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            // Resolve user-provided path to absolute (relative to CWD) before sending to Unity.
            // This eliminates ambiguity about which directory "relative" means.
            if (path != null)
            {
                path = Path.GetFullPath(path);
            }

            var args = new Dictionary<string, object?>
            {
                { "path", path },
                { "width", width },
                { "height", height }
            };

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.ScreenshotCapture, args, timeout);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
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
                    // Unity returns an absolute path; wait for the file to exist
                    // since ScreenCapture.CaptureScreenshot writes asynchronously at end-of-frame
                    var absolutePath = result.Path;
                    for (int i = 0; i < 50; i++)
                    {
                        if (File.Exists(absolutePath)) break;
                        await Task.Delay(100);
                    }

                    var displayPath = ContextHelper.FormatPath(absolutePath);
                    Console.WriteLine($"Screenshot captured: {displayPath}");
                    Console.WriteLine($"Resolution: {result.Width}x{result.Height}");
                }
            }
        });

        screenshotCommand.AddCommand(captureCommand);
        return screenshotCommand;
    }
}
