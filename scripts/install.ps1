# UnityCtl Installation Script for Windows
# Usage: iwr https://raw.githubusercontent.com/DirtybitGames/unityctl/main/scripts/install.ps1 | iex
# Or:    Invoke-WebRequest -Uri https://raw.githubusercontent.com/DirtybitGames/unityctl/main/scripts/install.ps1 -OutFile install.ps1; .\install.ps1

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "         UnityCtl Installation Script" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host

# Check for dotnet
try {
    $dotnetVersion = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet not found"
    }
    Write-Host "Found .NET SDK version: $dotnetVersion"
    Write-Host
} catch {
    Write-Host "Error: .NET SDK is not installed." -ForegroundColor Red
    Write-Host "Please install .NET SDK 10.0 or later from https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    exit 1
}

# Get list of installed tools
$installedTools = & dotnet tool list -g 2>&1

# Install or update CLI
Write-Host "Installing UnityCtl.Cli..."
if ($installedTools -match "unityctl\.cli") {
    Write-Host "  Updating existing installation..." -ForegroundColor Gray
    & dotnet tool update -g UnityCtl.Cli
} else {
    Write-Host "  Installing..." -ForegroundColor Gray
    & dotnet tool install -g UnityCtl.Cli
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "Warning: CLI installation may have issues" -ForegroundColor Yellow
}

# Install or update Bridge
Write-Host "Installing UnityCtl.Bridge..."
if ($installedTools -match "unityctl\.bridge") {
    Write-Host "  Updating existing installation..." -ForegroundColor Gray
    & dotnet tool update -g UnityCtl.Bridge
} else {
    Write-Host "  Installing..." -ForegroundColor Gray
    & dotnet tool install -g UnityCtl.Bridge
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "Warning: Bridge installation may have issues" -ForegroundColor Yellow
}

Write-Host
Write-Host "============================================" -ForegroundColor Green
Write-Host "         Installation Complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host
Write-Host "UnityCtl has been installed successfully."
Write-Host
Write-Host "Getting Started:" -ForegroundColor Yellow
Write-Host "  1. Navigate to your Unity project directory"
Write-Host "  2. Run: unityctl setup"
Write-Host "  3. Open your Unity project in Unity Editor"
Write-Host
Write-Host "For more information:" -ForegroundColor Yellow
Write-Host "  unityctl --help"
Write-Host "  https://github.com/DirtybitGames/unityctl"
Write-Host
