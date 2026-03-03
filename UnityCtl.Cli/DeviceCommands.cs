using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class DeviceCommands
{
    public static Command CreateCommand()
    {
        var deviceCommand = new Command("device", "Connect to and manage Unity player builds on devices");

        var portOption = new Option<int>(
            aliases: ["--port", "-p"],
            getDefaultValue: () => 7400,
            description: "TCP port for device connection (default: 7400)"
        );

        // device forward
        var forwardCommand = new Command("forward", "Set up ADB port forwarding to connect to a device");
        forwardCommand.AddOption(portOption);

        forwardCommand.SetHandler(async (InvocationContext context) =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var json = ContextHelper.GetJson(context);

            try
            {
                var psi = new ProcessStartInfo("adb", $"forward tcp:{port} tcp:{port}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                var process = Process.Start(psi);
                if (process == null)
                {
                    Console.Error.WriteLine("Error: Failed to start adb. Is it installed and in PATH?");
                    context.ExitCode = 1;
                    return;
                }

                await process.WaitForExitAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();

                if (process.ExitCode != 0)
                {
                    Console.Error.WriteLine($"Error: adb forward failed: {stderr.Trim()}");
                    context.ExitCode = 1;
                    return;
                }

                if (json)
                    Console.WriteLine(JsonHelper.Serialize(new { port, status = "forwarded" }));
                else
                {
                    Console.WriteLine($"ADB port forwarding set up: localhost:{port} -> device:{port}");
                    Console.WriteLine($"You can now use: unityctl script eval --device \"Screen.width\"");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Console.Error.WriteLine("Ensure ADB is installed and a device is connected.");
                context.ExitCode = 1;
            }
        });

        // device health
        var healthCommand = new Command("health", "Check the health of a connected device's dev server");
        healthCommand.AddOption(portOption);

        healthCommand.SetHandler(async (InvocationContext context) =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var json = ContextHelper.GetJson(context);

            using var client = new DeviceClient(port: port);
            try
            {
                await client.ConnectAsync();
                var response = await client.SendCommandAsync(UnityCtlCommands.DeviceHealth);
                if (response == null) { context.ExitCode = 1; return; }

                var result = response.RootElement.GetProperty("result");

                if (json)
                {
                    Console.WriteLine(result.GetRawText());
                }
                else
                {
                    var platform = result.GetProperty("platform").GetString();
                    var backend = result.GetProperty("scriptingBackend").GetString();
                    var unityVer = result.TryGetProperty("unityVersion", out var uv) ? uv.GetString() : "?";
                    var device = result.TryGetProperty("deviceModel", out var dm) ? dm.GetString() : "?";
                    var fullEval = result.GetProperty("supportsFullEval").GetBoolean();

                    Console.WriteLine($"Device connected successfully:");
                    Console.WriteLine($"  Platform:          {platform}");
                    Console.WriteLine($"  Unity version:     {unityVer}");
                    Console.WriteLine($"  Device:            {device}");
                    Console.WriteLine($"  Scripting backend: {backend}");
                    Console.WriteLine($"  Full eval support: {(fullEval ? "yes (Mono)" : "no (IL2CPP - expression eval only)")}");
                }
            }
            catch (DeviceConnectionException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        // device list (ADB devices)
        var listCommand = new Command("list", "List connected ADB devices");
        listCommand.SetHandler(async (InvocationContext context) =>
        {
            try
            {
                var psi = new ProcessStartInfo("adb", "devices -l")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                var process = Process.Start(psi);
                if (process == null)
                {
                    Console.Error.WriteLine("Error: Failed to start adb. Is it installed and in PATH?");
                    context.ExitCode = 1;
                    return;
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                Console.Write(output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Console.Error.WriteLine("Ensure ADB is installed and in PATH.");
                context.ExitCode = 1;
            }
        });

        deviceCommand.AddCommand(forwardCommand);
        deviceCommand.AddCommand(healthCommand);
        deviceCommand.AddCommand(listCommand);

        return deviceCommand;
    }
}
