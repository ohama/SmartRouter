module SmartRouter.Tests.RetrainingTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.ML
open SmartRouter.Core.MLPorts
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.DatasetMerger
open SmartRouter.Cli.Adapters.Retrainer
open SmartRouter.Cli.Adapters.Validator
open SmartRouter.Cli.Adapters.ModelVersionProvider
open SmartRouter.Cli.Adapters.RetrainLock
open SmartRouter.Cli.Adapters.RetrainingService
open SmartRouter.Cli.Adapters.ModelBootstrapper   // computeModelVersion

// ── Helpers ──────────────────────────────────────────────────────────────────

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-retrain-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

/// Build a synthetic 1024-dim feature vector seeded by integer salt.
/// Different salts produce easily-separable clusters so LR can learn.
let private synthFeatures (salt: int) (label: bool) : float32[] =
    let rng = Random(salt)
    Array.init 1024 (fun _ ->
        let baseVal = if label then 0.5 else -0.5
        float32 (baseVal + (rng.NextDouble() - 0.5) * 0.2))

let private mkSample (salt: int) (label: bool) : TrainSample =
    { Features = synthFeatures salt label; Label = label }

let private mkHardCaseEntry (salt: int) (label: int) : HardCaseEntry =
    { SchemaVersion          = 1
      CorrelationId          = sprintf "cid-%d" salt
      PromptHash             = sprintf "hash-%d" salt
      PromptText             = sprintf "synthetic prompt %d" salt
      Label                  = label
      Source                 = "test"
      TeacherResponseExcerpt = None
      LabeledAt              = DateTimeOffset.UtcNow
      PromptKoreanCharRatio  = 0.0
      RoutingAlgorithm       = "ml"
      Target                 = if label = 0 then "Qwen35B" else "Qwen122B" }

/// Fake IEmbedder that returns synthetic features keyed off the prompt text.
/// Avoids loading bge-m3 ONNX in tests (saves ~580MB + 5s warmup per test run).
/// Uses Task.Yield() so callers genuinely suspend at the first await point —
/// required for test4_concurrentTriggers to observe the SemaphoreSlim skip semantics
/// (both RunNowAsync calls are in-flight simultaneously; t1 holds semaphore while
/// awaiting embeddings; t2 finds semaphore taken and logs "skipping trigger").
type private FakeEmbedder() =
    interface IEmbedder with
        member _.EmbedAsync(text: string, _ct: CancellationToken) : Task<float32[]> =
            task {
                // Yield so the task scheduler can interleave other tasks.
                // Without this, Task.FromResult-based returns allow the entire runRetrain
                // pipeline to execute synchronously before the second RunNowAsync checks
                // the semaphore — causing both to appear as "1 start, 0 skips".
                do! Task.Yield()
                // Parse "synthetic prompt N" -> N as salt; default to text hash if unparseable.
                let salt =
                    let parts = text.Split(' ')
                    if parts.Length >= 3 then
                        match Int32.TryParse(parts.[2]) with
                        | true, n -> n
                        | _ -> text.GetHashCode()
                    else
                        text.GetHashCode()
                // Choose label by salt parity (matches mkHardCaseEntry where Label=salt%2)
                let label = salt % 2 = 1
                return synthFeatures salt label
            }

/// Default RetrainingOptions for tests — small intervals and counts.
let private mkOptions (dir: string) : RetrainingOptions =
    { IntervalMinutes           = 60
      HardCaseCountTrigger      = 4
      CountCheckIntervalMinutes = 5
      HardCasePath              = Path.Combine(dir, "hard-cases.jsonl")
      TrainingSetPath           = Path.Combine(dir, "training-set.jsonl")
      StatePath                 = Path.Combine(dir, "last-retrain.json")
      ModelPath                 = Path.Combine(dir, "router.zip")
      PreviousModelPath         = Path.Combine(dir, "router.zip.prev")
      RejectionLogPath          = Path.Combine(dir, "rejections.jsonl")
      HeldOutFraction           = 0.2
      HeldOutRandomSeed         = 42
      L2Regularization          = 0.1f }

