using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

internal static class ContextHelper
{
    public static string? GetProjectPath(InvocationContext context) =>
        GetGlobalOption<string>(context, "project");

    public static string? GetAgentId(InvocationContext context) =>
        GetGlobalOption<string>(context, "agent-id");

    public static bool GetJson(InvocationContext context) =>
        GetGlobalOption<bool>(context, "json");

    public static int? GetTimeout(InvocationContext context) =>
        GetGlobalOption<int?>(context, "timeout");

    private static T? GetGlobalOption<T>(InvocationContext context, string name)
    {
        var parseResult = context.ParseResult;
        var option = parseResult.RootCommandResult.Command.Options
            .FirstOrDefault(o => o.Name == name) as Option<T>;
        return option != null ? parseResult.GetValueForOption(option) : default;
    }

    public static bool? GetResultBool(ResponseMessage response, string key)
    {
        if (response.Result == null) return null;
        var resultJson = JsonConvert.SerializeObject(response.Result, JsonHelper.Settings);
        var result = JObject.Parse(resultJson);
        return result[key]?.Value<bool>();
    }

    /// <summary>
    /// Format a path for display: use relative if under CWD, absolute otherwise.
    /// Avoids confusing "../../../.." paths when CWD is deep in the tree.
    /// </summary>
    public static string FormatPath(string absolutePath)
    {
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, absolutePath);
        return relativePath.StartsWith(".." + Path.DirectorySeparatorChar) || relativePath == ".."
            ? absolutePath : relativePath;
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
