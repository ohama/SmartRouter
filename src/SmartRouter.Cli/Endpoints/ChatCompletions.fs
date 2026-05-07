module SmartRouter.Cli.Endpoints.ChatCompletions

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
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
/// 2. Phase 1 streaming policy: returns HTTP 501 on stream=true.
/// 3. Calls Routing.routeRequest with the DI-resolved RoutingConfig singleton.
/// 4. Forwards to IUpstreamClient.CompleteAsync on a successful routing decision.
/// 5. Returns OpenAI-shaped errors on routing failure or upstream error.
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

        // 2. Phase 1 streaming policy: 501 on stream=true
        if req.Stream then
            ctx.Response.StatusCode <- 501
            do! ctx.Response.WriteAsJsonAsync(
                    {| error = {| message = "streaming not yet implemented (Phase 2)"
                                  ``type`` = "not_implemented" |} |},
                    jsonOptions, ctx.RequestAborted)
        else

        // 3. Pure routing — config-driven. The RoutingConfig singleton was built from
        //    appsettings.json at startup; editing JSON + restart changes this behavior (ROUT-05).
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
            Log.Information(
                "Routing target={Target} reason={Reason} priority={Priority}",
                decision.Target, decision.Reason, decision.Priority)

            // 4. Forward to upstream client
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
