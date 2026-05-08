module SmartRouter.Cli.Adapters.RetrainingService

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.ML
open Serilog
open SmartRouter.Core.MLPorts
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.DatasetMerger
open SmartRouter.Cli.Adapters.Retrainer
open SmartRouter.Cli.Adapters.Validator
open SmartRouter.Cli.Adapters.ModelBootstrapper   // computeModelVersion

// ── Options ──────────────────────────────────────────────────────────────────
//
// Bound from appsettings.json "Retraining" section. Defensive defaults applied
// in CompositionRoot's factory lambda (Plan 08-02 Task 3).

[<CLIMutable>]
type RetrainingOptions =
    { IntervalMinutes            : int
      HardCaseCountTrigger       : int
      CountCheckIntervalMinutes  : int
      HardCasePath               : string
      TrainingSetPath            : string
      StatePath                  : string
      ModelPath                  : string
      PreviousModelPath          : string
      RejectionLogPath           : string
      HeldOutFraction            : float
      HeldOutRandomSeed          : int
      L2Regularization           : float32 }

// ── State file (last-retrain.json) ───────────────────────────────────────────
//
// Single-object JSON (NOT JSONL). Schema per CONTEXT.md Lock 4.

[<CLIMutable>]
type RetrainState =
    { schema_version              : int
      last_retrain_utc            : string
      hard_case_count_at_retrain  : int
      model_version_after         : string }

let private stateJsonOpts =
    let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
    o.Converters.Add(JsonFSharpConverter())
    o

let private readState (path: string) : RetrainState option =
    if not (File.Exists path) then None
    else
        try
            let text = File.ReadAllText(path, Encoding.UTF8)
            Some (JsonSerializer.Deserialize<RetrainState>(text, stateJsonOpts))
        with ex ->
            Log.Warning(ex, "RetrainingService: malformed state file at {Path}; treating as missing", path)
            None

let private writeState (path: string) (state: RetrainState) : unit =
    let dir = Path.GetDirectoryName(path)
    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore
    let tmp  = path + ".tmp"
    let text = JsonSerializer.Serialize(state, stateJsonOpts)
    File.WriteAllText(tmp, text, Encoding.UTF8)
    File.Move(tmp, path, overwrite = true)

// ── Line counter (RESEARCH.md countLines pattern) ────────────────────────────
//
// FileShare.ReadWrite is REQUIRED — HardCaseDatasetWriter holds FileShare.None.
// Counts newline bytes — JSONL guarantees one entry per line.

let private countJsonlLines (path: string) : int =
    if not (File.Exists path) then 0
    else
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let buf = Array.zeroCreate<byte> 65536
        let mutable count = 0
        let mutable read  = 1
        while read > 0 do
            read <- stream.Read(buf, 0, buf.Length)
            for i in 0 .. read - 1 do
                if buf.[i] = byte '\n' then count <- count + 1
        count

// ── RetrainingService ────────────────────────────────────────────────────────

