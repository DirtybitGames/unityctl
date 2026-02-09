using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for asset.refresh, asset.import, and asset.reimportAll flows.
/// These commands use event-based completion: the Bridge waits for
/// a completion event from Unity after sending the command.
/// </summary>
public class AssetRefreshFlowTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // asset.refresh: success with no compilation
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = false }));

        // asset.import: success
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetImport, req =>
        {
            var path = req.Args?["path"]?.ToString() ?? "unknown";
            return new AssetImportResult { Success = true };
        },
        ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
            UnityCtlEvents.AssetImportComplete,
            new { path = "Assets/Scripts/Test.cs", success = true }));

        // asset.reimportAll: success
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetReimportAll, _ =>
            new { },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.AssetReimportAllComplete,
                new { success = true }));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task AssetRefresh_NoCompilation_ReturnsOk()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        AssertExtensions.IsOk(response);
    }

    [Fact]
    public async Task AssetRefresh_WithCompilation_WaitsForCompilationFinished()
    {
        // Override with compilation triggered
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = true, hasCompilationErrors = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.CompilationFinished,
                new { success = true, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.True(result["compilationTriggered"]?.Value<bool>());
        Assert.True(result["compilationSuccess"]?.Value<bool>());
    }

    [Fact]
    public async Task AssetRefresh_CompilationFails_ReturnsErrorWithDetails()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.Immediate(UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = true, hasCompilationErrors = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(100),
                UnityCtlEvents.CompilationFinished,
                new
                {
                    success = false,
                    errors = new[] { new { file = "Foo.cs", line = 1, column = 1, message = "error" } },
                    warnings = Array.Empty<object>()
                }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        AssertExtensions.IsError(response, "COMPILATION_ERROR");
        var result = AssertExtensions.GetResultJObject(response);
        Assert.False(result["compilationSuccess"]?.Value<bool>());
        var errors = result["errors"]?.ToObject<CompilationMessageInfo[]>();
        Assert.NotNull(errors);
        Assert.Single(errors);
        Assert.Equal("Foo.cs", errors[0].File);
        Assert.Equal(1, errors[0].Line);
        Assert.Equal("error", errors[0].Message);
    }

    [Fact]
    public async Task AssetImport_SendsPathArgToUnity()
    {
        var args = new Dictionary<string, object?> { ["path"] = "Assets/Scripts/Test.cs" };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetImport, args);

        AssertExtensions.IsOk(response);

        // Verify the request was received by FakeUnity with correct args
        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.AssetImport);
        Assert.NotNull(received.Args);
        Assert.Equal("Assets/Scripts/Test.cs", received.Args["path"]?.ToString());
    }

    [Fact]
    public async Task AssetReimportAll_WaitsForCompletionEvent()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetReimportAll);

        AssertExtensions.IsOk(response);
    }
}
