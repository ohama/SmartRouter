---
phase: 09-canary-deployment
plan: 01
subsystem: ml-routing
tags: [fsharp, canary, feature-management, core-domain, nuget, ml-routing]

# Dependency graph
requires:
  - phase: 08-retraining-loop
    provides: IModelVersionProvider singleton (CurrentVersion + Update); ChatCompletions per-request reads; RetrainingService.runRetrain calls Update after successful retrain
provides:
  - RouterRequest.CorrelationId: string field — "" sentinel for non-HTTP; ChatCompletions threads HttpContext correlation_id into Core for canary gate
  - RoutingDecision.ModelVersion: string field — "" for non-ML stages; "ml-{sha}" for ML baseline; "ml-{sha}-canary" for canary cohort
  - ICanaryGate BCL-only Core port — seam for FeatureManagementCanaryGate (Plan 09-02)
  - IModelVersionProvider extended to 4 members (CurrentVersion, CanaryVersion, Update, UpdateCanary)
  - ML.fs makeApplyML 6-param factory with single-isCanary-boolean guard (RESEARCH §11 Pitfall 8)
  - appsettings.json Canary section (7 keys) + feature_management section
  - Microsoft.FeatureManagement.AspNetCore 4.5.0 NuGet pin on Cli only
affects:
  - 09-02 (FeatureManagementCanaryGate + dual-classifier dispatch + ICanaryState + CanaryWatchdog + /canary endpoint)
  - 09-03 (CanaryTests.fs + CANARY-01/02/03 tests)

