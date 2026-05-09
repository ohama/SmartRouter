module SmartRouter.Cli.Adapters.HealthService

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.QwenUpstreamClient   // UpstreamOptions

/// Health probe configuration bound from appsettings.json `Routing.Health`.
[<CLIMutable>]
type HealthOptions =
    { PollingIntervalSeconds       : int
      ConsecutiveFailureThreshold  : int }

type HealthService
    ( httpFactory : IHttpClientFactory
    , upstreamOpts : IOptions<UpstreamOptions>
    , healthOpts   : IOptions<HealthOptions> ) =
    inherit BackgroundService()

    // (reachable, lastProbeUtc). Initial: reachable=true, lastProbe=MinValue (startup grace).
    let state = ConcurrentDictionary<ModelId, bool * DateTimeOffset>()
    // Consecutive-failure counters per target.
    let failures = ConcurrentDictionary<ModelId, int>()

    do
        state.[Qwen35B]    <- (true, DateTimeOffset.MinValue)
        state.[Qwen122B]   <- (true, DateTimeOffset.MinValue)
        failures.[Qwen35B]  <- 0
        failures.[Qwen122B] <- 0

    let threshold () =
        let t = healthOpts.Value.ConsecutiveFailureThreshold
        if t < 1 then 1 else t

    let probeOne (target: ModelId) (baseUrl: string) (ct: CancellationToken) = task {
        let mutable success = false
        try
            let client = httpFactory.CreateClient("health-probe")
            let url = baseUrl.TrimEnd('/') + "/v1/models"
            use! resp = client.GetAsync(url, ct)
            success <- resp.IsSuccessStatusCode
        with
        | :? OperationCanceledException as oce ->
            ExceptionDispatchInfo.Capture(oce).Throw()
        | ex ->
            success <- false
            Log.Debug(ex, "HealthService: probe for {Target} threw", target)

        let now = DateTimeOffset.UtcNow
        if success then
            failures.[target] <- 0
            state.[target] <- (true, now)
            Log.Debug("HealthService: {Target} reachable", target)
        else
            let newCount = failures.AddOrUpdate(target, 1, fun _ c -> c + 1)
            let isUnreachable = newCount >= threshold ()
            if isUnreachable then
                state.[target] <- (false, now)
                Log.Warning(
                    "HealthService: {Target} marked UNREACHABLE after {Count} consecutive failures",
                    target, newCount)
            else
                // Update timestamp but keep reachable=true (under-threshold)
                let prevReachable = match state.TryGetValue(target) with true, (r, _) -> r | _ -> true
                state.[target] <- (prevReachable, now)
                Log.Information(
                    "HealthService: {Target} probe failed ({Count}/{Threshold}); not yet unreachable",
                    target, newCount, threshold ())
    }

    // ── IHealthProbe ───────────────────────────────────────────────────────────
    interface IHealthProbe with
        member _.IsReachable(target) =
            match state.TryGetValue(target) with
            | true,  (r, _) -> r
            | false, _      -> true   // unknown → grace period

        member _.IsReachableAsync target _ct =
            let r =
                match state.TryGetValue(target) with
                | true,  (r, _) -> r
                | false, _      -> true
            Task.FromResult(r)

        member _.LastProbedAt(target) =
            match state.TryGetValue(target) with
            | true,  (_, ts) -> ts
            | false, _       -> DateTimeOffset.MinValue

    override this.ExecuteAsync(stoppingToken: CancellationToken) = task {
        let intervalSeconds =
            let v = healthOpts.Value.PollingIntervalSeconds
            if v <= 0 then 10 else v
        let interval = TimeSpan.FromSeconds(float intervalSeconds)
        Log.Information(
            "HealthService starting — probing every {Sec}s, threshold={T}",
            intervalSeconds, threshold ())

        // Run an immediate probe pass before entering the timer loop so /health
        // and the fallback policy reflect real state ASAP after startup.
        try
            do! probeOne Qwen35B  upstreamOpts.Value.Model35B  stoppingToken
            do! probeOne Qwen122B upstreamOpts.Value.Model122B stoppingToken
        with
        | :? OperationCanceledException as oce ->
            ExceptionDispatchInfo.Capture(oce).Throw()
        | ex ->
            Log.Warning(ex, "HealthService: initial probe pass threw")

        use timer = new PeriodicTimer(interval)
        let mutable running = true
        while running do
            try
                let! ticked = timer.WaitForNextTickAsync(stoppingToken)
                if not ticked then running <- false
                else
                    do! probeOne Qwen35B  upstreamOpts.Value.Model35B  stoppingToken
                    do! probeOne Qwen122B upstreamOpts.Value.Model122B stoppingToken
            with
            | :? OperationCanceledException as oce ->
                running <- false
                ExceptionDispatchInfo.Capture(oce).Throw()
            | ex ->
                Log.Warning(ex, "HealthService: probe loop iteration threw; will retry next tick")

        Log.Information("HealthService stopping")
    }
