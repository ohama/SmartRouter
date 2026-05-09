module SmartRouter.Cli.Endpoints.ChatCompletions

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Core.Routing
open SmartRouter.Core.RetrainingPorts   // IModelVersionProvider
open SmartRouter.Cli.Adapters.Json
open SmartRouter.Cli.Adapters.DecisionLogger
open SmartRouter.Cli.Adapters.CorrelationMiddleware
open SmartRouter.Cli.Adapters.RoutingAlgorithm
open SmartRouter.Cli.Adapters.CanaryMetrics

// ── Wire types ───────────────────────────────────────────────────────────────

/// Wire format for a single message in the request body.
[<CLIMutable>]
type WireMessage =
    { role    : string
      content : string }

/// Wire format for the POST /v1/chat/completions request body.
/// [<JsonExtensionData>] captures any unrecognized JSON properties into `extra`
/// so they can be forwarded verbatim to the upstream (API-04 / ROUT-07).
/// Uses [<CLIMutable>] with standard .NET types for standard STJ compatibility
/// (no FSharpConverter on wireJsonOptions — that avoids missing-field errors).
[<CLIMutable>]
type RouterRequestWire =
    { mutable messages    : WireMessage[]
      mutable model       : string                    // null when absent
      mutable stream      : Nullable<bool>
      mutable temperature : Nullable<float>
      mutable top_p       : Nullable<float>
      mutable max_tokens  : Nullable<int>
      mutable task        : string                    // null when absent
      [<JsonExtensionData>]
      mutable extra       : Dictionary<string, JsonElement> }

// ── Wire → Domain mapping ────────────────────────────────────────────────────

/// Map a wire role string to a MessageRole DU case.
/// Case-insensitive. Unknown roles default to User.
let private parseRole (s: string) : MessageRole =
    match (if isNull s then "" else s.ToLowerInvariant()) with
    | "system"    -> MessageRole.System
    | "assistant" -> MessageRole.Assistant
    | _           -> MessageRole.User

/// Convert the wire request type to the Core RouterRequest domain type.
/// Phase 9: correlationId threaded through so RouterRequest.CorrelationId carries
/// the HttpContext-extracted value into Core for the canary gate (ML.fs).
let private mapWireToRequest (correlationId: string) (wire: RouterRequestWire) : RouterRequest =
    let messages =
        if isNull wire.messages then []
        else
            wire.messages
            |> Array.toList
            |> List.map (fun m ->
                { Role    = parseRole m.role
                  Content = if isNull m.content then "" else m.content })

    let unknownFields =
        if isNull wire.extra then Map.empty
        else
            wire.extra
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Map.ofSeq

    { Messages      = messages
      ModelOverride  = if String.IsNullOrEmpty(wire.model) then None else Some wire.model
      Task           = if String.IsNullOrWhiteSpace(wire.task) then None else Some (wire.task.Trim())
      Stream         = wire.stream.HasValue && wire.stream.Value
      Temperature    = if wire.temperature.HasValue then Some wire.temperature.Value else None
      TopP           = if wire.top_p.HasValue then Some wire.top_p.Value else None
      MaxTokens      = if wire.max_tokens.HasValue then Some wire.max_tokens.Value else None
      CorrelationId  = correlationId   // NEW Phase 9
      UnknownFields  = unknownFields }

// ── DecisionLog helpers ──────────────────────────────────────────────────────

