using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class UICommands
{
    public static Command CreateCommand()
    {
        var uiCommand = new Command("ui", "UI interaction commands (play mode)");
        uiCommand.AddCommand(CreateClickCommand());
        return uiCommand;
    }

    private static Command CreateClickCommand()
    {
        var clickCommand = new Command("click", "Click a UI element by instance ID or screen coordinates");

        var idOption = new Option<int?>("--id", "Instance ID of the UI element to click");
        var xArg = new Argument<int?>("x", () => null, "Screen X coordinate");
        var yArg = new Argument<int?>("y", () => null, "Screen Y coordinate");

        clickCommand.AddOption(idOption);
        clickCommand.AddArgument(xArg);
        clickCommand.AddArgument(yArg);

        clickCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var id = context.ParseResult.GetValueForOption(idOption);
            var x = context.ParseResult.GetValueForArgument(xArg);
            var y = context.ParseResult.GetValueForArgument(yArg);

            if (id.HasValue && (x.HasValue || y.HasValue))
            {
                Console.Error.WriteLine("Error: Use either --id or x y coordinates, not both");
                context.ExitCode = 1;
                return;
            }
            if (!id.HasValue && (!x.HasValue || !y.HasValue))
            {
                Console.Error.WriteLine("Error: Provide --id <instanceId> or <x> <y> coordinates");
                context.ExitCode = 1;
                return;
            }

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>();
            if (id.HasValue)
                args["id"] = id.Value;
            else
            {
                args["x"] = x!.Value;
                args["y"] = y!.Value;
            }

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.UIClick, args, timeout);
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
                var result = JsonConvert.DeserializeObject<UIClickResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    var text = !string.IsNullOrEmpty(result.Text) ? $" \"{result.Text}\"" : "";
                    Console.WriteLine($"Clicked {result.Name} [i:{result.InstanceId}]{text} at {result.ScreenPosition}");
                }
            }
        });

        return clickCommand;
    }
}
