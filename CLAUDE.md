# UnityCtl

CLI tool enabling AI agents to control Unity Editor programmatically (edit scripts, compile, play, capture screenshots, run tests).

## Architecture

4 components: **Protocol** (shared types), **Bridge** (daemon), **Cli**, **UnityPackage** (Unity plugin)

Communication flow: `CLI → Bridge (HTTP) → Unity (WebSocket)`

## Development

```bash
# Build
dotnet build

# Install locally
./dev-install.sh

# Test project
unity-project/
```

## Testing

```bash
dotnet test
```

- Uses xUnit with `BridgeTestFixture` + `FakeUnityClient` for integration tests
- See `UnityCtl.Tests/` for unit and integration test examples

## Key Conventions

- Commands use RPC pattern via bridge (`asset.refresh`, `play.enter`, `scene.load`, etc.)
- Unity APIs run on main thread (queued from WebSocket background thread)
- Protocol/UnityPackage: netstandard2.1 (Unity compatibility)
- CLI/Bridge: .NET 10.0

## Skill

Claude Code skill for AI integration: `.claude/skills/unity-editor/SKILL.md`

## Notes

- Bridge survives Unity domain reloads; Unity plugin auto-reconnects
- Detailed docs: README.md, ARCHITECTURE.md, CONTRIBUTING.md, TROUBLESHOOTING.md
