using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityCtl.Protocol;

public static class ProjectLocator
{
    public const string BridgeConfigDir = ".unityctl";
    public const string BridgeConfigFile = "bridge.json";

    /// <summary>
    /// Finds the Unity project root by looking for ProjectSettings/ProjectVersion.txt
    /// </summary>
    public static string? FindProjectRoot(string? startPath = null)
    {
        var current = startPath != null ? new DirectoryInfo(startPath) : new DirectoryInfo(Directory.GetCurrentDirectory());

        while (current != null)
        {
            var projectSettings = Path.Combine(current.FullName, "ProjectSettings", "ProjectVersion.txt");
            if (File.Exists(projectSettings))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Computes a stable project ID from the absolute project path
    /// </summary>
    public static string ComputeProjectId(string projectPath)
    {
        var absolutePath = Path.GetFullPath(projectPath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(absolutePath));
        var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring(0, 8);
        return $"proj-{hashString}";
    }

    /// <summary>
    /// Gets the path to the bridge config file for a project
    /// </summary>
    public static string GetBridgeConfigPath(string projectRoot)
    {
        return Path.Combine(projectRoot, BridgeConfigDir, BridgeConfigFile);
    }

    /// <summary>
    /// Reads the bridge config for a project, returns null if not found
    /// </summary>
    public static BridgeConfig? ReadBridgeConfig(string projectRoot)
    {
        var configPath = GetBridgeConfigPath(projectRoot);
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonHelper.Deserialize<BridgeConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the bridge config for a project
    /// </summary>
    public static void WriteBridgeConfig(string projectRoot, BridgeConfig config)
    {
        var configDir = Path.Combine(projectRoot, BridgeConfigDir);
        Directory.CreateDirectory(configDir);

        var configPath = GetBridgeConfigPath(projectRoot);
        var json = JsonHelper.Serialize(config);
        File.WriteAllText(configPath, json);
    }
}