/// Write hard-cases.jsonl in the schema HardCaseDatasetWriter would produce.
let private writeHardCases (path: string) (entries: HardCaseEntry[]) : unit =
    let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
    opts.Converters.Add(JsonFSharpConverter())
    let dir = Path.GetDirectoryName(path)
    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore
    use stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
    use sw = new StreamWriter(stream, Encoding.UTF8)
    for e in entries do
        sw.WriteLine(JsonSerializer.Serialize(e, opts))
    sw.Flush()

/// Shared NullLogger for direct module-function call sites in this test file.
let private nullLogger = NullLogger.Instance :> Microsoft.Extensions.Logging.ILogger

/// Write a baseline router.zip so Validator.computeBaseline has something to load.
/// Trains on a balanced synthetic set so accuracy ~= reasonable baseline.
let private writeBaselineModel (path: string) : unit =
    let samples = [|
        for i in 0 .. 99 do
            yield mkSample i (i % 2 = 0)   // 50 positive, 50 negative
    |]
    // Retrainer signature (08-01 truth: retrain takes a caller-built MLContext + IDataView).
    // Build both here so this baseline helper is self-contained.
    let mlCtx = MLContext(seed = Nullable<int>(42))
    let trainView = mlCtx.Data.LoadFromEnumerable(samples)
    let _model = retrain nullLogger mlCtx trainView path 0.1f
    ()

// ── In-memory Serilog sink ────────────────────────────────────────────────────

/// In-memory Serilog sink for asserting log lines fired during a test.
/// Mirrors the CapturingSink pattern from LoggingTests.fs (Phase 5).
type private CapturingSink(captured: System.Collections.Generic.List<string>) =
    interface Serilog.Core.ILogEventSink with
        member _.Emit(logEvent: Serilog.Events.LogEvent) =
            // Render template+properties into a flat string for grep-style asserts.
            let sw = new System.IO.StringWriter()
            logEvent.RenderMessage(sw)
            lock captured (fun () -> captured.Add(sw.ToString()))

// ── Test 1 — RETRAIN-01 merger class balance ──────────────────────────────────

let private test1_mergerClassBalance =
    testCase "RETRAIN-01: DatasetMerger preserves >=30% per class on imbalanced new data" <| fun _ ->
        // Old: 200 balanced (100 positive, 100 negative)
        // New: 500 imbalanced (475 positive, 25 negative — heavy 122B bias as in production)
        let oldSamples = Array.init 200 (fun i -> mkSample i (i % 2 = 0))
        let newSamples = Array.append
                           (Array.init 475 (fun i -> mkSample (i + 1000) true))
                           (Array.init 25 (fun i -> mkSample (i + 2000) false))
        let rng = Random(42)
        let merged = merge nullLogger oldSamples newSamples rng

        let total = float merged.Length
        let class1 = merged |> Array.filter (fun s -> s.Label) |> Array.length |> float
        let class0 = total - class1
        let r0 = class0 / total
        let r1 = class1 / total

        Expect.isGreaterThanOrEqual r0 0.30 (sprintf "class0 ratio %.3f must be >= 0.30" r0)
        Expect.isGreaterThanOrEqual r1 0.30 (sprintf "class1 ratio %.3f must be >= 0.30" r1)
        Expect.isGreaterThan merged.Length 0 "merged set must be non-empty"

// ── Test 2 — RETRAIN-01 bootstrap (empty old) ─────────────────────────────────

