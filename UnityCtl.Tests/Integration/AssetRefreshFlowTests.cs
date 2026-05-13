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
    public async Task AssetRefresh_NoCompilation_ReturnsOkWithCompilationTriggeredFalse()
    {
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.False(result["compilationTriggered"]?.Value<bool>());
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
    public async Task AssetRefresh_NoCompilationButErrorsExist_ReturnsOkWithHasCompilationErrors()
    {
        // Simulate: no new compilation triggered, but previous errors still exist
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ =>
            new { },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = false, hasCompilationErrors = true }));

        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        // Bridge returns ok (it's Unity's response), but the payload signals errors exist
        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.False(result["compilationTriggered"]?.Value<bool>());
        Assert.True(result["hasCompilationErrors"]?.Value<bool>());
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

    [Fact]
    public async Task AssetRefresh_DomainReload_WaitsForReconnection()
    {
        // Real Unity sequence after a compile-triggering refresh:
        //   AssetRefreshComplete(compilationTriggered=true) → CompilationFinished
        //   → DomainReloadStarting → Unity disconnects → reconnects → editor.ping ok.
        // The bridge must wait for this whole sequence so the next chained RPC
        // doesn't race a busy main thread.
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ => new { },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = true, hasCompilationErrors = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(150),
                UnityCtlEvents.CompilationFinished,
                new { success = true, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(250),
                UnityCtlEvents.DomainReloadStarting,
                new { }));

        var refreshTask = _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);

        await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress,
            timeout: TimeSpan.FromSeconds(2));

        Assert.False(refreshTask.IsCompleted,
            "asset.refresh must wait for domain reload + editor ready before returning");

        await _fixture.FakeUnity.DisconnectAsync();
        await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);

        var newFakeUnity = _fixture.CreateFakeUnity();
        await newFakeUnity.ConnectAsync(_fixture.BaseUri);

        var response = await refreshTask;
        AssertExtensions.IsOk(response);

        Assert.True(_fixture.BridgeState.IsEditorReady,
            "editor must be ready by the time asset.refresh returns");

        await newFakeUnity.DisposeAsync();
    }

    [Fact]
    public async Task AssetRefresh_ChainedCommand_DoesNotRaceMainThread()
    {
        // End-to-end: asset.refresh triggers compilation + reload, then a chained
        // RPC follows. The follow-up must succeed quickly.
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.AssetRefresh, _ => new { },
            ScheduledEvent.After(TimeSpan.FromMilliseconds(50),
                UnityCtlEvents.AssetRefreshComplete,
                new { compilationTriggered = true, hasCompilationErrors = false }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(150),
                UnityCtlEvents.CompilationFinished,
                new { success = true, errors = Array.Empty<object>(), warnings = Array.Empty<object>() }),
            ScheduledEvent.After(TimeSpan.FromMilliseconds(200),
                UnityCtlEvents.DomainReloadStarting,
                new { }));

        // Background task: simulate Unity's disconnect/reconnect on domain reload.
        // Without this, the FakeUnity stays connected and DomainReloadStarting alone
        // doesn't reflect real Unity behavior.
        var reloadTask = Task.Run(async () =>
        {
            await AssertExtensions.WaitUntilAsync(() => _fixture.BridgeState.IsDomainReloadInProgress,
                timeout: TimeSpan.FromSeconds(5));
            await _fixture.FakeUnity.DisconnectAsync();
            await AssertExtensions.WaitUntilAsync(() => !_fixture.BridgeState.IsUnityConnected);
            var newFake = _fixture.CreateFakeUnity();
            await newFake.ConnectAsync(_fixture.BaseUri);
            return newFake;
        });

        var refreshResponse = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.AssetRefresh);
        AssertExtensions.IsOk(refreshResponse);

        var newFake = await reloadTask;

        // Chained command must succeed quickly — Unity is responsive by now
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chainedResponse = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.PlayStatus);
        sw.Stop();

        AssertExtensions.IsOk(chainedResponse);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"chained play.status after asset.refresh took {sw.ElapsedMilliseconds}ms — should be <5s");

        await newFake.DisposeAsync();
    }
}
