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

### 1. Install the CLI and Bridge Tools

From the repository root:

```bash
# Install as global dotnet tools
dotnet pack UnityCtl.Cli/UnityCtl.Cli.csproj -o ./artifacts
dotnet pack UnityCtl.Bridge/UnityCtl.Bridge.csproj -o ./artifacts

dotnet tool install -g UnityCtl.Cli --add-source ./artifacts
dotnet tool install -g UnityCtl.Bridge --add-source ./artifacts
```

Or build and run locally:

```bash
# Run bridge locally
dotnet run --project UnityCtl.Bridge/UnityCtl.Bridge.csproj -- --project ./unity-project

# Run CLI locally
dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --help
```

### 2. Add Unity Package

The Unity package is already added to the test project in `unity-project/`. For other projects:

**Option A: Local Path** (for development)
Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.dirtybit.unityctl": "file:../path/to/UnityCtl.UnityPackage",
    ...
  }
}
```

**Option B: Git URL** (for production)
```json
{
  "dependencies": {
    "com.dirtybit.unityctl": "https://github.com/yourorg/unityctl.git?path=UnityCtl.UnityPackage#v0.1.0",
    ...
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

Or if you're not in the project directory:

```bash
unityctl bridge start --project ./unity-project
```

Output:
```
Starting bridge for project: C:\Users\...\unity-project
Bridge started successfully (PID: 12345, Port: 49521)
```

### 2. Open Unity Editor

Open the Unity project in Unity Editor. The plugin will automatically connect to the bridge on startup.

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

List scenes:
```bash
unityctl scene list
```

Load a scene:
```bash
unityctl scene load Assets/Scenes/SampleScene.unity
```

View console logs:
```bash
unityctl console tail --lines 20
```

Trigger compilation:
```bash
unityctl compile scripts
```

## CLI Commands

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

# Get play mode status
unityctl play status
```

### Scene Management

```bash
# List scenes in build settings
unityctl scene list

# List all scenes in project
unityctl scene list --source all

# Load a scene (single mode)
unityctl scene load Assets/Scenes/Main.unity

# Load a scene (additive mode)
unityctl scene load Assets/Scenes/Level1.unity --mode additive
```

### Console Logs

```bash
# Show recent console logs
unityctl console tail --lines 50
```

### Asset Management

```bash
# Import a specific asset
unityctl asset import Assets/Textures/logo.png
```

### Compilation

```bash
# Trigger script compilation
unityctl compile scripts
```

### Global Options

```bash
# Specify project path
unityctl --project ./my-unity-project play enter

# Set agent ID (for multi-agent scenarios)
unityctl --agent-id agent-1 play enter

# Get JSON output (for scripting)
unityctl --json scene list
```

## How It Works

### Project Isolation

Each Unity project has its own bridge instance. The bridge writes a config file to `.unityctl/bridge.json` in the project root:

```json
{
  "projectId": "proj-a1b2c3d4",
  "port": 49521,
  "pid": 12345
}
```

- **projectId**: Stable hash of project path
- **port**: Port the bridge is listening on
- **pid**: Process ID of the bridge

The CLI auto-detects the project by walking up from the current directory to find `ProjectSettings/ProjectVersion.txt`.

### Domain Reload Resilience

Unity's domain reload (triggered by script compilation) destroys all Editor objects. UnityCtl handles this gracefully:

1. Bridge maintains connection and state
2. Unity plugin reconnects after domain reload
3. No commands are lost during reconnection

### Architecture

**Protocol Layer** (`UnityCtl.Protocol`)
- Shared message types (Hello, Request, Response, Event)
- Config models and DTOs
- JSON serialization helpers
- Project detection logic

**Bridge Daemon** (`unityctl-bridge`)
- HTTP server for CLI requests (`/health`, `/rpc`, `/console/tail`)
- WebSocket server for Unity connection (`/unity`)
- Log buffering (last 1000 entries)
- Request/response matching

**CLI Tool** (`unityctl`)
- Command-line interface using System.CommandLine
- HTTP client for bridge communication
- Human-readable and JSON output modes

**Unity Plugin** (`com.dirtybit.unityctl`)
- WebSocket client for bridge connection
- Command handlers (scenes, play mode, assets, etc.)
- Event forwarding (logs, play mode changes, compilation)
- Main thread action queue

## Development

### Building

```bash
# Build all projects
dotnet build unityctl.sln

# Build and copy Protocol DLL with dependencies for Unity
dotnet publish UnityCtl.Protocol/UnityCtl.Protocol.csproj -c Release -o UnityCtl.Protocol/publish
cp UnityCtl.Protocol/publish/*.dll UnityCtl.UnityPackage/Plugins/
```

This copies UnityCtl.Protocol.dll and all its dependencies (System.Text.Json, etc.) to the Unity package.

### Project Structure

```
unityctl/
├── UnityCtl.Protocol/        # Shared protocol library (netstandard2.1)
├── UnityCtl.Bridge/          # Bridge daemon (net8.0)
├── UnityCtl.Cli/             # CLI tool (net8.0)
├── UnityCtl.UnityPackage/    # Unity UPM package
│   ├── package.json
│   ├── Editor/
│   │   ├── UnityCtl.asmdef
│   │   ├── UnityCtlBootstrap.cs
│   │   └── UnityCtlClient.cs
│   └── Plugins/
│       └── UnityCtl.Protocol.dll
└── unity-project/            # Test Unity project
```

### Testing the Integration

1. Start the bridge:
   ```bash
   dotnet run --project UnityCtl.Bridge/UnityCtl.Bridge.csproj -- --project ./unity-project
   ```

2. Open `unity-project/` in Unity Editor

3. Run CLI commands:
   ```bash
   dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --project ./unity-project bridge status
   dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --project ./unity-project play enter
   ```

## Troubleshooting

### Bridge not starting

- Check if port is already in use
- Verify you're in a Unity project directory
- Check bridge logs for errors

### Unity not connecting

- Ensure bridge is running (`unityctl bridge status`)
- Check `.unityctl/bridge.json` exists in project root
- Look for Unity console logs with `[UnityCtl]` prefix
- Restart Unity Editor to trigger reconnection

### Commands timing out

- Ensure Unity Editor window has focus (some operations require it)
- Check if Unity is responsive (not frozen)
- Look for errors in Unity console

### Domain reload issues

- Plugin automatically reconnects after domain reload
- Allow a few seconds after compilation before sending commands
- Check Unity console for reconnection logs

## License

MIT License - See LICENSE file for details
