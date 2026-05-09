---
phase: 12
plan: "01"
subsystem: core-routing
tags: [heuristic-removal, domain, routing-config, fsproj, decision-logger]
requires: [11-01, 11-02, 11-03]
provides: [heuristic-free-core]
affects: [12-02, 12-03, 12-04, 12-05, 12-06]
tech-stack:
  added: []
  patterns: [atomic-deletion, TreatWarningsAsErrors-exhaustive-match]
key-files:
  created: []
  modified:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Core/Routing.fs
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
  deleted:
    - src/SmartRouter.Core/Heuristic.fs
decisions:
  - "RoutingReason.Heuristic DU case permanently deleted (not soft-paused); 5-case DU is now the shape"
  - "RoutingConfig reduced from 4 fields to 2 (TaskTable + MlThreshold)"
  - "canonicalKeywords deleted entirely (no consumer after Keywords field removal)"
  - "DecisionLogger.fs formatReason exhaustive match verified clean under TreatWarningsAsErrors=true"
metrics:
  duration: "~8 min"
  completed: "2026-05-09"
---

# Phase 12 Plan 01: Core Deletion Summary

**One-liner:** Heuristic.fs deleted, RoutingReason.Heuristic DU case and Keywords/ComplexityThreshold RoutingConfig fields removed; Core builds clean with TreatWarningsAsErrors=true; Cli broken as expected (12-02 finishes wiring).

## Tasks Done

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Delete Heuristic.fs + remove from Core.fsproj | f710667 | Heuristic.fs (deleted), SmartRouter.Core.fsproj |
| 2 | Domain.fs — remove RoutingReason.Heuristic + RoutingConfig.Keywords/ComplexityThreshold | a6b1312 | Domain.fs |
| 3 | Routing.fs — remove canonicalKeywords + rebuild defaultRoutingConfig | 20bedc4 | Routing.fs |
| 4 | DecisionLogger.fs — remove Heuristic arm from formatReason | 978b72f | DecisionLogger.fs |

## Files Modified

### Deleted
- `src/SmartRouter.Core/Heuristic.fs` — 46-line module containing `scoreComplexity` and `applyHeuristic`; gone permanently

### Modified
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` — removed `<Compile Include="Heuristic.fs" />`; compile order now: Domain.fs → MLPorts.fs → CanaryPorts.fs → ML.fs → Routing.fs → RetrainingPorts.fs → Ports.fs
- `src/SmartRouter.Core/Domain.fs` — removed `RoutingReason.Heuristic of score: int` case; removed `ComplexityThreshold : int` and `Keywords : string list` fields from `RoutingConfig`; updated doc comments
- `src/SmartRouter.Core/Routing.fs` — deleted `canonicalKeywords` let binding; rebuilt `defaultRoutingConfig` to `{ TaskTable = canonicalTaskTable; MlThreshold = 0.5f }`; updated pipeline doc comment
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — removed `| Heuristic score -> sprintf "heuristic:score=%d" score` arm from `formatReason`; cleaned comment in `computePromptHash`

## Build Status

| Project | Status | Notes |
|---------|--------|-------|
| SmartRouter.Core | GREEN | `dotnet build` — 0 warnings, 0 errors; TreatWarningsAsErrors=true satisfied |
| SmartRouter.Cli | BROKEN (expected) | CompositionRoot.fs still references ComplexityThreshold, Keywords, and Heuristic DU case — addressed by 12-02 |
| SmartRouter.Tests | UNDEFINED | Not built at this plan's boundary |

**Cli errors (expected, documented):**
- `CompositionRoot.fs(118)`: FS1129 — `RoutingConfig` has no `ComplexityThreshold` label
- `CompositionRoot.fs(119)`: FS1129 — `RoutingConfig` has no `Keywords` label
- `CompositionRoot.fs(356)`: FS0039 — `Heuristic` value/constructor undefined

## Grep Verification Results

```
# Heuristic symbol in Core: 0 hits (OK)
grep -rn "Heuristic" src/SmartRouter.Core/  → (no hits)

# applyHeuristic / scoreComplexity / canonicalKeywords in Core: 0 hits (OK)
grep -c "canonicalKeywords|ComplexityThreshold|Keywords " src/SmartRouter.Core/Routing.fs → 0
grep -c "applyHeuristic" src/SmartRouter.Core/Routing.fs → 0

# RoutingReason.Heuristic case in Domain.fs: 0 hits (OK)
grep -c "| Heuristic " src/SmartRouter.Core/Domain.fs → 0

# heuristic:score in DecisionLogger.fs: 0 hits (OK)
grep -c "heuristic:score" src/SmartRouter.Cli/Adapters/DecisionLogger.fs → 0

# Remaining Cli hits (expected — 12-02 scope):
src/SmartRouter.Cli/CompositionRoot.fs:348  (comment)
src/SmartRouter.Cli/CompositionRoot.fs:356  (heuristic dispatch arm — broken, 12-02 deletes)
src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs:11  (doc comment — 12-06 cleanup scope)
```

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Minor doc cleanup] computePromptHash comment mentioned heuristic**

- **Found during:** Task 4
- **Issue:** `DecisionLogger.fs` line 9 comment said "matches what the heuristic scores" — stale reference with the heuristic gone
- **Fix:** Trimmed to "Full conversation, not just last user message."
- **Files modified:** `src/SmartRouter.Cli/Adapters/DecisionLogger.fs`
- **Commit:** 978b72f (included in Task 4 commit)

No other deviations. Plan executed as specified.

## Next Phase Readiness

Plan 12-02 can proceed immediately. It must fix:
- `CompositionRoot.fs` lines 118-119: build `RoutingConfig` with only `TaskTable` + `MlThreshold`
- `CompositionRoot.fs` line 348-358: delete heuristic dispatch arm
- `CompositionRoot.fs` line 356: delete `SmartRouter.Core.Heuristic.applyHeuristic` reference
- All other Q1/Q3/Q4 items per 12-CONTEXT.md
