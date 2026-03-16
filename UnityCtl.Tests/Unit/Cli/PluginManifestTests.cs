using System;
using Newtonsoft.Json;
using UnityCtl.Cli;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class PluginManifestTests
{
    [Fact]
    public void Deserialize_FullManifest_AllFieldsPopulated()
    {
        var json = """
        {
          "name": "my-tool",
          "version": "2.1.0",
          "description": "A cool tool",
          "commands": [
            {
              "name": "run",
              "description": "Run the tool",
              "arguments": [
                { "name": "target", "description": "Target name", "required": true }
              ],
              "options": [
                { "name": "verbose", "type": "bool", "description": "Verbose output" },
                { "name": "format", "type": "string", "description": "Output format" }
              ],
              "handler": { "type": "script", "file": "run.cs" }
            }
          ],
          "skill": { "file": "SKILL-SECTION.md" }
        }
        """;

        var manifest = JsonConvert.DeserializeObject<PluginManifest>(json)!;

        Assert.Equal("my-tool", manifest.Name);
        Assert.Equal("2.1.0", manifest.Version);
        Assert.Equal("A cool tool", manifest.Description);
        Assert.Single(manifest.Commands);

        var cmd = manifest.Commands[0];
        Assert.Equal("run", cmd.Name);
        Assert.Equal("Run the tool", cmd.Description);
        Assert.Single(cmd.Arguments);
        Assert.Equal("target", cmd.Arguments[0].Name);
        Assert.True(cmd.Arguments[0].Required);
        Assert.Equal(2, cmd.Options.Length);
        Assert.Equal("verbose", cmd.Options[0].Name);
        Assert.Equal("bool", cmd.Options[0].Type);
        Assert.Equal("format", cmd.Options[1].Name);
        Assert.Equal("string", cmd.Options[1].Type);
        Assert.Equal("script", cmd.Handler.Type);
        Assert.Equal("run.cs", cmd.Handler.File);

        Assert.NotNull(manifest.Skill);
        Assert.Equal("SKILL-SECTION.md", manifest.Skill!.File);
    }

    [Fact]
    public void Deserialize_MinimalManifest_DefaultsPopulated()
    {
        var json = """
        {
          "name": "bare",
          "commands": [
            {
              "name": "do",
              "handler": { "file": "do.cs" }
            }
          ]
        }
        """;

        var manifest = JsonConvert.DeserializeObject<PluginManifest>(json)!;

        Assert.Equal("bare", manifest.Name);
        Assert.Null(manifest.Version);
        Assert.Null(manifest.Description);
        Assert.Single(manifest.Commands);

        var cmd = manifest.Commands[0];
        Assert.Null(cmd.Description);
        Assert.Empty(cmd.Arguments);
        Assert.Empty(cmd.Options);
        Assert.Equal("script", cmd.Handler.Type); // default
        Assert.Null(manifest.Skill);
    }

    [Fact]
    public void Deserialize_NoCommands_EmptyArray()
    {
        var json = """{ "name": "empty" }""";

        var manifest = JsonConvert.DeserializeObject<PluginManifest>(json)!;

        Assert.Equal("empty", manifest.Name);
        Assert.Empty(manifest.Commands);
    }

    [Fact]
    public void Deserialize_UnknownFields_Ignored()
    {
        var json = """
        {
          "name": "future",
          "futureField": true,
          "commands": [
            {
              "name": "cmd",
              "handler": { "file": "cmd.cs", "runtime": "mono" }
            }
          ]
        }
        """;

        var manifest = JsonConvert.DeserializeObject<PluginManifest>(json)!;

        Assert.Equal("future", manifest.Name);
        Assert.Single(manifest.Commands);
    }

    [Fact]
    public void Deserialize_MultipleCommands_AllPreserved()
    {
        var json = """
        {
          "name": "multi",
          "commands": [
            { "name": "a", "handler": { "file": "a.cs" } },
            { "name": "b", "handler": { "file": "b.cs" } },
            { "name": "c", "handler": { "file": "c.cs" } }
          ]
        }
        """;

        var manifest = JsonConvert.DeserializeObject<PluginManifest>(json)!;

        Assert.Equal(3, manifest.Commands.Length);
        Assert.Equal("a", manifest.Commands[0].Name);
        Assert.Equal("b", manifest.Commands[1].Name);
        Assert.Equal("c", manifest.Commands[2].Name);
    }

    [Fact]
    public void Deserialize_OptionTypeDefaults_ToString()
    {
        var json = """
        {
          "name": "opt-test",
          "commands": [
            {
              "name": "cmd",
              "options": [
                { "name": "no-type" }
              ],
              "handler": { "file": "cmd.cs" }
            }
          ]
        }
        """;

        var manifest = JsonConvert.DeserializeObject<PluginManifest>(json)!;

        Assert.Equal("string", manifest.Commands[0].Options[0].Type);
    }

    [Fact]
    public void Deserialize_ArgumentRequired_DefaultsFalse()
    {
        var json = """
        {
          "name": "arg-test",
          "commands": [
            {
              "name": "cmd",
              "arguments": [
                { "name": "optional-arg" }
              ],
              "handler": { "file": "cmd.cs" }
            }
          ]
        }
        """;

        var manifest = JsonConvert.DeserializeObject<PluginManifest>(json)!;

        Assert.False(manifest.Commands[0].Arguments[0].Required);
    }
}
