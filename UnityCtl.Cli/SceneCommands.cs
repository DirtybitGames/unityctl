using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class SceneCommands
{
    public static Command CreateCommand()
    {
        var sceneCommand = new Command("scene", "Scene management operations");

        // scene list
        var listCommand = new Command("list", "List available scenes");
        var sourceOption = new Option<string>("--source", getDefaultValue: () => "buildSettings", "Source: buildSettings or all");
        listCommand.AddOption(sourceOption);

        listCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var source = context.ParseResult.GetValueForOption(sourceOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var args = new Dictionary<string, object?> { { "source", source } };
            var response = await client.SendCommandAsync(UnityCtlCommands.SceneList, args);
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
                var result = JsonConvert.DeserializeObject<SceneListResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result?.Scenes != null)
                {
                    Console.WriteLine($"Found {result.Scenes.Length} scene(s):");
                    foreach (var scene in result.Scenes)
                    {
                        var enabled = scene.EnabledInBuild ? "✓" : " ";
                        Console.WriteLine($"  [{enabled}] {scene.Path}");
                    }
                }
            }
        });

        // scene load
        var loadCommand = new Command("load", "Load a scene");
        var pathArg = new Argument<string>("path", "Scene path (e.g., Assets/Scenes/Main.unity)");
        var modeOption = new Option<string>("--mode", getDefaultValue: () => "single", "Mode: single or additive");
        loadCommand.AddArgument(pathArg);
        loadCommand.AddOption(modeOption);

        loadCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var path = context.ParseResult.GetValueForArgument(pathArg);
            var mode = context.ParseResult.GetValueForOption(modeOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var args = new Dictionary<string, object?>
            {
                { "path", path },
                { "mode", mode }
            };
            var response = await client.SendCommandAsync(UnityCtlCommands.SceneLoad, args);
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
                Console.WriteLine($"Scene loaded: {path}");
            }
        });

        sceneCommand.AddCommand(listCommand);
        sceneCommand.AddCommand(loadCommand);
        return sceneCommand;
    }
}
