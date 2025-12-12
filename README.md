# UnityCtl

Let AI agents drive the Unity Editor. Edit scripts, compile, play, screenshot, debug—all from the command line.

## What Can AI Do With This?

### Edit, Compile, Check Logs

An agent modifies your C# scripts, then sees the results:

```bash
# Agent edits PlayerController.cs, then...
unityctl compile scripts
unityctl console tail --count 20
```

The agent sees compiler errors or success, iterates until it works.

### Play Mode + Screenshots

Enter play mode and capture what's happening:

```bash
unityctl play enter
unityctl screenshot capture
unityctl console tail --count 50
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
```

## Installation

```bash
dotnet tool install -g UnityCtl.Cli
dotnet tool install -g UnityCtl.Bridge
```

Add to your Unity project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.dirtybit.unityctl": "https://github.com/DirtybitGames/unityctl.git?path=UnityCtl.UnityPackage#v0.2"
  }
}
```

**Quick start:** `unityctl bridge start`, open Unity, `unityctl status`

**For Claude Code:** Copy [examples/unity-editor/SKILL.md](examples/unity-editor/SKILL.md) to `.claude/skills/` in your project.

## More Commands

| Command | Description |
|---------|-------------|
| `unityctl scene load <path>` | Load a scene |
| `unityctl scene list` | List scenes in build settings |
| `unityctl test run` | Run edit mode tests |
| `unityctl test run --mode playmode` | Run play mode tests |
| `unityctl menu execute <path>` | Execute Unity menu item |
| `unityctl asset import <path>` | Import an asset |

Run `unityctl --help` for the full command list.

## Architecture

```
┌─────────┐        ┌────────┐        ┌──────────────┐
│ unityctl│──HTTP──│ bridge │──WS────│ Unity Editor │
│  (CLI)  │        │(daemon)│        │   (plugin)   │
└─────────┘        └────────┘        └──────────────┘
```

The bridge daemon survives Unity's domain reloads, so your workflow isn't interrupted when scripts recompile.

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - Technical details
- [CONTRIBUTING.md](CONTRIBUTING.md) - Development setup
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common issues
- [examples/unity-editor/SKILL.md](examples/unity-editor/SKILL.md) - AI assistant skill file

## License

MIT
