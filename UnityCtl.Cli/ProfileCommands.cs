using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class ProfileCommands
{
    public static Command CreateCommand()
    {
        var profileCommand = new Command("profile", "Profiler operations: capture frame stats, detect hitches, save full profiler data");

        profileCommand.AddCommand(BuildListStatsCommand());
        profileCommand.AddCommand(BuildStartCommand());
        profileCommand.AddCommand(BuildStopCommand());
        profileCommand.AddCommand(BuildStatusCommand());
        profileCommand.AddCommand(BuildCaptureCommand());
        profileCommand.AddCommand(BuildVitalsCommand());
        profileCommand.AddCommand(BuildAssertCommand());
        profileCommand.AddCommand(BuildSnapshotCommand());
        profileCommand.AddCommand(BuildTargetsCommand());
        profileCommand.AddCommand(BuildConnectCommand());
        profileCommand.AddCommand(BuildExplainCommand());
        profileCommand.AddCommand(BuildHotspotsCommand());
        profileCommand.AddCommand(BuildFrameCommand());
        profileCommand.AddCommand(BuildMarkCommand());

        return profileCommand;
    }

    // ---------- list-stats ----------

    private static Command BuildListStatsCommand()
    {
        var cmd = new Command("list-stats", "List available profiler stats (counters)");
        var category = new Option<string?>("--category", "Filter by category (e.g., Render, Memory, Internal)");
        cmd.AddOption(category);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "category", ctx.ParseResult.GetValueForOption(category) }
            };
            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileListStats, args, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }

            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1;
                return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileListStatsResult>(resp.Result);
            if (result == null) return;

            Console.WriteLine($"{result.Count} stats available");
            string? lastCategory = null;
            foreach (var s in result.Stats)
            {
                if (s.Category != lastCategory)
                {
                    Console.WriteLine();
                    Console.WriteLine($"[{s.Category}]");
                    lastCategory = s.Category;
                }
                Console.WriteLine($"  {s.Name,-40} {s.Unit}  ({s.DataType})");
            }
        });

        return cmd;
    }

    // ---------- start ----------

    private static Command BuildStartCommand()
    {
        var cmd = new Command("start", "Start a profiling session (returns sessionId)");
        var stats = new Option<string?>("--stats",
            "Comma-separated stats or aliases (e.g., main,gpu,drawcalls,gc-alloc). Default: vitals.");
        var maxDuration = new Option<double?>("--max-duration",
            "Auto-stop after N seconds if not explicitly stopped (safety cap)");
        var target = new Option<string?>("--target",
            "Target id from 'profile targets' (default: editor)");
        var savePath = new Option<string?>("--save",
            "Also drive the Editor profiler buffer and save a .data file (Phase 2)");
        cmd.AddOption(stats);
        cmd.AddOption(maxDuration);
        cmd.AddOption(target);
        cmd.AddOption(savePath);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            await HandleStart(ctx, stats, maxDuration, target, savePath);
        });
        return cmd;
    }

    private static async Task HandleStart(
        InvocationContext ctx,
        Option<string?> statsOpt,
        Option<double?> maxOpt,
        Option<string?> targetOpt,
        Option<string?> saveOpt)
    {
        var client = MakeClient(ctx);
        if (client == null) { ctx.ExitCode = 1; return; }

        var statsRaw = ctx.ParseResult.GetValueForOption(statsOpt);
        string[]? statsList = null;
        if (!string.IsNullOrWhiteSpace(statsRaw))
            statsList = statsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();

        var args = new Dictionary<string, object?>
        {
            { "stats", statsList },
            { "maxDurationSeconds", ctx.ParseResult.GetValueForOption(maxOpt) },
            { "target", ctx.ParseResult.GetValueForOption(targetOpt) },
            { "savePath", ResolveSavePathForUnity(ctx, ctx.ParseResult.GetValueForOption(saveOpt)) }
        };

        var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileStart, args, ContextHelper.GetTimeout(ctx));
        if (resp == null) { ctx.ExitCode = 1; return; }

        if (resp.Status == ResponseStatus.Error)
        {
            Console.Error.WriteLine($"Error: {resp.Error?.Message}");
            ctx.ExitCode = 1;
            return;
        }

        if (ContextHelper.GetJson(ctx))
        {
            Console.WriteLine(JsonHelper.Serialize(resp.Result));
            return;
        }

        var result = Deser<ProfileStartResult>(resp.Result);
        if (result == null) return;
        Console.WriteLine($"Started profiling session {result.SessionId}");
        Console.WriteLine($"  stats: {string.Join(", ", result.Stats)}");
        if (result.MaxDurationSeconds.HasValue)
            Console.WriteLine($"  max-duration: {result.MaxDurationSeconds}s (auto-stop)");
        if (!string.IsNullOrEmpty(result.SavePath))
            Console.WriteLine($"  save: {result.SavePath}");
    }

    // ---------- stop ----------

    private static Command BuildStopCommand()
    {
        var cmd = new Command("stop", "Stop a profiling session and return the summary");
        var sessionArg = new Argument<string>("session-id", "Session id from 'profile start'");
        var includeSamples = new Option<bool>("--include-samples", "Include per-frame sample arrays in output");
        var hitchMultiplier = new Option<double?>("--hitch-multiplier", "Hitch threshold = median × this (default 2.0)");
        var hitchAbsoluteMs = new Option<double?>("--hitch-ms", "Absolute hitch threshold in ms (overrides multiplier)");

        cmd.AddArgument(sessionArg);
        cmd.AddOption(includeSamples);
        cmd.AddOption(hitchMultiplier);
        cmd.AddOption(hitchAbsoluteMs);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "sessionId", ctx.ParseResult.GetValueForArgument(sessionArg) },
                { "includeSamples", ctx.ParseResult.GetValueForOption(includeSamples) },
                { "hitchMultiplier", ctx.ParseResult.GetValueForOption(hitchMultiplier) },
                { "hitchAbsoluteMs", ctx.ParseResult.GetValueForOption(hitchAbsoluteMs) }
            };

            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileStop, args, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }

            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1;
                return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileStopResult>(resp.Result);
            if (result == null) return;
            PrintStopHuman(result);
        });

        return cmd;
    }

    // ---------- status ----------

    private static Command BuildStatusCommand()
    {
        var cmd = new Command("status", "List active profiling sessions");
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileStatus, null, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }

            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1;
                return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileStatusResult>(resp.Result);
            if (result == null) return;
            if (result.Sessions.Length == 0)
            {
                Console.WriteLine("No active profiling sessions");
                return;
            }
            foreach (var s in result.Sessions)
            {
                Console.WriteLine($"{s.SessionId}  elapsed={s.ElapsedSeconds:F1}s  frames={s.Frames}  stats=[{string.Join(",", s.Stats)}]" +
                    (s.MaxDurationSeconds.HasValue ? $"  max={s.MaxDurationSeconds}s" : ""));
            }
        });
        return cmd;
    }

    // ---------- capture (start + sleep + stop) ----------

    private static Command BuildCaptureCommand()
    {
        var cmd = new Command("capture", "One-shot capture: start, wait, stop. Sugar for start+stop.");
        var duration = new Option<double>("--duration", () => 5.0, "Duration in seconds");
        var stats = new Option<string?>("--stats", "Comma-separated stats or aliases");
        var target = new Option<string?>("--target", "Target id (default: editor)");
        var save = new Option<string?>("--save", "Also save a .data file (drives Editor profiler buffer)");
        var includeSamples = new Option<bool>("--include-samples", "Include per-frame sample arrays");
        var hitchMs = new Option<double?>("--hitch-ms", "Absolute hitch threshold in ms");
        cmd.AddOption(duration);
        cmd.AddOption(stats);
        cmd.AddOption(target);
        cmd.AddOption(save);
        cmd.AddOption(includeSamples);
        cmd.AddOption(hitchMs);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var statsRaw = ctx.ParseResult.GetValueForOption(stats);
            string[]? statsList = null;
            if (!string.IsNullOrWhiteSpace(statsRaw))
                statsList = statsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();

            var dur = ctx.ParseResult.GetValueForOption(duration);
            var savePath = ResolveSavePathForUnity(ctx, ctx.ParseResult.GetValueForOption(save));

            var startArgs = new Dictionary<string, object?>
            {
                { "stats", statsList },
                { "maxDurationSeconds", dur + 5 }, // safety cap > duration
                { "target", ctx.ParseResult.GetValueForOption(target) },
                { "savePath", savePath }
            };

            var startResp = await client.SendCommandAsync(UnityCtlCommands.ProfileStart, startArgs, ContextHelper.GetTimeout(ctx));
            if (startResp == null || startResp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error starting capture: {startResp?.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            var startResult = Deser<ProfileStartResult>(startResp.Result);
            if (startResult == null) { ctx.ExitCode = 1; return; }

            await Task.Delay(TimeSpan.FromSeconds(dur));

            var stopArgs = new Dictionary<string, object?>
            {
                { "sessionId", startResult.SessionId },
                { "includeSamples", ctx.ParseResult.GetValueForOption(includeSamples) },
                { "hitchAbsoluteMs", ctx.ParseResult.GetValueForOption(hitchMs) }
            };
            var stopResp = await client.SendCommandAsync(UnityCtlCommands.ProfileStop, stopArgs, ContextHelper.GetTimeout(ctx));
            if (stopResp == null || stopResp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error stopping capture: {stopResp?.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(stopResp.Result));
                return;
            }

            var stopResult = Deser<ProfileStopResult>(stopResp.Result);
            if (stopResult != null) PrintStopHuman(stopResult);
        });
        return cmd;
    }

    // ---------- vitals ----------

    private static Command BuildVitalsCommand()
    {
        var cmd = new Command("vitals", "Curated 5-number perf report (avg/p99 frame, GC alloc, draw calls, GPU)");
        var duration = new Option<double>("--duration", () => 3.0, "Sample window in seconds");
        var target = new Option<string?>("--target", "Target id (default: editor)");
        cmd.AddOption(duration);
        cmd.AddOption(target);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var dur = ctx.ParseResult.GetValueForOption(duration);
            var startArgs = new Dictionary<string, object?>
            {
                { "stats", new[] { "CPU Main Thread Frame Time", "CPU Render Thread Frame Time", "GPU Frame Time",
                                   "Draw Calls Count", "GC Allocated In Frame", "System Used Memory" } },
                { "maxDurationSeconds", dur + 5 },
                { "target", ctx.ParseResult.GetValueForOption(target) }
            };

            var startResp = await client.SendCommandAsync(UnityCtlCommands.ProfileStart, startArgs, ContextHelper.GetTimeout(ctx));
            if (startResp == null || startResp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error starting vitals: {startResp?.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            var startResult = Deser<ProfileStartResult>(startResp.Result);
            if (startResult == null) { ctx.ExitCode = 1; return; }

            await Task.Delay(TimeSpan.FromSeconds(dur));

            var stopResp = await client.SendCommandAsync(UnityCtlCommands.ProfileStop,
                new Dictionary<string, object?> { { "sessionId", startResult.SessionId } },
                ContextHelper.GetTimeout(ctx));
            if (stopResp == null || stopResp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error stopping vitals: {stopResp?.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(stopResp.Result));
                return;
            }

            var stopResult = Deser<ProfileStopResult>(stopResp.Result);
            if (stopResult == null) return;
            PrintVitalsHuman(stopResult);
        });
        return cmd;
    }

    // ---------- assert ----------

    private static Command BuildAssertCommand()
    {
        var cmd = new Command("assert", "Run a short capture and assert thresholds. Non-zero exit on failure.");
        var duration = new Option<double>("--duration", () => 3.0, "Sample window in seconds");
        var p99Frame = new Option<double?>("--p99-frame-ms", "Fail if p99 frame time (ms) >= this");
        var avgFrame = new Option<double?>("--avg-frame-ms", "Fail if avg frame time (ms) >= this");
        var maxGcAlloc = new Option<double?>("--gc-alloc-per-frame", "Fail if avg GC alloc/frame (bytes) >= this");
        var maxDrawCalls = new Option<double?>("--draw-calls", "Fail if avg draw calls >= this");
        var maxHitches = new Option<int?>("--max-hitches", "Fail if hitches > this");
        var hitchMs = new Option<double>("--hitch-ms", () => 33.3, "Hitch threshold in ms (default 33.3)");
        cmd.AddOption(duration);
        cmd.AddOption(p99Frame);
        cmd.AddOption(avgFrame);
        cmd.AddOption(maxGcAlloc);
        cmd.AddOption(maxDrawCalls);
        cmd.AddOption(maxHitches);
        cmd.AddOption(hitchMs);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var dur = ctx.ParseResult.GetValueForOption(duration);
            var startArgs = new Dictionary<string, object?>
            {
                { "stats", new[] { "CPU Main Thread Frame Time", "GPU Frame Time", "Draw Calls Count", "GC Allocated In Frame" } },
                { "maxDurationSeconds", dur + 5 }
            };
            var startResp = await client.SendCommandAsync(UnityCtlCommands.ProfileStart, startArgs, ContextHelper.GetTimeout(ctx));
            if (startResp == null || startResp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error starting assert: {startResp?.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            var startResult = Deser<ProfileStartResult>(startResp.Result);
            if (startResult == null) { ctx.ExitCode = 1; return; }

            await Task.Delay(TimeSpan.FromSeconds(dur));

            var stopResp = await client.SendCommandAsync(UnityCtlCommands.ProfileStop,
                new Dictionary<string, object?>
                {
                    { "sessionId", startResult.SessionId },
                    { "hitchAbsoluteMs", ctx.ParseResult.GetValueForOption(hitchMs) }
                }, ContextHelper.GetTimeout(ctx));
            if (stopResp == null || stopResp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error stopping assert: {stopResp?.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            var stopResult = Deser<ProfileStopResult>(stopResp.Result);
            if (stopResult == null) { ctx.ExitCode = 1; return; }

            var failures = new List<string>();
            var mainThread = stopResult.Summaries.FirstOrDefault(s => s.Name == "CPU Main Thread Frame Time");
            var gcAlloc = stopResult.Summaries.FirstOrDefault(s => s.Name == "GC Allocated In Frame");
            var drawCalls = stopResult.Summaries.FirstOrDefault(s => s.Name == "Draw Calls Count");

            var p99Limit = ctx.ParseResult.GetValueForOption(p99Frame);
            var avgLimit = ctx.ParseResult.GetValueForOption(avgFrame);
            var gcLimit = ctx.ParseResult.GetValueForOption(maxGcAlloc);
            var dcLimit = ctx.ParseResult.GetValueForOption(maxDrawCalls);
            var hitchLimit = ctx.ParseResult.GetValueForOption(maxHitches);

            if (p99Limit.HasValue && mainThread != null && mainThread.P99 >= p99Limit.Value)
                failures.Add($"p99 frame time {mainThread.P99:F2}ms >= {p99Limit:F2}ms");
            if (avgLimit.HasValue && mainThread != null && mainThread.Avg >= avgLimit.Value)
                failures.Add($"avg frame time {mainThread.Avg:F2}ms >= {avgLimit:F2}ms");
            if (gcLimit.HasValue && gcAlloc != null && gcAlloc.Avg >= gcLimit.Value)
                failures.Add($"avg GC alloc/frame {gcAlloc.Avg:F0} bytes >= {gcLimit:F0} bytes");
            if (dcLimit.HasValue && drawCalls != null && drawCalls.Avg >= dcLimit.Value)
                failures.Add($"avg draw calls {drawCalls.Avg:F0} >= {dcLimit:F0}");
            var hitchCount = stopResult.Hitches?.Length ?? 0;
            if (hitchLimit.HasValue && hitchCount > hitchLimit.Value)
                failures.Add($"hitches {hitchCount} > {hitchLimit.Value}");

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    passed = failures.Count == 0,
                    failures,
                    summary = stopResult
                }));
            }
            else
            {
                PrintStopHuman(stopResult);
                if (failures.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("PASS — all thresholds met.");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("FAIL:");
                    foreach (var f in failures) Console.WriteLine($"  - {f}");
                }
            }

            if (failures.Count > 0) ctx.ExitCode = 1;
        });
        return cmd;
    }

    // ---------- snapshot (memory) ----------

    private static Command BuildSnapshotCommand()
    {
        var cmd = new Command("snapshot", "Capture a memory snapshot (.snap) via Memory Profiler package");
        var output = new Option<string?>("--output", "Output path (default: MemoryCaptures/<timestamp>.snap, project-relative)");
        cmd.AddOption(output);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "output", ResolveSavePathForUnity(ctx, ctx.ParseResult.GetValueForOption(output)) }
            };

            // Snapshots can take a while.
            var timeout = ContextHelper.GetTimeout(ctx) ?? 180;
            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileSnapshot, args, timeout);
            if (resp == null) { ctx.ExitCode = 1; return; }
            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }
            var result = Deser<ProfileSnapshotResult>(resp.Result);
            if (result != null)
                Console.WriteLine($"Saved snapshot: {ContextHelper.FormatPath(result.Path)} ({result.SizeBytes / 1024.0 / 1024.0:F1} MB)");
        });
        return cmd;
    }

    // ---------- targets ----------

    private static Command BuildTargetsCommand()
    {
        var cmd = new Command("targets", "List available profiler targets (editor + connected players)");
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileTargets, null, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }
            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileTargetsResult>(resp.Result);
            if (result == null) return;
            if (result.Targets.Length == 0)
            {
                Console.WriteLine("No profiler targets visible (editor only).");
                return;
            }

            Console.WriteLine("ID    KIND      CURRENT  NAME");
            foreach (var t in result.Targets)
                Console.WriteLine($"{t.Id,-5} {t.Kind,-9} {(t.IsCurrent ? "*" : " ")}        {t.DisplayName}");
        });
        return cmd;
    }

    // ---------- connect (DirectURLConnect) ----------

    private static Command BuildConnectCommand()
    {
        var cmd = new Command("connect", "Connect Editor profiler to a remote target by URL (e.g. 127.0.0.1:54999 for adb-forwarded Android player)");
        var urlArg = new Argument<string>("url", "Target URL (host:port). For Android over USB, set up `adb forward tcp:54999 tcp:<player-port>` first.");
        cmd.AddArgument(urlArg);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?> { { "url", ctx.ParseResult.GetValueForArgument(urlArg) } };
            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileConnect, args, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }
            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1; return;
            }
            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }
            Console.WriteLine($"Connected: {JsonHelper.Serialize(resp.Result)}");
        });
        return cmd;
    }

    // ---------- explain ----------

    private static Command BuildExplainCommand()
    {
        var cmd = new Command("explain", "Top-N markers by self time on a specific frame (use a hitch's absoluteFrameIndex)");
        var frameArg = new Argument<int>("frame", "Absolute frame index (from a hitch's absoluteFrameIndex, or from a recent capture)");
        var threadIndex = new Option<int>("--thread", () => 0, "Thread index (0 = main thread)");
        var topN = new Option<int>("--top", () => 15, "Number of markers to return");
        cmd.AddArgument(frameArg);
        cmd.AddOption(threadIndex);
        cmd.AddOption(topN);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "frameIndex", ctx.ParseResult.GetValueForArgument(frameArg) },
                { "threadIndex", ctx.ParseResult.GetValueForOption(threadIndex) },
                { "topN", ctx.ParseResult.GetValueForOption(topN) }
            };
            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileExplain, args, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }
            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileExplainResult>(resp.Result);
            if (result == null) return;
            PrintExplainHuman(result);
        });
        return cmd;
    }

    // ---------- hotspots ----------

    private static Command BuildHotspotsCommand()
    {
        var cmd = new Command("hotspots", "Aggregate top-N markers by self time across a range of recently-captured frames");
        var startFrame = new Option<int?>("--start", "First frame in range (default: oldest in buffer)");
        var endFrame = new Option<int?>("--end", "Last frame in range (default: newest in buffer)");
        var threadIndex = new Option<int>("--thread", () => 0, "Thread index (0 = main thread)");
        var topN = new Option<int>("--top", () => 20, "Number of markers to return");
        var root = new Option<string?>("--root", "Restrict accumulation to a named subtree of the frame hierarchy (e.g. PlayerLoop to exclude editor IMGUI)");
        cmd.AddOption(startFrame);
        cmd.AddOption(endFrame);
        cmd.AddOption(threadIndex);
        cmd.AddOption(topN);
        cmd.AddOption(root);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "startFrame", ctx.ParseResult.GetValueForOption(startFrame) },
                { "endFrame", ctx.ParseResult.GetValueForOption(endFrame) },
                { "threadIndex", ctx.ParseResult.GetValueForOption(threadIndex) },
                { "topN", ctx.ParseResult.GetValueForOption(topN) },
                { "rootMarker", ctx.ParseResult.GetValueForOption(root) }
            };
            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileHotspots, args, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }
            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileHotspotsResult>(resp.Result);
            if (result == null) return;
            PrintHotspotsHuman(result);
        });
        return cmd;
    }

    // ---------- frame (hierarchy drill-down) ----------

    private static Command BuildFrameCommand()
    {
        var cmd = new Command("frame", "Hierarchy tree drill-down for one frame (use a hitch's absoluteFrameIndex)");
        var frameArg = new Argument<int>("frame", "Absolute frame index (from a hitch, or any frame in the profiler buffer)");
        var threadIndex = new Option<int>("--thread", () => 0, "Thread index (0 = main thread)");
        var depth = new Option<int>("--depth", () => 3, "Max tree depth");
        var thresholdMs = new Option<double>("--threshold-ms", () => 0.2, "Prune nodes whose totalMs is below this");
        var topPerNode = new Option<int>("--top", () => 8, "Keep at most this many children per node");
        var root = new Option<string?>("--root", "Start the tree at a named subtree instead of the frame root (e.g. PlayerLoop to skip past EditorLoop in editor+play captures)");
        cmd.AddArgument(frameArg);
        cmd.AddOption(threadIndex);
        cmd.AddOption(depth);
        cmd.AddOption(thresholdMs);
        cmd.AddOption(topPerNode);
        cmd.AddOption(root);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "frameIndex", ctx.ParseResult.GetValueForArgument(frameArg) },
                { "threadIndex", ctx.ParseResult.GetValueForOption(threadIndex) },
                { "depth", ctx.ParseResult.GetValueForOption(depth) },
                { "thresholdMs", ctx.ParseResult.GetValueForOption(thresholdMs) },
                { "topPerNode", ctx.ParseResult.GetValueForOption(topPerNode) },
                { "rootMarker", ctx.ParseResult.GetValueForOption(root) }
            };
            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileFrame, args, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }
            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileFrameResult>(resp.Result);
            if (result == null) return;
            PrintFrameHuman(result);
        });
        return cmd;
    }

    // ---------- mark (microbenchmark) ----------

    private static Command BuildMarkCommand()
    {
        var cmd = new Command("mark", "Run a C# expression wrapped in a ProfilerMarker; report timing + GC alloc per call");
        var exprArg = new Argument<string>("expression", "C# expression to time (must be reachable in editor context, e.g. \"GameObject.FindObjectsByType<Camera>(FindObjectsSortMode.None)\")");
        var name = new Option<string?>("--name", () => null, "Marker name (default: unityctl.mark)");
        var repeat = new Option<int>("--repeat", () => 1, "Run N times, return per-call percentiles");
        cmd.AddArgument(exprArg);
        cmd.AddOption(name);
        cmd.AddOption(repeat);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var client = MakeClient(ctx);
            if (client == null) { ctx.ExitCode = 1; return; }

            var args = new Dictionary<string, object?>
            {
                { "expression", ctx.ParseResult.GetValueForArgument(exprArg) },
                { "name", ctx.ParseResult.GetValueForOption(name) },
                { "repeat", ctx.ParseResult.GetValueForOption(repeat) }
            };
            var resp = await client.SendCommandAsync(UnityCtlCommands.ProfileMark, args, ContextHelper.GetTimeout(ctx));
            if (resp == null) { ctx.ExitCode = 1; return; }
            if (resp.Status == ResponseStatus.Error)
            {
                Console.Error.WriteLine($"Error: {resp.Error?.Message}");
                ctx.ExitCode = 1; return;
            }

            if (ContextHelper.GetJson(ctx))
            {
                Console.WriteLine(JsonHelper.Serialize(resp.Result));
                return;
            }

            var result = Deser<ProfileMarkResult>(resp.Result);
            if (result == null) return;
            PrintMarkHuman(result);
        });
        return cmd;
    }

    // ---------- helpers ----------

    private static BridgeClient? MakeClient(InvocationContext ctx)
    {
        return BridgeClient.TryCreateFromProject(
            ContextHelper.GetProjectPath(ctx),
            ContextHelper.GetAgentId(ctx));
    }

    private static T? Deser<T>(object? raw) where T : class
    {
        if (raw == null) return null;
        var json = JsonConvert.SerializeObject(raw, JsonHelper.Settings);
        return JsonConvert.DeserializeObject<T>(json, JsonHelper.Settings);
    }

    private static string? ResolveSavePathForUnity(InvocationContext ctx, string? input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        // Unity-side resolves relative to its working directory (project root). Pass through as-is
        // unless the user gave an absolute path, in which case keep absolute.
        return input;
    }

    private static void PrintStopHuman(ProfileStopResult result)
    {
        Console.WriteLine($"Profiling session {result.SessionId} stopped");
        Console.WriteLine($"  duration: {result.DurationSeconds:F1}s ({result.Frames} frames)");
        if (result.TargetIsRemote)
            Console.WriteLine($"  target:   REMOTE — {result.Target ?? "(connected player)"}");
        if (!string.IsNullOrEmpty(result.SavedPath))
            Console.WriteLine($"  saved: {result.SavedPath}");

        Console.WriteLine();
        Console.WriteLine($"  {"Stat",-30} {"Avg",10} {"p50",10} {"p95",10} {"p99",10} {"Max",10}  Unit");
        foreach (var s in result.Summaries)
        {
            Console.WriteLine($"  {s.Name,-30} {Fmt(s.Avg),10} {Fmt(s.P50),10} {Fmt(s.P95),10} {Fmt(s.P99),10} {Fmt(s.Max),10}  {s.Unit}");
        }

        if (result.TopFrames != null && result.TopFrames.Length > 0)
        {
            bool ranksPlayerLoop = result.TopFrames.Any(t => t.PlayerLoopMs.HasValue);
            Console.WriteLine();
            Console.WriteLine(ranksPlayerLoop
                ? $"  Top {result.TopFrames.Length} frames by PlayerLoop time (gameplay-only ranking):"
                : $"  Top {result.TopFrames.Length} frames by CPU main thread:");
            foreach (var t in result.TopFrames)
            {
                var playerPart = t.PlayerLoopMs.HasValue ? $"  player {Fmt(t.PlayerLoopMs.Value),6}ms" : "";
                if (t.Drivers.Length == 0)
                {
                    Console.WriteLine($"    frame {t.AbsoluteFrameIndex,8}  cpu {Fmt(t.CpuMainMs),7}ms{playerPart}  → (no hierarchy)");
                    continue;
                }
                Console.WriteLine($"    frame {t.AbsoluteFrameIndex,8}  cpu {Fmt(t.CpuMainMs),7}ms{playerPart}");
                foreach (var d in t.Drivers.Take(3))
                {
                    var hotPart = d.Hot != null
                        ? $"   → {Truncate(d.Hot.Name, 38)} {Fmt(d.Hot.SelfMs)}ms self"
                        : "";
                    Console.WriteLine($"        {Truncate(d.Name, 32),-32} {Fmt(d.TotalMs),7}ms total{hotPart}");
                }
            }
            Console.WriteLine($"    (drill in: profile frame <absoluteFrameIndex>)");
        }

        if (result.Hitches != null && result.Hitches.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {result.Hitches.Length} hitch(es) over total-frame-time threshold:");
            foreach (var h in result.Hitches.Take(10))
                Console.WriteLine($"    frame {h.FrameIndex}: {h.FrameTimeMs:F2}ms");
            if (result.Hitches.Length > 10) Console.WriteLine($"    ... and {result.Hitches.Length - 10} more");
        }
    }

    private static void PrintVitalsHuman(ProfileStopResult r)
    {
        Console.WriteLine($"Vitals  ({r.Frames} frames, {r.DurationSeconds:F1}s)");
        var dict = r.Summaries.ToDictionary(s => s.Name, s => s);

        var main = dict.GetValueOrDefault("CPU Main Thread Frame Time");
        var render = dict.GetValueOrDefault("CPU Render Thread Frame Time");
        var gpu = dict.GetValueOrDefault("GPU Frame Time");
        var draws = dict.GetValueOrDefault("Draw Calls Count");
        var gc = dict.GetValueOrDefault("GC Allocated In Frame");
        var sysmem = dict.GetValueOrDefault("System Used Memory");

        if (main != null) Console.WriteLine($"  Frame time:      avg {Fmt(main.Avg)} ms   p99 {Fmt(main.P99)} ms   max {Fmt(main.Max)} ms");
        if (render != null) Console.WriteLine($"  Render thread:   avg {Fmt(render.Avg)} ms");
        if (gpu != null) Console.WriteLine($"  GPU frame:       avg {Fmt(gpu.Avg)} ms");
        if (draws != null) Console.WriteLine($"  Draw calls:      avg {Fmt(draws.Avg)}   max {Fmt(draws.Max)}");
        if (gc != null) Console.WriteLine($"  GC alloc/frame:  avg {Fmt(gc.Avg)} bytes   max {Fmt(gc.Max)} bytes");
        if (sysmem != null) Console.WriteLine($"  System memory:   {sysmem.Avg / 1024.0 / 1024.0:F1} MB");

        if (r.TopFrames != null && r.TopFrames.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Worst frames (CPU main thread):");
            foreach (var t in r.TopFrames.Take(3))
            {
                var top = t.Drivers.FirstOrDefault();
                var driver = top != null ? $" ({Truncate(top.Name, 30)})" : "";
                Console.WriteLine($"    frame {t.AbsoluteFrameIndex}: cpu {Fmt(t.CpuMainMs)}ms{driver}");
            }
        }

        if (r.Hitches != null && r.Hitches.Length > 0)
        {
            var hitchThresh = 33.3;
            Console.WriteLine($"  Hitches:         {r.Hitches.Length} frame(s) > {hitchThresh:F1}ms (or median × multiplier)");
        }
    }

    private static string Fmt(double v)
    {
        if (double.IsNaN(v)) return "—";
        if (Math.Abs(v) >= 10000) return v.ToString("F0");
        if (Math.Abs(v) >= 100) return v.ToString("F1");
        return v.ToString("F2");
    }

    private static void PrintExplainHuman(ProfileExplainResult r)
    {
        Console.WriteLine($"Frame {r.FrameIndex}  thread={r.ThreadIndex} ({r.ThreadName})  frame_time={Fmt(r.FrameTimeMs)} ms");
        Console.WriteLine();
        Console.WriteLine($"  {"Marker",-50} {"Self ms",10} {"Calls",8}  GC alloc");
        foreach (var m in r.TopMarkers)
        {
            var gc = m.GcAllocBytes.HasValue ? $"{m.GcAllocBytes.Value} B" : "";
            Console.WriteLine($"  {Truncate(m.Name, 50),-50} {Fmt(m.SelfTimeMs),10} {m.Calls,8}  {gc}");
        }
    }

    private static void PrintHotspotsHuman(ProfileHotspotsResult r)
    {
        Console.WriteLine($"Hotspots  frames=[{r.StartFrame}..{r.EndFrame}] processed={r.FrameCount}  thread={r.ThreadIndex} ({r.ThreadName})");
        Console.WriteLine();
        Console.WriteLine($"  {"Marker",-50} {"Self ms",12} {"Calls",10}  GC alloc");
        foreach (var m in r.TopMarkers)
        {
            var gc = m.GcAllocBytes.HasValue ? $"{m.GcAllocBytes.Value} B" : "";
            Console.WriteLine($"  {Truncate(m.Name, 50),-50} {Fmt(m.SelfTimeMs),12} {m.Calls,10}  {gc}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private static void PrintFrameHuman(ProfileFrameResult r)
    {
        Console.WriteLine($"Frame {r.FrameIndex}  thread={r.ThreadIndex} ({r.ThreadName})  frame_time={Fmt(r.FrameTimeMs)} ms");
        Console.WriteLine($"  depth={r.Depth}  threshold={Fmt(r.ThresholdMs)}ms  pruned={r.PrunedNodes}");
        Console.WriteLine();
        if (r.Tree.Length == 0)
        {
            Console.WriteLine("  (no nodes above threshold)");
            return;
        }
        foreach (var n in r.Tree) PrintFrameNode(n, 0);
    }

    private static void PrintFrameNode(ProfileFrameNode n, int indent)
    {
        var pad = new string(' ', 2 + indent * 2);
        var gc = n.GcKb.HasValue ? $"  gc={n.GcKb.Value:F2}KB" : "";
        Console.WriteLine($"{pad}{Truncate(n.Name, 60),-60} self={Fmt(n.SelfMs),8}ms  total={Fmt(n.TotalMs),8}ms  calls={n.Calls,4}{gc}");
        if (n.Children == null) return;
        foreach (var c in n.Children) PrintFrameNode(c, indent + 1);
    }

    private static void PrintMarkHuman(ProfileMarkResult r)
    {
        Console.WriteLine($"{r.Name}  ({r.Repeat} call{(r.Repeat == 1 ? "" : "s")})");
        if (r.Repeat == 1)
        {
            Console.WriteLine($"  time:    {r.MeanMs:F4} ms");
        }
        else
        {
            Console.WriteLine($"  mean:    {r.MeanMs:F4} ms");
            Console.WriteLine($"  p50/p95: {r.P50Ms:F4} / {r.P95Ms:F4} ms");
            Console.WriteLine($"  min/max: {r.MinMs:F4} / {r.MaxMs:F4} ms");
        }
        Console.WriteLine($"  gc:      {r.GcBytesPerCall} bytes/call ({r.GcBytes} total)");
        if (!string.IsNullOrEmpty(r.Result))
            Console.WriteLine($"  result:  {Truncate(r.Result, 120)}");
    }
}
