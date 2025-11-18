using System;
using System.CommandLine;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class CompileCommands
{
    public static Command CreateCommand()
    {
        var compileCommand = new Command("compile", "Compilation operations");

        // compile scripts
        var scriptsCommand = new Command("scripts", "Trigger script compilation");

        scriptsCommand.SetHandler(async (string? projectPath, string? agentId, bool json) =>
        {
            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var response = await client.SendCommandAsync(UnityCtlCommands.CompileScripts);
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
                Console.WriteLine("Script compilation triggered");
            }
        },
        new ProjectBinder(),
        new AgentIdBinder(),
        new JsonBinder());

        compileCommand.AddCommand(scriptsCommand);
        return compileCommand;
    }
}
