# UnityCtl CLI Command Naming Proposal

Based on research of 13+ popular developer tools (git, npm, docker, cargo, dotnet, kubectl, gh, rustup, etc.)

## Design Principles

1. **Follow industry patterns** - Use naming conventions users already know
2. **Noun-verb grouping** - Group related commands under nouns (modern pattern)
3. **Consistency** - Similar commands use similar verbs
4. **Discoverability** - Help text and tab completion make commands obvious
5. **Dotnet alignment** - Since unityctl IS a dotnet tool, follow dotnet patterns closely

---

## Core Command Groups

### Initialization & Setup

```bash
# Initialize Unity project for unityctl (creates .unityctl/config.json)
unityctl init [--project <path>] [--yes]

# One-command setup (runs init + install package + install skill + bridge start)
unityctl setup [--method <upm|local>] [--skip-package] [--skip-skill] [--yes]
```

**Pattern Source:** git init, npm init, cargo init, docker init, terraform init
**Behavior:**
- `init` creates `.unityctl/config.json` (for monorepo) or validates Unity project
- `setup` runs full onboarding flow
- Both support `--yes` for non-interactive mode

---

### Package Management

```bash
# Install Unity package to current project
unityctl package add [--method <upm|local>] [--version <version>]

# Remove Unity package from current project
unityctl package remove

# Update Unity package to latest version
unityctl package update [--version <version>]

# Show Unity package status
unityctl package status
```

**Pattern Source:** dotnet add package, npm install, cargo add
**Alternative considered:** `unityctl install package` (also valid, less consistent)
**Recommendation:** Use `package add` to match `dotnet add package` pattern

---

### Skill Management

```bash
# Install Claude Code skill
unityctl skill add [--global] [--claude-dir <path>]

# Remove Claude Code skill
unityctl skill remove [--global]

# Show skill installation status
unityctl skill status
```

**Pattern Source:** rustup component add, dotnet add reference
**Note:** `--global` installs to `~/.claude/skills/` vs local `.claude/skills/`

---

### Configuration

```bash
# Set configuration value
unityctl config set <key> <value>

# Get configuration value
unityctl config get <key>

# List all configuration
unityctl config list

# Common shortcuts (aliases for set)
unityctl config set project-path <path>
unityctl config set bridge-port <port>
```

**Pattern Source:** git config, npm config, kubectl config, gh config
**Keys:**
- `project-path` - Path to Unity project (for monorepo)
- `bridge-port` - Default bridge port (override auto-detect)
- Future: `auto-update`, `telemetry`, etc.

---

### Update Management

```bash
# Update everything (CLI, bridge, Unity package)
unityctl update [--version <version>]

# Check for updates without installing
unityctl update --check

# Update specific components
unityctl update --tools-only      # Just CLI + Bridge
unityctl update --package-only    # Just Unity package
```

**Pattern Source:** rustup update (comprehensive), dotnet tool update
**Behavior:**
1. Check NuGet for latest UnityCtl.Cli and UnityCtl.Bridge
2. Update both tools: `dotnet tool update -g UnityCtl.Cli/Bridge`
3. Restart bridge if running
4. Update Unity package reference in Packages/manifest.json

---

### Existing Command Groups (Keep as-is)

These already follow good patterns:

```bash
unityctl status                   # Project status
unityctl logs [--follow]          # Show bridge logs

unityctl bridge start|stop|status # Bridge lifecycle
unityctl editor run|stop          # Unity Editor control

unityctl scene load|list          # Scene operations
unityctl asset refresh|import     # Asset operations
unityctl play enter|exit          # Play mode control
unityctl test run                 # Test runner
unityctl screenshot capture       # Screenshot tool
unityctl script execute           # Script execution
unityctl menu execute             # Menu execution
```

---

## Full Command Tree

```
unityctl
│
├── init [--project <path>] [--yes]
├── setup [--method <upm|local>] [--skip-*] [--yes]
├── update [--check] [--tools-only] [--package-only] [--version <v>]
│
├── status
├── logs [--follow]
│
├── package
│   ├── add [--method <upm|local>] [--version <v>]
│   ├── remove
│   ├── update [--version <v>]
│   └── status
│
├── skill
│   ├── add [--global] [--claude-dir <path>]
│   ├── remove [--global]
│   └── status
│
├── config
│   ├── set <key> <value>
│   ├── get <key>
│   └── list
│
├── bridge
│   ├── start [--port <port>]
│   ├── stop
│   └── status
│
├── editor
│   ├── run [--project <path>]
│   └── stop
│
├── scene
│   ├── load <scene>
│   ├── list
│   └── (future: create, delete)
│
├── asset
│   ├── refresh
│   ├── import <path>
│   └── (future: export, delete)
│
├── play
│   ├── enter
│   └── exit
│
├── test
│   └── run [--filter <filter>]
│
├── screenshot
│   └── capture [--output <path>]
│
├── script
│   └── execute <script> [--args <args>]
│
└── menu
    └── execute <menu-path>
```

---

## Comparison: Old Plan vs Recommended

### Old Plan (from PLAN_ISSUE_8.md)

```bash
unityctl install package [--method <upm|local>]
unityctl install skill [--claude-dir <path>]
unityctl config set-project <path>
unityctl init [--project <path>]
unityctl setup
unityctl update
```

### Recommended (Industry-aligned)

