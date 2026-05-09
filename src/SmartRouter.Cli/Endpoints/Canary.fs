module SmartRouter.Cli.Endpoints.Canary

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open SmartRouter.Cli.Adapters.CanaryService
open SmartRouter.Cli.Adapters.Json

/// Map four canary endpoints. Mirrors Endpoints/Stats.fs registration shape.
/// All loopback-only (Kestrel binds 127.0.0.1:4000 in production; tests bind 127.0.0.1:0).
let mapEndpoints (app: WebApplication) =

    // GET /canary → status JSON
    app.MapGet("/canary", Func<HttpContext, Task>(fun ctx ->
        task {
            let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Canary")
            logger.LogDebug("/canary GET hit")
            let svc = ctx.RequestServices.GetRequiredService<ICanaryService>()
            let! status = svc.GetStatusAsync(ctx.RequestAborted)
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync(status, jsonOptions, ctx.RequestAborted)
        })) |> ignore

    // POST /canary/promote → 200 (newVersion) | 404 (no canary) | 409 (retrain busy) | 500 (error)
    app.MapPost("/canary/promote", Func<HttpContext, Task>(fun ctx ->
        task {
            let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Canary")
            let svc = ctx.RequestServices.GetRequiredService<ICanaryService>()
            let! result = svc.PromoteAsync(ctx.RequestAborted)
            match result with
            | Promoted v ->
                logger.LogDebug("/canary POST promote hit; result=promoted new_version={V}", v)
                ctx.Response.StatusCode <- 200
                do! ctx.Response.WriteAsJsonAsync({| status = "promoted"; new_baseline_version = v |}, jsonOptions, ctx.RequestAborted)
            | NoCanaryFile ->
                logger.LogDebug("/canary POST promote hit; result=no_canary_file")
                ctx.Response.StatusCode <- 404
                do! ctx.Response.WriteAsJsonAsync({| error = "no canary file at configured CanaryModelPath" |}, jsonOptions, ctx.RequestAborted)
            | RetrainInProgress ->
                logger.LogDebug("/canary POST promote hit; result=retrain_in_progress")
                ctx.Response.StatusCode <- 409
                do! ctx.Response.WriteAsJsonAsync({| error = "retrain in progress; try again momentarily" |}, jsonOptions, ctx.RequestAborted)
            | Failed err ->
                logger.LogDebug("/canary POST promote hit; result=failed error={Err}", err)
                ctx.Response.StatusCode <- 500
                do! ctx.Response.WriteAsJsonAsync({| error = err |}, jsonOptions, ctx.RequestAborted)
        })) |> ignore

    // POST /canary/rollback → 200, idempotent
    app.MapPost("/canary/rollback", Func<HttpContext, Task>(fun ctx ->
        task {
            let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Canary")
            let svc = ctx.RequestServices.GetRequiredService<ICanaryService>()
            let reason =
                match ctx.Request.Query.TryGetValue("reason") with
                | true, sv when sv.Count > 0 -> sv.[0]
                | _ -> "operator-rollback"
            logger.LogDebug("/canary POST rollback hit; reason={Reason}", reason)
            svc.RollbackAsync(reason)
            ctx.Response.StatusCode <- 200
            do! ctx.Response.WriteAsJsonAsync({| status = "rolled_back" |}, jsonOptions, ctx.RequestAborted)
        })) |> ignore

    // POST /canary/enable?percentage=N → 200 (set) | 400 (bad arg)
    app.MapPost("/canary/enable", Func<HttpContext, Task>(fun ctx ->
        task {
            let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Canary")
            let svc = ctx.RequestServices.GetRequiredService<ICanaryService>()
            let pctOpt =
                match ctx.Request.Query.TryGetValue("percentage") with
                | true, sv when sv.Count > 0 ->
                    match Int32.TryParse(sv.[0]) with
                    | true, n when n >= 0 && n <= 100 -> Some n
                    | _ -> None
                | _ -> None
            match pctOpt with
            | None ->
                logger.LogDebug("/canary POST enable hit; result=bad_arg")
                ctx.Response.StatusCode <- 400
                do! ctx.Response.WriteAsJsonAsync({| error = "percentage query parameter required, integer 0..100" |}, jsonOptions, ctx.RequestAborted)
            | Some p ->
                logger.LogDebug("/canary POST enable hit; percentage={P}", p)
                svc.EnableAsync(p)
                ctx.Response.StatusCode <- 200
                do! ctx.Response.WriteAsJsonAsync({| status = "enabled"; percentage = p |}, jsonOptions, ctx.RequestAborted)
        })) |> ignore
