using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;

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

    public static bool? GetResultBool(ResponseMessage response, string key)
    {
        if (response.Result == null) return null;
        var resultJson = JsonConvert.SerializeObject(response.Result, JsonHelper.Settings);
        var result = JObject.Parse(resultJson);
        return result[key]?.Value<bool>();
    }

    /// <summary>
    /// If response.Result contains compilation errors/warnings, print them to stderr.
    /// Used by asset refresh and play enter when compilation fails.
    /// </summary>
    public static void DisplayCompilationErrors(ResponseMessage response)
    {
        if (response.Result == null)
            return;

        var resultJson = JsonConvert.SerializeObject(response.Result, JsonHelper.Settings);
        var result = JObject.Parse(resultJson);

        var errors = result["errors"]?.ToObject<CompilationMessageInfo[]>();
        var warnings = result["warnings"]?.ToObject<CompilationMessageInfo[]>();

        if (errors != null && errors.Length > 0)
        {
            foreach (var e in errors)
                Console.Error.WriteLine($"  {e.File}({e.Line},{e.Column}): {e.Message}");
        }

        if (warnings != null && warnings.Length > 0)
        {
            foreach (var w in warnings)
                Console.Error.WriteLine($"  {w.File}({w.Line},{w.Column}): warning: {w.Message}");
        }
    }
}
