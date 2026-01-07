# UnityCtl Implementation Summary

## Overview

Successfully implemented a complete remote control system for Unity Editor based on the design specification. The system consists of three components that work together to provide CLI-based control of a running Unity Editor.

## Components Implemented

### 1. UnityCtl.Protocol (Shared Library)

**Location:** `UnityCtl.Protocol/`
**Target:** netstandard2.1 (Unity compatible)

**Files:**
- `Messages.cs` - Protocol message types (Hello, Request, Response, Event)
- `Config.cs` - Bridge configuration model
- `DTOs.cs` - Data transfer objects for all commands
- `Constants.cs` - Command and event name constants
- `JsonHelper.cs` - Shared JSON serialization
- `ProjectLocator.cs` - Project detection and config management
- `IsExternalInit.cs` - Polyfills for C# features in netstandard2.1

**Key Features:**
- JSON-based message protocol
- Project isolation via projectId (hash of project path)
- Shared types used by all components
- Unity-compatible (netstandard2.1)

### 2. UnityCtl.Bridge (Bridge Daemon)

**Location:** `UnityCtl.Bridge/`
**Target:** net10.0
**Tool Name:** `unityctl-bridge`

**Files:**
- `Program.cs` - CLI entry point with System.CommandLine
- `BridgeState.cs` - State management (connections, logs, requests)
- `BridgeEndpoints.cs` - HTTP and WebSocket endpoints

**Endpoints:**
- `GET /health` - Health check with Unity connection status
- `GET /console/tail?lines=N` - Recent console logs
- `POST /rpc` - RPC commands to Unity
- `GET /unity` (WebSocket) - Unity Editor connection endpoint

**Key Features:**
- Automatic port assignment
- Project isolation via `.unityctl/bridge.json`
- Log ring buffer (1000 entries)
- Request/response matching with timeouts
- Domain reload resilience

### 3. UnityCtl.Cli (CLI Tool)

**Location:** `UnityCtl.Cli/`
**Target:** net10.0
**Tool Name:** `unityctl`

**Files:**
- `Program.cs` - Main CLI with global options
- `BridgeClient.cs` - HTTP client for bridge communication
- `ConsoleCommands.cs` - Console log commands
- `SceneCommands.cs` - Scene management commands
- `PlayCommands.cs` - Play mode commands
- `AssetCommands.cs` - Asset management commands
- `BridgeCommands.cs` - Bridge management commands
- `Binders.cs` - Global option binders

**Commands Implemented:**
```bash
unityctl bridge status          # Check bridge status
unityctl bridge start           # Start bridge daemon
unityctl console tail           # View console logs
unityctl scene list             # List scenes
unityctl scene load <path>      # Load a scene
unityctl play enter/exit/toggle # Control play mode
unityctl play status            # Get play mode status
unityctl asset import <path>    # Import an asset
unityctl asset refresh          # Refresh assets (triggers compilation if needed)
```

**Global Options:**
- `--project <path>` - Specify project path
- `--agent-id <id>` - Set agent ID
- `--json` - JSON output mode

### 4. UnityCtl.UnityPackage (Unity Editor Plugin)

**Location:** `UnityCtl.UnityPackage/`
**Package Name:** `com.dirtybit.unityctl`
**Unity Version:** 6.0+

**Files:**
- `package.json` - UPM package manifest
- `Editor/UnityCtl.asmdef` - Assembly definition
- `Editor/UnityCtlBootstrap.cs` - Initialization and event handlers
- `Editor/UnityCtlClient.cs` - WebSocket client and command handlers
- `Plugins/UnityCtl.Protocol.dll` - Protocol library

**Commands Handled:**
- `console.tail` - Log buffering
- `scene.list` - List build settings or all scenes
- `scene.load` - Load scene (single/additive)
- `play.enter/exit/toggle/status` - Play mode control
- `asset.import` - Asset import
- `asset.refresh` - Refresh assets (triggers compilation)

**Events Sent:**
- `log` - Console log entries (all levels)
- `playModeChanged` - Play mode state changes
- `compilation.started` - Compilation start
- `compilation.finished` - Compilation end with success status

**Key Features:**
- Auto-connect on Editor startup
- Auto-reconnect after domain reload
- Main thread action queue for Unity API calls
- Non-intrusive (no effect if bridge not running)
- WebSocket-based real-time communication

## Project Structure

```
unityctl/
├── .gitignore                    # Git ignore rules
├── README.md                     # User documentation
├── IMPLEMENTATION.md             # This file
├── bootstrap.md                  # Original design spec
├── unityctl.sln                  # Solution file
├── UnityCtl.Protocol/            # Shared protocol library
│   ├── UnityCtl.Protocol.csproj
│   ├── Messages.cs
│   ├── Config.cs
│   ├── DTOs.cs
│   ├── Constants.cs
│   ├── JsonHelper.cs
│   ├── ProjectLocator.cs
│   └── IsExternalInit.cs
├── UnityCtl.Bridge/              # Bridge daemon
│   ├── UnityCtl.Bridge.csproj
│   ├── Program.cs
│   ├── BridgeState.cs
│   └── BridgeEndpoints.cs
├── UnityCtl.Cli/                 # CLI tool
│   ├── UnityCtl.Cli.csproj
│   ├── Program.cs
│   ├── BridgeClient.cs
│   ├── Binders.cs
│   ├── ConsoleCommands.cs
│   ├── SceneCommands.cs
│   ├── PlayCommands.cs
│   ├── AssetCommands.cs
│   └── BridgeCommands.cs
├── UnityCtl.UnityPackage/        # Unity UPM package
│   ├── package.json
│   ├── Editor/
│   │   ├── UnityCtl.asmdef
│   │   ├── UnityCtlBootstrap.cs
│   │   └── UnityCtlClient.cs
│   └── Plugins/
│       └── UnityCtl.Protocol.dll
└── unity-project/                # Test Unity project
    ├── Packages/manifest.json    # (includes UnityCtl package)
    └── ...
```

