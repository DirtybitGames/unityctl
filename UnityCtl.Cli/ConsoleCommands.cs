using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class ConsoleCommands
{
    public static Command CreateCommand()
    {
        var consoleCommand = new Command("console", "Console log operations");

        var tailCommand = new Command("tail", "Show recent console logs");
        var linesOption = new Option<int>("--lines", getDefaultValue: () => 50, "Number of lines to show");
        tailCommand.AddOption(linesOption);

        tailCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var lines = context.ParseResult.GetValueForOption(linesOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var result = await client.GetAsync<ConsoleTailResult>($"/console/tail?lines={lines}");
            if (result == null) return;

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(result));
            }
            else
            {
                foreach (var entry in result.Entries)
                {
                    var levelColor = entry.Level switch
                    {
                        LogLevel.Error => ConsoleColor.Red,
                        LogLevel.Exception => ConsoleColor.Red,
                        LogLevel.Warning => ConsoleColor.Yellow,
                        _ => ConsoleColor.Gray
                    };

                    Console.ForegroundColor = levelColor;
                    Console.Write($"[{entry.Level}] ");
                    Console.ResetColor();
                    Console.WriteLine(entry.Message);

                    if (!string.IsNullOrEmpty(entry.StackTrace))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine(entry.StackTrace);
                        Console.ResetColor();
                    }
                }
            }
        });

        consoleCommand.AddCommand(tailCommand);
        return consoleCommand;
    }
}
