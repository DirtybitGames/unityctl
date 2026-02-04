# UnityCtl

Let AI agents drive the Unity Editor. Edit scripts, compile, play, screenshot, debug—all from the command line.

## What Can AI Do With This?

### Edit and Compile

An agent modifies your C# scripts, then compiles:

```bash
# Agent edits PlayerController.cs, then...
unityctl asset refresh
```

Compilation errors appear directly in the output. The agent iterates until it compiles.

### Play Mode + Screenshots

Enter play mode and capture what's happening:

```bash
unityctl play enter
unityctl screenshot capture
unityctl logs                 # shows all logs since last clear
unityctl play exit
```

The agent can see the game running and read any Debug.Log output.

### Execute Arbitrary C#

Run any C# code in the editor—create objects, set properties, click buttons, open windows, whatever:

```bash
# Spawn a cube at the origin
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() { return GameObject.CreatePrimitive(PrimitiveType.Cube).name; } }"

# Find the player and move them
unityctl script execute -c "using UnityEngine; public class Script { public static object Main() { var p = GameObject.Find(\"Player\"); p.transform.position = new Vector3(0, 10, 0); return \"moved\"; } }"

# Pass arguments to scripts (use -- separator)
unityctl script execute -f SpawnObjects.cs -- Cube 5
```

## Installation

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or newer.

**Linux/macOS:**
```bash
curl -sSL https://raw.githubusercontent.com/DirtybitGames/unityctl/main/scripts/install.sh | bash
```

**Windows (PowerShell):**
```powershell
iwr https://raw.githubusercontent.com/DirtybitGames/unityctl/main/scripts/install.ps1 | iex
```

**Or install manually:**
```bash
dotnet tool install -g UnityCtl.Cli
dotnet tool install -g UnityCtl.Bridge
```

### Project Setup

Run setup from your Unity project directory (or any parent folder like a monorepo root):
```bash
unityctl setup
```

This installs the Unity package and Claude Code skill. If run outside a Unity project, it will prompt for the project path and save it to `.unityctl/config.json` for future commands.

**Quick start:** `unityctl bridge start`, open Unity, `unityctl status`

**Or launch Unity via CLI:** `unityctl bridge start`, `unityctl editor run`, `unityctl status`

## More Commands

| Command | Description |
|---------|-------------|
| `unityctl setup` | One-command project setup (config + package + skill) |
| `unityctl update` | Update CLI, bridge, and Unity package |
| `unityctl editor run` | Launch Unity Editor (auto-detects version) |
| `unityctl editor stop` | Stop running Unity Editor |
| `unityctl asset refresh` | Refresh assets and trigger compilation |
| `unityctl logs` | View logs since last clear (use `-n` to limit, `--full` for all history) |
| `unityctl scene load <path>` | Load a scene |
| `unityctl scene list` | List scenes in build settings |
| `unityctl test run` | Run edit mode tests |
| `unityctl test run --mode playmode` | Run play mode tests |
| `unityctl menu execute <path>` | Execute Unity menu item |
| `unityctl asset import <path>` | Import an asset |
| `unityctl config set/get/list` | Manage configuration |
| `unityctl package add/remove/status` | Manage Unity package |
| `unityctl skill add/remove/status` | Manage Claude Code skill |

Run `unityctl --help` for the full command list.

## Architecture

```
┌─────────┐        ┌────────┐        ┌──────────────┐
│ unityctl│──HTTP──│ bridge │──WS────│ Unity Editor │
│  (CLI)  │        │(daemon)│        │   (plugin)   │
└─────────┘        └────────┘        └──────────────┘
```

The bridge daemon survives Unity's domain reloads, so your workflow isn't interrupted when scripts recompile.

## Motivation
This project comes from a frustration with trying to use claude code to be productive in Unity. It is great at spitting out code that looks OK but doesn't compile. It would try to call unity in batch mode to compile, but Unity refuses since the editor is running.
Having the editor open is key to being in the loop and getting visual confirmation that things are working.

There are MCP servers for Unity, but they eat a lot of context window for any task that doesn't require driving the editor. By creating a CLI that claude can learn by trying to use it (and can "remember" on demand through the skill.md), claude is able to use the editor on demand without a constant context cost.

The API is kept simple, and leans mostly on the script execution to get work done. Claude is good at writing ad-hoc scripts to achieve a goal in unity, and this seems more flexible and reliable than implementing APIs for managing or editing specific types of assets.


## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - Technical details
- [CONTRIBUTING.md](CONTRIBUTING.md) - Development setup
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common issues
- [.claude/skills/unity-editor/SKILL.md](.claude/skills/unity-editor/SKILL.md) - AI assistant skill file

## License

MIT
