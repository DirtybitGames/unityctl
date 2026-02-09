using System.Net.Http;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for log buffering, tailing, clearing, and SSE streaming.
/// </summary>
public class LogStreamingTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task LogsTail_ReturnsEmptyInitially()
    {
        var response = await _fixture.HttpClient.GetAsync("/logs/tail?lines=10");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JObject.Parse(json);
        var entries = result["entries"] as JArray;

        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task LogsTail_ReturnsLogsAfterEvents()
    {
        // Send log events from FakeUnity
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "First log"
        });

        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:00:01Z",
            Level = LogLevel.Warning,
            Message = "Second log"
        });

        // Give the bridge time to process events
        await Task.Delay(200);

        var response = await _fixture.HttpClient.GetAsync("/logs/tail?lines=10&source=console");
        var json = await response.Content.ReadAsStringAsync();
        var result = JObject.Parse(json);
        var entries = result["entries"] as JArray;

        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("First log", entries[0]["message"]?.ToString());
        Assert.Equal("Second log", entries[1]["message"]?.ToString());
    }

    [Fact]
    public async Task LogsTail_LinesParam_LimitsResults()
    {
        // Send 5 log events
        for (int i = 0; i < 5; i++)
        {
            await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Log {i}"
            });
        }
        await Task.Delay(200);

        var response = await _fixture.HttpClient.GetAsync("/logs/tail?lines=3&source=console");
        var json = await response.Content.ReadAsStringAsync();
        var result = JObject.Parse(json);
        var entries = result["entries"] as JArray;

        Assert.NotNull(entries);
        Assert.Equal(3, entries.Count);
        // Should return the LAST 3 entries
        Assert.Equal("Log 2", entries[0]["message"]?.ToString());
    }

    [Fact]
    public async Task LogsClear_SetsWatermark()
    {
        // Add some logs
        for (int i = 0; i < 3; i++)
        {
            await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
            {
                Timestamp = "2024-01-01T12:00:00Z",
                Level = LogLevel.Log,
                Message = $"Before clear {i}"
            });
        }
        await Task.Delay(200);

        // Clear logs
        var clearResponse = await _fixture.HttpClient.PostAsync("/logs/clear?reason=test", null);
        clearResponse.EnsureSuccessStatusCode();
        var clearJson = await clearResponse.Content.ReadAsStringAsync();
        var clearResult = JObject.Parse(clearJson);
        Assert.True(clearResult["success"]?.Value<bool>());

        // Add more logs
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:01:00Z",
            Level = LogLevel.Log,
            Message = "After clear"
        });
        await Task.Delay(200);

        // Tail should only show logs after clear
        var response = await _fixture.HttpClient.GetAsync("/logs/tail?lines=0&source=console");
        var json = await response.Content.ReadAsStringAsync();
        var result = JObject.Parse(json);
        var entries = result["entries"] as JArray;

        Assert.NotNull(entries);
        Assert.Single(entries);
        Assert.Equal("After clear", entries[0]["message"]?.ToString());
    }

    [Fact]
    public async Task LogsTail_FullFlag_IgnoresWatermark()
    {
        // Add logs and clear
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Before clear"
        });
        await Task.Delay(100);
        await _fixture.HttpClient.PostAsync("/logs/clear", null);

        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:01:00Z",
            Level = LogLevel.Log,
            Message = "After clear"
        });
        await Task.Delay(200);

        // full=true should show all logs
        var response = await _fixture.HttpClient.GetAsync("/logs/tail?lines=0&source=console&full=true");
        var json = await response.Content.ReadAsStringAsync();
        var result = JObject.Parse(json);
        var entries = result["entries"] as JArray;

        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task LogsStream_ReceivesSSEEvents()
    {
        // Start SSE stream
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new HttpRequestMessage(HttpMethod.Get, "/logs/stream?source=console");
        var response = await _fixture.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // Give the Bridge time to register the SSE subscriber
        await Task.Delay(100);

        // Send a log event
        await _fixture.FakeUnity.SendEventAsync(UnityCtlEvents.Log, new LogEntry
        {
            Timestamp = "2024-01-01T12:00:00Z",
            Level = LogLevel.Log,
            Message = "Streamed log"
        });

        // Read from the SSE stream
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var line = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(line);
        Assert.StartsWith("data: ", line);

        var eventJson = line["data: ".Length..];
        var logEntry = JObject.Parse(eventJson);
        Assert.Equal("Streamed log", logEntry["message"]?.ToString());
    }
}
