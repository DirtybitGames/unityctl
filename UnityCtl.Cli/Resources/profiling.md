# unityctl profile — Unity profiler from the CLI

Capture frame stats, find spikes, drill into hierarchies, microbenchmark expressions.
Read this when the user mentions performance, FPS, hitches, GC, draw calls, profiling, frame budget, or asks "what's slow".

## Lifecycle

Sessions are start → run scenario → stop. The buffer survives after stop, so `explain`/`hotspots`/`frame` can drill into captured frames until the next session clears it.

```bash
# One-shot (most common)
unityctl profile vitals --duration 3        # Curated 5-number report
unityctl profile capture --duration 5       # Full summary + topFrames + drivers
unityctl profile capture --duration 5 --save run.data    # Also save .data for the Profiler window

# Manual session
unityctl profile start --stats main,gpu,drawcalls,gc-alloc --max-duration 30   # Returns sessionId
unityctl profile stop <sessionId>           # Returns summary JSON

# CI gate (non-zero exit on threshold breach)
unityctl profile assert --p99-frame-ms 33 --gc-alloc-per-frame 1024
```

**Always profile in play mode** unless you explicitly want editor-tick stats. Edit-mode samples editor update frequency, which doesn't reflect game performance.

## Capture output: topFrames

`profile capture` and `profile vitals` always include a `topFrames` array — the worst frames in the window with top-3 driver markers attached, each carrying a `hot` field that points at the descendant with the highest self time inside the driver's subtree. **This is the spike-detection primary output**. No need to dump per-frame samples, chain `explain` calls, or drill with `profile frame` for the typical "what's slow" question.

Ranking metric depends on what's available:
- **In play mode** (PlayerLoop in hierarchy): ranked by `playerLoopMs` — gameplay-only time, ignores editor IMGUI repaint variance. Drivers descend from inside PlayerLoop.
- **Otherwise**: ranked by `cpuMainMs` (full main thread). Drivers descend from frame root.

Both `cpuMainMs` and `playerLoopMs` are populated when available, so consumers can rerank or read either signal.

`hitches` (frames over an absolute total-frame-time threshold) is separate — useful for CI gates, but blind to CPU spikes when total frame time is vsync-capped (Android/iOS clamp at 16/33 ms regardless of CPU time).

## Drilling a spike

Pick an `absoluteFrameIndex` from `topFrames` (or any frame in the buffer):

```bash
unityctl profile explain <frame> --top 15                # Flat top-N markers by self time + GC
unityctl profile frame <frame> --depth 3                 # Hierarchy *tree*, self-pruned
unityctl profile frame <frame> --depth 4 --root PlayerLoop   # Tree scoped to gameplay subtree
```

Use `frame --root PlayerLoop` in editor+play to skip past the EditorLoop / Application.Tick wrapping that otherwise dominates the tree.

## Hotspots aggregate

```bash
unityctl profile hotspots --top 20                       # Across the whole captured buffer
unityctl profile hotspots --top 20 --root PlayerLoop     # Gameplay-only — essential in editor+play mode
```

In editor+play, the unfiltered `hotspots` is dominated by `OnGUI` / `IMGUIContainer` / `EditorApplication.update:*` (editor menu polling) which can swamp game work by 40×. Always pass `--root PlayerLoop` when you care about game perf.

## Microbenchmark a single expression

`profile mark` wraps an expression in a `ProfilerMarker` + Stopwatch + per-thread GC accounting and runs it N times. No capture session needed.

```bash
unityctl profile mark "GameObject.FindObjectsByType<Camera>(FindObjectsSortMode.None)" --repeat 100
# → mean / p50 / p95 / min / max ms + gcBytesPerCall
```

Useful for "is this hot path actually slow?" without the start/wait/stop dance.

## Stat aliases

`profile start --stats` accepts these aliases (resolved server-side):

| Alias | Resolves to |
|---|---|
| `main` | CPU Main Thread Frame Time |
| `render` | CPU Render Thread Frame Time |
| `gpu` | GPU Frame Time |
| `total-frame` | CPU Total Frame Time |
| `drawcalls` | Draw Calls Count |
| `setpass` | SetPass Calls Count |
| `batches` | Batches Count |
| `triangles` / `tris` | Triangles Count |
| `gc-alloc` | GC Allocated In Frame |
| `gc-used` / `gc-reserved` | GC Used Memory / GC Reserved Memory |
| `system-memory` | System Used Memory |

Default vitals stats: main, render, gpu, drawcalls, gc-alloc, system-memory. List all available counters with `unityctl profile list-stats [--category Render]`.

## Memory snapshot

```bash
unityctl profile snapshot --output mem.snap     # Requires com.unity.memoryprofiler package
```

## Remote / Android profiling

```bash
unityctl profile targets                        # List editor + connected players
unityctl profile connect 127.0.0.1:54999        # Direct-URL connect (Android via adb forward)
```

When the editor's profiler is connected to a remote target (autoconnect-profiler dev build, or via `connect`), captures automatically come from the remote process. The summary's `targetIsRemote: true` flags this — the human output also prints `target: REMOTE — <connection name>`.

Remote captures use the same code path as local (post-hoc `RawFrameDataView` walk), so `topFrames` / `explain` / `frame` / `hotspots` all work.

## Typical workflows

### "Where are my spikes?"

```bash
unityctl play enter
sleep 2  # let it settle
unityctl profile capture --duration 10 -t 120 --json
# Inspect topFrames[] — pick worst absoluteFrameIndex
unityctl profile frame <abs> --depth 4 --root PlayerLoop
unityctl play exit
```

### "What's hot on average?"

```bash
unityctl profile capture --duration 10 -t 120
unityctl profile hotspots --top 20 --root PlayerLoop
```

### "Is my new code slow?"

```bash
unityctl profile mark "MySystem.DoExpensiveThing()" --repeat 200
# Compare mean / p95 / gcBytesPerCall before/after
```

### "Did this PR regress perf?"

```bash
unityctl play enter
sleep 2
unityctl profile assert --p99-frame-ms 33 --gc-alloc-per-frame 4096 --duration 10 -t 120
unityctl play exit
# Exit 1 on breach — wire into CI
```
