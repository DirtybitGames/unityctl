# Troubleshooting UnityCtl

This guide covers common issues and how to resolve them.

## Bridge Issues

### Bridge not starting

**Symptoms:**
- Error: "Failed to start bridge"
- Error: "Port already in use"

**Solutions:**

1. **Check if port is already in use:**
   ```bash
   # Windows
   netstat -ano | findstr :49521

   # Linux/Mac
   lsof -i :49521
   ```

   If a process is using the port, either kill it or let the bridge auto-assign a different port.

2. **Verify you're in a Unity project directory:**
   ```bash
   # Check for Unity project marker
   ls ProjectSettings/ProjectVersion.txt
   ```

   If not found, use `--project` flag:
   ```bash
   unityctl bridge start --project /path/to/unity-project
   ```

3. **Check bridge logs:**
   - Look for error messages in bridge output
   - Check if .NET 10.0 runtime is installed:
     ```bash
     dotnet --version
     ```

4. **Kill existing bridge processes:**
   ```bash
   # Windows
   taskkill /F /IM unityctl-bridge.exe

   # Linux/Mac
   pkill -f unityctl-bridge
   ```

### Bridge status shows "Not running"

**Symptoms:**
- `unityctl bridge status` shows bridge is not running
- `.unityctl/bridge.json` exists but bridge is dead

**Solutions:**

1. **Check if process is actually running:**
   ```bash
   # Read PID from bridge.json
   cat .unityctl/bridge.json

   # Check if process exists (Windows)
   tasklist | findstr <PID>

   # Check if process exists (Linux/Mac)
   ps -p <PID>
   ```

2. **Clean up stale config:**
   ```bash
   rm -rf .unityctl
   unityctl bridge start
   ```

3. **Restart the bridge:**
   ```bash
   unityctl bridge start --project .
   ```

## Unity Connection Issues

### Unity not connecting to bridge

**Symptoms:**
- No `[UnityCtl]` logs in Unity console
- Unity Editor is open but CLI shows "Unity not connected"

**Note:** The Unity plugin uses exponential backoff when attempting to reconnect to the bridge. Connection attempts occur at intervals of 1s, 2s, 4s, 8s, up to a maximum of 15 seconds between attempts. If the bridge was just started, allow up to 15 seconds for Unity to reconnect.

**Solutions:**

1. **Check bridge is running:**
   ```bash
   unityctl bridge status
   ```

   Expected output:
   ```
   Bridge Status: Running
   PID: 12345
   Port: 49521
   Unity Connected: Yes
   ```

2. **Verify bridge.json exists:**
   ```bash
   cat .unityctl/bridge.json
   ```

   If missing, restart the bridge.

3. **Check Unity console for errors:**
   - Look for `[UnityCtl]` messages
   - Check for WebSocket connection errors
   - Enable "Error Pause" to catch exceptions

4. **Restart Unity Editor:**
   - Close Unity completely
   - Ensure bridge is running
   - Reopen Unity project
   - Check console for `[UnityCtl] Connected` message

5. **Check Unity package is installed:**
   - Open Package Manager (Window → Package Manager)
   - Look for "Unity Ctl" in the list
   - Verify `Packages/manifest.json` includes:
     ```json
     "com.dirtybit.unityctl": "https://github.com/DirtybitGames/unityctl.git?path=UnityCtl.UnityPackage#v0.1.0"
     ```

6. **Check for Unity errors:**
   - Look for compilation errors in Unity console
   - Ensure all scripts compile successfully
   - Try: Assets → Reimport All

### Unity connects then immediately disconnects

**Symptoms:**
- Unity console shows `[UnityCtl] Connected` then `[UnityCtl] Disconnected`
- Connection drops immediately

**Solutions:**

1. **Check for Unity Editor crashes:**
   - Look for crash logs in Unity console
   - Check Unity Hub for crash reports

2. **Check bridge logs:**
   - Look for WebSocket errors
   - Check for authentication issues

3. **Firewall/Antivirus:**
   - Ensure localhost connections are allowed
   - Try temporarily disabling firewall/antivirus

## Command Execution Issues

### Commands timing out

**Symptoms:**
- Error: "Request timed out after 30s"
- Commands take too long to execute

**Solutions:**

1. **Ensure Unity Editor window has focus:**
   - Some Unity operations require the Editor window to be focused
   - Click on Unity Editor window before running command

2. **Check if Unity is responsive:**
   - Unity may be frozen or stuck
   - Look for "Not Responding" in window title (Windows)
   - Check Activity Monitor/Task Manager for high CPU usage

