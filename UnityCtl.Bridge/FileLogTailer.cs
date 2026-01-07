using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCtl.Bridge;

/// <summary>
/// Tails a file in real-time, yielding new lines as they are appended.
/// </summary>
public class FileLogTailer : IDisposable
{
    private readonly string _logPath;
    private readonly int _pollIntervalMs;
    private bool _disposed;

    /// <summary>
    /// Creates a new file tailer.
    /// </summary>
    /// <param name="logPath">Path to the log file to tail</param>
    /// <param name="pollIntervalMs">How often to check for new content (default: 100ms)</param>
    public FileLogTailer(string logPath, int pollIntervalMs = 100)
    {
        _logPath = logPath;
        _pollIntervalMs = pollIntervalMs;
    }

    /// <summary>
    /// Asynchronously yields new lines as they are appended to the file.
    /// Starts from the current end of file (skips existing content).
    /// </summary>
    public async IAsyncEnumerable<string> TailAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Wait for log file to exist (Unity creates it on start)
        while (!File.Exists(_logPath) && !ct.IsCancellationRequested)
        {
            await Task.Delay(_pollIntervalMs, ct);
        }

        if (ct.IsCancellationRequested)
            yield break;

        // Open file with shared read access (Unity keeps it open for writing)
        using var fs = new FileStream(
            _logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        // Seek to end to skip existing content
        fs.Seek(0, SeekOrigin.End);
        long position = fs.Position;

        using var reader = new StreamReader(fs);
        var lineBuffer = string.Empty;

        while (!ct.IsCancellationRequested)
        {
            // Check if file was truncated/rotated (position beyond file end)
            var fileInfo = new FileInfo(_logPath);
            if (!fileInfo.Exists || fileInfo.Length < position)
            {
                // File was deleted or rotated - exit and let caller create a new tailer
                // This handles the case where editor.log was moved to editor-prev.log
                // and a new editor.log was created
                yield break;
            }

            // Read any new content
            var chunk = await reader.ReadToEndAsync(ct);
            if (!string.IsNullOrEmpty(chunk))
            {
                // Combine with any partial line from previous read
                var content = lineBuffer + chunk;
                lineBuffer = string.Empty;

                // Split into lines
                var lines = content.Split('\n');

                // If content doesn't end with newline, last element is partial
                if (!content.EndsWith('\n') && lines.Length > 0)
                {
                    lineBuffer = lines[^1];
                    lines = lines[..^1];
                }

                foreach (var line in lines)
                {
                    var trimmed = line.TrimEnd('\r');
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        yield return trimmed;
                    }
                }

                position = fs.Position;
            }
            else
            {
                // No new content, wait before polling again
                await Task.Delay(_pollIntervalMs, ct);
            }
        }

        // Yield any remaining partial line
        if (!string.IsNullOrEmpty(lineBuffer))
        {
            yield return lineBuffer.TrimEnd('\r');
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