type RetrainingService(
    options          : RetrainingOptions,
    embedder         : IEmbedder,
    versionProvider  : IModelVersionProvider) =
    inherit BackgroundService()

    let semaphore = new SemaphoreSlim(1, 1)

    // Embed all hard-case entries (off the hot path; cancellable).
    // ~25ms x N samples. Uses stoppingToken so a host shutdown mid-embedding aborts.
    let embedAll (entries: HardCaseEntry[]) (ct: CancellationToken) : Task<TrainSample[]> =
        task {
            let result = Array.zeroCreate<TrainSample> entries.Length
            for i in 0 .. entries.Length - 1 do
                ct.ThrowIfCancellationRequested()
                let! features = embedder.EmbedAsync(entries.[i].PromptText, ct)
                result.[i] <- { Features = features; Label = entries.[i].Label = 1 }
            return result
        }

    // ── runRetrain ── single end-to-end pipeline ──────────────────────────────
    //
    // Pipeline order (CONTEXT.md Lock 5 — MANDATORY):
    // 1. Read hard-cases.jsonl + training-set.jsonl
    // 2. Embed both via IEmbedder
    // 3. Merge 70/30 (or bootstrap if old empty)
    // 4. TrainTestSplit on merged
    // 5. Train candidate on split.TrainSet ONLY (NOT full dataView)
    // 6. Compute baseline metrics on split.TestSet (existing router.zip)
    // 7. Validate candidate on split.TestSet vs baseline
    // 8. Accepted: copy router.zip→.prev; Move candidate→router.zip; save training set; update state; Update provider
    //    Rejected: log + writeRejectionLog; delete candidate; do NOT touch router.zip
    let runRetrain (stoppingToken: CancellationToken) : Task<unit> =
        task {
            let cycleStart = DateTimeOffset.UtcNow
            Log.Information("RetrainingService: starting retrain cycle")

            // 1. Read inputs
            let hardCaseEntries = readHardCases options.HardCasePath
            let oldEntries      = readTrainingSet options.TrainingSetPath
            Log.Information(
                "RetrainingService: read {Hard} hard cases + {Old} old training samples",
                hardCaseEntries.Length, oldEntries.Length)

            if hardCaseEntries.Length = 0 then
                Log.Information("RetrainingService: no hard cases; skipping retrain cycle")
                return ()
            else

            // 2. Embed both sets (off the hot path; cancellable)
            let! newSamples = embedAll hardCaseEntries stoppingToken
            let! oldSamples = embedAll oldEntries stoppingToken

            // 3. Merge with seeded RNG (reproducible ordering)
            let rng = Random(options.HeldOutRandomSeed)
            let merged = merge oldSamples newSamples rng

            if merged.Length < 4 then
                Log.Warning(
                    "RetrainingService: merged set too small ({N} samples) for train/test split; skipping",
                    merged.Length)
                return ()
            else

            // 4. Build MLContext + dataView from the merged samples.
            //    CONTEXT.md Lock 5: caller owns MLContext; same instance used for split, train, evaluate.
            let mlContext = MLContext(seed = Nullable<int>(options.HeldOutRandomSeed))
            let dataView  = mlContext.Data.LoadFromEnumerable(merged)

            // 5. SPLIT FIRST — held-out set must NOT be seen during training.
            //    CONTEXT.md Lock 5 mandates this order; reverse order produces optimistically biased metrics.
            let split =
                mlContext.Data.TrainTestSplit(
                    dataView,
                    testFraction = options.HeldOutFraction,
                    seed         = Nullable<int>(options.HeldOutRandomSeed))

            // 6. Train candidate on split.TrainSet ONLY (NOT the full dataView).
            //    Candidate file lives next to the target; cleaned up on rejection.
            let candidatePath = options.ModelPath + ".candidate.zip"
            let model = retrain mlContext split.TrainSet candidatePath options.L2Regularization

            // 7. Compute baseline on the SAME split.TestSet (load existing router.zip + evaluate).
            //    Same split for both candidate AND baseline -> fair comparison.
            let baselineAcc, baselineFbRate =
                computeBaseline mlContext options.ModelPath split.TestSet

            // 8. Validate candidate on split.TestSet against baseline metrics.
            let result = validate mlContext model split.TestSet baselineAcc baselineFbRate

            match result with
            | Accepted (newAcc, newFbRate) ->
                Log.Information(
                    "RetrainingService: validation passed (acc={Acc:F4} >= {BAcc:F4}; fbRate={Fb:F4} <= {BFb:F4})",
                    newAcc, baselineAcc, newFbRate, baselineFbRate)

                // 8a. Copy existing router.zip to .prev for Phase 9 rollback (Pitfall 10)
                if File.Exists(options.ModelPath) then
                    let prevDir = Path.GetDirectoryName(options.PreviousModelPath)
                    if not (String.IsNullOrEmpty(prevDir)) && not (Directory.Exists(prevDir)) then
                        Directory.CreateDirectory(prevDir) |> ignore
                    File.Copy(options.ModelPath, options.PreviousModelPath, overwrite = true)

                // 8b. Atomic swap: candidate -> router.zip
                File.Move(candidatePath, options.ModelPath, overwrite = true)

                // 8c. Update IModelVersionProvider so next request picks up new hash
                let newVersion = computeModelVersion options.ModelPath
                versionProvider.Update(sprintf "ml-%s" newVersion)

                // 8d. Persist the CUMULATIVE training set (oldEntries + this cycle's hardCaseEntries)
                //     so the next cycle's "old" set includes today's new entries. Without this union,
                //     each retrain would discard prior training data and the 70/30 continuity from
                //     CONTEXT.md Lock 3 would break after the first retrain.
                //
                //     First-retrain edge case: oldEntries=[||] -> cumulative = hardCaseEntries only
                //     (CONTEXT.md Lock 3 bootstrap path; correct).
                //
                //     Saving HardCaseEntry[] (not TrainSample[]) — preserves prompt text and metadata;
                //     next retrain re-embeds with whichever embedder is current.
                let cumulative = Array.append oldEntries hardCaseEntries
                saveTrainingSet options.TrainingSetPath cumulative

                // 8e. Update state file
                let currentCount = countJsonlLines options.HardCasePath
                writeState options.StatePath {
                    schema_version              = 1
                    last_retrain_utc            = DateTimeOffset.UtcNow.ToString("o")
                    hard_case_count_at_retrain  = currentCount
                    model_version_after         = sprintf "ml-%s" newVersion
                }

                let elapsed = (DateTimeOffset.UtcNow - cycleStart).TotalSeconds
                Log.Information(
                    "RetrainingService: retrain accepted in {Elapsed:F1}s; new model_version={Version}",
                    elapsed, newVersion)

            | Rejected reason ->
                Log.Warning(
                    "RetrainingService: validation rejected — {Reason}; router.zip unchanged",
                    reason)
                writeRejectionLog
                    options.RejectionLogPath
                    reason
                    baselineAcc
                    baselineFbRate
                    merged.Length
                // Clean up candidate file
                if File.Exists(candidatePath) then
                    try
                        File.Delete(candidatePath)
                    with _ ->
                        ()
        }

    // tryRunRetrain — non-blocking acquire; concurrent triggers SKIP.
    // RETRAIN-06: inner try/with catches ANY exception from runRetrain so the outer
    // timer loops continue. OperationCanceledException is re-thrown (with original stack
    // via ExceptionDispatchInfo) so stoppingToken shutdown propagates to ExecuteAsync's
    // outer catch. `reraise()` is not valid inside task{} nested try/with (FS0413);
    // ExceptionDispatchInfo.Capture(...).Throw() preserves the original stack trace.
    let tryRunRetrain (stoppingToken: CancellationToken) : Task<unit> =
        task {
            if semaphore.Wait(0) then
                try
                    try
                        do! runRetrain stoppingToken
                    with
                    | :? OperationCanceledException as oce ->
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(oce).Throw()
                    | ex ->
                        Log.Error(ex, "RetrainingService: pipeline threw; model unchanged; will retry next tick")
                finally
                    semaphore.Release() |> ignore
            else
                Log.Warning("RetrainingService: retrain already in progress; skipping trigger")
        }

    /// Test seam: Plan 08-03 RETRAIN-04 end-to-end test calls this directly to
    /// drive a synchronous retrain without waiting for the timer.
    member _.RunNowAsync(stoppingToken: CancellationToken) : Task<unit> =
        tryRunRetrain stoppingToken

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Two raced loops — periodic sweep + count check.
            let periodicLoop () : Task<unit> =
                task {
                    use timer = new PeriodicTimer(TimeSpan.FromMinutes(float options.IntervalMinutes))
                    let mutable running = true
                    while running do
                        try
                            let! ticked = timer.WaitForNextTickAsync(stoppingToken)
                            if not ticked then
                                running <- false
                            else
                                do! tryRunRetrain stoppingToken
                        with
                        | :? OperationCanceledException -> running <- false
                }

            let countCheckLoop () : Task<unit> =
                task {
                    use timer = new PeriodicTimer(TimeSpan.FromMinutes(float options.CountCheckIntervalMinutes))
                    let mutable running = true
                    while running do
                        try
                            let! ticked = timer.WaitForNextTickAsync(stoppingToken)
                            if not ticked then
                                running <- false
                            else
                                let currentCount = countJsonlLines options.HardCasePath
                                let priorCount =
                                    match readState options.StatePath with
                                    | Some s -> s.hard_case_count_at_retrain
                                    | None   -> 0
                                let delta = currentCount - priorCount
                                if delta >= options.HardCaseCountTrigger then
                                    Log.Information(
                                        "RetrainingService: count trigger fired ({Delta} >= {Threshold}); starting retrain",
                                        delta, options.HardCaseCountTrigger)
                                    do! tryRunRetrain stoppingToken
                        with
                        | :? OperationCanceledException -> running <- false
                }

            try
                let! _ = Task.WhenAll(periodicLoop (), countCheckLoop ())
                ()
            with
            | :? OperationCanceledException -> ()
            | ex -> Log.Error(ex, "RetrainingService: outer loop crashed")
        }
