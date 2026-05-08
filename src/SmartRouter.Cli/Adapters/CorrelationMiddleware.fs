module SmartRouter.Cli.Adapters.CorrelationMiddleware

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Serilog.Context

/// Key used in HttpContext.Items.
/// String constant avoids boxing allocation on repeated access.
[<Literal>]
let CorrelationIdKey = "CorrelationId"

/// Minimal ASP.NET Core middleware lambda.
/// Runs FIRST in the pipeline. Generates a correlation ID per request:
///   1. Stores in HttpContext.Items["CorrelationId"] — endpoint reads it for DecisionLog.
///   2. Pushes to Serilog LogContext — all Serilog log lines for this request carry correlation_id.
/// Function name MUST be correlationMiddleware (verified by must_haves grep).
/// (Pitfall P5: use _ for IDisposable disposal scope — LogContext property auto-removed when task ends.)
let correlationMiddleware (ctx: HttpContext) (next: RequestDelegate) : Task =
    task {
        let cid = Guid.NewGuid().ToString("N")           // 32-char hex, no dashes
        ctx.Items.[CorrelationIdKey] <- cid
        use _ = LogContext.PushProperty("correlation_id", cid)
        do! next.Invoke(ctx)
    }
