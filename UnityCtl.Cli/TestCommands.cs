using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class TestCommands
{
    public static Command CreateCommand()
    {
        var testCommand = new Command("test", "Unity test runner operations");

        // test run
        var runCommand = new Command("run", "Run Unity tests");

        var modeOption = new Option<string>(
            "--mode",
            getDefaultValue: () => "editmode",
            description: "Test mode: editmode or playmode"
        );
        runCommand.AddOption(modeOption);

        var filterOption = new Option<string?>(
            "--filter",
            description: "Filter tests by name pattern"
        );
        runCommand.AddOption(filterOption);

        runCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var mode = context.ParseResult.GetValueForOption(modeOption);
            var filter = context.ParseResult.GetValueForOption(filterOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            // Normalize mode to lowercase for consistency
            mode = mode?.ToLower() ?? "editmode";
            if (mode != "editmode" && mode != "playmode")
            {
                Console.Error.WriteLine($"Error: Invalid mode '{mode}'. Must be 'editmode' or 'playmode'.");
                return;
            }

            var args = new Dictionary<string, object?>
            {
                { "mode", mode },
                { "filter", filter }
            };

            if (!json)
            {
                Console.WriteLine("Running tests...");
            }

            var response = await client.SendCommandAsync(UnityCtlCommands.TestRun, args);
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
                var result = JsonConvert.DeserializeObject<TestFinishedPayload>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    Console.WriteLine($"Tests completed in {result.Duration:F1}s");
                    Console.WriteLine($"Passed: {result.Passed}, Failed: {result.Failed}, Skipped: {result.Skipped}");
                    Console.WriteLine();

                    if (result.Failed > 0 && result.Failures != null && result.Failures.Length > 0)
                    {
                        Console.WriteLine("Failed Tests:");
                        foreach (var failure in result.Failures)
                        {
                            Console.WriteLine($"  {failure.TestName}");
                            Console.WriteLine($"    {failure.Message}");
                            if (!string.IsNullOrEmpty(failure.StackTrace))
                            {
                                // Print first line of stack trace
                                var stackLines = failure.StackTrace.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                if (stackLines.Length > 0)
                                {
                                    Console.WriteLine($"    {stackLines[0].Trim()}");
                                }
                            }
                            Console.WriteLine();
                        }
                    }
                }
            }
        });

        testCommand.AddCommand(runCommand);
        return testCommand;
    }
}
