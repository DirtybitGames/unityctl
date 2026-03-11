---
name: demo-page
description: Rebuild and publish the unityctl demo page hosted at https://dirtybitgames.github.io/unityctl/. Use when asked to update the demo, rebuild the demo page, update gh-pages, or refresh the demo screenshots/video.
---

# Demo Page Workflow

The unityctl demo page is a self-contained HTML file hosted on GitHub Pages via the `gh-pages` branch. It showcases unityctl features with live command output, screenshots, and video.

**Live URL:** https://dirtybitgames.github.io/unityctl/

## How It Works

1. **`demo.md`** — showboat document (markdown with executable code blocks + captured output)
2. **`demo.html`** — self-contained HTML with all images/video embedded as base64
3. **`gh-pages` branch** — contains `index.html` (copy of `demo.html`) and `demo.md` (source)

Tools: [showboat](https://pypi.org/project/showboat/) for the demo document, Python for HTML generation.

## Prerequisites

- `uvx` available (for running showboat)

### Required State Before Recording

The bridge and Unity Editor must be running with `TestScene` loaded. This was the state when the demo was originally recorded. Set it up with:

```bash
unityctl bridge start
unityctl editor run          # or open unity-project/ manually
unityctl wait                # block until Unity connects
unityctl scene load Assets/Scenes/TestScene.unity
```

Verify with `unityctl status` — all three indicators (Editor, Bridge, Connection) should show `[+]`.

## Rebuilding From Scratch

The demo document is built with showboat. Each `exec` block runs a real command and captures its output. Instance IDs change between sessions, so look them up dynamically with `unityctl snapshot`.

### Demo Structure

Extract the sequence of showboat commands from the current published demo:

```bash
git show gh-pages:demo.md > demo.md
uvx showboat extract demo.md
```

This prints all the `showboat init/note/exec/image` commands needed to recreate the demo. Use these as the recipe — adapt instance IDs from the current `unityctl snapshot` output.

### Important Notes

- Use `uvx showboat` (not bare `showboat`) since it's a Python tool
- Instance IDs (e.g., `[i:-5074]`) change on every scene reload — look them up from snapshot output
- Use `uvx showboat pop demo.md` to remove failed entries
- Replace any user-specific or OS-specific absolute paths with `~` equivalents in the final document (e.g. `C:\Users\name\...` or `/home/name/...` → `~/...`)
- Normalize backslash path separators in output to forward slashes for readability
- The `--project` flag is not needed — unityctl auto-detects the project
- Prefer `script eval` over `script execute` for simple expressions
- Use `script execute -f` only for multi-class scripts

## Generating HTML

Run `.claude/skills/demo-page/generate-demo-html.py` to convert `demo.md` into a self-contained `demo.html`:

```bash
python3 .claude/skills/demo-page/generate-demo-html.py
```

This script:
- Parses the showboat markdown format
- Embeds all referenced PNG images as base64 data URIs
- Embeds `unity-project/Recordings/demo-spin.mp4` as base64
- Produces a dark-themed HTML file with syntax-highlighted code blocks

## Publishing to GitHub Pages

```bash
git stash
git checkout gh-pages
cp demo.html index.html
cp demo.md demo.md
git add index.html demo.md
git commit -m "Update demo page"
git push origin gh-pages
git checkout main
git stash pop
```

## Quick Update (no full rebuild)

To update just a screenshot or the video without rebuilding the whole demo:

1. Edit `demo.md` directly (or use `showboat pop` + `showboat exec` to redo a block)
2. Re-run `python3 .claude/skills/demo-page/generate-demo-html.py`
3. Publish to `gh-pages` (see above)

## Interaction Guidelines

1. **Check prerequisites first** — run `unityctl status` to verify connectivity
2. **Load TestScene** before starting — `unityctl scene load Assets/Scenes/TestScene.unity`
3. **Look up instance IDs** from `unityctl snapshot` output (they change between sessions)
4. **Show progress** as each section is recorded
5. **Clean up** at the end — remove any temp scripts, restore original scene
6. **Confirm before pushing** to `gh-pages`
