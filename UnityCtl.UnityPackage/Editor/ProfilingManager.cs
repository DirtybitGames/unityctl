using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityCtl.Protocol;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Per-session profiling state. The session enables ProfilerDriver, snapshots the start
    /// frame index, and on Stop() walks the profiler buffer with RawFrameDataView, reading
    /// counter values per actual rendered frame. Same code path for editor and remote players —
    /// the only difference is ProfilerDriver.profileEditor.
    ///
    /// Why post-hoc buffer reads, not per-tick ProfilerRecorder sampling: EditorApplication.update
    /// ticks at editor framerate (often throttled, especially when unfocused), which mismatches
    /// the actual rendered frame rate. Reading from the profiler buffer gives one record per real
    /// frame — matches what the user sees in the Profiler window.
    /// </summary>
    internal sealed class ProfilingSession
    {
        public string Id { get; }
        public DateTime StartedAtUtc { get; }
        public string[] StatNames { get; }
        public double? MaxDurationSeconds { get; }
        public string? Target { get; }
        public bool TargetIsRemote { get; }
        public string? SavePath { get; }
        public bool DriveEditorProfiler { get; }

        // Built-in counter names share units across editor and player. Resolve once at start
        // from the local Editor's ProfilerRecorderHandle catalog.
        private readonly Dictionary<string, ProfilerMarkerDataUnit> _statNameToUnit = new();

        public int AbsoluteStartFrame { get; private set; } = -1;
        public bool IsActive { get; private set; }

        public int CurrentFrameCount =>
            IsActive && AbsoluteStartFrame >= 0
                ? Math.Max(0, ProfilerDriver.lastFrameIndex - AbsoluteStartFrame + 1)
                : 0;

        public ProfilingSession(
            string id,
            string[] statNames,
            double? maxDurationSeconds,
            string? target,
            bool targetIsRemote,
            string? savePath,
            bool driveEditorProfiler)
        {
            Id = id;
            StatNames = statNames;
            MaxDurationSeconds = maxDurationSeconds;
            Target = target;
            TargetIsRemote = targetIsRemote;
            SavePath = savePath;
            DriveEditorProfiler = driveEditorProfiler;
            StartedAtUtc = DateTime.UtcNow;

            for (int i = 0; i < statNames.Length; i++)
            {
                var (_, unit, _) = FindHandleByName(statNames[i]);
                _statNameToUnit[statNames[i]] = unit;
            }

            IsActive = true;
        }

        public void StartEditorProfilerCapture()
        {
            TryBumpBufferSize();
            // Local: profile the editor itself. Remote: leave profileEditor false so the streamed
            // remote frames populate the buffer.
            ProfilerDriver.profileEditor = !TargetIsRemote;
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.enabled = true;
            AbsoluteStartFrame = ProfilerDriver.lastFrameIndex + 1;
        }

        // ProfilerUserSettings.frameCount is internal (Unity 6) — default 300 frames evicts frames
        // mid-capture for any session longer than ~1.5 s at 200 FPS. Bump to 2000 for the duration
        // of a session so `profile explain` against early hitches still works after stop. Restored
        // at session stop via RestoreBufferSize().
        private static int? _originalFrameCount;
        private static void TryBumpBufferSize()
        {
            const int Target = 2000;
            try
            {
                var t = Type.GetType("UnityEditor.Profiling.ProfilerUserSettings, UnityEditor");
                if (t == null) return;
                var prop = t.GetProperty("frameCount", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop == null) return;
                if (!_originalFrameCount.HasValue)
                    _originalFrameCount = (int)prop.GetValue(null);
                var current = (int)prop.GetValue(null);
                if (current < Target) prop.SetValue(null, Target);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCtl] Failed to bump profiler frame buffer: {ex.Message}");
            }
        }
        private static void RestoreBufferSize()
        {
            if (!_originalFrameCount.HasValue) return;
            try
            {
                var t = Type.GetType("UnityEditor.Profiling.ProfilerUserSettings, UnityEditor");
                var prop = t?.GetProperty("frameCount", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                prop?.SetValue(null, _originalFrameCount.Value);
            }
            catch { }
            _originalFrameCount = null;
        }

        public ProfileStopResult Stop(bool includeSamples, double hitchMultiplier, double? hitchAbsoluteMs)
        {
            IsActive = false;

            // Snapshot the end frame before disabling the driver so frames received in the same
            // editor tick aren't dropped.
            int endFrame = ProfilerDriver.lastFrameIndex;
            ProfilerDriver.enabled = false;

            // Save .data first while the buffer still holds the frames. We leave the buffered
            // frames in place (driver is just paused) so a follow-up `profile explain` /
            // `hotspots` / `frame` can walk them. Next session's start clears the buffer.
            TrySaveProfile();

            int startFrame = Math.Max(AbsoluteStartFrame, ProfilerDriver.firstFrameIndex);
            int totalFrames = endFrame >= startFrame ? endFrame - startFrame + 1 : 0;

            var perStat = new double[StatNames.Length][];
            for (int i = 0; i < StatNames.Length; i++) perStat[i] = new double[totalFrames];
            var frameTimesMs = new double[totalFrames];

            for (int idx = 0; idx < totalFrames; idx++)
            {
                int frame = startFrame + idx;
                using var view = ProfilerDriver.GetRawFrameDataView(frame, 0);
                if (view == null || !view.valid)
                {
                    for (int i = 0; i < StatNames.Length; i++) perStat[i][idx] = double.NaN;
                    frameTimesMs[idx] = double.NaN;
                    continue;
                }

                for (int i = 0; i < StatNames.Length; i++)
                {
                    perStat[i][idx] = ReadCounter(view, StatNames[i]);
                }

                // Use the view's own frame time — actual wall-clock for this frame, which is what
                // determines perceived FPS. More accurate than the CPU Main Thread Frame Time
                // counter (which excludes GPU stalls) and immune to editor tick rate.
                frameTimesMs[idx] = view.frameTimeNs / 1_000_000.0;
            }

            var summaries = new List<ProfileStatSummary>(StatNames.Length);
            for (int i = 0; i < StatNames.Length; i++)
            {
                var unit = _statNameToUnit.TryGetValue(StatNames[i], out var u) ? u : ProfilerMarkerDataUnit.Undefined;
                var converted = new double[perStat[i].Length];
                for (int j = 0; j < perStat[i].Length; j++)
                    converted[j] = ConvertSample(perStat[i][j], unit);
                summaries.Add(BuildSummary(StatNames[i], UnitDisplay(unit), converted, includeSamples));
            }

            var ftList = new List<double>(frameTimesMs.Length);
            foreach (var v in frameTimesMs) if (!double.IsNaN(v) && v > 0) ftList.Add(v);
            var threshold = ComputeHitchThreshold(ftList, hitchMultiplier, hitchAbsoluteMs);
            var hitches = new List<ProfileHitch>();
            for (int i = 0; i < frameTimesMs.Length; i++)
            {
                if (!double.IsNaN(frameTimesMs[i]) && frameTimesMs[i] > threshold)
                    hitches.Add(new ProfileHitch
                    {
                        FrameIndex = i,
                        FrameTimeMs = frameTimesMs[i],
                        AbsoluteFrameIndex = startFrame + i
                    });
            }

            // topFrames: the worst frames by CPU main thread time. This is the spike axis agents
            // actually want — total frame time is vsync-capped (16/33ms) on most platforms and
            // hides CPU variance. We always populate this regardless of hitch threshold.
            var topFrames = ComputeTopFrames(StatNames, perStat, frameTimesMs, startFrame, _statNameToUnit, topN: 5, driversPerFrame: 3);

            return new ProfileStopResult
            {
                SessionId = Id,
                DurationSeconds = (DateTime.UtcNow - StartedAtUtc).TotalSeconds,
                Frames = totalFrames,
                Summaries = summaries.ToArray(),
                Hitches = hitches.Count > 0 ? hitches.ToArray() : null,
                TopFrames = topFrames,
                SavedPath = !string.IsNullOrEmpty(SavePath) ? SavePath : null,
                Target = Target,
                TargetIsRemote = TargetIsRemote
            };
        }

        private static ProfileTopFrame[]? ComputeTopFrames(
            string[] statNames,
            double[][] perStat,
            double[] frameTimesMs,
            int startFrame,
            Dictionary<string, ProfilerMarkerDataUnit> unitMap,
            int topN,
            int driversPerFrame)
        {
            int total = frameTimesMs.Length;
            if (total == 0) return null;

            // Find a CPU main thread stat. Prefer the precise counter; fall back to the broader
            // "Main Thread" if the precise one wasn't requested.
            int cpuStatIdx = -1;
            foreach (var preferred in new[] { "CPU Main Thread Frame Time", "Main Thread" })
            {
                for (int i = 0; i < statNames.Length; i++)
                    if (statNames[i] == preferred) { cpuStatIdx = i; break; }
                if (cpuStatIdx >= 0) break;
            }

            var candidates = new List<(int idx, double cpuMs, double ftMs)>(total);
            for (int i = 0; i < total; i++)
            {
                double cpuMs;
                if (cpuStatIdx >= 0)
                {
                    var raw = perStat[cpuStatIdx][i];
                    cpuMs = double.IsNaN(raw) ? double.NaN : raw / 1_000_000.0;
                }
                else
                {
                    cpuMs = frameTimesMs[i];
                }
                if (double.IsNaN(cpuMs)) continue;
                candidates.Add((i, cpuMs, frameTimesMs[i]));
            }
            if (candidates.Count == 0) return null;

            // First pass by CPU main thread to find the candidate pool. We over-pick (3x topN)
            // so that re-ranking by PlayerLoop time has enough room to surface a different set.
            // Per-frame hierarchy walks are ~1ms each; 3*topN keeps it bounded.
            candidates.Sort((a, b) => b.cpuMs.CompareTo(a.cpuMs));
            int poolSize = Math.Min(candidates.Count, Math.Max(topN * 3, topN));
            var pool = candidates.GetRange(0, poolSize);

            // For each candidate, walk the hierarchy once to collect:
            //   1. PlayerLoop nodeId + totalMs (gameplay-only signal)
            //   2. Top-N drivers — descended from inside PlayerLoop when present (so attribution
            //      reflects gameplay variance), otherwise descended from the frame root.
            var enriched = new List<(int idx, double cpuMs, double ftMs, double? playerLoopMs, int? playerLoopId, ProfileFrameDriver[] driversFromRoot, ProfileFrameDriver[] driversFromPlayerLoop)>(pool.Count);
            foreach (var p in pool)
            {
                int absFrame = startFrame + p.idx;
                using var hv = ProfilerDriver.GetHierarchyFrameDataView(
                    absFrame, 0,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnTotalTime,
                    sortAscending: false);
                double? playerLoopMs = null;
                int? playerLoopId = null;
                ProfileFrameDriver[] driversFromRoot = Array.Empty<ProfileFrameDriver>();
                ProfileFrameDriver[] driversFromPlayerLoop = Array.Empty<ProfileFrameDriver>();
                if (hv != null && hv.valid)
                {
                    playerLoopId = FindDescendantIdByName(hv, hv.GetRootItemID(), "PlayerLoop");
                    if (playerLoopId.HasValue)
                        playerLoopMs = hv.GetItemColumnDataAsDouble(playerLoopId.Value, HierarchyFrameDataView.columnTotalTime);
                    driversFromRoot = ResolveDrivers(hv, hv.GetRootItemID(), driversPerFrame);
                    if (playerLoopId.HasValue)
                        driversFromPlayerLoop = ResolveDrivers(hv, playerLoopId.Value, driversPerFrame);
                }
                enriched.Add((p.idx, p.cpuMs, p.ftMs, playerLoopMs, playerLoopId, driversFromRoot, driversFromPlayerLoop));
            }

            // Re-rank by playerLoopMs when most candidates have it (typical of editor+play and
            // remote-player captures). Otherwise stay with CPU main thread ranking.
            int withPlayerLoop = enriched.Count(e => e.playerLoopMs.HasValue && e.playerLoopMs.Value > 0);
            bool rankByPlayerLoop = withPlayerLoop >= enriched.Count / 2 && withPlayerLoop > 0;
            if (rankByPlayerLoop)
                enriched.Sort((a, b) => (b.playerLoopMs ?? 0).CompareTo(a.playerLoopMs ?? 0));
            // (else already sorted by cpuMs from the first pass)

            int take = Math.Min(topN, enriched.Count);
            var entries = new List<ProfileTopFrame>(take);
            for (int k = 0; k < take; k++)
            {
                var e = enriched[k];
                // When ranking by PlayerLoop, drivers should also come from inside PlayerLoop —
                // editor IMGUI/repaint dominates the root view but isn't what the agent's tracking.
                var drivers = rankByPlayerLoop && e.driversFromPlayerLoop.Length > 0
                    ? e.driversFromPlayerLoop
                    : e.driversFromRoot;
                entries.Add(new ProfileTopFrame
                {
                    FrameIndex = e.idx,
                    AbsoluteFrameIndex = startFrame + e.idx,
                    CpuMainMs = Math.Round(e.cpuMs, 3),
                    FrameTimeMs = double.IsNaN(e.ftMs) ? 0 : Math.Round(e.ftMs, 3),
                    PlayerLoopMs = e.playerLoopMs.HasValue ? Math.Round(e.playerLoopMs.Value, 3) : (double?)null,
                    Drivers = drivers
                });
            }
            return entries.ToArray();
        }

        internal static double? FindDescendantTotalMs(HierarchyFrameDataView hv, int parentId, string name, int maxDepth = 10)
        {
            // PlayerLoop in editor+play mode is buried under
            // EditorLoop > Application.Tick > UpdateScene > UpdateSceneIfNeeded > UpdateScene > PlayerLoop,
            // and nested "PlayerLoop" markers can exist (e.g. a small outer one + the substantial
            // gameplay one). We return the LARGEST match so we get the gameplay tick, not noise.
            var queue = new Queue<(int Id, int Depth)>();
            queue.Enqueue((parentId, 0));
            var children = new List<int>();
            double? best = null;
            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                if (depth > maxDepth) continue;
                children.Clear();
                hv.GetItemChildren(id, children);
                foreach (var c in children)
                {
                    if (string.Equals(hv.GetItemName(c), name, StringComparison.Ordinal))
                    {
                        var v = hv.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnTotalTime);
                        if (!best.HasValue || v > best.Value) best = v;
                    }
                    queue.Enqueue((c, depth + 1));
                }
            }
            return best;
        }

        internal static int? FindDescendantIdByName(HierarchyFrameDataView hv, int parentId, string name, int maxDepth = 10)
        {
            // Find the descendant with this name that has the LARGEST totalMs — handles nested
            // markers of the same name (e.g. multiple PlayerLoop occurrences) by picking the one
            // that actually wraps significant work.
            var queue = new Queue<(int Id, int Depth)>();
            queue.Enqueue((parentId, 0));
            var children = new List<int>();
            int? bestId = null;
            double bestTotal = -1;
            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                if (depth > maxDepth) continue;
                children.Clear();
                hv.GetItemChildren(id, children);
                foreach (var c in children)
                {
                    if (string.Equals(hv.GetItemName(c), name, StringComparison.Ordinal))
                    {
                        var v = hv.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnTotalTime);
                        if (v > bestTotal) { bestTotal = v; bestId = c; }
                    }
                    queue.Enqueue((c, depth + 1));
                }
            }
            return bestId;
        }

        private static ProfileFrameDriver[] ResolveDrivers(HierarchyFrameDataView hv, int startId, int n)
        {
            // Walk down through dominant-single-child levels until we reach a real bifurcation,
            // then return top-N children at that level. Caller picks the starting node — usually
            // the hierarchy root, but PlayerLoop's id when we want gameplay-only attribution.
            int currentId = startId;
            const double DominanceRatio = 0.7;
            const int MaxDescend = 10;

            for (int step = 0; step < MaxDescend; step++)
            {
                var children = new List<int>();
                hv.GetItemChildren(currentId, children);
                if (children.Count == 0) break;

                var ranked = children
                    .Select(c => new
                    {
                        Id = c,
                        Total = hv.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnTotalTime)
                    })
                    .Where(x => x.Total > 0.01)
                    .OrderByDescending(x => x.Total)
                    .ToList();
                if (ranked.Count == 0) break;

                double totalAtLevel = ranked.Sum(x => x.Total);
                double biggestShare = totalAtLevel > 0 ? ranked[0].Total / totalAtLevel : 1.0;

                if (biggestShare < DominanceRatio)
                    return RankToDrivers(hv, ranked.Select(r => r.Id), n);

                currentId = ranked[0].Id;
            }

            var fallback = new List<int>();
            hv.GetItemChildren(currentId, fallback);
            if (fallback.Count == 0)
                return new[] { ItemToDriver(hv, currentId) };
            return RankToDrivers(hv, fallback, n);
        }

        private static ProfileFrameDriver[] RankToDrivers(HierarchyFrameDataView hv, IEnumerable<int> ids, int n) =>
            ids
                .Select(c => ItemToDriver(hv, c))
                .OrderByDescending(d => d.TotalMs)
                .Take(Math.Max(1, n))
                .ToArray();

        private static ProfileFrameDriver ItemToDriver(HierarchyFrameDataView hv, int id)
        {
            var name = hv.GetItemName(id) ?? "";
            var total = hv.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnTotalTime);
            var self = hv.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnSelfTime);
            var calls = (int)hv.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnCalls);
            var gcKb = hv.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnGcMemory) / 1024.0;
            return new ProfileFrameDriver
            {
                Name = name,
                TotalMs = Math.Round(total, 3),
                SelfMs = Math.Round(self, 3),
                Calls = calls,
                GcKb = gcKb > 0.01 ? Math.Round(gcKb, 2) : (double?)null,
                Hot = FindHotLeaf(hv, id)
            };
        }

        /// <summary>
        /// Walks the driver's subtree and returns the descendant with the highest self time —
        /// the "what's actually slow" answer. Drivers are usually intermediate nodes (selfMs ~ 0)
        /// so without this an agent gets a system-level totalMs but no clue where the wall-clock
        /// goes inside it.
        /// </summary>
        private static ProfileFrameHotLeaf? FindHotLeaf(HierarchyFrameDataView hv, int rootId, int maxDepth = 12)
        {
            string? bestName = null;
            double bestSelf = 0;
            double bestTotal = 0;
            var stack = new Stack<(int Id, int Depth)>();
            stack.Push((rootId, 0));
            var children = new List<int>();
            while (stack.Count > 0)
            {
                var (id, depth) = stack.Pop();
                if (depth > maxDepth) continue;
                if (id != rootId)
                {
                    // Only consider descendants — the driver itself is reported separately.
                    var self = hv.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnSelfTime);
                    if (self > bestSelf)
                    {
                        bestSelf = self;
                        bestTotal = hv.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnTotalTime);
                        bestName = hv.GetItemName(id);
                    }
                }
                children.Clear();
                hv.GetItemChildren(id, children);
                foreach (var c in children) stack.Push((c, depth + 1));
            }
            // Filter out hot leaves that are essentially noise — anything below 0.05ms self is
            // not worth reporting to the agent (Round to 3 dp would show 0.000 anyway).
            if (bestName == null || bestSelf < 0.05) return null;
            return new ProfileFrameHotLeaf
            {
                Name = bestName,
                SelfMs = Math.Round(bestSelf, 3),
                TotalMs = Math.Round(bestTotal, 3)
            };
        }

        private static double ReadCounter(RawFrameDataView view, string statName)
        {
            int markerId = view.GetMarkerId(statName);
            if (markerId == FrameDataView.invalidMarkerId) return double.NaN;
            // Counter values arrive as long-encoded ints/ns. Conversion to ms etc. happens
            // upstream based on the unit map.
            return view.GetCounterValueAsLong(markerId);
        }

        private void TrySaveProfile()
        {
            if (string.IsNullOrEmpty(SavePath)) return;
            try
            {
                var dir = Path.GetDirectoryName(SavePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                ProfilerDriver.SaveProfile(SavePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCtl] Failed to save profile to {SavePath}: {ex.Message}");
            }
        }

        public void Cancel()
        {
            IsActive = false;
            try { ProfilerDriver.enabled = false; } catch { }
        }

        private static (ProfilerRecorderHandle Handle, ProfilerMarkerDataUnit Unit, ProfilerCategory Category) FindHandleByName(string name)
        {
            var list = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(list);
            foreach (var h in list)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (d.Name == name)
                    return (h, d.UnitType, d.Category);
            }
            return (default, ProfilerMarkerDataUnit.Undefined, default);
        }

        private static double ConvertSample(double raw, ProfilerMarkerDataUnit unit)
        {
            if (double.IsNaN(raw)) return raw;
            return unit switch
            {
                ProfilerMarkerDataUnit.TimeNanoseconds => raw / 1_000_000.0, // ns → ms
                _ => raw
            };
        }

        private static string UnitDisplay(ProfilerMarkerDataUnit unit) => unit switch
        {
            ProfilerMarkerDataUnit.TimeNanoseconds => "ms",
            ProfilerMarkerDataUnit.Bytes => "bytes",
            ProfilerMarkerDataUnit.Count => "count",
            ProfilerMarkerDataUnit.Percent => "percent",
            ProfilerMarkerDataUnit.FrequencyHz => "hz",
            _ => "value"
        };

        private static double ComputeHitchThreshold(List<double> frameTimesMs, double multiplier, double? abs)
        {
            if (abs.HasValue) return abs.Value;
            if (frameTimesMs.Count == 0) return double.PositiveInfinity;
            // Use median × multiplier as a robust baseline (avoids jitter from a few warmup frames).
            var sorted = frameTimesMs.ToArray();
            Array.Sort(sorted);
            var median = sorted[sorted.Length / 2];
            return median * multiplier;
        }

        private static ProfileStatSummary BuildSummary(string name, string unit, double[] samples, bool includeSamples)
        {
            if (samples.Length == 0)
            {
                return new ProfileStatSummary
                {
                    Name = name,
                    Unit = unit,
                    Frames = 0,
                    Avg = 0,
                    Min = 0,
                    Max = 0,
                    P50 = 0,
                    P95 = 0,
                    P99 = 0,
                    Samples = includeSamples ? samples : null
                };
            }

            var valid = new List<double>(samples.Length);
            foreach (var s in samples) if (!double.IsNaN(s)) valid.Add(s);
            if (valid.Count == 0)
            {
                return new ProfileStatSummary
                {
                    Name = name,
                    Unit = unit,
                    Frames = samples.Length,
                    Avg = 0, Min = 0, Max = 0, P50 = 0, P95 = 0, P99 = 0,
                    Samples = includeSamples ? samples : null
                };
            }

            var sorted = valid.ToArray();
            Array.Sort(sorted);
            double sum = 0;
            for (int i = 0; i < sorted.Length; i++) sum += sorted[i];

            return new ProfileStatSummary
            {
                Name = name,
                Unit = unit,
                Frames = samples.Length,
                Avg = sum / sorted.Length,
                Min = sorted[0],
                Max = sorted[sorted.Length - 1],
                P50 = Percentile(sorted, 0.50),
                P95 = Percentile(sorted, 0.95),
                P99 = Percentile(sorted, 0.99),
                Samples = includeSamples ? samples : null
            };
        }

        private static double Percentile(double[] sortedValues, double p)
        {
            if (sortedValues.Length == 0) return 0;
            var idx = (int)Math.Min(sortedValues.Length - 1, Math.Max(0, Math.Round(p * (sortedValues.Length - 1))));
            return sortedValues[idx];
        }
    }

    /// <summary>
    /// Singleton coordinator. Owns active profiling sessions, watches max-duration auto-stop,
    /// and serves list-stats / start / stop / status / explain / hotspots / frame / mark / snapshot / targets RPCs.
    /// </summary>
    public class ProfilingManager
    {
        private static ProfilingManager _instance;
        public static ProfilingManager Instance => _instance ??= new ProfilingManager();

        private readonly Dictionary<string, ProfilingSession> _sessions = new();
        private bool _tickHooked;

        // Curated alias map → real ProfilerRecorder stat names.
        // Keep names that exist on Unity 6 / 2023.x. Aliases give agents stable handles
        // independent of Unity's slightly inconsistent counter naming.
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // CPU/GPU frame timings (Render category — the public ones with stable names)
            { "main", "CPU Main Thread Frame Time" },
            { "render", "CPU Render Thread Frame Time" },
            { "render-thread", "CPU Render Thread Frame Time" },
            { "gpu", "GPU Frame Time" },
            { "frame-time", "CPU Main Thread Frame Time" },
            { "total-frame", "CPU Total Frame Time" },
            // Internal/Main Thread fallback (slightly different units, sums all editor work)
            { "main-internal", "Main Thread" },

            // Render
            { "drawcalls", "Draw Calls Count" },
            { "draw-calls", "Draw Calls Count" },
            { "setpass", "SetPass Calls Count" },
            { "batches", "Batches Count" },
            { "triangles", "Triangles Count" },
            { "tris", "Triangles Count" },
            { "vertices", "Vertices Count" },
            { "verts", "Vertices Count" },

            // Memory
            { "system-memory", "System Used Memory" },
            { "gc-reserved", "GC Reserved Memory" },
            { "gc-used", "GC Used Memory" },
            { "gc-alloc", "GC Allocated In Frame" },
            { "total-memory", "Total Reserved Memory" },
            { "total-used", "Total Used Memory" }
        };

        public static string ResolveAlias(string name) =>
            Aliases.TryGetValue(name, out var resolved) ? resolved : name;

        public ProfileListStatsResult ListStats(string? categoryFilter)
        {
            var list = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(list);

            var stats = new List<ProfileStatInfo>(list.Count);
            foreach (var h in list)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                var category = d.Category.Name;
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                stats.Add(new ProfileStatInfo
                {
                    Name = d.Name,
                    Category = category,
                    Unit = d.UnitType.ToString(),
                    DataType = d.DataType.ToString()
                });
            }

            stats.Sort((a, b) => string.CompareOrdinal(a.Category + "/" + a.Name, b.Category + "/" + b.Name));

            return new ProfileListStatsResult
            {
                Count = stats.Count,
                Stats = stats.ToArray()
            };
        }

        public ProfileStartResult Start(
            string[] requestedStats,
            double? maxDurationSeconds,
            string? target,
            bool targetIsRemote,
            string? savePath,
            bool driveEditorProfiler)
        {
            var sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);

            // Resolve aliases.
            var resolvedStats = requestedStats.Select(ResolveAlias).Distinct().ToArray();

            var session = new ProfilingSession(
                sessionId,
                resolvedStats,
                maxDurationSeconds,
                target,
                targetIsRemote,
                savePath,
                driveEditorProfiler);

            _sessions[sessionId] = session;
            EnsureTickHooked();

            session.StartEditorProfilerCapture();

            Debug.Log($"[UnityCtl] Profiling session {sessionId} started: stats=[{string.Join(",", resolvedStats)}]" +
                      (maxDurationSeconds.HasValue ? $" max={maxDurationSeconds}s" : ""));

            return new ProfileStartResult
            {
                SessionId = sessionId,
                Stats = resolvedStats,
                StartedAt = session.StartedAtUtc.ToString("o"),
                MaxDurationSeconds = maxDurationSeconds,
                Target = target,
                TargetIsRemote = targetIsRemote,
                SavePath = savePath
            };
        }

        public ProfileStopResult Stop(string sessionId, bool includeSamples, double hitchMultiplier, double? hitchAbsoluteMs)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new InvalidOperationException($"No active profiling session: {sessionId}");

            _sessions.Remove(sessionId);
            UnhookIfIdle();
            return session.Stop(includeSamples, hitchMultiplier, hitchAbsoluteMs);
        }

        public ProfileStatusResult Status()
        {
            var entries = new List<ProfileStatusEntry>();
            foreach (var (id, s) in _sessions)
            {
                entries.Add(new ProfileStatusEntry
                {
                    SessionId = id,
                    StartedAt = s.StartedAtUtc.ToString("o"),
                    ElapsedSeconds = (DateTime.UtcNow - s.StartedAtUtc).TotalSeconds,
                    Frames = s.CurrentFrameCount,
                    Stats = s.StatNames,
                    MaxDurationSeconds = s.MaxDurationSeconds,
                    Target = s.Target,
                    TargetIsRemote = s.TargetIsRemote
                });
            }
            return new ProfileStatusResult { Sessions = entries.ToArray() };
        }

        public ProfileTargetsResult Targets()
        {
            // Unity exposes connected profiler targets via ProfilerDriver.GetAvailableProfilers.
            // We surface the raw list — selection during Start happens via ProfilerDriver.connectedProfiler.
            var result = new List<ProfileTargetInfo>();
            try
            {
                var available = ProfilerDriver.GetAvailableProfilers();
                var current = ProfilerDriver.connectedProfiler;
                foreach (var connId in available)
                {
                    var name = ProfilerDriver.GetConnectionIdentifier(connId);
                    if (string.IsNullOrEmpty(name)) name = $"target-{connId}";
                    result.Add(new ProfileTargetInfo
                    {
                        Id = connId.ToString(),
                        DisplayName = name,
                        Kind = connId == 0 ? "editor" : "player",
                        IsCurrent = connId == current
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCtl] Failed to enumerate profiler targets: {ex.Message}");
            }

            return new ProfileTargetsResult { Targets = result.ToArray() };
        }

        public void SelectTarget(int connectionId)
        {
            ProfilerDriver.connectedProfiler = connectionId;
        }

        public string DirectConnect(string url)
        {
            // Url like "127.0.0.1:54999" for an adb-forwarded Android player.
            ProfilerDriver.DirectURLConnect(url);
            return ProfilerDriver.directConnectionUrl;
        }

        // ---------- explain / hotspots / frame ----------

        /// <summary>
        /// Walks the profiler buffer for a single frame and returns the top-N markers by self time
        /// across all hierarchy depths. The agent uses this to diagnose what made a hitch frame slow:
        /// hitches carry an absoluteFrameIndex that can be passed straight into Explain.
        /// </summary>
        public ProfileExplainResult Explain(int frameIndex, int threadIndex, int topN)
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            if (frameIndex < first || frameIndex > last)
                throw new InvalidOperationException(
                    $"Frame {frameIndex} is outside the profiler buffer [{first}, {last}]. " +
                    "Run a `profile capture` first or pass a frame inside that range.");

            var bucket = new Dictionary<string, MarkerAccum>(StringComparer.Ordinal);
            string threadName = "";
            double frameTimeMs = 0;

            using (var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
            {
                if (raw == null || !raw.valid)
                    throw new InvalidOperationException($"No frame data for frame {frameIndex}, thread {threadIndex}.");
                threadName = raw.threadName ?? "";
                frameTimeMs = raw.frameTimeNs / 1_000_000.0;
            }

            using var hv = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex, threadIndex,
                HierarchyFrameDataView.ViewModes.Default,
                HierarchyFrameDataView.columnSelfTime,
                sortAscending: false);
            if (hv == null || !hv.valid)
                throw new InvalidOperationException($"No hierarchy data for frame {frameIndex}, thread {threadIndex}.");

            AccumulateMarkers(hv, hv.GetRootItemID(), bucket);

            var top = bucket.Values
                .OrderByDescending(m => m.SelfTimeMs)
                .Take(Math.Max(1, topN))
                .Select(m => m.ToEntry())
                .ToArray();

            return new ProfileExplainResult
            {
                FrameIndex = frameIndex,
                ThreadIndex = threadIndex,
                ThreadName = threadName,
                FrameTimeMs = frameTimeMs,
                TopMarkers = top
            };
        }

        /// <summary>
        /// Aggregates self time across a frame range and returns the top-N hottest markers.
        /// Defaults to the entire profiler buffer when start/end are unspecified.
        /// When `rootMarker` is set, accumulation only descends into the named child of the frame
        /// root — useful for filtering to PlayerLoop in editor+play captures (excludes editor
        /// IMGUI / OnGUI / menu-item update callbacks that otherwise drown out gameplay markers).
        /// </summary>
        public ProfileHotspotsResult Hotspots(int? startFrame, int? endFrame, int threadIndex, int topN, string? rootMarker)
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            int s = Math.Max(first, startFrame ?? first);
            int e = Math.Min(last, endFrame ?? last);
            if (e < s)
                throw new InvalidOperationException(
                    $"Empty frame range [{s}, {e}]. Profiler buffer holds [{first}, {last}].");

            var bucket = new Dictionary<string, MarkerAccum>(StringComparer.Ordinal);
            string threadName = "";
            int framesProcessed = 0;
            int framesWithRoot = 0;

            for (int f = s; f <= e; f++)
            {
                using var hv = ProfilerDriver.GetHierarchyFrameDataView(
                    f, threadIndex,
                    HierarchyFrameDataView.ViewModes.Default,
                    HierarchyFrameDataView.columnSelfTime,
                    sortAscending: false);
                if (hv == null || !hv.valid) continue;

                if (string.IsNullOrEmpty(threadName))
                {
                    using var raw = ProfilerDriver.GetRawFrameDataView(f, threadIndex);
                    if (raw != null && raw.valid) threadName = raw.threadName ?? "";
                }

                int walkFrom = hv.GetRootItemID();
                if (!string.IsNullOrEmpty(rootMarker))
                {
                    int? rootChild = ProfilingSession.FindDescendantIdByName(hv, walkFrom, rootMarker);
                    if (rootChild == null) continue; // skip frames that don't have the requested root
                    walkFrom = rootChild.Value;
                    framesWithRoot++;
                }
                AccumulateMarkers(hv, walkFrom, bucket);
                framesProcessed++;
            }

            var top = bucket.Values
                .OrderByDescending(m => m.SelfTimeMs)
                .Take(Math.Max(1, topN))
                .Select(m => m.ToEntry())
                .ToArray();

            return new ProfileHotspotsResult
            {
                StartFrame = s,
                EndFrame = e,
                FrameCount = framesProcessed,
                ThreadIndex = threadIndex,
                ThreadName = threadName,
                TopMarkers = top
            };
        }


        /// <summary>
        /// Hierarchy tree drill-down for a single frame. Prunes nodes below thresholdMs and keeps
        /// the top-N children per parent, so the response stays bounded for deep hierarchies.
        /// Pairs with hitches[].absoluteFrameIndex — pass a hitch's index to see what's inside it.
        /// When `rootMarker` is set, the tree starts at that named subtree (e.g. PlayerLoop) instead
        /// of the frame root — useful for skipping past EditorLoop noise in editor+play captures.
        /// </summary>
        public ProfileFrameResult Frame(int frameIndex, int threadIndex, int maxDepth, double thresholdMs, int topPerNode, string? rootMarker)
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            if (frameIndex < first || frameIndex > last)
                throw new InvalidOperationException(
                    $"Frame {frameIndex} is outside the profiler buffer [{first}, {last}].");

            // MergeSamplesWithTheSameName collapses repeated calls to the same marker into one
            // node per parent, which is what an agent wants for a readable tree.
            using var hv = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex, threadIndex,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime,
                sortAscending: false);
            if (hv == null || !hv.valid)
                throw new InvalidOperationException($"No hierarchy data for frame {frameIndex}, thread {threadIndex}.");

            string threadName = "";
            double frameTimeMs;
            using (var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
            {
                if (raw != null && raw.valid)
                {
                    threadName = raw.threadName ?? "";
                    frameTimeMs = raw.frameTimeNs / 1_000_000.0;
                }
                else
                {
                    var rootId = hv.GetRootItemID();
                    frameTimeMs = hv.GetItemColumnDataAsDouble(rootId, HierarchyFrameDataView.columnTotalTime);
                }
            }

            int treeRoot = hv.GetRootItemID();
            if (!string.IsNullOrEmpty(rootMarker))
            {
                int? scoped = ProfilingSession.FindDescendantIdByName(hv, treeRoot, rootMarker);
                if (scoped == null)
                    throw new InvalidOperationException(
                        $"Marker '{rootMarker}' not found in frame {frameIndex}'s hierarchy. " +
                        $"Try without --root, or pick a different subtree (PlayerLoop / EditorLoop / Application.Tick).");
                treeRoot = scoped.Value;
            }

            int prunedNodes = 0;
            var tree = BuildFrameTree(hv, treeRoot, 0, maxDepth, (float)thresholdMs, topPerNode, ref prunedNodes);

            return new ProfileFrameResult
            {
                FrameIndex = frameIndex,
                ThreadIndex = threadIndex,
                ThreadName = threadName,
                FrameTimeMs = frameTimeMs,
                Depth = maxDepth,
                ThresholdMs = thresholdMs,
                PrunedNodes = prunedNodes,
                Tree = tree ?? Array.Empty<ProfileFrameNode>()
            };
        }

        private static ProfileFrameNode[]? BuildFrameTree(
            HierarchyFrameDataView hv, int id, int depth, int maxDepth,
            float thresholdMs, int topPerNode, ref int pruned)
        {
            if (depth >= maxDepth) return null;

            var children = new List<int>();
            hv.GetItemChildren(id, children);
            if (children.Count == 0) return null;

            // Snapshot all children with their times, then prune.
            var entries = new List<(int Id, string Name, float SelfMs, float TotalMs, int Calls, float GcKb)>(children.Count);
            foreach (var c in children)
            {
                var name = hv.GetItemName(c) ?? "";
                var selfMs = hv.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnSelfTime);
                var totalMs = hv.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnTotalTime);
                var calls = (int)hv.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnCalls);
                var gcKb = hv.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnGcMemory) / 1024f;
                entries.Add((c, name, selfMs, totalMs, calls, gcKb));
            }

            var kept = entries
                .Where(e => e.TotalMs >= thresholdMs)
                .OrderByDescending(e => e.TotalMs)
                .Take(Math.Max(1, topPerNode))
                .ToList();
            pruned += entries.Count - kept.Count;

            var result = new List<ProfileFrameNode>(kept.Count);
            foreach (var n in kept)
            {
                var sub = BuildFrameTree(hv, n.Id, depth + 1, maxDepth, thresholdMs, topPerNode, ref pruned);
                result.Add(new ProfileFrameNode
                {
                    Name = n.Name,
                    SelfMs = Math.Round(n.SelfMs, 3),
                    TotalMs = Math.Round(n.TotalMs, 3),
                    Calls = n.Calls,
                    GcKb = n.GcKb > 0.01f ? Math.Round(n.GcKb, 2) : (double?)null,
                    Children = (sub != null && sub.Length > 0) ? sub : null
                });
            }
            return result.ToArray();
        }

        private sealed class MarkerAccum
        {
            public string Name = "";
            public double SelfTimeMs;
            public int Calls;
            public long GcAllocBytes;

            public ProfileMarkerEntry ToEntry() => new()
            {
                Name = Name,
                SelfTimeMs = SelfTimeMs,
                Calls = Calls,
                GcAllocBytes = GcAllocBytes > 0 ? GcAllocBytes : (long?)null
            };
        }

        private static void AccumulateMarkers(HierarchyFrameDataView hv, int itemId, Dictionary<string, MarkerAccum> bucket)
        {
            // Walk every node in the tree; aggregate self time / calls / GC alloc by marker name.
            // Self time is additive across nesting (each call's self contribution is disjoint from
            // its children's), so summing across the tree gives the right per-marker total. Total
            // time is intentionally not aggregated — it would double-count along the parent chain.
            var children = new List<int>();
            hv.GetItemChildren(itemId, children);
            foreach (var c in children)
            {
                var name = hv.GetItemName(c);
                if (string.IsNullOrEmpty(name)) { AccumulateMarkers(hv, c, bucket); continue; }

                if (!bucket.TryGetValue(name, out var acc))
                {
                    acc = new MarkerAccum { Name = name };
                    bucket[name] = acc;
                }
                // Unity 6 only exposes Single/Double/Float column readers — long would overflow for
                // very large GC allocs, but Double has 53-bit mantissa which covers 8 PiB cleanly.
                acc.SelfTimeMs += hv.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnSelfTime);
                acc.Calls += (int)hv.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnCalls);
                acc.GcAllocBytes += (long)hv.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnGcMemory);

                AccumulateMarkers(hv, c, bucket);
            }
        }

        // ---------- mark ----------

        /// <summary>
        /// Wraps a user-provided C# expression in a ProfilerMarker + Stopwatch + per-thread GC
        /// allocation accounting, runs it `repeat` times, and returns timing percentiles + GC bytes.
        /// Re-uses the editor-side ScriptExecutor so the expression has access to the full editor
        /// API surface and any runtime types the agent has loaded.
        /// </summary>
        public ProfileMarkResult Mark(string expression, string markerName, int repeat)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new InvalidOperationException("Expression is required.");
            if (repeat < 1) repeat = 1;
            if (string.IsNullOrWhiteSpace(markerName)) markerName = "unityctl.mark";

            // String literals must escape via Newtonsoft JSON — same trick fire/profile/mark.cs uses
            // to safely embed user-supplied marker name inside generated source.
            var nameLit = Newtonsoft.Json.JsonConvert.ToString(markerName);

            var code = $@"
