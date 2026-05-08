# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-07)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Current focus:** Phase 3 — 122B Concurrency Gate (next; Phase 2 verified Complete)

## Current Position

Phase: 3 of 6 (122B Concurrency Gate) — In progress
Plan: 2 of 3 in current phase — COMPLETE ✓
Status: Plan 03-02 complete — GET /stats endpoint live; 9 QueueTests pass (39 total); all PITFALL mitigations proven
Last activity: 2026-05-08 — Completed 03-02-STATS-AND-QUEUE-TESTS-PLAN.md
Next: Plan 03-03 — Load tests (TEST-06, deferred from 03-02)

Progress: [███████░░░] ~41% (7 of ~17 plans estimated)

## Performance Metrics

**Velocity:**
- Total plans completed: 3
- Average duration: ~7 min
- Total execution time: ~21 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 3/3 | ~21 min | 7 min |
| 02-sse-streaming-pass-through | 2/2 | ~24 min | 12 min |
| 03-122b-concurrency-gate | 2/3 | ~50 min | 25 min |

**Recent Trend:**
- Last 5 plans: 02-01 (18 min), 02-02 (6 min), 03-01 (15 min), 03-02 (~35 min)
- Trend: 03-02 was large due to 9 test cases with complex concurrency semantics and 3 test design bugs auto-fixed

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- Roadmap: SSE streaming (Phase 2) and concurrency gate (Phase 3) are atomic units — must not be split
- Roadmap: graph_indexing no-fallback rule ships in same phase as health probing (Phase 4)
- Roadmap: Phase 3 depends on Phase 1 only (not Phase 2); Phases 2 and 3 have no cross-dependency
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

### Pending Todos

None.

### Blockers/Concerns

- NuGet package versions all resolved at pinned versions — no concerns remaining.
- Graphify task field string literals ("graph_indexing", etc.) must be confirmed against actual Graphify client when it is built.
- Scenario B (live upstream HTTP 200 passthrough) was not verified during Phase 1 execution because Qwen 35B was not running. User explicitly approved on automated evidence (502-on-down was already proven; full passthrough will be exercised during Phase 6 deploy + first Hermes/Graphify smoke).

## Session Continuity

Last session: 2026-05-08T01:46:09Z
Stopped at: Completed 03-02-STATS-AND-QUEUE-TESTS-PLAN.md — GET /stats live; 39/39 tests pass; all PITFALL mitigations proven
Resume file: None
