#!/usr/bin/env python3
"""Convert a showboat document into an HTML file for GitHub Pages.

Images and video are referenced as relative paths (not embedded).
The script copies media files into an output directory for deployment.

Media discovery:
- Images: found via ![alt](path) markdown references
- Videos: found via file paths ending in video extensions (.mp4, .webm, .mov)
  mentioned in output blocks. A <video> element is inserted after the output
  block that references each video.

Usage:
    python3 generate-demo-html.py [input.md] [out-dir]

Output directory structure:
    out-dir/
      index.html
      demo.md
      images/screenshot-1.png
      video/recording.mp4
"""

import html as html_mod
import re
import shutil
import sys
from pathlib import Path

VIDEO_EXTENSIONS = {".mp4", ".webm", ".mov"}

GITHUB_ICON = (
    '<svg height="16" width="16" viewBox="0 0 16 16" fill="currentColor" '
    'style="vertical-align:text-bottom;margin-right:6px;">'
    '<path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17'
    ".55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94"
    "-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87"
    " 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59"
    ".82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27"
    ".68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51"
    '.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 '
    '1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"'
    "/></svg>"
)

MIME_TYPES = {".mp4": "video/mp4", ".webm": "video/webm", ".mov": "video/mp4"}


def find_video_paths(text: str) -> list[str]:
    """Find file paths with video extensions in text (handles both / and \\ separators)."""
    paths = []
    for m in re.finditer(r"[\w./\\:-]+\.(?:mp4|webm|mov)\b", text):
        paths.append(m.group(0))
    return paths


def process_inline(text: str, image_map: dict[str, str]) -> str:
    """Convert inline markdown to HTML. image_map maps original paths to output hrefs."""
    text = html_mod.escape(text)
    text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)

    def img_replace(m):
        alt, path = m.group(1), m.group(2)
        href = image_map.get(path, path)
        return (
            f'<img src="{href}" alt="{alt}" '
            f'style="max-width:100%;border-radius:8px;margin:8px 0;">'
        )

    text = re.sub(r"(?<!\\)!\[([^\]]*)\]\(([^)]+\.(?:png|jpg|jpeg|gif|webp|svg))\)", img_replace, text)

    def link_replace(m):
        label, url = m.group(1), m.group(2)
        if "github.com" in url:
            return (
                f'<a href="{url}" class="github-link">'
                f"{GITHUB_ICON}{label}</a>"
            )
        return f'<a href="{url}">{label}</a>'

    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", link_replace, text)
    return text


def convert(md_path: str, out_dir: str):
    md = Path(md_path).read_text(encoding="utf-8")
    out_path = Path(out_dir)
    img_dir = out_path / "images"
    vid_dir = out_path / "video"
    img_dir.mkdir(parents=True, exist_ok=True)
    vid_dir.mkdir(parents=True, exist_ok=True)

    # Discover and copy images — map original paths to output hrefs
    image_map: dict[str, str] = {}
    img_idx = 0
    for m in re.finditer(r"(?<!\\)!\[([^\]]*)\]\(([^)]+\.(?:png|jpg|jpeg|gif|webp|svg))\)", md):
        path = m.group(2)
        if path in image_map:
            continue
        src = Path(path)
        if src.exists():
            img_idx += 1
            dest_name = f"screenshot-{img_idx}{src.suffix}"
            shutil.copy2(src, img_dir / dest_name)
            image_map[path] = f"images/{dest_name}"
            print(f"  Copied {path} -> images/{dest_name}")
        else:
            print(f"  Warning: image not found: {path}", file=sys.stderr)
            image_map[path] = path

    # Discover and copy videos from output blocks
    # video_map maps original filename to output href
    video_map: dict[str, str] = {}
    vid_idx = 0
    for video_path_str in find_video_paths(md):
        # Normalize backslashes
        normalized = video_path_str.replace("\\", "/")
        if normalized in video_map:
            continue
        src = Path(normalized)
        if not src.exists():
            # Try the original (unnormalized) path
            src = Path(video_path_str)
        if src.exists():
            vid_idx += 1
            dest_name = f"recording-{vid_idx}{src.suffix}" if vid_idx > 1 else f"recording{src.suffix}"
            shutil.copy2(src, vid_dir / dest_name)
            video_map[normalized] = f"video/{dest_name}"
            print(f"  Copied {src} -> video/{dest_name}")
        else:
            print(f"  Warning: video not found: {video_path_str}", file=sys.stderr)

    # Parse markdown
    lines = md.split("\n")
    out = []
    in_code = False
    code_lang = ""
    code_lines = []
    i = 0

    while i < len(lines):
        line = lines[i]

        if line.startswith("```") and not in_code:
            lang = line[3:].strip()
            if "{image}" in lang:
                i += 1
                while i < len(lines) and not lines[i].startswith("```"):
                    i += 1
                i += 1
                continue
            in_code = True
            code_lang = lang
            code_lines = []
            i += 1
            continue
        elif line.startswith("```") and in_code:
            in_code = False
            code_content = html_mod.escape("\n".join(code_lines))
            if code_lang == "output":
                out.append(f'<pre class="output"><code>{code_content}</code></pre>')
                # Check if this output block referenced a video — insert player after it
                raw_output = "\n".join(code_lines)
                for video_path_str in find_video_paths(raw_output):
                    normalized = video_path_str.replace("\\", "/")
                    href = video_map.get(normalized)
                    if href:
                        suffix = Path(href).suffix
                        mime = MIME_TYPES.get(suffix, "video/mp4")
                        out.append(
                            f'<video controls autoplay loop muted '
                            f'style="max-width:100%;border-radius:8px;margin:8px 0;">'
                            f'<source src="{href}" type="{mime}"></video>'
                        )
            else:
                cls = f' class="lang-{code_lang}"' if code_lang else ""
                out.append(f"<pre{cls}><code>{code_content}</code></pre>")
            i += 1
            continue
        elif in_code:
            code_lines.append(line)
            i += 1
            continue

        if line.startswith("# "):
            out.append(f"<h1>{html_mod.escape(line[2:])}</h1>")
        elif line.startswith("## "):
            out.append(f"<h2>{html_mod.escape(line[3:])}</h2>")
        elif line.startswith("### "):
            out.append(f"<h3>{html_mod.escape(line[4:])}</h3>")
        elif line.startswith("*2") and "Showboat" in line:
            out.append(
                f'<p class="meta">{html_mod.escape(line.strip("*"))}</p>'
            )
        elif line.startswith("<!--"):
            pass
        elif re.match(r"^!\[", line):
            out.append(process_inline(line, image_map))
        elif line.strip() == "":
            pass
        else:
            out.append(f"<p>{process_inline(line, image_map)}</p>")

        i += 1

    body = "\n".join(out)

    html_out = f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>UnityCtl Demo</title>
