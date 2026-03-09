using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class SnapshotCommand
{
    public static Command CreateCommand()
    {
        var snapshotCommand = new Command("snapshot", "Snapshot the scene hierarchy as a compact, LLM-friendly tree with instance IDs");

        var idOption = new Option<int?>(
            "--id",
            "Drill into a specific object by instance ID"
        );

        var depthOption = new Option<int?>(
            "--depth",
            "Max hierarchy depth (default: 2)"
        );

        var componentsOption = new Option<bool>(
            "--components",
            "Include all serialized property values"
        );

        var interactiveOption = new Option<bool>(
            "--interactive",
            "Show UI text content and interactable state"
        );

        var layoutOption = new Option<bool>(
            "--layout",
            "Show RectTransform info instead of world position"
        );

        var filterOption = new Option<string?>(
            "--filter",
            "Filter by type:T, name:N*, or tag:T"
        );

        var sceneOption = new Option<string?>(
            "--scene",
            "Snapshot a specific scene (opens additively if not loaded)"
        );

        var prefabOption = new Option<string?>(
            "--prefab",
            "Snapshot a prefab asset"
        );

        snapshotCommand.AddOption(idOption);
        snapshotCommand.AddOption(depthOption);
        snapshotCommand.AddOption(componentsOption);
        snapshotCommand.AddOption(interactiveOption);
        snapshotCommand.AddOption(layoutOption);
        snapshotCommand.AddOption(filterOption);
        snapshotCommand.AddOption(sceneOption);
        snapshotCommand.AddOption(prefabOption);

        snapshotCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var id = context.ParseResult.GetValueForOption(idOption);
            var depth = context.ParseResult.GetValueForOption(depthOption);
            var components = context.ParseResult.GetValueForOption(componentsOption);
            var interactive = context.ParseResult.GetValueForOption(interactiveOption);
            var layout = context.ParseResult.GetValueForOption(layoutOption);
            var filter = context.ParseResult.GetValueForOption(filterOption);
            var scene = context.ParseResult.GetValueForOption(sceneOption);
            var prefab = context.ParseResult.GetValueForOption(prefabOption);

            if (!string.IsNullOrEmpty(scene) && !string.IsNullOrEmpty(prefab))
            {
                Console.Error.WriteLine("Error: Cannot use both --scene and --prefab");
                context.ExitCode = 1;
                return;
            }

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "depth", depth },
                { "id", id },
                { "components", components },
                { "interactive", interactive },
                { "layout", layout },
                { "filter", filter },
                { "scenePath", scene },
                { "prefabPath", prefab }
            };

            var response = await client.SendCommandAsync(UnityCtlCommands.Snapshot, args);
            if (response == null) { context.ExitCode = 1; return; }

            if (response.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {response.Error?.Message}");
                context.ExitCode = 1;
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(response.Result));
            }
            else
            {
                var result = JsonConvert.DeserializeObject<SnapshotResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    FormatSnapshot(result, components, interactive, layout, id.HasValue);
                }
            }
        });

        return snapshotCommand;
    }

    private static void FormatSnapshot(SnapshotResult result, bool components, bool interactive, bool layout, bool isDrillDown)
    {
        var sb = new StringBuilder();

        if (!isDrillDown)
        {
            // Header: stage context
            if (!string.IsNullOrEmpty(result.PrefabAssetPath) && result.Stage == null)
            {
                // --prefab asset inspection
                sb.AppendLine($"Prefab: {result.PrefabAssetPath}");
            }
            else if (!string.IsNullOrEmpty(result.PrefabAssetPath))
            {
                // In prefab editing stage
                sb.AppendLine($"Stage: {result.Stage}");
                sb.AppendLine($"Prefab: {result.PrefabAssetPath}");
                if (result.HasUnsavedChanges == true)
                    sb.AppendLine("Unsaved changes: yes");
                if (result.OpenedFromInstanceId.HasValue)
                    sb.AppendLine($"Opened from: [i:{result.OpenedFromInstanceId}]");
            }
            else
            {
                // Scene mode
                var playIndicator = result.IsPlaying ? " (playing)" : "";
                sb.AppendLine($"Scene: {result.SceneName}{playIndicator}");
            }

            sb.AppendLine($"{result.RootObjectCount} root objects");
            sb.AppendLine();
        }

        foreach (var obj in result.Objects)
        {
            FormatObject(sb, obj, 0, components, interactive, layout, isDrillDown);
        }

        Console.Write(sb.ToString());
    }

    private static void FormatObject(StringBuilder sb, SnapshotObject obj, int indent, bool components, bool interactive, bool layout, bool isDrillDown)
    {
        var prefix = new string(' ', indent * 2);

        // Main line: Name [i:ID] Components  tag:Tag  prefab:path
        sb.Append(prefix);
        sb.Append(obj.Name);
        if (!obj.Active) sb.Append(" [inactive]");
        sb.Append($" [i:{obj.InstanceId}]");

        if (obj.Components != null && obj.Components.Length > 0 && !components)
        {
            sb.Append(' ');
            sb.Append(string.Join(", ", Array.ConvertAll(obj.Components, c => c.TypeName)));
        }

        if (!string.IsNullOrEmpty(obj.Tag)) sb.Append($"  tag:{obj.Tag}");
        if (!string.IsNullOrEmpty(obj.Layer)) sb.Append($"  layer:{obj.Layer}");
        if (obj.IsPrefabInstanceRoot == true && !string.IsNullOrEmpty(obj.PrefabAssetPath))
            sb.Append($"  prefab:{obj.PrefabAssetPath}");
        sb.AppendLine();

        // Position/layout info
        if (components)
        {
            // Full component detail
            FormatDrillDown(sb, obj, prefix, layout);
        }
        else
        {
            // Summary mode
            if (interactive && !string.IsNullOrEmpty(obj.Text))
            {
                sb.AppendLine($"{prefix}  \"{obj.Text}\"{(obj.Interactable == true ? " interactable" : obj.Interactable == false ? " disabled" : "")}");
            }
            else if (layout && !string.IsNullOrEmpty(obj.Rect))
            {
                sb.Append($"{prefix}  {obj.Rect}");
                if (!string.IsNullOrEmpty(obj.Anchors)) sb.Append($" {obj.Anchors}");
                if (!string.IsNullOrEmpty(obj.Pivot)) sb.Append($" pivot{obj.Pivot}");
                sb.AppendLine();
            }
            else if (!string.IsNullOrEmpty(obj.Position))
            {
                sb.Append($"{prefix}  pos{obj.Position}");
                if (!string.IsNullOrEmpty(obj.Scale)) sb.Append($" scale{obj.Scale}");
                sb.AppendLine();
            }
        }

        // Children
        if (obj.Children != null)
        {
            foreach (var child in obj.Children)
            {
                FormatObject(sb, child, indent + 1, components, interactive, layout, isDrillDown);
            }
        }
        else if (obj.ChildCount > 0 && !isDrillDown)
        {
            sb.AppendLine($"{prefix}  ({obj.ChildCount} children)");
        }
    }

    private static void FormatDrillDown(StringBuilder sb, SnapshotObject obj, string prefix, bool layout)
    {
        if (obj.Components != null)
        {
            // Always show Transform info first
            if (!string.IsNullOrEmpty(obj.Position))
            {
                sb.AppendLine($"{prefix}  Transform:");
                sb.AppendLine($"{prefix}    position: {obj.Position}");
                if (!string.IsNullOrEmpty(obj.Rotation))
                    sb.AppendLine($"{prefix}    rotation: {obj.Rotation}");
                if (!string.IsNullOrEmpty(obj.Scale))
                    sb.AppendLine($"{prefix}    localScale: {obj.Scale}");
            }
            if (layout && !string.IsNullOrEmpty(obj.Rect))
            {
                sb.AppendLine($"{prefix}  RectTransform:");
                sb.AppendLine($"{prefix}    rect: {obj.Rect}");
                if (!string.IsNullOrEmpty(obj.Anchors))
                    sb.AppendLine($"{prefix}    anchors: {obj.Anchors}");
                if (!string.IsNullOrEmpty(obj.Pivot))
                    sb.AppendLine($"{prefix}    pivot: {obj.Pivot}");
            }

            // Prefab info in drill-down
            if (obj.IsPrefabInstanceRoot == true && !string.IsNullOrEmpty(obj.PrefabAssetPath))
            {
                sb.AppendLine($"{prefix}  Prefab: {obj.PrefabAssetPath} ({obj.PrefabAssetType ?? "Unknown"})");
            }

            foreach (var comp in obj.Components)
            {
                sb.AppendLine($"{prefix}  {comp.TypeName}:");
                if (comp.Properties != null)
                {
                    foreach (var kvp in comp.Properties)
                    {
                        FormatPropertyValue(sb, kvp.Key, kvp.Value, $"{prefix}    ");
                    }
                }
            }
        }

        // Show children summary in drill-down
        if (obj.ChildCount > 0)
        {
            sb.AppendLine($"{prefix}  Children ({obj.ChildCount}):");
            if (obj.Children != null)
            {
                foreach (var child in obj.Children)
                {
                    sb.Append($"{prefix}    {child.Name} [i:{child.InstanceId}]");
                    if (child.Components != null && child.Components.Length > 0)
                    {
                        sb.Append(' ');
                        sb.Append(string.Join(", ", Array.ConvertAll(child.Components, c => c.TypeName)));
                    }
                    if (child.IsPrefabInstanceRoot == true && !string.IsNullOrEmpty(child.PrefabAssetPath))
                        sb.Append($"  prefab:{child.PrefabAssetPath}");
                    sb.AppendLine();
                }
            }
        }
    }

    private static void FormatPropertyValue(StringBuilder sb, string key, object value, string indent)
    {
        if (value is Newtonsoft.Json.Linq.JObject jobj)
        {
            sb.AppendLine($"{indent}{key}:");
            foreach (var prop in jobj.Properties())
            {
                FormatPropertyValue(sb, prop.Name, prop.Value, indent + "  ");
            }
        }
        else if (value is Newtonsoft.Json.Linq.JArray jarr)
        {
            if (jarr.Count == 0)
            {
                sb.AppendLine($"{indent}{key}: []");
            }
            else
            {
                sb.AppendLine($"{indent}{key}:");
                foreach (var item in jarr)
                {
                    if (item is Newtonsoft.Json.Linq.JObject itemObj)
                    {
                        sb.AppendLine($"{indent}  -");
                        foreach (var prop in itemObj.Properties())
                        {
                            FormatPropertyValue(sb, prop.Name, prop.Value, indent + "    ");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"{indent}  - {item}");
                    }
                }
            }
        }
        else
        {
            sb.AppendLine($"{indent}{key}: {value}");
        }
    }
}
