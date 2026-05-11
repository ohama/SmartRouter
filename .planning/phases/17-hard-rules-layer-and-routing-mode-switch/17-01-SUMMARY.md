---
phase: 17-hard-rules-layer-and-routing-mode-switch
plan: "01"
subsystem: routing-core
tags: [fsharp, hard-rules, routing, cascade, bcl-only, keyword-matching]

# Dependency graph
requires: []
provides:
  - "HardRules.fs: pure BCL-only applyHardRules function in SmartRouter.Core"
  - "RoutingReason.HardRule: 7th DU case (compile-time exhaustive-match enforcement)"
  - "Stage 0 Hard Rules in routeRequest: fires BEFORE model override and task table"
  - "DecisionLogger 7th arm: HardRule -> \"hard_rule\" (schema_version=1 additive)"
  - "16 unit tests: keyword coverage + case-insensitivity + cascade ordering verified"
affects:
  - "18-01 (sticky escalation Stage 3 depends on cascade order locked here)"
  - "19-01 (self-classify Stage 4 depends on cascade order locked here)"
  - "20-01 (DecisionLog schema_version=1 additive enum verified here)"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Stage 0 pre-routing: pure BCL function before all config-driven stages"
    - "Safety keyword list hardcoded in Core (not config) — HR-02 pattern"
    - "String.Contains with OrdinalIgnoreCase for ASCII keyword matching (mirrors Phase 15)"
    - "Additive RoutingReason DU case forces exhaustive match update at all match sites"

key-files:
  created:
    - src/SmartRouter.Core/HardRules.fs
    - tests/SmartRouter.Tests/HardRulesTests.fs
  modified:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Core/Routing.fs
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
    - src/SmartRouter.Cli/Adapters/DecisionLogger.fs
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "Hard Rules wins over model override + task table: cascade Stage 0 > Stage 1 > Stage 2 (STATE.md decision 5 locked in code)"
  - "Keyword list hardcoded in HardRules.fs, not config: safety mechanism must not be misconfigurable (HR-02)"
  - "HardRules.fs in SmartRouter.Core, not Cli: pure BCL function belongs in Core (ARCH-01)"
  - "No payload on HardRule DU case: target/priority are fixed constants (Qwen122B/High)"
  - "ModelVersion = \"\" on hard-rule decisions: consistent with non-ML stages convention"

patterns-established:
  - "Stage 0 pre-routing pattern: pure BCL function inserted before all config-driven stages"
  - "Hardcoded safety list pattern: keywords in Core module, not appsettings"
  - "Additive DU enum pattern: TreatWarningsAsErrors forces all match sites updated atomically"

# Metrics
duration: 8min
completed: 2026-05-11
---

# Phase 17 Plan 01: Hard Rules Layer (Core Module + Cascade Stage 0) Summary

**BCL-only keyword pre-filter (LLVM/MLIR/compiler/segfault/optimization/concurrency) inserted as Stage 0 in routeRequest, guaranteeing 122B routing regardless of model override, with 16 new unit tests verifying all 6 keywords + cascade dominance**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-05-11T05:04:01Z
- **Completed:** 2026-05-11T05:11:31Z
- **Tasks:** 3
- **Files modified:** 6 modified + 2 created

## Accomplishments

- Created `HardRules.fs` in SmartRouter.Core: pure synchronous function, BCL-only (`System.StringComparison.OrdinalIgnoreCase`), no IO, no DI, no config — safety keyword list hardcoded per HR-02
- Extended `RoutingReason` DU to 7 cases (`HardRule` as 7th); `TreatWarningsAsErrors=true` forced `DecisionLogger.formatReason` to add 7th arm (`"hard_rule"`) in the same atomic commit — compile-time invariant preserved
- Inserted Stage 0 in `routeRequest` BEFORE `tryModelOverride` (Stage 1); cascade order now: Hard Rules → model override → task table → algorithm; this ordering is the foundation Phases 18 and 19 depend on
- 16 new `HardRulesTests` covering all 6 keywords individually, 3 case-insensitivity variants, no-match passthrough, multi-message concatenation, cascade ordering (Stage 0 > Stage 1, Stage 0 > Stage 2), and `formatReason` arm
- Full test suite: 129 passed (113 baseline + 16 new), 16 ignored, 0 failed — zero regressions

