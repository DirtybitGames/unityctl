using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public class BridgeClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _agentId;
    private readonly string? _projectRoot;

    public BridgeClient(string baseUrl, string? agentId = null, string? projectRoot = null)
    {
        _baseUrl = baseUrl;
        _agentId = agentId;
        _projectRoot = projectRoot;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    public static BridgeClient? TryCreateFromProject(string? projectPath, string? agentId)
    {
        var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project.");
            Console.Error.WriteLine("  Use --project to specify project root, or run:");
            Console.Error.WriteLine("  unityctl config set project-path <path-to-unity-project>");
            return null;
        }

        var config = ProjectLocator.ReadBridgeConfig(projectRoot);
        if (config == null)
        {
            // Check if Unity is running to provide better guidance
            var unityStatus = ProjectLocator.CheckUnityEditorStatus(projectRoot);
            Console.Error.WriteLine("Error: Bridge not configured.");

            if (unityStatus.Status == UnityEditorStatus.Running)
            {
                Console.Error.WriteLine("  Unity Editor is running. Run 'unityctl bridge start' to start the bridge.");
            }
            else
            {
                Console.Error.WriteLine("  Run 'unityctl bridge start' first, then start Unity Editor.");
            }
            return null;
        }

        var baseUrl = $"http://localhost:{config.Port}";
        return new BridgeClient(baseUrl, agentId, projectRoot);
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
                    DisplayUnityNotConnectedError();
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
            DisplayBridgeConnectionError();
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
                    DisplayUnityNotConnectedError();
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
            DisplayBridgeConnectionError();
            return default;
        }
    }

    public async Task<ResponseMessage?> SendCommandAsync(string command, Dictionary<string, object?>? args = null, int? timeoutSeconds = null)
    {
        try
        {
            var request = new
            {
                agentId = _agentId,
                command = command,
                args = args,
                timeout = timeoutSeconds
            };

            var json = JsonHelper.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // The bridge enforces the real timeout server-side. The HTTP timeout
            // just needs to be long enough to not race it. Add a 30s buffer so the
            // bridge always gets to respond first (with a proper 504) rather than
            // the HTTP client throwing a TaskCanceledException.
            using var cts = new CancellationTokenSource();
            if (timeoutSeconds.HasValue)
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.Value + 30));

            var response = await _httpClient.PostAsync("/rpc", content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    DisplayUnityNotConnectedError();
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
            DisplayBridgeConnectionError();
            return null;
        }
    }

    public static async Task<bool> StartBridgeAsync(string? projectPath)
    {
        // Normalize path to absolute to ensure consistent project identification
        var projectRoot = projectPath != null
            ? Path.GetFullPath(projectPath)
            : ProjectLocator.FindProjectRoot();

        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project.");
            Console.Error.WriteLine("  Use --project to specify project root, or run:");
            Console.Error.WriteLine("  unityctl config set project-path <path-to-unity-project>");
            return false;
        }

        // Check if bridge is already running for this project
        var existingConfig = ProjectLocator.ReadBridgeConfig(projectRoot);
        if (existingConfig != null)
        {
            try
            {
                var process = Process.GetProcessById(existingConfig.Pid);
                if (!process.HasExited)
                {
                    // Verify bridge is responding
                    var client = new BridgeClient($"http://localhost:{existingConfig.Port}");
                    var health = await client.GetAsync<HealthResult>("/health");

                    if (health != null)
                    {
                        Console.WriteLine($"Bridge already running for project: {projectRoot}");
                        Console.WriteLine($"  PID: {existingConfig.Pid}");
                        Console.WriteLine($"  Port: {existingConfig.Port}");
                        return true;
                    }
                }
            }
            catch (ArgumentException)
            {
                // Process not found - stale config, continue to start new bridge
            }
            catch (InvalidOperationException)
            {
                // Process has exited - stale config, continue to start new bridge
            }
        }

        Console.WriteLine($"Starting bridge for project: {projectRoot}");

        try
        {
            // Start bridge as fully detached process (survives terminal close, Ctrl+C doesn't affect it)
            var startInfo = new ProcessStartInfo
            {
                FileName = "unityctl-bridge",
                Arguments = $"--project \"{projectRoot}\"",
                UseShellExecute = true,  // Required for full detachment
                CreateNoWindow = true,   // Run without visible window
                WindowStyle = ProcessWindowStyle.Hidden
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

    public static async Task<bool> StopBridgeAsync(string? projectPath)
    {
        var projectRoot = projectPath ?? ProjectLocator.FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project.");
            Console.Error.WriteLine("  Use --project to specify project root, or run:");
            Console.Error.WriteLine("  unityctl config set project-path <path-to-unity-project>");
            return false;
        }

        var config = ProjectLocator.ReadBridgeConfig(projectRoot);
        if (config == null)
        {
            Console.Error.WriteLine("Error: Bridge is not running (no config found).");
            return false;
        }

        Console.WriteLine($"Stopping bridge (PID: {config.Pid})...");

        try
        {
            var process = Process.GetProcessById(config.Pid);

            // Kill the process
            process.Kill();

            // Wait for process to exit (with timeout)
            bool exited = process.WaitForExit(5000);

            if (!exited)
            {
                Console.Error.WriteLine("Warning: Bridge process did not exit within timeout, forcing termination...");
                process.Kill(true); // Kill entire process tree
                process.WaitForExit(2000);
            }

            Console.WriteLine("Bridge stopped successfully");
            Console.WriteLine("Note: Config file preserved to allow Unity to reconnect when bridge restarts");
            return true;
        }
        catch (ArgumentException)
        {
            // Process not found
            Console.WriteLine($"Bridge process (PID: {config.Pid}) is not running");
            Console.WriteLine("Note: Config file preserved - bridge may have already stopped");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to stop bridge: {ex.Message}");
            return false;
        }
    }

    private void DisplayUnityNotConnectedError()
    {
        if (_projectRoot != null)
        {
            var unityStatus = ProjectLocator.CheckUnityEditorStatus(_projectRoot);
            if (unityStatus.Status == UnityEditorStatus.NotRunning)
            {
                Console.Error.WriteLine("Error: Unity Editor is not running for this project.");
                Console.Error.WriteLine("Open the project in Unity Editor with the UnityCtl package installed.");
            }
            else
            {
                Console.Error.WriteLine("Error: Unity Editor is running but not connected to the bridge.");
                Console.Error.WriteLine("Ensure the UnityCtl package is installed and enabled in Unity.");
            }
        }
        else
        {
            Console.Error.WriteLine("Error: Unity Editor is not connected to the bridge.");
            Console.Error.WriteLine("Ensure Unity is running with the UnityCtl package installed.");
        }
    }

    private void DisplayBridgeConnectionError()
    {
        Console.Error.WriteLine("Error: Failed to communicate with bridge.");

        if (_projectRoot != null)
        {
            var unityStatus = ProjectLocator.CheckUnityEditorStatus(_projectRoot);
            if (unityStatus.Status == UnityEditorStatus.NotRunning)
            {
                Console.Error.WriteLine("Unity Editor is not running for this project.");
                Console.Error.WriteLine("  1. Start the bridge: unityctl bridge start");
                Console.Error.WriteLine("  2. Open the project in Unity Editor");
            }
            else
            {
                Console.Error.WriteLine("Unity Editor is running but bridge is not responding.");
                Console.Error.WriteLine("  Try: unityctl bridge start");
            }
        }
        else
        {
            Console.Error.WriteLine("The bridge process may not be running. Try: unityctl bridge start");
        }
    }
}
