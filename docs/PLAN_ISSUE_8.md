# Implementation Plan for Issue #8: Improve Onboarding and Setup

**Issue:** https://github.com/DirtybitGames/unityctl/issues/8
**Author:** Claude
**Date:** 2026-01-11
**Status:** Planning Complete - Ready for Implementation

## Overview
This issue requests three improvements to streamline the unityctl developer experience:
1. **CLI installation commands** for Unity package, skill, and project configuration
2. **Update functionality** for CLI and bridge
3. **Consolidated package distribution** (single NuGet package)

---

## Part 1: CLI Installation Commands

### 1.1 Install Unity Package Command
**Command:** `unityctl install package [--method <upm|local>]`

**Implementation:**
- **Location:** Create `UnityCtl.Cli/InstallCommands.cs`
- **Functionality:**
  - Detect Unity project root (use existing `ProjectLocator.FindProjectRoot()`)
  - Parse/modify `Packages/manifest.json`
  - **UPM method (default):** Add git dependency `"com.dirtybit.unityctl": "https://github.com/DirtybitGames/unityctl.git?path=UnityCtl.UnityPackage#v{version}"`
  - **Local method:** Add file dependency `"com.dirtybit.unityctl": "file:../path/to/UnityCtl.UnityPackage"`
  - Validate JSON integrity after modification
  - Handle existing dependency (warn if already installed, option to update)

**Technical Details:**
- Use `System.Text.Json` for JSON manipulation (already available in .NET 10)
- Calculate relative path for local installs
- Get current version from `Directory.Build.props` or assembly version

**User Experience:**
```bash
$ cd unity-project
$ unityctl install package
Installing Unity package com.dirtybit.unityctl...
✓ Added to Packages/manifest.json
Unity will automatically import the package on next domain reload.

$ unityctl install package --method local
Installing Unity package (local development mode)...
✓ Added to Packages/manifest.json (file:../../UnityCtl.UnityPackage)
```

---

### 1.2 Install Skill Command
**Command:** `unityctl install skill [--claude-dir <path>]`

**Implementation:**
- **Location:** Add to `UnityCtl.Cli/InstallCommands.cs`
- **Functionality:**
  - Find skill source: `examples/unity-editor/SKILL.md` (embedded as resource or from repo)
  - Detect `.claude/skills/` directory (default: current project or user specifies)
  - Copy `SKILL.md` to `.claude/skills/unity-editor.md`
  - Create `.claude/skills/` if it doesn't exist
  - Handle existing skill file (warn and ask to overwrite)

**Technical Details:**
- Embed `SKILL.md` as embedded resource in CLI assembly during build
- Fallback to reading from repository if in dev environment
- Cross-platform path handling

**User Experience:**
```bash
$ unityctl install skill
Installing Claude Code skill...
✓ Created .claude/skills/unity-editor.md
Skill installed successfully. Restart Claude Code to load the skill.

$ unityctl install skill --claude-dir ~/.claude
Installing Claude Code skill to ~/.claude/skills/...
✓ Created ~/.claude/skills/unity-editor.md
```

---

### 1.3 Configure Project Path Command
**Command:** `unityctl config set-project <path>` or `unityctl init [--project <path>]`

**Implementation:**
- **Location:** Create `UnityCtl.Cli/ConfigCommands.cs`
- **Functionality:**
  - Create/update `.unityctl/config.json` in repository root (for monorepos)
  - Validate that path points to a valid Unity project (check `ProjectSettings/ProjectVersion.txt`)
  - Make path relative to config location for portability
  - Support absolute paths for non-monorepo scenarios

**Technical Details:**
- Use existing `ProjectLocator` logic but in reverse (write instead of read)
- Store relative paths when possible for repository portability
- Create `.unityctl/` directory if it doesn't exist

**User Experience:**
```bash
$ unityctl init
Initializing unityctl for this Unity project...
✓ Created .unityctl/config.json
Project configured successfully.

$ cd monorepo-root
$ unityctl config set-project unity-project
✓ Created .unityctl/config.json
✓ Project path set to: unity-project
```

---

### 1.4 Combined Setup Command (Bonus)
**Command:** `unityctl setup [--method <upm|local>] [--install-skill]`

**Implementation:**
- Runs all three commands in sequence:
  1. `unityctl init`
  2. `unityctl install package`
  3. `unityctl install skill` (if --install-skill flag set)
  4. `unityctl bridge start`

**User Experience:**
```bash
$ unityctl setup --install-skill
Setting up unityctl for this project...
✓ Configured project (.unityctl/config.json)
✓ Installed Unity package (Packages/manifest.json)
✓ Installed Claude Code skill (.claude/skills/unity-editor.md)
✓ Started bridge daemon

Setup complete! Open Unity Editor to complete the installation.
```

---

## Part 2: Update Functionality

### 2.1 Self-Update Command
**Command:** `unityctl update [--version <version>]`

