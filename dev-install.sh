#!/bin/bash
set -e

echo "========================================"
echo "UnityCtl - Local Development Install"
echo "========================================"
echo ""

# Step 1: Kill any running bridge processes
echo "[1/7] Stopping running bridge processes..."
taskkill //F //IM unityctl-bridge.exe 2>/dev/null || echo "  No bridge process running"
echo ""

# Step 2: Uninstall existing tools
echo "[2/7] Uninstalling existing tools..."
dotnet tool uninstall -g UnityCtl.Cli 2>/dev/null || echo "  UnityCtl.Cli not installed"
dotnet tool uninstall -g UnityCtl.Bridge 2>/dev/null || echo "  UnityCtl.Bridge not installed"
echo ""

# Step 3: Clean the solution
echo "[3/7] Cleaning solution..."
dotnet clean --configuration Release
echo ""

# Step 4: Build the solution
echo "[4/7] Building solution..."
dotnet build --configuration Release
if [ $? -ne 0 ]; then
    echo "❌ Build failed!"
    exit 1
fi
echo ""

# Step 5: Publish Protocol DLL to Unity package
echo "[5/7] Publishing Protocol DLL to Unity package..."
dotnet publish UnityCtl.Protocol/UnityCtl.Protocol.csproj --configuration Release --no-build
echo ""

# Step 6: Pack NuGet packages
echo "[6/7] Packing NuGet packages..."
rm -rf ./artifacts
mkdir -p ./artifacts
dotnet pack --configuration Release --output ./artifacts --no-build
echo ""
echo "Packages created:"
ls -lh ./artifacts/*.nupkg
echo ""

# Step 7: Install tools globally
echo "[7/7] Installing tools globally..."
dotnet tool install -g UnityCtl.Cli --add-source ./artifacts
dotnet tool install -g UnityCtl.Bridge --add-source ./artifacts
echo ""

# Verify installation
echo "========================================"
echo "✅ Installation complete!"
echo "========================================"
echo ""
echo "Installed tools:"
dotnet tool list -g | grep -i unityctl || true
echo ""
echo "Testing command availability:"
echo "  unityctl version:"
unityctl --version || echo "  ❌ unityctl not found in PATH"
echo ""
echo "  unityctl-bridge version:"
unityctl-bridge --version || echo "  ❌ unityctl-bridge not found in PATH"
echo ""
echo "Available console commands:"
unityctl console --help || true
