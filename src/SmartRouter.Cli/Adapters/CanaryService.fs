module SmartRouter.Cli.Adapters.CanaryService

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting   // IHostedService
open Serilog
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.CanaryState
open SmartRouter.Cli.Adapters.CanaryMetrics
open SmartRouter.Cli.Adapters.CanaryWatchdog
open SmartRouter.Cli.Adapters.RetrainLock
open SmartRouter.Cli.Adapters.ModelBootstrapper   // computeModelVersion

/// Status DTO returned by GET /canary. snake_case wire format (matches /stats).
type CanaryStatusWire =
    { percentage_enabled       : int
      is_rolled_back           : bool
      auto_rollback_enabled    : bool
      baseline_model_version   : string
      canary_model_version     : string
      canary_file_present      : bool
      last_rollback_at         : string option
      last_rollback_reason     : string option
      rolling_60s              : RollingMetricsWire }

and RollingMetricsWire =
    { baseline_fallback_rate   : float
      canary_fallback_rate     : float
      baseline_request_count   : int
      canary_request_count     : int
      delta                    : float }

/// Outcome of /canary/promote.
type PromoteResult =
    | Promoted        of newBaselineVersion: string
    | NoCanaryFile
    | RetrainInProgress
    | Failed          of error: string

type ICanaryService =
    abstract member GetStatusAsync : ct: CancellationToken -> Task<CanaryStatusWire>
    abstract member PromoteAsync   : ct: CancellationToken -> Task<PromoteResult>
    abstract member RollbackAsync  : reason: string -> unit
    abstract member EnableAsync    : percentage: int -> unit

