using System;
using Newtonsoft.Json;

namespace UnityCtl.Cli;

/// <summary>
/// Represents a plugin.json manifest file that declares CLI commands backed by C# scripts.
/// </summary>
public class PluginManifest
{
    [JsonProperty("name")]
    public required string Name { get; set; }

    [JsonProperty("version")]
    public string? Version { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("commands")]
    public PluginCommandDefinition[] Commands { get; set; } = Array.Empty<PluginCommandDefinition>();

    [JsonProperty("skill")]
    public PluginSkillSection? Skill { get; set; }
}

public class PluginCommandDefinition
{
    [JsonProperty("name")]
    public required string Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("arguments")]
    public PluginArgumentDefinition[] Arguments { get; set; } = Array.Empty<PluginArgumentDefinition>();

    [JsonProperty("options")]
    public PluginOptionDefinition[] Options { get; set; } = Array.Empty<PluginOptionDefinition>();

    [JsonProperty("handler")]
    public required PluginHandler Handler { get; set; }
}

public class PluginArgumentDefinition
{
    [JsonProperty("name")]
    public required string Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("required")]
    public bool Required { get; set; }
}

public class PluginOptionDefinition
{
    [JsonProperty("name")]
    public required string Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = "string";
}

public class PluginHandler
{
    [JsonProperty("type")]
    public string Type { get; set; } = "script";

    [JsonProperty("file")]
    public required string File { get; set; }
}

public class PluginSkillSection
{
    [JsonProperty("file")]
    public required string File { get; set; }
}
