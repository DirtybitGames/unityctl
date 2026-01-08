# UnityCtl - Local Development Install Script (PowerShell)
$ErrorActionPreference = "Stop"

Write-Host "UnityCtl - Local Development Install" -ForegroundColor Cyan
Write-Host ""

# Step 1: Kill any running bridge processes
Write-Host "[1/7] Stopping bridge processes... " -NoNewline
$stopped = Stop-Process -Name "unityctl-bridge" -Force -ErrorAction SilentlyContinue -PassThru
if ($stopped) { Write-Host "stopped" } else { Write-Host "not running" }

# Step 2: Uninstall existing tools
Write-Host "[2/7] Uninstalling existing tools... " -NoNewline
dotnet tool uninstall -g UnityCtl.Cli 2>$null | Out-Null
dotnet tool uninstall -g UnityCtl.Bridge 2>$null | Out-Null
Write-Host "done"

# Step 3: Clean the solution
Write-Host "[3/7] Cleaning solution... " -NoNewline
dotnet clean --configuration Release -v quiet | Out-Null
Write-Host "done"

# Step 4: Build the solution
Write-Host "[4/7] Building solution... " -NoNewline
$buildOutput = dotnet build --configuration Release -v quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED" -ForegroundColor Red
    Write-Host "Re-running with verbose output:"
    dotnet build --configuration Release
    exit 1
}
Write-Host "done"

# Step 5: Publish Protocol DLL to Unity package
Write-Host "[5/7] Publishing Protocol DLL... " -NoNewline
dotnet publish UnityCtl.Protocol/UnityCtl.Protocol.csproj --configuration Release --no-build -v quiet | Out-Null
Write-Host "done"

# Step 6: Pack NuGet packages
Write-Host "[6/7] Packing NuGet packages... " -NoNewline
if (Test-Path "./artifacts") {
    Remove-Item -Recurse -Force "./artifacts"
}
New-Item -ItemType Directory -Path "./artifacts" | Out-Null
dotnet pack --configuration Release --output ./artifacts --no-build -v quiet | Out-Null
Write-Host "done"

# Step 7: Install tools globally
Write-Host "[7/7] Installing tools globally... " -NoNewline
dotnet tool install -g UnityCtl.Cli --add-source ./artifacts | Out-Null
dotnet tool install -g UnityCtl.Bridge --add-source ./artifacts | Out-Null
Write-Host "done"

# Verify installation
Write-Host ""
$cliVersion = try { unityctl --version 2>$null } catch { "NOT FOUND" }
$bridgeVersion = try { unityctl-bridge --version 2>$null } catch { "NOT FOUND" }
Write-Host "Installed: unityctl $cliVersion, unityctl-bridge $bridgeVersion" -ForegroundColor Green
