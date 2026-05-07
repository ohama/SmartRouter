# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-07)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Current focus:** Phase 2 — SSE Streaming Pass-Through (next; per ROADMAP execution order)

## Current Position

Phase: 1 of 6 (Foundation) — COMPLETE ✓
Plan: 3 of 3 in current phase
Status: Phase 1 verified; user approved on automated evidence (live-upstream Scenario B deferred to Phase 6 deploy)
Last activity: 2026-05-07 — Phase 1 complete; verifier 9/10 automated checks passed; human-verification item (live upstream Scenario B) approved without re-test
Next: /gsd:plan-phase 2

Progress: [███░░░░░░░] ~18% (3 of ~17 plans estimated)

## Performance Metrics

**Velocity:**
- Total plans completed: 3
- Average duration: ~7 min
- Total execution time: ~21 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 3/3 | ~21 min | 7 min |

**Recent Trend:**
- Last 5 plans: 01-01 (3 min), 01-02 (5 min), 01-03 (13 min)
- Trend: Increasing with complexity (infrastructure wiring > domain logic > scaffolding)

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

### Pending Todos

None.

### Blockers/Concerns

- NuGet package versions all resolved at pinned versions — no concerns remaining.
- Graphify task field string literals ("graph_indexing", etc.) must be confirmed against actual Graphify client when it is built.
- Scenario B (live upstream HTTP 200 passthrough) was not verified during Phase 1 execution because Qwen 35B was not running. User explicitly approved on automated evidence (502-on-down was already proven; full passthrough will be exercised during Phase 6 deploy + first Hermes/Graphify smoke).

## Session Continuity

Last session: 2026-05-07T06:58:00Z
Stopped at: Completed 01-03-UPSTREAM-WIRING-PLAN.md — Phase 1 Foundation complete
Resume file: None
