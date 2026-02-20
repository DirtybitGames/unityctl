using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
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
            "Only update CLI and Bridge tools (skip Unity package and skill)"
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

        var skipSkillOption = new Option<bool>(
            "--skip-skill",
            "Skip Claude Code skill update"
        );

        updateCommand.AddOption(checkOption);
        updateCommand.AddOption(toolsOnlyOption);
        updateCommand.AddOption(packageOnlyOption);
        updateCommand.AddOption(versionOption);
        updateCommand.AddOption(yesOption);
        updateCommand.AddOption(skipSkillOption);

        updateCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var check = context.ParseResult.GetValueForOption(checkOption);
            var toolsOnly = context.ParseResult.GetValueForOption(toolsOnlyOption);
            var packageOnly = context.ParseResult.GetValueForOption(packageOnlyOption);
            var version = context.ParseResult.GetValueForOption(versionOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            var skipSkill = context.ParseResult.GetValueForOption(skipSkillOption);
            var json = ContextHelper.GetJson(context);

            await ExecuteAsync(projectPath, check, toolsOnly, packageOnly, version, yes, skipSkill, json);
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

    private enum UpdateResult { Success, Scheduled, Failed }

    private static async Task<UpdateResult> RunDotnetToolUpdateAsync(string packageId, string? version = null, bool isSelf = false)
    {
        // On Windows, updating the currently-running CLI requires a workaround:
        // spawn a script that waits for this process to exit, then performs the update
        if (isSelf && OperatingSystem.IsWindows())
        {
            var scheduled = await ScheduleSelfUpdateViaScriptAsync(packageId, version);
            return scheduled ? UpdateResult.Scheduled : UpdateResult.Failed;
        }

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
            if (process == null) return UpdateResult.Failed;

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
                return UpdateResult.Success;

            // If a specific version was requested and update failed, try uninstall+install
            // (dotnet tool update rejects versions lower than current)
            if (!string.IsNullOrEmpty(version))
            {
                return await UninstallAndInstallAsync(packageId, version);
            }

            return UpdateResult.Failed;
        }
        catch
        {
            return UpdateResult.Failed;
        }
    }

    private static async Task<UpdateResult> UninstallAndInstallAsync(string packageId, string version)
    {
        // Uninstall
        var uninstallPsi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"tool uninstall -g {packageId}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var uninstallProcess = Process.Start(uninstallPsi);
            if (uninstallProcess == null) return UpdateResult.Failed;

            await uninstallProcess.WaitForExitAsync();
            if (uninstallProcess.ExitCode != 0) return UpdateResult.Failed;
        }
        catch
        {
            return UpdateResult.Failed;
        }

        // Install specific version
        var installPsi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"tool install -g {packageId} --version {version}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var installProcess = Process.Start(installPsi);
            if (installProcess == null) return UpdateResult.Failed;

            await installProcess.WaitForExitAsync();
            return installProcess.ExitCode == 0 ? UpdateResult.Success : UpdateResult.Failed;
        }
        catch
        {
            return UpdateResult.Failed;
        }
    }

    /// <summary>
    /// Schedules the CLI self-update via a detached PowerShell script.
    /// Returns true if the script was successfully launched (update is pending).
    /// The actual update happens after this process exits.
    /// </summary>
    private static async Task<bool> ScheduleSelfUpdateViaScriptAsync(string packageId, string? version)
    {
        var currentPid = Environment.ProcessId;
        var versionArg = string.IsNullOrEmpty(version) ? "" : $" --version {version}";

        // Create a temporary PowerShell script that:
        // 1. Waits for the current process to exit
        // 2. Runs the dotnet tool update command (with uninstall+install fallback for downgrades)
        // 3. Deletes itself
        var tempDir = Path.GetTempPath();
        var scriptPath = Path.Combine(tempDir, $"unityctl-update-{Guid.NewGuid():N}.ps1");

        var scriptContent = $@"
# Wait for the CLI process to exit (max 30 seconds)
$maxWait = 30
$waited = 0
while ($waited -lt $maxWait) {{
    try {{
        $proc = Get-Process -Id {currentPid} -ErrorAction SilentlyContinue
        if (-not $proc) {{ break }}
        Start-Sleep -Milliseconds 500
        $waited += 0.5
    }} catch {{
        break
    }}
}}

# Try update first
dotnet tool update -g {packageId}{versionArg} 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0 -and '{version}' -ne '') {{
    # Fallback to uninstall+install for downgrades
    dotnet tool uninstall -g {packageId} 2>&1 | Out-Null
    dotnet tool install -g {packageId}{versionArg} 2>&1 | Out-Null
}}