let private test2_mergerBootstrap =
    testCase "RETRAIN-01: DatasetMerger with old=[||] returns new samples without scaling" <| fun _ ->
        let newSamples = Array.init 100 (fun i -> mkSample i (i % 2 = 0))
        let rng = Random(42)
        let merged = merge nullLogger [||] newSamples rng

        Expect.equal merged.Length newSamples.Length
            "bootstrap (old=[||]) must return same count as new samples (no 70/30 scaling)"

        // Class balance preserved on already-balanced new input
        let class1 = merged |> Array.filter (fun s -> s.Label) |> Array.length
        let class0 = merged.Length - class1
        Expect.isGreaterThan class0 0 "class0 must be present"
        Expect.isGreaterThan class1 0 "class1 must be present"

// ── Test 3 — RETRAIN-03 validation gate accept + reject + rejection log ────────

let private test3_validatorGate =
    testCase "RETRAIN-03: Validator accepts good model, rejects regression, writes rejection log" <| fun _ ->
        let dir = mkTempDir ()
        try
            let modelPath = Path.Combine(dir, "router.zip")
            // Train baseline
            writeBaselineModel modelPath

            // Build a held-out IDataView from balanced synthetic samples
            let heldOut = Array.init 40 (fun i -> mkSample (i + 5000) (i % 2 = 0))

            // Case A — Acceptance: train a model on similar data; should match or beat baseline.
            // Retrainer signature (08-01 truth): caller builds MLContext + IDataView, passes both in,
            // gets only ITransformer back. Validate against per-context heldOutDV_A so MLContext
            // identity matches the candidate's training MLContext (avoids ML.NET schema-binding
            // drift across contexts). Recompute baseline on the same context for fair comparison.
            let acceptSamples = Array.init 100 (fun i -> mkSample (i + 6000) (i % 2 = 0))
            let mlCtxA = MLContext(seed = Nullable<int>(42))
            let dvA = mlCtxA.Data.LoadFromEnumerable(acceptSamples)
            let _modelA = retrain nullLogger mlCtxA dvA (modelPath + ".accept.tmp.zip") 0.1f
            // Per-context held-out view so baseline + candidate comparison is fair (Lock 5)
            let heldOutDV_A = mlCtxA.Data.LoadFromEnumerable(heldOut)
            let baselineAccA, baselineFbRateA = computeBaseline nullLogger mlCtxA modelPath heldOutDV_A
            let resultA = validate mlCtxA _modelA heldOutDV_A baselineAccA baselineFbRateA
            match resultA with
            | Accepted (acc, fb) ->
                Expect.isGreaterThanOrEqual acc baselineAccA "accepted: acc >= baseline (Case A MLContext)"
                Expect.isLessThanOrEqual fb baselineFbRateA "accepted: fbRate <= baseline (Case A MLContext)"
            | Rejected r ->
                failtestf "expected Accepted on baseline-match training, got Rejected: %s" r

            // Case B — Rejection: train a single-class model that will fail accuracy gate.
            // Same caller-built (MLContext, IDataView) pattern as Case A — fresh MLContext keeps
            // the candidate isolated from Case A's pipeline state.
            let rejectSamples = Array.init 50 (fun i -> mkSample (i + 7000) true)   // all positive
            let mlCtxB = MLContext(seed = Nullable<int>(42))
            let dvB = mlCtxB.Data.LoadFromEnumerable(rejectSamples)
            let _modelB = retrain nullLogger mlCtxB dvB (modelPath + ".reject.tmp.zip") 0.1f
            let heldOutDV_B = mlCtxB.Data.LoadFromEnumerable(heldOut)
            let baselineAccB, baselineFbRateB = computeBaseline nullLogger mlCtxB modelPath heldOutDV_B
            let resultB = validate mlCtxB _modelB heldOutDV_B baselineAccB baselineFbRateB
            match resultB with
            | Rejected reason ->
                Expect.isNotEmpty reason "rejection reason must be non-empty"
                // Write the rejection log; verify a JSONL line lands. Use Case B's baseline values
                // because that's the gate the candidate actually failed against.
                let rejLog = Path.Combine(dir, "rejections.jsonl")
                writeRejectionLog nullLogger rejLog reason baselineAccB baselineFbRateB rejectSamples.Length
                Expect.isTrue (File.Exists rejLog) "rejection log file must exist"
                let lines = File.ReadAllLines(rejLog)
                Expect.isGreaterThan lines.Length 0 "rejection log must have >= 1 line"
                use doc = JsonDocument.Parse(lines.[0])
                Expect.equal (doc.RootElement.GetProperty("schema_version").GetInt32()) 1
                    "rejection log line has schema_version=1"
                Expect.isNotNull (doc.RootElement.GetProperty("reason").GetString())
                    "rejection log line has reason"
            | Accepted _ ->
                failtest "expected Rejected on single-class training, got Accepted"
        finally
            cleanupDir dir

