using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnityCtl.Bridge;

/// <summary>
/// Represents a filtered log line ready for output.
/// </summary>
public class FilteredLine
{
    public required string Text { get; init; }
    public ConsoleColor? Color { get; init; }
}

/// <summary>
/// Rule-based log analyzer inspired by u3d's log filtering system.
/// Uses a whitelist approach - only matching rules produce output.
/// Supports phases, multi-line patterns, memory buffer backtracking, and template injection.
/// </summary>
public class LogAnalyzer
{
    private const int MemorySize = 10;

    // Circular buffer for backtracking (Debug.Log message extraction)
    private readonly string?[] _linesMemory = new string?[MemorySize];
    private int _memoryIndex = 0;

    // Loaded rules
    private readonly Dictionary<string, LogRule> _generalRules = new();
    private readonly Dictionary<string, LogPhase> _phases = new();

    // State
    private string? _activePhase;
    private string? _activeRuleName;
    private LogRule? _activeRule;
    private readonly Dictionary<string, string> _context = new();
    private readonly List<string> _ruleBuffer = new();

    // Output queue (for multi-line rules that output multiple lines)
    private readonly Queue<FilteredLine> _outputQueue = new();

    public LogAnalyzer(string? rulesPath = null)
    {
        LoadRules(rulesPath);
    }

    /// <summary>
    /// Process a log line. Returns filtered output or null if line should be hidden.
    /// May return multiple lines for buffered multi-line rules.
    /// </summary>
    public FilteredLine? ParseLine(string line)
    {
        // Push to memory buffer
        _linesMemory[_memoryIndex] = line;
        _memoryIndex = (_memoryIndex + 1) % MemorySize;

        // Check if there are queued outputs from previous calls
        if (_outputQueue.TryDequeue(out var queued))
        {
            // Re-process this line on next call
            _memoryIndex = (_memoryIndex - 1 + MemorySize) % MemorySize;
            return queued;
        }

        // Check for phase transitions
        foreach (var (phaseName, phase) in _phases)
        {
            if (phaseName == _activePhase)
                continue;

            if (phase.PhaseStartRegex?.IsMatch(line) == true)
            {
                if (_activePhase != null)
                {
                    FinishPhase();
                }
                _activePhase = phaseName;
                break;
            }
        }

        // Apply phase-specific rules first
        FilteredLine? result = null;
        if (_activePhase != null && _phases.TryGetValue(_activePhase, out var activePhase))
        {
            result = ApplyRuleset(activePhase.Rules, _activePhase, line);

            // Check for phase end
            if (activePhase.PhaseEndRegex?.IsMatch(line) == true)
            {
                FinishPhase();
            }
        }

        // Apply general rules if no phase rule matched
        if (result == null)
        {
            result = ApplyRuleset(_generalRules, "GENERAL", line);
        }

        return result;
    }

    private void LoadRules(string? rulesPath)
    {
        string json;

        // Check environment variable for custom rules path
        rulesPath ??= Environment.GetEnvironmentVariable("UNITYCTL_RULES_PATH");

        if (rulesPath != null)
        {
            if (!File.Exists(rulesPath))
            {
                throw new FileNotFoundException($"Rules file not found: {rulesPath}");
            }
            json = File.ReadAllText(rulesPath);
        }
        else
        {
            // Load embedded resource
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "UnityCtl.Bridge.log_rules.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
            }

            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, options)
            ?? throw new InvalidOperationException("Failed to parse rules JSON");

        // Parse GENERAL section
        if (config.TryGetValue("GENERAL", out var generalElement))
        {
            var generalPhase = ParsePhase(generalElement);
            if (generalPhase.Active)
            {
                foreach (var (ruleName, rule) in generalPhase.Rules)
                {
                    _generalRules[ruleName] = rule;
                }
            }
            config.Remove("GENERAL");
        }

