using System;
using System.Text.RegularExpressions;

namespace UnityCtl.Cli.LogFormatting;

/// <summary>
/// Formats Unity log lines with color based on log level.
/// </summary>
public class SimpleLogFormatter
{
    // Patterns to detect log levels
    private static readonly Regex ErrorPattern = new(
        @"^(error|exception|Error|Exception|ERROR|EXCEPTION)|\.cs\(\d+,\d+\):\s*error\s+",
        RegexOptions.Compiled);

    private static readonly Regex WarningPattern = new(
        @"^(warning|Warning|WARNING)|\.cs\(\d+,\d+\):\s*warning\s+",
        RegexOptions.Compiled);

    private static readonly Regex StackTracePattern = new(
        @"^\s+(at\s+|in\s+)|^UnityEngine\.|^System\.",
        RegexOptions.Compiled);

    private readonly bool _useColor;

    public SimpleLogFormatter(bool useColor = true)
    {
        _useColor = useColor;
    }

    /// <summary>
    /// Writes a log line to console with appropriate coloring.
    /// </summary>
    public void WriteFormatted(string line)
    {
        if (!_useColor)
        {
            Console.WriteLine(line);
            return;
        }

        var color = DetectColor(line);

        if (color.HasValue)
        {
            Console.ForegroundColor = color.Value;
            Console.WriteLine(line);
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    /// <summary>
    /// Detects the appropriate color for a log line based on its content.
    /// </summary>
    private ConsoleColor? DetectColor(string line)
    {
        // Check for errors first (highest priority)
        if (ErrorPattern.IsMatch(line))
            return ConsoleColor.Red;

        // Check for warnings
        if (WarningPattern.IsMatch(line))
            return ConsoleColor.Yellow;

        // Check for stack traces
        if (StackTracePattern.IsMatch(line))
            return ConsoleColor.DarkGray;

        // Default: no color override
        return null;
    }
}
