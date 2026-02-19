using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class BuildCodeGeneratorTests
{
    [Fact]
    public void StandardBuild_ContainsBuildTarget()
    {
        var code = BuildCommands.BuildStandardCode("StandaloneLinux64", "./Builds/MyGame", []);

        Assert.Contains("BuildTarget.StandaloneLinux64", code);
    }

    [Fact]
    public void StandardBuild_ContainsOutputPath()
    {
        var code = BuildCommands.BuildStandardCode("StandaloneLinux64", "./Builds/MyGame", []);

        Assert.Contains("./Builds/MyGame", code);
    }

    [Fact]
    public void StandardBuild_NoScenes_UsesBuildSettings()
    {
        var code = BuildCommands.BuildStandardCode("StandaloneLinux64", "./Builds/MyGame", []);

        Assert.Contains("EditorBuildSettings.scenes", code);
    }

    [Fact]
    public void StandardBuild_WithScenes_UsesExplicitScenes()
    {
        var code = BuildCommands.BuildStandardCode("StandaloneLinux64", "./Builds/MyGame",
            ["Assets/Scenes/Main.unity", "Assets/Scenes/Menu.unity"]);

        Assert.Contains("\"Assets/Scenes/Main.unity\"", code);
        Assert.Contains("\"Assets/Scenes/Menu.unity\"", code);
        Assert.DoesNotContain("EditorBuildSettings.scenes", code);
    }

    [Fact]
    public void StandardBuild_HasRequiredUsings()
    {
        var code = BuildCommands.BuildStandardCode("StandaloneLinux64", "./Builds/MyGame", []);

        Assert.Contains("using UnityEditor;", code);
        Assert.Contains("using UnityEditor.Build.Reporting;", code);
    }

    [Fact]
    public void StandardBuild_ReturnsBuildReport()
    {
        var code = BuildCommands.BuildStandardCode("StandaloneLinux64", "./Builds/MyGame", []);

        Assert.Contains("BuildPipeline.BuildPlayer(options)", code);
        Assert.Contains("summary.result.ToString()", code);
        Assert.Contains("summary.totalErrors", code);
    }

    [Fact]
    public void StandardBuild_EscapesOutputPath()
    {
        var code = BuildCommands.BuildStandardCode("StandaloneWindows64", "C:\\Builds\\My Game", []);

        Assert.Contains("C:\\\\Builds\\\\My Game", code);
    }

    [Fact]
    public void CustomBuild_WithClassDefinition_UsedAsIs()
    {
        var custom = "using UnityEditor;\npublic class MyBuild { public static object Main() { return null; } }";
        var code = BuildCommands.BuildCustomCode(custom);

        Assert.Equal(custom, code);
    }

    [Fact]
    public void CustomBuild_Expression_WrappedWithReturn()
    {
        var code = BuildCommands.BuildCustomCode("BuildPipeline.BuildPlayer(new string[0], \"out\", BuildTarget.WebGL, BuildOptions.None)");

        Assert.Contains("return BuildPipeline.BuildPlayer", code);
        Assert.Contains("public class Script", code);
    }

    [Fact]
    public void CustomBuild_BodyStatements_UsedAsBody()
    {
        var code = BuildCommands.BuildCustomCode("var report = BuildPipeline.BuildPlayer(new string[0], \"out\", BuildTarget.WebGL, BuildOptions.None); return report.summary.result.ToString();");

        Assert.Contains("var report = BuildPipeline.BuildPlayer", code);
        Assert.DoesNotContain("return var report", code);
    }

    [Fact]
    public void CustomBuild_HasBuildUsings()
    {
        var code = BuildCommands.BuildCustomCode("BuildPipeline.BuildPlayer(new string[0], \"out\", BuildTarget.WebGL, BuildOptions.None)");

        Assert.Contains("using UnityEditor.Build.Reporting;", code);
    }
}
