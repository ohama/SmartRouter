module SmartRouter.Cli.Adapters.CanaryWatchdog

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Serilog
open SmartRouter.Cli.Adapters.CanaryState
open SmartRouter.Cli.Adapters.CanaryMetrics

/// Configuration sub-record bound from appsettings.json "Canary" section.
[<CLIMutable>]
type CanaryOptions =
    { CanaryModelPath              : string
      PercentageEnabled            : int
      RollingWindowSeconds         : int
      WatchdogPollIntervalSeconds  : int
      AutoRollbackThreshold        : float
      AutoRollbackEnabled          : bool
      MinBaselineSampleSize        : int }

/// Watchdog that polls per-cohort fallback rate every WatchdogPollIntervalSeconds and
/// fires SetPercentage(0) when:
///   - AutoRollbackEnabled = true
///   - canary_rate - baseline_rate > AutoRollbackThreshold
///   - baseline_count >= MinBaselineSampleSize  (avoid noisy small-N comparisons)
///
/// AutoRollbackEnabled defaults to false until Phase 10 (CONTEXT.md Lock 1) — production
/// trigger stays disabled even though the metric is computed and logged.
type CanaryWatchdog(metrics: ICanaryMetrics, canaryState: ICanaryState, options: CanaryOptions) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let pollSeconds = max 1 options.WatchdogPollIntervalSeconds
            let windowSecs  = max 1 options.RollingWindowSeconds
            let minSamples  = max 1 options.MinBaselineSampleSize
            use timer = new PeriodicTimer(TimeSpan.FromSeconds(float pollSeconds))
            let mutable running = true
            while running do
                try
                    let! ticked = timer.WaitForNextTickAsync(stoppingToken)
                    if not ticked then
                        running <- false
                    else
                        let baselineRate, baselineCount = metrics.FallbackRate(false, float windowSecs)
                        let canaryRate,   canaryCount   = metrics.FallbackRate(true,  float windowSecs)
                        let delta = canaryRate - baselineRate

                        Log.Verbose(
                            "CanaryWatchdog: baseline_count={BC} baseline_fb={BR:F4} canary_count={CC} canary_fb={CR:F4} delta={D:F4}",
                            baselineCount, baselineRate, canaryCount, canaryRate, delta)

                        if options.AutoRollbackEnabled
                           && baselineCount >= minSamples
                           && delta > options.AutoRollbackThreshold then
                            let reason =
                                sprintf
                                    "auto-rollback: canary_fb=%.4f baseline_fb=%.4f delta=%.4f > threshold=%.4f (baseline_n=%d)"
                                    canaryRate baselineRate delta options.AutoRollbackThreshold baselineCount
                            canaryState.SetPercentage(0, reason)
                            Log.Warning(
                                "CanaryWatchdog: AUTO-ROLLBACK fired. canary_fb_rate={C:F4} baseline_fb_rate={B:F4} delta={D:F4} threshold={T:F2} baseline_count={N}",
                                canaryRate, baselineRate, delta, options.AutoRollbackThreshold, baselineCount)
                with
                | :? OperationCanceledException as oce ->
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(oce).Throw()
                | ex ->
                    Log.Error(ex, "CanaryWatchdog: poll loop exception (continuing)")
        }
