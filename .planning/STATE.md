# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-08)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Current focus:** Phase 4 — ML Algorithm Seam (placeholder + dispatch + CLI override)

## Current Position

Phase: 3 of 11 (122B Concurrency Gate) — COMPLETE ✓
Plan: 3 of 3 in current phase — COMPLETE ✓
Status: Phase 3 verified Complete (39/39 tests + 2 opt-in). Roadmap reorganized 2026-05-08: NEW Phases 4-9 ship the ML arc (handoff doc folded forward); old Phase 4/6 deferred to Phases 10-11; old Phase 5 dissolved.
Last activity: 2026-05-08 — Roadmap reorganization (ROADMAP/REQUIREMENTS/PROJECT updated; 26 new REQ-IDs added)
Next: Phase 4 — ML Algorithm Seam (placeholder + config dispatch + CLI override)

Progress: [████░░░░░░░░░░░░░░░░░░░] 8 of ~30 plans (3 done; ~6 ML phases × ~3 plans + 2 deferred phases × ~3 plans = ~24 remaining)

## Performance Metrics

**Velocity:**
- Total plans completed: 8 (3 foundation + 2 streaming + 3 concurrency-gate)
- Average duration: ~7 min
- Total execution time: ~21 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 3/3 | ~21 min | 7 min |
| 02-sse-streaming-pass-through | 2/2 | ~24 min | 12 min |
| 03-122b-concurrency-gate | 3/3 | ~53 min | 18 min |

**Recent Trend:**
- Last 5 plans: 02-02 (6 min), 03-01 (15 min), 03-02 (~35 min), 03-03 (~3 min)
- Trend: 03-03 was fast (single task, plan code nearly complete; 1 bug auto-fix)

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- Roadmap: SSE streaming (Phase 2) and concurrency gate (Phase 3) are atomic units — must not be split
- Roadmap (2026-05-08 reorganization): NEW Phases 4-9 ship the ML arc (handoff doc folded forward); old Phase 4 (Health/Fallback/graph_indexing-no-fallback) deferred to Phase 10; old Phase 6 (launchd/README) deferred to Phase 11; old Phase 5 (Observability+Tests) dissolved — OBS-01/03 absorbed into NEW Phase 5 (Decision Logging — Loop B's input), TEST-01/02 retroactively Complete via Phases 1-3 tests. Heuristic stays forever as baseline + emergency fallback.
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

### Pending Todos

None.

### Blockers/Concerns

- NuGet package versions all resolved at pinned versions — no concerns remaining.
- Graphify task field string literals ("graph_indexing", etc.) must be confirmed against actual Graphify client when it is built.
- Scenario B (live upstream HTTP 200 passthrough) was not verified during Phase 1 execution because Qwen 35B was not running. User explicitly approved on automated evidence (502-on-down was already proven; full passthrough will be exercised during Phase 6 deploy + first Hermes/Graphify smoke).

## Session Continuity

Last session: 2026-05-08T01:52:40Z
Stopped at: Completed 03-03-LOAD-TESTS-PLAN.md — Phase 3 complete; 39/39 pass, 2 ignored (load tests pending); TEST-06 satisfied
Resume file: None
