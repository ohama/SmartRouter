module SmartRouter.Cli.Endpoints.Stats

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.QueueDispatcher
open SmartRouter.Cli.Adapters.Json
open SmartRouter.Cli.Adapters.CanaryState
open SmartRouter.Cli.Adapters.JudgeClient    // Phase 16: IJudgeStats
open SmartRouter.Cli.Adapters.SelfRouter    // Phase 19: ISelfRouterStats

/// Wire shape for GET /stats. snake_case to match OpenAI conventions.
/// Built fresh from a StatsSnapshot on every request — no caching.
///
/// Issue #7: extended with baseline_model_version, canary_model_version,
/// canary_percent, canary_active so monitoring tooling can scrape a single
/// endpoint instead of hitting /stats + /canary in lockstep.
type private StatsWire =
    { timestamp                          : string
      active_122b                        : int
      queue_depth_122b_high              : int
      queue_depth_122b_low               : int
      active_35b                         : int
      requests_per_sec                   : float
      avg_latency_ms_60s                 : float
      failure_count_total                : int64
      fairness_picks_high                : int64
      fairness_picks_low                 : int64
      semaphore_available                : int
      baseline_model_version             : string
      canary_model_version               : string option
      canary_percent                     : int
      canary_active                      : bool
      quality_check_hits_finish_reason   : int64    // NEW Phase 15
      quality_check_hits_length          : int64    // NEW Phase 15
      quality_check_hits_entropy         : int64    // NEW Phase 15
      quality_check_hits_keyword         : int64
      judge_cache_hits                   : int64    // NEW Phase 16
      judge_cache_misses                 : int64    // NEW Phase 16
      judge_call_count                   : int64    // NEW Phase 16
      selfrouter_cache_hits              : int64    // NEW Phase 19 SR-05
      selfrouter_cache_misses            : int64    // NEW Phase 19 SR-05
      selfrouter_call_count              : int64    // NEW Phase 19 SR-05
      selfrouter_skipped                 : int64 }  // NEW Phase 19 SR-05

let private snapshotToWireFields (s: StatsSnapshot) : StatsWire =
    { timestamp                          = s.Timestamp.ToString("o")
      active_122b                        = s.Active122B
      queue_depth_122b_high              = s.QueueDepth122BHigh
      queue_depth_122b_low               = s.QueueDepth122BLow
      active_35b                         = s.Active35B
      requests_per_sec                   = s.RequestsPerSec
      avg_latency_ms_60s                 = s.AvgLatencyMs60s
      failure_count_total                = s.FailureCountTotal
      fairness_picks_high                = s.FairnessPicksHigh
      fairness_picks_low                 = s.FairnessPicksLow
      semaphore_available                = s.SemaphoreAvailable
      baseline_model_version             = ""
      canary_model_version               = None
      canary_percent                     = 0
      canary_active                      = false
      quality_check_hits_finish_reason   = s.QualityCheckHits.FinishReason
      quality_check_hits_length          = s.QualityCheckHits.Length
      quality_check_hits_entropy         = s.QualityCheckHits.Entropy
      quality_check_hits_keyword         = s.QualityCheckHits.Keyword
      judge_cache_hits                   = 0L      // overridden in mapEndpoints after IJudgeStats resolve
      judge_cache_misses                 = 0L      // overridden in mapEndpoints
      judge_call_count                   = 0L      // overridden in mapEndpoints
      selfrouter_cache_hits              = 0L      // overridden in mapEndpoints after ISelfRouterStats resolve
      selfrouter_cache_misses            = 0L      // overridden in mapEndpoints
      selfrouter_call_count              = 0L      // overridden in mapEndpoints
      selfrouter_skipped                 = 0L }    // overridden in mapEndpoints

/// Register GET /stats. Resolves IStatsProvider, IModelVersionProvider, and
/// ICanaryState from DI on each request and serializes a single self-contained
/// wire object. No caching: the snapshot is cheap and operators want live values.
let mapEndpoints (app: WebApplication) =
    app.MapGet("/stats", Func<HttpContext, Task>(fun ctx ->
        task {
            let logger    = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Stats")
            let stats     = ctx.RequestServices.GetRequiredService<IStatsProvider>()
            let versionP  = ctx.RequestServices.GetRequiredService<IModelVersionProvider>()
            let canarySt  = ctx.RequestServices.GetRequiredService<ICanaryState>()
            let snap      = stats.GetSnapshot()
            let baseFields = snapshotToWireFields snap
            // Canary fields. CanaryVersion is the empty string when no canary is loaded;
            // surface as JSON null in that case for cleaner consumer handling.
            let canaryVer =
                let v = versionP.CanaryVersion
                if String.IsNullOrWhiteSpace(v) then None else Some v
            let canaryPct = canarySt.GetPercentage()
            let canaryActive = canaryVer.IsSome && canaryPct > 0
            // Phase 16 — judge stats: null-safe resolve (GetService returns null when judge disabled or offline mode).
            // Both composition paths guarantee IJudgeStats is resolvable (real or NoOp), so this is
            // defense-in-depth for future test fixtures that may forget to register it.
            let judgeStats = ctx.RequestServices.GetService<IJudgeStats>()
            let struct (jHits, jMisses, jCalls) =
                if isNull (box judgeStats) then struct (0L, 0L, 0L)
                else judgeStats.GetJudgeStats()
            // Phase 19 — self-router stats: null-safe resolve (GetService returns null when
            // mode="ml" or offline; both paths guarantee ISelfRouterStats NoOp is registered
            // so this guard is defense-in-depth only — mirrors judgeStats pattern above).
            let selfRouterStats = ctx.RequestServices.GetService<ISelfRouterStats>()
            let struct (srHits, srMisses, srCalls, srSkipped) =
                if isNull (box selfRouterStats) then struct (0L, 0L, 0L, 0L)
                else selfRouterStats.GetSelfRouterStats()
            let wire =
                { baseFields with
                    baseline_model_version  = versionP.CurrentVersion
                    canary_model_version    = canaryVer
                    canary_percent          = canaryPct
                    canary_active           = canaryActive
                    judge_cache_hits        = jHits        // NEW Phase 16
                    judge_cache_misses      = jMisses      // NEW Phase 16
                    judge_call_count        = jCalls       // NEW Phase 16
                    selfrouter_cache_hits   = srHits       // NEW Phase 19
                    selfrouter_cache_misses = srMisses     // NEW Phase 19
                    selfrouter_call_count   = srCalls      // NEW Phase 19
                    selfrouter_skipped      = srSkipped }  // NEW Phase 19
            logger.LogDebug(
                "/stats hit; queue_depth_high={H} active_122b={A} model_version={V} canary_active={C}",
                wire.queue_depth_122b_high, wire.active_122b, wire.baseline_model_version, wire.canary_active)
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync(wire, jsonOptions, ctx.RequestAborted)
        })) |> ignore
