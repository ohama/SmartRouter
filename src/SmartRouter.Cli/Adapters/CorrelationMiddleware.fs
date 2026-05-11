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
/// Empty string in Items = stateless request (no header, or whitespace-only header).
/// Sentinel matches RouterRequest.SessionId "" convention.
[<Literal>]
let SessionIdKey = "SessionId"

/// HTTP response header name for surfacing the per-request correlation ID
/// to API consumers and observability tooling. Issue #6.
[<Literal>]
let CorrelationIdHeader = "X-Correlation-Id"

/// Phase 18 — incoming session-identity header. Hermes Agent (Phase 20)
/// propagates session.id from upstream conversation context; clients
/// without this header continue to work statelessly (v1.x backward-compat).
[<Literal>]
let SessionIdHeader = "X-Session-Id"

/// Minimal ASP.NET Core middleware lambda.
/// Runs FIRST in the pipeline. Generates a correlation ID per request, reads X-Session-Id:
///   1. Stores correlation ID in HttpContext.Items["CorrelationId"] — endpoint reads it for DecisionLog.
///   2. Pushes correlation ID to Serilog LogContext — all Serilog log lines for this request carry correlation_id.
///   3. Stores X-Session-Id (or "" if absent/whitespace) in HttpContext.Items["SessionId"] — Phase 18 sticky.
///   4. Sets `X-Correlation-Id` response header via OnStarting callback so it ships with
///      the response (works for both buffered and SSE streaming responses; #6).
/// Function name MUST be correlationMiddleware (verified by must_haves grep).
/// (Pitfall P5: use _ for IDisposable disposal scope — LogContext property auto-removed when task ends.)
let correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
    task {
        let cid = Guid.NewGuid().ToString("N")           // 32-char hex, no dashes
        ctx.Items.[CorrelationIdKey] <- cid

        // Phase 18 — X-Session-Id header read.
        // Null-safe: header absent OR present-but-whitespace-only → empty string sentinel.
        // Empty string in Items prevents Pitfall 7 (Anti-Pattern 1 from 18-RESEARCH):
        // never call sessionStore.Update("") — would create one shared sticky bucket
        // for every v1.x backward-compat client and permanently escalate them all to 122B.
        let sessionId =
            match ctx.Request.Headers.TryGetValue(SessionIdHeader) with
            | true, sv when sv.Count > 0 && not (String.IsNullOrWhiteSpace(sv.[0])) -> sv.[0]
            | _ -> ""
        ctx.Items.[SessionIdKey] <- sessionId

        use _ = LogContext.PushProperty("correlation_id", cid)
        // Register the response-header set BEFORE first byte ships. OnStarting fires after
        // any handler completes but before headers serialize, so it is safe for SSE too.
        ctx.Response.OnStarting(fun () ->
            ctx.Response.Headers.[CorrelationIdHeader] <- Microsoft.Extensions.Primitives.StringValues(cid)
            Task.CompletedTask) |> ignore
        do! next.Invoke(ctx)
    }
