# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-08)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Current focus:** Phase 9 — Canary Routing (next: canary cohort tagging, A/B model comparison, rollback via router.zip.prev; depends on Phase 8 IModelVersionProvider singleton + models/router.zip.prev)

## Current Position

Phase: 8 of 11 (Retraining Loop) — COMPLETE ✓
Plan: 3 of 3 in current phase — COMPLETE ✓
Status: Phase 8 complete. 7 Expecto tests covering RETRAIN-01..06 added. FakeEmbedder (Task.Yield for concurrent semaphore test), ThrowingEmbedder, CapturingSink helpers. test7 runs as testCase (StartAsync + PeriodicTimer count-check integration, ~60-90s). Build: 0 errors, 0 warnings. Tests: 73 pass + 10 ignored, 0 failed (was 66 pass baseline). Canonical run: dotnet run -- --sequenced.
Last activity: 2026-05-09 — Phase 8 Plan 3 COMPLETE

Progress: [██████████████████░░░░░] 26 of ~30 plans (phase 8 complete; phase 9 next)

## Performance Metrics

**Velocity:**
- Total plans completed: 26 (3 foundation + 2 streaming + 3 concurrency-gate + 3 ml-seam + 3 decision-logging + 3 real-ml-routing + 6 failure-detection + 3 retraining-loop)
- Average duration: ~7 min
- Total execution time: ~132 min

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

**Recent Trend:**
- Last 5 plans: 07-05 (~8 min), 07-06 (~10 min), 08-01 (~8 min), 08-02 (~7 min), 08-03 (~17 min)
- Trend: Tests-only plans with ML.NET training take longer (~17 min); core implementation plans ~7-10 min

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

### Pending Todos

None.

### Blockers/Concerns

- NuGet package versions all resolved at pinned versions — no concerns remaining.
- Graphify task field string literals ("graph_indexing", etc.) must be confirmed against actual Graphify client when it is built.
- Scenario B (live upstream HTTP 200 passthrough) was not verified during Phase 1 execution because Qwen 35B was not running. User explicitly approved on automated evidence (502-on-down was already proven; full passthrough will be exercised during Phase 6 deploy + first Hermes/Graphify smoke).

## Session Continuity

Last session: 2026-05-09T05:57:29Z
Stopped at: Phase 8 Plan 3 COMPLETE — 08-03 (RetrainingTests.fs: 7 tests covering RETRAIN-01..06); build clean; 73 pass + 10 ignored, 0 failed (--sequenced canonical run)
Resume file: None
