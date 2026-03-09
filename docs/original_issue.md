# Issue #27: Add snapshot command and ref-aware eval --id for structured scene observation

## Summary

unityctl has strong coverage of editor lifecycle (compilation, play mode, screenshots, logs, tests, menu commands, `script eval`). The critical missing piece is **structured observation** — an agent's equivalent of Playwright MCP's `browser_snapshot` (accessibility tree).

Today, to understand a scene, an agent must write ad-hoc C# across multiple `script eval` round-trips:

```bash
unityctl script eval "GameObject.FindObjectsOfType<Transform>().Where(t => t.parent == null).Select(t => t.name).ToArray()"
unityctl script eval "var go = GameObject.Find(\"Player\"); return go.GetComponents<Component>().Select(c => c.GetType().Name).ToArray();"
unityctl script eval "var go = GameObject.Find(\"Player\"); return go.transform.position;"
# ... and so on for each object/property
```

This costs 3-10 round-trips per "look at the scene," each requiring the agent to write correct C# and parse the result.

## Design Principles

1. **Only add dedicated commands when `script eval` is clearly insufficient** — too verbose (50+ lines), needed every agent turn, or requires non-trivial infrastructure.
2. **Observation is the one area that qualifies.** Scene traversal, filtering, depth-limiting, component summarization, and compact formatting is too much boilerplate for every agent turn.
3. **Use instance IDs as the bridge between observation and action.** Unity's `GetInstanceID()` is built-in, stable, and resolves with a single call. No custom ref tracker needed.
4. **Keep mutations in `script eval`.** C# is the type system. Adding `set-property`, `add-component`, `delete-object` commands means reinventing Unity's API surface.
5. **Always return current values.** No edit-mode vs play-mode distinction — `--components` shows whatever the values are right now, regardless of mode.

## Phase 1: `snapshot` + `eval --id` (Minimum Viable Pair)

These two features together unlock effective agent workflows. One without the other is incomplete — `snapshot` without refs forces `GameObject.Find()` round-trips; `--id` without `snapshot` has nothing to reference.

### `snapshot` command

A single top-level command that snapshots the scene hierarchy as a compact, LLM-friendly tree with instance IDs.

```bash
unityctl snapshot                          # scene hierarchy tree (~2-5KB)
unityctl snapshot --id 14200 --components  # drill into one object
unityctl snapshot --interactive            # UI focus: text content, button states
unityctl snapshot --layout                 # RectTransform anchors/sizes
unityctl snapshot --filter "type:Rigidbody"
unityctl snapshot --depth 4
```

**Flags:**
| Flag | Purpose |
|------|---------|
| `--depth N` | Max hierarchy depth (default: 2) |
| `--id N` | Drill into specific object by instance ID |
| `--components` | Include all serialized property values (current values, regardless of edit/play mode) |
| `--interactive` | Show UI text content and interactable state |
| `--layout` | Show RectTransform info instead of world position |
| `--filter` | Filter by `type:T`, `name:N*`, `tag:T` |

Instance IDs (`[i:N]`) use Unity's built-in `GetInstanceID()` — stable for the object's lifetime, survives domain reloads for serialized scene objects, resolves via `EditorUtility.InstanceIDToObject()`. Multiple agents get the same IDs for the same objects.

#### Default output (~2-5KB for typical scenes)

```
Scene: MainScene (playing)
12 root objects

Main Camera [i:13842] Camera, AudioListener
  pos(0.0, 1.0, -10.0)
Directional Light [i:13856] Light
  pos(0.0, 3.0, 0.0)
Player [i:14200] MeshRenderer, Rigidbody, PlayerController  tag:Player
  pos(0.0, 0.0, 0.0)
  Weapon [i:14210] MeshRenderer
    pos(0.5, 0.0, 0.8)
  Shield [i:14212] MeshRenderer
    pos(-0.5, 0.0, 0.5)
Ground [i:14220] MeshRenderer, BoxCollider
  pos(0.0, -0.5, 0.0) scale(100.0, 1.0, 100.0)
Enemies [i:14230] (3 children)
  ...
UI Canvas [i:14300] Canvas, GraphicRaycaster (8 children)
  ...
```

