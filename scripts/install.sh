#!/bin/bash
# UnityCtl Installation Script for Linux/macOS
# Usage: curl -sSL https://raw.githubusercontent.com/DirtybitGames/unityctl/main/scripts/install.sh | bash

set -e

echo "============================================"
echo "         UnityCtl Installation Script"
echo "============================================"
echo

# Check for dotnet
if ! command -v dotnet &> /dev/null; then
    echo "Error: .NET SDK is not installed."
    echo "Please install .NET SDK 10.0 or later from https://dotnet.microsoft.com/download"
    exit 1
fi

# Check dotnet version
DOTNET_VERSION=$(dotnet --version)
echo "Found .NET SDK version: $DOTNET_VERSION"
echo

# Install or update CLI
echo "Installing UnityCtl.Cli..."
if dotnet tool list -g | grep -q "unityctl.cli"; then
    echo "  Updating existing installation..."
    dotnet tool update -g UnityCtl.Cli
else
    echo "  Installing..."
    dotnet tool install -g UnityCtl.Cli
fi

# Install or update Bridge
echo "Installing UnityCtl.Bridge..."
if dotnet tool list -g | grep -q "unityctl.bridge"; then
    echo "  Updating existing installation..."
    dotnet tool update -g UnityCtl.Bridge
else
    echo "  Installing..."
    dotnet tool install -g UnityCtl.Bridge
fi

echo
echo "============================================"
echo "         Installation Complete!"
echo "============================================"
echo
echo "UnityCtl has been installed successfully."
echo
echo "Getting Started:"
echo "  1. Navigate to your Unity project directory"
echo "  2. Run: unityctl setup"
echo "  3. Open your Unity project in Unity Editor"
echo
echo "For more information:"
echo "  unityctl --help"
echo "  https://github.com/DirtybitGames/unityctl"
echo
