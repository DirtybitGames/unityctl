using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class PrefabCommand
{
    public static Command CreateCommand()
    {
        var prefabCommand = new Command("prefab", "Prefab stage navigation");

        // prefab open <path> [--context N]
        var openCommand = new Command("open", "Open a prefab for editing");
        var pathArg = new Argument<string>("path", "Path to .prefab asset (e.g., Assets/Prefabs/Player.prefab)");
        var contextOption = new Option<int?>("--context", "Instance ID of scene object for in-context editing");
        openCommand.AddArgument(pathArg);
        openCommand.AddOption(contextOption);

        openCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var path = context.ParseResult.GetValueForArgument(pathArg);
            var contextId = context.ParseResult.GetValueForOption(contextOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "path", path },
                { "context", contextId }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.PrefabOpen, args);
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
                var result = JsonConvert.DeserializeObject<PrefabOpenResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    Console.WriteLine($"Opened: {result.PrefabAssetPath}");
                    Console.WriteLine($"Stage: {result.Stage}");
                }
            }
        });

        // prefab close
        var closeCommand = new Command("close", "Return to main scene editing");

        closeCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var response = await client.SendCommandAsync(UnityCtlCommands.PrefabClose);
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
                var result = JsonConvert.DeserializeObject<PrefabCloseResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    Console.WriteLine($"Returned to scene: {result.ReturnedToScene}");
                }
            }
        });

        prefabCommand.AddCommand(openCommand);
        prefabCommand.AddCommand(closeCommand);
        return prefabCommand;
    }
}
