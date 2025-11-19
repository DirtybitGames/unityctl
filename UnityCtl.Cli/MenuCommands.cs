using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class MenuCommands
{
    public static Command CreateCommand()
    {
        var menuCommand = new Command("menu", "Unity menu operations");

        // menu list
        var listCommand = new Command("list", "List all available Unity menu items");

        listCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var args = new Dictionary<string, object?>();
            var response = await client.SendCommandAsync(UnityCtlCommands.MenuList, args);
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
                var result = JsonConvert.DeserializeObject<MenuListResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result?.MenuItems != null)
                {
                    Console.WriteLine($"Found {result.MenuItems.Length} menu item(s):");
                    foreach (var item in result.MenuItems)
                    {
                        Console.WriteLine($"  {item.Path}");
                    }
                }
            }
        });

        // menu execute
        var executeCommand = new Command("execute", "Execute a Unity menu item");
        var pathArg = new Argument<string>("path", "Menu item path (e.g., \"File/Save\" or \"Assets/Refresh\")");
        executeCommand.AddArgument(pathArg);

        executeCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var menuPath = context.ParseResult.GetValueForArgument(pathArg);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var args = new Dictionary<string, object?> { { "menuPath", menuPath } };
            var response = await client.SendCommandAsync(UnityCtlCommands.MenuExecute, args);
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
                var result = JsonConvert.DeserializeObject<MenuExecuteResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    if (result.Success)
                    {
                        Console.WriteLine($"✓ {result.Message}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"✗ {result.Message}");
                    }
                }
            }
        });

        menuCommand.AddCommand(listCommand);
        menuCommand.AddCommand(executeCommand);
        return menuCommand;
    }
}
