using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class AssetCommands
{
    public static Command CreateCommand()
    {
        var assetCommand = new Command("asset", "Asset management operations");

        // asset import
        var importCommand = new Command("import", "Import a specific asset");
        var pathArg = new Argument<string>("path", "Asset path (e.g., Assets/Textures/logo.png)");
        importCommand.AddArgument(pathArg);

        importCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var path = context.ParseResult.GetValueForArgument(pathArg);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var args = new Dictionary<string, object?> { { "path", path } };
            var response = await client.SendCommandAsync(UnityCtlCommands.AssetImport, args);
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
                Console.WriteLine($"Asset imported: {path}");
            }
        });

        assetCommand.AddCommand(importCommand);

        // asset refresh
        var refreshCommand = new Command("refresh", "Refresh all assets (like focusing the editor)");
        refreshCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var response = await client.SendCommandAsync(UnityCtlCommands.AssetRefresh, null);
            if (response == null) return;

            if (json)
            {
                // Always output JSON, including errors
                Console.WriteLine(JsonHelper.Serialize(response.Result));
                if (response.Status == ResponseStatus.Error)
                {
                    Environment.ExitCode = 1;
                }
            }
            else if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                Environment.ExitCode = 1;
            }
            else
            {
                Console.WriteLine("Asset refresh completed");
            }
        });
        assetCommand.AddCommand(refreshCommand);

        return assetCommand;
    }
}
