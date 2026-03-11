#!/bin/bash
# Development wrapper - runs unityctl via dotnet run (no global install needed)
# Sets UNITYCTL_REPO_ROOT so bridge start also uses the local build (via dotnet run)
export UNITYCTL_REPO_ROOT="$(dirname "$0")"
exec dotnet run --project "$UNITYCTL_REPO_ROOT/UnityCtl.Cli" -- "$@"