        // Parse other phases
        foreach (var (phaseName, phaseElement) in config)
        {
            var phase = ParsePhase(phaseElement);
            if (phase.Active && phase.PhaseStartRegex != null)
            {
                _phases[phaseName] = phase;
            }
        }
    }

    private LogPhase ParsePhase(JsonElement element)
    {
        var phase = new LogPhase();

        if (element.TryGetProperty("active", out var activeEl))
            phase.Active = activeEl.GetBoolean();

        if (element.TryGetProperty("silent", out var silentEl))
            phase.Silent = silentEl.GetBoolean();

        if (element.TryGetProperty("phase_start_pattern", out var startEl))
        {
            var pattern = startEl.GetString();
            if (!string.IsNullOrEmpty(pattern))
                phase.PhaseStartRegex = new Regex(pattern, RegexOptions.Compiled);
        }

        if (element.TryGetProperty("phase_end_pattern", out var endEl))
        {
            var pattern = endEl.GetString();
            if (!string.IsNullOrEmpty(pattern))
                phase.PhaseEndRegex = new Regex(pattern, RegexOptions.Compiled);
        }

        if (element.TryGetProperty("rules", out var rulesEl))
        {
            foreach (var ruleProp in rulesEl.EnumerateObject())
            {
                var rule = ParseRule(ruleProp.Value);
                if (rule != null)
                {
                    phase.Rules[ruleProp.Name] = rule;
                }
            }
        }

        return phase;
    }

    private LogRule? ParseRule(JsonElement element)
    {
        var rule = new LogRule();

        if (element.TryGetProperty("active", out var activeEl) && !activeEl.GetBoolean())
            return null;

        if (!element.TryGetProperty("start_pattern", out var startEl))
            return null;

        var startPattern = startEl.GetString();
        if (string.IsNullOrEmpty(startPattern))
            return null;

        rule.StartRegex = new Regex(startPattern, RegexOptions.Compiled);

        if (element.TryGetProperty("end_pattern", out var endEl))
        {
            var endPattern = endEl.GetString();
            if (!string.IsNullOrEmpty(endPattern))
                rule.EndRegex = new Regex(endPattern, RegexOptions.Compiled);
        }

        if (element.TryGetProperty("start_message", out var startMsgEl))
        {
            if (startMsgEl.ValueKind == JsonValueKind.False)
                rule.StartMessageSilent = true;
            else if (startMsgEl.ValueKind == JsonValueKind.String)
                rule.StartMessageTemplate = startMsgEl.GetString();
        }

        if (element.TryGetProperty("end_message", out var endMsgEl))
        {
            if (endMsgEl.ValueKind == JsonValueKind.False)
                rule.EndMessageSilent = true;
            else if (endMsgEl.ValueKind == JsonValueKind.String)
                rule.EndMessageTemplate = endMsgEl.GetString();
        }

        if (element.TryGetProperty("type", out var typeEl))
        {
            var type = typeEl.GetString()?.ToLowerInvariant();
            rule.Type = type switch
            {
                "error" => LogType.Error,
                "warning" or "important" => LogType.Warning,
                "success" => LogType.Success,
                _ => LogType.Message
            };
        }

        if (element.TryGetProperty("store_lines", out var storeEl))
            rule.StoreLines = storeEl.GetBoolean();

        if (element.TryGetProperty("ignore_lines", out var ignoreEl))
        {
            foreach (var pattern in ignoreEl.EnumerateArray())
            {
                var p = pattern.GetString();
                if (!string.IsNullOrEmpty(p))
                    rule.IgnoreRegexes.Add(new Regex(p, RegexOptions.Compiled));
            }
        }

        if (element.TryGetProperty("fetch_line_at_index", out var fetchIndexEl))
        {
            var idx = fetchIndexEl.GetInt32();
            if (idx > 0 && idx < MemorySize)
                rule.FetchLineAtIndex = idx;
        }

        if (element.TryGetProperty("fetch_first_line_not_matching", out var fetchNotMatchEl))
        {
            foreach (var pattern in fetchNotMatchEl.EnumerateArray())
            {
                var p = pattern.GetString();
                if (!string.IsNullOrEmpty(p))
                    rule.FetchFirstLineNotMatchingRegexes.Add(new Regex(p, RegexOptions.Compiled));
            }
        }

        if (element.TryGetProperty("fetched_line_pattern", out var fetchedPatternEl))
        {
            var pattern = fetchedPatternEl.GetString();
            if (!string.IsNullOrEmpty(pattern))
                rule.FetchedLineRegex = new Regex(pattern, RegexOptions.Compiled);
        }

        if (element.TryGetProperty("fetched_line_message", out var fetchedMsgEl))
        {
            if (fetchedMsgEl.ValueKind == JsonValueKind.False)
                rule.FetchedMessageSilent = true;
            else if (fetchedMsgEl.ValueKind == JsonValueKind.String)
                rule.FetchedMessageTemplate = fetchedMsgEl.GetString();
        }

        return rule;
    }

    private FilteredLine? ApplyRuleset(Dictionary<string, LogRule> rules, string header, string line)
    {
        // If there's an active multi-line rule, check for end
        if (_activeRule != null && _activeRuleName != null)
        {
            if (_activeRule.EndRegex?.IsMatch(line) == true)
            {
                // Output buffered lines
                foreach (var bufferedLine in _ruleBuffer)
                {
                    _outputQueue.Enqueue(new FilteredLine
                    {
                        Text = $"[{header}] {bufferedLine}",
                        Color = GetColor(_activeRule.Type)
                    });
                }

                // Output end message
                FilteredLine? result = null;
                if (!_activeRule.EndMessageSilent)
                {
                    var match = _activeRule.EndRegex.Match(line);
                    var message = _activeRule.EndMessageTemplate != null
                        ? Inject(_activeRule.EndMessageTemplate, ExtractCaptures(match))
                        : line.TrimEnd('\r', '\n');

                    result = new FilteredLine
                    {
                        Text = $"[{header}] {message}",
                        Color = GetColor(_activeRule.Type)
                    };
                }

                // Clear state
                _activeRule = null;
                _activeRuleName = null;
                _context.Clear();
                _ruleBuffer.Clear();

                // Return end message or first queued item
                if (result != null)
                    return result;
                if (_outputQueue.TryDequeue(out var queued))
                    return queued;
                return null;
            }

            // Still in multi-line rule, maybe buffer the line
            if (_activeRule.StoreLines)
            {
                bool shouldIgnore = _activeRule.IgnoreRegexes.Any(r => r.IsMatch(line));
                if (!shouldIgnore)
                {
                    _ruleBuffer.Add(line.TrimEnd('\r', '\n'));
                }
            }

            return null;
        }

        // No active rule, try to match a new one
        foreach (var (ruleName, rule) in rules)
        {
            if (rule.StartRegex?.IsMatch(line) != true)
                continue;

            // Set as active rule if it has an end pattern
            if (rule.EndRegex != null)
            {
                _activeRule = rule;
                _activeRuleName = ruleName;
            }

            // Extract captures to context
            var match = rule.StartRegex.Match(line);
            _context.Clear();
            foreach (var name in rule.StartRegex.GetGroupNames().Where(n => !int.TryParse(n, out _)))
            {
                var group = match.Groups[name];
                if (group.Success)
                    _context[name] = group.Value;
            }

            // Handle memory fetch
            string? fetchedLine = null;
            if (rule.FetchLineAtIndex.HasValue)
            {
                fetchedLine = GetFromMemory(rule.FetchLineAtIndex.Value);
            }
            else if (rule.FetchFirstLineNotMatchingRegexes.Count > 0)
            {
                fetchedLine = GetFirstLineNotMatching(rule.FetchFirstLineNotMatchingRegexes);
            }

            if (fetchedLine != null)
            {
                // Extract from fetched line
                if (rule.FetchedLineRegex != null)
                {
                    var fetchedMatch = rule.FetchedLineRegex.Match(fetchedLine);
                    if (fetchedMatch.Success)
                    {
                        foreach (var name in rule.FetchedLineRegex.GetGroupNames().Where(n => !int.TryParse(n, out _)))
                        {
                            var group = fetchedMatch.Groups[name];
                            if (group.Success)
                                _context[name] = group.Value;
                        }
                    }
                }

                // Output fetched line message
                if (!rule.FetchedMessageSilent)
                {
                    var message = rule.FetchedMessageTemplate != null
                        ? Inject(rule.FetchedMessageTemplate)
                        : fetchedLine.TrimEnd('\r', '\n');

                    return new FilteredLine
                    {
                        Text = $"[{header}] {message}",
                        Color = GetColor(rule.Type)
                    };
                }
            }

            // Output start message
            if (!rule.StartMessageSilent)
            {
                var message = rule.StartMessageTemplate != null
                    ? Inject(rule.StartMessageTemplate)
                    : line.TrimEnd('\r', '\n');

                return new FilteredLine
                {
                    Text = $"[{header}] {message}",
                    Color = GetColor(rule.Type)
                };
            }

            // Rule matched but silent
            return null;
        }

        // No rule matched
        return null;
    }

    private void FinishPhase()
    {
        if (_activeRule != null)
        {
            // Active rule wasn't finished - this indicates an issue
            Console.Error.WriteLine($"[{_activePhase}] Rule '{_activeRuleName}' was not finished before phase ended");
        }

        _activePhase = null;
        _activeRule = null;
        _activeRuleName = null;
        _context.Clear();
        _ruleBuffer.Clear();
    }

    private string? GetFromMemory(int index)
    {
        // Index 1 = most recent line (before current), index 2 = 2 lines ago, etc.
        var actualIndex = (_memoryIndex - 1 - index + MemorySize) % MemorySize;
        return _linesMemory[actualIndex];
    }

    private string? GetFirstLineNotMatching(List<Regex> patterns)
    {
        // Search backwards through memory for first line that doesn't match any pattern
        for (int i = 1; i < MemorySize; i++)
        {
            var line = GetFromMemory(i);
            if (line == null)
                continue;

            bool matches = patterns.Any(p => p.IsMatch(line));
            if (!matches)
                return line;
        }
        return null;
    }

    private string Inject(string template, Dictionary<string, string>? extraParams = null)
    {
        var result = template;
        var allParams = new Dictionary<string, string>(_context);

        if (extraParams != null)
        {
            foreach (var (key, value) in extraParams)
            {
                allParams[key] = value;
            }
        }

        foreach (var (key, value) in allParams)
        {
            result = result.Replace($"%{{{key}}}", value ?? "");
        }

        return result;
    }

    private static Dictionary<string, string> ExtractCaptures(Match match)
    {
        var captures = new Dictionary<string, string>();
        foreach (var name in match.Groups.Keys.Where(n => !int.TryParse(n, out _)))
        {
            var group = match.Groups[name];
            if (group.Success)
                captures[name] = group.Value;
        }
        return captures;
    }

    private static ConsoleColor? GetColor(LogType type) => type switch
    {
        LogType.Error => ConsoleColor.Red,
        LogType.Warning => ConsoleColor.Yellow,
        LogType.Success => ConsoleColor.Green,
        _ => null
    };
}

internal enum LogType
{
    Message,
    Warning,
    Error,
    Success
}

internal class LogRule
{
    public Regex? StartRegex { get; set; }
    public Regex? EndRegex { get; set; }

    // Message handling: Silent = true means no output, Template is the format string (null = use line)
    public bool StartMessageSilent { get; set; }
    public string? StartMessageTemplate { get; set; }
    public bool EndMessageSilent { get; set; }
    public string? EndMessageTemplate { get; set; }
    public bool FetchedMessageSilent { get; set; }
    public string? FetchedMessageTemplate { get; set; }

    public LogType Type { get; set; } = LogType.Message;
    public bool StoreLines { get; set; }
    public List<Regex> IgnoreRegexes { get; } = new();

    public int? FetchLineAtIndex { get; set; }
    public List<Regex> FetchFirstLineNotMatchingRegexes { get; } = new();
    public Regex? FetchedLineRegex { get; set; }
}

internal class LogPhase
{
    public bool Active { get; set; }
    public bool Silent { get; set; }
    public Regex? PhaseStartRegex { get; set; }
    public Regex? PhaseEndRegex { get; set; }
    public Dictionary<string, LogRule> Rules { get; } = new();
}
