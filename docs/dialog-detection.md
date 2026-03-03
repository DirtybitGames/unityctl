# Dialog Detection

Unity Editor shows modal dialog popups in various situations — compilation errors triggering Safe Mode, license issues, import failures, etc. These dialogs block Unity's main thread, which means:

- The UnityCtl plugin cannot process any RPC commands
- `unityctl wait` hangs indefinitely
- The bridge reports Unity as connected, but all commands time out (504)

The `dialog` command detects these popups from the CLI side (no bridge or plugin needed) and can dismiss them programmatically.

## Commands

### `unityctl dialog list`

Lists all detected popup dialogs for the running Unity Editor.

```
$ unityctl dialog list
Detected 1 dialog(s):

  "Enter Safe Mode?" [Enter Safe Mode] [Ignore] [Quit]
```

```
$ unityctl dialog list --json
[{"title":"Enter Safe Mode?","buttons":["Enter Safe Mode","Ignore","Quit"]}]
```

### `unityctl dialog dismiss`

Dismisses the first detected dialog by clicking a button.

```
$ unityctl dialog dismiss --button "Ignore"
Clicked "Ignore" on "Enter Safe Mode?"
```

If `--button` is omitted, clicks the first button. Button matching is case-insensitive.

### `unityctl status`

The `status` command also reports detected dialogs:

```
Popups: [!] 1 dialog detected
  "Enter Safe Mode?" [Enter Safe Mode] [Ignore] [Quit]
  Use 'unityctl dialog dismiss' to dismiss
```

## Architecture

Dialog detection is purely client-side — it runs in the CLI process using OS-level window APIs. This is necessary because dialogs block Unity's main thread, so the plugin can't respond to bridge commands.

The flow is:
1. Find the Unity process for the project (`FindUnityProcessForProject`)
2. Use platform-specific APIs to enumerate that process's dialog windows
3. Extract window titles and button labels
4. Optionally click a button to dismiss

## Platform Support

### Windows (fully working)

Uses Win32 P/Invoke APIs to detect and interact with dialogs:

- **Detection**: `EnumWindows` to find visible windows belonging to the Unity PID, filtering for the `#32770` dialog window class
- **Button enumeration**: `EnumChildWindows` to find child controls with the `Button` class, reading text via `GetWindowText`
- **Button text cleanup**: Win32 button text includes `&` accelerator prefixes (e.g., `&OK`, `&Cancel`) — these are stripped automatically
- **Clicking**: `SendMessage(WM_COMMAND)` to the parent dialog with the button's control ID (via `GetDlgCtrlID`). `SetForegroundWindow` is called first to ensure the dialog processes messages

**Why `SendMessage(WM_COMMAND)` instead of `PostMessage(BM_CLICK)`**: During testing, `PostMessage(BM_CLICK)` proved unreliable across processes — the dismiss would report success but the dialog wouldn't actually close. `SendMessage(WM_COMMAND)` mimics exactly what the dialog's own message loop does when a button is clicked, making it reliable cross-process. Falls back to `SendMessage(BM_CLICK)` if control ID lookup fails.

**No special permissions required.** Works from any terminal, SSH session, or CI environment.

### macOS (limited)

Uses AppleScript via `osascript` to interact with System Events:

- **Detection**: Enumerates windows of the Unity process by PID, looking for windows that have button controls
- **Clicking**: Uses `click button "<name>" of window "<title>" of process "<name>"`
- **Script delivery**: Scripts are piped via stdin to avoid shell quoting issues

**Accessibility permission required.** macOS requires the calling process to have Accessibility access (System Settings > Privacy & Security > Accessibility). This means:

- From **Terminal.app** or **iTerm2**: Works after a one-time permission grant for the terminal app
- From **SSH sessions**: Does not work because `sshd` (`/usr/libexec/sshd-keygen-wrapper`) lacks Accessibility permission, and the permission prompt cannot be shown remotely. The `osascript` call hangs indefinitely until killed by the 5-second subprocess timeout. Detection returns an empty list gracefully (no crash, no hang).

To enable over SSH, you would need to physically grant Accessibility access to the SSH daemon on the Mac. In managed environments, Accessibility permissions can be pre-provisioned via MDM/PPPC profiles.

**Alternative APIs investigated and ruled out:**

