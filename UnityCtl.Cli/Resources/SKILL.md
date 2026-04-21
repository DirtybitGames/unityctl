---
name: unity-editor
description: Remote control Unity Editor via CLI using unityctl. Activate when user mentions Unity Editor, play mode, asset compilation, Unity console logs, C# script debugging, Unity tests, scene loading, screenshots, or video recording. Use for launching/stopping editor, entering/exiting play mode, compiling scripts, viewing logs, loading scenes, running tests, capturing screenshots, recording video, or executing arbitrary C# in Unity context.
---

# unityctl - Unity Editor Remote Control

Control a running Unity Editor from the command line without batch mode.

## Setup (Required First)

Run `unityctl status` first to check what's already running. If Unity is already connected, skip straight to commands.

```bash
unityctl status                # Check current state — may already be connected
unityctl bridge start          # Start bridge daemon (idempotent, skips if running)
unityctl editor run            # Launch Unity Editor (or open project manually)
unityctl wait                  # Block until Unity is connected (up to 120s)
```

## Commands

```bash
# Status & Bridge
unityctl status              # Check Unity, bridge, and connection status
unityctl bridge start/stop   # Manage bridge daemon

# Editor
unityctl editor run/stop     # Launch or stop Unity Editor

# Compile (run after modifying C# scripts or importing packages)
unityctl asset refresh       # Compile scripts, returns errors on failure

# Play Mode
unityctl play enter/exit     # Enter or exit play mode
unityctl play pause          # Toggle pause (works in edit mode too — arms pause-on-play)
unityctl play step           # Advance one frame (only in play mode)

# Logs
unityctl logs                # Show logs since last clear (auto-clears on play/compile)
unityctl logs -n 50          # Limit entries
unityctl logs --stack        # Include stack traces
unityctl logs --full         # Ignore clear boundary

# Scenes
unityctl scene list          # List scenes
unityctl scene load <path>   # Load scene (e.g., Assets/Scenes/Main.unity)
unityctl scene load <path> --additive  # Load additively (without unloading current)

# Testing
unityctl test run            # Run edit mode tests
unityctl test run --mode playmode

# Screenshots (use sparingly — prefer snapshot/logs/eval for verification)
unityctl screenshot capture              # Capture game view (large context cost, hard to diff)
unityctl screenshot list-windows         # List open editor windows (type, title, size, docked)
unityctl screenshot window <window>      # Capture specific editor window by type or title
unityctl screenshot window SceneView out.png  # e.g. capture Scene view to out.png

# Video Recording (requires com.unity.recorder package)
# Note: record start auto-enters play mode if not already playing
unityctl record start                  # Start recording (manual stop)
unityctl record start --duration 10    # Record 10 seconds, blocks until done
unityctl record start --frames 300     # Record exactly 300 frames, blocks until done
unityctl record stop                   # Stop recording, returns file path + duration

# Scene Snapshot (structured observation with instance IDs)
unityctl snapshot                          # Scene hierarchy tree (default depth 2)
unityctl snapshot --depth 4                # Deeper traversal
unityctl snapshot --id 14200 --components  # Drill into one object with all properties
unityctl snapshot --id 14200 --filter "type:Rigidbody"  # Filter drill-down result
unityctl snapshot --screen                 # Add screen-space bounds and visibility for UI elements
unityctl snapshot --filter "type:Rigidbody"  # Filter: type:T, name:N* (glob, not regex), tag:T
unityctl snapshot --scene Assets/Scenes/Other.unity      # Snapshot another scene (read-only)
unityctl snapshot --prefab Assets/Prefabs/Player.prefab  # Snapshot a prefab asset
unityctl snapshot query 400 300            # What UI element is at screen pixel (400, 300)?

# UI Interaction (play mode only)
unityctl ui click --name "StartButton"    # Find by name and click (recommended)
unityctl ui click --id 14200              # Click UI element by instance ID
unityctl ui click 400 300                 # Click at screen coordinates

# Prefab Editing
unityctl prefab open Assets/Prefabs/Player.prefab  # Open prefab in isolation mode
unityctl prefab open Assets/Prefabs/Player.prefab --context 14200  # In-context editing
unityctl prefab close                              # Close prefab stage, return to scene
unityctl prefab close --save                       # Save changes then close
unityctl prefab close --discard                    # Discard changes then close

# Dialog Detection (native OS popups blocking Unity)
unityctl dialog list                   # List detected popup dialogs
unityctl dialog dismiss                # Dismiss first dialog (clicks first button)
unityctl dialog dismiss --button "OK"  # Click specific button
```

## Verifying Changes

Pick the cheapest observation that answers the question — screenshots are expensive (consume context, hard to diff across iterations) and imprecise (pixel details are unreliable). Prefer structured tools:

