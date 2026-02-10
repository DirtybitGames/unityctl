using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;

namespace UnityCtl.Bridge;

public static class BridgeEndpoints
{
    // Internal timeout constants (not user-configurable)
    private static readonly TimeSpan StatusCheckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortEventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompilationWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DomainReloadReconnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DomainReloadDetectionTimeout = TimeSpan.FromSeconds(3);

    // Command timeout configuration (in seconds, configurable via environment variables)
    // Note: Timeouts are resolved lazily via env vars so tests can override them per-fixture.
    private static readonly Dictionary<string, CommandConfig> CommandConfigs = new()
    {
        [UnityCtlCommands.PlayEnter] = new CommandConfig
        {
            TimeoutEnvVar = "UNITYCTL_TIMEOUT_PLAYMODE", TimeoutDefaultSeconds = 30,
            CompletionEvent = UnityCtlEvents.PlayModeChanged,
            CompletionEventState = "EnteredPlayMode"
        },
        [UnityCtlCommands.PlayExit] = new CommandConfig
        {
            TimeoutEnvVar = "UNITYCTL_TIMEOUT_PLAYMODE", TimeoutDefaultSeconds = 30,
            CompletionEvent = UnityCtlEvents.PlayModeChanged
            // Note: Don't filter by state for play exit - domain reload can cause event loss
        },
        [UnityCtlCommands.AssetImport] = new CommandConfig
        {
            TimeoutEnvVar = "UNITYCTL_TIMEOUT_ASSET", TimeoutDefaultSeconds = 30,
            CompletionEvent = UnityCtlEvents.AssetImportComplete
        },
        [UnityCtlCommands.AssetReimportAll] = new CommandConfig
        {
            TimeoutEnvVar = "UNITYCTL_TIMEOUT_ASSET", TimeoutDefaultSeconds = 30,
            CompletionEvent = UnityCtlEvents.AssetReimportAllComplete
        },
        [UnityCtlCommands.AssetRefresh] = new CommandConfig
        {
            TimeoutEnvVar = "UNITYCTL_TIMEOUT_REFRESH", TimeoutDefaultSeconds = 60,
            CompletionEvent = UnityCtlEvents.AssetRefreshComplete
        },
        [UnityCtlCommands.TestRun] = new CommandConfig
        {
            TimeoutEnvVar = "UNITYCTL_TIMEOUT_TEST", TimeoutDefaultSeconds = 300,
            CompletionEvent = UnityCtlEvents.TestFinished
        },
        [UnityCtlCommands.RecordStart] = new CommandConfig
        {
            TimeoutEnvVar = "UNITYCTL_TIMEOUT_RECORD", TimeoutDefaultSeconds = 600,
            CompletionEvent = UnityCtlEvents.RecordFinished
        }
    };

