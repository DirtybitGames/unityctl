using System;
using System.Text.RegularExpressions;

namespace UnityCtl.Cli.LogFormatting;

public class FilteredLine
{
    public required string Text { get; init; }
    public ConsoleColor? Color { get; init; }
}

/// <summary>
/// Filters Unity log output to show only meaningful lines:
/// - Console output (Debug.Log/Warning/Error messages)
/// - Lifecycle events (project load, domain reload, compilation, etc.)
///
/// Hides noise like stack traces, licensing messages, and verbose internal logs.
/// </summary>
public class LogFilter
{
    private enum FilterState
    {
        Normal,
        SkippingStackTrace,
        SkippingDomainReloadDetails
    }

    // Events to SHOW (with colors)
    private static readonly Regex ProjectLoadedPattern = new(
        @"^\[Project\] Loading completed",
        RegexOptions.Compiled);

    private static readonly Regex DomainReloadPattern = new(
        @"^Domain Reload Profiling: \d+ms",
        RegexOptions.Compiled);

    private static readonly Regex CompilationStartPattern = new(
        @"^\[ScriptCompilation\] Requested",
        RegexOptions.Compiled);

    private static readonly Regex BuildResultPattern = new(
        @"^\*\*\* Tundra build (success|failed)",
        RegexOptions.Compiled);

    private static readonly Regex CompilerErrorPattern = new(
        @"\.cs\(\d+,\d+\):\s*error\s+CS",
        RegexOptions.Compiled);

    private static readonly Regex CompilerWarningPattern = new(
        @"\.cs\(\d+,\d+\):\s*warning\s+CS",
        RegexOptions.Compiled);

    // Patterns that indicate START of stack trace (after a Debug.Log message)
    private static readonly Regex StackTraceStartPattern = new(
        @"^UnityEngine\.Debug:|^UnityEngine\.StackTraceUtility:",
        RegexOptions.Compiled);

    // Patterns that look like stack trace lines
    private static readonly Regex StackTraceContinuePattern = new(
        @"^UnityEngine\.|^UnityEditor\.|^System\.|^\(Filename:|^\s+at\s|^UnityCtl\.",
        RegexOptions.Compiled);

