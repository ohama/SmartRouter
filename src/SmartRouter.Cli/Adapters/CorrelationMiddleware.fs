module SmartRouter.Cli.Adapters.CorrelationMiddleware

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Serilog.Context

/// Key used in HttpContext.Items.
/// String constant avoids boxing allocation on repeated access.
[<Literal>]
let CorrelationIdKey = "CorrelationId"

/// Phase 18 — key for the X-Session-Id-derived session identifier in HttpContext.Items.
/// Empty string in Items = no header (Tier 1 absent); cascade in ChatCompletions.fs
/// (Phase 22 TIER-01..03) resolves Tier 2 (sysprompt) and Tier 3 (content fingerprint)
/// after mapWireToRequest constructs the RouterRequest.
[<Literal>]
let SessionIdKey = "SessionId"

/// HTTP response header name for surfacing the per-request correlation ID
/// to API consumers and observability tooling. Issue #6.
[<Literal>]
let CorrelationIdHeader = "X-Correlation-Id"

/// Phase 18 — incoming session-identity header (Tier 1 of the v2.1 cascade).
/// Hermes Agent operators using --pass-session-id propagate session.id via
/// the system-prompt path (Tier 2) — see HermesSessionExtract.fs. Clients
/// without this header rely on Tier 2/3 fallback resolved in ChatCompletions.fs.
[<Literal>]
let SessionIdHeader = "X-Session-Id"

/// Minimal ASP.NET Core middleware lambda.
/// Runs FIRST in the pipeline. Generates a correlation ID per request, reads X-Session-Id:
///   1. Stores correlation ID in HttpContext.Items["CorrelationId"] — endpoint reads it for DecisionLog.
///   2. Pushes correlation ID to Serilog LogContext — all Serilog log lines for this request carry correlation_id.
///   3. Stores X-Session-Id in HttpContext.Items["SessionId"] — Phase 18 sticky / Phase 22 Tier 1.
///      Whitespace-only or absent header is coerced to "" (empty-string sentinel); the v2.1 cascade in
///      ChatCompletions.fs resolves Tier 2/3 after the body is parsed (TIER-02 fall-through).
///   4. Sets `X-Correlation-Id` response header via OnStarting callback so it ships with
///      the response (works for both buffered and SSE streaming responses; #6).
/// Function name MUST be correlationMiddleware (verified by must_haves grep).
/// (Pitfall P5: use _ for IDisposable disposal scope — LogContext property auto-removed when task ends.)
let correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
    task {
        let cid = Guid.NewGuid().ToString("N")           // 32-char hex, no dashes
        ctx.Items.[CorrelationIdKey] <- cid

        // Phase 18 / Phase 22 — X-Session-Id header read (Tier 1).
        // Whitespace-only or absent → "" sentinel; cascade in ChatCompletions.fs
        // (resolveSessionCascade) resolves Tier 2/3 after body parse (TIER-02).
        // Empty string in Items prevents Pitfall 7 (Anti-Pattern 1 from 18-RESEARCH):
        // sessionStore.Update("") would create one shared sticky bucket for every
        // stateless v1.x client. Phase 22 cascade fills the sentinel with Tier 2 or
        // Tier 3 value before reaching the sticky Update site at ChatCompletions.fs:~418.
        let sessionId =
            match ctx.Request.Headers.TryGetValue(SessionIdHeader) with
            | true, sv when sv.Count > 0 && not (String.IsNullOrWhiteSpace(sv.[0])) ->
                sv.[0]   // explicit header wins (Tier 1)
            | _ ->
                ""   // sentinel — Phase 22 cascade resolves Tier 2 (sysprompt) or Tier 3 (content fingerprint)
        ctx.Items.[SessionIdKey] <- sessionId

        use _ = LogContext.PushProperty("correlation_id", cid)
        // Register the response-header set BEFORE first byte ships. OnStarting fires after
        // any handler completes but before headers serialize, so it is safe for SSE too.
        ctx.Response.OnStarting(fun () ->
            ctx.Response.Headers.[CorrelationIdHeader] <- Microsoft.Extensions.Primitives.StringValues(cid)
            Task.CompletedTask) |> ignore
        do! next.Invoke(ctx)
    }
