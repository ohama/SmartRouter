module SmartRouter.Cli.Endpoints.Stats

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Cli.Adapters.QueueDispatcher
open SmartRouter.Cli.Adapters.Json

/// Wire shape for GET /stats. snake_case to match OpenAI conventions.
/// Built fresh from a StatsSnapshot on every request — no caching.
type private StatsWire =
    { timestamp                 : string
      active_122b               : int
      queue_depth_122b_high     : int
      queue_depth_122b_low      : int
      active_35b                : int
      requests_per_sec          : float
      avg_latency_ms_60s        : float
      failure_count_total       : int64
      fairness_picks_high       : int64
      fairness_picks_low        : int64
      semaphore_available       : int }

let private toWire (s: StatsSnapshot) : StatsWire =
    { timestamp                 = s.Timestamp.ToString("o")
      active_122b               = s.Active122B
      queue_depth_122b_high     = s.QueueDepth122BHigh
      queue_depth_122b_low      = s.QueueDepth122BLow
      active_35b                = s.Active35B
      requests_per_sec          = s.RequestsPerSec
      avg_latency_ms_60s        = s.AvgLatencyMs60s
      failure_count_total       = s.FailureCountTotal
      fairness_picks_high       = s.FairnessPicksHigh
      fairness_picks_low        = s.FairnessPicksLow
      semaphore_available       = s.SemaphoreAvailable }

/// Register GET /stats. Resolves IStatsProvider from DI on each request and
/// serializes a fresh snapshot. No caching: the snapshot is cheap (Volatile.Read +
/// two locks) and operators want live values, not stale ones.
let mapEndpoints (app: WebApplication) =
    app.MapGet("/stats", Func<HttpContext, Task>(fun ctx ->
        task {
            let stats = ctx.RequestServices.GetRequiredService<IStatsProvider>()
            let wire = toWire (stats.GetSnapshot())
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync(wire, jsonOptions, ctx.RequestAborted)
        })) |> ignore
