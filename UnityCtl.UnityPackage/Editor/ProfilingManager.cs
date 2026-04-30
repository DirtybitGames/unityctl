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
    /// Per-session profiling state. Owns a set of ProfilerRecorders sampling Unity's built-in
    /// counters once per editor frame; collects per-frame samples for statistical summary on stop.
    /// Optionally drives the Editor's profiler buffer (for ProfilerDriver.SaveProfile).
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

        // Local-mode state (ProfilerRecorder per stat).
        private readonly List<(ProfilerRecorder Recorder, string Name, ProfilerMarkerDataUnit Unit)> _recorders;
        private readonly List<List<double>> _samples;
        private int _frameCount;

        // Remote-mode state (read after stop from ProfilerDriver buffer).
        private readonly bool _isRemoteMode;
        private int _remoteStartFrame = -1;
        private readonly Dictionary<string, ProfilerMarkerDataUnit> _statNameToUnit = new();

        // Frame-time tracking for hitch detection (always sampled). Local only.
        private readonly ProfilerRecorder _frameTimeRecorder;
        private readonly List<double> _frameTimesMs = new();

        public int FrameCount => _frameCount;
        public bool IsActive { get; private set; }

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
            _isRemoteMode = targetIsRemote;

            _recorders = new List<(ProfilerRecorder, string, ProfilerMarkerDataUnit)>(statNames.Length);
            _samples = new List<List<double>>(statNames.Length);
            for (int i = 0; i < statNames.Length; i++)
            {
                _samples.Add(new List<double>(1024));
            }

            // Pre-compute the unit for every requested stat from the LOCAL Editor's known counters
            // (built-in counters share names + units across local and remote). Used to convert
            // raw counter values (e.g. ns → ms) consistently for both local and remote modes.
            for (int i = 0; i < statNames.Length; i++)
            {
                var (_, unit, _) = FindHandleByName(statNames[i]);
                _statNameToUnit[statNames[i]] = unit;
            }

            if (_isRemoteMode)
            {
                // Remote mode: don't sample per-frame via ProfilerRecorder (local-only).
                // Instead, drive the Editor profiler buffer to capture remote frames; at Stop()
                // we'll walk frames with RawFrameDataView and pull counter values per stat.
                ProfilerDriver.profileEditor = false;
                ProfilerDriver.ClearAllFrames();
                ProfilerDriver.enabled = true;
                _remoteStartFrame = ProfilerDriver.lastFrameIndex + 1;
            }
            else
            {
                // Local mode: ProfilerRecorder per stat, sampled each editor tick.
                for (int i = 0; i < statNames.Length; i++)
                {
                    var name = statNames[i];
                    var (handle, unit, category) = FindHandleByName(name);
                    if (!handle.Valid)
                    {
                        _recorders.Add((default, name, ProfilerMarkerDataUnit.Undefined));
                        continue;
                    }
                    var rec = ProfilerRecorder.StartNew(category, name, capacity: 1);
                    _recorders.Add((rec, name, unit));
                }

                // Always-on frame time for hitches (local only — remote uses RawFrameDataView).
                var (ftHandle, _, ftCategory) = FindHandleByName("Main Thread");
                _frameTimeRecorder = ftHandle.Valid
                    ? ProfilerRecorder.StartNew(ftCategory, "Main Thread", capacity: 1)
                    : default;
            }

            IsActive = true;
        }

        public void StartEditorProfilerCapture()
        {
            // Remote mode already enabled the driver in the constructor. Local mode only enables
            // the driver when --save was requested (drives the editor's profiler buffer alongside
            // ProfilerRecorder sampling so SaveProfile has data).
            if (_isRemoteMode || !DriveEditorProfiler) return;
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.profileEditor = true;
            ProfilerDriver.enabled = true;
        }

        public void TickFrame()
        {
            if (!IsActive) return;
            _frameCount++;

            // Remote mode reads frames at Stop() — nothing to sample per editor tick.
            if (_isRemoteMode) return;

            for (int i = 0; i < _recorders.Count; i++)
            {
                var rec = _recorders[i].Recorder;
                if (!rec.Valid) { _samples[i].Add(double.NaN); continue; }
                _samples[i].Add(rec.LastValue);
            }

            if (_frameTimeRecorder.Valid)
            {
                _frameTimesMs.Add(_frameTimeRecorder.LastValue / 1_000_000.0);
            }
        }

        public ProfileStopResult Stop(bool includeSamples, double hitchMultiplier, double? hitchAbsoluteMs)
        {
            IsActive = false;

            if (_isRemoteMode)
                return StopRemote(includeSamples, hitchMultiplier, hitchAbsoluteMs);

            return StopLocal(includeSamples, hitchMultiplier, hitchAbsoluteMs);
        }

        private ProfileStopResult StopLocal(bool includeSamples, double hitchMultiplier, double? hitchAbsoluteMs)
        {
            if (DriveEditorProfiler)
            {
                ProfilerDriver.enabled = false;
                TrySaveProfile();
            }

            var summaries = new List<ProfileStatSummary>(_recorders.Count);
            for (int i = 0; i < _recorders.Count; i++)
            {
                var entry = _recorders[i];
                var samples = _samples[i];
                var converted = new double[samples.Count];
                for (int j = 0; j < samples.Count; j++)
                {
                    converted[j] = ConvertSample(samples[j], entry.Unit);
                }
                summaries.Add(BuildSummary(entry.Name, UnitDisplay(entry.Unit), converted, includeSamples));
                if (entry.Recorder.Valid) entry.Recorder.Dispose();
            }

            var threshold = ComputeHitchThreshold(_frameTimesMs, hitchMultiplier, hitchAbsoluteMs);
            var hitches = new List<ProfileHitch>();
            for (int i = 0; i < _frameTimesMs.Count; i++)
            {
                if (_frameTimesMs[i] > threshold)
                    hitches.Add(new ProfileHitch { FrameIndex = i, FrameTimeMs = _frameTimesMs[i] });
            }

            if (_frameTimeRecorder.Valid) _frameTimeRecorder.Dispose();

            return new ProfileStopResult
            {
                SessionId = Id,
                DurationSeconds = (DateTime.UtcNow - StartedAtUtc).TotalSeconds,
                Frames = _frameCount,
                Summaries = summaries.ToArray(),
                Hitches = hitches.Count > 0 ? hitches.ToArray() : null,
                SavedPath = !string.IsNullOrEmpty(SavePath) ? SavePath : null,
                Target = Target,
                TargetIsRemote = TargetIsRemote
            };
        }

        private ProfileStopResult StopRemote(bool includeSamples, double hitchMultiplier, double? hitchAbsoluteMs)
        {
            // Snapshot the end frame before disabling the driver so frames received in the same
            // editor tick aren't dropped.
            int endFrame = ProfilerDriver.lastFrameIndex;
            ProfilerDriver.enabled = false;

            // Save .data first if requested — buffer still has all the remote frames.
            TrySaveProfile();

            int startFrame = Math.Max(_remoteStartFrame, ProfilerDriver.firstFrameIndex);
            int totalFrames = endFrame >= startFrame ? endFrame - startFrame + 1 : 0;

            var perStat = new double[StatNames.Length][];
            for (int i = 0; i < StatNames.Length; i++) perStat[i] = new double[totalFrames];
            var frameTimesMs = new double[totalFrames];

            // Resolve "Main Thread" frame-time stat name. CPU Main Thread Frame Time is reported
            // in ns; remote players expose it the same way.
            const string FrameTimeStat = "CPU Main Thread Frame Time";

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
                var ftRaw = ReadCounter(view, FrameTimeStat);
                frameTimesMs[idx] = double.IsNaN(ftRaw) ? double.NaN : ftRaw / 1_000_000.0;
            }

            // Convert per-stat values according to local-known units.
            var summaries = new List<ProfileStatSummary>(StatNames.Length);
            for (int i = 0; i < StatNames.Length; i++)
            {
                var unit = _statNameToUnit.TryGetValue(StatNames[i], out var u) ? u : ProfilerMarkerDataUnit.Undefined;
                var converted = new double[perStat[i].Length];
                for (int j = 0; j < perStat[i].Length; j++)
                    converted[j] = ConvertSample(perStat[i][j], unit);
                summaries.Add(BuildSummary(StatNames[i], UnitDisplay(unit), converted, includeSamples));
            }

            // Hitch detection over the converted (ms) frame-time series.
            var ftList = new List<double>(frameTimesMs.Length);
            foreach (var v in frameTimesMs) if (!double.IsNaN(v)) ftList.Add(v);
            var threshold = ComputeHitchThreshold(ftList, hitchMultiplier, hitchAbsoluteMs);
            var hitches = new List<ProfileHitch>();
            for (int i = 0; i < frameTimesMs.Length; i++)
            {
                if (!double.IsNaN(frameTimesMs[i]) && frameTimesMs[i] > threshold)
                    hitches.Add(new ProfileHitch { FrameIndex = i, FrameTimeMs = frameTimesMs[i] });
            }

            return new ProfileStopResult
            {
                SessionId = Id,
                DurationSeconds = (DateTime.UtcNow - StartedAtUtc).TotalSeconds,
                Frames = totalFrames,
                Summaries = summaries.ToArray(),
                Hitches = hitches.Count > 0 ? hitches.ToArray() : null,
                SavedPath = !string.IsNullOrEmpty(SavePath) ? SavePath : null,
                Target = Target,
                TargetIsRemote = TargetIsRemote
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
            for (int i = 0; i < _recorders.Count; i++)
            {
                if (_recorders[i].Recorder.Valid) _recorders[i].Recorder.Dispose();
            }
            if (_frameTimeRecorder.Valid) _frameTimeRecorder.Dispose();
            if (DriveEditorProfiler || _isRemoteMode)
            {
                try { ProfilerDriver.enabled = false; } catch { }
            }
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

        private static string UnitToString(ProfilerMarkerDataUnit unit) => unit.ToString();

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
    /// Singleton coordinator. Manages active profiling sessions, sampling them once per editor frame,
    /// and serves list-stats / start / stop / status / snapshot / targets RPCs.
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
                    Frames = s.FrameCount,
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

        private void Tick()
        {
            if (_sessions.Count == 0) return;

            // Snapshot keys to allow modification during iteration (e.g., auto-stop).
            string[] ids;
            {
                ids = new string[_sessions.Count];
                int i = 0;
                foreach (var k in _sessions.Keys) ids[i++] = k;
            }

            foreach (var id in ids)
            {
                if (!_sessions.TryGetValue(id, out var s)) continue;
                s.TickFrame();

                if (s.MaxDurationSeconds.HasValue &&
                    (DateTime.UtcNow - s.StartedAtUtc).TotalSeconds >= s.MaxDurationSeconds.Value)
                {
                    Debug.Log($"[UnityCtl] Profiling session {id} reached max-duration; auto-stopping.");
                    // Auto-stop — caller will pull result via 'profile stop <id>' (returns the cached result).
                    // For simplicity, leave the session in place so 'stop' still works; just disable sampling.
                    // We model this by stopping internally and re-inserting a finalised wrapper is overkill,
                    // so instead we mark the session inactive — TickFrame guards against double-counting.
                    // The caller's stop() will get the right summaries because samples were collected up to now.
                    // To avoid sampling forever after the cap, dispose recorders now via a controlled stop and
                    // cache the result so 'stop' returns it.
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