/// Escape a string for safe inclusion in a JSON string value.
/// Handles backslash, double-quote, newline, and carriage return.
let private escapeJsonString (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

/// Build a DecisionLog from request + routing outcome.
/// Phase 9: model_version cascades from RoutingDecision.ModelVersion (load-bearing for ML cohort)
/// to IModelVersionProvider.CurrentVersion (fallback for non-ML stages: override / task table / heuristic).
/// decisionOpt = None for pre-routing failures (null body, parse error).
let private buildDecisionLog
    (req             : RouterRequest)
    (regn            : RoutingAlgorithmRegistration)
    (versionProvider : IModelVersionProvider)
    (correlationId   : string)
    (started         : DateTimeOffset)
    (decisionOpt     : RoutingDecision option)
    (target          : string)
    (reason          : string)
    (fallbackUsed    : bool)
    : DecisionLog =
    let modelVersion =
        match decisionOpt with
        | Some d when not (String.IsNullOrEmpty(d.ModelVersion)) -> d.ModelVersion
        | _ -> versionProvider.CurrentVersion
    { schema_version           = 1
      correlation_id           = correlationId
      prompt_hash              = computePromptHash req.Messages
      prompt_korean_char_ratio = computeKoreanRatio req.Messages
      routing_algorithm        = regn.Name
      routing_reason           = reason
      target                   = target
      latency_ms               = (DateTimeOffset.UtcNow - started).TotalMilliseconds
      fallback_used            = fallbackUsed
      model_version            = modelVersion
      task_type                = req.Task
      timestamp                = DateTimeOffset.UtcNow }

// ── Canary metric helper ─────────────────────────────────────────────────────

/// Compute (isCanary, isFallback) for the rolling-60s metric (Lock 15).
/// Called AFTER the response is written so the metric reflects actual user-facing outcome.
let private metricCohort (decision: RoutingDecision) (reason: string) : bool * bool =
    let isCanary    = decision.ModelVersion.EndsWith("-canary", StringComparison.Ordinal)
    let suffixFallback =
        reason.EndsWith(";upstream_error", StringComparison.Ordinal)
        || reason.EndsWith(";stream_error", StringComparison.Ordinal)
    let isFallback  = decision.IsFallback || suffixFallback
    isCanary, isFallback

// ── Handler ──────────────────────────────────────────────────────────────────

/// POST /v1/chat/completions handler.
///
/// 1. Parses wire body → RouterRequest.
/// 2. Calls Routing.routeRequest with the DI-resolved RoutingConfig singleton.
///    Routing errors return HTTP 400 with normal JSON body — no SSE headers set.
/// 3. On Ok decision AND stream=true: SSE forward loop (Phase 2 streaming).
/// 4. On Ok decision AND stream=false: calls IUpstreamClient.CompleteAsync (Phase 1 path).
/// 5. Returns OpenAI-shaped errors on routing failure or upstream error.
/// 6. Emits a DecisionLog at EVERY exit point (Phase 5 wiring — LOG-01 / OBS-01).
///
/// SSE pitfalls addressed:
///   STRM-04 / PITFALL-6: SSE headers set BEFORE any WriteAsync call.
///   STRM-03 / PITFALL-3: FlushAsync called after every WriteAsync in the chunk loop.
///   STRM-05 / PITFALL-4: enumerator.DisposeAsync() in finally — chains to use _ = resp in StreamAsync.
///   STRM-07 / PITFALL-19: Strategy D [DONE] injection — sentDone flag tracks sentinel; inject if missing.
///   Routing errors (UnsupportedTask etc.) return HTTP 400 BEFORE SSE headers are written (STRM-04 ordering).
///
/// The RoutingConfig parameter MUST come from DI (resolved by mapEndpoints below).
/// This is the explicit wiring that satisfies ROUT-05 / CONTEXT.md's config-driven
/// dispatch requirement: editing appsettings.json + restarting rebuilds this
/// singleton, changing runtime dispatch without recompile.
let handler
    (routingConfig   : RoutingConfig)
    (regn            : RoutingAlgorithmRegistration)
    (versionProvider : IModelVersionProvider)
    (decisionLogger  : IDecisionLogger)
    (metrics         : ICanaryMetrics)        // NEW Phase 9 — canary rolling metric
    (upstream        : IUpstreamClient)
    (ctx             : HttpContext) : Task =
    task {
        // Capture start time and correlation ID at the very top of the handler.
        // correlationId fallback is defensive — if CorrelationMiddleware is somehow
        // bypassed, the request still gets a unique ID rather than null/empty.
        let started = DateTimeOffset.UtcNow
        let correlationId =
            match ctx.Items.TryGetValue(CorrelationIdKey) with
            | true, (:? string as cid) when not (String.IsNullOrEmpty(cid)) -> cid
            | _ -> Guid.NewGuid().ToString("N")

        // 1. Parse wire body — use wireJsonOptions (allows missing/null fields for optional wire fields)
        let! wireBody = ctx.Request.ReadFromJsonAsync<RouterRequestWire>(wireJsonOptions, ctx.RequestAborted)

        if isNull (wireBody :> obj) then
            // Null body — log with synthetic empty request (no messages, no task).
            let emptyReq =
                { Messages      = []
                  ModelOverride  = None
                  Task           = None
                  Stream         = false
                  Temperature    = None
                  TopP           = None
                  MaxTokens      = None
                  CorrelationId  = correlationId   // NEW Phase 9 — even synthetic requests carry the correlation ID
                  UnknownFields  = Map.empty }
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = "request body is required"
                                  ``type`` = "invalid_request_error" |} |},
                    jsonOptions, ctx.RequestAborted)
            decisionLogger.Log(buildDecisionLog emptyReq regn versionProvider correlationId started None "unknown" "error:null_body" false)
        else

        let req = mapWireToRequest correlationId wireBody

        // 2. Pure routing — config-driven. The RoutingConfig singleton was built from
        //    appsettings.json at startup; editing JSON + restart changes this behavior (ROUT-05).
        //    Routing errors return HTTP 400 with normal JSON body BEFORE any SSE headers are set
        //    (STRM-04 ordering: the streaming branch is only entered after a successful routing Ok decision).
        match routeRequest routingConfig regn.Algorithm req with
        | Error (UnsupportedTask raw) ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = $"unknown task: {raw}"
                                  ``type`` = "invalid_request_error" |} |},
                    jsonOptions, ctx.RequestAborted)
            decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started None "unknown" (sprintf "error:unsupported_task:%s" raw) false)

        | Error e ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = string e
                                  ``type`` = "invalid_request_error" |} |},
                    jsonOptions, ctx.RequestAborted)
            decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started None "unknown" (sprintf "error:%A" e) false)

        | Ok decision ->
            // ── Phase 10: pre-flight + fallback rebind (BEFORE SSE headers / stream branch) ─
            let healthProbe = ctx.RequestServices.GetRequiredService<IHealthProbe>()
            let isGraphIndexing =
                req.Task
                |> Option.map (fun t -> t.Trim().ToLowerInvariant())
                |> (=) (Some "graph_indexing")
            let isGraphIndexingFallback =
                decision.Target = Qwen122B
                && isGraphIndexing
                && not (healthProbe.IsReachable(Qwen122B))

            if isGraphIndexingFallback then
                // graph_indexing-must-fail: HTTP 503 + structured error JSON, BEFORE any SSE headers.
                // fallback_used = false (this is a hard error, not a fallback).
                ctx.Response.StatusCode  <- 503
                ctx.Response.ContentType <- "application/json"
                let body =
                    {| error = {| message = "Task 'graph_indexing' requires Qwen122B which is currently unreachable; fallback policy does not apply for graph_indexing."
                                  ``type`` = "model_unavailable"
                                  correlation_id = correlationId |} |}
                do! ctx.Response.WriteAsJsonAsync(body, jsonOptions, ctx.RequestAborted)
                let reason = formatReason decision.Reason + ";graph_indexing_must_fail"
                decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started (Some decision) (sprintf "%A" decision.Target) reason false)
                return ()   // EARLY-RETURN: skip the rest of the Ok arm
            else
                ()   // fall through to the rebind + existing body

            // ── Phase 10: 35B reroute rebind (transparent fallback) ──────────────────
            let decision =   // shadows the parameter
                if decision.Target = Qwen122B
                   && not isGraphIndexing
                   && not (healthProbe.IsReachable(Qwen122B)) then
                    Log.Warning(
                        "ChatCompletions: 122B unreachable; rerouting task={Task} to 35B (fallback)",
                        req.Task)
                    { decision with
                        Target     = Qwen35B
                        Reason     = FallbackTo35B
                        IsFallback = true }
                else
                    decision

            // ── existing body UNCHANGED from here onwards ─────────────────────────────
            if req.Stream then
                // ── SSE streaming branch ──────────────────────────────────────────────
                // STRM-04 / PITFALL-6: Set all SSE headers BEFORE writing any body bytes.
                // Once any WriteAsync runs, headers are committed and cannot be changed.
                ctx.Response.ContentType <- "text/event-stream"
                ctx.Response.Headers["Cache-Control"]     <- StringValues "no-cache"
                ctx.Response.Headers["X-Accel-Buffering"] <- StringValues "no"
                ctx.Response.Headers["Connection"]        <- StringValues "keep-alive"
                // Transfer-Encoding: chunked is applied automatically by Kestrel when
                // Content-Length is absent. Do NOT set Content-Length or Transfer-Encoding.

                Log.Information(
                    "Routing target={Target} reason={Reason} priority={Priority} stream=true",
                    decision.Target, decision.Reason, decision.Priority)

                let ct = ctx.RequestAborted
                let chunks = upstream.StreamAsync req decision ct

                // Manual enumerator loop — required for precise disposal semantics.
                // F# task {} does not support do! in finally blocks, so enumerator.DisposeAsync()
                // is called explicitly in every exit path (normal, cancel, unexpected error).
                // DisposeAsync on the taskSeq enumerator chains to use _ = resp in StreamAsync,
                // which disposes the HttpResponseMessage and closes the upstream socket. (STRM-05 / PITFALL-4)
                let enumerator = chunks.GetAsyncEnumerator(ct)
                let mutable sentDone = false
                let mutable streamError = false
                try
                    let mutable go = true
                    while go do
                        let! hasNext = enumerator.MoveNextAsync()
                        if not hasNext then
                            go <- false
                        else
                            match enumerator.Current with
                            | Error e ->
                                // Headers already sent — cannot return HTTP 502.
                                // Emit a best-effort SSE error event in OpenAI error shape.
                                // LOG-04 / OBS-03: correlation_id MUST be included in SSE error body.
                                let errMsg =
                                    sprintf
                                        "data: {\"error\":{\"message\":\"%s\",\"type\":\"upstream_error\",\"correlation_id\":\"%s\"}}\n\n"
                                        (escapeJsonString (string e))
                                        correlationId
                                let errBytes = Encoding.UTF8.GetBytes(errMsg)
                                do! ctx.Response.Body.WriteAsync(errBytes, 0, errBytes.Length, ct)
                                do! ctx.Response.Body.FlushAsync(ct)
                                streamError <- true
                                go <- false
                            | Ok chunk ->
                                // Strategy D (STRM-07 / PITFALL-19): track whether this chunk contains [DONE].
                                if chunk.Contains("[DONE]") then sentDone <- true
                                // ReadLineAsync strips the trailing \n; re-append \n\n to restore
                                // complete SSE event framing before writing to the downstream client.
                                let eventLine = chunk + "\n\n"
                                let bytes = Encoding.UTF8.GetBytes(eventLine)
                                do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ct)
                                do! ctx.Response.Body.FlushAsync(ct)  // STRM-03 / PITFALL-3: flush after EVERY chunk

                    // Strategy D injection: upstream did not send [DONE]; inject it now.
                    // Only on normal loop exit — NOT after OperationCanceledException (connection gone).
                    if not sentDone then
                        let doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n")
                        do! ctx.Response.Body.WriteAsync(doneBytes, 0, doneBytes.Length, ct)
                        do! ctx.Response.Body.FlushAsync(ct)

                    // Normal path disposal — triggers use _ = resp in StreamAsync → upstream socket close.
                    // DecisionLog AFTER disposal so latency_ms reflects time-to-last-byte (LOG-01).
                    do! enumerator.DisposeAsync()
                    let reason =
                        if streamError
                        then formatReason decision.Reason + ";stream_error"
                        else formatReason decision.Reason
                    decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started (Some decision) (sprintf "%A" decision.Target) reason decision.IsFallback)
                    let isCanary, isFb = metricCohort decision reason
                    metrics.Record(isCanary, isFb)

                with
                | :? OperationCanceledException ->
                    // Client disconnected mid-stream (ctx.RequestAborted fired).
                    // No more writes possible — log and dispose.
                    Log.Information("StreamAsync: client disconnected mid-stream for {Target}", decision.Target)
                    do! enumerator.DisposeAsync()
                    let cancelReason = formatReason decision.Reason + ";cancelled"
                    decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started (Some decision) (sprintf "%A" decision.Target) cancelReason decision.IsFallback)
                    let isCanary, isFb = metricCohort decision cancelReason
                    metrics.Record(isCanary, isFb)
                | ex ->
                    Log.Error(ex, "StreamAsync: unexpected error writing to response for {Target}", decision.Target)
                    do! enumerator.DisposeAsync()
                    let errReason = formatReason decision.Reason + ";stream_error"
                    decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started (Some decision) (sprintf "%A" decision.Target) errReason decision.IsFallback)
                    let isCanary, isFb = metricCohort decision errReason
                    metrics.Record(isCanary, isFb)

            else
                // ── Non-streaming branch (unchanged from Phase 1) ────────────────────
                Log.Information(
                    "Routing target={Target} reason={Reason} priority={Priority}",
                    decision.Target, decision.Reason, decision.Priority)

                let! result = upstream.CompleteAsync req decision ctx.RequestAborted

                match result with
                | Ok body ->
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(body, ctx.RequestAborted)
                    let okReason = formatReason decision.Reason
                    decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started (Some decision) (sprintf "%A" decision.Target) okReason decision.IsFallback)
                    let isCanary, isFb = metricCohort decision okReason
                    metrics.Record(isCanary, isFb)

                | Error e ->
                    ctx.Response.StatusCode <- 502
                    do! ctx.Response.WriteAsJsonAsync(
                            {| error = {| message = string e
                                          ``type`` = "upstream_error" |} |},
                            jsonOptions, ctx.RequestAborted)
                    let upstreamErrReason = formatReason decision.Reason + ";upstream_error"
                    decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started (Some decision) (sprintf "%A" decision.Target) upstreamErrReason decision.IsFallback)
                    let isCanary, isFb = metricCohort decision upstreamErrReason
                    metrics.Record(isCanary, isFb)
    }