    // Patterns to HIDE (noise)
    private static readonly Regex[] HidePatterns = new[]
    {
        // Licensing
        new Regex(@"^\[Licensing::", RegexOptions.Compiled),
        new Regex(@"^\s+Id:\s+", RegexOptions.Compiled),
        new Regex(@"^\s+Product:\s+", RegexOptions.Compiled),
        new Regex(@"^\s+Type:\s+", RegexOptions.Compiled),
        new Regex(@"^\s+Expiration:\s+", RegexOptions.Compiled),

        // Package manager
        new Regex(@"^\[Package Manager\]", RegexOptions.Compiled),
        new Regex(@"^\s+Packages from \[", RegexOptions.Compiled),
        new Regex(@"^\s+com\.\w+\.", RegexOptions.Compiled),  // Package list
        new Regex(@"^  (Built-in packages|Local packages):", RegexOptions.Compiled),

        // Player connection
        new Regex(@"^Player connection \[", RegexOptions.Compiled),

        // Memory config
        new Regex(@"^\s+""memorysetup-", RegexOptions.Compiled),
        new Regex(@"^\[UnityMemory\]", RegexOptions.Compiled),
        new Regex(@"^Memory consumption", RegexOptions.Compiled),
        new Regex(@"^Total: \d+\.\d+ ms \(FindLiveObjects", RegexOptions.Compiled),

        // Workers
        new Regex(@"^\[Worker\d+\]", RegexOptions.Compiled),

        // Telemetry
        new Regex(@"^##utp:", RegexOptions.Compiled),

        // Mono init
        new Regex(@"^Mono path\[", RegexOptions.Compiled),
        new Regex(@"^Mono config path", RegexOptions.Compiled),
        new Regex(@"^Using monoOptions", RegexOptions.Compiled),
        new Regex(@"^Mono: successfully reloaded", RegexOptions.Compiled),
        new Regex(@"^Initialize mono", RegexOptions.Compiled),
        new Regex(@"^Begin MonoManager", RegexOptions.Compiled),
        new Regex(@"^- Loaded All Assemblies", RegexOptions.Compiled),
        new Regex(@"^- Finished resetting", RegexOptions.Compiled),
        new Regex(@"^Registered in \d+\.\d+ seconds", RegexOptions.Compiled),

        // Platform modules
        new Regex(@"^Register platform support module:", RegexOptions.Compiled),
        new Regex(@"^Registering precompiled unity dll", RegexOptions.Compiled),
        new Regex(@"^Native extension for .+ target not found", RegexOptions.Compiled),
        new Regex(@"^\[Subsystems\]", RegexOptions.Compiled),

        // Asset pipeline details
        new Regex(@"^AcceleratorClient", RegexOptions.Compiled),
        new Regex(@"^ImportWorker Server", RegexOptions.Compiled),
        new Regex(@"^Using cacheserver", RegexOptions.Compiled),
        new Regex(@"^Library Redirect Path:", RegexOptions.Compiled),
        new Regex(@"^Android Extension", RegexOptions.Compiled),
        new Regex(@"^TrimDiskCacheJob:", RegexOptions.Compiled),
        new Regex(@"^Application\.AssetDatabase", RegexOptions.Compiled),
        new Regex(@"^Asset Pipeline Refresh", RegexOptions.Compiled),
        new Regex(@"^\tSummary:", RegexOptions.Compiled),
        new Regex(@"^\t\tImports:", RegexOptions.Compiled),
        new Regex(@"^\t\tAsset DB", RegexOptions.Compiled),
        new Regex(@"^\t\tScripting:", RegexOptions.Compiled),
        new Regex(@"^\t\tProject Asset", RegexOptions.Compiled),
        new Regex(@"^\t\tAsset File", RegexOptions.Compiled),
        new Regex(@"^\t\tScan Filter", RegexOptions.Compiled),
        new Regex(@"^\tInvoke", RegexOptions.Compiled),
        new Regex(@"^\tApply", RegexOptions.Compiled),
        new Regex(@"^\tScan:", RegexOptions.Compiled),
        new Regex(@"^\tOnSource", RegexOptions.Compiled),
        new Regex(@"^\tCategorize", RegexOptions.Compiled),
        new Regex(@"^\tProcess", RegexOptions.Compiled),
        new Regex(@"^\tImport", RegexOptions.Compiled),
        new Regex(@"^\tPost", RegexOptions.Compiled),
        new Regex(@"^\tHotreload:", RegexOptions.Compiled),
        new Regex(@"^\tGather", RegexOptions.Compiled),
        new Regex(@"^\tUnload", RegexOptions.Compiled),
        new Regex(@"^\tPersist", RegexOptions.Compiled),
        new Regex(@"^\tGenerate", RegexOptions.Compiled),
        new Regex(@"^\tUntracked:", RegexOptions.Compiled),

        // Graphics init
        new Regex(@"^Direct3D:", RegexOptions.Compiled),
        new Regex(@"^\s+Version:\s+Direct3D", RegexOptions.Compiled),
        new Regex(@"^\s+Renderer:\s+", RegexOptions.Compiled),
        new Regex(@"^\s+Vendor:\s+", RegexOptions.Compiled),
        new Regex(@"^\s+VRAM:\s+", RegexOptions.Compiled),
        new Regex(@"^\s+Driver:\s+", RegexOptions.Compiled),
        new Regex(@"^GfxDevice:", RegexOptions.Compiled),
        new Regex(@"^kGfxThreadingMode", RegexOptions.Compiled),
        new Regex(@"^Initialize engine version:", RegexOptions.Compiled),
        new Regex(@"^Refreshing native plugins", RegexOptions.Compiled),
        new Regex(@"^Preloading \d+ native plugins", RegexOptions.Compiled),
        new Regex(@"^Launched and connected shader compiler", RegexOptions.Compiled),

        // Physics
        new Regex(@"^\[Physics::Module\]", RegexOptions.Compiled),

        // Layout/Modes
        new Regex(@"^\[LAYOUT\]", RegexOptions.Compiled),
        new Regex(@"^\[MODES\]", RegexOptions.Compiled),

        // Scene loading details
        new Regex(@"^Unloading \d+ (Unused Serialized|unused Assets)", RegexOptions.Compiled),
        new Regex(@"^Loaded scene '", RegexOptions.Compiled),
        new Regex(@"^\tDeserialize:", RegexOptions.Compiled),
        new Regex(@"^\tIntegration", RegexOptions.Compiled),
        new Regex(@"^\tThread Wait", RegexOptions.Compiled),
        new Regex(@"^\tTotal Operation", RegexOptions.Compiled),

        // Input system
        new Regex(@"^<RI>", RegexOptions.Compiled),
        new Regex(@"^Using Windows\.Gaming\.Input", RegexOptions.Compiled),

        // Build system details
        new Regex(@"^Starting: .+\\bee_backend\.exe", RegexOptions.Compiled),
        new Regex(@"^WorkingDir:", RegexOptions.Compiled),
        new Regex(@"^ExitCode: \d+ Duration:", RegexOptions.Compiled),
        new Regex(@"^\[.+/\d+\s+\d+s\]", RegexOptions.Compiled),  // Progress like [794/1023 0s]
        new Regex(@"^AssetDatabase: script compilation time", RegexOptions.Compiled),

        // Project loading breakdown
        new Regex(@"^\tProject init time:", RegexOptions.Compiled),
        new Regex(@"^\t\tTemplate init", RegexOptions.Compiled),
        new Regex(@"^\t\tPackage Manager init", RegexOptions.Compiled),
        new Regex(@"^\t\tAsset Database init", RegexOptions.Compiled),
        new Regex(@"^\t\tGlobal illumination", RegexOptions.Compiled),
        new Regex(@"^\t\tAssemblies load", RegexOptions.Compiled),
        new Regex(@"^\t\tUnity extensions init", RegexOptions.Compiled),
        new Regex(@"^\t\tAsset Database refresh", RegexOptions.Compiled),
        new Regex(@"^\tScene opening time:", RegexOptions.Compiled),

        // Misc
        new Regex(@"^Package Manager log level", RegexOptions.Compiled),
        new Regex(@"^Initializing Unity extensions", RegexOptions.Compiled),
        new Regex(@"^Scanning for USB devices", RegexOptions.Compiled),
        new Regex(@"^Created GICache", RegexOptions.Compiled),

        // All tab-indented lines (profiling breakdown details)
        new Regex(@"^\t", RegexOptions.Compiled),
    };

