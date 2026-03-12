using System;
using System.IO;
using System.Text.Json.Nodes;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

/// <summary>
/// Pre-flight version check that can block commands when enforce-version-match is enabled.
/// Compares CLI, Bridge, and Unity Plugin versions and errors on mismatch.
/// </summary>
public static class VersionCheck
{
    /// <summary>
    /// Checks whether enforce-version-match is enabled in the project config.
    /// </summary>
    public static bool IsEnforced(string? projectRoot = null)
    {
        try
        {
            var root = projectRoot ?? ProjectLocator.FindProjectRoot();
            if (root == null) return false;

            var configPath = Path.Combine(root, ProjectLocator.BridgeConfigDir, ProjectLocator.ConfigFile);
            if (!File.Exists(configPath)) return false;

            var content = File.ReadAllText(configPath);
            var config = JsonNode.Parse(content);
            var value = config?["enforceVersionMatch"];
            return value?.GetValue<bool>() == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Result of a version check against the running bridge.
    /// </summary>
    public record VersionCheckResult(bool HasMismatch, string? CliVersion, string? BridgeVersion, string? PluginVersion);

    /// <summary>
    /// Queries the bridge health endpoint and compares versions.
    /// Returns null if the bridge is not reachable.
    /// </summary>
    public static async System.Threading.Tasks.Task<VersionCheckResult?> CheckAsync(BridgeClient client)
    {
        try
        {
            var health = await client.GetAsync<HealthResult>("/health");
            if (health == null) return null;

            return Check(health);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compares CLI version against bridge and plugin versions from a health result.
    /// </summary>
    public static VersionCheckResult Check(HealthResult health)
    {
        var cliBase = GetBaseVersion(VersionInfo.Version);
        var bridgeBase = GetBaseVersion(health.BridgeVersion);
        var pluginBase = GetBaseVersion(health.UnityPluginVersion);

        var hasMismatch = false;

        if (bridgeBase != null && bridgeBase != cliBase)
            hasMismatch = true;

        if (pluginBase != null && pluginBase != cliBase)
            hasMismatch = true;

        if (pluginBase != null && bridgeBase != null && pluginBase != bridgeBase)
            hasMismatch = true;

        return new VersionCheckResult(hasMismatch, cliBase, bridgeBase, pluginBase);
    }

    /// <summary>
    /// Runs the pre-flight version check. If enforce-version-match is enabled and
    /// versions mismatch, prints an error to stderr and returns false (command should abort).
    /// Returns true if the command may proceed.
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> EnforceAsync(BridgeClient client, string? projectRoot = null)
    {
        if (!IsEnforced(projectRoot))
            return true;

        var result = await CheckAsync(client);
        if (result == null)
            return true; // Can't reach bridge health — let the command proceed and fail naturally

        if (!result.HasMismatch)
            return true;

        PrintMismatchError(result);
        return false;
    }

    internal static void PrintMismatchError(VersionCheckResult result)
    {
        Console.Error.WriteLine("Error: Version mismatch detected (enforce-version-match is enabled).");
        Console.Error.WriteLine($"  CLI: {result.CliVersion ?? "N/A"}, Bridge: {result.BridgeVersion ?? "N/A"}, Plugin: {result.PluginVersion ?? "N/A"}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Run 'unityctl update' to sync all components to the same version.");
        Console.Error.WriteLine("To disable this check: unityctl config set enforce-version-match false");
    }

    private static string? GetBaseVersion(string? version)
    {
        if (version == null) return null;
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version.Substring(0, plusIndex) : version;
    }
}
