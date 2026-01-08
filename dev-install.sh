#!/bin/bash
set -e

echo "UnityCtl - Local Development Install"
echo ""

# Step 1: Kill any running bridge processes
echo -n "[1/7] Stopping bridge processes... "
taskkill //F //IM unityctl-bridge.exe 2>/dev/null && echo "stopped" || echo "not running"

# Step 2: Uninstall existing tools
echo -n "[2/7] Uninstalling existing tools... "
dotnet tool uninstall -g UnityCtl.Cli > /dev/null 2>&1 || true
dotnet tool uninstall -g UnityCtl.Bridge > /dev/null 2>&1 || true
echo "done"

# Step 3: Clean the solution
echo -n "[3/7] Cleaning solution... "
dotnet clean --configuration Release -v quiet > /dev/null
echo "done"

# Step 4: Build the solution
echo -n "[4/7] Building solution... "
if ! dotnet build --configuration Release -v quiet > /dev/null; then
    echo "FAILED"
    echo "Re-running with verbose output:"
    dotnet build --configuration Release
    exit 1
fi
echo "done"

# Step 5: Publish Protocol DLL to Unity package
echo -n "[5/7] Publishing Protocol DLL... "
dotnet publish UnityCtl.Protocol/UnityCtl.Protocol.csproj --configuration Release --no-build -v quiet > /dev/null
echo "done"

# Step 6: Pack NuGet packages
echo -n "[6/7] Packing NuGet packages... "
rm -rf ./artifacts
mkdir -p ./artifacts
dotnet pack --configuration Release --output ./artifacts --no-build -v quiet > /dev/null
echo "done"

# Step 7: Install tools globally
echo -n "[7/7] Installing tools globally... "
dotnet tool install -g UnityCtl.Cli --add-source ./artifacts > /dev/null
dotnet tool install -g UnityCtl.Bridge --add-source ./artifacts > /dev/null
echo "done"

# Verify installation
echo ""
CLI_VERSION=$(unityctl --version 2>/dev/null || echo "NOT FOUND")
BRIDGE_VERSION=$(unityctl-bridge --version 2>/dev/null || echo "NOT FOUND")
echo "Installed: unityctl $CLI_VERSION, unityctl-bridge $BRIDGE_VERSION"