    private static TimeSpan GetDefaultTimeout()
    {
        var value = Environment.GetEnvironmentVariable("UNITYCTL_TIMEOUT_DEFAULT");
        if (int.TryParse(value, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(30);
    }

    // --- Helper methods ---

    private static Dictionary<string, object?>? ConvertArgs(Dictionary<string, object?>? args)
    {
        if (args == null)
            return null;

        // Round-trip through JSON to convert System.Text.Json types to Newtonsoft.Json types
        var argsJson = System.Text.Json.JsonSerializer.Serialize(args);
        return JsonConvert.DeserializeObject<Dictionary<string, object?>>(argsJson, JsonHelper.Settings);
    }

    private static RequestMessage CreateRequestMessage(RpcRequest request, Dictionary<string, object?>? convertedArgs)
    {
        return new RequestMessage
        {
            Origin = MessageOrigin.Bridge,
            RequestId = Guid.NewGuid().ToString(),
            AgentId = request.AgentId,
            Command = request.Command,
            Args = convertedArgs
        };
    }

    private static RequestMessage CreateInternalRequest(string? agentId, string command)
    {
        return new RequestMessage
        {
            Origin = MessageOrigin.Bridge,
            RequestId = Guid.NewGuid().ToString(),
            AgentId = agentId,
            Command = command,
            Args = null
        };
    }

    private static IResult JsonResponse(ResponseMessage response)
    {
        var json = JsonConvert.SerializeObject(response, JsonHelper.Settings);
        return Results.Content(json, "application/json");
    }

    private static ResponseMessage OkResponse(string requestId, object? result)
    {
        return new ResponseMessage
        {
            Origin = MessageOrigin.Unity,
            RequestId = requestId,
            Status = ResponseStatus.Ok,
            Result = result
        };
    }

    private static ResponseMessage ErrorResponse(string requestId, object? result, string code, string message)
    {
        return new ResponseMessage
        {
            Origin = MessageOrigin.Unity,
            RequestId = requestId,
            Status = ResponseStatus.Error,
            Result = result,
            Error = new ErrorPayload { Code = code, Message = message }
        };
    }

    private record CompilationResult(bool Success, CompilationMessageInfo[]? Errors, CompilationMessageInfo[]? Warnings);

    private static async Task<CompilationResult?> WaitForCompilationAsync(
        BridgeState state,
        string waiterKey,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Task<EventMessage>? preRegistered = null)
    {
        try
        {
            var compileEvent = preRegistered != null
                ? await preRegistered
                : await state.WaitForEventAsync(
                    waiterKey,
                    UnityCtlEvents.CompilationFinished,
                    timeout,
                    cancellationToken);

            var compilePayload = compileEvent.Payload as JObject;
            var success = compilePayload?["success"]?.Value<bool>() ?? true;
            var errors = compilePayload?["errors"]?.ToObject<CompilationMessageInfo[]>();
            var warnings = compilePayload?["warnings"]?.ToObject<CompilationMessageInfo[]>();

            return new CompilationResult(success, errors, warnings);
        }
        catch (TimeoutException)
        {
            return null; // Compilation timed out (e.g., domain reload)
        }
    }

    // --- Shared play mode entry ---

    private enum EnsurePlayModeOutcome { AlreadyPlaying, EnteredPlayMode, CompilationFailed, PlayModeEntryFailed }

    private record EnsurePlayModeResult(
        EnsurePlayModeOutcome Outcome,
        bool CompilationTriggered,
        bool HasPreExistingErrors,
        CompilationResult? Compilation);

    private static async Task<EnsurePlayModeResult> EnsurePlayModeAsync(
        BridgeState state,
        string? agentId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Check if already playing
        var statusRequest = CreateInternalRequest(agentId, UnityCtlCommands.PlayStatus);
        var statusResponse = await state.SendCommandToUnityAsync(statusRequest, timeout, cancellationToken);
        var currentState = (statusResponse.Result as JObject)?["state"]?.ToString();

        if (currentState == "playing")
        {
            Console.WriteLine($"[Bridge] EnsurePlayMode: already playing");
            return new EnsurePlayModeResult(EnsurePlayModeOutcome.AlreadyPlaying, false, false, null);
        }

        Console.WriteLine($"[Bridge] EnsurePlayMode: doing asset refresh first to catch pending changes");

        // Asset refresh to pick up pending changes
        var refreshRequest = CreateInternalRequest(agentId, UnityCtlCommands.AssetRefresh);
        var refreshWaitTask = state.WaitForEventAsync(
            refreshRequest.RequestId,
            UnityCtlEvents.AssetRefreshComplete,
            timeout,
            cancellationToken);

        await state.SendCommandToUnityAsync(refreshRequest, timeout, cancellationToken);
        var refreshEvent = await refreshWaitTask;
        var refreshPayload = refreshEvent.Payload as JObject;
        var compilationTriggered = refreshPayload?["compilationTriggered"]?.Value<bool>() ?? false;
        var hasCompilationErrors = refreshPayload?["hasCompilationErrors"]?.Value<bool>() ?? false;

        Console.WriteLine($"[Bridge] Asset refresh complete, compilationTriggered: {compilationTriggered}, hasCompilationErrors: {hasCompilationErrors}");

        // Pre-existing compilation errors (no new compilation triggered)
        if (hasCompilationErrors && !compilationTriggered)
        {
            Console.WriteLine($"[Bridge] Existing compilation errors detected - aborting");
            return new EnsurePlayModeResult(EnsurePlayModeOutcome.CompilationFailed, false, true, null);
        }

        // Wait for compilation if triggered
        CompilationResult? compResult = null;
        if (compilationTriggered)
        {
            Console.WriteLine($"[Bridge] Waiting for compilation to finish...");
            compResult = await WaitForCompilationAsync(
                state,
                refreshRequest.RequestId + "_compile",
                timeout,
                cancellationToken);

            Console.WriteLine($"[Bridge] Compilation finished, success: {compResult!.Success}");

            if (!compResult.Success)
            {
                var errorCount = compResult.Errors?.Length ?? 0;
                Console.WriteLine($"[Bridge] Compilation failed with {errorCount} error(s) - aborting");
                return new EnsurePlayModeResult(EnsurePlayModeOutcome.CompilationFailed, true, false, compResult);
            }
        }

        // Play mode entry retry loop (handles domain reload, reconnection, bounce-back)
        var playEnterRequest = CreateInternalRequest(agentId, UnityCtlCommands.PlayEnter);
        var playEnterStartTime = DateTime.UtcNow;

        while (DateTime.UtcNow - playEnterStartTime < timeout)
        {
            // Ensure Unity is connected
            if (!state.IsUnityConnected)
            {
                Console.WriteLine($"[Bridge] Waiting for Unity to reconnect...");
                var remaining = timeout - (DateTime.UtcNow - playEnterStartTime);
                if (remaining <= TimeSpan.Zero || !await state.WaitForUnityConnectionAsync(remaining, cancellationToken))
                {
                    throw new TimeoutException("Unity did not reconnect after domain reload");
                }
                Console.WriteLine($"[Bridge] Unity reconnected");
            }

            // Check current play status
            try
            {
                var loopStatusRequest = CreateInternalRequest(agentId, UnityCtlCommands.PlayStatus);
                var loopStatusResponse = await state.SendCommandToUnityAsync(loopStatusRequest, StatusCheckTimeout, cancellationToken);
                var currentPlayState = (loopStatusResponse.Result as JObject)?["state"]?.ToString();

                Console.WriteLine($"[Bridge] Current play status: {currentPlayState}");

                if (currentPlayState == "playing")
                {
                    return new EnsurePlayModeResult(EnsurePlayModeOutcome.EnteredPlayMode, compilationTriggered, false, compResult);
                }
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[Bridge] Play status check timed out - Unity may have disconnected, retrying...");
                continue;
            }

            // Pre-register event waiter for play mode changes
            var shortTimeout = ShortEventTimeout;
            var playEnterWaitTask = state.WaitForEventAsync(
                playEnterRequest.RequestId + "_" + Guid.NewGuid().ToString("N")[..8],
                UnityCtlEvents.PlayModeChanged,
                shortTimeout,
                cancellationToken);

            // Send play.enter command
            Console.WriteLine($"[Bridge] Sending play.enter command");
            ResponseMessage playEnterResponse;
            try
            {
                playEnterResponse = await state.SendCommandToUnityAsync(playEnterRequest, StatusCheckTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[Bridge] Play enter command timed out - Unity may have disconnected, retrying...");
                continue;
            }

            var playEnterState = (playEnterResponse.Result as JObject)?["state"]?.ToString();

            if (playEnterState == "AlreadyPlaying")
            {
                return new EnsurePlayModeResult(EnsurePlayModeOutcome.AlreadyPlaying, compilationTriggered, false, compResult);
            }

            // Wait for play mode event
            EventMessage? firstEvent = null;
            try
            {
                firstEvent = await playEnterWaitTask;
            }
            catch (TimeoutException)
            {
                if (state.IsDomainReloadInProgress || !state.IsUnityConnected)
                {
                    Console.WriteLine($"[Bridge] Domain reload detected during play enter - will retry after reconnect");
                }
                continue;
            }

            var firstPayload = firstEvent.Payload as JObject;
            var firstState = firstPayload?["state"]?.ToString();

            Console.WriteLine($"[Bridge] Play mode event received: {firstState}");

            if (firstState == "ExitingEditMode")
            {
                // Wait for the second event
                var secondWaitTask = state.WaitForEventAsync(
                    playEnterRequest.RequestId + "_second_" + Guid.NewGuid().ToString("N")[..8],
                    UnityCtlEvents.PlayModeChanged,
                    shortTimeout,
                    cancellationToken);

                EventMessage? secondEvent = null;
                try
                {
                    secondEvent = await secondWaitTask;
                }
                catch (TimeoutException)
                {
                    Console.WriteLine($"[Bridge] Timeout waiting for second event - checking status");
                    continue;
                }

                var secondPayload = secondEvent.Payload as JObject;
                var secondState = secondPayload?["state"]?.ToString();

                Console.WriteLine($"[Bridge] Second play mode event: {secondState}");

                if (secondState == "EnteredEditMode")
                {
                    // Bounced back - play mode entry failed
                    Console.WriteLine($"[Bridge] Play mode entry failed - bounced back to edit mode");
                    return new EnsurePlayModeResult(EnsurePlayModeOutcome.PlayModeEntryFailed, compilationTriggered, false, compResult);
                }

                if (secondState == "EnteredPlayMode")
                {
                    return new EnsurePlayModeResult(EnsurePlayModeOutcome.EnteredPlayMode, compilationTriggered, false, compResult);
                }
            }
            else if (firstState == "EnteredPlayMode")
            {
                return new EnsurePlayModeResult(EnsurePlayModeOutcome.EnteredPlayMode, compilationTriggered, false, compResult);
            }
        }

        // Overall timeout
        throw new TimeoutException($"Play mode entry timed out after {timeout.TotalSeconds}s");
    }

    // --- RPC dispatch ---

    private static async Task<IResult> HandleRpcAsync(BridgeState state, HttpContext context, RpcRequest request)
    {
        if (!state.IsUnityConnected)
        {
            if (state.IsDomainReloadInProgress)
            {
                // Unity is temporarily disconnected during domain reload — wait for reconnection
                Console.WriteLine($"[Bridge] RPC received during domain reload - waiting for Unity to reconnect");
                var reloadComplete = await state.WaitForDomainReloadCompleteAsync(
                    DomainReloadReconnectTimeout, context.RequestAborted);
                if (!reloadComplete)
                {
                    return Results.Problem(
                        statusCode: 503,
                        title: "Unity Offline",
                        detail: "Unity Editor did not reconnect after domain reload"
                    );
                }
                Console.WriteLine($"[Bridge] Unity reconnected after domain reload - processing RPC");
            }
            else
            {
                return Results.Problem(
                    statusCode: 503,
                    title: "Unity Offline",
                    detail: "Unity Editor is not connected to the bridge"
                );
            }
        }

        var convertedArgs = ConvertArgs(request.Args);
        var requestMessage = CreateRequestMessage(request, convertedArgs);

        try
        {
            var hasConfig = CommandConfigs.TryGetValue(request.Command, out var config);
            var timeout = hasConfig ? config!.Timeout : GetDefaultTimeout();

            if (request.Command == UnityCtlCommands.PlayEnter)
                return await HandlePlayEnterAsync(state, requestMessage, request, timeout, context.RequestAborted);

            if (request.Command == UnityCtlCommands.PlayExit)
                return await HandlePlayExitAsync(state, requestMessage, config!, timeout, context.RequestAborted);

            if (request.Command == UnityCtlCommands.RecordStart)
                return await HandleRecordStartAsync(state, requestMessage, request, config!, timeout, context.RequestAborted);

            return await HandleGenericCommandAsync(state, requestMessage, hasConfig ? config : null, timeout, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(
                statusCode: 499,
                title: "Request Cancelled",
                detail: "Request was cancelled (client disconnected or server shutting down)"
            );
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(
                statusCode: 504,
                title: "Request Timeout",
                detail: ex.Message
            );
        }
        catch (Exception ex)
        {
            return Results.Problem(
                statusCode: 500,
                title: "Request Failed",
                detail: ex.Message
            );
        }
    }

    // --- Command handlers ---

    private static async Task<IResult> HandlePlayEnterAsync(
        BridgeState state,
        RequestMessage requestMessage,
        RpcRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await EnsurePlayModeAsync(state, request.AgentId, timeout, cancellationToken);

        switch (result.Outcome)
        {
            case EnsurePlayModeOutcome.AlreadyPlaying:
                return Results.Json(OkResponse(requestMessage.RequestId, new { state = "AlreadyPlaying" }), statusCode: 200);

            case EnsurePlayModeOutcome.CompilationFailed when result.HasPreExistingErrors:
                return Results.Json(ErrorResponse(
                    requestMessage.RequestId,
                    new
                    {
                        state = "CompilationFailed",
                        compilationTriggered = false,
                        compilationSuccess = false,
                        hasCompilationErrors = true
                    },
                    "COMPILATION_ERROR",
                    "Cannot enter play mode - compilation errors exist. Fix the errors and try again."), statusCode: 200);

            case EnsurePlayModeOutcome.CompilationFailed:
            {
                var errorCount = result.Compilation?.Errors?.Length ?? 0;
                return Results.Json(ErrorResponse(
                    requestMessage.RequestId,
                    new
                    {
                        state = "CompilationFailed",
                        compilationTriggered = true,
                        compilationSuccess = false,
                        errors = result.Compilation?.Errors,
                        warnings = result.Compilation?.Warnings
                    },
                    "COMPILATION_ERROR",
                    $"Compilation failed with {errorCount} error(s)"), statusCode: 200);
            }

            case EnsurePlayModeOutcome.PlayModeEntryFailed:
                return Results.Json(ErrorResponse(
                    requestMessage.RequestId,
                    new
                    {
                        state = "PlayModeEntryFailed",
                        compilationTriggered = result.CompilationTriggered
                    },
                    "PLAY_MODE_FAILED",
                    "Cannot enter play mode - check for compilation errors or other issues in the Unity console"), statusCode: 200);

            default: // EnteredPlayMode
                return Results.Json(OkResponse(requestMessage.RequestId, new
                {
                    state = "EnteredPlayMode",
                    compilationTriggered = result.CompilationTriggered
                }), statusCode: 200);
        }
    }

    private static async Task<IResult> HandlePlayExitAsync(
        BridgeState state,
        RequestMessage requestMessage,
        CommandConfig config,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Pre-register all event waiters BEFORE sending command
        var eventWaitTask = state.WaitForEventAsync(
            requestMessage.RequestId,
            config.CompletionEvent!,
            timeout,
            cancellationToken,
            config.CompletionEventState);

        var compilationStartedWaitTask = state.WaitForEventAsync(
            requestMessage.RequestId + "_compile_started_preregister",
            UnityCtlEvents.CompilationStarted,
            timeout,
            cancellationToken);

        var compilationWaitTask = state.WaitForEventAsync(
            requestMessage.RequestId + "_compile_preregister",
            UnityCtlEvents.CompilationFinished,
            timeout,
            cancellationToken);

        var domainReloadWaitTask = state.WaitForEventAsync(
            requestMessage.RequestId + "_domain_reload_preregister",
            UnityCtlEvents.DomainReloadStarting,
            timeout,
            cancellationToken);

        try
        {
            // Send command to Unity
            var response = await state.SendCommandToUnityAsync(requestMessage, timeout, cancellationToken);

            // Check if Unity indicated the command was a no-op
            var resultState = (response.Result as JObject)?["state"]?.ToString();
            if (resultState == "AlreadyPlaying" || resultState == "AlreadyStopped")
            {
                return JsonResponse(response);
            }

            // Wait for play mode changed event
            var eventMessage = await eventWaitTask;

            // Detect compilation via compilation.started event
            var compilationTriggered = false;
            {
                var waitTask = Task.Delay(CompilationWaitTimeout, cancellationToken);
                var completedTask = await Task.WhenAny(compilationStartedWaitTask, waitTask);

                if (completedTask == compilationStartedWaitTask && compilationStartedWaitTask.IsCompletedSuccessfully)
                {
                    compilationTriggered = true;
                    Console.WriteLine($"[Bridge] Compilation started detected during play exit - will wait for compilation to finish");
                }
            }

            // Build final result
            var eventPayload = eventMessage.Payload as JObject;
            var playModeState = eventPayload?["state"]?.Value<string>();
            object? finalResult = eventMessage.Payload;

            if (compilationTriggered)
            {
                var compResult = await WaitForCompilationAsync(
                    state,
                    requestMessage.RequestId + "_compile",
                    timeout,
                    cancellationToken,
                    compilationWaitTask);

                if (compResult != null)
                {
                    finalResult = playModeState != null
                        ? new
                        {
                            state = playModeState,
                            compilationTriggered = true,
                            compilationSuccess = compResult.Success,
                            errors = compResult.Errors,
                            warnings = compResult.Warnings
                        }
                        : (object)new
                        {
                            compilationTriggered = true,
                            compilationSuccess = compResult.Success,
                            errors = compResult.Errors,
                            warnings = compResult.Warnings
                        };

                    if (!compResult.Success)
                    {
                        var errorCount = compResult.Errors?.Length ?? 0;
                        return Results.Json(new ResponseMessage
                        {
                            Origin = response.Origin,
                            RequestId = response.RequestId,
                            Status = ResponseStatus.Error,
                            Result = finalResult,
                            Error = new ErrorPayload { Code = "COMPILATION_ERROR", Message = $"Compilation failed with {errorCount} error(s)" }
                        }, statusCode: 200);
                    }
                }
                else
                {
                    // Compilation timed out (e.g., domain reload)
                    finalResult = playModeState != null
                        ? new
                        {
                            state = playModeState,
                            compilationTriggered = true,
                            compilationSuccess = (bool?)null,
                            note = "Compilation may still be in progress (domain reload)"
                        }
                        : (object)new
                        {
                            compilationTriggered = true,
                            compilationSuccess = (bool?)null,
                            note = "Compilation may still be in progress (domain reload)"
                        };
                }
            }

            response = new ResponseMessage
            {
                Origin = response.Origin,
                RequestId = response.RequestId,
                Status = response.Status,
                Result = finalResult,
                Error = response.Error
            };

            // If compilation was triggered, a domain reload typically follows.
            // Wait for the reload to complete so the caller's next command doesn't hit a 503.
            if (compilationTriggered)
            {
                var detectionDelay = Task.Delay(DomainReloadDetectionTimeout, cancellationToken);
                var completed = await Task.WhenAny(domainReloadWaitTask, detectionDelay);

                if (completed == domainReloadWaitTask && domainReloadWaitTask.IsCompletedSuccessfully)
                {
                    Console.WriteLine($"[Bridge] Domain reload detected after play exit - waiting for Unity to reconnect");
                    var reloadComplete = await state.WaitForDomainReloadCompleteAsync(
                        DomainReloadReconnectTimeout, cancellationToken);
                    if (reloadComplete)
                    {
                        Console.WriteLine($"[Bridge] Unity reconnected after play exit domain reload");
                    }
                }
            }

            return JsonResponse(response);
        }
        finally
        {
            // Clean up pre-registered waiters if not used
            if (!compilationStartedWaitTask.IsCompleted)
                state.CancelEventWaiter(requestMessage.RequestId + "_compile_started_preregister");
            if (!compilationWaitTask.IsCompleted)
                state.CancelEventWaiter(requestMessage.RequestId + "_compile_preregister");
            if (!domainReloadWaitTask.IsCompleted)
                state.CancelEventWaiter(requestMessage.RequestId + "_domain_reload_preregister");
        }
    }

    private static async Task<IResult> HandleRecordStartAsync(
        BridgeState state,
        RequestMessage requestMessage,
        RpcRequest request,
        CommandConfig config,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Check if duration is specified (determines blocking vs fire-and-forget)
        var convertedArgs = requestMessage.Args as IDictionary<string, object?>;
        double? duration = null;
        if (convertedArgs != null && convertedArgs.TryGetValue("duration", out var durationVal) && durationVal != null)
        {
            if (durationVal is JToken jt)
                duration = jt.Value<double>();
            else if (durationVal is double d)
                duration = d;
            else if (double.TryParse(durationVal.ToString(), out var parsed))
                duration = parsed;
        }

        // Auto-enter play mode if not already playing (with asset refresh + compilation)
        var playModeTimeout = CommandConfigs[UnityCtlCommands.PlayEnter].Timeout;
        var playResult = await EnsurePlayModeAsync(state, request.AgentId, playModeTimeout, cancellationToken);

        if (playResult.Outcome == EnsurePlayModeOutcome.CompilationFailed)
        {
            if (playResult.HasPreExistingErrors)
            {
                return Results.Json(ErrorResponse(
                    requestMessage.RequestId,
                    new { state = "CompilationFailed" },
                    "COMPILATION_ERROR",
                    "Cannot start recording - compilation errors exist. Fix the errors and try again."), statusCode: 200);
            }
            var errorCount = playResult.Compilation?.Errors?.Length ?? 0;
            return Results.Json(ErrorResponse(
                requestMessage.RequestId,
                new
                {
                    state = "CompilationFailed",
                    errors = playResult.Compilation?.Errors,
                    warnings = playResult.Compilation?.Warnings
                },
                "COMPILATION_ERROR",
                $"Compilation failed with {errorCount} error(s)"), statusCode: 200);
        }

        if (playResult.Outcome == EnsurePlayModeOutcome.PlayModeEntryFailed)
        {
            return Results.Json(ErrorResponse(
                requestMessage.RequestId,
                new { state = "PlayModeEntryFailed" },
                "PLAY_MODE_FAILED",
                "Cannot start recording - play mode entry failed"), statusCode: 200);
        }

        Console.WriteLine($"[Bridge] Record start: in play mode, sending record.start to Unity");

        // If duration is specified, pre-register the event waiter for record.finished
        Task<EventMessage>? recordFinishedTask = null;
        if (duration.HasValue)
        {
            var recordTimeout = TimeSpan.FromSeconds(duration.Value + 30); // duration + buffer
            recordFinishedTask = state.WaitForEventAsync(
                requestMessage.RequestId,
                config.CompletionEvent!,
                recordTimeout,
                cancellationToken);
        }

        // Send record.start to Unity
        var response = await state.SendCommandToUnityAsync(requestMessage, timeout, cancellationToken);

        if (response.Status == ResponseStatus.Error)
        {
            return JsonResponse(response);
        }

        // If duration is specified, wait for the record.finished event
        if (recordFinishedTask != null)
        {
            Console.WriteLine($"[Bridge] Record start: waiting for recording to finish (duration: {duration}s)");
            var finishedEvent = await recordFinishedTask;

            // Return the finished event payload as the result
            response = new ResponseMessage
            {
                Origin = response.Origin,
                RequestId = response.RequestId,
                Status = ResponseStatus.Ok,
                Result = finishedEvent.Payload
            };
        }

        return JsonResponse(response);
    }

    private static async Task<IResult> HandleGenericCommandAsync(
        BridgeState state,
        RequestMessage requestMessage,
        CommandConfig? config,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Pre-register WebSocket event waiter for commands that have a completion event
        Task<EventMessage>? eventWaitTask = null;
        if (config?.CompletionEvent != null)
        {
            eventWaitTask = state.WaitForEventAsync(
                requestMessage.RequestId, config.CompletionEvent, timeout, cancellationToken, config.CompletionEventState);
        }

        // Send command to Unity
        var response = await state.SendCommandToUnityAsync(requestMessage, timeout, cancellationToken);

        // Check if Unity indicated the command was a no-op (already in desired state)
        var resultState = (response.Result as JObject)?["state"]?.ToString();
        var isAlreadyInState = resultState == "AlreadyPlaying" || resultState == "AlreadyStopped";

        // If command has a WebSocket completion event, wait for it (unless already in state)
        if (eventWaitTask != null && !isAlreadyInState)
        {
            var eventMessage = await eventWaitTask;

            // Check if compilation was triggered
            var compilationTriggered = false;
            if (eventMessage.Event == UnityCtlEvents.AssetRefreshComplete ||
                eventMessage.Event == UnityCtlEvents.PlayModeChanged)
            {
                var payload = eventMessage.Payload as JObject;
                compilationTriggered = payload?["compilationTriggered"]?.Value<bool>() ?? false;
            }

            object? finalResult = eventMessage.Payload;
            var eventPayload = eventMessage.Payload as JObject;

            if (compilationTriggered)
            {
                var compResult = await WaitForCompilationAsync(
                    state,
                    requestMessage.RequestId + "_compile",
                    timeout,
                    cancellationToken);

                if (compResult != null)
                {
                    var playModeState = eventPayload?["state"]?.Value<string>();
                    finalResult = playModeState != null
                        ? new
                        {
                            state = playModeState,
                            compilationTriggered = true,
                            compilationSuccess = compResult.Success,
                            errors = compResult.Errors,
                            warnings = compResult.Warnings
                        }
                        : (object)new
                        {
                            compilationTriggered = true,
                            compilationSuccess = compResult.Success,
                            errors = compResult.Errors,
                            warnings = compResult.Warnings
                        };

                    if (!compResult.Success)
                    {
                        var errorCount = compResult.Errors?.Length ?? 0;
                        return Results.Json(new ResponseMessage
                        {
                            Origin = response.Origin,
                            RequestId = response.RequestId,
                            Status = ResponseStatus.Error,
                            Result = finalResult,
                            Error = new ErrorPayload { Code = "COMPILATION_ERROR", Message = $"Compilation failed with {errorCount} error(s)" }
                        }, statusCode: 200);
                    }
                }
                else
                {
                    // Compilation timed out
                    var playModeState = eventPayload?["state"]?.Value<string>();
                    finalResult = playModeState != null
                        ? new
                        {
                            state = playModeState,
                            compilationTriggered = true,
                            compilationSuccess = (bool?)null,
                            note = "Compilation may still be in progress (domain reload)"
                        }
                        : (object)new
                        {
                            compilationTriggered = true,
                            compilationSuccess = (bool?)null,
                            note = "Compilation may still be in progress (domain reload)"
                        };
                }
            }

            response = new ResponseMessage
            {
                Origin = response.Origin,
                RequestId = response.RequestId,
                Status = response.Status,
                Result = finalResult,
                Error = response.Error
            };
        }

        return JsonResponse(response);
    }

    // --- Endpoint mapping ---

    public static void MapEndpoints(WebApplication app)
    {
        var state = app.Services.GetRequiredService<BridgeState>();

        // Health endpoint
        app.MapGet("/health", () =>
        {
            var unityHello = state.UnityHelloMessage;
            return new HealthResult
            {
                Status = "ok",
                ProjectId = state.ProjectId,
                UnityConnected = state.IsUnityConnected,
                BridgeVersion = VersionInfo.Version,
                UnityPluginVersion = unityHello?.PluginVersion
            };
        });

        // Unified logs tail endpoint (lines=0 means all logs since clear)
        app.MapGet("/logs/tail", ([FromQuery] int lines = 0, [FromQuery] string source = "console", [FromQuery] bool full = false) =>
        {
            var entries = state.GetRecentUnifiedLogs(lines, source, ignoreWatermark: full);
            var clearInfo = full ? null : state.GetClearInfo();
            return new
            {
                entries,
                watermark = state.GetWatermark(),
                clearedAt = clearInfo?.Timestamp.ToString("o"),
                clearReason = clearInfo?.Reason
            };
        });

        // Unified logs clear endpoint (sets watermark)
        app.MapPost("/logs/clear", ([FromQuery] string? reason = null) =>
        {
            var watermark = state.ClearLogWatermark(reason ?? "manual clear");
            return new { success = true, watermark, message = "Logs cleared" };
        });

        // Unified logs stream endpoint (SSE)
        app.MapGet("/logs/stream", async (HttpContext context, [FromQuery] string source = "console") =>
        {
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            await context.Response.Body.FlushAsync(context.RequestAborted);

            var reader = state.SubscribeToLogs();

            try
            {
                await foreach (var entry in reader.ReadAllAsync(context.RequestAborted))
                {
                    // Filter by source if specified
                    if (source != "all" && entry.Source != source)
                        continue;

                    var json = JsonConvert.SerializeObject(entry, JsonHelper.Settings);
                    await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            finally
            {
                state.UnsubscribeFromLogs(reader);
            }
        });

        // RPC endpoint
        app.MapPost("/rpc", async (HttpContext context, [FromBody] RpcRequest request) =>
            await HandleRpcAsync(state, context, request));

        // WebSocket endpoint for Unity
        app.Map("/unity", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Unity connected");

            state.SetUnityConnection(webSocket);

            try
            {
                await HandleUnityConnectionAsync(webSocket, state, context.RequestAborted);
            }
            finally
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Unity disconnected");
                state.SetUnityConnection(null);
            }
        });
    }

    private static async Task HandleUnityConnectionAsync(WebSocket webSocket, BridgeState state, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 16];
        var messageBuilder = new System.Text.StringBuilder();

        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Append the received chunk to the message builder
                    var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(chunk);

                    // Only process when we have the complete message
                    if (result.EndOfMessage)
                    {
                        var json = messageBuilder.ToString();
                        messageBuilder.Clear();
                        await HandleUnityMessageAsync(json, webSocket, state, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown detected - forcefully abort the WebSocket connection
            // We use Abort() instead of CloseAsync() because we need immediate termination
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent)
            {
                try
                {
                    webSocket.Abort();
                }
                catch
                {
                    // Ignore errors during abort - we're shutting down anyway
                }
            }
        }
        catch (WebSocketException)
        {
            // WebSocket was already closed or aborted - this is fine during shutdown
        }
    }

    private static async Task HandleUnityMessageAsync(string json, WebSocket webSocket, BridgeState state, CancellationToken cancellationToken)
    {
        try
        {
            var jObject = JObject.Parse(json);

            if (!jObject.TryGetValue("type", out var typeToken))
            {
                Console.WriteLine($"[Warning] Received message without 'type' field");
                return;
            }

            var messageType = typeToken.ToString();

            switch (messageType)
            {
                case "hello":
                    await HandleHelloAsync(json, webSocket, state, cancellationToken);
                    break;

                case "response":
                    HandleResponse(json, state);
                    break;

                case "event":
                    HandleEvent(json, state);
                    break;

                default:
                    Console.WriteLine($"[Warning] Unknown message type: {messageType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to handle Unity message: {ex.Message}");
        }
    }

    private static async Task HandleHelloAsync(string json, WebSocket webSocket, BridgeState state, CancellationToken cancellationToken)
    {
        var hello = JsonHelper.Deserialize<HelloMessage>(json);
        if (hello == null)
        {
            Console.WriteLine("[Error] Failed to deserialize hello message");
            return;
        }

        // Store hello message for version info queries
        state.SetUnityHelloMessage(hello);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Hello from Unity");
        Console.WriteLine($"  Project ID: {hello.ProjectId}");
        Console.WriteLine($"  Unity Version: {hello.UnityVersion}");
        Console.WriteLine($"  Protocol Version: {hello.ProtocolVersion}");
        Console.WriteLine($"  Plugin Version: {hello.PluginVersion ?? "unknown"}");

        // Send hello response
        var response = new ResponseMessage
        {
            Origin = MessageOrigin.Bridge,
            RequestId = "hello",
            Status = ResponseStatus.Ok,
            Result = new
            {
                bridgeVersion = VersionInfo.Version,
                projectId = state.ProjectId,
                protocolVersion = "1.0.0"
            }
        };

        var responseJson = JsonHelper.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        );
    }

    private static void HandleResponse(string json, BridgeState state)
    {
        var response = JsonHelper.Deserialize<ResponseMessage>(json);
        if (response != null)
        {
            state.CompleteRequest(response.RequestId, response);
        }
    }

    private static void HandleEvent(string json, BridgeState state)
    {
        var eventMessage = JsonHelper.Deserialize<EventMessage>(json);
        if (eventMessage == null) return;

        // Handle state changes BEFORE notifying waiters, so that waiter continuations
        // see the updated state (e.g., IsDomainReloadInProgress is set before the
        // domain reload waiter's continuation checks it).
        switch (eventMessage.Event)
        {
            case UnityCtlEvents.Log:
                var logEntry = JsonConvert.DeserializeObject<LogEntry>(
                    JsonConvert.SerializeObject(eventMessage.Payload, JsonHelper.Settings),
                    JsonHelper.Settings
                );
                if (logEntry != null)
                {
                    state.AddConsoleLogEntry(logEntry);
                }
                break;

            case UnityCtlEvents.PlayModeChanged:
                var playModePayload = eventMessage.Payload as Newtonsoft.Json.Linq.JObject;
                var playModeState = playModePayload?["state"]?.ToString();
                Console.WriteLine($"[Event] Play mode changed: {playModeState}");

                if (playModeState == "EnteredPlayMode")
                {
                    state.ClearLogWatermark("entered play mode");
                    Console.WriteLine($"[Bridge] Auto-cleared logs on play mode enter");
                }
                break;

            case UnityCtlEvents.CompilationStarted:
                Console.WriteLine($"[Event] Compilation started");
                break;

            case UnityCtlEvents.CompilationFinished:
                Console.WriteLine($"[Event] Compilation finished");
                break;

            case UnityCtlEvents.AssetImportComplete:
                Console.WriteLine($"[Event] Asset import complete");
                break;

            case UnityCtlEvents.AssetReimportAllComplete:
                Console.WriteLine($"[Event] Asset reimport all complete");
                break;

            case UnityCtlEvents.TestFinished:
                Console.WriteLine($"[Event] Tests finished");
                break;

            case UnityCtlEvents.RecordFinished:
                Console.WriteLine($"[Event] Recording finished");
                break;

            case UnityCtlEvents.DomainReloadStarting:
                Console.WriteLine($"[Event] Domain reload starting");
                state.OnDomainReloadStarting();
                break;
        }

        // Notify waiters after state changes are applied
        state.ProcessEvent(eventMessage);
    }
}

public class RpcRequest
{
    public string? AgentId { get; set; }
    public required string Command { get; set; }
    public Dictionary<string, object?>? Args { get; set; }
}

internal class CommandConfig
{
    public required string TimeoutEnvVar { get; init; }
    public required int TimeoutDefaultSeconds { get; init; }
    public TimeSpan Timeout => GetTimeoutFromEnv(TimeoutEnvVar, TimeoutDefaultSeconds);
    public string? CompletionEvent { get; init; }               // WebSocket event from Unity
    public string? CompletionEventState { get; init; }          // Expected state in payload to consider event complete (e.g., "EnteredPlayMode")

    private static TimeSpan GetTimeoutFromEnv(string envVar, int defaultSeconds)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (int.TryParse(value, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(defaultSeconds);
    }
}
