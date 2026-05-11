---
phase: 12-heuristic-removal
plan: 05
subsystem: testing
tags: [fsharp, expecto, dependency-injection, configureWithoutMl, test-stub, RoutingAlgorithmRegistration]

# Dependency graph
requires:
  - phase: 12-02
    provides: "configureWithoutMl split from configureRequestPipeline; configureServices backwards-compat alias"
provides:
  - "StreamingTests.fs migrated to configureWithoutMl + test-stub RoutingAlgorithmRegistration"
  - "LoggingTests.fs migrated to configureWithoutMl + routing_algorithm=ml assertion"
  - "HealthFallbackTests.fs migrated to configureWithoutMl + manual HealthService/QueueDispatcher wiring (option b)"
affects: ["12-06 (final cleanup — configureServices alias removal)", "13-02 (HealthService + QueueDispatcher ctor logger params)"]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Test fixture option(b) pattern: configureWithoutMl + manual HealthService triple-reg for tests needing fallback wiring"
    - "test-stub RoutingAlgorithmRegistration: Name=ml, ModelVersion=test-stub, always routes to Qwen35B"

key-files:
  created: []
  modified:
    - "tests/SmartRouter.Tests/StreamingTests.fs"
    - "tests/SmartRouter.Tests/LoggingTests.fs"
    - "tests/SmartRouter.Tests/HealthFallbackTests.fs"

key-decisions:
  - "12-05: option(b) chosen for HealthFallbackTests — configureWithoutMl + manual HealthService/QueueDispatcher registrations; keeps fallback wiring active without needing configureRequestPipeline (which would trigger ensureEmbeddingFilesPresent)"
  - "12-05: ICanaryMetrics lives in SmartRouter.Cli.Adapters.CanaryMetrics (not SmartRouter.Core.CanaryPorts); test stubs must reference the Cli namespace"
  - "12-05: QueueDispatcherOptions must be manually bound via services.Configure<QueueDispatcherOptions> in each test fixture that manually registers QueueDispatcher; configureWithoutMl does not bind it"
  - "12-05: Phase 13-02 will add ILogger<HealthService> and ILogger<QueueDispatcher> ctor params; NO NullLogger added preemptively in this plan per cross-phase note"
  - "12-05: LoggingTests model_version assertion updated from heuristic-v1 to test-stub (matches test-stub registration ModelVersion field)"
  - "12-05: ModelsTests.fs MODELS-01/02/03 were already broken before this plan (pre-existing from 12-02 restructuring, not in 12-05 scope)"

patterns-established:
  - "configureWithoutMl test fixture pattern: configureWithoutMl + testStubReg + IHealthProbe stub + QueueDispatcher + IModelVersionProvider + NullCanaryGate + NoOpCanaryMetrics (minimal complete wiring for ChatCompletions handler)"
  - "HealthFallback test fixture pattern: configureWithoutMl + manual HealthService triple-reg + QueueDispatcher with real IHealthProbe for fallback tests"

# Metrics
duration: 22min
completed: 2026-05-09
---

# Phase 12 Plan 05: Fixture Migration Summary

**Three test fixtures migrated from `Routing:Algorithm = "heuristic"` config override to test-stub RoutingAlgorithmRegistration injected post-configureWithoutMl; 18 tests pass (8 streaming + 5 logging + 5 health)**

## Performance

- **Duration:** ~22 min
- **Started:** 2026-05-09T07:30:00Z
- **Completed:** 2026-05-09T07:52:00Z
- **Tasks:** 3
- **Files modified:** 3

## Accomplishments
- Removed all heuristic string literals and Routing:Algorithm config keys from StreamingTests, LoggingTests, and HealthFallbackTests
- Each fixture now calls `configureWithoutMl` (skips ML init, no model files needed) and injects a test-stub `RoutingAlgorithmRegistration` as the sole registration
- HealthFallbackTests uses option(b): manual HealthService triple-reg + QueueDispatcher with real IHealthProbe so the full fallback wiring remains active
- LoggingTests routing_algorithm assertion updated from "heuristic" to "ml"; model_version updated from "heuristic-v1" to "test-stub"
- 18 total tests pass (8 streaming + 5 logging + 5 health fallback)

## Task Commits

