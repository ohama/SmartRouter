module SmartRouter.Cli.Endpoints.Health

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.Json   // jsonOptions

let private formatTimestamp (ts: DateTimeOffset) : obj =
    if ts = DateTimeOffset.MinValue then box "never"
    else box (ts.UtcDateTime.ToString("o"))

let mapEndpoints (app: WebApplication) =
    let handler =
        Func<HttpContext, System.Threading.Tasks.Task>(fun ctx ->
            task {
                let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Health")
                logger.LogDebug("/health hit; method={Method}; path={Path}", ctx.Request.Method, ctx.Request.Path.Value)
                let probe = ctx.RequestServices.GetRequiredService<IHealthProbe>()
                let r35  = probe.IsReachable(Qwen35B)
                let r122 = probe.IsReachable(Qwen122B)
                let t35  = probe.LastProbedAt(Qwen35B)  |> formatTimestamp
                let t122 = probe.LastProbedAt(Qwen122B) |> formatTimestamp
                let body =
                    {| qwen35b  = {| reachable = r35;  last_probed_at = t35  |}
                       qwen122b = {| reachable = r122; last_probed_at = t122 |} |}
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsJsonAsync(body, jsonOptions, ctx.RequestAborted)
            } :> System.Threading.Tasks.Task)
    app.MapGet("/health",  handler) |> ignore
    // Issue #10: /healthz alias for k8s/cloud probe convention.
    app.MapGet("/healthz", handler) |> ignore