    // Tab-indented lines (domain reload breakdown, asset pipeline details, etc.)
    private static readonly Regex TabIndentedPattern = new(
        @"^\t",
        RegexOptions.Compiled);

    private readonly bool _useColor;
    private FilterState _state = FilterState.Normal;

    public LogFilter(bool useColor = true)
    {
        _useColor = useColor;
    }

    /// <summary>
    /// Process a log line. Returns null if the line should be hidden.
    /// </summary>
    public FilteredLine? ProcessLine(string line)
    {
        // Handle state transitions
        switch (_state)
        {
            case FilterState.SkippingStackTrace:
                if (StackTraceContinuePattern.IsMatch(line) || string.IsNullOrWhiteSpace(line))
                {
                    // Continue skipping
                    if (line.StartsWith("(Filename:"))
                    {
                        // End of stack trace
                        _state = FilterState.Normal;
                    }
                    return null;
                }
                // Not a stack trace line, return to normal
                _state = FilterState.Normal;
                break;

            case FilterState.SkippingDomainReloadDetails:
                if (TabIndentedPattern.IsMatch(line))
                {
                    // Continue skipping indented details
                    return null;
                }
                // Not an indented line, return to normal
                _state = FilterState.Normal;
                break;
        }

        // Hide stack trace lines (anywhere, not just after state transition)
        if (StackTraceStartPattern.IsMatch(line) || StackTraceContinuePattern.IsMatch(line))
        {
            return null;
        }

        // Check hide patterns
        foreach (var pattern in HidePatterns)
        {
            if (pattern.IsMatch(line))
                return null;
        }

        // Check for events to show with special colors
        if (ProjectLoadedPattern.IsMatch(line))
        {
            return new FilteredLine
            {
                Text = line,
                Color = _useColor ? ConsoleColor.Green : null
            };
        }

        if (DomainReloadPattern.IsMatch(line))
        {
            _state = FilterState.SkippingDomainReloadDetails;
            return new FilteredLine
            {
                Text = line,
                Color = _useColor ? ConsoleColor.Cyan : null
            };
        }

        if (CompilationStartPattern.IsMatch(line))
        {
            return new FilteredLine
            {
                Text = line,
                Color = _useColor ? ConsoleColor.Cyan : null
            };
        }

        if (BuildResultPattern.IsMatch(line))
        {
            var isSuccess = line.Contains("success");
            return new FilteredLine
            {
                Text = line,
                Color = _useColor ? (isSuccess ? ConsoleColor.Green : ConsoleColor.Red) : null
            };
        }

        if (CompilerErrorPattern.IsMatch(line))
        {
            return new FilteredLine
            {
                Text = line,
                Color = _useColor ? ConsoleColor.Red : null
            };
        }

        if (CompilerWarningPattern.IsMatch(line))
        {
            return new FilteredLine
            {
                Text = line,
                Color = _useColor ? ConsoleColor.Yellow : null
            };
        }

        // For normal lines that aren't explicitly hidden or shown as events,
        // they might be console output. Buffer it in case next line is stack trace.
        // If next line isn't stack trace, we'll emit it then.

        // But for now, keep it simple: show lines that don't match hide patterns
        // and aren't part of stack traces
        return new FilteredLine
        {
            Text = line,
            Color = null
        };
    }

    /// <summary>
    /// Write a filtered line to console with color and timestamp.
    /// </summary>
    public static void WriteLine(FilteredLine line)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[{timestamp}] ");
        Console.ResetColor();

        if (line.Color.HasValue)
        {
            Console.ForegroundColor = line.Color.Value;
            Console.WriteLine(line.Text);
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine(line.Text);
        }
    }
}
