using System;
using System.IO;
using System.Text.Json;
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
            var configDir = ProjectLocator.FindConfigDirectory(projectRoot);
            if (configDir == null) return false;

            var configPath = Path.Combine(configDir, ProjectLocator.ConfigFile);
            if (!File.Exists(configPath)) return false;

            var content = File.ReadAllText(configPath);
            var config = JsonNode.Parse(content);
            var value = config?["enforceVersionMatch"];
            return value?.GetValue<bool>() == true;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
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
        => Check(VersionInfo.Version, health.BridgeVersion, health.UnityPluginVersion);

    /// <summary>
    /// Compares the given CLI, bridge, and plugin versions for mismatches.
    /// Any version may be null (e.g. bridge not running, plugin not connected).
    /// </summary>
    public static VersionCheckResult Check(string cliVersion, string? bridgeVersion, string? pluginVersion)
    {
        var cliBase = GetBaseVersion(cliVersion);
        var bridgeBase = GetBaseVersion(bridgeVersion);
        var pluginBase = GetBaseVersion(pluginVersion);

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
            if (pluginParsed != null)
            {
                if (cliBase != null)
                {
                    var cliParsed = ParseVersion(cliBase);
                    if (cliParsed != null && pluginParsed > cliParsed)
                        pluginAhead = true;
                }
                if (bridgeBase != null)
                {
                    var bridgeParsed = ParseVersion(bridgeBase);
                    if (bridgeParsed != null && pluginParsed > bridgeParsed)
                        pluginAhead = true;
                }
            }
        }

        return new VersionCheckResult(hasMismatch, pluginAhead, cliBase, bridgeBase, pluginBase);
    }

    private const string PackageName = "com.dirtybit.unityctl";

    /// <summary>
    /// Reads the installed plugin version from Packages/manifest.json.
    /// Returns the version string (e.g. "0.7.0") or null if not found.
    /// </summary>
    public static string? ReadPluginVersionFromManifest(string projectRoot)
    {
        try
        {
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            var content = File.ReadAllText(manifestPath);
            var manifest = JsonNode.Parse(content);
            var value = manifest?["dependencies"]?[PackageName]?.ToString();
            if (value == null) return null;

            // Extract version from git URL: "...#v0.7.0" → "0.7.0"
            var hashIndex = value.LastIndexOf('#');
            if (hashIndex < 0) return null;

            var version = value.Substring(hashIndex + 1);
            // Strip leading 'v' if present
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                version = version.Substring(1);

            return version;
        }
        catch
        {
            return null;
        }
    }

    private static Version? ParseVersion(string version)
    {
        // Strip pre-release suffix (e.g. "1.2.3-beta" → "1.2.3")
        var dashIndex = version.IndexOf('-');
        var clean = dashIndex >= 0 ? version.Substring(0, dashIndex) : version;
        return Version.TryParse(clean, out var v) ? v : null;
    }

    private static string? GetBaseVersion(string? version)
    {
        if (version == null) return null;
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version.Substring(0, plusIndex) : version;
    }
}