/// Wires together the in-memory state, the rolling metrics, the version provider, and
/// the file ops for promote. Loopback-only (no auth surface needed; Kestrel binds to
/// 127.0.0.1:4000 in production; tests bind 127.0.0.1:0).
type CanaryService(
    state            : ICanaryState,
    metrics          : ICanaryMetrics,
    versionProvider  : IModelVersionProvider,
    retrainLock      : IRetrainLock,
    options          : CanaryOptions,
    baselineModelPath: string,
    previousModelPath: string) =

    // ── IHostedService state: own a FileSystemWatcher for the canary model file ──
    //
    // CONTEXT.md Lock 9 says CanaryVersion is updated by CanaryService on file detection.
    // Without a watcher, post-startup arrival of router-canary.zip (operator copies the file
    // AFTER the router has booted) would leave canary_model_version stuck at "" until restart
    // or POST /canary/promote. The watcher closes the gap: Created/Changed/Deleted events on
    // the configured CanaryModelPath flip versionProvider.UpdateCanary in real-time.
    //
    // F# requires let bindings before interface implementations in the type body.
    let mutable watcher : FileSystemWatcher option = None

    let onCanaryFileMutation () =
        try
            if File.Exists(options.CanaryModelPath) then
                let v = sprintf "ml-%s-canary" (computeModelVersion options.CanaryModelPath)
                versionProvider.UpdateCanary(v)
                Log.Information("CanaryService: canary file mutation observed; canary_version={V}", v)
            else
                versionProvider.UpdateCanary("")
                Log.Information("CanaryService: canary file removed; canary_version cleared")
        with ex ->
            Log.Warning(ex, "CanaryService: failed to handle canary file mutation (continuing)")

    interface ICanaryService with
        member _.GetStatusAsync(_ct: CancellationToken) =
            task {
                let baselineRate, baselineCount = metrics.FallbackRate(false, float options.RollingWindowSeconds)
                let canaryRate,   canaryCount   = metrics.FallbackRate(true,  float options.RollingWindowSeconds)
                return
                    { percentage_enabled     = state.GetPercentage()
                      is_rolled_back         = state.IsRolledBack
                      auto_rollback_enabled  = options.AutoRollbackEnabled
                      baseline_model_version = versionProvider.CurrentVersion
                      canary_model_version   = versionProvider.CanaryVersion
                      canary_file_present    = File.Exists(options.CanaryModelPath)
                      last_rollback_at       = state.LastRollbackAt |> Option.map (fun t -> t.ToString("o"))
                      last_rollback_reason   = state.LastRollbackReason
                      rolling_60s            =
                        { baseline_fallback_rate = baselineRate
                          canary_fallback_rate   = canaryRate
                          baseline_request_count = baselineCount
                          canary_request_count   = canaryCount
                          delta                  = canaryRate - baselineRate } }
            }

        member _.PromoteAsync(_ct: CancellationToken) =
            task {
                if not (File.Exists(options.CanaryModelPath)) then
                    return NoCanaryFile
                else
                    match retrainLock.TryAcquire(0) with
                    | None ->
                        Log.Warning("CanaryService: promote skipped — retrain in progress")
                        return RetrainInProgress
                    | Some lockHandle ->
                        use _ = lockHandle
                        try
                            // Copy current baseline to .prev (preserves rollback path; Phase 8 invariant).
                            if File.Exists(baselineModelPath) then
                                let prevDir = Path.GetDirectoryName(previousModelPath)
                                if not (String.IsNullOrEmpty(prevDir)) && not (Directory.Exists(prevDir)) then
                                    Directory.CreateDirectory(prevDir) |> ignore
                                File.Copy(baselineModelPath, previousModelPath, overwrite = true)
                            // Atomic move: canary -> baseline.
                            File.Move(options.CanaryModelPath, baselineModelPath, overwrite = true)
                            // Compute new baseline version + flip provider state.
                            let newVersion = sprintf "ml-%s" (computeModelVersion baselineModelPath)
                            versionProvider.Update(newVersion)
                            versionProvider.UpdateCanary("")   // canary is now baseline; clear canary version
                            // The watchdog continues; new requests bucket against the (still-zero canary file) gate.
                            // ICanaryGate.File.Exists check now returns false → all traffic to baseline.
                            Log.Information("CanaryService: PROMOTE complete; new baseline_version={V}", newVersion)
                            return Promoted newVersion
                        with
                        | ex ->
                            Log.Error(ex, "CanaryService: promote failed")
                            return Failed (string ex)
            }

        member _.RollbackAsync(reason: string) =
            state.SetPercentage(0, if String.IsNullOrEmpty(reason) then "operator-rollback" else reason)
            Log.Warning("CanaryService: ROLLBACK fired (reason={R})", reason)

        member _.EnableAsync(percentage: int) =
            let clamped = max 0 (min 100 percentage)
            state.SetPercentage(clamped, sprintf "operator-enable %d%%" clamped)
            Log.Information("CanaryService: ENABLE percentage={P}", clamped)

    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) =
            task {
                try
                    let dir =
                        let d = Path.GetDirectoryName(options.CanaryModelPath)
                        if String.IsNullOrEmpty(d) then "." else d
                    if not (Directory.Exists(dir)) then
                        Directory.CreateDirectory(dir) |> ignore
                    let fileName = Path.GetFileName(options.CanaryModelPath)
                    let w = new FileSystemWatcher(dir, fileName)
                    w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName ||| NotifyFilters.Size
                    w.Created.Add(fun _ -> onCanaryFileMutation ())
                    w.Changed.Add(fun _ -> onCanaryFileMutation ())
                    w.Deleted.Add(fun _ -> onCanaryFileMutation ())
                    w.Renamed.Add(fun _ -> onCanaryFileMutation ())
                    w.EnableRaisingEvents <- true
                    watcher <- Some w
                    // Fire once at startup so versionProvider reflects current file state immediately,
                    // not just on subsequent mutations.
                    onCanaryFileMutation ()
                    Log.Information(
                        "CanaryService: FileSystemWatcher armed dir={Dir} pattern={Pat}",
                        dir, fileName)
                with ex ->
                    // Non-fatal: router still boots; canary_version updates only at promote-time
                    // (the version was always populated by the (1.2) RoutingAlgorithmRegistration
                    // factory at startup). Watcher failure is logged + tolerated.
                    Log.Warning(ex, "CanaryService: failed to arm FileSystemWatcher (canary version will lag until restart)")
            } :> Task

        member _.StopAsync(_ct: CancellationToken) =
            task {
                match watcher with
                | Some w ->
                    try
                        w.EnableRaisingEvents <- false
                        w.Dispose()
                    with ex ->
                        Log.Warning(ex, "CanaryService: error disposing FileSystemWatcher (continuing shutdown)")
                    watcher <- None
                | None -> ()
            } :> Task