**Implementation:**
- **Location:** Create `UnityCtl.Cli/UpdateCommands.cs`
- **Functionality:**
  - Check NuGet.org for latest version of `UnityCtl.Cli` and `UnityCtl.Bridge`
  - Compare with current version (use assembly version or `--version` output)
  - If newer version available, run:
    ```bash
    dotnet tool update -g UnityCtl.Cli
    dotnet tool update -g UnityCtl.Bridge
    ```
  - Restart bridge if it's running (detect via `bridge.json`, stop, then start)
  - Update Unity package reference in `Packages/manifest.json` to new version

**Technical Details:**
- Use NuGet API to query latest version: `https://api.nuget.org/v3-flatcontainer/unityctl.cli/index.json`
- Parse JSON response for latest version
- Shell out to `dotnet tool update` commands
- Handle case where user doesn't have permissions (suggest `sudo` on Linux/macOS)

**User Experience:**
```bash
$ unityctl update
Checking for updates...
Current version: 0.3.1
Latest version:  0.4.0

Updating UnityCtl.Cli...
✓ Updated UnityCtl.Cli to 0.4.0

Updating UnityCtl.Bridge...
✓ Updated UnityCtl.Bridge to 0.4.0
✓ Restarted bridge daemon

Updating Unity package reference...
✓ Updated Packages/manifest.json to v0.4.0

Update complete!
```

---

### 2.2 Check Update Command
**Command:** `unityctl update --check-only`

**Implementation:**
- Same as above but only check and report, don't update
- Useful for CI/CD pipelines or checking before updating

**User Experience:**
```bash
$ unityctl update --check-only
Current version: 0.3.1
Latest version:  0.4.0
Update available. Run 'unityctl update' to install.
```

---

## Part 3: Consolidated Package Distribution

### 3.1 Analysis: Current Dual-Package Approach

**Current State:**
- `UnityCtl.Cli` - Global tool for CLI commands
- `UnityCtl.Bridge` - Global tool for bridge daemon

**Why Two Packages:**
1. **Modularity:** Users can update CLI or bridge independently
2. **Tool Naming:** Each global tool needs unique command name (`unityctl` vs `unityctl-bridge`)
3. **Process Isolation:** Bridge runs as separate daemon, CLI is short-lived

---

### 3.2 Consolidated Package Options

#### Option A: Single NuGet Package with Two Tool Commands
**Approach:** Merge both projects into single NuGet package that registers two tools

**Implementation:**
- Create `UnityCtl.Tools.csproj` (multi-tool package)
- Package contains both CLI and Bridge executables
- Use `<PackageReference>` to include both as tools

**Challenges:**
- NuGet global tools officially support one entry point per package
- Would require workaround or custom MSBuild logic

**Verdict:** **Not officially supported by .NET tooling**. Would require hacks.

---

#### Option B: CLI Launches Bridge Inline (Alternative Architecture)
**Approach:** Eliminate `unityctl-bridge` command, make CLI launch bridge as subprocess

**Implementation:**
- Move bridge code into CLI project
- When `unityctl bridge start` runs, CLI spawns bridge in background from its own assembly
- Bridge becomes embedded library, not separate tool

**Pros:**
- Single install: `dotnet tool install -g UnityCtl`
- Simpler for users
- Single version to track

**Cons:**
- Larger CLI binary (includes ASP.NET Core)
- Bridge can't be updated independently
- Increases CLI startup time (loads ASP.NET dependencies even for simple commands)

**Verdict:** **Feasible but worse architecture**. Violates separation of concerns.

---

#### Option C: Meta-Package with Dependencies
**Approach:** Create `UnityCtl` meta-package that depends on both tools

**Implementation:**
- Create `UnityCtl.csproj` (meta-package, not a tool itself)
- Add package dependencies to both CLI and Bridge

**Challenges:**
- Global tools don't support package dependencies like libraries
- Meta-package approach doesn't work for tools

**Verdict:** **Not supported for global tools**.

---

#### Option D: Installation Script (Pragmatic Solution - RECOMMENDED)
**Approach:** Provide installation script that installs both tools

**Implementation:**
- Create `install.sh` and `install.ps1` scripts
- Host on GitHub repository and website
- One-liner install:
  ```bash
  # Linux/macOS
  curl -sSL https://raw.githubusercontent.com/DirtybitGames/unityctl/main/install.sh | bash

  # Windows
  iwr https://raw.githubusercontent.com/DirtybitGames/unityctl/main/install.ps1 | iex
  ```

**Script Logic:**
```bash
#!/bin/bash
echo "Installing UnityCtl..."
dotnet tool install -g UnityCtl.Cli || dotnet tool update -g UnityCtl.Cli
dotnet tool install -g UnityCtl.Bridge || dotnet tool update -g UnityCtl.Bridge
echo "✓ UnityCtl installed successfully!"
echo "Run 'unityctl --help' to get started."
```

**Pros:**
- Simple user experience (one command)
- Keeps tool separation (good architecture)
- Can add validation, platform detection, etc.
- Versioning handled properly

**Cons:**
- Requires network access to download script
- Security concern (running remote scripts)
- Not a "real" NuGet package consolidation

