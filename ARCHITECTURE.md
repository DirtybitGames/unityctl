# UnityCtl Architecture

This document provides a technical deep-dive into how UnityCtl works, its architecture, and design decisions.

## System Overview

UnityCtl uses a three-tier architecture to enable remote control of Unity Editor:

```
┌─────────────┐     HTTP      ┌──────────────┐    WebSocket   ┌──────────────┐
│   CLI Tool  │──────────────▶│    Bridge    │◀───────────────│ Unity Plugin │
│  (unityctl) │               │   (daemon)   │                │  (Editor)    │
└─────────────┘               └──────────────┘                └──────────────┘
     │                               │                                │
     │                               │                                │
     └──────────── Detects ──────────┴──── Writes ────────────────────┘
                .unityctl/bridge.json in project root
```

## Components

### 1. Protocol Layer (UnityCtl.Protocol)

Shared library (netstandard2.1) containing all message types and shared logic.

**Key Components:**

- **Message Types:**
  - `HelloMessage` - Initial handshake
  - `Request` - Commands from CLI to Unity
  - `Response` - Results from Unity to CLI
  - `Event` - Async events from Unity (logs, play mode changes)

- **Configuration:**
  - `BridgeConfig` - Bridge connection details (port, PID, project ID)
  - Project detection logic (finds Unity project root)

- **DTOs:**
  - Request/response payloads for all commands
  - Scene info, console logs, compilation results

**Why netstandard2.1?**
- Compatible with both .NET 8.0 (CLI/Bridge) and Unity 2021.3+ (requires netstandard2.1)
- Allows sharing types without duplication

### 2. Bridge Daemon (UnityCtl.Bridge)

.NET 8.0 console application that runs as a daemon process.

**Responsibilities:**

1. **HTTP Server** - Handles CLI requests
   - `GET /health` - Health check
   - `POST /rpc` - Execute commands
   - `GET /console/tail?lines=N` - Get recent logs

2. **WebSocket Server** - Maintains connection with Unity
   - `WS /unity` - Persistent connection
   - Handles reconnection after domain reload

3. **Request/Response Matching** - Correlates CLI requests with Unity responses
   - Each request has unique ID
   - Timeout handling (default 30s)

4. **Log Buffering** - Stores last 1000 console entries
   - Ring buffer for efficient memory usage
   - Supports tail queries from CLI

**Process Model:**

```
unityctl bridge start
         │
         ▼
    Fork daemon ───▶ Background process
         │                    │
         │                    ├─ HTTP server (CLI)
         │                    ├─ WebSocket server (Unity)
         │                    └─ Log buffer
         │
    Write bridge.json
         │
    Exit (daemon continues)
```

### 3. CLI Tool (UnityCtl.Cli)

.NET 8.0 console application using System.CommandLine for argument parsing.

**Architecture:**

```
unityctl play enter
      │
      ▼
Parse arguments (System.CommandLine)
      │
      ▼
Detect project (.unityctl/bridge.json)
      │
      ▼
HTTP POST to bridge (/rpc)
      │
      ▼
Wait for response (with timeout)
      │
      ▼
Format output (human or JSON)
```

**Project Detection:**
1. Check `--project` flag
2. Walk up directory tree
3. Find `ProjectSettings/ProjectVersion.txt`
4. Read `.unityctl/bridge.json` for connection details

### 4. Unity Plugin (com.dirtybit.unityctl)

Unity Editor package that connects to the bridge via WebSocket.

**Key Components:**

- **UnityCtlBootstrap.cs** - `[InitializeOnLoad]` auto-starts client
- **UnityCtlClient.cs** - WebSocket client and command handlers

**Threading Model:**

Unity Editor APIs must run on main thread, but WebSocket runs on background thread:

```
┌──────────────────┐         ┌─────────────────┐
│ WebSocket Thread │         │   Main Thread   │
└────────┬─────────┘         └────────┬────────┘
         │                            │
    Message arrives                   │
         │                            │
    Deserialize                       │
         │                            │
    Queue action ──────────▶ Execute on main
         │                            │
    Wait for result                   │
         │                  ◀─── Complete action
         │                            │
    Send response                     │
         │                            │
```

**Main Thread Queue:**
```csharp
EditorApplication.update += () => {
    while (_mainThreadActions.TryDequeue(out var action)) {
        action();
    }
};
```

## Domain Reload Resilience

