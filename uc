#!/bin/bash
# Development wrapper - runs unityctl via dotnet run (no global install needed)
exec dotnet run --project "$(dirname "$0")/UnityCtl.Cli" -- "$@"
