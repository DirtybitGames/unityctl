# UnityCtl

Remote control system for Unity Editor via CLI. Control a running Unity Editor without batch mode or interruption.

## Overview

UnityCtl consists of three components:

1. **unityctl** (CLI) - Command-line tool for sending commands
2. **unityctl-bridge** (Daemon) - Bridge daemon that coordinates between CLI and Unity
3. **Unity Editor Plugin** (UPM Package) - Editor plugin that receives commands

```
┌─────────┐        ┌────────┐        ┌──────────────┐
│ unityctl│──HTTP──│ bridge │──WS────│ Unity Editor │
│  (CLI)  │        │(daemon)│        │   (plugin)   │
└─────────┘        └────────┘        └──────────────┘
```

## Installation

[![NuGet - UnityCtl.Cli](https://img.shields.io/nuget/v/UnityCtl.Cli.svg?label=UnityCtl.Cli)](https://www.nuget.org/packages/UnityCtl.Cli)
[![NuGet - UnityCtl.Bridge](https://img.shields.io/nuget/v/UnityCtl.Bridge.svg?label=UnityCtl.Bridge)](https://www.nuget.org/packages/UnityCtl.Bridge)

### 1. Install CLI and Bridge

Install from NuGet (requires .NET 8.0+):

```bash
dotnet tool install -g UnityCtl.Cli
dotnet tool install -g UnityCtl.Bridge
```

To update existing installation:

```bash
dotnet tool update -g UnityCtl.Cli
dotnet tool update -g UnityCtl.Bridge
```

> For development installation from source, see [CONTRIBUTING.md](CONTRIBUTING.md)

### 2. Add Unity Package

Add to your Unity project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.dirtybit.unityctl": "https://github.com/DirtybitGames/unityctl.git?path=UnityCtl.UnityPackage#v0.1.0"
  }
}
```

## Quick Start

### 1. Start the Bridge

From your Unity project directory:

```bash
cd unity-project
unityctl bridge start
```

Or specify the project path:

```bash
unityctl bridge start --project ./unity-project
```

Expected output:
```
Starting bridge for project: C:\Users\...\unity-project
Bridge started successfully (PID: 12345, Port: 49521)
```

### 2. Open Unity Editor

Open the Unity project in Unity Editor. The plugin will automatically connect to the bridge.

Look for Unity console logs:
```
[UnityCtl] Connected to bridge at port 49521
[UnityCtl] Handshake complete
```

### 3. Use the CLI

Check bridge status:
```bash
unityctl bridge status
```

Enter play mode:
```bash
unityctl play enter
```

Load a scene:
```bash
unityctl scene load Assets/Scenes/SampleScene.unity
```

View console logs:
```bash
unityctl console tail --lines 20
```

## Essential Commands

### Bridge Management

```bash
# Check bridge status
unityctl bridge status

# Start bridge daemon
unityctl bridge start [--project <path>]
```

### Play Mode

```bash
# Enter play mode
unityctl play enter

# Exit play mode
unityctl play exit

# Toggle play mode
unityctl play toggle
```

### Scene Management

```bash
# List scenes in build settings
unityctl scene list

# Load a scene (single mode)
unityctl scene load Assets/Scenes/Main.unity

# Load a scene (additive mode)
unityctl scene load Assets/Scenes/Level1.unity --mode additive
```

### Console Logs

```bash
# Show recent console logs
unityctl console tail --lines 50

# Clear the console log buffer
unityctl console clear
```

### Compilation

```bash
# Trigger script compilation
unityctl compile scripts
```

### Asset Management

```bash
# Import a specific asset
unityctl asset import Assets/Textures/logo.png
```

### Global Options

```bash
# Specify project path
unityctl --project ./my-unity-project play enter

# Get JSON output (for scripting)
unityctl --json scene list
```

For complete command reference with all options, run `unityctl --help` or see any command's help with `unityctl <command> --help`.

## Key Concepts

### Project Isolation

Each Unity project has its own bridge instance. The bridge creates `.unityctl/bridge.json` in your project root containing connection details:

```json
{
  "projectId": "proj-a1b2c3d4",
  "port": 49521,
  "pid": 12345
}
```

The CLI auto-detects your project by walking up from the current directory to find `ProjectSettings/ProjectVersion.txt`.

### Domain Reload Resilience

Unity's domain reload (triggered by script compilation) normally destroys all Editor objects. UnityCtl's bridge daemon survives these reloads and automatically reconnects, so your workflow isn't interrupted.

> For detailed architecture information, see [ARCHITECTURE.md](ARCHITECTURE.md)

## Common Issues

**Bridge not starting?**
- Check if port is already in use
- Verify you're in a Unity project directory

**Unity not connecting?**
- Check `unityctl bridge status`
- Ensure `.unityctl/bridge.json` exists in project root
- Restart Unity Editor

**Commands timing out?**
- Ensure Unity Editor window has focus (some operations require it)
- Check if Unity is responsive (not frozen)

For detailed troubleshooting, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - Technical details and architecture
- [CONTRIBUTING.md](CONTRIBUTING.md) - Development setup and guidelines
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Extended debugging guide

## License

MIT License - See LICENSE file for details
