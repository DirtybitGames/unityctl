#!/bin/bash
# Syncs the version in package.json with the provided version
VERSION=$1
PACKAGE_JSON=$2

sed -i "s/\"version\": \"[^\"]*\"/\"version\": \"$VERSION\"/" "$PACKAGE_JSON"
