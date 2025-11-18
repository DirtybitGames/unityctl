using System;
using System.Collections.Generic;
using System.CommandLine;
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

        importCommand.SetHandler(async (string? projectPath, string? agentId, bool json, string path) =>
        {
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
        },
        new ProjectBinder(),
        new AgentIdBinder(),
        new JsonBinder(),
        pathArg);

        assetCommand.AddCommand(importCommand);
        return assetCommand;
    }
}
