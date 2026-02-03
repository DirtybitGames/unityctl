---
name: unity-editor
description: Remote control Unity Editor via CLI using unityctl. Activate when user mentions Unity Editor, play mode, asset compilation, Unity console logs, C# script debugging, Unity tests, scene loading, or screenshots. Use for launching/stopping editor, entering/exiting play mode, compiling scripts, viewing logs, loading scenes, running tests, capturing screenshots, or executing arbitrary C# in Unity context.
---

# unityctl - Unity Editor Remote Control

Control a running Unity Editor from the command line without batch mode.

## Setup (Required First)

```bash
unityctl bridge start        # Start bridge daemon (runs in background)
unityctl editor run          # Launch Unity Editor (or open project manually)
unityctl status              # Verify connection
```

## Commands

```bash
# Status & Bridge
unityctl status              # Check Unity, bridge, and connection status
unityctl bridge start/stop   # Manage bridge daemon

# Editor
unityctl editor run/stop     # Launch or stop Unity Editor

# Compile (run after modifying C# scripts)
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
```

## Script Execution

Execute arbitrary C# in the running editor via Roslyn. Write scripts to `/tmp/` and execute:

```cs
// /tmp/debug.cs
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
unityctl script execute -f /tmp/debug.cs
```

Inline execution with `-c`:
```bash
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() => Application.version; }"
```

Scripts require a class with static `Main()` returning `object`. Return value is JSON-serialized.

**With arguments** (`Main(string[] args)`):
```bash
unityctl script execute -f /tmp/spawn.cs -- Cube 5 "My Object"
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
| Editor not connected | Normal - exponential backoff, up to 30 seconds |
| Connection lost after compile | Normal - domain reload, auto-reconnects |
| "Project not found" | `unityctl setup` or `unityctl config set project-path <path>` |
| Editor not found | Use `--unity-path` to specify Unity executable |