using System;
using System.Diagnostics;
using System.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public class Marked
{{
    public static object Run()
    {{
        var marker = new ProfilerMarker({nameLit});
        int repeat = {repeat};
        var samples = new double[repeat];
        long totalGcBytes = 0;
        object lastResult = null;
        for (int i = 0; i < repeat; i++)
        {{
            long gc0 = System.GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            marker.Begin();
            try {{ lastResult = ({expression}); }}
            finally {{ marker.End(); sw.Stop(); }}
            samples[i] = sw.Elapsed.TotalMilliseconds;
            totalGcBytes += System.GC.GetAllocatedBytesForCurrentThread() - gc0;
        }}
        var sorted = samples.OrderBy(x => x).ToArray();
        double pct(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Round(q * (sorted.Length - 1)))];
        return new
        {{
            name = {nameLit},
            repeat,
            meanMs = Math.Round(samples.Average(), 4),
            minMs = Math.Round(sorted[0], 4),
            maxMs = Math.Round(sorted[sorted.Length - 1], 4),
            p50Ms = Math.Round(pct(0.50), 4),
            p95Ms = Math.Round(pct(0.95), 4),
            gcBytes = totalGcBytes,
            gcBytesPerCall = totalGcBytes / repeat,
            result = lastResult?.ToString()
        }};
    }}
}}
";

            var res = ScriptExecutor.Execute(code, "Marked", "Run", null);
            if (!res.Success)
            {
                var detail = res.Error ?? "";
                if (res.Diagnostics != null && res.Diagnostics.Length > 0)
                    detail += "\nDiagnostics:\n  " + string.Join("\n  ", res.Diagnostics);
                throw new InvalidOperationException($"profile mark failed to compile/run expression: {detail}");
            }

            var json = res.Result ?? "null";
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
            return new ProfileMarkResult
            {
                Name = (string)parsed["name"]!,
                Repeat = (int)parsed["repeat"]!,
                MeanMs = (double)parsed["meanMs"]!,
                MinMs = (double)parsed["minMs"]!,
                MaxMs = (double)parsed["maxMs"]!,
                P50Ms = (double)parsed["p50Ms"]!,
                P95Ms = (double)parsed["p95Ms"]!,
                GcBytes = (long)parsed["gcBytes"]!,
                GcBytesPerCall = (long)parsed["gcBytesPerCall"]!,
                Result = parsed["result"]?.Type == Newtonsoft.Json.Linq.JTokenType.Null ? null : (string?)parsed["result"]
            };
        }

        public ProfileSnapshotResult MemorySnapshot(string outputPath)
        {
            // Use Memory Profiler package when available; otherwise return a clear error.
            // The dependency is loaded reflectively to avoid forcing the package on users.
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Unity.MemoryProfiler.Runtime" || a.GetName().Name == "Unity.MemoryProfiler");
            if (asm == null)
            {
                throw new InvalidOperationException(
                    "Memory Profiler package not found. Install com.unity.memoryprofiler via Package Manager.");
            }

            var mpType = asm.GetType("Unity.Profiling.Memory.MemoryProfiler");
            if (mpType == null)
            {
                throw new InvalidOperationException("Unity.Profiling.Memory.MemoryProfiler type not found in loaded assemblies.");
            }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var done = false;
            string? finalPath = null;
            Exception? captureError = null;

            // Find TakeSnapshot(string, Action<string,bool>, CaptureFlags) overload.
            var captureFlagsType = asm.GetType("Unity.Profiling.Memory.CaptureFlags");
            var defaultFlags = captureFlagsType != null
                ? Enum.Parse(captureFlagsType, "ManagedObjects,NativeObjects,NativeAllocations")
                : null;

            var actionType = typeof(Action<,>).MakeGenericType(typeof(string), typeof(bool));
            var takeSnapshot = mpType.GetMethods()
                .FirstOrDefault(m => m.Name == "TakeSnapshot" && m.GetParameters().Length == 3 &&
                                     m.GetParameters()[0].ParameterType == typeof(string));
            if (takeSnapshot == null)
                throw new InvalidOperationException("TakeSnapshot(string, callback, flags) overload not found.");

            Action<string, bool> callback = (p, ok) =>
            {
                done = true;
                if (ok) finalPath = p;
                else captureError = new InvalidOperationException("Memory snapshot capture failed.");
            };

            takeSnapshot.Invoke(null, new object?[] { outputPath, callback, defaultFlags });

            // Pump until callback fires or timeout. The capture is asynchronous on the editor's main loop.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!done && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(50);
            }

            if (!done) throw new TimeoutException("Memory snapshot capture timed out after 120s.");
            if (captureError != null) throw captureError;

            var path = finalPath ?? outputPath;
            var size = File.Exists(path) ? new FileInfo(path).Length : 0L;
            return new ProfileSnapshotResult { Path = path, SizeBytes = size };
        }

        private void EnsureTickHooked()
        {
            if (_tickHooked) return;
            EditorApplication.update += Tick;
            _tickHooked = true;
        }

        private void UnhookIfIdle()
        {
            if (_sessions.Count > 0 || !_tickHooked) return;
            EditorApplication.update -= Tick;
            _tickHooked = false;
        }

        // The tick is now only used to enforce max-duration auto-stop. Per-frame counter
        // sampling moved into Stop() (post-hoc RawFrameDataView walk), since editor ticks
        // don't line up with rendered frames.
        private void Tick()
        {
            if (_sessions.Count == 0) return;

            string[] ids;
            {
                ids = new string[_sessions.Count];
                int i = 0;
                foreach (var k in _sessions.Keys) ids[i++] = k;
            }

            foreach (var id in ids)
            {
                if (!_sessions.TryGetValue(id, out var s)) continue;

                if (s.MaxDurationSeconds.HasValue &&
                    (DateTime.UtcNow - s.StartedAtUtc).TotalSeconds >= s.MaxDurationSeconds.Value)
                {
                    Debug.Log($"[UnityCtl] Profiling session {id} reached max-duration; auto-stopping.");
                    var cached = s.Stop(includeSamples: false, hitchMultiplier: 2.0, hitchAbsoluteMs: null);
                    _sessions.Remove(id);
                    _autoStopped[id] = cached;
                }
            }

            UnhookIfIdle();
        }

        private readonly Dictionary<string, ProfileStopResult> _autoStopped = new();

        public bool TryGetAutoStopped(string sessionId, out ProfileStopResult result) =>
            _autoStopped.TryGetValue(sessionId, out result!);

        public bool ConsumeAutoStopped(string sessionId, out ProfileStopResult result)
        {
            if (_autoStopped.TryGetValue(sessionId, out result!))
            {
                _autoStopped.Remove(sessionId);
                return true;
            }
            return false;
        }
    }
}
