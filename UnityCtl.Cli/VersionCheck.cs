using System;
using System.IO;
using System.Text.Json.Nodes;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

/// <summary>
/// Version check used by the status command. When enforce-version-match is enabled,
/// a plugin version newer than CLI/Bridge is an error (team member needs to update tools).
/// CLI/Bridge newer than plugin is only a warning (tools updated, package not yet).
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
    /// <param name="HasMismatch">True if any versions differ.</param>
    /// <param name="PluginAhead">True if the Unity plugin version is newer than CLI or Bridge — indicates tools need updating.</param>
    public record VersionCheckResult(bool HasMismatch, bool PluginAhead, string? CliVersion, string? BridgeVersion, string? PluginVersion);

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

        // Determine if the plugin is ahead of CLI or Bridge
        var pluginAhead = false;
        if (pluginBase != null)
        {
            var pluginParsed = ParseVersion(pluginBase);
            if (cliBase != null)
            {
                var cliParsed = ParseVersion(cliBase);
                if (pluginParsed > cliParsed)
                    pluginAhead = true;
            }
            if (bridgeBase != null)
            {
                var bridgeParsed = ParseVersion(bridgeBase);
                if (pluginParsed > bridgeParsed)
                    pluginAhead = true;
            }
        }

        return new VersionCheckResult(hasMismatch, pluginAhead, cliBase, bridgeBase, pluginBase);
    }

    private static Version ParseVersion(string version)
    {
        // Strip pre-release suffix (e.g. "1.2.3-beta" → "1.2.3")
        var dashIndex = version.IndexOf('-');
        var clean = dashIndex >= 0 ? version.Substring(0, dashIndex) : version;
        return Version.TryParse(clean, out var v) ? v : new Version(0, 0);
    }

    private static string? GetBaseVersion(string? version)
    {
        if (version == null) return null;
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version.Substring(0, plusIndex) : version;
    }
}