// ── Test 4 — RETRAIN-05 concurrent triggers serialize ─────────────────────────

let private test4_concurrentTriggers =
    testCase "RETRAIN-05: concurrent RunNowAsync — exactly one acquires the semaphore; second trigger logs a skip" <| fun _ ->
        let dir = mkTempDir ()
        try
            // Install capturing sink BEFORE service construction so all RetrainingService
            // log events are recorded. Save and restore the prior global Logger to avoid
            // bleeding into other tests (testSequenced means strict ordering, but defensive).
            let captured = System.Collections.Generic.List<string>()
            let priorLogger = Serilog.Log.Logger
            try
                Serilog.Log.Logger <-
                    Serilog.LoggerConfiguration()
                        .MinimumLevel.Debug()
                        .WriteTo.Sink(CapturingSink(captured))
                        .CreateLogger()

                let opts = mkOptions dir
                // Seed enough hard cases to actually run a retrain
                let entries = Array.init 20 (fun i -> mkHardCaseEntry i (i % 2))
                writeHardCases opts.HardCasePath entries
                // Initial model required for baseline computation
                writeBaselineModel opts.ModelPath

                let provider = ModelVersionProvider("ml-test-initial")
                let embedder = FakeEmbedder()
                use service = new RetrainingService(opts, embedder :> IEmbedder, provider :> IModelVersionProvider, new RetrainLock() :> IRetrainLock, NullLogger<RetrainingService>.Instance)

                // Drive both calls concurrently. With Wait(0), exactly one wins; the other
                // returns immediately after logging a "skipping" Warning.
                let t1 = service.RunNowAsync(CancellationToken.None)
                let t2 = service.RunNowAsync(CancellationToken.None)
                Task.WaitAll(t1, t2)

                // ── Existing positive checks ──────────────────────────────────────────
                Expect.isTrue (File.Exists opts.ModelPath) "model file must exist after retrain"
                let v = computeModelVersion opts.ModelPath
                Expect.notEqual ("ml-" + v) "ml-test-initial" "version must have flipped from seed"
                Expect.isTrue (File.Exists opts.StatePath) "state file must exist after exactly one successful retrain"

                // ── NEW: explicit skip-assertion — exactly one cycle started + the other
                //        produced the "already in progress; skipping" Warning line.
                //        Match the literal log strings emitted by RetrainingService:
                //          start: "RetrainingService: starting retrain cycle"  (Information)
                //          skip:  "RetrainingService: retrain already in progress; skipping trigger"  (Warning)
                let starts =
                    captured
                    |> Seq.filter (fun m -> m.Contains "starting retrain cycle")
                    |> Seq.length
                let skips =
                    captured
                    |> Seq.filter (fun m -> m.Contains "skipping trigger")
                    |> Seq.length
                Expect.equal starts 1
                    (sprintf "RETRAIN-05: exactly one retrain must run (got %d starts; %d skips)" starts skips)
                Expect.isGreaterThan skips 0
                    (sprintf "RETRAIN-05: second concurrent trigger must log a skip (got %d skips)" skips)
            finally
                Serilog.Log.Logger <- priorLogger
        finally
            cleanupDir dir

// ── Test 5 — RETRAIN-06 throw isolation ──────────────────────────────────────

