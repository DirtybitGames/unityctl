using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class ConsoleCommands
{
    public static Command CreateCommand()
    {
        var consoleCommand = new Command("console", "Console log operations");

        var tailCommand = new Command("tail", "Show recent console logs");
        var countOption = new Option<int>("--count", getDefaultValue: () => 10, "Number of log entries to show");
        var stackOption = new Option<bool>("--stack", "Show stack traces");
        tailCommand.AddOption(countOption);
        tailCommand.AddOption(stackOption);

        tailCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);
            var count = context.ParseResult.GetValueForOption(countOption);
            var showStack = context.ParseResult.GetValueForOption(stackOption);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            var result = await client.GetAsync<ConsoleTailResult>($"/console/tail?lines={count}");
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

                    var time = DateTime.Parse(entry.Timestamp).ToLocalTime().ToString("HH:mm:ss");
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write($"[{time}] ");
                    Console.ForegroundColor = levelColor;
                    Console.Write($"[{entry.Level}] ");
                    Console.ResetColor();
                    Console.WriteLine(entry.Message);

                    if (showStack && !string.IsNullOrEmpty(entry.StackTrace))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine(entry.StackTrace);
                        Console.ResetColor();
                    }
                }
            }
        });

        var clearCommand = new Command("clear", "Clear the console log buffer");

        clearCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) return;

            // Send clear request
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var result = await client.PostAsync<dynamic>("/console/clear", content);
            if (result == null) return;

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(result));
            }
            else
            {
                Console.WriteLine("Console cleared successfully");
            }
        });

        consoleCommand.AddCommand(tailCommand);
        consoleCommand.AddCommand(clearCommand);
        return consoleCommand;
    }
}
