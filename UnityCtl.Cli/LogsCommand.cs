using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class LogsCommand
{
    public static Command CreateCommand()
    {
        var logsCommand = new Command("logs", "View logs from the bridge (editor.log + console)");

        var followOption = new Option<bool>(
            new[] { "-f", "--follow" },
            "Follow log output (stream continuously)");

        var linesOption = new Option<int>(
            new[] { "-n", "--lines" },
            getDefaultValue: () => 50,
            "Number of lines to show");

        var sourceOption = new Option<string>(
            "--source",
            getDefaultValue: () => "editor",
            "Filter by source: editor, console, all");

        var noColorOption = new Option<bool>(
            "--no-color",
            "Disable colored output");

        var verboseOption = new Option<bool>(
            "--verbose",
            "Show all fields including timestamps");

        logsCommand.AddOption(followOption);
        logsCommand.AddOption(linesOption);
        logsCommand.AddOption(sourceOption);
        logsCommand.AddOption(noColorOption);
        logsCommand.AddOption(verboseOption);

        logsCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var follow = context.ParseResult.GetValueForOption(followOption);
            var lines = context.ParseResult.GetValueForOption(linesOption);
            var source = context.ParseResult.GetValueForOption(sourceOption) ?? "all";
            var noColor = context.ParseResult.GetValueForOption(noColorOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);

            await ShowLogsAsync(projectPath, follow, lines, source, noColor, verbose);
        });

        return logsCommand;
    }

    private static async Task ShowLogsAsync(
        string? projectPath,
        bool follow,
        int lines,
        string source,
        bool noColor,
        bool verbose)
    {
        // Find project and bridge config
        var projectRoot = projectPath != null
            ? Path.GetFullPath(projectPath)
            : ProjectLocator.FindProjectRoot();

        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project.");
            Console.Error.WriteLine("  Use --project to specify project root");
            return;
        }

        var config = ProjectLocator.ReadBridgeConfig(projectRoot);
        if (config == null)
        {
            Console.Error.WriteLine("Error: Bridge not running.");
            Console.Error.WriteLine("  Start the bridge first: unityctl bridge start");
            return;
        }

        var baseUrl = $"http://localhost:{config.Port}";

        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // First, show recent logs (tail)
        if (lines > 0)
        {
            try
            {
                var response = await httpClient.GetAsync($"/logs/tail?lines={lines}&source={source}");
                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"Error: Bridge returned {response.StatusCode}");
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonHelper.Deserialize<LogsTailResult>(json);

                if (result?.Entries != null)
                {
                    foreach (var entry in result.Entries)
                    {
                        WriteLogEntry(entry, noColor, verbose);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Error: Cannot connect to bridge at {baseUrl}");
                Console.Error.WriteLine($"  {ex.Message}");
                Console.Error.WriteLine("  Make sure the bridge is running: unityctl bridge start");
                return;
            }
        }

        // If follow mode, stream new logs
        if (follow)
        {
            if (lines > 0)
            {
                Console.WriteLine("--- streaming new logs (Ctrl+C to stop) ---");
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await StreamLogsAsync(httpClient, source, noColor, verbose, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Error: Lost connection to bridge");
                Console.Error.WriteLine($"  {ex.Message}");
            }
        }
    }

    private static async Task StreamLogsAsync(
        HttpClient httpClient,
        string source,
        bool noColor,
        bool verbose,
        CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(
            $"/logs/stream?source={source}",
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
                break;

            // SSE format: "data: {json}"
            if (line.StartsWith("data: "))
            {
                var json = line.Substring(6);
                var entry = JsonHelper.Deserialize<UnifiedLogEntry>(json);
                if (entry != null)
                {
                    WriteLogEntry(entry, noColor, verbose);
                }
            }
        }
    }

    private static void WriteLogEntry(UnifiedLogEntry entry, bool noColor, bool verbose)
    {
        // Timestamp
        if (verbose)
        {
            if (!noColor) Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{FormatTimestamp(entry.Timestamp)}] ");
            if (!noColor) Console.ResetColor();
        }
        else
        {
            // Short timestamp
            if (!noColor) Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{FormatShortTimestamp(entry.Timestamp)}] ");
            if (!noColor) Console.ResetColor();
        }

        // Source tag for verbose or when filtering
        if (verbose)
        {
            if (!noColor) Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write($"[{entry.Source}] ");
            if (!noColor) Console.ResetColor();
        }

        // Message with color
        if (!noColor && entry.Color.HasValue)
        {
            Console.ForegroundColor = entry.Color.Value;
        }

        Console.WriteLine(entry.Message);

        if (!noColor)
        {
            Console.ResetColor();
        }
    }

    private static string FormatTimestamp(string timestamp)
    {
        if (DateTime.TryParse(timestamp, out var dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
        return timestamp;
    }

    private static string FormatShortTimestamp(string timestamp)
    {
        if (DateTime.TryParse(timestamp, out var dt))
        {
            return dt.ToString("HH:mm:ss");
        }
        return timestamp;
    }
}

// DTOs for logs endpoint responses
internal class LogsTailResult
{
    public UnifiedLogEntry[]? Entries { get; set; }
}

internal class UnifiedLogEntry
{
    public string Timestamp { get; set; } = "";
    public string Source { get; set; } = "";
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public string? StackTrace { get; set; }
    public ConsoleColor? Color { get; set; }
}