/// Test-only IEmbedder that throws on the N-th call. Used to inject a failure
/// inside runRetrain's embedAll loop and verify the BackgroundService's
/// outer try/with catches it without crashing the host.
type private ThrowingEmbedder(throwAfter: int) =
    let mutable count = 0
    interface IEmbedder with
        member _.EmbedAsync(text: string, _ct: CancellationToken) : Task<float32[]> =
            let n = Interlocked.Increment(&count)
            if n > throwAfter then
                raise (InvalidOperationException("forced throw for test"))
            else
                let salt = text.GetHashCode()
                Task.FromResult(synthFeatures salt (n % 2 = 1))

let private test5_throwIsolation =
    testCase "RETRAIN-06: a throw inside runRetrain is caught; subsequent retrain succeeds" <| fun _ ->
        let dir = mkTempDir ()
        try
            let opts = mkOptions dir
            let entries = Array.init 20 (fun i -> mkHardCaseEntry i (i % 2))
            writeHardCases opts.HardCasePath entries
            writeBaselineModel opts.ModelPath

            let provider = ModelVersionProvider("ml-test-initial")

            // First service: throws after 5 embeddings; the cycle MUST fail safely.
            let throwingEmbedder = ThrowingEmbedder(5)
            use service1 = new RetrainingService(opts, throwingEmbedder :> IEmbedder, provider :> IModelVersionProvider, new RetrainLock() :> IRetrainLock, NullLogger<RetrainingService>.Instance)
            // RunNowAsync must NOT throw — internal try/with catches.
            (service1.RunNowAsync(CancellationToken.None)).GetAwaiter().GetResult()
            // Initial version must NOT have been flipped (model write didn't happen).
            Expect.equal (provider :> IModelVersionProvider).CurrentVersion "ml-test-initial"
                "version must be unchanged after a failed cycle"

            // Second service: working embedder; cycle must succeed.
            let goodEmbedder = FakeEmbedder()
            use service2 = new RetrainingService(opts, goodEmbedder :> IEmbedder, provider :> IModelVersionProvider, new RetrainLock() :> IRetrainLock, NullLogger<RetrainingService>.Instance)
            (service2.RunNowAsync(CancellationToken.None)).GetAwaiter().GetResult()
            // Version flipped — Loop A is unaffected by the prior failure.
            Expect.notEqual (provider :> IModelVersionProvider).CurrentVersion "ml-test-initial"
                "version must have flipped after a successful cycle following a failed one"
        finally
            cleanupDir dir

// ── Test 6 — RETRAIN-04 model_version flip end-to-end ────────────────────────

let private test6_modelVersionFlip =
    testCase "RETRAIN-04: ModelVersionProvider.CurrentVersion flips after successful retrain" <| fun _ ->
        let dir = mkTempDir ()
        try
            let opts = mkOptions dir
            let entries = Array.init 20 (fun i -> mkHardCaseEntry i (i % 2))
            writeHardCases opts.HardCasePath entries
            writeBaselineModel opts.ModelPath

            let initialVersion = sprintf "ml-%s" (computeModelVersion opts.ModelPath)
            let provider = ModelVersionProvider(initialVersion) :> IModelVersionProvider
            Expect.equal provider.CurrentVersion initialVersion
                "provider seeded with initial model_version"

            let embedder = FakeEmbedder()
            use service = new RetrainingService(opts, embedder :> IEmbedder, provider, new RetrainLock() :> IRetrainLock, NullLogger<RetrainingService>.Instance)
            (service.RunNowAsync(CancellationToken.None)).GetAwaiter().GetResult()

            let newVersion = provider.CurrentVersion
            Expect.notEqual newVersion initialVersion
                "RETRAIN-04: provider.CurrentVersion must change after successful retrain"
            Expect.isTrue (newVersion.StartsWith("ml-")) "version retains 'ml-' prefix"

            // Sanity: router.zip.prev must exist (Phase 9 rollback contract)
            Expect.isTrue (File.Exists opts.PreviousModelPath)
                "router.zip.prev must be created BEFORE the rename"

            // Sanity: training-set.jsonl was saved for next cycle
            Expect.isTrue (File.Exists opts.TrainingSetPath)
                "training-set.jsonl must be written after successful retrain"

            // Sanity: state file with hard_case_count_at_retrain
            Expect.isTrue (File.Exists opts.StatePath)
                "state file must be written after successful retrain"
        finally
            cleanupDir dir