Design choices:
- Component type names only by default (not property values) — keeps output compact
- Collapsed children beyond `--depth` show child count, expandable via `--id`
- Inactive objects shown with `[inactive]` marker
- Position always included — the most universally useful spatial property
- Scale/rotation only shown when non-default — reduces noise

#### Drill-down (`--id 14200 --components`)

```
Player [i:14200] active  tag:Player  layer:Default
  Transform:
    position: (0.0, 0.0, 0.0)
    rotation: (0.0, 0.0, 0.0, 1.0)
    localScale: (1.0, 1.0, 1.0)
  MeshRenderer:
    enabled: true
    material: "PlayerMat"
    shadowCastingMode: On
  Rigidbody:
    mass: 1.0
    useGravity: true
    isKinematic: false
    velocity: (0.0, -0.1, 0.0)
  PlayerController:
    speed: 5.0
    jumpForce: 10.0
    health: 100
  Children (2):
    Weapon [i:14210] MeshRenderer
    Shield [i:14212] MeshRenderer
```

#### Interactive mode (`--interactive`)

Changes per-object summary for UI elements — shows text content and interactable state instead of world position:

```
UI Canvas [i:14300] Canvas overlay
  Panel "Header" [i:14310]
    Text [i:14320] "Score: 1250"
    Text [i:14330] "Health: 75/100"
  Panel "Controls" [i:14340]
    Button [i:14350] "Attack" interactable
    Button [i:14360] "Defend" interactable
    Button [i:14370] "Inventory" disabled
```

#### Layout mode (`--layout`)

Shows RectTransform positioning instead of world position:

```
UI Canvas [i:14300] Canvas overlay
  Panel "Header" [i:14310]
    rect(0, 560, 1920, 80) anchor(0-1, 1-1) pivot(0.5, 1.0)
    Text [i:14320] "Score: 1250"
      rect(10, 0, 200, 80) anchor(0-0, 0-1)
```

Flags compose: `--interactive --components` gives full UI detail. `--layout --components` gives layout plus all serialized properties.

### `script eval --id` (ref-aware eval)

After `snapshot` returns instance IDs, target objects directly without `GameObject.Find()`:

```bash
# Single target — injected as `target`
unityctl script eval --id 14200 'target.transform.position = new Vector3(0, 10, 0); return "moved";'
unityctl script eval --id 14200 'target.AddComponent<Rigidbody>(); return "added";'
unityctl script eval --id 14200 'DestroyImmediate(target); return "deleted";'

# Multiple targets — injected as `target0`, `target1`, ...
unityctl script eval --id 14200,14210 'target0.transform.SetParent(target1.transform); return "reparented";'
```

Implementation: the CLI prepends variable declarations into the generated C# before Roslyn compilation:

```csharp
// For --id 14200:
var target = (GameObject)UnityEditor.EditorUtility.InstanceIDToObject(14200);
if (target == null) throw new System.Exception("Object 14200 not found (destroyed?)");

// For --id 14200,14210:
var target0 = (GameObject)UnityEditor.EditorUtility.InstanceIDToObject(14200);
var target1 = (GameObject)UnityEditor.EditorUtility.InstanceIDToObject(14210);
```

~15 lines in `ScriptCommands.cs`, no Unity plugin changes, no new protocol commands.

## The Agent Workflow

```
snapshot → think → eval --id → snapshot (verify)
```

This mirrors the browser automation observe-think-act loop:
1. `unityctl snapshot` — see the scene, get instance IDs
2. `unityctl script eval --id N '<C#>'` — act on specific objects
3. `unityctl snapshot` — verify changes took effect
4. `unityctl screenshot capture` — visual confirmation when needed