Unity's domain reload destroys all Editor objects when scripts recompile. UnityCtl handles this gracefully:

### Problem

```
Unity Plugin         Bridge
     │                  │
     ├─────Connected────┤
     │                  │
[Domain Reload] ────────┤  ← Unity plugin destroyed
     │                  │
     X                  │  ← Connection lost
```

### Solution

1. **Bridge survives** - Runs as separate process
2. **Bridge maintains state** - Keeps log buffer, connection info
3. **Unity reconnects** - `[InitializeOnLoad]` runs after reload
4. **Seamless handshake** - Plugin re-establishes connection

```
Unity Plugin         Bridge
     │                  │
     ├─────Connected────┤
     │                  │
[Domain Reload]         │  ← Bridge unaffected
     │                  │
[Plugin Reloads]        │
     │                  │
     ├────Reconnect─────┤  ← Auto-reconnect
     │                  │
     └─────Connected────┘
```

### Implementation

**Bridge side:**
- WebSocket disconnection is detected
- State is preserved (log buffer, config)
- Ready for new connection

**Unity side:**
- `[InitializeOnLoad]` ensures bootstrap runs after reload
- Client attempts connection using bridge.json
- Exponential backoff on connection failures

## Request/Response Flow

### Example: Load Scene Command

```
CLI                    Bridge                 Unity
 │                       │                      │
 │──1. POST /rpc────────▶│                      │
 │   {                   │                      │
 │     "method": "scene.load",                  │
 │     "path": "Assets/Scenes/Main.unity"       │
 │   }                   │                      │
 │                       │                      │
 │                       │──2. Forward (WS)────▶│
 │                       │   request_id: "abc"  │
 │                       │                      │
 │                       │                      │──3. Queue action
 │                       │                      │   (main thread)
 │                       │                      │
 │                       │                      │──4. Execute
 │                       │                      │   EditorSceneManager.LoadScene()
 │                       │                      │
 │                       │◀─5. Response (WS)────│
 │                       │   request_id: "abc"  │
 │                       │   success: true      │
 │                       │                      │
 │◀─6. Return JSON───────│                      │
 │   200 OK              │                      │
 │   { "success": true } │                      │
 │                       │                      │
```

## Project Isolation

Each Unity project has its own bridge instance to prevent conflicts.

**Bridge Config (`.unityctl/bridge.json`):**

```json
{
  "projectId": "proj-a1b2c3d4",
  "port": 49521,
  "pid": 12345
}
```

- **projectId** - Stable hash of absolute project path
- **port** - Dynamic port assigned by OS (0 = auto-assign)
- **pid** - Bridge process ID for health checks

**Port Assignment:**

1. Bridge starts with port 0 (OS assigns free port)
2. Actual port written to bridge.json
3. CLI reads port from bridge.json
4. Unity plugin reads port from bridge.json

This allows multiple Unity projects to run simultaneously without port conflicts.

## Security Considerations

**Current Model:**
- Bridge binds to `localhost` only (not accessible remotely)
- No authentication (assumes local trust)
- Unity plugin trusts all commands from bridge

**Future Considerations:**
- Add optional API key for bridge ↔ Unity connection
- Rate limiting on bridge endpoints
- Command whitelist/blacklist in Unity

## Performance

**Log Buffering:**
- Ring buffer of 1000 entries
- O(1) append, O(n) tail query
- Minimal memory overhead (~100KB for 1000 logs)

**WebSocket vs HTTP:**
- WebSocket for Unity → Bridge (persistent, low latency)
- HTTP for CLI → Bridge (stateless, simple)

**Threading:**
- Bridge: Thread pool for HTTP requests
- Unity: Main thread for all Unity APIs
- Background thread for WebSocket I/O

## Error Handling

**Connection Failures:**
- CLI: Immediate error with troubleshooting hints
- Unity: Exponential backoff, retry up to 10 times
- Bridge: Graceful shutdown on fatal errors

**Command Timeouts:**
- Default: 30 seconds
- Configurable via CLI flags
- Returns error with partial state

**Domain Reload:**
- In-flight requests are lost
- CLI receives timeout error
- User can retry after reconnection

## Future Enhancements

- **Multi-agent support** - Multiple Unity instances per project (for multiplayer testing)
- **Event subscriptions** - CLI can listen for Unity events
- **Batch commands** - Execute multiple commands atomically
- **Snapshot/restore** - Save and restore editor state
- **Remote access** - Optional TCP binding for remote control (with auth)
