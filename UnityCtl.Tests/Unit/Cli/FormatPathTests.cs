using System;
using System.IO;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class FormatPathTests
{
    [Fact]
    public void PathUnderCwd_ReturnsRelative()
    {
        var cwd = Environment.CurrentDirectory;
        var absolute = Path.Combine(cwd, "Screenshots", "shot.png");

        var result = ContextHelper.FormatPath(absolute);

        Assert.Equal(Path.Combine("Screenshots", "shot.png"), result);
    }

    [Fact]
    public void PathOutsideCwd_ReturnsAbsolute()
    {
        // Parent's sibling is definitely outside CWD
        var cwd = Environment.CurrentDirectory;
        var outside = Path.GetFullPath(Path.Combine(cwd, "..", "other-project", "shot.png"));

        var result = ContextHelper.FormatPath(outside);

        Assert.Equal(outside, result);
    }

    [Fact]
    public void PathIsCwd_ReturnsRelativeDot()
    {
        var cwd = Environment.CurrentDirectory;

        var result = ContextHelper.FormatPath(cwd);

        Assert.Equal(".", result);
    }
}
