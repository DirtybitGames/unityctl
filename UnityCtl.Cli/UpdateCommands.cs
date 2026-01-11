using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class UpdateCommands
{
    private const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/{0}/index.json";
    private const string CliPackageId = "UnityCtl.Cli";
    private const string BridgePackageId = "UnityCtl.Bridge";

    public static Command CreateCommand()
    {
        var updateCommand = new Command("update", "Update UnityCtl CLI, bridge, and Unity package");

        var checkOption = new Option<bool>(
            "--check",
            "Check for updates without installing"
        );

        var toolsOnlyOption = new Option<bool>(
            "--tools-only",
            "Only update CLI and Bridge tools (skip Unity package)"
        );

        var packageOnlyOption = new Option<bool>(
            "--package-only",
            "Only update Unity package (skip CLI and Bridge tools)"
        );

        var versionOption = new Option<string?>(
            "--version",
            "Specific version to update to (default: latest)"
        );
        versionOption.AddAlias("-v");

        var yesOption = new Option<bool>(
            "--yes",
            "Skip confirmation prompts"
        );
        yesOption.AddAlias("-y");

        updateCommand.AddOption(checkOption);
        updateCommand.AddOption(toolsOnlyOption);
        updateCommand.AddOption(packageOnlyOption);
        updateCommand.AddOption(versionOption);
        updateCommand.AddOption(yesOption);

        updateCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var check = context.ParseResult.GetValueForOption(checkOption);
            var toolsOnly = context.ParseResult.GetValueForOption(toolsOnlyOption);
            var packageOnly = context.ParseResult.GetValueForOption(packageOnlyOption);
            var version = context.ParseResult.GetValueForOption(versionOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            var json = ContextHelper.GetJson(context);

            await ExecuteAsync(projectPath, check, toolsOnly, packageOnly, version, yes, json);
        });

        return updateCommand;
    }

    private static async Task<string?> GetLatestVersionAsync(string packageId)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var url = string.Format(NuGetIndexUrl, packageId.ToLowerInvariant());
            var response = await client.GetStringAsync(url);
            var jsonDoc = JsonNode.Parse(response);
            var versions = jsonDoc?["versions"]?.AsArray();

            if (versions == null || versions.Count == 0)
                return null;

            // Get the last version (they're sorted chronologically)
            return versions[versions.Count - 1]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> RunDotnetToolUpdateAsync(string packageId, string? version = null)
    {
        var args = $"tool update -g {packageId}";
        if (!string.IsNullOrEmpty(version))
        {
            args += $" --version {version}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ExecuteAsync(
        string? projectPath,
        bool checkOnly,
        bool toolsOnly,
        bool packageOnly,
        string? targetVersion,
        bool yes,
        bool json)
    {
        var currentVersion = VersionInfo.Version;

        Console.WriteLine("Checking for updates...");
        Console.WriteLine();

        // Get latest version from NuGet
        var latestVersion = await GetLatestVersionAsync(CliPackageId);

        if (latestVersion == null)
        {
            Console.Error.WriteLine("Error: Could not check for updates. Please check your network connection.");
            return;
        }

        var effectiveTargetVersion = targetVersion ?? latestVersion;

        // Compare versions
        var currentParsed = Version.TryParse(currentVersion.Split('+')[0].Split('-')[0], out var cv) ? cv : new Version(0, 0);
        var targetParsed = Version.TryParse(effectiveTargetVersion.Split('+')[0].Split('-')[0], out var tv) ? tv : new Version(0, 0);

        var isUpToDate = currentParsed >= targetParsed && targetVersion == null;

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new
            {
                currentVersion,
                latestVersion,
                targetVersion = effectiveTargetVersion,
                upToDate = isUpToDate,
                checkOnly
            }));

            if (checkOnly || isUpToDate)
                return;
        }
        else
        {
            Console.WriteLine($"Current version: {currentVersion}");
            Console.WriteLine($"Latest version:  {latestVersion}");

            if (targetVersion != null)
            {
                Console.WriteLine($"Target version:  {targetVersion}");
            }

            Console.WriteLine();

            if (isUpToDate)
            {
                Console.WriteLine("You are already running the latest version.");
                return;
            }

            if (checkOnly)
            {
                Console.WriteLine($"Update available: {currentVersion} -> {effectiveTargetVersion}");
                Console.WriteLine("Run 'unityctl update' to install the update.");
                return;
            }
        }

        // Confirm update
        if (!yes && !json)
        {
            Console.Write($"Update to version {effectiveTargetVersion}? [y/N]: ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response != "y" && response != "yes")
            {
                Console.WriteLine("Update cancelled.");
                return;
            }
            Console.WriteLine();
        }

        var results = new List<UpdateStepResult>();

        // Update CLI and Bridge tools
        if (!packageOnly)
        {
            Console.WriteLine("Updating CLI tools...");
            Console.WriteLine();

            // Check if bridge is running and stop it
            var bridgeWasRunning = false;
            var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
            BridgeConfig? bridgeConfig = null;

            if (projectRoot != null)
            {
                bridgeConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
                if (bridgeConfig != null)
                {
                    Console.WriteLine("  Stopping bridge daemon...");
                    await BridgeClient.StopBridgeAsync(projectPath);
                    bridgeWasRunning = true;
                    // Give it a moment to shut down
                    await Task.Delay(1000);
                }
            }

            // Update CLI
            Console.WriteLine($"  Updating {CliPackageId}...");
            var cliSuccess = await RunDotnetToolUpdateAsync(CliPackageId, targetVersion);
            results.Add(new UpdateStepResult
            {
                Step = CliPackageId,
                Success = cliSuccess,
                Error = cliSuccess ? null : "Failed to update. Run 'dotnet tool update -g UnityCtl.Cli' manually."
            });
            Console.WriteLine(cliSuccess ? $"    Updated {CliPackageId}" : $"    Failed to update {CliPackageId}");

            // Update Bridge
            Console.WriteLine($"  Updating {BridgePackageId}...");
            var bridgeSuccess = await RunDotnetToolUpdateAsync(BridgePackageId, targetVersion);
            results.Add(new UpdateStepResult
            {
                Step = BridgePackageId,
                Success = bridgeSuccess,
                Error = bridgeSuccess ? null : "Failed to update. Run 'dotnet tool update -g UnityCtl.Bridge' manually."
            });
            Console.WriteLine(bridgeSuccess ? $"    Updated {BridgePackageId}" : $"    Failed to update {BridgePackageId}");

            // Restart bridge if it was running
            if (bridgeWasRunning && bridgeSuccess)
            {
                Console.WriteLine("  Restarting bridge daemon...");
                await BridgeClient.StartBridgeAsync(projectPath);
            }

            Console.WriteLine();
        }

        // Update Unity package
        if (!toolsOnly)
        {
            Console.WriteLine("Updating Unity package...");

            var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
            if (projectRoot == null)
            {
                Console.WriteLine("  Skipped: Not in a Unity project");
                results.Add(new UpdateStepResult
                {
                    Step = "Unity Package",
                    Success = true,
                    Skipped = true
                });
            }
            else
            {
                try
                {
                    await PackageCommands.UpdatePackageAsync(projectPath, $"v{effectiveTargetVersion}", json: false);
                    results.Add(new UpdateStepResult
                    {
                        Step = "Unity Package",
                        Success = true
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new UpdateStepResult
                    {
                        Step = "Unity Package",
                        Success = false,
                        Error = ex.Message
                    });
                }
            }

            Console.WriteLine();
        }

        // Summary
        if (!json)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("Update Summary:");
            Console.WriteLine();

            foreach (var result in results)
            {
                var status = result.Skipped ? "Skipped" : (result.Success ? "OK" : "FAILED");
                var icon = result.Skipped ? "-" : (result.Success ? "+" : "x");
                Console.WriteLine($"  [{icon}] {result.Step}: {status}");
                if (!result.Success && result.Error != null)
                {
                    Console.WriteLine($"      Error: {result.Error}");
                }
            }

            Console.WriteLine();

            if (results.All(r => r.Success))
            {
                Console.WriteLine("Update complete!");
                Console.WriteLine();
                Console.WriteLine("Note: You may need to restart your terminal for CLI changes to take effect.");
            }
            else
            {
                Console.WriteLine("Update completed with some errors. Please review the issues above.");
            }
        }
        else
        {
            Console.WriteLine(JsonHelper.Serialize(new
            {
                success = results.All(r => r.Success),
                targetVersion = effectiveTargetVersion,
                results
            }));
        }
    }

    private class UpdateStepResult
    {
        public required string Step { get; init; }
        public bool Success { get; init; }
        public bool Skipped { get; init; }
        public string? Error { get; init; }
    }
}