## Technical Highlights

### Protocol Design

- **Type-based message routing** using JSON discriminators
- **Request/Response pattern** with unique request IDs
- **Event streaming** for real-time updates
- **Status codes** (ok/error) with structured error payloads

### Project Isolation

Each Unity project gets its own bridge instance:
```json
{
  "projectId": "proj-a1b2c3d4",
  "port": 49521,
  "pid": 12345
}
```

Stored in `.unityctl/bridge.json` at project root.

### Domain Reload Handling

Unity's domain reload (triggered by compilation) destroys all Editor objects:
1. Bridge maintains connection and state
2. Unity plugin uses `[InitializeOnLoad]` and `[DidReloadScripts]`
3. Plugin reconnects after each domain reload
4. No commands are lost during reconnection

### Threading Model

**Bridge:**
- Main thread: HTTP/WebSocket server
- Background threads: WebSocket receive loops
- Thread-safe: ConcurrentDictionary for pending requests

**Unity Plugin:**
- Background thread: WebSocket receive loop
- Main thread: Command execution via action queue
- Pumped every EditorApplication.update

## Build Instructions

### Building Everything

```bash
# Build all .NET projects
dotnet build unityctl.sln

# Build Protocol DLL for Unity
dotnet build UnityCtl.Protocol/UnityCtl.Protocol.csproj -c Release
cp UnityCtl.Protocol/bin/Release/netstandard2.1/UnityCtl.Protocol.dll UnityCtl.UnityPackage/Plugins/
```

### Installing as Global Tools

```bash
# Pack tools
dotnet pack UnityCtl.Cli/UnityCtl.Cli.csproj -o ./artifacts
dotnet pack UnityCtl.Bridge/UnityCtl.Bridge.csproj -o ./artifacts

# Install globally
dotnet tool install -g UnityCtl.Cli --add-source ./artifacts
dotnet tool install -g UnityCtl.Bridge --add-source ./artifacts
```

## Usage Example

1. **Start the bridge:**
   ```bash
   cd unity-project
   unityctl bridge start --project .
   ```

2. **Open Unity Editor** with the project

3. **Use CLI:**
   ```bash
   unityctl --project ./unity-project bridge status
   unityctl --project ./unity-project play enter
   unityctl --project ./unity-project scene list
   unityctl --project ./unity-project console tail --lines 20
   ```

## Testing

The implementation includes a test Unity project at `unity-project/` with the UnityCtl package already configured.

**To test:**

1. Start bridge: `dotnet run --project UnityCtl.Bridge/UnityCtl.Bridge.csproj -- --project ./unity-project`
2. Open `unity-project/` in Unity Editor 6.0
3. Run CLI commands: `dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --project ./unity-project bridge status`

## Known Issues & Fixes Applied

### netstandard2.1 Compatibility

- Added polyfills for `IsExternalInit`, `RequiredMemberAttribute`, etc.
- Used older SHA256 API (Create() instead of HashData())

### Global Options in System.CommandLine

- Fixed binders to traverse command tree for global options
- Handles nested commands properly

### Unity API

- Added `using UnityEditor.Callbacks` for `[DidReloadScripts]`
- Used CompilationPipeline for script compilation
- Proper main thread marshalling for Unity APIs

## Architecture Decisions

1. **Why WebSocket for Unity?**
   - Real-time bidirectional communication
   - Event streaming (logs, play mode, compilation)
   - Built-in in .NET and Unity

2. **Why HTTP for CLI?**
   - Simple request/response pattern
   - No connection management needed
   - Easy to debug (curl-friendly)

3. **Why separate Bridge process?**
   - Survives Unity domain reloads
   - Independent of Unity lifecycle
   - Can buffer logs and state
   - Multiple CLIs can connect

4. **Why netstandard2.1 for Protocol?**
   - Compatible with Unity 6.0+
   - Shared between .NET 10.0 and Unity
   - Single source of truth for protocol

## Compliance with Specification

✅ All goals from bootstrap.md achieved:
- Control running Unity 6.0 editor from CLI
- Read console logs
- Trigger asset import and script compilation
- List and load scenes
- Enter / exit / toggle play mode
- Stable connection across domain reloads
- Multi-agent support
- Project isolation
- Non-intrusive to teammates

✅ All wire protocol commands implemented:
- console.tail
- asset.import, asset.reimportAll (not exposed in CLI yet), asset.refresh
- scene.list, scene.load
- play.enter, play.exit, play.toggle, play.status

✅ All events implemented:
- log
- playModeChanged
- compilation.started, compilation.finished

## Future Enhancements

- Add `asset.reimportAll` CLI command
- Add scene unload command
- Add prefab instantiation
- Add GameObject hierarchy inspection
- Add component property get/set
- Add custom command extensibility
- Add authentication/authorization
- Package for NuGet and Unity Asset Store
