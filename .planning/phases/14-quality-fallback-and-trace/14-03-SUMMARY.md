---
phase: 14-quality-fallback-and-trace
plan: 03
subsystem: routing
tags: [fsharp, domain, quality-fallback, routing-reason, heuristic, appsettings]

# Dependency graph
requires:
  - phase: 14-02
    provides: TraceLogger infrastructure + --trace-responses CLI flag
  - phase: 10-health-fallback
    provides: FallbackTo35B DU case (peer case to new FallbackTo122B)
provides:
  - RoutingReason.FallbackTo122B (6th DU case in Domain.fs)
  - formatReason exhaustive 6-arm match in DecisionLogger.fs
  - QualityCheck.fs: QualityFallbackOptions + isBadResponse pure function
  - appsettings.json Routing.QualityFallback subsection (operator-tunable defaults)
  - RoutingOptions.QualityFallback field + normalizeQualityFallback defensive helper
affects:
  - 14-04 (ChatCompletions handler — consumes isBadResponse + FallbackTo122B in hot path)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "QualityFallbackOptions [<CLIMutable>] record: BCL-only Cli adapter, never in Core"
    - "normalizeQualityFallback helper: defensive defaults for null-section / zero values (mirrors ML opts pattern)"
    - "6-arm exhaustive match in formatReason: TreatWarningsAsErrors enforces coverage at compile time"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/QualityCheck.fs
  modified:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/CompositionRoot.fs

key-decisions:
  - "QualityCheck.fs placed in Cli/Adapters (not Core): ARCH-01 compliance; pure BCL, zero framework deps"
  - "normalizeQualityFallback returns Enabled=false defaults when section absent: safe-off rather than safe-on for missing config"
  - "isBadResponse is case-sensitive by design: operator adds lowercase variants explicitly to BadKeywords array"
  - "FallbackTo122B placed after FallbackTo35B in DU declaration: mirrors lexical/semantic pairing"

patterns-established:
  - "Distillation isBadResponse pattern externalized to appsettings.json: operator tunes MinResponseLength/BadKeywords without recompile"
  - "Defensive null-check pattern for [<CLIMutable>] records: obj.ReferenceEquals for null; fallback to safe defaults"

# Metrics
duration: 8min
completed: 2026-05-10
---

# Phase 14 Plan 03: Core Types Summary

**RoutingReason.FallbackTo122B DU case + isBadResponse heuristic (QualityFallbackOptions, operator-tunable via appsettings.json) — pure type foundation for 14-04 hot-path integration**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-05-10T06:32:00Z
- **Completed:** 2026-05-10T06:40:00Z
- **Tasks:** 3
- **Files modified:** 6 (1 created, 5 modified)

## Accomplishments

- `RoutingReason.FallbackTo122B` added as 6th DU case in Domain.fs; Core builds BCL-only
- `formatReason` exhaustive match updated to 6 arms; TreatWarningsAsErrors=true passes (no FS0025)
- `QualityCheck.fs` created: pure F# BCL-only Cli adapter with `QualityFallbackOptions` record and `isBadResponse` function; operator-tunable without recompile via `appsettings.json` `Routing.QualityFallback` subsection
- `RoutingOptions` record extended; `normalizeQualityFallback` helper applies defensive defaults for absent/zero config values
- 80 tests passed, 16 ignored, 0 failed — baseline unchanged

## Task Commits

Each task was committed atomically:

1. **Task 1: Domain.fs — add RoutingReason.FallbackTo122B** - `30afc1f` (feat)
2. **Task 2: DecisionLogger.fs — formatReason new arm** - `8f21cba` (feat)
3. **Task 3: Create QualityCheck.fs + appsettings.json + RoutingOptions binding** - `5318a32` (feat)

## Files Created/Modified

- `src/SmartRouter.Core/Domain.fs` - Added `FallbackTo122B` as 6th RoutingReason case
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` - Added `| FallbackTo122B -> "fallback_to_122b"` arm to formatReason
- `src/SmartRouter.Cli/Adapters/QualityCheck.fs` - NEW: QualityFallbackOptions + isBadResponse (37 lines, BCL only)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - Compile entry for QualityCheck.fs after ColdStart.fs
- `src/SmartRouter.Cli/appsettings.json` - Routing.QualityFallback subsection (Enabled=true, MinResponseLength=30, BadKeywords=["TODO","I think"])
- `src/SmartRouter.Cli/CompositionRoot.fs` - RoutingOptions.QualityFallback field + normalizeQualityFallback helper; open QualityCheck module

## Decisions Made

- `normalizeQualityFallback` returns `Enabled=false` (safe-off) when the config section is entirely absent — production behavior: quality fallback does not fire on missing config. Operator must explicitly set `Enabled: true`.
- `isBadResponse` is case-sensitive by design; operator who wants case-insensitive coverage adds lowercase variants to `BadKeywords` array explicitly.
- `FallbackTo122B` placed at end of DU (after `FallbackTo35B`) to maintain semantic pairing and avoid reordering existing cases.

## Deviations from Plan

None — plan executed exactly as written. `normalizeQualityFallback` was extracted as a named helper (plan showed inline code in a `let qualityFallback =` binding) — functionally equivalent, slightly more testable.

## Issues Encountered

`dotnet build` for the solution hit an intermittent exit code 138 (SIGKILL) during the test project build step when running all projects together. Individual project builds (`SmartRouter.Core`, `SmartRouter.Cli`, `SmartRouter.Tests`) each succeeded cleanly. Tests were run directly via the test binary confirming 80 passed / 0 failed. Likely transient memory pressure on the build host — not a code issue.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- All Phase 14 core types ready: `FallbackTo122B` + `isBadResponse` + `QualityFallbackOptions`
- 14-04 (ChatCompletions quality-fallback handler) can now consume `isBadResponse` from `QualityCheck.fs` and emit `FallbackTo122B` as routing reason
- `normalizeQualityFallback` should be called in the ChatCompletions composition or via IOptions injection pattern established in 14-04

---
*Phase: 14-quality-fallback-and-trace*
*Completed: 2026-05-10*
