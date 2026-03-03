using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCtl.Cli;

/// <summary>
/// TCP client for communicating with UnityCtlDevServer running on a device.
/// Uses length-prefixed JSON messages matching the player's protocol.
/// </summary>
internal class DeviceClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public DeviceClient(string host = "localhost", int port = 7400)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync(int timeoutMs = 5000)
    {
        _client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            await _client.ConnectAsync(_host, _port, cts.Token);
            _stream = _client.GetStream();
        }
        catch (OperationCanceledException)
        {
            throw new DeviceConnectionException(
                $"Connection to device at {_host}:{_port} timed out after {timeoutMs}ms.\n" +
                "Ensure the app is running and ADB port forwarding is set up:\n" +
                $"  adb forward tcp:{_port} tcp:{_port}");
        }
        catch (SocketException ex)
        {
            throw new DeviceConnectionException(
                $"Failed to connect to device at {_host}:{_port}: {ex.Message}\n" +
                "Ensure the app is running and ADB port forwarding is set up:\n" +
                $"  adb forward tcp:{_port} tcp:{_port}");
        }
    }

    public async Task<JsonDocument?> SendCommandAsync(string command, Dictionary<string, object?>? args = null, int timeoutSeconds = 30)
    {
        if (_stream == null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var request = new Dictionary<string, object?>
        {
            ["requestId"] = Guid.NewGuid().ToString("N"),
            ["command"] = command,
            ["args"] = args
        };

        var json = JsonSerializer.Serialize(request);
        var messageBytes = Encoding.UTF8.GetBytes(json);

        // Write length prefix + message
        var lengthBytes = BitConverter.GetBytes(messageBytes.Length);
        await _stream.WriteAsync(lengthBytes);
        await _stream.WriteAsync(messageBytes);
        await _stream.FlushAsync();

        // Read response length prefix
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var responseLengthBuf = new byte[4];
        var read = await ReadExactAsync(_stream, responseLengthBuf, 4, cts.Token);
        if (read < 4)
            throw new DeviceConnectionException("Device disconnected while waiting for response");

        var responseLength = BitConverter.ToInt32(responseLengthBuf, 0);
        if (responseLength <= 0 || responseLength > 10 * 1024 * 1024)
            throw new DeviceConnectionException($"Invalid response length: {responseLength}");

        // Read response body
        var responseBuf = new byte[responseLength];
        read = await ReadExactAsync(_stream, responseBuf, responseLength, cts.Token);
        if (read < responseLength)
            throw new DeviceConnectionException("Device disconnected while reading response");

        var responseJson = Encoding.UTF8.GetString(responseBuf);
        return JsonDocument.Parse(responseJson);
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (n == 0) return totalRead;
            totalRead += n;
        }
        return totalRead;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }
}

internal class DeviceConnectionException : Exception
{
    public DeviceConnectionException(string message) : base(message) { }
}
