using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UnityCtl.Cli;

internal class DetectedButton
{
    public required string Text { get; init; }
    public required object NativeHandle { get; init; }
}

internal class DetectedDialog
{
    public required string Title { get; init; }
    public required List<DetectedButton> Buttons { get; init; }
    public required object NativeHandle { get; init; }

    /// <summary>Platform-specific context needed for clicking buttons (e.g., macOS process name).</summary>
    public string? ProcessContext { get; init; }

    /// <summary>Description text from static labels (e.g., progress bar description).</summary>
    public string? Description { get; init; }

    /// <summary>Progress value 0.0-1.0 if this dialog contains a progress bar, null otherwise.</summary>
    public float? Progress { get; init; }
}

internal static class DialogDetector
{
    public static List<DetectedDialog> DetectDialogs(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return DetectDialogsWindows(processId);
            if (OperatingSystem.IsMacOS())
                return DetectDialogsMacOS(processId);
            if (OperatingSystem.IsLinux())
                return DetectDialogsLinux(processId);
        }
        catch
        {
            // Best-effort — never fail the parent command
        }

        return [];
    }

    public static bool ClickButton(DetectedDialog dialog, string buttonText)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ClickButtonWindows(dialog, buttonText);
            if (OperatingSystem.IsMacOS())
                return ClickButtonMacOS(dialog, buttonText);
            if (OperatingSystem.IsLinux())
                return ClickButtonLinux(dialog, buttonText);
        }
        catch
        {
            // Best-effort
        }

        return false;
    }

    // ─── Windows ────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static List<DetectedDialog> DetectDialogsWindows(int processId)
    {
        var dialogs = new List<DetectedDialog>();

        Win32.EnumWindows((hWnd, _) =>
        {
            // Check if window belongs to our process
            Win32.GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != processId)
                return true; // continue

            // Must be visible
            if (!Win32.IsWindowVisible(hWnd))
                return true;

            var classNameBuf = new StringBuilder(256);
            Win32.GetClassName(hWnd, classNameBuf, classNameBuf.Capacity);
            var className = classNameBuf.ToString();

            // Match dialog windows (#32770) and Unity splash/loading windows
            var isDialog = className == "#32770";
            var isSplash = className == "UnitySplashWindow";
            if (!isDialog && !isSplash)
                return true;

            // Get title
            var titleBuf = new StringBuilder(256);
            Win32.GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
            var title = titleBuf.ToString();

            // Enumerate child controls — buttons, progress bars, static text
            var buttons = new List<DetectedButton>();
            float? progress = null;
            string? description = null;

            Win32.EnumChildWindows(hWnd, (childHwnd, __) =>
            {
                var childClassBuf = new StringBuilder(256);
                Win32.GetClassName(childHwnd, childClassBuf, childClassBuf.Capacity);
                var childClass = childClassBuf.ToString();

                if (childClass == "Button")
                {
                    var btnTextBuf = new StringBuilder(256);
                    Win32.GetWindowText(childHwnd, btnTextBuf, btnTextBuf.Capacity);
                    var btnText = btnTextBuf.ToString();
                    // Strip Win32 accelerator prefix (&) — e.g. "&OK" → "OK"
                    btnText = btnText.Replace("&", "");
                    if (!string.IsNullOrWhiteSpace(btnText))
                    {
                        buttons.Add(new DetectedButton
                        {
                            Text = btnText,
                            NativeHandle = childHwnd
                        });
                    }
                }
                else if (childClass == "msctls_progress32")
                {
                    // Read progress position and range
                    var pos = (int)Win32.SendMessage(childHwnd, Win32.PBM_GETPOS, IntPtr.Zero, IntPtr.Zero);
                    var rangeHigh = (int)Win32.SendMessage(childHwnd, Win32.PBM_GETRANGE, IntPtr.Zero, IntPtr.Zero);
                    if (rangeHigh <= 0)
                        rangeHigh = 100; // default range
                    progress = Math.Clamp((float)pos / rangeHigh, 0f, 1f);
                }
                else if (childClass == "Static")
                {
                    var textBuf = new StringBuilder(512);
                    Win32.GetWindowText(childHwnd, textBuf, textBuf.Capacity);
                    var text = textBuf.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Keep the longest static text as the description
                        if (description == null || text.Length > description.Length)
                            description = text;
                    }
                }

                return true;
            }, IntPtr.Zero);

            dialogs.Add(new DetectedDialog
            {
                Title = title,
                Buttons = buttons,
                NativeHandle = hWnd,
                Description = description,
                Progress = progress
            });

            return true; // continue looking for more dialogs
        }, IntPtr.Zero);

        return dialogs;
    }

    [SupportedOSPlatform("windows")]
    private static bool ClickButtonWindows(DetectedDialog dialog, string buttonText)
    {
        foreach (var button in dialog.Buttons)
        {
            if (string.Equals(button.Text, buttonText, StringComparison.OrdinalIgnoreCase))
            {
                var btnHwnd = (IntPtr)button.NativeHandle;
                var dlgHwnd = (IntPtr)dialog.NativeHandle;

                // Bring the dialog to the foreground so it processes messages
                Win32.SetForegroundWindow(dlgHwnd);

                // Get the button's control ID and send WM_COMMAND to the dialog.
                // This is more reliable than BM_CLICK across processes because
                // it mimics exactly what the dialog's message loop expects.
                var ctrlId = Win32.GetDlgCtrlID(btnHwnd);
                if (ctrlId != 0)
                {
                    // WM_COMMAND: HIWORD(wParam)=BN_CLICKED(0), LOWORD(wParam)=controlId, lParam=button hwnd
                    var wParam = (IntPtr)ctrlId; // BN_CLICKED is 0, so high word is 0
                    Win32.SendMessage(dlgHwnd, Win32.WM_COMMAND, wParam, btnHwnd);
                    return true;
                }

                // Fallback: direct BM_CLICK on the button
                Win32.SendMessage(btnHwnd, Win32.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
        }
        return false;
    }

    // ─── macOS ──────────────────────────────────────────────────────────

    [SupportedOSPlatform("macos")]
    private static List<DetectedDialog> DetectDialogsMacOS(int processId)
    {
        // AppleScript to enumerate windows and buttons for a process by PID.
        // Returns lines like: DIALOG:windowTitle|btn1,btn2,btn3
        var script = $@"
tell application ""System Events""
    set unityProcs to every process whose unix id is {processId}
    if (count of unityProcs) is 0 then return """"
    set unityProc to item 1 of unityProcs
    set procName to name of unityProc
    set output to """"
    repeat with w in (every window of unityProc)
        set wName to name of w
        set btns to """"
        try
            repeat with b in (every button of w)
                set bName to name of b
                if bName is not missing value then
                    if btns is not """" then set btns to btns & "",""
                    set btns to btns & bName
                end if
            end repeat
        end try
        if btns is not """" then
            set output to output & ""DIALOG:"" & wName & ""|"" & btns & linefeed
        end if
    end repeat
    return ""PROC:"" & procName & linefeed & output
end tell";

        var result = RunProcess("osascript", $"-e '{script}'", script);
        if (string.IsNullOrWhiteSpace(result))
            return [];

        var dialogs = new List<DetectedDialog>();
        string? processName = null;
        foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("PROC:"))
            {
                processName = line.Substring(5).Trim();
                continue;
            }

            if (!line.StartsWith("DIALOG:"))
                continue;

            var payload = line.Substring(7);
            var pipeIdx = payload.IndexOf('|');
            if (pipeIdx < 0) continue;

            var title = payload.Substring(0, pipeIdx);
            var buttonNames = payload.Substring(pipeIdx + 1).Split(',', StringSplitOptions.RemoveEmptyEntries);

            var buttons = new List<DetectedButton>();
            foreach (var name in buttonNames)
            {
                var trimmed = name.Trim();
                if (trimmed.Length > 0)
                {
                    buttons.Add(new DetectedButton
                    {
                        Text = trimmed,
                        NativeHandle = trimmed // macOS uses button name for clicking
                    });
                }
            }

            dialogs.Add(new DetectedDialog
            {
                Title = title,
                Buttons = buttons,
                NativeHandle = title, // window name for clicking
                ProcessContext = processName
            });
        }

        return dialogs;
    }

    [SupportedOSPlatform("macos")]
    private static bool ClickButtonMacOS(DetectedDialog dialog, string buttonText)
    {
        var processName = dialog.ProcessContext;
        if (processName == null)
            return false;

        // Find the matching button (case-insensitive)
        DetectedButton? target = null;
        foreach (var button in dialog.Buttons)
        {
            if (string.Equals(button.Text, buttonText, StringComparison.OrdinalIgnoreCase))
            {
                target = button;
                break;
            }
        }
        if (target == null)
            return false;

        var windowName = (string)dialog.NativeHandle;
        var escapedProcess = processName.Replace("\"", "\\\"");
        var escapedWindow = windowName.Replace("\"", "\\\"");
        var escapedButton = target.Text.Replace("\"", "\\\"");

        var script = $@"
tell application ""System Events""
    tell process ""{escapedProcess}""
        click button ""{escapedButton}"" of window ""{escapedWindow}""
    end tell
end tell";

        var result = RunProcess("osascript", $"-e '{script}'", script);
        return result != null;
    }

    // ─── Linux ──────────────────────────────────────────────────────────

    [SupportedOSPlatform("linux")]
    private static List<DetectedDialog> DetectDialogsLinux(int processId)
    {
        var dialogs = new List<DetectedDialog>();

        // Try xdotool first for window discovery
        var windowIds = RunProcess("xdotool", $"search --pid {processId}");
        if (windowIds == null)
        {
            // xdotool not available (possibly Wayland) — try pyatspi only
            return DetectDialogsLinuxPyatspi(processId);
        }

        foreach (var line in windowIds.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var windowId = line.Trim();
            if (windowId.Length == 0) continue;

            var windowName = RunProcess("xdotool", $"getwindowname {windowId}");
            if (windowName == null) continue;
            windowName = windowName.Trim();
            if (windowName.Length == 0) continue;

            // Try to get buttons via pyatspi
            var buttons = GetButtonsPyatspi(processId, windowName);

            dialogs.Add(new DetectedDialog
            {
                Title = windowName,
                Buttons = buttons,
                NativeHandle = windowId
            });
        }

        // Filter to only windows that have buttons (likely dialogs)
        // If pyatspi isn't available, keep all windows (titles are still useful)
        var hasPyatspi = false;
        foreach (var d in dialogs)
        {
            if (d.Buttons.Count > 0) { hasPyatspi = true; break; }
        }

        if (hasPyatspi)
        {
            dialogs.RemoveAll(d => d.Buttons.Count == 0);
        }

        return dialogs;
    }

    [SupportedOSPlatform("linux")]
    private static List<DetectedDialog> DetectDialogsLinuxPyatspi(int processId)
    {
        // Full pyatspi-based detection for Wayland or when xdotool is unavailable
        var pyScript = $@"
import pyatspi, sys
for app in pyatspi.Registry.getDesktop(0):
    try:
        if app.get_process_id() != {processId}: continue
    except: continue
    for frame in app:
        try:
            role = frame.getRole()
            if role not in (pyatspi.ROLE_DIALOG, pyatspi.ROLE_ALERT):
                continue
            title = frame.name or ''
            btns = []
            for i in range(frame.childCount):
                child = frame.getChildAtIndex(i)
                if child and child.getRole() == pyatspi.ROLE_PUSH_BUTTON:
                    btns.append(child.name or '')
            print(f'DIALOG:{{title}}|{{"","".join(btns)}}')
        except: pass
";

        var result = RunProcess("python3", "-c -", pyScript);
        if (result == null)
            return [];

        var dialogs = new List<DetectedDialog>();
        foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("DIALOG:")) continue;

            var payload = line.Substring(7);
            var pipeIdx = payload.IndexOf('|');
            if (pipeIdx < 0) continue;

            var title = payload.Substring(0, pipeIdx);
            var buttonNames = payload.Substring(pipeIdx + 1).Split(',', StringSplitOptions.RemoveEmptyEntries);

            var buttons = new List<DetectedButton>();
            foreach (var name in buttonNames)
            {
                var trimmed = name.Trim();
                if (trimmed.Length > 0)
                {
                    buttons.Add(new DetectedButton
                    {
                        Text = trimmed,
                        NativeHandle = trimmed
                    });
                }
            }

            dialogs.Add(new DetectedDialog
            {
                Title = title,
                Buttons = buttons,
                NativeHandle = title
            });
        }

        return dialogs;
    }

    [SupportedOSPlatform("linux")]
    private static List<DetectedButton> GetButtonsPyatspi(int processId, string windowName)
    {
        var escapedName = windowName.Replace("'", "\\'");
        var pyScript = $@"
import pyatspi, sys
for app in pyatspi.Registry.getDesktop(0):
    try:
        if app.get_process_id() != {processId}: continue
    except: continue
    for frame in app:
        try:
            if frame.name == '{escapedName}':
                for i in range(frame.childCount):
                    child = frame.getChildAtIndex(i)
                    if child and child.getRole() == pyatspi.ROLE_PUSH_BUTTON:
                        print(child.name or '')
        except: pass
";

        var result = RunProcess("python3", "-c -", pyScript);
        var buttons = new List<DetectedButton>();
        if (result == null)
            return buttons;

        foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim();
            if (name.Length > 0)
            {
                buttons.Add(new DetectedButton
                {
                    Text = name,
                    NativeHandle = name
                });
            }
        }

        return buttons;
    }

    [SupportedOSPlatform("linux")]
    private static bool ClickButtonLinux(DetectedDialog dialog, string buttonText)
    {
        var escapedWindow = dialog.Title.Replace("'", "\\'");
        var escapedButton = buttonText.Replace("'", "\\'");

        var pyScript = $@"
import pyatspi, sys
for app in pyatspi.Registry.getDesktop(0):
    for frame in app:
        try:
            if frame.name == '{escapedWindow}':
                for i in range(frame.childCount):
                    child = frame.getChildAtIndex(i)
                    if child and child.getRole() == pyatspi.ROLE_PUSH_BUTTON:
                        if child.name and child.name.lower() == '{escapedButton.ToLowerInvariant()}':
                            action = child.queryAction()
                            action.doAction(0)
                            print('OK')
                            sys.exit(0)
        except: pass
print('NOTFOUND')
";

        var result = RunProcess("python3", "-c -", pyScript);
        return result != null && result.Trim().StartsWith("OK");
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Run a process and return stdout, or null on failure.
    /// When stdinContent is provided and args ends with "-", pipes content to stdin.
    /// Kills the process after 5 seconds to avoid hanging on permission prompts (e.g. macOS Accessibility).
    /// </summary>
    private static string? RunProcess(string fileName, string arguments, string? stdinContent = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (stdinContent != null)
            {
                startInfo.RedirectStandardInput = true;
                // For osascript, pass script via stdin instead of args (avoids shell quoting issues)
                if (fileName == "osascript")
                {
                    // No arguments — script comes from stdin
                }
                else if (arguments.EndsWith("-"))
                {
                    startInfo.Arguments = arguments.Substring(0, arguments.Length - 1).TrimEnd();
                }
                else
                {
                    startInfo.Arguments = arguments;
                }
            }
            else
            {
                startInfo.Arguments = arguments;
            }

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            if (stdinContent != null)
            {
                process.StandardInput.Write(stdinContent);
                process.StandardInput.Close();
            }

            // Use async read + WaitForExit with timeout to avoid blocking forever
            // when the child process hangs (e.g. macOS Accessibility permission prompt)
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            // Process exited within timeout — get the output
            outputTask.Wait(1_000);
            var output = outputTask.IsCompleted ? outputTask.Result : null;

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Win32 P/Invoke ─────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static class Win32
    {
        public const uint BM_CLICK = 0x00F5;
        public const uint WM_COMMAND = 0x0111;
        public const uint PBM_GETPOS = 0x0408;
        public const uint PBM_GETRANGE = 0x0407;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetDlgCtrlID(IntPtr hWnd);
    }
}
