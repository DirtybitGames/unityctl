# UnityCtl

CLI tool enabling AI agents to control Unity Editor programmatically (edit scripts, compile, play, capture screenshots, run tests).

## Architecture

4 components: **Protocol** (shared types), **Bridge** (daemon), **Cli**, **UnityPackage** (Unity plugin)

Communication flow: `CLI → Bridge (HTTP) → Unity (WebSocket)`

## Development

Use `./uc` to run CLI commands during development (no global install needed, avoids conflicts between checkouts):

```bash
./uc bridge start
./uc script eval "Debug.Log(42)"
./uc play enter
```

Build all projects: `dotnet build`

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

The Claude Code skill has two layers:

- **Base skill**: `UnityCtl.Cli/Resources/SKILL.md` — the source of truth, embedded into the CLI assembly. Edit this file when adding or changing CLI commands.
- **Composed skill**: `.claude/skills/unity-editor/SKILL.md` — generated output (base + plugin docs + user extra). This is what Claude Code loads. Committed so it works on clone.

After editing the base skill or changing plugins, regenerate the composed output:

```bash
./uc skill add --force    # or: ./uc skill rebuild
```

**Do not edit `.claude/skills/unity-editor/SKILL.md` directly** — it will be overwritten by the next rebuild.

## Notes

- Bridge survives Unity domain reloads; Unity plugin auto-reconnects
- Detailed docs: README.md, ARCHITECTURE.md, CONTRIBUTING.md, TROUBLESHOOTING.md