// ── Test 7 — RETRAIN-02 count trigger fires via PeriodicTimer (integration) ───

let private test7_countTriggerFires =
    testCase "RETRAIN-02: count-check PeriodicTimer fires retrain when threshold exceeded" <| fun _ ->
        let dir = mkTempDir ()
        try
            // Use IntervalMinutes=1 and CountCheckIntervalMinutes=1 so both PeriodicTimers
            // fire within 60s. Seed hard-cases.jsonl with > HardCaseCountTrigger entries BEFORE
            // StartAsync so the first count-check tick sees count >= threshold and triggers a cycle.
            // This specifically exercises the StartAsync path — distinct from RunNowAsync used in
            // Tests 4/5/6.
            let opts = { mkOptions dir with
                            IntervalMinutes           = 1
                            CountCheckIntervalMinutes = 1
                            HardCaseCountTrigger      = 4 }

            // Seed >4 hard cases so the count threshold is exceeded from the first count-check tick.
            let entries = Array.init 8 (fun i -> mkHardCaseEntry i (i % 2))
            writeHardCases opts.HardCasePath entries

            // Required initial model so Validator.computeBaseline has something to load.
            writeBaselineModel opts.ModelPath
            let initialModelVersion = sprintf "ml-%s" (computeModelVersion opts.ModelPath)
            let initialMtime        = File.GetLastWriteTimeUtc(opts.ModelPath)

            let provider = ModelVersionProvider(initialModelVersion) :> IModelVersionProvider
            let embedder = FakeEmbedder()
            use service = new RetrainingService(opts, embedder :> IEmbedder, provider, new RetrainLock() :> IRetrainLock, NullLogger<RetrainingService>.Instance)

            // Start via the IHostedService entry point — this is what production runs.
            // Distinct from RunNowAsync which bypasses both timers.
            (service.StartAsync(CancellationToken.None)).GetAwaiter().GetResult()

            // Poll for model file mutation (timer drove a cycle to completion).
            // PeriodicTimer fires the FIRST time AFTER the interval elapses, so we must wait
            // ~60s for IntervalMinutes=1. Bound the wait at 120s with a 1s poll interval.
            let deadline = DateTime.UtcNow.AddSeconds(120.0)
            let mutable changed = false
            while DateTime.UtcNow < deadline && not changed do
                Threading.Thread.Sleep(1000)
                if File.Exists(opts.ModelPath) then
                    let mtime = File.GetLastWriteTimeUtc(opts.ModelPath)
                    if mtime > initialMtime then changed <- true

            (service.StopAsync(CancellationToken.None)).GetAwaiter().GetResult()

            Expect.isTrue changed
                "RETRAIN-02: a PeriodicTimer-driven retrain cycle must complete within 120s"
            Expect.notEqual provider.CurrentVersion initialModelVersion
                "RETRAIN-02: ModelVersionProvider.Update must have fired from the timer-driven cycle"
            Expect.isTrue (File.Exists opts.StatePath)
                "RETRAIN-02: .last-retrain.json state file must be written by the timer-driven cycle"
        finally
            cleanupDir dir

// ── Test list ─────────────────────────────────────────────────────────────────

let tests =
    testSequenced (
        testList "RetrainingTests" [
            test1_mergerClassBalance
            test2_mergerBootstrap
            test3_validatorGate
            test4_concurrentTriggers
            test5_throwIsolation
            test6_modelVersionFlip
            test7_countTriggerFires
        ])