3. **Check for errors in Unity console:**
   - Look for exceptions or errors
   - Check if Unity is waiting for user input (dialogs, etc.)

4. **Try a simpler command first:**
   ```bash
   unityctl bridge status
   unityctl play status
   ```

### Commands return errors

**Symptoms:**
- Command executes but returns error response
- Unity console shows errors

**Solutions:**

1. **Check scene path is correct:**
   ```bash
   # List all scenes
   unityctl scene list --source all

   # Use exact path from list
   unityctl scene load Assets/Scenes/Main.unity
   ```

2. **Check asset path is valid:**
   ```bash
   # Verify asset exists in Unity project
   ls Assets/Textures/logo.png
   ```

3. **Check play mode state:**
   - Can't load scenes while in play mode
   - Exit play mode first:
     ```bash
     unityctl play exit
     ```

4. **Check Unity console for detailed errors:**
   - Unity may provide more context
   - Look for stack traces

## Domain Reload Issues

### Commands fail after script compilation

**Symptoms:**
- Commands work initially
- After script changes, commands timeout or fail
- Unity console shows recompilation messages

**Solutions:**

1. **Wait for reconnection:**
   - Plugin automatically reconnects after domain reload
   - Initial reconnection attempt is immediate after domain reload
   - If connection fails, the plugin uses exponential backoff (1s, 2s, 4s... up to 15s max)
   - Check Unity console for `[UnityCtl] Connected` message

2. **Check reconnection succeeded:**
   ```bash
   unityctl bridge status
   ```

   Should show "Unity Connected: Yes"

3. **Manual reconnection:**
   - Restart Unity Editor if auto-reconnect fails
   - Bridge survives domain reload, no need to restart it

4. **Check for compilation errors:**
   - Domain reload may have failed due to errors
   - Fix all compilation errors in Unity console
   - Wait for successful recompilation

## Installation Issues

### "dotnet tool install" fails

**Symptoms:**
- Error: "Package not found"
- Error: "Invalid package source"

**Solutions:**

1. **Check .NET SDK version:**
   ```bash
   dotnet --version
   ```

   Requires .NET 10.0 or later.

2. **Check NuGet package exists:**
   - Visit https://www.nuget.org/packages/UnityCtl.Cli
   - Verify package version exists

3. **Clear NuGet cache:**
   ```bash
   dotnet nuget locals all --clear
   ```

4. **Try with explicit version:**
   ```bash
   dotnet tool install -g UnityCtl.Cli --version 0.1.0
   ```

### Unity package not found

**Symptoms:**
- Package Manager shows error
- "No packages found" in Unity

**Solutions:**

1. **Check Git URL is correct:**
   ```json
   "com.dirtybit.unityctl": "https://github.com/DirtybitGames/unityctl.git?path=UnityCtl.UnityPackage#v0.1.0"
   ```

2. **Check Git is installed:**
   ```bash
   git --version
   ```

   Unity requires Git for Git-based packages.

3. **Try Git credential helper:**
   - Package Manager may need Git credentials
   - Set up SSH keys or credential manager

4. **Try manual clone:**
   ```bash
   git clone https://github.com/DirtybitGames/unityctl.git
   ```

   Then use local path:
   ```json
   "com.dirtybit.unityctl": "file:../unityctl/UnityCtl.UnityPackage"
   ```

## Debug Mode

### Enable verbose logging

**Bridge:**
```bash
# Not yet implemented - coming soon
unityctl bridge start --verbose
```

**Unity:**
Add this to Unity console to see all messages:
```csharp
// In Unity Editor console
Debug.Log("UnityCtl debug enabled");
```

**CLI:**
```bash
# Use --json for structured output
unityctl --json bridge status
```

## Getting Help

If you've tried these solutions and still have issues:

1. **Check GitHub Issues:**
   - Search existing issues: https://github.com/DirtybitGames/unityctl/issues
   - Your problem may already be reported

2. **Create a new issue with:**
   - UnityCtl version (`unityctl --version`)
   - Unity version
   - Operating system
   - Steps to reproduce
   - Error messages
   - Relevant logs

3. **Include diagnostic info:**
   ```bash
   # Bridge status
   unityctl bridge status

   # Check tool installation
   dotnet tool list -g | grep UnityCtl

   # Check .NET version
   dotnet --version

   # Check Unity logs (last 50 lines)
   unityctl console tail --lines 50
   ```
