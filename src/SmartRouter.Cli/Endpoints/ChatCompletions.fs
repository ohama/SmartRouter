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
open SmartRouter.Cli.Adapters.Json

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
let private mapWireToRequest (wire: RouterRequestWire) : RouterRequest =
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

    { Messages     = messages
      ModelOverride = if String.IsNullOrEmpty(wire.model) then None else Some wire.model
      Task          = if String.IsNullOrWhiteSpace(wire.task) then None else Some (wire.task.Trim())
      Stream        = wire.stream.HasValue && wire.stream.Value
      Temperature   = if wire.temperature.HasValue then Some wire.temperature.Value else None
      TopP          = if wire.top_p.HasValue then Some wire.top_p.Value else None
      MaxTokens     = if wire.max_tokens.HasValue then Some wire.max_tokens.Value else None
      UnknownFields = unknownFields }

// ── Handler ──────────────────────────────────────────────────────────────────

/// POST /v1/chat/completions handler.
///
/// 1. Parses wire body → RouterRequest.
/// 2. Calls Routing.routeRequest with the DI-resolved RoutingConfig singleton.
///    Routing errors return HTTP 400 with normal JSON body — no SSE headers set.
/// 3. On Ok decision AND stream=true: SSE forward loop (Phase 2 streaming).
/// 4. On Ok decision AND stream=false: calls IUpstreamClient.CompleteAsync (Phase 1 path).
/// 5. Returns OpenAI-shaped errors on routing failure or upstream error.
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
    (routingConfig : RoutingConfig)
    (upstream      : IUpstreamClient)
    (ctx           : HttpContext) : Task =
    task {
        // 1. Parse wire body — use wireJsonOptions (allows missing/null fields for optional wire fields)
        let! wireBody = ctx.Request.ReadFromJsonAsync<RouterRequestWire>(wireJsonOptions, ctx.RequestAborted)

        if isNull (wireBody :> obj) then
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = "request body is required"
                                  ``type`` = "invalid_request_error" |} |},
                    jsonOptions, ctx.RequestAborted)
        else

        let req = mapWireToRequest wireBody

        // 2. Pure routing — config-driven. The RoutingConfig singleton was built from
        //    appsettings.json at startup; editing JSON + restart changes this behavior (ROUT-05).
        //    Routing errors return HTTP 400 with normal JSON body BEFORE any SSE headers are set
        //    (STRM-04 ordering: the streaming branch is only entered after a successful routing Ok decision).
        match routeRequest routingConfig req with
        | Error (UnsupportedTask raw) ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = $"unknown task: {raw}"
                                  ``type`` = "invalid_request_error" |} |},
                    jsonOptions, ctx.RequestAborted)

        | Error e ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = string e
                                  ``type`` = "invalid_request_error" |} |},
                    jsonOptions, ctx.RequestAborted)

        | Ok decision ->

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
                let chunks = upstream.StreamAsync req decision.Target ct

                // Manual enumerator loop — required for precise disposal semantics.
                // F# task {} does not support do! in finally blocks, so enumerator.DisposeAsync()
                // is called explicitly in every exit path (normal, cancel, unexpected error).
                // DisposeAsync on the taskSeq enumerator chains to use _ = resp in StreamAsync,
                // which disposes the HttpResponseMessage and closes the upstream socket. (STRM-05 / PITFALL-4)
                let enumerator = chunks.GetAsyncEnumerator(ct)
                let mutable sentDone = false
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
                                let errMsg = sprintf "data: {\"error\":{\"message\":\"%s\",\"type\":\"upstream_error\"}}\n\n" (string e)
                                let errBytes = Encoding.UTF8.GetBytes(errMsg)
                                do! ctx.Response.Body.WriteAsync(errBytes, 0, errBytes.Length, ct)
                                do! ctx.Response.Body.FlushAsync(ct)
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
                    do! enumerator.DisposeAsync()

                with
                | :? OperationCanceledException ->
                    // Client disconnected mid-stream (ctx.RequestAborted fired).
                    // No more writes possible — log and dispose.
                    Log.Information("StreamAsync: client disconnected mid-stream for {Target}", decision.Target)
                    do! enumerator.DisposeAsync()
                | ex ->
                    Log.Error(ex, "StreamAsync: unexpected error writing to response for {Target}", decision.Target)
                    do! enumerator.DisposeAsync()

            else
                // ── Non-streaming branch (unchanged from Phase 1) ────────────────────
                Log.Information(
                    "Routing target={Target} reason={Reason} priority={Priority}",
                    decision.Target, decision.Reason, decision.Priority)

                let! result = upstream.CompleteAsync req decision.Target ctx.RequestAborted

                match result with
                | Ok body ->
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(body, ctx.RequestAborted)

                | Error e ->
                    ctx.Response.StatusCode <- 502
                    do! ctx.Response.WriteAsJsonAsync(
                            {| error = {| message = string e
                                          ``type`` = "upstream_error" |} |},
                            jsonOptions, ctx.RequestAborted)
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
        let routingConfig = ctx.RequestServices.GetRequiredService<RoutingConfig>()
        let upstream      = ctx.RequestServices.GetRequiredService<IUpstreamClient>()
        handler routingConfig upstream ctx)) |> ignore
