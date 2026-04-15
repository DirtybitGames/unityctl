#!/bin/bash
# Syncs the version in package.json with the provided version.
# Skips the write when the content is unchanged so we don't bump the file's
# mtime and retrigger Unity's Packages/ directory watcher on every build.
VERSION=$1
PACKAGE_JSON=$2

tmp=$(mktemp)
sed "s/\"version\": \"[^\"]*\"/\"version\": \"$VERSION\"/" "$PACKAGE_JSON" > "$tmp"
if cmp -s "$tmp" "$PACKAGE_JSON"; then
    rm -f "$tmp"
else
    mv "$tmp" "$PACKAGE_JSON"
fi