## Why This Matters

From browser automation research: the single biggest factor in AI agent effectiveness is how the agent observes state. Playwright MCP's accessibility tree (`browser_snapshot`) reduced tokens by 89% and raised accuracy to 98% compared to screenshot-only approaches. The `snapshot` command brings the same pattern to Unity.

## Implementation Sketch

### Unity-side (`UnityCtlClient.cs`)

```csharp
private object HandleSnapshot(RequestMessage request)
{
    var depth = GetIntArgument(request, "depth") ?? 2;
    var targetId = GetIntArgument(request, "id");
    var includeComponents = GetBoolArgument(request, "components");
    var interactive = GetBoolArgument(request, "interactive");
    var layout = GetBoolArgument(request, "layout");
    var filter = GetStringArgument(request, "filter");

    if (targetId.HasValue)
    {
        var go = EditorUtility.InstanceIDToObject(targetId.Value) as GameObject;
        if (go == null)
            throw new ArgumentException($"No GameObject with instance ID {targetId}");
        return SerializeGameObject(go, depth, includeComponents, interactive, layout);
    }

    var scene = SceneManager.GetActiveScene();
    var roots = scene.GetRootGameObjects();

    return new SnapshotResult
    {
        SceneName = scene.name,
        ScenePath = scene.path,
        IsPlaying = EditorApplication.isPlaying,
        RootObjectCount = roots.Length,
        Objects = roots
            .Where(go => MatchesFilter(go, filter))
            .Select(go => SerializeGameObject(go, depth, includeComponents, interactive, layout))
            .ToArray()
    };
}

private SnapshotObject SerializeGameObject(
    GameObject go, int depth, bool includeComponents, bool interactive, bool layout)
{
    var t = go.transform;
    var obj = new SnapshotObject
    {
        InstanceId = go.GetInstanceID(),
        Name = go.name,
        Active = go.activeSelf,
        Tag = go.tag != "Untagged" ? go.tag : null,
        Layer = go.layer != 0 ? LayerMask.LayerToName(go.layer) : null,
        Components = go.GetComponents<Component>()
            .Where(c => c != null && !(c is Transform))
            .Select(c => includeComponents
                ? SerializeComponentFull(c)
                : new SnapshotComponent { TypeName = c.GetType().Name })
            .ToArray(),
        ChildCount = t.childCount,
    };

    if (layout && t is RectTransform rt)
    {
        obj.Rect = FormatRect(rt);
        obj.Anchors = FormatAnchors(rt);
        obj.Pivot = FormatVector2(rt.pivot);
    }
    else
    {
        obj.Position = FormatVector3(t.localPosition);
        obj.Scale = t.localScale != Vector3.one ? FormatVector3(t.localScale) : null;
    }

    if (interactive)
    {
        obj.Text = GetUIText(go);
        obj.Interactable = GetInteractable(go);
    }

    if (depth > 0 && t.childCount > 0)
    {
        obj.Children = new SnapshotObject[t.childCount];
        for (int i = 0; i < t.childCount; i++)
            obj.Children[i] = SerializeGameObject(
                t.GetChild(i).gameObject, depth - 1, includeComponents, interactive, layout);
    }

    return obj;
}
```

For `--components`, use `SerializedObject`/`SerializedProperty` to iterate all serialized fields without knowing the component type. No special-casing for edit vs play mode — `SerializedObject` reads current values in both modes.

### eval --id (`ScriptCommands.cs`)