# Tech tracking
tech-stack:
  added:
    - Microsoft.FeatureManagement.AspNetCore 4.5.0 (Cli only; Core BCL-only ARCH-01 preserved)
  patterns:
    - NullCanaryGate object expression as placeholder in CompositionRoot "ml" branch (Plan 09-02 replaces with FeatureManagementCanaryGate)
    - Single-isCanary-boolean pattern in makeApplyML — SAME boolean gates classifier selection AND ModelVersion assignment (RESEARCH §11 Pitfall 8 prevention)
    - buildDecisionLog cascade: decision.ModelVersion → versionProvider.CurrentVersion for empty-string sentinel (non-ML stages)
    - CanaryPorts.fs compile position: before ML.fs in Core.fsproj (deviation from plan's "after RetrainingPorts.fs" — ML.fs must open it; see Deviations)

key-files:
  created:
    - src/SmartRouter.Core/CanaryPorts.fs
  modified:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Core/Routing.fs
    - src/SmartRouter.Core/Heuristic.fs
    - src/SmartRouter.Core/ML.fs
    - src/SmartRouter.Core/RetrainingPorts.fs
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
    - src/SmartRouter.Cli/Adapters/ModelVersionProvider.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/appsettings.json
    - tests/SmartRouter.Tests/MLRoutingTests.fs
    - tests/SmartRouter.Tests/RoutingTests.fs
    - tests/SmartRouter.Tests/QueueTests.fs
    - tests/SmartRouter.Tests/LoadTests.fs

key-decisions:
  - "CanaryPorts.fs placed BEFORE ML.fs in Core.fsproj (not 'after RetrainingPorts.fs' as plan stated) — ML.fs opens CanaryPorts, so it must compile first; ARCH-01 preserved (BCL-only)"
  - "makeApplyML now takes 6 parameters; existing MLRoutingTests test call sites updated with NullCanaryGate + 'baseline-v1' + '' placeholders so 73-test baseline holds"
  - "NullCanaryGate in CompositionRoot is an inline object expression (not a new named type in Plan 09-01) — Plan 09-02 replaces it with FeatureManagementCanaryGate registration"
  - "buildDecisionLog gains decisionOpt: RoutingDecision option param — None for pre-routing failures, Some decision for Ok-routing branches; ModelVersion cascade uses empty-string sentinel"

patterns-established:
  - "Phase 9 empty-string sentinel: RoutingDecision.ModelVersion = '' means non-ML stage; ChatCompletions.buildDecisionLog falls back to versionProvider.CurrentVersion"
  - "Phase 9 canary boolean: in makeApplyML, the SAME isCanary bool selects (classifier, modelVersion) in one adjacent let-binding — prevents cohort label leakage (RESEARCH §11 Pitfall 8)"

# Metrics
duration: 8min
completed: 2026-05-09
---

# Phase 9 Plan 01: Foundation Summary

**Domain field additions (CorrelationId + ModelVersion) + ICanaryGate BCL-only port + IModelVersionProvider extension + NuGet pin + appsettings Canary sections; 19 construction sites updated; 73/73 test baseline preserved**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-05-08T22:55:05Z
- **Completed:** 2026-05-08T23:02:53Z
- **Tasks:** 2
- **Files modified:** 15 (5 Core + 6 Cli + 4 tests)

## Accomplishments

- RouterRequest gains `CorrelationId: string` and RoutingDecision gains `ModelVersion: string` — both required fields with "" sentinel for non-load-bearing construction sites
- All 19 enumerated construction sites updated: 9 RoutingDecision (Routing.fs×9, Heuristic.fs×1, ML.fs×1 non-empty) + 6 RouterRequest (ChatCompletions×2, MLRoutingTests, RoutingTests, QueueTests, LoadTests) = 15 source sites + 4 mkDecision test helpers
- ICanaryGate BCL-only port (CanaryPorts.fs) and IModelVersionProvider extended to 4 members; ML.fs makeApplyML now 6-param factory with single-isCanary-boolean guard
- Microsoft.FeatureManagement.AspNetCore 4.5.0 pinned to Cli.fsproj only; Core remains BCL-only (ARCH-01 preserved)
- Build: 0 errors, 0 warnings; Tests: 73 passed + 10 ignored + 0 failed (exact Phase 8 baseline preserved)

## Task Commits

1. **Task 1: Core domain field additions + ICanaryGate port + IModelVersionProvider extension** — `df6c7cc` (feat)
2. **Task 2: Cli + test construction sites updated for new domain fields** — `3e01be2` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/SmartRouter.Core/CanaryPorts.fs` (NEW) — ICanaryGate BCL-only port; `open System.Threading + System.Threading.Tasks` only
- `src/SmartRouter.Core/Domain.fs` — RouterRequest + CorrelationId: string; RoutingDecision + ModelVersion: string
- `src/SmartRouter.Core/Routing.fs` — 9 RoutingDecision construction sites updated with ModelVersion = ""
- `src/SmartRouter.Core/Heuristic.fs` — applyHeuristic RoutingDecision updated with ModelVersion = ""
- `src/SmartRouter.Core/ML.fs` — makeApplyML 6-param factory; single isCanary boolean; opens CanaryPorts
- `src/SmartRouter.Core/RetrainingPorts.fs` — IModelVersionProvider extended: CanaryVersion getter + UpdateCanary setter
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` — CanaryPorts.fs added before ML.fs in compile order
- `src/SmartRouter.Cli/Adapters/ModelVersionProvider.fs` — 4-member implementation with shared lock gate + mutable canary field
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — mapWireToRequest takes correlationId param; buildDecisionLog gains decisionOpt; cascade pattern
- `src/SmartRouter.Cli/CompositionRoot.fs` — makeApplyML factory updated to 6-arg with NullCanaryGate object expression
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Microsoft.FeatureManagement.AspNetCore 4.5.0 added
- `src/SmartRouter.Cli/appsettings.json` — Canary section (7 keys) + feature_management section
- `tests/SmartRouter.Tests/MLRoutingTests.fs` — CorrelationId = "" in mkReq; makeApplyML calls updated to 6-arg with NullCanaryGate
- `tests/SmartRouter.Tests/RoutingTests.fs` — CorrelationId = "" in mkReq
- `tests/SmartRouter.Tests/QueueTests.fs` — CorrelationId = "" in emptyRequest; ModelVersion = "" in mkDecision
- `tests/SmartRouter.Tests/LoadTests.fs` — CorrelationId = "" in emptyRequest; ModelVersion = "" in mkDecision

## Decisions Made

- **CanaryPorts.fs compile order**: Plan specified "after RetrainingPorts.fs" but ML.fs opens CanaryPorts — moved CanaryPorts.fs before ML.fs in Core.fsproj to satisfy F# compile-order requirement. ARCH-01 preserved (CanaryPorts.fs is BCL-only).
- **MLRoutingTests makeApplyML call sites**: 3 direct call sites in tests needed updating to 6-arg form. Added NullCanaryGate object expression inline per test + "baseline-v1" + "" placeholders. Consistent with the plan's inline-object-expression preference.
- **buildDecisionLog signature**: Added `decisionOpt: RoutingDecision option` parameter as 6th arg (between `started` and `target`). Pre-routing failures pass `None`; Ok-decision branches pass `Some decision`. 8 total call sites updated.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 — Blocking] CanaryPorts.fs compile position moved before ML.fs**

