using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class PlayCommands
{
    public static Command CreateCommand()
    {
        var playCommand = new Command("play", "Play mode operations");

        // play enter
        var enterCommand = new Command("enter", "Enter play mode");
        enterCommand.SetHandler(async (InvocationContext context) =>
            await HandlePlayCommand(context, UnityCtlCommands.PlayEnter));

        // play exit
        var exitCommand = new Command("exit", "Exit play mode");
        exitCommand.SetHandler(async (InvocationContext context) =>
            await HandlePlayCommand(context, UnityCtlCommands.PlayExit));

        // play toggle
        var toggleCommand = new Command("toggle", "Toggle play mode");
        toggleCommand.SetHandler(async (InvocationContext context) =>
            await HandlePlayCommand(context, UnityCtlCommands.PlayToggle));

        // play status
        var statusCommand = new Command("status", "Get play mode status");
        statusCommand.SetHandler(async (InvocationContext context) =>
            await HandlePlayCommand(context, UnityCtlCommands.PlayStatus));

        playCommand.AddCommand(enterCommand);
        playCommand.AddCommand(exitCommand);
        playCommand.AddCommand(toggleCommand);
        playCommand.AddCommand(statusCommand);
        return playCommand;
    }

    private static async Task HandlePlayCommand(InvocationContext context, string command)
    {
        var projectPath = ContextHelper.GetProjectPath(context);
        var agentId = ContextHelper.GetAgentId(context);
        var json = ContextHelper.GetJson(context);

        var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
        if (client == null) { context.ExitCode = 1; return; }

        var response = await client.SendCommandAsync(command);
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
            var result = JsonConvert.DeserializeObject<PlayModeResult>(
                JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                JsonHelper.Settings
            );

            if (result != null)
            {
                Console.WriteLine($"Play mode: {result.State}");
            }
        }
    }
}
