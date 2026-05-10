# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-08)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Current focus:** Phase 14 IN PROGRESS — Wave 1 (14-01 cold-start CLI) complete. `--cold-start` flag with BCL-only `runColdStartBackup` function: timestamp-suffixed backup of 4 model/dataset files, then continues startup for fresh dummy model generation. DRY bootstrap preamble pattern (Logging.configure hoisted before --retrain/Kestrel split). Test baseline: 80 passed + 16 ignored + 0 failed.

## Current Position

Phase: 14 of 14 (Quality Fallback and Trace)
Plan: 1 of 6 in current phase — 14-01 complete
Status: Wave 1 plan 14-01 complete. ColdStart.fs (43 lines, BCL only) + Program.fs --cold-start wiring (DRY bootstrap preamble). Build clean (TreatWarningsAsErrors=true). **80 passed + 16 ignored + 0 failed**.
Last activity: 2026-05-10 — Completed 14-01-COLD-START-CLI-PLAN.md.

Progress: [████████████████████████████████████████] 48 of 53 plans (Phase 14 Wave 1 done)

## Performance Metrics

**Velocity:**
- Total plans completed: 32 (3 foundation + 2 streaming + 3 concurrency-gate + 3 ml-seam + 3 decision-logging + 3 real-ml-routing + 6 failure-detection + 3 retraining-loop + 3 canary-deployment + 3 health-fallback)
- Average duration: ~7 min
- Total execution time: ~198 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 3/3 | ~21 min | 7 min |
| 02-sse-streaming-pass-through | 2/2 | ~24 min | 12 min |
| 03-122b-concurrency-gate | 3/3 | ~53 min | 18 min |
| 04-ml-algorithm-seam | 3/3 | ~21 min | 7 min |
| 05-routing-decision-logging | 3/3 | ~19 min | 6 min |
| 06-real-ml-routing | 3/3 | ~18 min | ~6 min |
| 07-failure-detection-and-teacher-labeling | 6/6 | ~50 min | ~8 min |
| 08-retraining-loop | 3/3 | ~32 min | ~11 min |
| 09-canary-deployment | 3/3 | ~38 min | ~13 min |
| 10-health-fallback | 3/3 | ~40 min | ~13 min |