// ── Endpoint registration ────────────────────────────────────────────────────

/// Register the POST /v1/chat/completions route on the WebApplication.
///
/// The RoutingConfig singleton is resolved from DI via GetRequiredService<RoutingConfig>().
/// This explicit resolution is the load-bearing ROUT-05 wiring: the singleton was built
/// from appsettings.json by CompositionRoot.buildRoutingConfig, so editing the JSON +
/// restarting changes the runtime routing behavior.
let mapEndpoints (app: WebApplication) =
    app.MapPost("/v1/chat/completions", Func<HttpContext, Task>(fun ctx ->
        let routingConfig    = ctx.RequestServices.GetRequiredService<RoutingConfig>()
        let regn             = ctx.RequestServices.GetRequiredService<RoutingAlgorithmRegistration>()
        let versionProvider  = ctx.RequestServices.GetRequiredService<IModelVersionProvider>()
        let decisionLogger   = ctx.RequestServices.GetRequiredService<IDecisionLogger>()
        let metrics          = ctx.RequestServices.GetRequiredService<ICanaryMetrics>()  // NEW Phase 9
        let upstream         = ctx.RequestServices.GetRequiredService<IUpstreamClient>()
        handler routingConfig regn versionProvider decisionLogger metrics upstream ctx)) |> ignore
