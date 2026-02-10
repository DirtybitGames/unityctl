using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class RecordCommands
{
    public static Command CreateCommand()
    {
        var recordCommand = new Command("record", "Video recording operations (requires Unity Recorder package)");

        // record start
        var startCommand = new Command("start", "Start recording video of the game view");
        var outputOption = new Option<string?>("--output", "Output filename (without extension, defaults to timestamp-based name)");
        var durationOption = new Option<double?>("--duration", "Recording duration in seconds (blocks until done). Omit for manual stop.");
        var widthOption = new Option<int?>("--width", "Override width (default: game view width)");
        var heightOption = new Option<int?>("--height", "Override height (default: game view height)");
        var fpsOption = new Option<int>("--fps", () => 30, "Frames per second (default: 30)");

        startCommand.AddOption(outputOption);
        startCommand.AddOption(durationOption);
        startCommand.AddOption(widthOption);
        startCommand.AddOption(heightOption);
        startCommand.AddOption(fpsOption);

        startCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var output = context.ParseResult.GetValueForOption(outputOption);
            var duration = context.ParseResult.GetValueForOption(durationOption);
            var width = context.ParseResult.GetValueForOption(widthOption);
            var height = context.ParseResult.GetValueForOption(heightOption);
            var fps = context.ParseResult.GetValueForOption(fpsOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var resolvedProjectPath = projectPath ?? ProjectLocator.FindProjectRoot()!;

            var args = new Dictionary<string, object?>
            {
                { "outputName", output },
                { "duration", duration },
                { "width", width },
                { "height", height },
                { "fps", fps }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.RecordStart, args);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(response.Result));
                }
                else
                {
                    Console.Error.WriteLine($"Error: {response.Error?.Message}");
                    ContextHelper.DisplayCompilationErrors(response);
                }
                context.ExitCode = 1;
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(response.Result));
            }
            else if (duration.HasValue)
            {
                // Duration-based: bridge waited for completion and returns finished payload
                var result = JsonConvert.DeserializeObject<RecordFinishedPayload>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    var absolutePath = Path.GetFullPath(Path.Combine(resolvedProjectPath, result.OutputPath));
                    var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, absolutePath);
                    Console.WriteLine($"Recording saved: {relativePath}");
                    Console.WriteLine($"Duration: {result.Duration:F1}s ({result.FrameCount} frames)");
                }
            }
            else
            {
                // Fire-and-forget: returns start result
                var result = JsonConvert.DeserializeObject<RecordStartResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    var absolutePath = Path.GetFullPath(Path.Combine(resolvedProjectPath, result.OutputPath));
                    var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, absolutePath);
                    Console.WriteLine($"Recording: {relativePath}");
                    Console.WriteLine($"State: {result.State}");
                }
            }
        });

        // record stop
        var stopCommand = new Command("stop", "Stop the current recording");
        stopCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var resolvedProjectPath = projectPath ?? ProjectLocator.FindProjectRoot()!;

            var response = await client.SendCommandAsync(UnityCtlCommands.RecordStop);
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
                var result = JsonConvert.DeserializeObject<RecordStopResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    var absolutePath = Path.GetFullPath(Path.Combine(resolvedProjectPath, result.OutputPath));
                    var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, absolutePath);
                    Console.WriteLine($"Recording saved: {relativePath}");
                    Console.WriteLine($"Duration: {result.Duration:F1}s ({result.FrameCount} frames)");
                }
            }
        });

        // record status
        var statusCommand = new Command("status", "Get current recording status");
        statusCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var response = await client.SendCommandAsync(UnityCtlCommands.RecordStatus);
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
                var result = JsonConvert.DeserializeObject<RecordStatusResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    if (result.IsRecording)
                    {
                        Console.WriteLine($"Recording: {result.OutputPath}");
                        Console.WriteLine($"Elapsed: {result.Elapsed:F1}s ({result.FrameCount} frames)");
                    }
                    else
                    {
                        Console.WriteLine("Not recording");
                    }
                }
            }
        });

        recordCommand.AddCommand(startCommand);
        recordCommand.AddCommand(stopCommand);
        recordCommand.AddCommand(statusCommand);
        return recordCommand;
    }
}
