# Contributing to UnityCtl

Thank you for your interest in contributing to UnityCtl! This guide will help you set up your development environment and understand the project structure.

## Development Installation

### Quick Install (Recommended)

For local development, use the provided scripts to build and install all components:

**Bash/Git Bash:**
```bash
./dev-install.sh
```

**PowerShell:**
```powershell
.\dev-install.ps1
```

These scripts will:
1. Stop any running bridge processes
2. Uninstall existing global tools
3. Clean and build the solution
4. Publish Protocol DLL to Unity package
5. Pack all NuGet packages to `./artifacts`
6. Install the tools globally from artifacts
7. Verify the installation

### Manual Installation

From the repository root:

```bash
# Pack all tool projects to ./artifacts
dotnet pack

# Install as global dotnet tools
dotnet tool install -g UnityCtl.Cli --add-source ./artifacts
dotnet tool install -g UnityCtl.Bridge --add-source ./artifacts
```

### Run Without Installing

You can run the tools directly from source without installing:

```bash
# Run bridge locally
dotnet run --project UnityCtl.Bridge/UnityCtl.Bridge.csproj -- --project ./unity-project

# Run CLI locally
dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --help
```

### Unity Package Development

For local Unity package development, add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.dirtybit.unityctl": "file:../path/to/UnityCtl.UnityPackage"
  }
}
```

## Building

### Build All Projects

```bash
# Build entire solution
dotnet build

# Build in Release mode
dotnet build -c Release
```

### Publish Protocol DLL

The Protocol DLL needs to be published with dependencies for Unity:

```bash
dotnet publish UnityCtl.Protocol/UnityCtl.Protocol.csproj -c Release
```

This automatically copies `UnityCtl.Protocol.dll` to `UnityCtl.UnityPackage/Plugins/` where Unity can load it.

### Pack NuGet Packages

```bash
# Pack all projects
dotnet pack

# Packages will be created in ./artifacts
```

## Uninstalling

To uninstall the global tools:

```bash
dotnet tool uninstall -g UnityCtl.Cli
dotnet tool uninstall -g UnityCtl.Bridge
```

## Project Structure

```
unityctl/
├── UnityCtl.Protocol/        # Shared protocol library (netstandard2.1)
│   ├── Messages/             # Request/Response/Event types
│   ├── Config/               # Configuration models
│   └── Utils/                # Helper utilities
│
├── UnityCtl.Bridge/          # Bridge daemon (net10.0)
│   ├── Server/               # HTTP and WebSocket servers
│   ├── LogBuffer/            # Console log buffering
│   └── Program.cs            # Entry point
│
├── UnityCtl.Cli/             # CLI tool (net10.0)
│   ├── Commands/             # Command implementations
│   ├── BridgeClient.cs       # HTTP client for bridge
│   └── Program.cs            # Entry point
│
├── UnityCtl.UnityPackage/    # Unity UPM package
│   ├── package.json          # UPM package manifest
│   ├── Editor/
│   │   ├── UnityCtl.asmdef   # Assembly definition
│   │   ├── UnityCtlBootstrap.cs  # Auto-initialization
│   │   └── UnityCtlClient.cs     # WebSocket client & handlers
│   └── Plugins/
│       └── UnityCtl.Protocol.dll # Shared protocol library
│
└── unity-project/            # Test Unity project
    ├── Assets/
    │   ├── Scripts/          # Test scripts
    │   ├── Scenes/           # Test scenes
    │   └── Tests/            # Unity tests
    └── Packages/
        └── manifest.json     # Includes local UnityCtl package
```

## Testing the Integration

1. **Start the bridge:**
   ```bash
   dotnet run --project UnityCtl.Bridge/UnityCtl.Bridge.csproj -- --project ./unity-project
   ```

2. **Open Unity Editor:**
   - Open `unity-project/` in Unity Editor
   - Check console for `[UnityCtl] Connected` messages

3. **Run CLI commands:**
   ```bash
   dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --project ./unity-project bridge status
   dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --project ./unity-project play enter
   dotnet run --project UnityCtl.Cli/UnityCtl.Cli.csproj -- --project ./unity-project scene list
   ```

## Development Workflow

### Making Changes

1. **Modify code** in CLI, Bridge, or Protocol projects
2. **Rebuild** the project:
   ```bash
   dotnet build
   ```
3. **For Protocol changes**, republish the DLL:
   ```bash
   dotnet publish UnityCtl.Protocol/UnityCtl.Protocol.csproj -c Release
   ```
4. **Reinstall tools** (if testing global install):
   ```bash
   ./dev-install.sh  # or dev-install.ps1
   ```

### Testing Unity Plugin Changes

1. **Modify** `UnityCtl.UnityPackage/Editor/*.cs` files
2. **Reload** Unity Editor domain:
   - Wait for auto-compilation, or
   - Use `unityctl compile scripts`
3. **Plugin automatically reconnects** after domain reload

## Code Style

- Follow C# conventions
- Use clear, descriptive names
- Add XML documentation comments for public APIs
- Keep methods focused and small

## Submitting Changes

1. **Fork** the repository
2. **Create a branch** for your feature/fix
3. **Make your changes** with clear commit messages
4. **Test thoroughly** using the test Unity project
5. **Submit a pull request** with a description of your changes

## Questions?

If you have questions or need help, please open an issue on GitHub.
