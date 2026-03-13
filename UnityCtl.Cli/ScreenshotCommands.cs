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

            // Resolve user path to absolute (relative to CWD) — we'll move the file here after capture.
            // Send only the filename to Unity; it always captures to Screenshots/<filename>.
            string? desiredPath = path != null ? Path.GetFullPath(path) : null;
            var filename = desiredPath != null ? Path.GetFileName(desiredPath) : null;

            var args = new Dictionary<string, object?>
            {
                { "filename", filename },
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
                    // Unity captures to a project-relative path (e.g. Screenshots/shot.png).
                    // Resolve it against the project root to get the absolute capture path.
                    var resolvedProjectPath = projectPath ?? ProjectLocator.FindProjectRoot()!;
                    var capturePath = Path.GetFullPath(Path.Combine(resolvedProjectPath, result.Path));

                    // CaptureScreenshot writes asynchronously at end-of-frame — wait for the file.
                    for (int i = 0; i < 50; i++)
                    {
                        if (File.Exists(capturePath)) break;
                        await Task.Delay(100);
                    }

                    // Determine the final path: user's desired location, or the capture path itself.
                    var finalPath = desiredPath ?? capturePath;

                    if (!File.Exists(capturePath))
                    {
                        Console.Error.WriteLine($"Warning: screenshot file not written after 5s: {capturePath}");
                    }
                    else if (finalPath != capturePath)
                    {
                        // Move from Screenshots/ to user's desired destination
                        var directory = Path.GetDirectoryName(finalPath);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                            Directory.CreateDirectory(directory);

                        File.Move(capturePath, finalPath, overwrite: true);
                    }

                    var displayPath = ContextHelper.FormatPath(finalPath);
                    Console.WriteLine($"Screenshot captured: {displayPath}");
                    Console.WriteLine($"Resolution: {result.Width}x{result.Height}");
                }
            }
        });

        screenshotCommand.AddCommand(captureCommand);

        // screenshot list-windows
        var listWindowsCommand = new Command("list-windows", "List open editor windows");
        listWindowsCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.ScreenshotListWindows, null, timeout);
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
                var result = JsonConvert.DeserializeObject<ScreenshotListWindowsResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result?.Windows != null)
                {
                    // Header
                    Console.WriteLine($"{"Type",-50} {"Title",-25} {"Size",-12} Docked");
                    Console.WriteLine(new string('-', 95));

                    foreach (var w in result.Windows)
                    {
                        Console.WriteLine($"{w.Type,-50} {w.Title,-25} {w.Width}x{w.Height,-6} {(w.Docked ? "yes" : "no")}");
                    }

                    Console.WriteLine($"\n{result.Windows.Length} window(s) open");
                }
            }
        });
        screenshotCommand.AddCommand(listWindowsCommand);

        // screenshot window <window> [output]
        var windowCommand = new Command("window", "Capture a screenshot of a specific editor window");
        var windowArg = new Argument<string>("window", "Window type name or title to capture");
        var windowPathArg = new Argument<string?>("path", () => null, "Output path (optional)");
        var windowOutputOption = new Option<string?>(["-o", "--output"], "Output path (same as positional argument)");

        windowCommand.AddArgument(windowArg);
        windowCommand.AddArgument(windowPathArg);
        windowCommand.AddOption(windowOutputOption);

        windowCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var jsonFlag = ContextHelper.GetJson(context);
            var window = context.ParseResult.GetValueForArgument(windowArg);
            var path = context.ParseResult.GetValueForOption(windowOutputOption)
                       ?? context.ParseResult.GetValueForArgument(windowPathArg);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            string? desiredPath = path != null ? Path.GetFullPath(path) : null;
            var filename = desiredPath != null ? Path.GetFileName(desiredPath) : null;

            var args = new Dictionary<string, object?>
            {
                { "window", window },
                { "filename", filename }
            };

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.ScreenshotWindow, args, timeout);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            if (jsonFlag)
            {
                Console.WriteLine(JsonHelper.Serialize(response.Result));
            }
            else
            {
                var result = JsonConvert.DeserializeObject<ScreenshotWindowResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    var resolvedProjectPath = projectPath ?? ProjectLocator.FindProjectRoot()!;
                    var capturePath = Path.GetFullPath(Path.Combine(resolvedProjectPath, result.Path));

                    // Window capture writes synchronously (File.WriteAllBytes), but give a brief moment
                    for (int i = 0; i < 20; i++)
                    {
                        if (File.Exists(capturePath)) break;
                        await Task.Delay(100);
                    }

                    var finalPath = desiredPath ?? capturePath;

                    if (!File.Exists(capturePath))
                    {
                        Console.Error.WriteLine($"Warning: screenshot file not written after 2s: {capturePath}");
                    }
                    else if (finalPath != capturePath)
                    {
                        var directory = Path.GetDirectoryName(finalPath);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                            Directory.CreateDirectory(directory);

                        File.Move(capturePath, finalPath, overwrite: true);
                    }

                    var displayPath = ContextHelper.FormatPath(finalPath);
                    Console.WriteLine($"Window captured: {displayPath}");
                    Console.WriteLine($"Window: {result.WindowType} (\"{result.WindowTitle}\")");
                    Console.WriteLine($"Resolution: {result.Width}x{result.Height}");
                }
            }
        });
        screenshotCommand.AddCommand(windowCommand);

        return screenshotCommand;
    }
}
