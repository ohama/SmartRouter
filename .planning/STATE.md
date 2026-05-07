# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-07)

**Core value:** Route every request to the model best suited to it — fast 35B for simple work, expensive 122B only when the task or signals justify it — while protecting 122B from concurrent overload.
**Current focus:** Phase 1 — Foundation

## Current Position

Phase: 1 of 6 (Foundation)
Plan: 2 of 3 in current phase
Status: In progress
Last activity: 2026-05-07 — Completed 01-02-CORE-DOMAIN-PLAN.md (Domain.fs, Routing.fs, Ports.fs, Json.fs, Logging.fs, RoutingTests.fs 22 tests)

Progress: [██░░░░░░░░] ~12% (2 of ~17 plans estimated)

## Performance Metrics

**Velocity:**
- Total plans completed: 2
- Average duration: 4 min
- Total execution time: ~8 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 2/3 | 8 min | 4 min |

**Recent Trend:**
- Last 5 plans: 01-01 (3 min), 01-02 (5 min)
- Trend: —

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- Roadmap: SSE streaming (Phase 2) and concurrency gate (Phase 3) are atomic units — must not be split
- Roadmap: graph_indexing no-fallback rule ships in same phase as health probing (Phase 4)
- Roadmap: Phase 3 depends on Phase 1 only (not Phase 2); Phases 2 and 3 have no cross-dependency
- 01-01: `dotnet new slnx` unavailable in SDK 10.0.203 — SmartRouter.slnx written manually in XML (no functional difference)
- 01-01: Cli stub Program.fs required to satisfy F# FS0988 (empty main module); replaced in plan 01-03
- 01-01: launchSettings.json has no applicationUrl — appsettings.json is single source of truth for Kestrel binding (OPS-04)
- 01-02: RoutingConfig is a plain F# record in Core (no IOptions<T>); Cli constructs it from appsettings.json at composition time (ARCH-01)
- 01-02: taskToDecision is utility-only (compile-time DU completeness anchor); routeRequest/tryTaskTable read config.TaskTable Map at runtime (ROUT-05 locked decision)
- 01-02: MessageRole.System DU case shadows System namespace — test code must use String.replicate not System.String.replicate

### Pending Todos

None.

### Blockers/Concerns

- NuGet package versions all resolved at pinned versions — no concerns remaining from earlier MEDIUM-confidence list.
- Graphify task field string literals ("graph_indexing", etc.) must be confirmed against actual Graphify client when it is built.

## Session Continuity

Last session: 2026-05-07T06:42:15Z
Stopped at: Completed 01-02-CORE-DOMAIN-PLAN.md
Resume file: None
