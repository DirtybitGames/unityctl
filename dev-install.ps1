# UnityCtl - Local Development Install Script (PowerShell)
$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "UnityCtl - Local Development Install" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Kill any running bridge processes
Write-Host "[1/6] Stopping running bridge processes..." -ForegroundColor Yellow
try {
    Stop-Process -Name "unityctl-bridge" -Force -ErrorAction SilentlyContinue
    Write-Host "  Bridge process stopped" -ForegroundColor Gray
} catch {
    Write-Host "  No bridge process running" -ForegroundColor Gray
}
Write-Host ""

# Step 2: Uninstall existing tools
Write-Host "[2/6] Uninstalling existing tools..." -ForegroundColor Yellow
try {
    dotnet tool uninstall -g UnityCtl.Cli 2>$null
    Write-Host "  UnityCtl.Cli uninstalled" -ForegroundColor Gray
} catch {
    Write-Host "  UnityCtl.Cli not installed" -ForegroundColor Gray
}
try {
    dotnet tool uninstall -g UnityCtl.Bridge 2>$null
    Write-Host "  UnityCtl.Bridge uninstalled" -ForegroundColor Gray
} catch {
    Write-Host "  UnityCtl.Bridge not installed" -ForegroundColor Gray
}
Write-Host ""

# Step 3: Clean the solution
Write-Host "[3/6] Cleaning solution..." -ForegroundColor Yellow
dotnet clean --configuration Release
Write-Host ""

# Step 4: Build the solution
Write-Host "[4/7] Building solution..." -ForegroundColor Yellow
dotnet build --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host ""

# Step 5: Publish Protocol DLL to Unity package
Write-Host "[5/7] Publishing Protocol DLL to Unity package..." -ForegroundColor Yellow
dotnet publish UnityCtl.Protocol/UnityCtl.Protocol.csproj --configuration Release --no-build
Write-Host ""

# Step 6: Pack NuGet packages
Write-Host "[6/7] Packing NuGet packages..." -ForegroundColor Yellow
if (Test-Path "./artifacts") {
    Remove-Item -Recurse -Force "./artifacts"
}
New-Item -ItemType Directory -Path "./artifacts" | Out-Null
dotnet pack --configuration Release --output ./artifacts --no-build
Write-Host ""
Write-Host "Packages created:" -ForegroundColor Gray
Get-ChildItem ./artifacts/*.nupkg | Format-Table Name, Length, LastWriteTime
Write-Host ""

# Step 7: Install tools globally
Write-Host "[7/7] Installing tools globally..." -ForegroundColor Yellow
dotnet tool install -g UnityCtl.Cli --add-source ./artifacts
dotnet tool install -g UnityCtl.Bridge --add-source ./artifacts
Write-Host ""

# Verify installation
Write-Host "========================================" -ForegroundColor Green
Write-Host "✅ Installation complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Installed tools:" -ForegroundColor Cyan
dotnet tool list -g | Select-String "unityctl"
Write-Host ""
Write-Host "Testing command availability:" -ForegroundColor Cyan
Write-Host "  unityctl version:" -ForegroundColor Gray
try {
    unityctl --version
} catch {
    Write-Host "  ❌ unityctl not found in PATH" -ForegroundColor Red
}
Write-Host ""
Write-Host "  unityctl-bridge version:" -ForegroundColor Gray
try {
    unityctl-bridge --version
} catch {
    Write-Host "  ❌ unityctl-bridge not found in PATH" -ForegroundColor Red
}
Write-Host ""
Write-Host "Available console commands:" -ForegroundColor Cyan
try {
    unityctl console --help
} catch {
    Write-Host "  ❌ Failed to get console help" -ForegroundColor Red
}
