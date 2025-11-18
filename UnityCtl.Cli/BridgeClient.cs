using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public class BridgeClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _agentId;

    public BridgeClient(string baseUrl, string? agentId = null)
    {
        _baseUrl = baseUrl;
        _agentId = agentId;
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public static BridgeClient? TryCreateFromProject(string? projectPath, string? agentId)
    {
        var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project. Use --project to specify project root.");
            return null;
        }

        var config = ProjectLocator.ReadBridgeConfig(projectRoot);
        if (config == null)
        {
            Console.Error.WriteLine("Error: Bridge not configured. Run 'unityctl bridge start' first.");
            return null;
        }

        var baseUrl = $"http://localhost:{config.Port}";
        return new BridgeClient(baseUrl, agentId);
    }

    public async Task<T?> GetAsync<T>(string endpoint)
    {
        try
        {
            var response = await _httpClient.GetAsync(endpoint);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Console.Error.WriteLine("Error: Unity Editor is not connected to the bridge.");
                    Console.Error.WriteLine("Ensure Unity is running with the UnityCtl package installed.");
                    return default;
                }

                var error = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"Error: Bridge returned {response.StatusCode}");
                Console.Error.WriteLine(error);
                return default;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonHelper.Deserialize<T>(json);
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("Error: Failed to communicate with bridge.");
            Console.Error.WriteLine("The bridge process may not be running. Try: unityctl bridge start");
            return default;
        }
    }

    public async Task<T?> PostAsync<T>(string endpoint, HttpContent content)
    {
        try
        {
            var response = await _httpClient.PostAsync(endpoint, content);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Console.Error.WriteLine("Error: Unity Editor is not connected to the bridge.");
                    Console.Error.WriteLine("Ensure Unity is running with the UnityCtl package installed.");
                    return default;
                }

                var error = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"Error: Bridge returned {response.StatusCode}");
                Console.Error.WriteLine(error);
                return default;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonHelper.Deserialize<T>(json);
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("Error: Failed to communicate with bridge.");
            Console.Error.WriteLine("The bridge process may not be running. Try: unityctl bridge start");
            return default;
        }
    }

    public async Task<ResponseMessage?> SendCommandAsync(string command, Dictionary<string, object?>? args = null)
    {
        try
        {
            var request = new
            {
                agentId = _agentId,
                command = command,
                args = args
            };

            var json = JsonHelper.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/rpc", content);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Console.Error.WriteLine("Error: Unity Editor is not connected to the bridge.");
                    Console.Error.WriteLine("Ensure Unity is running with the UnityCtl package installed.");
                    return null;
                }

                var error = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"Error: Bridge returned {response.StatusCode}");
                Console.Error.WriteLine(error);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonHelper.Deserialize<ResponseMessage>(responseJson);
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("Error: Failed to communicate with bridge.");
            Console.Error.WriteLine("The bridge process may not be running. Try: unityctl bridge start");
            return null;
        }
    }

    public static async Task<bool> StartBridgeAsync(string? projectPath)
    {
        var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project. Use --project to specify project root.");
            return false;
        }

        Console.WriteLine($"Starting bridge for project: {projectRoot}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "unityctl-bridge",
                Arguments = $"--project \"{projectRoot}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine("Error: Failed to start bridge process");
                return false;
            }

            // Wait a bit for bridge to start and write config
            await Task.Delay(2000);

            // Check if bridge is responding
            var config = ProjectLocator.ReadBridgeConfig(projectRoot);
            if (config != null)
            {
                Console.WriteLine($"Bridge started successfully (PID: {config.Pid}, Port: {config.Port})");
                return true;
            }
            else
            {
                Console.Error.WriteLine("Warning: Bridge process started but config file not found");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to start bridge: {ex.Message}");
            return false;
        }
    }
}
