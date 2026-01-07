using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCtl.Bridge;

/// <summary>
/// Background service that tails the editor.log file and adds entries to BridgeState.
/// Uses LogAnalyzer (u3d-style rule-based filtering) to filter noise and determine log levels/colors.
/// </summary>
public class EditorLogTailer : IDisposable
{
    private readonly string _projectRoot;
    private readonly BridgeState _state;
    private readonly CancellationTokenSource _cts = new();
    private Task? _tailTask;
    private bool _disposed;
    private bool _warningPrinted;

    public EditorLogTailer(string projectRoot, BridgeState state)
    {
        _projectRoot = projectRoot;
        _state = state;
    }

    /// <summary>
    /// Start tailing the editor log file in the background.
    /// </summary>
    public void Start()
    {
        if (_tailTask != null)
            return;

        _tailTask = Task.Run(() => TailLoopAsync(_cts.Token));
    }

    private async Task TailLoopAsync(CancellationToken ct)
    {
        var analyzer = new LogAnalyzer();
        string? lastResolvedPath = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Resolve log path - re-check each iteration until we find a valid file
                // This handles the case where Unity starts after the bridge
                var resolution = EditorLogPathResolver.Resolve(_projectRoot);
                var logPath = resolution.LogPath;

                // Print warning once if using default global log
                if (resolution.IsDefaultGlobalLog && !_warningPrinted && resolution.WarningMessage != null)
                {
                    Console.WriteLine($"[EditorLogTailer] {resolution.WarningMessage}");
                    _warningPrinted = true;
                }

                // Log path change detection
                if (logPath != lastResolvedPath)
                {
                    Console.WriteLine($"[EditorLogTailer] Watching for: {logPath}");
                    lastResolvedPath = logPath;
                }

                // Wait for the log file to exist
                if (!File.Exists(logPath))
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                Console.WriteLine($"[EditorLogTailer] Starting to tail: {logPath}");

                using var tailer = new FileLogTailer(logPath);

                await foreach (var line in tailer.TailAsync(ct))
                {
                    // Apply rule-based filtering with event emission
                    var (filtered, logEvent) = analyzer.ParseLineWithEvent(line);

                    if (filtered != null)
                    {
                        // Determine level based on color
                        var level = filtered.Color switch
                        {
                            ConsoleColor.Red => "Error",
                            ConsoleColor.Yellow => "Warning",
                            ConsoleColor.Green => "Info",
                            _ => "Log"
                        };

                        _state.AddEditorLogEntry(filtered.Text, level, filtered.Color);
                    }

                    // Notify any waiters if an event was emitted
                    if (logEvent != null)
                    {
                        _state.NotifyLogEvent(logEvent);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (FileNotFoundException)
            {
                // File was deleted, wait and try again
                await Task.Delay(1000, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorLogTailer] Error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }

        Console.WriteLine("[EditorLogTailer] Stopped");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        try
        {
            _tailTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore errors during shutdown
        }

        _cts.Dispose();
    }
}
