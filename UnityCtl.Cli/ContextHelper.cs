using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;

namespace UnityCtl.Cli;

internal static class ContextHelper
{
    public static string? GetProjectPath(InvocationContext context)
    {
        var parseResult = context.ParseResult;
        var rootCommand = parseResult.RootCommandResult.Command;
        var option = rootCommand.Options.FirstOrDefault(o => o.Name == "project") as Option<string>;
        return option != null ? parseResult.GetValueForOption(option) : null;
    }

    public static string? GetAgentId(InvocationContext context)
    {
        var parseResult = context.ParseResult;
        var rootCommand = parseResult.RootCommandResult.Command;
        var option = rootCommand.Options.FirstOrDefault(o => o.Name == "agent-id") as Option<string>;
        return option != null ? parseResult.GetValueForOption(option) : null;
    }

    public static bool GetJson(InvocationContext context)
    {
        var parseResult = context.ParseResult;
        var rootCommand = parseResult.RootCommandResult.Command;
        var option = rootCommand.Options.FirstOrDefault(o => o.Name == "json") as Option<bool>;
        return option != null && parseResult.GetValueForOption(option);
    }
}
