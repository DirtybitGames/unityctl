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

# Logs
unityctl logs                # Show logs since last clear (auto-clears on play/compile)
unityctl logs -n 50          # Limit entries
unityctl logs --stack        # Include stack traces
unityctl logs --full         # Ignore clear boundary

# Scenes
unityctl scene list          # List scenes
unityctl scene load <path>   # Load scene (e.g., Assets/Scenes/Main.unity)

# Testing
unityctl test run            # Run edit mode tests
unityctl test run --mode playmode

# Screenshots
unityctl screenshot capture

# Video Recording (requires com.unity.recorder package)
unityctl record start                  # Start recording (manual stop)
unityctl record start --duration 10    # Record 10 seconds, blocks until done
unityctl record stop                   # Stop recording, returns file path + duration
```

## Script Execution

Evaluate C# expressions directly (common usings like UnityEngine, UnityEditor, System auto-included):

```bash
unityctl script eval "Application.version"
unityctl script eval "GameObject.FindObjectsOfType<Camera>().Length"
unityctl script eval "var p = GameObject.Find(\"Player\"); return p.transform.position;"
unityctl script eval -u UnityEngine.SceneManagement "SceneManager.GetActiveScene().name"
```

Pass arguments to the script with `--`:
```bash
unityctl script eval "args[0]" -- hello
```

### Full Script Execution

For complex scripts with custom classes, multiple methods, or file-based execution. Write a script file with a class containing a static `Main()` method returning `object` (JSON-serialized):

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
unityctl script execute -f /tmp/MyScript.cs
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() => Application.version; }"
unityctl script execute -f /tmp/SpawnObjects.cs -- Cube 5 "My Object"
```

Use `Main(string[] args)` to accept arguments passed after `--`.

Use `-t <seconds>` on `script eval`/`script execute` for long-running operations (default 30s):

```bash
unityctl script eval -t 600 -u UnityEditor 'return BuildPipeline.BuildPlayer(opts).summary.result.ToString();'
```

## Typical Workflow

```bash
# After editing C# files...
unityctl asset refresh       # Compile (fix errors if any)
unityctl play enter
unityctl logs                # Check runtime logs
unityctl play exit
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
| Editor not found | Use `--unity-path` to specify Unity executable |
