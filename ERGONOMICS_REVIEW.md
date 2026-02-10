# Ergonomics Review: unityctl for AI Agents

Review of unityctl CLI from the perspective of an AI agent (e.g., Claude Code) trying to use the tool to control Unity Editor programmatically.

## What Works Well

1. **`--json` global flag** - Every command supports structured output for machine parsing
2. **`script eval` with auto-usings** - Quick C# expression evaluation without boilerplate (System, UnityEngine, UnityEditor included by default)
3. **Actionable error messages** - Errors suggest the corrective command (e.g., "Run 'unityctl bridge start'")
4. **`unityctl status`** - Single command shows Unity, bridge, and connection state
5. **`unityctl wait`** - Blocks until Unity connects; essential for sequencing
6. **`asset refresh` returns compilation errors** - Agent doesn't need separate build log
7. **Proper exit codes** - Non-zero on failure; agents can check `$?`
8. **SKILL.md** - Good Claude Code integration with command reference and troubleshooting

## Issues and Improvement Opportunities

### 1. `asset refresh` is the wrong mental model for "compile"

**Severity: High** | `UnityCtl.Cli/AssetCommands.cs:55`

An AI agent editing C# scripts thinks "compile" or "build." The command `asset refresh` maps to Unity's `AssetDatabase.Refresh()`, which is opaque outside Unity. The description "Refresh all assets (like focusing the editor)" is meaningless to an agent.

**Suggestion**: Add a top-level `unityctl compile` alias (or `build`) that maps to the same RPC. Keep `asset refresh` for Unity users but give agents the intuitive entry point. Description: "Compile C# scripts and refresh assets. Returns compilation errors on failure."

### 2. No log-level filtering

**Severity: High** | `UnityCtl.Cli/LogsCommand.cs`

After compile or play, an agent's #1 question is "were there errors?" Currently must pull all logs and filter client-side. No `--level error` or `--errors-only` flag exists despite the bridge already having `level` on each entry.

**Suggestion**: Add `--level <log|warning|error>` filter. Consider `--since <timestamp>` for logs from after a specific action.

### 3. `play enter`/`play exit` naming

**Severity: Medium** | `UnityCtl.Cli/PlayCommands.cs:17-24`

"Enter"/"exit" are Unity's internal terms. Standard CLI tools use `start`/`stop` (docker, systemctl, pm2). An agent trained on general CLI patterns will try `play start`/`play stop`.

**Suggestion**: Add `start`/`stop` as aliases for `enter`/`exit`.

### 4. No `play toggle`

**Severity: Medium** | `UnityCtl.Cli/PlayCommands.cs`

Agent often doesn't know current play state and just wants to flip it. Currently requires `play status` then `play enter` or `play exit` (two commands).

**Suggestion**: Add `play toggle` subcommand.

### 5. `editor run` doesn't auto-start the bridge

**Severity: Medium** | `UnityCtl.Cli/EditorCommands.cs:54`

Standard bootstrap is 3 steps: `bridge start` → `editor run` → `wait`. Agent frequently forgets bridge step. Since `bridge start` is idempotent, `editor run` could auto-ensure bridge is running.

**Suggestion**: Auto-start bridge from `editor run`, or add `unityctl start` compound command (bridge + editor + wait).

### 6. Inconsistent `status` command placement

**Severity: Medium**

- `unityctl status` → top-level
- `unityctl play status` → subcommand
- `unityctl bridge status` → subcommand

Agent must memorize different patterns.

### 7. No active scene query

**Severity: Medium** | `UnityCtl.Cli/SceneCommands.cs`

Has `scene list` and `scene load` but no `scene active`/`scene current`. Agent must use roundabout `script eval -u UnityEngine.SceneManagement "SceneManager.GetActiveScene().name"`.

**Suggestion**: Add `scene active` subcommand.

### 8. `script eval` semicolon heuristic is fragile

**Severity: Medium** | `UnityCtl.Cli/ScriptCommands.cs:227`

Uses `expression.Contains(';')` to choose between expression/body mode. Breaks for string literals with semicolons: `script eval "Debug.Log(\"hello;world\")"` → interpreted as body mode, missing return.

**Suggestion**: More robust heuristic, or explicit `--body` flag.

### 9. `asset refresh` success message lacks detail

**Severity: Low** | `UnityCtl.Cli/AssetCommands.cs:85`

Success outputs only "Asset refresh completed". The payload has `compilationTriggered` and `hasCompilationErrors` that aren't surfaced.

**Suggestion**: "Asset refresh completed (compilation triggered, no errors)" or "Asset refresh completed (no compilation needed)".

### 10. Missing `--quiet`/`-q` mode

**Severity: Low** | Global

Commands output informational text on success. For agents chaining commands, this adds noise.

### 11. `test run` doesn't confirm which mode is running

**Severity: Low** | `UnityCtl.Cli/TestCommands.cs:61`

Outputs "Running tests..." without indicating editmode/playmode.

### 12. Screenshot JSON should include absolute path

**Severity: Low** | `UnityCtl.Cli/ScreenshotCommands.cs:66-78`

JSON output has path relative to Unity project. Agent working from different directory must compute absolute path.

### 13. No structured error codes

**Severity: Low** | `UnityCtl.Protocol/Messages.cs`

Error responses have message strings but no well-defined codes. Agent can't programmatically distinguish "compilation failed" from "Unity not connected" without text parsing.

### 14. `script execute` boilerplate gap

**Severity: Low** | `UnityCtl.Cli/ScriptCommands.cs:32-33`

Gap between "one-liner eval" and "full class execute" is large. Consider auto-wrapping multi-statement eval in `Main()`.

## Priority Ordering

1. Add `compile`/`build` alias for `asset refresh`
2. Add `--level` filter to `logs`
3. Add `play start`/`stop` aliases
4. Auto-start bridge from `editor run` or add `unityctl start` compound command
5. Add `play toggle`
6. Add `scene active`
7. Richer `asset refresh` output
8. Fix semicolon heuristic
9. Other (quiet mode, structured error codes, test mode display, etc.)
