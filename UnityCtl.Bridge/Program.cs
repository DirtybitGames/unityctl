using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UnityCtl.Bridge;
using UnityCtl.Protocol;

var rootCommand = new RootCommand("UnityCtl Bridge - WebSocket bridge between CLI and Unity Editor");

// Bridge options
var projectOption = new Option<string>(
    "--project",
    "Path to Unity project root (optional, will auto-detect if not specified)"
);

var portOption = new Option<int>(
    "--port",
    getDefaultValue: () => 0,
    "Port to listen on (0 for automatic)"
);

rootCommand.AddOption(projectOption);
rootCommand.AddOption(portOption);

// Add test-log subcommand
var testLogCommand = new Command("test-log", "Test log filtering against a log file");
var fileArg = new Argument<string>("file", "Path to the editor.log file to test");
var showEventsOption = new Option<bool>("--show-events", "Show emitted events (compile.success, etc.)");
testLogCommand.AddArgument(fileArg);
testLogCommand.AddOption(showEventsOption);
testLogCommand.SetHandler((string filePath, bool showEvents) =>
{
    TestLogFile(filePath, showEvents);
}, fileArg, showEventsOption);
rootCommand.AddCommand(testLogCommand);

rootCommand.SetHandler(async (string? projectPath, int port) =>
{
    // Find project root
    var projectRoot = projectPath != null
        ? Path.GetFullPath(projectPath)
        : ProjectLocator.FindProjectRoot();

    if (projectRoot == null)
    {
        Console.Error.WriteLine("Error: Not in a Unity project.");
        Console.Error.WriteLine("  Use --project to specify project root, or create .unityctl/config.json");
        Console.Error.WriteLine("  with: { \"projectPath\": \"path/to/unity/project\" }");
        Environment.Exit(1);
        return;
    }

    // Validate it's actually a Unity project
    if (!File.Exists(Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt")))
    {
        Console.Error.WriteLine($"Error: {projectRoot} is not a valid Unity project.");
        Environment.Exit(1);
        return;
    }

    // Check if bridge is already running for this project
    var existingConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
    if (existingConfig != null)
    {
        try
        {
            var process = Process.GetProcessById(existingConfig.Pid);
            if (!process.HasExited)
            {
                // Verify bridge is responding
                using var httpClient = new HttpClient();
                var response = await httpClient.GetAsync($"http://localhost:{existingConfig.Port}/health");

                if (response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"Error: Bridge already running for project: {projectRoot}");
                    Console.Error.WriteLine($"  PID: {existingConfig.Pid}");
                    Console.Error.WriteLine($"  Port: {existingConfig.Port}");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Use 'unityctl bridge stop' to stop the existing bridge first.");
                    Environment.Exit(1);
                    return;
                }
            }
        }
        catch (ArgumentException)
        {
            // Process not found - stale config, continue to start new bridge
        }
        catch (InvalidOperationException)
        {
            // Process has exited - stale config, continue to start new bridge
        }
        catch (HttpRequestException)
        {
            // Bridge not responding - stale config, continue to start new bridge
        }
    }

    // Compute project ID
    var projectId = ProjectLocator.ComputeProjectId(projectRoot);

    // Find available port if not specified
    if (port == 0)
    {
        port = FindAvailablePort();
    }

    // Write bridge config
    var config = new BridgeConfig
    {
        ProjectId = projectId,
        Port = port,
        Pid = Environment.ProcessId
    };
    ProjectLocator.WriteBridgeConfig(projectRoot, config);

    Console.WriteLine($"UnityCtl Bridge");
    Console.WriteLine($"Project: {projectRoot}");
    Console.WriteLine($"Project ID: {projectId}");
    Console.WriteLine($"Port: {port}");
    Console.WriteLine($"PID: {config.Pid}");
    Console.WriteLine();

    // Create and run web host
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls($"http://localhost:{port}");

    // Configure fast shutdown
    builder.Services.Configure<HostOptions>(opts =>
    {
        opts.ShutdownTimeout = TimeSpan.FromSeconds(2);
    });

    // Add services
    var bridgeState = new BridgeState(projectId);
    builder.Services.AddSingleton(bridgeState);

    // Editor log tailer disabled - using WebSocket events for completion detection instead
    // var editorLogTailer = new EditorLogTailer(projectRoot, bridgeState);

    var app = builder.Build();

    // Configure endpoints
    app.UseWebSockets();
    BridgeEndpoints.MapEndpoints(app);

    // Editor log tailer disabled
    // editorLogTailer.Start();

    // Register shutdown handler to forcefully abort WebSocket connections
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Console.WriteLine("Shutting down bridge...");
        // editorLogTailer.Dispose();
        bridgeState.AbortUnityConnection();
    });

    Console.WriteLine("Bridge is ready. Press Ctrl+C to stop.");

    await app.RunAsync();

}, projectOption, portOption);

return await rootCommand.InvokeAsync(args);

static int FindAvailablePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    try
    {
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return port;
    }
    finally
    {
        listener.Stop();
    }
}

static void TestLogFile(string filePath, bool showEvents)
{
    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"Error: File not found: {filePath}");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine($"Testing: {filePath}");
    Console.WriteLine();

    var analyzer = new LogAnalyzer();
    var totalLines = 0;
    var filteredLines = 0;

    foreach (var line in File.ReadLines(filePath))
    {
        totalLines++;
        var (filtered, evt) = analyzer.ParseLineWithEvent(line);

        if (filtered != null)
        {
            filteredLines++;
            if (filtered.Color.HasValue)
            {
                Console.ForegroundColor = filtered.Color.Value;
            }
            Console.WriteLine(filtered.Text);
            Console.ResetColor();
        }

        if (showEvents && evt != null)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            var dataStr = evt.Data != null
                ? $" ({string.Join(", ", evt.Data.Select(kv => $"{kv.Key}={kv.Value}"))})"
                : "";
            Console.WriteLine($"  >> EVENT: {evt.Type}{dataStr}");
            Console.ResetColor();
        }
    }

    Console.WriteLine();
    var pct = totalLines > 0 ? (double)filteredLines / totalLines : 0;
    Console.WriteLine($"Summary: {totalLines} lines -> {filteredLines} filtered ({pct:P1})");
}
