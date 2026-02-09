using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the test.run command flow. Test execution is a long-running
/// operation that waits for a test.finished event from Unity.
/// </summary>
public class TestRunFlowTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.FakeUnity.OnCommand(UnityCtlCommands.TestRun, _ =>
            new TestRunResult { Started = true, TestRunId = "run-001" },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(200),
                UnityCtlEvents.TestFinished,
                new
                {
                    testRunId = "run-001",
                    passed = 10,
                    failed = 0,
                    skipped = 1,
                    duration = 2.5,
                    failures = Array.Empty<object>()
                }));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task TestRun_AllPass_ReturnsResults()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.TestRun);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal(10, result["passed"]?.Value<int>());
        Assert.Equal(0, result["failed"]?.Value<int>());
        Assert.Equal(1, result["skipped"]?.Value<int>());
    }

    [Fact]
    public async Task TestRun_WithFailures_ReturnsFailureDetails()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.TestRun, _ =>
            new TestRunResult { Started = true, TestRunId = "run-002" },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(200),
                UnityCtlEvents.TestFinished,
                new
                {
                    testRunId = "run-002",
                    passed = 8,
                    failed = 2,
                    skipped = 0,
                    duration = 3.1,
                    failures = new[]
                    {
                        new { testName = "TestA.Foo", message = "Expected 1 but got 2", stackTrace = "at TestA.cs:10" },
                        new { testName = "TestB.Bar", message = "Timeout", stackTrace = "at TestB.cs:20" }
                    }
                }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.TestRun);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal(2, result["failed"]?.Value<int>());
        var failures = result["failures"] as JArray;
        Assert.NotNull(failures);
        Assert.Equal(2, failures.Count);
        Assert.Equal("TestA.Foo", failures[0]["testName"]?.ToString());
    }

    [Fact]
    public async Task TestRun_WithModeArg_ForwardsToUnity()
    {
        var args = new Dictionary<string, object?>
        {
            ["mode"] = "playmode",
            ["filter"] = "MyTests"
        };

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.TestRun, args);
        AssertExtensions.IsOk(response);

        // Verify args were forwarded
        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.TestRun);
        Assert.NotNull(received.Args);
    }
}