## Task Commits

1. **Task 1: Add HardRule DU case + HardRules.fs + Core.fsproj compile order** - `1a45a83` (feat)
2. **Task 2: Wire Stage 0 in routeRequest + DecisionLogger 7th arm** - `8139e9f` (feat)
3. **Task 3: HardRulesTests.fs + fsproj + rootTests** - `82889d9` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/SmartRouter.Core/HardRules.fs` (NEW) - Pure BCL `applyHardRules : RouterRequest -> RoutingDecision option`; 6 hardcoded keywords; case-insensitive OrdinalIgnoreCase; returns Some Qwen122B+High+HardRule on match
- `src/SmartRouter.Core/Domain.fs` - `RoutingReason` DU extended with `| HardRule` (7th case, Phase 17 comment)
- `src/SmartRouter.Core/Routing.fs` - `routeRequest` now 4-stage; Stage 0 calls `HardRules.applyHardRules` before `tryModelOverride`; docstring updated to "four-stage"
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` - `HardRules.fs` inserted between `RetrainingPorts.fs` and `ML.fs` (F# compile order requirement)
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` - `formatReason` 7th arm: `| HardRule -> "hard_rule"`; schema_version=1 unchanged (additive enum value)
- `tests/SmartRouter.Tests/HardRulesTests.fs` (NEW) - 16 unit tests; pure tests (no Console.SetOut, no testSequenced needed)
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` - `HardRulesTests.fs` added before `RouterTests.fs` (PITFALL-26)
- `tests/SmartRouter.Tests/RouterTests.fs` - `HardRulesTests.tests` appended to `rootTests`

## Decisions Made

- **Hard Rules wins over model override**: STATE.md decision 5 locked in code. A request with `model=35b` + LLVM in prompt → routes to 122B. HR-06's "explicit override bypasses Hard Rules" wording is incorrect; wording fix lands in Plan 17-03.
- **No DU payload on HardRule**: Target (Qwen122B) and Priority (High) are invariant for any keyword match. Payload would add noise without value.
- **`ModelVersion = ""`**: Consistent with existing non-ML stage convention (ChatCompletions falls back to `IModelVersionProvider.CurrentVersion`).
- **No `open` statement needed in Routing.fs**: `HardRules` resolves via the `SmartRouter.Core.HardRules` module declaration; `Routing.fs` is in the same namespace scope.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. Research greps (17-RESEARCH.md) correctly predicted all line numbers and symbols. `defaultRoutingConfig` confirmed at line 129 of Routing.fs as expected by the W3 pre-check. No existing test prompts contain any of the 6 Hard Rules keywords — baseline 113 tests preserved without modification.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Cascade ordering is locked in code: Stage 0 Hard Rules → Stage 1 model override → Stage 2 task table → Stage 3 algorithm. Phases 18 (sticky, Stage 3) and 19 (self-classify, Stage 4) can safely insert their stages after Stage 2 without conflict.
- `RoutingReason` DU has 7 cases; Phase 18 will add `StickyTo122B` (8th case) following the same additive pattern established here.
- `HardRules.fs` pattern established: Core BCL-only pure function. Phase 19's `SelfRouter.fs` will follow the same Core-module pattern (though it requires HTTP IO so it goes in Cli adapters per ARCH-01).
- No blockers. Test count delta: +16 (total 129 passing, 16 ignored).

---
*Phase: 17-hard-rules-layer-and-routing-mode-switch*
*Completed: 2026-05-11*