```csharp
private static string InjectInstanceIdPreamble(string code, string idArg)
{
    var ids = idArg.Split(',').Select(s => s.Trim()).ToArray();
    var preamble = new StringBuilder();

    if (ids.Length == 1)
    {
        preamble.AppendLine(
            $"var target = (GameObject)UnityEditor.EditorUtility.InstanceIDToObject({ids[0]});");
        preamble.AppendLine(
            $"if (target == null) throw new System.Exception(\"Object {ids[0]} not found (destroyed?)\");");
    }
    else
    {
        for (int i = 0; i < ids.Length; i++)
        {
            preamble.AppendLine(
                $"var target{i} = (GameObject)UnityEditor.EditorUtility.InstanceIDToObject({ids[i]});");
            preamble.AppendLine(
                $"if (target{i} == null) throw new System.Exception(\"Object {ids[i]} not found\");");
        }
    }

    return code.Replace(
        "public static object Main() { ",
        "public static object Main() { " + preamble);
}
```

## Implementation Scope

~280 lines of new code following existing patterns:

| Step | What | Where | Effort |
|------|------|-------|--------|
| 1 | `Snapshot` command constant | `Protocol/Constants.cs` | 1 line |
| 2 | `SnapshotResult`, `SnapshotObject`, `SnapshotComponent` DTOs | `Protocol/DTOs.cs` | ~50 lines |
| 3 | `HandleSnapshot()` + `SerializeGameObject()` | `Plugin/UnityCtlClient.cs` | ~100 lines |
| 4 | `SnapshotCommand.cs` (CLI registration, arg parsing, output formatting) | `Cli/SnapshotCommand.cs` | ~110 lines |
| 5 | `--id` option on `script eval` | `Cli/ScriptCommands.cs` | ~20 lines |
| 6 | Skill file updates (snapshot-act-verify workflow) | `.claude/skills/.../SKILL.md` | ~20 lines |

Bridge requires no changes — the generic `HandleGenericCommandAsync` handler forwards the request automatically.

## Phase 2: `play pause` and `play step`

Separate from this issue but worth noting as the next high-value addition:

```bash
unityctl play pause              # Toggle pause state
unityctl play step               # Advance one frame
unityctl play step --frames 5    # Advance 5 frames
```

These need dedicated commands because frame stepping requires bridge-level coordination — `script eval` runs on the main thread and can't step frames while executing.

## What NOT to Add

The `script eval --id` pattern intentionally avoids growing the API surface. These should stay as eval:

- **Object creation/deletion** — too many variations, eval is more flexible
- **Component add/remove** — same reason
- **Property setting** — thousands of component types, can't enumerate
- **Input simulation** — very game-specific, use eval + `SendMessage`
- **Profiler/animation/build** — niche, complex APIs, eval covers them

---

## Implementation Changes vs Original Design

The following deviations were made from the issue's original design during implementation:

### `eval --id` multi-target naming

**Issue proposed:** `target0`, `target1`, ... for multiple IDs.
**Implemented:** Single ID injects `target`. Multiple comma-separated IDs inject a `targets[]` array (`targets[0]`, `targets[1]`, ...). This scales better to arbitrary numbers of targets and uses familiar array syntax. Single-target `target` remains the primary interface since it covers ~95% of use cases.

### Rotation shown as euler angles

**Issue proposed:** Rotation as quaternion `(0.0, 0.0, 0.0, 1.0)`.
**Implemented:** Rotation displayed as euler angles `(0.0, 0.0, 0.0)` which is more intuitive for agents and matches what Unity's Inspector shows. Only shown when non-identity (same noise-reduction principle as scale).

### `--components` property names use `displayName`

**Issue proposed:** camelCase property names like `useGravity`, `isKinematic`.
**Implemented:** Uses Unity's `SerializedProperty.displayName` which produces spaced names like "use Gravity", "is Kinematic". This is what `SerializedObject` iteration naturally provides and avoids maintaining a separate name mapping layer.

### All float formatting uses InvariantCulture

Not mentioned in the issue but required for correctness — all float/vector formatting explicitly uses `CultureInfo.InvariantCulture` to ensure decimal dots regardless of system locale.

### `--filter` searches children for type matches

The `type:` filter checks not just the root object's components but also recursively searches children. This makes `--filter "type:Rigidbody"` return a root object if any of its descendants have a Rigidbody, which is more useful for finding relevant hierarchies.