**Recent Trend:**
- Last 5 plans: 08-01 (~8 min), 08-02 (~7 min), 08-03 (~17 min), 09-01 (~8 min)
- Trend: Domain-field-cascade plans (many files, mechanical edits) are fast ~8 min; tests-only plans with ML.NET training take longer (~17 min)

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- Roadmap: SSE streaming (Phase 2) and concurrency gate (Phase 3) are atomic units — must not be split
- Roadmap (2026-05-08 reorganization): NEW Phases 4-9 ship the ML arc (handoff doc folded forward); old Phase 4 (Health/Fallback/graph_indexing-no-fallback) deferred to Phase 10; old Phase 6 (launchd/README) deferred to Phase 11; old Phase 5 (Observability+Tests) dissolved — OBS-01/03 absorbed into NEW Phase 5 (Decision Logging — Loop B's input), TEST-01/02 retroactively Complete via Phases 1-3 tests.
- **Heuristic SOFT-PAUSED (2026-05-08)**: ML is now the primary path. Heuristic code stays in `src/SmartRouter.Core/Heuristic.fs` as dormant emergency fallback (model-corrupt/missing scenario, debugging, rollback) but no new heuristic features. Phase 9 canary compares ML model versions to each other (no heuristic-vs-ML A/B). Phase 6 will flip `appsettings.json` `Routing.Algorithm` default from `"heuristic"` to `"ml"`. Snapshot at `archive/heuristic-baseline` branch + `v0.5-heuristic-baseline` tag (commit `a4cfce1`).
- Roadmap: graph_indexing no-fallback rule ships in same phase as health probing — now Phase 10 (was Phase 4)
- Roadmap: Phase 3 depends on Phase 1 only (not Phase 2); Phases 2 and 3 have no cross-dependency
- ML arc design source: /Users/ohama/projs/smart-router-distillation/docs/handoff-to-smart-router.md — 3-layer separation (code: Heuristic.fs vs ML.fs no cross-imports; config: Routing.Algorithm key; CLI: --routing-algorithm override). Heuristic and ML must have same signature `RoutingConfig -> RouterRequest -> RoutingDecision`.
- 01-01: `dotnet new slnx` unavailable in SDK 10.0.203 — SmartRouter.slnx written manually in XML (no functional difference)
- 01-01: launchSettings.json has no applicationUrl — appsettings.json is single source of truth for Kestrel binding (OPS-04)
- 01-02: RoutingConfig is a plain F# record in Core (no IOptions<T>); Cli constructs it from appsettings.json at composition time (ARCH-01)
- 01-02: taskToDecision is utility-only (compile-time DU completeness anchor); routeRequest/tryTaskTable read config.TaskTable Map at runtime (ROUT-05 locked decision)
- 01-02: MessageRole.System DU case shadows System namespace — test code must use String.replicate not System.String.replicate
- 01-03: wireJsonOptions uses standard STJ without FSharpConverter for incoming wire body — FSharp.SystemTextJson record converter requires all fields to be present; standard STJ tolerates missing optional fields
- 01-03: Lazy probe returns Result<string, RouterError> (not ModelInfo record) — probe failure maps directly to ModelUnavailable, no silent fallback to empty model id
- 01-03: F# interpolated strings reject escaped quotes inside interpolation expressions — use sprintf for error messages containing quotes
- 01-03: Phase 3 concurrency gate swap is one-line DI change: AddSingleton<IUpstreamClient>(QueueDispatcher(QwenUpstreamClient())) in CompositionRoot
- 02-01: F# task{} does not support do! in finally blocks — enumerator.DisposeAsync() called explicitly in each catch arm (normal, cancel, error); semantically equivalent to finally
- 02-01: StreamAsync uses direct let! resp = client.SendAsync(..., HttpCompletionOption.ResponseHeadersRead, ct) — no task{return!...} wrapper
- 02-01: StreamingTests deferred to Plan 02-02 — 02-01 ships the implementation only; 02-02 owns the fake-Kestrel integration test harness
- 02-02: ConfigurationManager.AddInMemoryCollection requires explicit cast to IConfigurationBuilder — extension method on interface, not concrete type
- 02-02: F# task{} finally blocks do not allow do! — use .GetAwaiter().GetResult() for async teardown (StopAsync/DisposeAsync) in test helpers
- 02-02: ctx.RequestAborted in Kestrel fires on TCP socket close (response.Dispose()), not on CancellationToken.Cancel() — cancellation test must close the socket
- 02-02: startTestRouter requires full Routing section in AddInMemoryCollection — validateConfig (called via DI singleton factory) checks all canonical tasks are present
- 03-01: IUpstreamClient port shape changed to take decision: RoutingDecision (Option A, locked decision from CONTEXT.md) — QueueDispatcher dispatches on decision.Target + decision.Priority without a separate interface
- 03-01: Two Queue<Ticket> (high/low) chosen over PriorityQueue<T,int> for two-level priority — FairnessK enforcement is explicit; no rebuild-on-promote complexity
- 03-01: Sub-pattern A: dispatcher acquires sem122b BEFORE signalling TCS — individual requests park on tcs.Task, never on sem.WaitAsync (PITFALL-9 mitigation)
- 03-01: Linked CTS created AFTER enqueue122b returns — timeout starts at slot grant, queue wait never burns timeout budget (PITFALL-11 mitigation)
- 03-01: startTestRouter requires Queue section in AddInMemoryCollection — QueueDispatcherOptions.MaxConcurrent122B defaults to 0 which the QueueDispatcher constructor rejects
- 03-02: StatsWire is a separate private record with snake_case fields (not StatsSnapshot) — F# records serialize as PascalCase by default; StatsWire fields are lowercase and emit correctly via jsonOptions
- 03-02: QueueDepth is always transient in the dispatcher — dispatcher dequeues a ticket within microseconds of signal.Release() and blocks on sem.WaitAsync; QueueDepth=N assertions in polls race the dispatcher; use Active122B + SemaphoreAvailable + fake.CallCount as stable observables instead
- 03-02: Test 3 PITFALL-10 proof uses LatencyFake not FakeUpstreamClient — gate-per-call would require knowing the correct drain order before the test runs (circular dependency); LatencyFake auto-completes and lets WhenAll observe the completion order vector
- 03-03: LatencyFakeLoad re-declared private in LoadTests.fs (not imported from QueueTests) — avoids cross-module coupling; Task.Run lambda cast to :> Task to resolve FS0041 overload ambiguity for Task<Result<_,_>> return type
- 03-03: ptestCaseAsync chosen over env-var gate — Expecto pending is idiomatic and tooling-friendly; default run reports "2 ignored" (not "0 run")
- 04-01: RoutingAlgorithm alias lives in Domain.fs (upstream of both Heuristic.fs and ML.fs in compile order) — both algorithm modules compile before Routing.fs and need to satisfy the type
- 04-01: Wave 1 boundary state — Tests project build fails because Cli (ChatCompletions.fs) still uses old 2-arg routeRequest; Cli callsite fix + full dotnet test 39/39 deferred to 04-02
- 04-01: ML.fs comment mentioning Heuristic module name revised to avoid triggering CI isolation grep false positive (grep pattern without trailing dot matched comment text)
- 04-02: Func<IServiceProvider, RoutingAlgorithm> explicit cast required for AddSingleton<T> when T is an F# function-type alias — without the Func wrapper, F# currying causes DI overload resolver to reject the factory lambda (inferred as 4-arg rather than Func<IServiceProvider, RoutingAlgorithm>)
- 04-02: open Microsoft.Extensions.Configuration required in Program.fs for both IConfigurationBuilder cast and AddInMemoryCollection extension method (was missing)
- 04-02: RoutingTests.fs 04-01 latent bug fixed — after open SmartRouter.Core.Heuristic, correct is applyHeuristic not Heuristic.applyHeuristic; Heuristic is not a sub-module; tests were cached from --no-build in 04-01
- 05-01: ChannelClosedException must be caught alongside OperationCanceledException in BackgroundService consumer loop — StopAsync calls TryComplete() which closes the channel before stoppingToken is cancelled; both exceptions are valid graceful-shutdown signals
- 05-01: DI triple-registration pattern: AddSingleton<Concrete>, AddSingleton<IInterface>(sp -> GetRequiredService<Concrete>()), AddHostedService<Concrete>(sp -> ...) — single instance for all three roles; do NOT use three separate AddSingleton<DecisionLogWriter>
- 05-01: app.Use requires explicit Func<HttpContext, RequestDelegate, Task> cast for F# lambda — without it the compiler infers incorrect arity
- 05-01: new DecisionLogWriter(...) required — BackgroundService implements IDisposable; F# FS0760 enforces new Type(...) syntax when used as a value (not a constructor call in a let binding)
- 05-01: JsonFSharpConverter() accessed via open System.Text.Json.Serialization (not FSharp.SystemTextJson prefix) — consistent with existing Json.fs pattern
- 05-02: RoutingAlgorithmRegistration lives in its own Adapters/RoutingAlgorithm.fs file (not in CompositionRoot.fs) so ChatCompletions.fs (compile pos 14) can open it without F# compile-order violation — CompositionRoot.fs is at compile pos 16
- 05-02: streamError mutable flag in streaming branch tracks whether the Error arm fired during the enumerator loop so the normal-exit disposal path can append ;stream_error to the reason without a second try/with
- 05-02: escapeJsonString private helper in ChatCompletions.fs prevents JSON injection when upstream error messages contain quotes/newlines in SSE error event bodies
- 05-02: Streaming path logs AFTER enumerator.DisposeAsync() in all 3 try/with arms so latency_ms reflects time-to-last-byte (LOG-01)
- 05-03: CapturingSink ILogEventSink installed as Log.Logger BEFORE configureServices + testBuilder.Host.UseSerilog() — Serilog Console sink captures Console.Error reference at construction time, making post-hoc Console.SetError redirect ineffective; the only correct approach is a custom ILogEventSink installed before the host pipeline builds
- 05-03: startFakeStreamingErrorUpstream returns HTTP 502 (not malformed SSE payload) — QwenUpstreamClient.StreamAsync yields Ok line for any non-blank SSE line without JSON-parsing the payload; Error arm is only triggered by non-2xx HTTP status
- 05-03: DecisionLog:Directory override in AddInMemoryCollection required in all test modules that call configureServices to prevent JSONL pollution under bin/Debug/net10.0/logs/decisions/
- 06-01: IEmbedder + IClassifier use float32[] not ReadOnlyMemory<float32> — simpler BCL type; Cli adapters can wrap to ReadOnlyMemory if OnnxRuntime requires it in 06-02
- 06-01: runSync uses Task.Run factory lambda (Task.Run<'a>(Func<Task<'a>>(taskFactory))) — avoids capturing already-started Task onto ASP.NET SyncContext; canonical deadlock-prevention guard
- 06-01: CompositionRoot buildRoutingConfig defaults MlThreshold to 0.5f when opts.MlThreshold = 0.0f — CLIMutable float32 defaults to 0.0f when JSON key absent; no appsettings.json change needed at wave 1
- 06-01: applyML placeholder retained in ML.fs wave 1 so CompositionRoot "ml" branch compiles — swapped out in 06-02 Task 3; 4 NuGet pins (OnnxRuntime 1.25.1, Tokenizers 2.0.0, ML 5.0.0, Extensions.ML 5.0.0) on Cli resolved cleanly with no System.Memory conflict
- 06-02: SentencePieceTokenizer.Create uses `addBeginningOfSentence` (not `addBeginOfSentence`); EncodeToIds maxTokenCount overload requires ref out-params (normalizedText, charsConsumed); DenseTensor.Buffer.ToArray() not Tensor.ToArray() (Buffer is on DenseTensor subclass, not abstract Tensor<T>); SentencePieceTokenizer has no IDisposable in 2.0.0
- 06-02: Task 3 (remove applyML) and Task 4 (rewrite tests) committed together atomically — removing applyML immediately breaks MLRoutingTests.fs build
- 06-02: Routing.Algorithm default flipped to "ml"; StreamingTests + LoggingTests unaffected (both override to "heuristic" explicitly); MLRoutingTests Tests 4+5 gated with mlTestCase (ptestCase when models/embed/* absent)
- 06-03: Expect.isNotNull on F# interfaces requires box cast (Expect.isNotNull (box iface)) — F# interfaces are non-nullable in .NET 10; plain isNotNull fails with FS0001
- 06-03: mlTestCase (ptestCase-based) for tests that directly construct BgeM3Embedder; skiptest (runtime guard) for tests calling configureServices — DI throws at registration time when files absent, not at assertion time
- 06-03: CLS-03 cosine threshold calibration policy — default > 0.7; if bge-m3 measures 0.6-0.7 on test pairs, lower to > 0.6 and document measured values (per 06-CONTEXT.md); not yet measured (model files absent on executor)
- 07-01: RetrainingPorts.fs placed after Routing.fs and before Ports.fs in Core.fsproj (per CONTEXT.md — no dependency on Routing/ML/Ports; BCL-only; pure-Core invariant confirmed by grep)
- 07-01: HardCaseDatasetWriter inherits BackgroundService at stub stage so Plan 07-05 AddHostedService<HardCaseDatasetWriter> DI registration requires no signature change
- 07-01: TeacherLabelerOptions [CLIMutable] record declared in stub file; Plan 07-03 replaces LabelAsync body only; constructor signature final: (httpFactory: IHttpClientFactory, options: TeacherLabelerOptions)
- 07-01: Wave 1 stub pattern: constructor signatures are final at plan 01; Wave 2 plans (07-02/03/04) replace method bodies exclusively — no .fsproj write conflicts in parallel execution
- 07-02: jsonOpts uses SnakeCaseLower + JsonFSharpConverter (mirrors DecisionLogWriter) for correct JSONL round-trip when reading DecisionLog records — bare STJ rejects F# records
- 07-02: PromptText = None set explicitly in HardCase output per LOG-01 privacy decision; Phase 8 BackgroundService and seed script fill prompt text downstream via inline path
- 07-03: Pitfall 5 enforced — TeacherLabeler uses IHttpClientFactory.CreateClient("teacher"); never IUpstreamClient/QueueDispatcher (would starve real 122B inference traffic of its SemaphoreSlim(1) slot)
- 07-03: Persistent daily cap counter rotates by UTC date (file path datasets/teacher-cap-YYYY-MM-DD.json); counter incremented BEFORE HTTP call so cap-hit returns Skipped without contacting upstream; counter survives router restarts
- 07-03: ROUTE_122B wins over ROUTE_35B when both sentinels appear in teacher response — safety bias for ambiguous teachers
- 07-04: BoundedChannelFullMode.Wait (NOT DropWrite) — losing training data is unacceptable; producers tolerate back-pressure (writes infrequent — one per labeled hard case)
- 07-04: Dedupe via HashSet<string> keyed on $"{CorrelationId}|{PromptHash}", seeded from existing JSONL file at first ExecuteAsync iteration; misses logged Debug and dropped silently
- 07-04: F# parsing trap fix — `try X with _ -> (); Y` only runs Y in the exception arm (semicolon binds inside with-clause); split into two explicit statements when both X and Y must always execute
- 07-05: --retrain detection runs BEFORE WebApplication.CreateBuilder; uses a separate minimal Host with the same configureServices, resolves the 3 ports, runs the offline pipeline, exits 0; never starts Kestrel
- 07-05: configureServices ML init guarded on Routing.Algorithm == "ml" so --retrain works without bge-m3 model files; --retrain host injects "heuristic" override
- 07-05: AddResilienceHandler ShouldHandle predicate explicitly excludes 4xx — retrying on 4xx wastes cost cap budget on a non-recoverable failure
- 07-05: HardCaseDatasetWriter triple-registration mirrors DecisionLogWriter (concrete + IInterface alias + AddHostedService<concrete>); single instance, three roles
- 07-05: scripts/seed-hard-cases.fsx writes module-level entries inside a function (FS0524 forbids `use` at .fsx top level); StreamWriter uses UTF8Encoding(false) to avoid BOM in JSONL output
- 07-06: AddHttpClient F# lambda overload trap — services.AddHttpClient(name, fun c -> ...) does not bind reliably; use services.AddHttpClient(name).ConfigureHttpClient(...) chain
- 07-06: Private F# [<CLIMutable>] record requires JsonFSharpConverter for STJ; default ObjectDefaultConverter cannot access private parameterless ctor
- 07-06: Drain test pattern — Thread.Yield() between AppendAsync calls and StopAsync exercises both the steady-state and drain paths in BackgroundService
- 08-01: RESEARCH.md compile-order is inverted for Phase 8 — Retrainer.fs MUST precede DatasetMerger.fs and Validator.fs (both open Retrainer for TrainSample). RESEARCH.md lines 66-71 lists DatasetMerger before Retrainer; that causes FS0039. Use Retrainer→DatasetMerger→Validator order.
- 08-01: Lock 1 (fallback_rate) enforced — `fallback_rate := 1.0 - metrics.PositiveRecall` in both computeBaseline and validate; decoupled from MlThreshold config
- 08-01: Lock 5 (mandatory pipeline order) enforced at Retrainer signature level — `retrain : MLContext -> IDataView -> string -> float32 -> ITransformer`; caller does TrainTestSplit and passes split.TrainSet as IDataView; Retrainer never sees held-out set
- 08-01: Lock 3 (first-retrain bootstrap) enforced — when oldSamples=[||], merge returns new samples post-rebalance without 70/30 scaling, no synthetic noise
- 08-01: ARCH-01 preserved for IModelVersionProvider — port uses only string + unit (BCL); zero forbidden imports in Core/RetrainingPorts.fs
- 08-01: TrainSample mirrors MlNetClassifier.RouteInput exactly: [<CLIMutable>] + [<VectorType(1024)>] Features:float32[] + Label:bool (true=Route122B positive class)
- 08-01: DatasetMerger.hardCaseToTrainSample takes embed callback (string -> float32[]) so DatasetMerger stays free of BgeM3Embedder dependency; Plan 08-02 RetrainingService closes over IEmbedder.EmbedAsync
- 08-02: SemaphoreSlim(1,1).Wait(0) skip-if-busy — Mutex would break thread affinity across task{} await points (CONTEXT.md Lock 7)
- 08-02: ModelVersionProvider double-reg pattern (concrete AddSingleton<ModelVersionProvider> + interface alias AddSingleton<IModelVersionProvider> resolving via GetRequiredService); not triple-reg since ModelVersionProvider is not a BackgroundService
- 08-02: RetrainingService double-reg pattern (concrete AddSingleton<RetrainingService> + AddHostedService factory); no IInterface alias since RetrainingService has no IInterface consumer
- 08-02: ChatCompletions reads IModelVersionProvider.CurrentVersion per-request (live-updated) instead of frozen-at-startup RoutingAlgorithmRegistration.ModelVersion; all 8 buildDecisionLog call sites updated
- 08-02: runRetrain pipeline order: split FIRST → train on split.TrainSet only → evaluate baseline + candidate on split.TestSet (fair comparison; held-out genuinely held out — CONTEXT.md Lock 5)
- 08-02: ExceptionDispatchInfo.Capture(oce).Throw() instead of reraise() for OperationCanceledException in task{} nested try/with — FS0413 prevents reraise() inside CE try/with handlers; ExceptionDispatchInfo preserves original stack trace
- 08-02: Cumulative training-set persistence: Array.append oldEntries hardCaseEntries saved after each successful retrain (not just hardCaseEntries); first-retrain bootstrap: oldEntries=[||] → cumulative=hardCaseEntries (Lock 3)
- 08-03: CapturingSink ILogEventSink pattern (mirror LoggingTests.fs from Phase 5) — installed before service construction; test4 asserts exactly 1 "starting retrain cycle" log + >=1 "skipping trigger" log to prove SemaphoreSlim skip semantics (CONTEXT.md Lock 7)
- 08-03: test7_countTriggerFires uses StartAsync (NOT RunNowAsync) with IntervalMinutes=1 to exercise the count-check PeriodicTimer branch — satisfies ROADMAP "two integration tests" criterion for RETRAIN-02 alongside test1_runNowAsync
- 08-03: FakeEmbedder requires Task.Yield() inside task{} — without it, runRetrain executes synchronously (Task.FromResult doesn't yield), both concurrent RunNowAsync calls acquire the semaphore sequentially; Task.Yield() forces genuine suspension so t1 holds semaphore while t2 finds it taken
- 08-03: ExceptionDispatchInfo.Capture(oce).Throw() (from Plan 08-02) preserves OperationCanceledException through task{} await points — reraise() inside task{} nested try/with is invalid (F# FS0413); test5 force-throw isolation confirmed: cancellation during retrain propagates through BackgroundService loop so StopAsync works
- 08-03: Canonical test run is dotnet run -- --sequenced (73 passed, 10 ignored, 0 failed); parallel mode shows pre-existing flakiness in QueueTests PITFALL-10 and HardCaseDatasetTests graceful-drain tests when run alongside CPU-heavy ML.NET training
- 08-tests-flake (commit dd1da7d): HardCaseDatasetTests "graceful StopAsync drains in-flight entries" stabilized — replaced Thread.Yield() with Thread.Sleep(200). Pre-existing Phase 7 flake exposed by Phase 8 ThreadPool contention (66→73 tests). File polling failed because HardCaseDatasetWriter holds FileShare.None — peeking the file conflicts with the writer's exclusive lock. 5/5 parallel runs pass after fix. Stable now even without --sequenced.
- 08-VERIFICATION (verifier scored 27/27 must-haves): Loop B is real and proven. Notable findings: ChatCompletions reads IModelVersionProvider per-request via DI (line 326+114, NOT frozen at startup); Lock 5 split-FIRST pipeline confirmed in runRetrain; cumulative training-set persistence active; RETRAIN-04 verified via provider.CurrentVersion (equivalent to JSONL model_version assertion). Three human-verification items deferred (real-ONNX retrain timing under launchd, PredictionEnginePool hot-swap under live traffic) — non-blocking; require 122B server.
- 09-01: 19 RouterRequest + RoutingDecision construction sites updated (Lock 16); new fields CorrelationId + ModelVersion are required at construction; "" is the empty-string sentinel for non-ML stages
- 09-01: ICanaryGate Core port (BCL-only, CanaryPorts.fs); NullCanaryGate inline object expression in CompositionRoot "ml" branch as Plan 09-02 placeholder; FeatureManagementCanaryGate concrete in Plan 09-02
- 09-01: IModelVersionProvider extended with CanaryVersion getter + UpdateCanary setter; concrete ModelVersionProvider.fs has shared lock gate + mutable canary field; same DI registration as Phase 8 (no new registrations needed)
- 09-01: ML.fs makeApplyML 6-param factory — SINGLE isCanary boolean gates classifier selection AND ModelVersion assignment in adjacent let-binding (RESEARCH §11 Pitfall 8); CorrelationId="" → bypass canary gate (NullCanaryGate path preserved for non-HTTP construction sites)
- 09-01: CanaryPorts.fs placed BEFORE ML.fs in Core.fsproj (deviation from plan's "after RetrainingPorts.fs") — F# compile-order requires it because ML.fs opens SmartRouter.Core.CanaryPorts; ARCH-01 preserved
- 09-01: NuGet pin Microsoft.FeatureManagement.AspNetCore 4.5.0 added to Cli.fsproj only (preserves ARCH-01 for Core)
- 09-01: appsettings.json Routing.Canary section (7 keys: CanaryModelPath, PercentageEnabled=10, RollingWindowSeconds=60, WatchdogPollIntervalSeconds=10, AutoRollbackThreshold=0.10, AutoRollbackEnabled=false, MinBaselineSampleSize=50) + feature_management section (Microsoft.Targeting filter, DefaultRolloutPercentage=10)
- 09-01: buildDecisionLog gains decisionOpt: RoutingDecision option param — None for pre-routing failures, Some decision for Ok-routing branches; cascade: decision.ModelVersion → versionProvider.CurrentVersion for "" sentinel
- 09-02: 8 new Cli adapter/endpoint files (7 Adapters/ + 1 Endpoints/): CanaryTargetingAccessor, CanaryState, CanaryGate, CanaryMetrics, CanaryWatchdog, RetrainLock, CanaryService, Endpoints/Canary.fs
- 09-02: ContextualTargetingFilter via WithTargeting<CanaryTargetingContextAccessor>; PercentageFilter EXPLICITLY FORBIDDEN (non-sticky — random per evaluation, breaks cohort assignment)
- 09-02: TryAddSingleton<ICanaryGate>(NullCanaryGate) + TryAddSingleton<ICanaryMetrics>(NoOpCanaryMetrics) Step 1.0 UNCONDITIONAL before ML branch; plain AddSingleton inside ML block overrides via last-registration-wins
- 09-02: CanaryService implements IHostedService (not BackgroundService) with FileSystemWatcher armed in StartAsync; StopAsync disposes in try/with (separate statement, F# parsing trap avoidance)
- 09-02: F# FS0960 — let/do bindings must precede interface implementations in type body; CanaryService let mutable watcher + onCanaryFileMutation moved to top of type
- 09-02: F# compile-order fix — RetrainLock.fs placed BEFORE RetrainingService.fs in fsproj (consumed module must compile first: FS0039)
- 09-02: RetrainingService refactored to accept shared IRetrainLock parameter (replaces private SemaphoreSlim); skip-if-busy + ExceptionDispatchInfo.Capture semantics preserved
- 09-02: /canary endpoint shape: GET status / POST promote (File.Move under IRetrainLock; 409 on contention) / POST rollback (idempotent SetPercentage(0)) / POST enable?percentage=N (loopback-only)
- 09-02: ChatCompletions threads ICanaryMetrics; metrics.Record after each decisionLogger.Log in 5 Ok-decision arms; metricCohort helper computes isCanary from ModelVersion.EndsWith("-canary") + isFallback from IsFallback || ;upstream_error || ;stream_error suffixes
- 09-02: Test construction site Rule 3 auto-fixes: MLClassifierTests MlNetClassifier(pool, "router"); RetrainingTests (5 sites) RetrainingService(opts, emb, vp, RetrainLock()); Plan 09-03 owns tests/ going forward
- 09-03: 12 tests (5 CANARY-01 unit + 1 CANARY-02 integration + 5 CANARY-03 manual+auto + 1 CANARY-04 watcher)
- 09-03: mkStableCorrelationIds (Random(seed=42) → 16-byte → Guid) — deterministic test correlation_ids; binomial 95% CI [80,120] bit-stable (96 hits measured)
- 09-03: JsonDocument.Parse + GetProperty("model_version") + EndsWith("-canary") for cohort assertion (replaces fragile string-contains)
- 09-03: CapturingSink ILogEventSink for AUTO-ROLLBACK log verification (mirrors LoggingTests.fs Phase 5 pattern)
- 09-03: canary04_fileSystemWatcher uses CanaryModelExists=false override + 200ms settle + 20x100ms poll for -canary suffix + delete-and-clear test (verifies Lock 9)
- 09-VERIFICATION (verifier scored 36/36 must-haves): Canary deployment is real and proven. Notable findings: Pure-Core invariant preserved (CanaryPorts.fs BCL-only — only System.Threading + System.Threading.Tasks); ML.fs single isCanary boolean correctly gates BOTH classifier selection AND ModelVersion assignment (Pitfall 8 verified); CompositionRoot Step 1.0 TryAddSingleton fallbacks precede ML conditional block (B2 fix); CanaryService FileSystemWatcher arms in StartAsync, disposes in StopAsync via separate try/with (no F# parsing trap); ContextualTargetingFilter via WithTargeting<>, PercentageFilter zero hits in src; AutoRollbackEnabled defaults to false (Lock 1) — proxy signal will become real in Phase 10. Three human-verification items deferred (real traffic distribution at scale, real launchd auto-rollback under upstream failures, macOS FSEvents latency) — non-blocking; require live traffic.
- 09-03: Auto-rollback test drives ICanaryMetrics.Record() directly via DI (not real HTTP traffic) — avoids fake-upstream cohort-coordination problem; 30 baseline success + 30 canary fail → delta=1.0 >> threshold=0.10 → watchdog fires
- 09-03: Phase 9 COMPLETE. All Locks 1-17 (CONTEXT.md) have corresponding test or grep guard. Ready for /gsd:verify-phase 9 + /gsd:uat-phase 9.
- 10-01: RoutingReason.FallbackTo35B DU case added (6th case, no payload — routing_reason JSONL literal = "fallback_to_35b"); formatReason exhaustive match in DecisionLogger.fs cascaded (FS0025 confirmed then resolved under TreatWarningsAsErrors=true)
- 10-01: IHealthProbe extended with IsReachable: ModelId -> bool (sync fast-path for QueueDispatcher + ChatCompletions hot path) + LastProbedAt: ModelId -> DateTimeOffset (for /health endpoint body rendering); BCL-only preserved (ARCH-01); 6 total abstract members in Ports.fs
- 10-01: appsettings.Routing.Health section (PollingIntervalSeconds=10, ConsecutiveFailureThreshold=1) per Lock 1+2; consumed by Plan 10-02 HealthService via IOptions binding
- 10-01: Canary.AutoRollbackEnabled flipped from false to true — Phase 10 makes fallback_used signal real (REL-03 fires fallback_used=true); probe-blip risk damped by ConsecutiveFailureThreshold (raise to 2 in prod if flapping)
- 10-01: MLRoutingTests.fs read-only confirmation passed — both match sites at lines 167+190 have | r -> failtestf catch-all arms; RoutingTests.fs match sites all have | r -> failtestf catch-alls; no test edits made (per CONTEXT D12)
- 10-02 Task 1: HealthService BackgroundService probes /v1/models per upstream every PollingIntervalSeconds (default 10s); ConsecutiveFailureThreshold=1 default; ConcurrentDictionary state; ExceptionDispatchInfo.Capture for OCE through task{} await points; triple-reg DI; IsReachableAsync is curried (target -> ct -> Task<bool>) NOT tupled
- 10-02 Task 2a: 5 named HttpClients via .ConfigureHttpClient chain (forbidden 2-arg form replaced); -stream clients have NO retry handler; non-stream clients use AddResilienceHandler with shouldHandle 5xx + transient (NOT 4xx); open System.Threading.Tasks required for ValueTask in retry predicate; HealthService() needs 'new' keyword (BackgroundService: IDisposable)
- 10-02 Task 2b: QueueDispatcher fallback in BOTH CompleteAsync + StreamAsync (taskSeq yield-and-terminate); QwenUpstreamClient.resolveProbe stream-aware (4-way map: target x bool); ChatCompletions Option B early-return + shadow-rebind (NOT wrap-in-else); 5+ existing decisionLogger.Log calls auto-pickup fallback flag from rebound shadow variable
- 10-02 Task 3: 11 test construction sites updated for QueueDispatcher 3-arg signature; alwaysReachableProbe stub used as default fake; AutoRollbackEnabled=true (10-01) now active; CanaryWatchdog rolling-60s metric becomes meaningful when REL-03 fires real fallback_used=true records
- 10-03: acquireDeadPort uses TcpListener(Loopback, 0) start-then-stop (not privileged port 1 or hard-coded 65530) — OS-allocated guaranteed-unused port, no TIME_WAIT since no connections accepted
- 10-03: Fake upstreams always return 200 + model-id JSON for GET /v1/models — neutralizes QwenUpstreamClient lazy probe (Lazy<Task<Result>>) interference with callCount assertions
- 10-03: HLTH-06 elapsed threshold is >= 400ms (not >= 1000ms) — Polly ±50% jitter on 1s base can produce ~500ms; 400ms proves retry happened without flaking on high-jitter runs
- 10-03: HLTH-05 body read uses ResponseHeadersRead + exception catch — Kestrel early-return 503 closes TCP before chunked terminal frame; status code 503 is the authoritative assertion
- 10-03: All 5 HLTH tests pass: 83 passed, 17 ignored, 0 failed (net +5 from Phase 9 baseline of 78). Phase 10 COMPLETE.
- 11-01: /v1/models endpoint reuses existing health-probe HttpClient + IHealthProbe gating (no new named client; no new DI registrations)
- 11-01: JsonElement.Clone() called before `use doc` exits — Lock 14 silent-corruption guard (non-negotiable; without it JsonElements become invalid memory references)
- 11-01: Both-upstreams-down returns 200 + empty data array (Lock 11; mirrors /stats graceful-degradation; 503 reserved for actual server errors)
- 11-01: 3 integration tests use StubHealthProbe last-registration-wins DI override (no FakeHealthProbe service replacement; deterministic without probe-cycle timing)
- 11-01: Pure-Core invariant preserved (no src/SmartRouter.Core/ changes); 83→86 passed, 17 ignored unchanged
- 11-03: README.md at repo root (1020 lines, 14 sections) covering all operator concerns end-to-end per ROADMAP SC#4
- 11-03: All 8 endpoints + 7 task types + Loop A/B + canary workflow + launchd setup documented; bge-m3, LbfgsLogisticRegression, ContextualTargetingFilter all named
- 11-03: README links to documentation/howto/ (11 howtos) and .planning/ROADMAP.md for deeper material; README is operator-facing only (not technical reference — that's CLAUDE.md / .planning/)
- 11-03: No code changes — pure documentation plan; 86 pass + 17 ignored test count unchanged
- 11-02: deploy/com.ohama.smart-router.plist mirrors qwen36-35b + qwen122b convention exactly (4-space XML, KeepAlive=<true/>, ThrottleInterval=30, RunAtLoad=<true/>); plutil -lint OK
- 11-02: dotnet absolute path /opt/homebrew/bin/dotnet (operator's host; PATH unavailable to launchd at load time per OPS-02)
- 11-02: WorkingDirectory /Users/ohama/llm-system/services/smart-router — relative paths in appsettings.json (models/, datasets/, prompts/, logs/) resolve from this root
- 11-02: install-launchd.sh does NOT auto-execute launchctl load (Lock 21) — manual UAT step on host since SC#1+SC#2 require live macOS launchd interaction; script prints next-steps heredoc with $ prompt prefix
- 11-02: Framework-dependent publish (PublishTrimmed=false — ML.NET reflection breaks under trimming; operator already has .NET 10 runtime); EnvironmentVariables adds HOME + ASPNETCORE_ENVIRONMENT (qwen plists only need PATH)
- 12-02: Q1=B split pattern — configureRequestPipeline (unconditional ML wiring, production HTTP path) + configureWithoutMl (offline retrain subset: HTTP factories + DecisionLogWriter + retrain ports + ModelVersionProvider placeholder; no IEmbedder/IClassifier/RoutingAlgorithmRegistration/HealthService/CanaryService/RetrainingService BackgroundService)
- 12-02: BgeM3EmbedderOptions does not exist as a type — BgeM3Embedder constructor takes onnxPath/tokenizerPath/maxTokens directly; configureWithoutMl omits the non-existent IOptions binding
- 12-02: configureServices backwards-compat alias = configureRequestPipeline; removed in 12-05 after test fixture migration to configureWithoutMl
- 12-05: option(b) for HealthFallbackTests — configureWithoutMl + manual HealthService triple-reg + QueueDispatcher with real IHealthProbe; keeps fallback wiring without triggering ensureEmbeddingFilesPresent
- 12-05: ICanaryMetrics lives in SmartRouter.Cli.Adapters.CanaryMetrics (NOT Core.CanaryPorts); test fixtures must reference the Cli namespace
- 12-05: QueueDispatcherOptions must be manually bound via services.Configure<QueueDispatcherOptions> in each fixture that manually registers QueueDispatcher (configureWithoutMl excludes it)
- 12-05: Phase 13-02 executor must add NullLogger<HealthService> + NullLogger<QueueDispatcher> at manual instantiation sites in StreamingTests/LoggingTests/HealthFallbackTests when those ctor params are added
- 12-06: configureServices backwards-compat alias RETAINED — ModelsTests.fs still uses it; remove alias after ModelsTests migration to configureWithoutMl
- 12-06: ModelsTests MODELS-01/02/03 error with IEmbedder (pre-existing consequence of Phase 12 unconditional ML init); migrate ModelsTests.fs to configureWithoutMl to fix (small mechanical change — same option-b pattern as HealthFallbackTests)
- 12-06: Phase-level grep checklist complete — all 5 patterns return 0 hits; all 3 file-absence checks pass; build clean; new baseline 59 passed + 16 ignored + 3 errored
- 11-VERIFICATION (verifier scored 22/22 automated must-haves; status=human_needed): Phase 11 structurally complete. Models.fs reuses health-probe HttpClient + IHealthProbe.IsReachable gating + JsonElement.Clone() guard + id-only dedupe; plist mirrors operator's qwen36-35b/qwen122b convention exactly (plutil -lint clean); install-launchd.sh does NOT auto-execute launchctl load (heredoc only); README 1020 lines, 14 sections, all 25 required terms present. API-06 + OPS-01..03 marked Complete in REQUIREMENTS.md. Three deferred host-UAT items (ROADMAP SC#1/SC#2/SC#3) — require live macOS host with launchd + running mlx_lm servers; non-blocking for milestone v1.0 readiness because they are operator-driven verification of artifacts that have already been built and structurally validated.
- ROADMAP SC#2 wording note: ROADMAP says "restart within 5 seconds" but ThrottleInterval=30 (locked from operator's qwen plist convention) means actual restart latency is ~30-35s. README §12.1 documents the actual 30s timing. Operator may flip to ThrottleInterval=5 in deploy/com.ohama.smart-router.plist if 30s is unacceptable — single-line edit, no code change required.
- 10-VERIFICATION (verifier scored 30/30 must-haves): Phase 10 goal fully achieved. ChatCompletions pre-flight at lines 229-268 (BEFORE SSE headers); QueueDispatcher dual-layer fallback at lines 242-261 + 313-339 (CompleteAsync + StreamAsync); HealthService 135 lines with ConcurrentDictionary + PeriodicTimer + ExceptionDispatchInfo.Capture at all 3 OCE points; ARCH-01 invariant preserved (only IHealthProbe BCL-only port added to Core); REL-01..04 + API-05 + TEST-05 all marked Complete in REQUIREMENTS.md. ML feedback loop is now self-sustaining: real upstream failures → fallback fires → fallback_used=true in JSONL → FailureDetector → TeacherLabeler → Retraining loop. Three human-verification items deferred (live mlx_lm.server downtime detection, real Hermes Agent fallback round-trip, AutoRollback under production load) — non-blocking; require live upstream.

- 13-01: Serilog.Sinks.File pinned at 7.0.0 (latest stable; plan suggested 6.0.0); Serilog.Settings.Configuration at 10.0.0 (plan suggested 9.0.0); both resolved cleanly
- 13-01: configure(IConfiguration) moved AFTER WebApplication.CreateBuilder — IConfiguration needed for Logging:Directory read; pre-init crash window uses eprintfn + Log.Fatal dual write
- 13-01: File path pattern: Path.Combine(logDir, "smart-router-.log") → Serilog RollingInterval.Day appends date token → smart-router-20260509.log / smart-router-20260509_001.log (50MB rollover)
- 13-01: OBS-04 invariant preserved: Console sink standardErrorFromLevel=Verbose unchanged; file sink is purely additive
- 13-01: Enrich.WithProperty("correlation_id", "-") registered BEFORE WriteTo sinks — ensures [-] renders in background logs; LogContext.PushProperty overrides at request scope (CorrelationMiddleware)
- 13-02 executor note: Phase 12-05 stated "Phase 13-02 executor must add NullLogger<HealthService> + NullLogger<QueueDispatcher> at manual instantiation sites in StreamingTests/LoggingTests/HealthFallbackTests when those ctor params are added"
- 13-02: ILogger<T> ctor injection chosen (not ILoggerFactory injection) — DI auto-resolves ILogger<T> from AddLogging(); SourceContext auto-populated from T; no extra factory boilerplate
- 13-02: Option A (logger param) for module functions, not Option B — F# modules lack constructors; param threading is explicit + testable; no hidden global state
- 13-02: ILoggerFactory.CreateLogger("ChatCompletions") in mapEndpoints — handler is a module function; category name is explicit; ctx.RequestServices available at per-request resolution
- 13-02: NullLogger.Instance for bootstrap calls in configureRequestPipeline — DI container not yet built at configure time; startup-window Serilog static sink still captures fatal exceptions
- 13-02: CapturingLogger<T> MEL type replaces Serilog CapturingSink for RETRAIN-05 — ILogger<T> injection means messages no longer flow through Serilog static logger; MEL capture is the correct interception point
- 13-02: Log.Verbose maps to logger.LogTrace (ILogger equivalent of Serilog Verbose level) — confirmed for CanaryWatchdog migration
- 13-04: applyLogLevelFromArgs is module-level private helper called in BOTH --retrain and main Kestrel branches after Logging.configure; setLevel only meaningful after levelSwitch initialized
- 13-04: "trace" alias in parseLogLevel maps to Verbose (operator convenience); distinct from --trace flag which fails with migration error
- 13-05: [<CLIMutable>] alone is insufficient for Configure<T>(Action<T>) mutation pattern in F# — CLIMutable only helps reflection-based JSON binders; F# compiler enforces field immutability in source code. All 7 LogRetentionOptions fields declared mutable explicitly.
- 13-05: Plan sample used QueueDispatcher.GetStats() (nonexistent) — actual API is IStatsProvider.GetSnapshot() returning StatsSnapshot with QueueDepth122BHigh/QueueDepth122BLow field names.
- 13-05: LogRetentionService registered via AddHostedService<LogRetentionService>() (not triple-reg) — no IInterface consumer; DI needs only the hosted service leg. Options wired via Configure<LogRetentionOptions> action reading Logging + DecisionLog sections.
- 14-01: DRY bootstrap preamble — Logging.configure + applyLogLevelFromArgs hoisted before --retrain/Kestrel split using a minimal ConfigurationBuilder(appsettings.json, optional=true). Removed duplicate calls from both branches. --cold-start checked BEFORE --retrain (flags combinable). ColdStart.fs is BCL-only (no Serilog/HTTP deps; ILogger parameter supplied by caller).
- 14-01: XML comment with -- inside <!-- --> is invalid XML in .fsproj — auto-fixed to 'cold-start' without leading dashes.

### Pending Todos

- ModelsTests.fs migration to configureWithoutMl (MODELS-01/02/03 currently erroring with IEmbedder)
- Remove configureServices backwards-compat alias after ModelsTests migration

### Blockers/Concerns

- NuGet package versions all resolved at pinned versions — no concerns remaining.
- Graphify task field string literals ("graph_indexing", etc.) must be confirmed against actual Graphify client when it is built.
- Scenario B (live upstream HTTP 200 passthrough) was not verified during Phase 1 execution because Qwen 35B was not running. User explicitly approved on automated evidence (502-on-down was already proven; full passthrough will be exercised during Phase 6 deploy + first Hermes/Graphify smoke).
- ModelsTests.fs (MODELS-01/02/03): erroring with IEmbedder. Needs migration to configureWithoutMl — small mechanical fix, same pattern as HealthFallbackTests option-b. Flagged for Phase 12 verifier.

## Session Continuity

Last session: 2026-05-10
Stopped at: Completed 14-01-COLD-START-CLI-PLAN.md. ColdStart.fs (BCL-only runColdStartBackup, 4 candidate files, idempotent) + Program.fs --cold-start wiring (DRY bootstrap preamble). Build clean. 80+16+0 test baseline. Key commits: 428b30b, fe12164.
Resume file: None
