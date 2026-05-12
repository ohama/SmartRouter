---
phase: 17-hard-rules-layer-and-routing-mode-switch
plan: "02"
subsystem: routing-composition
tags: [fsharp, routing-mode, config-switch, composition-root, ml-dormancy, selfrouting]

# Dependency graph
requires:
  - phase: "17-01"
    provides: "HardRules.fs Stage 0 in routeRequest; cascade order locked; both modes funnel through Stage 0"
provides:
  - "Routing.Mode config key in appsettings.json (default 'selfrouting')"
  - "MODE-01 fail-fast: invalid Routing.Mode value throws InvalidOperationException before Kestrel binds"
  - "MODE-02 config branch: 'ml' -> v1.x ML RoutingAlgorithmRegistration; 'selfrouting' -> Phase 17 stub"
  - "MODE-03 invariant: ML adapters (BgeM3Embedder, MlNetClassifier, RetrainingService, CanaryService) DI-registered in BOTH modes"
  - "Selfrouting stub: Name='selfrouting', ModelVersion='selfrouting-v1', Algorithm returns Qwen35B/Default (Phase 19 replaces with real SelfRouter)"
affects:
  - "17-03 (README §5 + §7 documentation of Routing.Mode key + operator flip pattern)"
  - "19-01 (Phase 19 replaces selfrouting stub closure with real makeSelfRoutingAlgorithm)"
  - "19-02 (MlDormantTests.fs verifies ML adapters stay registered in selfrouting mode per MODE-03)"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Direct config read pattern: config.[\"Routing:Mode\"] (mirrors Phase 16 Routing:Judge:Enabled idiom)"
    - "Fail-fast validation before Kestrel bind: match routingMode with valid values | other -> raise InvalidOperationException"
    - "Mode-branched factory: match routingMode inside AddSingleton<RoutingAlgorithmRegistration> Func<IServiceProvider, T>"
    - "Stub selfrouting algorithm: let binding returning constant RoutingDecision (Qwen35B/Default/Low)"
    - "MODE-03 invariant: ML DI registrations above factory (unconditional); only Algorithm closure branches on mode"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/CompositionRoot.fs

key-decisions:
  - "Direct config read (not RoutingOptions field): config.[\"Routing:Mode\"] mirrors Phase 16 Judge pattern; avoids RoutingOptions CLIMutable extension + test fixture churn"
  - "Stub selfrouting algorithm returns Qwen35B/Default for Phase 17: all non-keyword non-override prompts go to 35B until Phase 19 ships real SelfRouter"
  - "Reason=Default in stub (not SelfRoute): SelfRoute DU case doesn't exist yet (Phase 19 adds it); Default is correct interim value in DecisionLog"
  - "ML adapter DI registrations UNCHANGED in both modes: RetrainingService accumulates training data regardless of mode so ML re-activation via config flip + restart is always possible"
  - "configureServices alias inherits behavior automatically: no alias code change needed (alias just calls configureRequestPipeline)"

patterns-established:
  - "Routing.Mode config gate: single config.[\"\"] read + ToLowerInvariant + fail-fast match before factory registration"
  - "Dormancy gate pattern: mode-branched factory; dormant adapters stay DI-registered (warm for re-activation) but not invoked in routing path"

# Metrics
duration: 5min
completed: 2026-05-11
---

# Phase 17 Plan 02: Routing.Mode Config Switch Summary

**`Routing.Mode="selfrouting"|"ml"` config gate wired into CompositionRoot with fail-fast validation, selfrouting stub (Qwen35B/Default placeholder), and ML adapters DI-registered unconditionally in both modes (MODE-01..03)**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-05-11T05:15:23Z
- **Completed:** 2026-05-11T05:19:45Z
- **Tasks:** 2
- **Files modified:** 2 modified, 0 created

## Accomplishments

- Added `"Mode": "selfrouting"` as the first key in the `Routing` section of `appsettings.json` — v2.0 default; operator flips to `"ml"` for v1.x ML routing without rebuild
- Added `routingMode` let binding in `configureRequestPipeline`: reads `config.["Routing:Mode"]`, normalizes (null/whitespace -> "selfrouting"), trims + ToLowerInvariant, fails fast with `InvalidOperationException` on any value outside `{"selfrouting", "ml"}` (MODE-01)
- Replaced unconditional ML-only `RoutingAlgorithmRegistration` factory with mode-branched version: `"ml"` arm is verbatim v1.3 closure; `_` arm is Phase 17 selfrouting stub returning Qwen35B/Default with `Name="selfrouting"` / `ModelVersion="selfrouting-v1"` (MODE-02)
- All ML adapter DI registrations (IEmbedder, keyed IClassifier baseline/canary, ICanaryGate, IModelVersionProvider, RetrainingService, CanaryService) remain unconditional — registered in both modes (MODE-03 invariant preserved)
- Full test suite: 129 passed, 16 ignored, 0 failed — zero regressions

## Task Commits

1. **Task 1: Routing.Mode config key + fail-fast validation** - `a1bdf98` (feat)
2. **Task 2: Branch RoutingAlgorithmRegistration on Routing.Mode** - `4f76ff7` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/SmartRouter.Cli/appsettings.json` - `"Mode": "selfrouting"` added as first key in Routing section
- `src/SmartRouter.Cli/CompositionRoot.fs` - `routingMode` read/validate block + mode-branched `RoutingAlgorithmRegistration` factory

## Decisions Made

- **Direct config read chosen over RoutingOptions field**: `config.["Routing:Mode"]` mirrors Phase 16 `Routing:Judge:Enabled` idiom; adding `mutable Mode : string` to CLIMutable `RoutingOptions` record would require updating every RoutingOptions construction site in tests (MLRoutingTests, CanaryTests, etc.) — not worth the churn for a single scalar.
- **Stub selfrouting algorithm for Phase 17**: Phase 17 must make the mode switch functional before Phase 19 ships the real `makeSelfRoutingAlgorithm`. Stub returns Qwen35B/Default — intentional interim regression (non-keyword non-override prompts all go to 35B); operator wanting ML behavior in interim can set `Routing.Mode="ml"`.
- **`Reason=Default` in stub (not `SelfRoute`)**: `SelfRoute` DU case doesn't exist until Phase 19. Using `Default` means DecisionLog rows for non-matched prompts emit `routing_reason="default"` in Phase 17 selfrouting mode. Phase 19 introduces `SelfRoute` and updates the formatReason arm.
- **ML adapter DI unconditional (MODE-03)**: `RetrainingService` background loop accumulates hard cases regardless of mode, keeping the training dataset growing. Fully gating ML DI on `Routing.Mode="ml"` would starve the dataset in selfrouting mode, defeating the "ML stays warm for re-activation" design goal.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. Line numbers from research greps (391, 394, 549) were accurate. The `match routingMode with` pattern appears twice in CompositionRoot.fs (validation at ~line 407 + factory branch at ~line 427) — both are correct and intentional; the plan's "1 hit" note referred to the factory branch only.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- MODE-01, MODE-02, MODE-03 satisfied. Operator can flip `Routing.Mode` in appsettings.json + restart to switch between v2.0 selfrouting (default) and v1.x ML routing.
- MODE-04 (README §5 + §7 documentation) lands in Plan 17-03.
- The selfrouting stub in `RoutingAlgorithmRegistration` is a deliberate placeholder: Phase 19 replaces the `| _` arm body with `makeSelfRoutingAlgorithm` call. The DU type, factory structure, and `Name="selfrouting"` string are stable contracts Phase 19 depends on.
- Hard Rules (Stage 0) fires in both modes — keyword matches always route to 122B regardless of Routing.Mode setting.
- Integration test for `Routing.Mode="invalid"` startup-throw lands in Plan 17-03 (Task 1 by plan charter).
- Open issue: `ModelVersionProvider` initial value is still computed as `"ml-{sha8}"` in its own factory (lines ~736-746) even when `Routing.Mode="selfrouting"`. This provider is read by `CanaryService` and `RetrainingService` which remain registered in both modes. The mismatch is harmless in Phase 17 (ML path not invoked in routing); Phase 19 can update `ModelVersionProvider` initial value to `"selfrouting-v1"` if needed.

---
*Phase: 17-hard-rules-layer-and-routing-mode-switch*
*Completed: 2026-05-11*