- **Found during:** Task 1 (Core build verification after all Task 1 edits)
- **Issue:** Plan specified placing CanaryPorts.fs "after RetrainingPorts.fs" but ML.fs opens `SmartRouter.Core.CanaryPorts`. F# compile order is strict: CanaryPorts.fs must precede ML.fs. Build failed with FS0039 ("CanaryPorts namespace not defined").
- **Fix:** Moved `<Compile Include="CanaryPorts.fs" />` in Core.fsproj to be between MLPorts.fs and ML.fs instead of after RetrainingPorts.fs.
- **Files modified:** src/SmartRouter.Core/SmartRouter.Core.fsproj
- **Verification:** `dotnet build src/SmartRouter.Core` → 0 errors, 0 warnings; ARCH-01 grep still passes.
- **Committed in:** df6c7cc (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 3 — blocking compile-order error)
**Impact on plan:** Necessary to unblock build. No scope change; ARCH-01 preserved.

## Issues Encountered

None beyond the compile-order deviation above.

## Build + Test Status

- `dotnet build SmartRouter.slnx -nologo --tl:off` → **0 errors, 0 warnings**
- `dotnet run --project tests/SmartRouter.Tests -- --sequenced` → **73 passed, 10 ignored, 0 failed** (exact Phase 8 baseline)
- ARCH-01 grep (`grep -rn "Microsoft\.FeatureManagement..." src/SmartRouter.Core/ --include="*.fs"`) → **0 matches in code lines** (2 comment-only lines in CanaryPorts.fs; excluded by `grep -v "//"`)
- NuGet pin: `Microsoft.FeatureManagement.AspNetCore 4.5.0` resolves cleanly; `dotnet list ... package` confirms 4.5.0 resolved

## Next Phase Readiness

**Plan 09-02 has everything it needs:**
- ICanaryGate port (CanaryPorts.fs) — plug in FeatureManagementCanaryGate
- IModelVersionProvider extended with CanaryVersion + UpdateCanary — plug in CanaryService file-watcher calls
- NullCanaryGate object expression in CompositionRoot "ml" branch — replace with `FeatureManagementCanaryGate` DI registration
- appsettings.json Canary section + feature_management section — CompositionRoot Plan 09-02 reads these
- Microsoft.FeatureManagement.AspNetCore 4.5.0 already added to Cli.fsproj — Plan 09-02 can call `AddScopedFeatureManagement` immediately

**Concerns for Plan 09-02:**
- Lock 17 pitfall list must be enforced: `AddScopedFeatureManagement` (NOT `AddFeatureManagement`); `PercentageFilter` is FORBIDDEN; `IVariantFeatureManager` not `IFeatureManager`; `IRetrainLock` race for /canary/promote; try/with semicolon trap in CanaryWatchdog; ExceptionDispatchInfo.Capture for OCE through task{}

---
*Phase: 09-canary-deployment*
*Completed: 2026-05-09*
