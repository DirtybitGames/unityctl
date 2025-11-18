using System;
using System.CommandLine;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UnityCtl.Bridge;
using UnityCtl.Protocol;

var rootCommand = new RootCommand("UnityCtl Bridge - WebSocket bridge between CLI and Unity Editor");

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

rootCommand.SetHandler(async (string? projectPath, int port) =>
{
    // Find project root
    var projectRoot = projectPath != null
        ? Path.GetFullPath(projectPath)
        : ProjectLocator.FindProjectRoot();

    if (projectRoot == null)
    {
        Console.Error.WriteLine("Error: Not in a Unity project. Use --project to specify project root.");
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

    // Add services
    builder.Services.AddSingleton(new BridgeState(projectId));

    var app = builder.Build();

    // Configure endpoints
    app.UseWebSockets();
    BridgeEndpoints.MapEndpoints(app);

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
