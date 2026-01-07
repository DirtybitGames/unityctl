---
name: unity-editor
description: Remote control Unity Editor via CLI using unityctl. Use when working with Unity projects to launch/stop editor, enter/exit play mode, compile scripts, view logs, load scenes, run tests, capture screenshots, or execute C# code for debugging. Activate when user mentions Unity, play mode, compilation, or needs to interact with a running Unity Editor.
---

# unityctl - Unity Editor Remote Control

Control a running Unity Editor from the command line without batch mode.

## Instructions

### Setup (Required First)

**Option A - Editor already open:**
1. Start the bridge daemon: `unityctl bridge start` (background it or it will time out)
2. Open the Unity project in Unity Editor
3. Verify connection: `unityctl status`

**Option B - Launch editor via CLI:**
1. Start the bridge daemon: `unityctl bridge start` (background it)
2. Launch Unity: `unityctl editor run`
3. Verify connection: `unityctl status`

### Critical: Refresh Assets After Script Changes

After modifying ANY C# scripts, you MUST refresh assets before entering play mode:

```bash
unityctl asset refresh
```

This triggers Unity's asset pipeline and script compilation. If there are compile errors, the command returns a non-zero exit code and JSON output includes error details. Play mode will use stale code otherwise.

### Common Commands

**Status & Bridge:**
```bash
unityctl status           # Check Unity running, bridge, and connection status
unityctl bridge start     # Start bridge daemon (runs in background)
unityctl bridge stop      # Stop bridge
```

**Editor Lifecycle:**
```bash
unityctl editor run         # Launch Unity Editor (auto-detects version)
unityctl editor stop        # Stop running Unity Editor
```

**Play Mode:**
```bash
unityctl play enter       # Enter play mode
unityctl play exit        # Exit play mode
unityctl play toggle      # Toggle play mode
```

**Logs:**
```bash
unityctl logs                 # Show recent logs (default: 50 lines)
unityctl logs -n 100          # More log entries
```

**Scenes:**
```bash
unityctl scene list                            # List scenes
unityctl scene load Assets/Scenes/Main.unity   # Load scene
```

**Testing:**
```bash
unityctl test run                    # Run edit mode tests
unityctl test run --mode playmode    # Play mode tests
```

**Screenshots:**
```bash
unityctl screenshot capture          # Capture screenshot
```

### Script Execution (Debugging Power Tool)

Execute arbitrary C# in the running editor via Roslyn. Invaluable for debugging and automation.

```bash
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() { return Application.version; } }"
```

### Getting Help

```bash
unityctl --help              # List all commands
unityctl <command> --help    # Command-specific help
```

## Examples

**Workflow: Edit script, compile, and test:**
```bash
# After editing C# files...
unityctl asset refresh
unityctl logs -n 20          # Check for compilation errors
unityctl play enter
unityctl logs -f             # Stream logs during play mode (Ctrl+C to stop)
unityctl play exit
```

**Debug: Find all GameObjects in scene:**
```bash
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() { return GameObject.FindObjectsOfType<GameObject>().Length; } }"
```

**Debug: Inspect Player position:**
```bash
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() { var go = GameObject.Find(\"Player\"); return go?.transform.position.ToString() ?? \"not found\"; } }"
```

**Debug: Log message to Unity console:**
```bash
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() { Debug.Log(\"Hello from CLI\"); return \"logged\"; } }"
```

## Best Practices

- Run `unityctl status` to check overall project status before running commands
- Always run `unityctl asset refresh` after modifying C# files before entering play mode
- Use `unityctl editor run` to launch Unity with automatic version detection
- Script execution requires a class with a static method; return values are JSON-serialized
- Domain reload after compilation is normal; the bridge auto-reconnects

## Troubleshooting

Run `unityctl status` first to diagnose issues.

| Problem | Solution |
|---------|----------|
| Bridge not responding | `unityctl bridge stop` then `unityctl bridge start` |
| Commands timing out | Ensure Unity Editor is responsive |
| Connection lost after compile | Normal - domain reload. Auto-reconnects. |
| "Project not found" | Run from project directory or use `--project` flag |
| Editor not found | Use `--unity-path` to specify Unity executable |
| Compilation errors after refresh | `unityctl logs -n 50` to see errors |
