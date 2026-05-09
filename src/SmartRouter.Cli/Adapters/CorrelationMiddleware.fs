module SmartRouter.Cli.Adapters.CorrelationMiddleware

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Serilog.Context

/// Key used in HttpContext.Items.
/// String constant avoids boxing allocation on repeated access.
[<Literal>]
let CorrelationIdKey = "CorrelationId"

/// HTTP response header name for surfacing the per-request correlation ID
/// to API consumers and observability tooling. Issue #6.
[<Literal>]
let CorrelationIdHeader = "X-Correlation-Id"

/// Minimal ASP.NET Core middleware lambda.
/// Runs FIRST in the pipeline. Generates a correlation ID per request:
///   1. Stores in HttpContext.Items["CorrelationId"] — endpoint reads it for DecisionLog.
///   2. Pushes to Serilog LogContext — all Serilog log lines for this request carry correlation_id.
///   3. Sets `X-Correlation-Id` response header via OnStarting callback so it ships with
///      the response (works for both buffered and SSE streaming responses; #6).
/// Function name MUST be correlationMiddleware (verified by must_haves grep).
/// (Pitfall P5: use _ for IDisposable disposal scope — LogContext property auto-removed when task ends.)
let correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
    task {
        let cid = Guid.NewGuid().ToString("N")           // 32-char hex, no dashes
        ctx.Items.[CorrelationIdKey] <- cid
        use _ = LogContext.PushProperty("correlation_id", cid)
        // Register the response-header set BEFORE first byte ships. OnStarting fires after
        // any handler completes but before headers serialize, so it is safe for SSE too.
        ctx.Response.OnStarting(fun () ->
            ctx.Response.Headers.[CorrelationIdHeader] <- Microsoft.Extensions.Primitives.StringValues(cid)
            Task.CompletedTask) |> ignore
        do! next.Invoke(ctx)
    }