| What you need to verify | Tool |
|------------------------|------|
| Scene hierarchy, components, properties | `snapshot` (with `--components`, `--filter`) |
| UI layout, visibility, screen positions | `snapshot --screen` |
| Runtime behavior, errors, warnings | `logs` |
| Specific value or state | `script eval` (query it directly) |
| Test correctness | `test run` |
| Visual appearance (art, shaders, layout polish) | `screenshot capture` (only when visuals are the point) |

**Rule of thumb:** if you can express the expected result as a value or property, verify with `snapshot`, `logs`, or `script eval` — not a screenshot.

When a screenshot is the right tool, crop it to the relevant region before reading it — a focused crop produces far better results than a full-screen capture and uses less context.

## Scene Observation & Manipulation Workflow

Use `snapshot` to observe, `ui click` to interact, `eval --id` for custom actions, then `snapshot` to verify.
UI elements auto-show text content, interactable state, and RectTransform layout.
`interactable` means the element has pointer event handlers (`Button`, `Toggle`, `Slider`, or custom `IPointerClickHandler`/`IPointerDownHandler`). For standard Selectables it reflects `Selectable.interactable`; custom pointer handlers always report as interactable.
Use `--screen` to add screen-space bounds and visibility for UI elements. Hittability (blocked-by detection) is only available in play mode.
Use `snapshot query <x> <y>` to identify what UI element is at a screen coordinate. Response includes a `mode` field: `play` (accurate) or `edit-approximate` (hit ordering may be imprecise).
Use `ui click --name <name>` to find and click a UI element by name in one call (play mode). Uses `GameObject.Find` — supports both simple names (`"StartButton"`) and hierarchy paths (`"/Canvas/Panel/StartButton"`). Prefer `--name` over `--id` since instance IDs are not stable across play mode transitions. Reports if the element is blocked by another.

```bash
unityctl snapshot --screen                 # See the scene with UI screen bounds
unityctl ui click --name "StartButton"    # Find and click by name (one call)
unityctl snapshot                          # Verify the result
unityctl snapshot query 400 300            # What UI element is at pixel (400, 300)?
unityctl ui click 400 300                 # Click at those coordinates
```

Multiple targets with `--id` (uses `targets[]` array):
```bash
unityctl script eval --id 14200,14210 'targets[0].transform.SetParent(targets[1].transform); return "done";'
```

## Script Execution

Evaluate C# expressions directly (common usings like UnityEngine, UnityEditor, System auto-included):

```bash
unityctl script eval 'Application.version'
unityctl script eval 'GameObject.FindObjectsOfType<Camera>().Length'
unityctl script eval --id -1290 'target.transform.position'
unityctl script eval -u UnityEngine.SceneManagement 'SceneManager.GetActiveScene().name'
unityctl script eval -u UnityEngine.UI,UnityEngine.SceneManagement 'SceneManager.GetActiveScene().name'  # comma-separated
```

Pass arguments to the script with `--`:
```bash
unityctl script eval 'args[0]' -- hello
```

### Full Script Execution

For complex scripts with custom classes, multiple methods, or logic beyond a single expression. Use the Write tool to create a `.cs` file, then execute it. The script must define a class with a static `Main()` method returning `object` (JSON-serialized):

```cs
// /tmp/MyScript.cs
using UnityEngine;

public class Script
{
    public static object Main()
    {
        var player = GameObject.Find("Player");
        return player?.transform.position.ToString() ?? "not found";
    }
}
```

```bash
unityctl script execute /tmp/MyScript.cs
unityctl script execute /tmp/SpawnObjects.cs -- Cube 5 'My Object'
```

Use `Main(string[] args)` to accept arguments passed after `--`.

**Important:** Always use the Write tool to create the `.cs` file rather than shell heredocs (`cat << 'EOF'`), which break on single quotes in C# code.

Use `-t <seconds>` on `script eval`/`script execute` for long-running operations (default 30s):

```bash
unityctl script eval -t 600 -u UnityEditor 'return BuildPipeline.BuildPlayer(opts).summary.result.ToString();'
```

## Typical Workflow

```bash
# After editing C# files...
unityctl asset refresh       # Compile — check for errors
unityctl snapshot            # Verify scene state (structured, cheap)
unityctl play enter
unityctl snapshot            # Check runtime state with instance IDs
unityctl logs                # Check for errors/warnings
unityctl play exit
# Only screenshot if you need to judge something visual (art, layout polish)
```

## Troubleshooting

Run `unityctl status` first to diagnose issues.

| Problem | Solution |
|---------|----------|
| Bridge not responding | `unityctl bridge stop && unityctl bridge start` |
| Editor not connected | Normal - exponential backoff, up to 15 seconds |
| Connection lost after compile | Normal - domain reload, auto-reconnects |
| "Project not found" | `unityctl setup` or `unityctl config set project-path <path>` |
| Can't tell when Unity is ready | `unityctl wait --timeout 300` |
| Command timed out | A native dialog may be blocking Unity: `unityctl dialog list` |
| Progress bar stuck | Check with `unityctl dialog list`, wait or dismiss |
| Editor not found | Use `--unity-path` to specify Unity executable |