```bash
unityctl package add [--method <upm|local>]      # Matches "dotnet add package"
unityctl skill add [--global]                    # Matches "rustup component add"
unityctl config set project-path <path>          # Matches "git config set"
unityctl config list                             # Standard config pattern
unityctl init [--yes]                            # Standard init pattern
unityctl setup [--yes]                           # Keep as convenience command
unityctl update [--check]                        # Standard update pattern
```

### Key Changes

1. **`install package` → `package add`**
   - Clearer grouping under `package` noun
   - Matches dotnet: `dotnet add package`
   - Leaves room for `package remove`, `package update`

2. **`install skill` → `skill add`**
   - Consistent with `package add`
   - Matches component managers: `rustup component add`

3. **`config set-project` → `config set project-path`**
   - Standard key-value pattern
   - Matches git/npm/kubectl config
   - Enables `config get project-path`, `config list`

4. **Add `--yes` flags**
   - Non-interactive mode for CI/CD
   - Matches npm, docker, terraform

---

## Alternative: Hybrid Approach

If we want to support both patterns (ease of use + industry alignment):

```bash
# Primary commands (recommended in docs)
unityctl package add
unityctl skill add
unityctl config set <key> <value>

# Aliases for convenience
unityctl install package    # Alias for: package add
unityctl install skill      # Alias for: skill add

# This gives users flexibility while encouraging the better pattern
```

**Implementation:** System.CommandLine supports multiple names per command:
```csharp
var packageAddCommand = new Command("add", "Add Unity package to project");
packageAddCommand.AddAlias("install");  // Allow both

var packageGroup = new Command("package", "Unity package management");
packageGroup.AddCommand(packageAddCommand);

// Supports both:
// unityctl package add
// unityctl package install
```

---

## Help Text Examples

### Top-level help

```
$ unityctl --help

UnityCtl v0.4.0 - Control Unity Editor from the command line

Usage:
  unityctl [command] [options]

Commands:
  init          Initialize Unity project for unityctl
  setup         One-command setup (init + package + skill + bridge)
  update        Update UnityCtl CLI, bridge, and Unity package

  package       Unity package management (add, remove, update)
  skill         Claude Code skill management (add, remove)
  config        Configuration management (set, get, list)

  bridge        Bridge daemon management (start, stop, status)
  editor        Unity Editor control (run, stop)

  scene         Scene operations (load, list)
  asset         Asset operations (refresh, import)
  play          Play mode control (enter, exit)
  test          Test runner (run)
  screenshot    Screenshot capture (capture)
  script        Script execution (execute)
  menu          Menu execution (execute)

  status        Show project and bridge status
  logs          Show bridge logs

Global Options:
  --project <path>    Unity project path (auto-detected if not specified)
  --agent-id <id>     Agent ID for multi-agent scenarios
  --json              Output JSON instead of human-readable text
  --help              Show help and usage information
  --version           Show version information

Getting Started:
  unityctl setup              # First-time setup (interactive)
  unityctl setup --yes        # First-time setup (non-interactive)
  unityctl bridge start       # Start bridge daemon
  unityctl status             # Check status

For more information, visit:
  https://github.com/DirtybitGames/unityctl
```

### Group help

```
$ unityctl package --help

Unity package management

Usage:
  unityctl package [command] [options]

Commands:
  add       Add Unity package to current project
  remove    Remove Unity package from current project
  update    Update Unity package to latest version
  status    Show Unity package installation status

Examples:
  unityctl package add                    # Add via UPM (git URL)
  unityctl package add --method local     # Add via local file path
  unityctl package add --version v0.3.0   # Add specific version
  unityctl package update                 # Update to latest
  unityctl package status                 # Check if installed
```

---

## Implementation Checklist

- [ ] Refactor commands to use noun-verb pattern
- [ ] Add `package` command group with `add/remove/update/status`
- [ ] Add `skill` command group with `add/remove/status`
- [ ] Add `config` command group with `set/get/list`
- [ ] Add `--yes` flag to `init` and `setup`
- [ ] Add component flags to `update` (--tools-only, --package-only)
- [ ] Implement command aliases for backwards compatibility
- [ ] Write comprehensive help text for all commands
- [ ] Add examples to help text
- [ ] Update README.md with new command structure
- [ ] Update SKILL.md with new command examples
- [ ] Test tab completion (if supported)

---

## Migration Path for Existing Users

Since this is a young project (v0.3.1), breaking changes are acceptable. But we should:

1. **Document changes clearly** in CHANGELOG
2. **Support aliases** for old commands (at least temporarily)
3. **Update all examples** in docs and skill file
4. **Announce in release notes** with migration guide

Example migration:
```bash
# Old (if we had implemented the original plan)
unityctl install package

# New (recommended)
unityctl package add

# Both work (via alias) during transition period
```

---

## Recommendation

**Use the recommended structure** (`package add`, `skill add`, `config set/get/list`) because:

1. ✅ Matches industry leaders (dotnet, cargo, kubectl, rustup)
2. ✅ More consistent and discoverable
3. ✅ Allows natural extensions (package update, skill remove, config list)
4. ✅ Follows modern noun-verb grouping pattern
5. ✅ Better tab completion and help text organization
6. ✅ We're still pre-1.0, so breaking changes are acceptable

The old plan was good, but this refinement makes it great.