# Clean up this script
Remove-Item -Path $MyInvocation.MyCommand.Path -Force
";

        try
        {
            // Write the script
            await File.WriteAllTextAsync(scriptPath, scriptContent);

            // Launch PowerShell detached
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                File.Delete(scriptPath);
                return false;
            }

            // Script launched successfully - update is scheduled
            return true;
        }
        catch
        {
            // Clean up on error
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
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
        bool skipSkill,
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

        // Display version info and handle --check
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

            if (checkOnly)
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

            if (checkOnly)
            {
                if (isUpToDate)
                    Console.WriteLine("You are already running the latest version.");
                else
                {
                    Console.WriteLine($"Update available: {currentVersion} -> {effectiveTargetVersion}");
                    Console.WriteLine("Run 'unityctl update' to install the update.");
                }
                return;
            }

            if (isUpToDate)
            {
                Console.WriteLine("CLI and Bridge are already up to date.");
                if (toolsOnly)
                    return;
                Console.WriteLine();
            }
        }

        // Confirm update (only when tools will be updated)
        if (!yes && !json && !isUpToDate)
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
        if (!packageOnly && !isUpToDate)
        {
            Console.WriteLine("Updating CLI tools...");
            Console.WriteLine();

            // Save current project's bridge config for restart later
            var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
            BridgeConfig? bridgeConfig = null;

            if (projectRoot != null)
            {
                bridgeConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
            }

            // Kill ALL bridge processes to release DLL locks
            var bridgeProcesses = Process.GetProcessesByName("unityctl-bridge");
            if (bridgeProcesses.Length > 0)
            {
                Console.WriteLine($"  Stopping {bridgeProcesses.Length} bridge process(es)...");
                foreach (var proc in bridgeProcesses)
                {
                    try { proc.Kill(true); } catch { }
                    proc.Dispose();
                }

                // Wait for processes to fully exit (max 10s) to release DLL locks
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    var remaining = Process.GetProcessesByName("unityctl-bridge");
                    var anyLeft = remaining.Length > 0;
                    foreach (var p in remaining) p.Dispose();
                    if (!anyLeft) break;
                    await Task.Delay(500);
                }
            }

            // Update CLI
            Console.WriteLine($"  Updating {CliPackageId}...");
            var cliResult = await RunDotnetToolUpdateAsync(CliPackageId, targetVersion, isSelf: true);
            var cliScheduled = cliResult == UpdateResult.Scheduled;
            var cliSuccess = cliResult == UpdateResult.Success || cliScheduled;
            results.Add(new UpdateStepResult
            {
                Step = CliPackageId,
                Success = cliSuccess,
                Scheduled = cliScheduled,
                Error = cliSuccess ? null : "Failed to update. Run 'dotnet tool update -g UnityCtl.Cli' manually."
            });
            var cliMessage = cliResult switch
            {
                UpdateResult.Success => $"    Updated {CliPackageId}",
                UpdateResult.Scheduled => $"    Scheduled {CliPackageId} (will complete after this command exits)",
                _ => $"    Failed to update {CliPackageId}"
            };
            Console.WriteLine(cliMessage);

            // Update Bridge
            Console.WriteLine($"  Updating {BridgePackageId}...");
            var bridgeResult = await RunDotnetToolUpdateAsync(BridgePackageId, targetVersion);
            var bridgeSuccess = bridgeResult == UpdateResult.Success;
            results.Add(new UpdateStepResult
            {
                Step = BridgePackageId,
                Success = bridgeSuccess,
                Error = bridgeSuccess ? null : "Failed to update. Run 'dotnet tool update -g UnityCtl.Bridge' manually."
            });
            Console.WriteLine(bridgeSuccess ? $"    Updated {BridgePackageId}" : $"    Failed to update {BridgePackageId}");

            // Restart bridge if it was running for the current project
            if (bridgeConfig != null && bridgeSuccess)
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

        // Update Claude Code skill
        if (!toolsOnly && !packageOnly && !skipSkill)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var globalSkillPath = Path.Combine(home, ".claude", "skills", "unity-editor", "SKILL.md");
            var localSkillPath = Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills", "unity-editor", "SKILL.md");

            var globalExists = File.Exists(globalSkillPath);
            var localExists = File.Exists(localSkillPath);

            if (globalExists || localExists)
            {
                Console.WriteLine("Updating Claude Code skill...");

                try
                {
                    var updated = new List<string>();

                    if (globalExists)
                    {
                        if (await SkillCommands.UpdateSkillFileAsync(globalSkillPath))
                            updated.Add("global");
                    }

                    if (localExists)
                    {
                        if (await SkillCommands.UpdateSkillFileAsync(localSkillPath))
                            updated.Add("local");
                    }

                    if (updated.Count > 0)
                    {
                        Console.WriteLine($"  Updated skill ({string.Join(" + ", updated)})");
                        results.Add(new UpdateStepResult
                        {
                            Step = "Claude Code Skill",
                            Success = true
                        });
                    }
                    else
                    {
                        Console.Error.WriteLine("  Could not find embedded skill content");
                        results.Add(new UpdateStepResult
                        {
                            Step = "Claude Code Skill",
                            Success = false,
                            Error = "Embedded SKILL.md resource not found"
                        });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new UpdateStepResult
                    {
                        Step = "Claude Code Skill",
                        Success = false,
                        Error = ex.Message
                    });
                }

                Console.WriteLine();
            }
        }

        // Summary (only if any steps ran)
        if (results.Count == 0)
            return;

        if (!json)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("Update Summary:");
            Console.WriteLine();

            var hasScheduled = false;
            foreach (var result in results)
            {
                var (status, icon) = result switch
                {
                    { Skipped: true } => ("Skipped", "-"),
                    { Scheduled: true } => ("Scheduled", "~"),
                    { Success: true } => ("OK", "+"),
                    _ => ("FAILED", "x")
                };
                Console.WriteLine($"  [{icon}] {result.Step}: {status}");
                if (!result.Success && result.Error != null)
                {
                    Console.WriteLine($"      Error: {result.Error}");
                }
                if (result.Scheduled) hasScheduled = true;
            }

            Console.WriteLine();

            if (results.All(r => r.Success))
            {
                Console.WriteLine("Update complete!");
                Console.WriteLine();
                if (hasScheduled)
                {
                    Console.WriteLine("Note: CLI update is scheduled and will complete after this command exits.");
                    Console.WriteLine("      Please restart your terminal to use the new version.");
                }
                else
                {
                    Console.WriteLine("Note: You may need to restart your terminal for CLI changes to take effect.");
                }
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
        public bool Scheduled { get; init; }
        public string? Error { get; init; }
    }
}