<style>
  :root {{ --bg: #ffffff; --fg: #1f2328; --muted: #656d76; --border: #d0d7de; --code-bg: #f6f8fa; --output-bg: #f6f8fa; --accent: #0969da; --green: #1a7f37; --strong: #1f2328; }}
  @media (prefers-color-scheme: dark) {{
    :root {{ --bg: #0d1117; --fg: #e6edf3; --muted: #8b949e; --border: #30363d; --code-bg: #161b22; --output-bg: #0d1117; --accent: #58a6ff; --green: #3fb950; --strong: #ffffff; }}
  }}
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; background: var(--bg); color: var(--fg); line-height: 1.6; max-width: 880px; margin: 0 auto; padding: 40px 24px; }}
  h1 {{ font-size: 2em; margin: 0 0 8px; border-bottom: 1px solid var(--border); padding-bottom: 12px; }}
  h2 {{ font-size: 1.4em; margin: 32px 0 12px; color: var(--accent); }}
  h3 {{ font-size: 1.1em; margin: 24px 0 8px; }}
  p {{ margin: 8px 0; }}
  p.meta {{ color: var(--muted); font-size: 0.85em; margin-bottom: 20px; }}
  code {{ font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace; font-size: 0.9em; background: var(--code-bg); padding: 2px 6px; border-radius: 4px; }}
  pre {{ border-radius: 8px; padding: 16px; overflow-x: auto; margin: 8px 0; font-size: 0.85em; }}
  pre code {{ background: none; padding: 0; }}
  pre:not(.output) {{ background: var(--code-bg); border: 1px solid var(--border); }}
  pre.output {{ background: var(--output-bg); border-left: 3px solid var(--green); color: var(--muted); }}
  img {{ display: block; }}
  video {{ display: block; }}
  a {{ color: var(--accent); text-decoration: none; }}
  a:hover {{ text-decoration: underline; }}
  .github-link {{ display: inline-flex; align-items: center; padding: 6px 14px; border: 1px solid var(--border); border-radius: 6px; color: var(--fg); font-size: 0.9em; transition: border-color 0.15s; }}
  .github-link:hover {{ border-color: var(--accent); text-decoration: none; }}
  strong {{ color: var(--strong); }}
</style>
</head>
<body>
{body}
</body>
</html>"""

    html_path = out_path / "index.html"
    html_path.write_text(html_out, encoding="utf-8")

    # Also copy source document
    shutil.copy2(md_path, out_path / "demo.md")

    print(f"Written {html_path} ({len(html_out) // 1024}KB)")


if __name__ == "__main__":
    src = sys.argv[1] if len(sys.argv) > 1 else "demo.md"
    dst = sys.argv[2] if len(sys.argv) > 2 else "dist"
    convert(src, dst)