**Verdict:** **Best pragmatic solution**. Maintains good architecture while improving UX.

---

### 3.3 Recommended Approach: Installation Script + Update Command

**Combine:**
1. **Installation Script** (Option D) for initial setup
2. **Update Command** (Part 2) for updates

**Benefits:**
- One-command install for new users
- One-command update for existing users
- Maintains current architecture
- NuGet packages stay separate and focused
- Can be extended with more setup logic later

**Implementation Plan:**
1. Create `scripts/install.sh` and `scripts/install.ps1`
2. Add one-liner instructions to README.md
3. Implement `unityctl update` command (Part 2)
4. Update documentation to promote installation script

---

## Implementation Sequence

### Phase 1: Core Installation Commands (High Priority)
1. **PR #1:** Implement `unityctl install package` command
   - Files: `UnityCtl.Cli/InstallCommands.cs`
   - Tests: Verify manifest.json modification

2. **PR #2:** Implement `unityctl install skill` command
   - Embed SKILL.md as resource
   - Files: `UnityCtl.Cli/InstallCommands.cs` (extend)

3. **PR #3:** Implement `unityctl config set-project` / `unityctl init` command
   - Files: `UnityCtl.Cli/ConfigCommands.cs`

4. **PR #4:** Implement `unityctl setup` combined command
   - Files: `UnityCtl.Cli/InstallCommands.cs` (extend)

### Phase 2: Update Functionality (Medium Priority)
5. **PR #5:** Implement `unityctl update` command
   - Files: `UnityCtl.Cli/UpdateCommands.cs`
   - NuGet API integration
   - Auto-restart bridge logic

### Phase 3: Installation Script (Low Priority)
6. **PR #6:** Create installation scripts
   - Files: `scripts/install.sh`, `scripts/install.ps1`
   - Update README.md with one-liner install
   - Add to GitHub releases

### Phase 4: Documentation (All Phases)
7. Update README.md with new commands
8. Update CONTRIBUTING.md for developers
9. Create migration guide for existing users

---

## Technical Considerations

### Error Handling
- **File not found:** Clear error messages with suggested actions
- **Permission denied:** Detect and suggest `sudo` or running as admin
- **Invalid Unity project:** Detect missing `ProjectSettings/` and explain
- **Network failures:** Graceful degradation for update checks

### Cross-Platform Support
- Test on Windows, macOS, Linux
- Use `Path.Combine()` and `Path.GetFullPath()` for all paths
- Handle Windows/Unix line endings in manifest.json
- PowerShell script for Windows, Bash for Unix

### Backwards Compatibility
- New commands don't break existing workflows
- Update command handles version downgrades gracefully
- Config file format is forward-compatible (extra fields ignored)

### Testing Strategy
- **Unit tests:** JSON parsing, path manipulation, version comparison
- **Integration tests:** E2E tests in test Unity project
- **Manual testing:** Each OS platform before release

---

## Expected User Impact

### Before (Current State)
```bash
# First-time setup (manual, error-prone)
dotnet tool install -g UnityCtl.Cli
dotnet tool install -g UnityCtl.Bridge
# Edit Packages/manifest.json manually
# Copy SKILL.md manually
cd unity-project
unityctl bridge start
```

### After (Improved)
```bash
# One-liner install
curl -sSL https://get-unityctl.sh | bash

# One-command setup per project
cd unity-project
unityctl setup --install-skill

# One-command updates
unityctl update
```

**Time Saved:** ~5-10 minutes per project setup, eliminates manual file editing errors.

---

## Open Questions

1. **Skill location:** Should skill be global (`~/.claude/skills/`) or per-project (`.claude/skills/`)?
   - **Recommendation:** Support both via `--global` flag

2. **Unity package versioning:** Lock to specific version tag or use latest?
   - **Recommendation:** Lock to CLI version for stability, `--latest` flag for bleeding edge

3. **Auto-update:** Should `unityctl` auto-check for updates on every run?
   - **Recommendation:** No (respects user control), but add `--check-updates` flag

4. **Bridge restart:** Auto-restart bridge during update or require manual restart?
   - **Recommendation:** Auto-restart with confirmation prompt

5. **Installation script security:** How to handle "curl | bash" security concerns?
   - **Recommendation:** Provide checksum verification, encourage manual download and inspect

---

## Success Criteria

Issue #8 is complete when:
- ✅ Users can run `unityctl install package` to add Unity package
- ✅ Users can run `unityctl install skill` to add Claude skill
- ✅ Users can run `unityctl init` to configure project path
- ✅ Users can run `unityctl setup` for one-command setup
- ✅ Users can run `unityctl update` to update all components
- ✅ Installation script provides one-liner setup experience
- ✅ Documentation updated with new workflows
- ✅ All commands work cross-platform (Windows/Mac/Linux)

---

## Next Steps

1. Review this plan with stakeholders
2. Prioritize which phases to implement first
3. Create GitHub issues for each PR
4. Begin implementation of Phase 1

---

**Plan Status:** Ready for Review and Implementation
