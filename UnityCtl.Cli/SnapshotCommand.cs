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

        var screenOption = new Option<bool>(
            "--screen",
            "Include screen-space bounds, visibility, and hittability"
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
        snapshotCommand.AddOption(screenOption);
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
            var screen = context.ParseResult.GetValueForOption(screenOption);
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
                { "screen", screen },
                { "filter", filter },
                { "scenePath", scene },
                { "prefabPath", prefab }
            };

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.Snapshot, args, timeout);
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
                    FormatSnapshot(result, components, screen, id.HasValue);
                }
            }
        });

        // Add query subcommand
        snapshotCommand.AddCommand(CreateQueryCommand());

        return snapshotCommand;
    }

    private static Command CreateQueryCommand()
    {
        var queryCommand = new Command("query", "Hit-test at screen coordinates — what's at this pixel?");

        var xArg = new Argument<int>("x", "Screen X coordinate");
        var yArg = new Argument<int>("y", "Screen Y coordinate");

        queryCommand.AddArgument(xArg);
        queryCommand.AddArgument(yArg);

        queryCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var agentId = ContextHelper.GetAgentId(context);
            var json = ContextHelper.GetJson(context);

            var x = context.ParseResult.GetValueForArgument(xArg);
            var y = context.ParseResult.GetValueForArgument(yArg);

            var client = BridgeClient.TryCreateFromProject(projectPath, agentId);
            if (client == null) { context.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "x", x },
                { "y", y }
            };

            var timeout = ContextHelper.GetTimeout(context);
            var response = await client.SendCommandAsync(UnityCtlCommands.SnapshotQuery, args, timeout);
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
                var result = JsonConvert.DeserializeObject<SnapshotQueryResult>(
                    JsonConvert.SerializeObject(response.Result, JsonHelper.Settings),
                    JsonHelper.Settings
                );

                if (result != null)
                {
                    FormatQueryResult(result);
                }
            }
        });

        return queryCommand;
    }

    private static void FormatSnapshot(SnapshotResult result, bool components, bool screen, bool isDrillDown)
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
            else if (result.Scenes is { Length: > 1 })
            {
                // Multi-scene mode
                var playIndicator = result.IsPlaying ? " (playing)" : "";
                sb.AppendLine($"{result.Scenes.Length} scenes loaded{playIndicator}");
                sb.AppendLine($"{result.RootObjectCount} root objects");
                sb.AppendLine();

                foreach (var scene in result.Scenes)
                {
                    var activeMarker = scene.IsActive ? " [active]" : "";
                    sb.AppendLine($"--- {scene.SceneName} ({scene.ScenePath}){activeMarker} ---");
                    sb.AppendLine($"{scene.RootObjectCount} root objects");
                    sb.AppendLine();

                    foreach (var obj in scene.Objects)
                    {
                        FormatObject(sb, obj, 0, components, screen, isDrillDown);
                    }

                    sb.AppendLine();
                }

                Console.Write(sb.ToString());
                return;
            }
            else
            {
                // Single scene mode
                var playIndicator = result.IsPlaying ? " (playing)" : "";
                sb.AppendLine($"Scene: {result.SceneName}{playIndicator}");
            }

            sb.AppendLine($"{result.RootObjectCount} root objects");
            sb.AppendLine();
        }

        foreach (var obj in result.Objects)
        {
            FormatObject(sb, obj, 0, components, screen, isDrillDown);
        }

        Console.Write(sb.ToString());
    }

    private static void FormatObject(StringBuilder sb, SnapshotObject obj, int indent, bool components, bool screen, bool isDrillDown)
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
            FormatDrillDown(sb, obj, prefix);
        }
        else
        {
            // UI objects: show rect + text/interactable; 3D objects: show world position
            if (!string.IsNullOrEmpty(obj.Rect))
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

            // UI text and interactable state (always shown when present)
            if (!string.IsNullOrEmpty(obj.Text))
            {
                sb.Append($"{prefix}  \"{obj.Text}\"");
                if (obj.Interactable == true) sb.Append(" interactable");
                else if (obj.Interactable == false) sb.Append(" disabled");
                sb.AppendLine();
            }
            else if (obj.Interactable != null)
            {
                sb.AppendLine($"{prefix}  {(obj.Interactable == true ? "interactable" : "disabled")}");
            }
        }

        // Screen-space info
        if (screen && !string.IsNullOrEmpty(obj.ScreenRect))
        {
            sb.Append($"{prefix}  {obj.ScreenRect}");
            if (obj.Visible == true)
            {
                sb.Append(" visible");
                if (obj.Hittable == true)
                    sb.Append(" hittable");
                else if (obj.Hittable == false)
                {
                    sb.Append(" blocked-by:");
                    sb.Append(obj.BlockedBy.HasValue ? $"[i:{obj.BlockedBy}]" : "unknown");
                }
            }
            else if (obj.Visible == false)
            {
                sb.Append(" off-screen");
            }
            sb.AppendLine();
        }

        // Children
        if (obj.Children != null)
        {
            foreach (var child in obj.Children)
            {
                FormatObject(sb, child, indent + 1, components, screen, isDrillDown);
            }
        }
        else if (obj.ChildCount > 0 && !isDrillDown)
        {
            sb.AppendLine($"{prefix}  ({obj.ChildCount} children)");
        }
    }

    private static void FormatDrillDown(StringBuilder sb, SnapshotObject obj, string prefix)
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
            if (!string.IsNullOrEmpty(obj.Rect))
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

        // Children are rendered by FormatObject's recursive loop after this method returns
    }

    private static void FormatQueryResult(SnapshotQueryResult result)
    {
        var sb = new StringBuilder();
        sb.Append($"Hit at ({result.X}, {result.Y})");
        if (result.Mode == "edit-approximate")
            sb.Append(" [edit mode — hit ordering is approximate]");
        sb.AppendLine(":");

        if (result.UiHits is not { Length: > 0 })
        {
            sb.AppendLine("  (nothing)");
            Console.Write(sb.ToString());
            return;
        }

        sb.AppendLine($"  UI ({result.UiHits.Length} hits):");
        for (int i = 0; i < result.UiHits.Length; i++)
        {
            var hit = result.UiHits[i];
            sb.Append($"    {i + 1}. {hit.Name} [i:{hit.InstanceId}] — {hit.Path}");
            if (!string.IsNullOrEmpty(hit.Text))
                sb.Append($" \"{hit.Text}\"");
            if (hit.Interactable == true) sb.Append(" interactable");
            else if (hit.Interactable == false) sb.Append(" disabled");
            sb.AppendLine();
        }

        Console.Write(sb.ToString());
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
