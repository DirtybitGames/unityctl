using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityCtl.Protocol;

/// <summary>
/// Status of the Unity Editor for a project
/// </summary>
public enum UnityEditorStatus
{
    /// <summary>Unity Editor is not running for this project</summary>
    NotRunning,
    /// <summary>Unity Editor is running for this project</summary>
    Running,
    /// <summary>Could not determine status (permission error, etc.)</summary>
    Unknown
}

/// <summary>
/// Result of checking Unity Editor status
/// </summary>
public class UnityEditorStatusResult
{
    public required UnityEditorStatus Status { get; init; }
    public string? Message { get; init; }
    public string? LockFilePath { get; init; }
}

public class UnityCtlConfig
{
    public string? ProjectPath { get; set; }
}

public static class ProjectLocator
{
    public const string BridgeConfigDir = ".unityctl";
    public const string BridgeConfigFile = "bridge.json";
    public const string ConfigFile = "config.json";

    /// <summary>
    /// Reads .unityctl/config.json and returns the resolved project path, or null if not found/invalid
    /// </summary>
    public static string? ReadProjectFromConfig(string directory)
    {
        var configPath = Path.Combine(directory, BridgeConfigDir, ConfigFile);
        if (!File.Exists(configPath))
            return null;

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonHelper.Deserialize<UnityCtlConfig>(json);
            if (string.IsNullOrEmpty(config?.ProjectPath))
                return null;

            var resolvedPath = Path.GetFullPath(Path.Combine(directory, config.ProjectPath));

            // Validate it's actually a Unity project
            var projectSettings = Path.Combine(resolvedPath, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(projectSettings))
                return null;

            return resolvedPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the Unity project root by looking for .unityctl/config.json or ProjectSettings/ProjectVersion.txt
    /// </summary>
    public static string? FindProjectRoot(string? startPath = null)
    {
        var current = startPath != null ? new DirectoryInfo(startPath) : new DirectoryInfo(Directory.GetCurrentDirectory());

        while (current != null)
        {
            // Check for .unityctl/config.json pointer first
            var configProject = ReadProjectFromConfig(current.FullName);
            if (configProject != null)
                return configProject;

            // Then check if current directory is a Unity project
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

    /// <summary>
    /// Checks if Unity Editor is running for the specified project by checking the lock file.
    /// Unity creates Temp/UnityLockfile and holds an exclusive lock while the project is open.
    /// </summary>
    public static UnityEditorStatusResult CheckUnityEditorStatus(string projectRoot)
    {
        var lockFilePath = Path.Combine(projectRoot, "Temp", "UnityLockfile");

        // If the lock file doesn't exist, Unity is definitely not running
        if (!File.Exists(lockFilePath))
        {
            return new UnityEditorStatusResult
            {
                Status = UnityEditorStatus.NotRunning,
                Message = "Unity Editor is not running (no lock file)",
                LockFilePath = lockFilePath
            };
        }

        try
        {
            // Try to open the file with exclusive access
            // If Unity is running, it holds a lock and this will fail
            using (var stream = new FileStream(
                lockFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                // Successfully opened with exclusive access - file exists but not locked
                // This means Unity crashed or closed without cleaning up
                return new UnityEditorStatusResult
                {
                    Status = UnityEditorStatus.NotRunning,
                    Message = "Unity Editor is not running (stale lock file)",
                    LockFilePath = lockFilePath
                };
            }
        }
        catch (IOException ex) when (IsFileLockedException(ex))
        {
            // File is locked - Unity is running
            return new UnityEditorStatusResult
            {
                Status = UnityEditorStatus.Running,
                Message = "Unity Editor is running",
                LockFilePath = lockFilePath
            };
        }
        catch (UnauthorizedAccessException)
        {
            // Permission denied - could be locked or permission issue
            // On some systems, locked files throw UnauthorizedAccessException
            return new UnityEditorStatusResult
            {
                Status = UnityEditorStatus.Running,
                Message = "Unity Editor is likely running (access denied to lock file)",
                LockFilePath = lockFilePath
            };
        }
        catch (Exception ex)
        {
            return new UnityEditorStatusResult
            {
                Status = UnityEditorStatus.Unknown,
                Message = $"Could not determine Unity status: {ex.Message}",
                LockFilePath = lockFilePath
            };
        }
    }

    /// <summary>
    /// Checks if an IOException indicates a file locking error.
    /// Works cross-platform (Windows, Mac, Linux).
    /// </summary>
    private static bool IsFileLockedException(IOException ex)
    {
        // Windows: ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33)
        // The HResult contains the error code in the lower 16 bits
        var errorCode = ex.HResult & 0xFFFF;
        if (errorCode == 32 || errorCode == 33)
            return true;

        // Unix/Mac: Check for common locking-related messages
        var message = ex.Message.ToLowerInvariant();
        if (message.Contains("being used by another process") ||
            message.Contains("sharing violation") ||
            message.Contains("locked") ||
            message.Contains("in use"))
        {
            return true;
        }

        return false;
    }
}
