using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UnityCtl.Bridge;
using UnityCtl.Protocol;
using UnityCtl.Tests.Fakes;

namespace UnityCtl.Tests.Helpers;

/// <summary>
/// Test fixture that starts a real Bridge in-process and provides a FakeUnityClient
/// for simulating Unity Editor behavior. Use as an IAsyncLifetime xUnit fixture.
/// </summary>
public class BridgeTestFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private Task? _appTask;
    private CancellationTokenSource? _appCts;

    public BridgeState BridgeState { get; private set; } = null!;
    public FakeUnityClient FakeUnity { get; private set; } = null!;
    public HttpClient HttpClient { get; private set; } = null!;
    public Uri BaseUri { get; private set; } = null!;
    public string ProjectId { get; } = "proj-test1234";

    public async Task InitializeAsync()
    {
        // Create bridge state
        BridgeState = new BridgeState(ProjectId);

        // Build the web application with a random port
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(BridgeState);

        _app = builder.Build();
        _app.UseWebSockets();
        BridgeEndpoints.MapEndpoints(_app);

        // Start the app
        _appCts = new CancellationTokenSource();
        _appTask = _app.RunAsync(_appCts.Token);

        // Wait for app to start and discover the port
        await _app.StartAsync();
        var addresses = _app.Urls;
        var address = addresses.First();
        BaseUri = new Uri(address);

        // Create HTTP client for test requests
        HttpClient = new HttpClient { BaseAddress = BaseUri };

        // Create and connect FakeUnity
        FakeUnity = new FakeUnityClient().WithProjectId(ProjectId);
        await FakeUnity.ConnectAsync(BaseUri);
    }

    public async Task DisposeAsync()
    {
        await FakeUnity.DisposeAsync();
        HttpClient.Dispose();

        _appCts?.Cancel();
        if (_app != null)
        {
            try { await _app.StopAsync(); } catch { }
        }
        if (_appTask != null)
        {
            try { await _appTask; } catch { }
        }
        _appCts?.Dispose();
    }

    /// <summary>
    /// Send an RPC command via HTTP, the same way the CLI does.
    /// </summary>
    public async Task<HttpResponseMessage> SendRpcAsync(string command,
        Dictionary<string, object?>? args = null, string? agentId = null)
    {
        var request = new
        {
            command,
            args,
            agentId
        };
        var json = JsonHelper.Serialize(request);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await HttpClient.PostAsync("/rpc", content);
    }

    /// <summary>
    /// Send an RPC command and parse the response as a ResponseMessage.
    /// </summary>
    public async Task<ResponseMessage> SendRpcAndParseAsync(string command,
        Dictionary<string, object?>? args = null, string? agentId = null)
    {
        var httpResponse = await SendRpcAsync(command, args, agentId);
        var responseJson = await httpResponse.Content.ReadAsStringAsync();
        return JsonHelper.Deserialize<ResponseMessage>(responseJson)!;
    }

    /// <summary>
    /// Create a fresh FakeUnityClient (useful for domain reload tests that need reconnection).
    /// The previous FakeUnity is NOT disposed; caller should manage lifecycle.
    /// </summary>
    public FakeUnityClient CreateFakeUnity()
    {
        return new FakeUnityClient().WithProjectId(ProjectId);
    }
}
