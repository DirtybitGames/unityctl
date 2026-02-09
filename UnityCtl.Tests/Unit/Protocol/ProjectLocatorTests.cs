using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Protocol;

public class ProjectLocatorTests
{
    [Fact]
    public void ComputeProjectId_ReturnsDeterministicId()
    {
        var id1 = ProjectLocator.ComputeProjectId("/home/user/my-project");
        var id2 = ProjectLocator.ComputeProjectId("/home/user/my-project");

        Assert.Equal(id1, id2);
        Assert.StartsWith("proj-", id1);
        Assert.Equal(13, id1.Length); // "proj-" + 8 hex chars
    }

    [Fact]
    public void ComputeProjectId_DifferentPaths_ProduceDifferentIds()
    {
        var id1 = ProjectLocator.ComputeProjectId("/home/user/project-a");
        var id2 = ProjectLocator.ComputeProjectId("/home/user/project-b");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GetBridgeConfigPath_ReturnsCorrectPath()
    {
        var path = ProjectLocator.GetBridgeConfigPath("/home/user/my-project");

        Assert.EndsWith(".unityctl/bridge.json", path.Replace('\\', '/'));
    }

    [Fact]
    public void ReadBridgeConfig_NonExistentPath_ReturnsNull()
    {
        var config = ProjectLocator.ReadBridgeConfig("/non/existent/path");

        Assert.Null(config);
    }

    [Fact]
    public void WriteBridgeConfig_ThenRead_Roundtrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unityctl-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = new BridgeConfig
            {
                ProjectId = "proj-test1234",
                Port = 49521,
                Pid = 12345
            };

            ProjectLocator.WriteBridgeConfig(tempDir, config);
            var readConfig = ProjectLocator.ReadBridgeConfig(tempDir);

            Assert.NotNull(readConfig);
            Assert.Equal(config.ProjectId, readConfig.ProjectId);
            Assert.Equal(config.Port, readConfig.Port);
            Assert.Equal(config.Pid, readConfig.Pid);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindProjectRoot_NotInUnityProject_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unityctl-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = ProjectLocator.FindProjectRoot(tempDir);
            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindProjectRoot_InUnityProject_ReturnsProjectRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unityctl-test-" + Guid.NewGuid().ToString("N")[..8]);
        var projectSettings = Path.Combine(tempDir, "ProjectSettings");
        Directory.CreateDirectory(projectSettings);
        File.WriteAllText(Path.Combine(projectSettings, "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1");

        var subDir = Path.Combine(tempDir, "Assets", "Scripts");
        Directory.CreateDirectory(subDir);

        try
        {
            var result = ProjectLocator.FindProjectRoot(subDir);
            Assert.Equal(Path.GetFullPath(tempDir), Path.GetFullPath(result!));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ReadProjectFromConfig_ValidConfig_ReturnsProjectPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unityctl-test-" + Guid.NewGuid().ToString("N")[..8]);
        var projectDir = Path.Combine(tempDir, "unity-project");
        var projectSettings = Path.Combine(projectDir, "ProjectSettings");

        // Create the Unity project structure
        Directory.CreateDirectory(projectSettings);
        File.WriteAllText(Path.Combine(projectSettings, "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1");

        // Create config pointing to project
        var configDir = Path.Combine(tempDir, ".unityctl");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"),
            """{"projectPath": "unity-project"}""");

        try
        {
            var result = ProjectLocator.ReadProjectFromConfig(tempDir);
            Assert.NotNull(result);
            Assert.Equal(Path.GetFullPath(projectDir), Path.GetFullPath(result));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ReadProjectFromConfig_NoConfigFile_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unityctl-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = ProjectLocator.ReadProjectFromConfig(tempDir);
            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ReadProjectFromConfig_InvalidTarget_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unityctl-test-" + Guid.NewGuid().ToString("N")[..8]);
        var configDir = Path.Combine(tempDir, ".unityctl");
        Directory.CreateDirectory(configDir);
        // Points to nonexistent directory
        File.WriteAllText(Path.Combine(configDir, "config.json"),
            """{"projectPath": "nonexistent-project"}""");

        try
        {
            var result = ProjectLocator.ReadProjectFromConfig(tempDir);
            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