1. **Task 1: StreamingTests.fs — rewire fixture** - `57e61a6` (feat)
2. **Task 2: LoggingTests.fs — rewire fixture + update routing_algorithm assertion** - `59d56b2` (feat)
3. **Task 3: HealthFallbackTests.fs — rewire fixture** - `85b5403` (feat)

## Files Created/Modified
- `/tests/SmartRouter.Tests/StreamingTests.fs` - Rewired to configureWithoutMl; added test-stub + full DI stack manually
- `/tests/SmartRouter.Tests/LoggingTests.fs` - Same rewire; routing_algorithm assertion updated to "ml"; model_version updated to "test-stub"
- `/tests/SmartRouter.Tests/HealthFallbackTests.fs` - Same rewire; HealthService triple-reg + QueueDispatcher with real IHealthProbe manually registered

## Decisions Made

1. **option(b) for HealthFallbackTests**: `configureWithoutMl` + manual HealthService/QueueDispatcher registrations. Chosen over option(a) (full configureRequestPipeline with fake IEmbedder/IClassifier) because option(a) would still trigger `ensureEmbeddingFilesPresent` (unconditional in configureRequestPipeline). option(b) is cleanest — no fake ML component injection needed.

2. **ICanaryMetrics is in SmartRouter.Cli namespace**: Not in Core.CanaryPorts as the initial attempt assumed. Fixed to `SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics`. Applies to all three fixtures.

3. **QueueDispatcherOptions manual binding**: `configureWithoutMl` excludes QueueDispatcher entirely, so `services.Configure<QueueDispatcherOptions>` must be called manually in each fixture before constructing QueueDispatcher. Without it, MaxConcurrent122B defaults to 0 and the ctor throws.

4. **Cross-phase note honored**: NO `NullLogger<HealthService>` or `NullLogger<QueueDispatcher>` arguments added. Phase 12 ctor signatures used exactly as-is. Phase 13-02 executor is responsible for updating these sites.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ICanaryMetrics namespace correction**
- **Found during:** Task 1 (StreamingTests.fs build)
- **Issue:** Plan suggested `SmartRouter.Core.CanaryPorts.ICanaryMetrics` but that type doesn't exist in Core — it lives in `SmartRouter.Cli.Adapters.CanaryMetrics`
- **Fix:** Used `SmartRouter.Cli.Adapters.CanaryMetrics.ICanaryMetrics` in all three fixtures
- **Files modified:** StreamingTests.fs, LoggingTests.fs, HealthFallbackTests.fs
- **Verification:** Build clean
- **Committed in:** 57e61a6, 59d56b2, 85b5403

**2. [Rule 3 - Blocking] QueueDispatcherOptions binding missing**
- **Found during:** Task 1 (TTFB streaming test run)
- **Issue:** Test threw `Queue.MaxConcurrent122B must be 1; got 0` — QueueDispatcherOptions was not bound from config because configureWithoutMl excludes that Configure<> call
- **Fix:** Added `services.Configure<QueueDispatcherOptions>(config.GetSection("Queue"))` before QueueDispatcher singleton registration in each fixture
- **Files modified:** StreamingTests.fs, LoggingTests.fs, HealthFallbackTests.fs
- **Verification:** 8/8 streaming tests pass after fix
- **Committed in:** 57e61a6, 59d56b2, 85b5403

---

**Total deviations:** 2 auto-fixed (1 bug/namespace, 1 blocking/missing config binding)
**Impact on plan:** Both fixes necessary for correctness. No scope creep.

## Issues Encountered

- ModelsTests.fs (MODELS-01/02/03) are erroring with `No service for type IEmbedder` — confirmed pre-existing from 12-02 restructuring, not caused by this plan. ModelsTests.fs is out of scope for 12-05 and will be handled by the appropriate Wave 3/4 plan.

## Next Phase Readiness
- StreamingTests, LoggingTests, HealthFallbackTests: fully migrated, zero heuristic refs, all passing
- ModelsTests needs its own fixture migration (separate plan)
- Phase 13-02 executor: must update HealthService + QueueDispatcher manual instantiation sites in all 3 fixtures when logger params are added to ctors

---
*Phase: 12-heuristic-removal*
*Completed: 2026-05-09*
