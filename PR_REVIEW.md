# PR #37 Review: Add plugin system (script + executable)

Good feature overall — the two-tier plugin model (script + executable) with lazy PATH resolution is well-designed. Below are opportunities for simplification, cleanup, and improvement.

---

## 1. Duplicated process execution — extract a shared helper

`ExecutablePluginLoader` has two nearly identical process-launch-and-stream patterns: `TryExecuteByName` (lines 66-167) and `ExecutePluginAsync` (lines 229-286). Both build `ProcessStartInfo`, set environment variables, start the process, stream stdout/stderr concurrently, and return the exit code.

**Suggestion:** Extract a private `RunProcessAsync(ProcessStartInfo startInfo)` method that handles the start → stream → wait → return-exit-code pattern. Both callers would just build the `ProcessStartInfo` and delegate.

---

## 2. Duplicated environment variable setup

The env vars `UNITYCTL_PROJECT_PATH`, `UNITYCTL_BRIDGE_PORT`, `UNITYCTL_BRIDGE_URL` are assembled identically in both `TryExecuteByName` and `ExecutePluginAsync`.

**Suggestion:** Extract a `SetBridgeEnvironment(ProcessStartInfo startInfo, string? projectPath, string? agentId, bool json)` helper used by both paths.

---

## 3. `StreamOutputAsync` can use `Stream.CopyToAsync`

The manual 4KB-buffer loop in `StreamOutputAsync` reimplements what `BaseStream.CopyToAsync` already does:

```csharp
private static Task StreamOutputAsync(StreamReader reader, TextWriter writer)
    => reader.BaseStream.CopyToAsync(writer.BaseStream ?? Stream.Null);
```

(Or keep the TextWriter version if encoding conversion is needed, but `Console.Out`/`Console.Error` just wrap the base stream.)

---

## 4. Duplicated "exclude names" set construction

The pattern of building a `HashSet` from `BuiltInCommandNames` + script plugin names appears in three places: `PluginCommands.CreateListCommand`, `SkillCommands.ComposeSkillContent`, and `Program.cs`.

**Suggestion:** Add a static helper like `PluginCommands.GetExcludeNames(IEnumerable<LoadedPlugin> scriptPlugins)` and call it from all three locations.

---

## 5. Duplicated path-traversal guard

The path-containment check (`Path.GetFullPath` + `StartsWith(resolvedDir + DirectorySeparatorChar)`, with platform-aware comparison) appears in both `PluginLoader.ExecutePluginCommandAsync` (line 138) and `PluginLoader.GenerateSkillSection` (line 269).

**Suggestion:** Extract to a shared utility:
```csharp
internal static bool IsPathWithin(string candidatePath, string directory)
```

---

## 6. `FindDotUnityctlDirectory` partially duplicates `ProjectLocator`

`PluginLoader.FindDotUnityctlDirectory()` walks up from CWD looking for `.unityctl/` — which is conceptually the same traversal `ProjectLocator.FindProjectRoot()` does. If the project root is found, `.unityctl` is always at `projectRoot/.unityctl`.

**Suggestion:** Consider reusing `ProjectLocator` and deriving the `.unityctl` path from the project root, rather than duplicating the directory-walk.

---

## 7. `GetUserPluginsDirectory` null check is dead code

`GetUserPluginsDirectory()` always returns a non-null string (it concatenates `Environment.SpecialFolder.UserProfile` with constant path segments). But `DiscoverPlugins()` line 38 checks `if (userDir != null)`. This null check is unreachable and misleading.

**Suggestion:** Remove the null check, or change the method signature to document it never returns null.

---

## 8. Fragile marker-based stripping in `ComposeSkillContent`

```csharp
var pluginMarker = "\n## Plugin Commands";
var markerIndex = baseContent.IndexOf(pluginMarker, StringComparison.Ordinal);
if (markerIndex >= 0)
    baseContent = baseContent.Substring(0, markerIndex);
```

If `## Plugin Commands` ever appears naturally in the base SKILL.md (e.g., in a "how plugins work" section), this would silently truncate the content.

**Suggestion:** Use a more explicit sentinel like `<!-- BEGIN PLUGIN SECTIONS -->` that won't collide with natural headings.

---

## 9. Model classes should live together

`ExecutablePlugin` is defined at the bottom of `ExecutablePluginLoader.cs` and `LoadedPlugin` at the bottom of `PluginLoader.cs`. `PluginManifest.cs` already exists as a models file.

**Suggestion:** Move `ExecutablePlugin` and `LoadedPlugin` into `PluginManifest.cs` (or rename it to `PluginModels.cs`) to keep all plugin DTOs together.

---

## 10. `Program.cs` — early PATH interception bypasses System.CommandLine

```csharp
if (args.Length > 0 && !registeredNames.Contains(args[0]) && !args[0].StartsWith("-"))
{
    var exitCode = await ExecutablePluginLoader.TryExecuteByName(args[0], args.Skip(1).ToArray());
    if (exitCode.HasValue)
        return exitCode.Value;
}
```

This runs **before** `rootCommand.InvokeAsync`, so it bypasses System.CommandLine middleware (global options like `--json`, `--project`, `--timeout` won't be parsed). A user running `unityctl --json my-path-plugin` would hit a different code path than `unityctl my-dir-plugin --json`.

**Suggestion:** Consider hooking into System.CommandLine's "unmatched token" handling instead, or at minimum parse and strip known global options before the early interception.

---

## 11. No tests

This is ~1,670 lines of new code with no test coverage. The codebase already has `BridgeTestFixture` + `FakeUnityClient` for integration tests, and xUnit for unit tests.

Key areas that would benefit from tests:
- Plugin discovery (both script and executable) with mock file systems
- Path traversal guard
- Name validation regex
- `ComposeSkillContent` composition logic
- Command collision detection

---

## 12. Minor: `PluginCommands.ValidPluginName` regex allows single-char names

The regex `^[a-z0-9]([a-z0-9-]*[a-z0-9])?$` allows single-character names like `"a"` or `"1"`. Is that intentional? Single-char plugin names could easily collide with future built-in commands or flags.

**Suggestion:** Consider requiring a minimum length of 2-3 characters.

---

## Summary

The architecture is sound — script plugins for Unity-side work, executable plugins for external tooling, with clear precedence rules. The main opportunities are:
1. **DRY up process execution** (items 1-2): the biggest win, reducing ~60 lines of duplication
2. **Extract shared utilities** (items 4-6): path traversal guard, exclude-names builder, directory walker
3. **Add tests** (item 11): critical for a feature this size
4. **Fix the early interception issue** (item 10): global options won't work for PATH-resolved plugins