| API | Issue |
|-----|-------|
| AXUIElement (Swift) | Same Accessibility permission requirement (it's what osascript uses under the hood) |
| JXA (`osascript -l JavaScript`) | Same osascript binary, same permission model |
| CGWindowListCopyWindowInfo | Can count windows (no permission), but can't read titles (needs Screen Recording) or buttons |
| NSWorkspace / AppKit | Can't enumerate windows of other processes; not daemon-safe |
| lsappinfo | App-level only, no window information |
| CGSConnection | Private API, fragile, no button-level interaction |

The fundamental macOS limitation is that **any API that can read window content or interact with UI elements requires Accessibility permissions**. There is no workaround. The current approach is the right one — it works from local terminals and degrades gracefully over SSH.

### Linux (best-effort)

Uses a combination of tools:

- **Window listing**: `xdotool search --pid <PID>` (X11 only) + `xdotool getwindowname` for titles
- **Button enumeration**: `python3` with `pyatspi` (AT-SPI2 accessibility framework) to enumerate `ROLE_PUSH_BUTTON` controls within dialog/alert windows
- **Clicking**: `pyatspi` `doAction(0)` on the target button

**Fallback behavior**:
- If `xdotool` is not installed or on Wayland: falls back to pyatspi-only detection
- If `pyatspi` is not installed: returns dialogs with titles but empty button lists (titles are still useful for status reporting)
- If neither is available: returns empty list silently

**No special permissions required** on most Linux desktop environments.

## Unity Safe Mode

### What it is

When Unity detects compilation errors on startup, it can enter **Safe Mode** — a restricted state that prevents most editor operations. The `EnterSafeModeDialog` EditorPref controls the behavior:

| Value | Behavior |
|-------|----------|
| **1** (default) | Shows "Enter Safe Mode?" dialog with 3 buttons: **Enter Safe Mode**, **Ignore**, **Quit** |
| **0** | Skips the dialog and **automatically enters safe mode** |

### Why suppressing the dialog is counterproductive

Setting `EnterSafeModeDialog = 0` causes Unity to enter safe mode automatically without showing the dialog. This is **bad for automated workflows** because:

1. In safe mode, Unity restricts domain reloading and assembly loading
2. The UnityCtl plugin cannot load in safe mode
3. The bridge never gets a WebSocket connection from Unity
4. All RPC commands hang or fail, `unityctl wait` times out

### Correct approach for automation

**Let the dialog appear** (keep `EnterSafeModeDialog = 1`, which is the default) and have the agent click **"Ignore"** via `unityctl dialog dismiss --button "Ignore"`. This keeps Unity in normal mode where the plugin connects and RPC works, even with compilation errors present.

The typical agent workflow for handling startup with broken scripts:

```bash
# Launch Unity
unityctl editor run --project ./my-project

# Wait for connection — if this times out, check for dialogs
unityctl wait --timeout 60 || {
    # Check if a dialog is blocking
    unityctl dialog list
    # Dismiss the safe mode dialog by clicking "Ignore"
    unityctl dialog dismiss --button "Ignore"
    # Wait again now that the dialog is gone
    unityctl wait --timeout 60
}
```

### Registry location (Windows)

The `EnterSafeModeDialog` preference is stored in the Windows registry:

- Key: `HKCU\Software\Unity Technologies\Unity Editor 5.x`
- Value name: `EnterSafeModeDialog_h2431637559` (the suffix is a DJB2 hash)

## Triggering Test Dialogs

`EditorUtility.DisplayDialog` blocks Unity's main thread. To spawn a dialog for testing without blocking the script eval RPC call, schedule it for the next editor frame:

```csharp
// Via unityctl script eval
unityctl script eval "EditorApplication.CallbackFunction show = null; \
  show = () => { EditorApplication.update -= show; \
  EditorUtility.DisplayDialog(\"Test\", \"Hello\", \"OK\", \"Cancel\"); }; \
  EditorApplication.update += show; return \"scheduled\";"
```

Key details:
- Must use `EditorApplication.CallbackFunction` as the delegate type (not `System.Action` — Unity's delegate type is different)
- Must use `EditorApplication.update`, not `EditorApplication.delayCall` (delayCall does not work reliably for this)
- The delegate must unregister itself on first invocation to avoid repeated dialog spawning

## Implementation Details

### `DialogDetector.cs`

Single static class with platform dispatch (`DetectDialogs`, `ClickButton`). Best-effort on all platforms — if detection fails (missing tools, no permissions), returns empty list silently. Never fails the parent command.

### `DialogCommands.cs`

Two subcommands under `unityctl dialog`:
- `list` — enumerates dialogs, outputs human-readable or JSON
- `dismiss --button <text>` — clicks the named button (case-insensitive), defaults to first button

### `StatusCommand.cs`

If Unity is running, calls `DialogDetector.DetectDialogs` and includes any detected dialogs in both human-readable and JSON output.

### Subprocess timeout

All external process calls (`osascript`, `xdotool`, `python3`) use a 5-second timeout with `process.Kill()` on expiry. This prevents the CLI from hanging when macOS Accessibility prompts block `osascript` indefinitely.
