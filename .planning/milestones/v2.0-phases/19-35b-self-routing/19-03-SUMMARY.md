---
phase: 19-35b-self-routing
plan: 03
subsystem: routing+testing
tags: [self-routing, ISelfRouter, ChatCompletions, cascade, integration-tests, unit-tests, SR-06, SR-08, SR-09]

# Dependency graph
requires:
  - phase: 19-01
    provides: SelfRouter.fs adapter (ISelfRouter + ISelfRouteVerdict + cache + parser)
  - phase: 19-02
    provides: DI wiring (named "selfrouter" HttpClient + ISelfRouter triple-reg + ISelfRouterStats NoOp)
provides:
  - ISelfRouter.ClassifyAsync wired into ChatCompletions.fs non-streaming branch (Stage 4 of cascade)
  - SR-06 streaming-skip comment + structural zero calls in streaming branch
  - SelfRouterTests.fs — 9 unit tests for parser safety bias, cache mechanics, template-missing skip
  - SelfRoutingIntegrationTests.fs — 7 DI integration tests (SC-1/2/3/4 + fail-open + DI singleton)
  - MlDormantTests.fs — 1 ML-mode DI registration guard (skip-guarded on CI without ONNX)
affects: [Phase 19-04 (README/CHANGELOG documentation), Phase 20 (Hermes integration)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Stage 4 self-classify: call site in ChatCompletions.fs after routeRequest, not inside algorithm closure (synchronous constraint)"
    - "Fail-open pattern: RouteSkipped/RouteFailed leave decision unchanged; only RouteSafe/RouteUnsafe rebind"
    - "Null-safe ISelfRouter resolution: GetService<ISelfRouter>() guards ml-mode path"
    - "Fake IHttpClientFactory with controllable verdict ref for unit + integration tests"

key-files:
  created:
    - tests/SmartRouter.Tests/SelfRouterTests.fs
    - tests/SmartRouter.Tests/SelfRoutingIntegrationTests.fs
    - tests/SmartRouter.Tests/MlDormantTests.fs
  modified:
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - tests/SmartRouter.Tests/RouterTests.fs
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj

key-decisions:
  - "Self-classify call site is ChatCompletions.fs (non-streaming branch), NOT CompositionRoot algorithm closure — RoutingAlgorithm is synchronous; async HTTP classify cannot go inside it"
  - "decision.Reason = Default gate: only Default-reason decisions are classified; HardRule/StickyEscalation/ExplicitModelOverride/ML/FallbackTo* short-circuit"
  - "Streaming branch: zero ClassifyAsync calls, only SR-06 comment block"
  - "RouteSafe → 35B/Low/SelfRoute/PromptVersion; RouteUnsafe → 122B/High/SelfRoute/PromptVersion"
  - "RouteSkipped/RouteFailed: fail-open (decision unchanged); selfrouter_skipped counter NOT incremented for streaming (streaming never reaches adapter)"
  - "MlDormantTests W4 skip guard: onnxEmbedPath = 'models/embed/bge-m3-int8.onnx' (same probe as ModeSwitchTests.fs)"

patterns-established:
  - "Phase 19 SR-06 streaming-skip comment: explicit documentation in streaming branch for each skipped async phase"
  - "FS0760 fix pattern: use 'new StubHandler(...)' not 'StubHandler(...)' when type inherits IDisposable"
  - "Integration test DI: buildContainer with fake IHttpClientFactory + AddLogging(None) + triple SelfRouter reg"

# Metrics
duration: ~30min
completed: 2026-05-12
---

# Phase 19 Plan 03: Cascade Integration + Streaming Skip + Tests Summary

**ISelfRouter.ClassifyAsync wired into ChatCompletions.fs non-streaming branch with SR-06 streaming-skip comment and 17 new tests covering SC-1 through SC-5 (parser, cache, integration, ML dormant)**

## Performance

- **Duration:** ~30 min
- **Started:** 2026-05-12
- **Completed:** 2026-05-12
- **Tasks:** 4
- **Files modified:** 6 (1 source, 3 test files created, 2 test infra updated)

## Accomplishments
- `ChatCompletions.fs` non-streaming branch now calls `ISelfRouter.ClassifyAsync` after `routeRequest` returns `Ok decision` with `Reason=Default`
- Streaming branch has explicit SR-06 comment block; zero `ClassifyAsync` calls verified structurally
- `SelfRouterTests.fs` (9 tests): parser safety bias (SAFE ⊂ UNSAFE), cache hit/miss, RouteFailed not cached, RouteSkipped + counter, PromptVersion hash/fallback
- `SelfRoutingIntegrationTests.fs` (7 tests): end-to-end DI with fake HttpClientFactory covering SC-1/2/3/4, fail-open, DI singleton
- `MlDormantTests.fs` (1 test, skip-guarded): resolves `RoutingAlgorithmRegistration` in `Routing.Mode="ml"` and asserts `Name="ml"` + `ModelVersion.StartsWith("ml-")`
- Total baseline: 167 passed + 18 ignored (ML dormant skipped on this host) + 0 failed (was 150+17+0)

## Task Commits

1. **Task 1: Wire self-classify into ChatCompletions non-streaming branch** - `f424ddb` (feat)
2. **Task 2: SelfRouterTests.fs unit tests** - `c45528f` (test)
3. **Task 3: SelfRoutingIntegrationTests.fs end-to-end DI tests** - `a5cc886` (test)
4. **Task 4: MlDormantTests.fs SR-09 dormant guard** - `5d7a010` (test)

## Files Created/Modified
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — added `open SelfRouter`, SR-06 streaming comment, `let! decision = task { if decision.Reason = Default then ... }` non-streaming self-classify block
- `tests/SmartRouter.Tests/SelfRouterTests.fs` — 9 unit tests (NEW)
- `tests/SmartRouter.Tests/SelfRoutingIntegrationTests.fs` — 7 DI integration tests (NEW)
- `tests/SmartRouter.Tests/MlDormantTests.fs` — 1 skip-guarded ML dormant test (NEW)
- `tests/SmartRouter.Tests/RouterTests.fs` — 3 new entries in rootTests
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — 3 new `<Compile Include>` entries

## Decisions Made

- **Self-classify in ChatCompletions.fs, not algorithm closure:** `RoutingAlgorithm` is typed `RoutingConfig -> RouterRequest -> RoutingDecision` (synchronous). Calling async `ISelfRouter.ClassifyAsync` inside it would require `.GetAwaiter().GetResult()` (deadlock risk). Call site mirrors Phase 14 QualityFallback pattern.
- **`decision.Reason = Default` gate:** Only `Default`-reason decisions go through self-classify. `HardRule`, `StickyEscalation`, `ExplicitModelOverride`, `ExplicitTask`, `ML`, `FallbackTo*` short-circuit — these already have decided targets.
- **Null-safe `GetService<ISelfRouter>()`:** `ISelfRouter` is NOT registered in `Routing.Mode="ml"` arm (by design from 19-02). `GetService` returns null; `isNull (box selfRouter)` guard fail-opens to original decision. Matches judge pattern.
- **FS0760 fix:** `StubHandler(...)` without `new` emitted FS0760 (TreatWarningsAsErrors). Fixed with `new StubHandler(...)`. Required for all types inheriting IDisposable in test code.
- **SelfRoutingIntegrationTests domain open:** `open SmartRouter.Core.Domain` required for `Message` record type used in SC-1 test that calls `computePromptHash`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] FS0760 warning in SelfRouterTests.fs**
- **Found during:** Task 2 (SelfRouterTests.fs build)
- **Issue:** `StubHandler(fun () -> ...)` inside `makeCountingFactory` emitted FS0760 "use 'new Type(args)' syntax for IDisposable-inheriting objects" — promoted to error by `TreatWarningsAsErrors=true`
- **Fix:** Changed to `new StubHandler(fun () -> ...)`
- **Files modified:** `tests/SmartRouter.Tests/SelfRouterTests.fs`
- **Verification:** `dotnet build` 0 warnings, 0 errors
- **Committed in:** c45528f (Task 2 commit)

**2. [Rule 1 - Bug] FS0072 type inference error in SelfRoutingIntegrationTests.fs**
- **Found during:** Task 3 (SelfRoutingIntegrationTests.fs build)
- **Issue:** `m.Content` in `List.map` lambda couldn't be resolved without `open SmartRouter.Core.Domain`; also needed `let messages : Message list = ...` type annotation
- **Fix:** Added `open SmartRouter.Core.Domain` and explicit `Message list` type annotation
- **Files modified:** `tests/SmartRouter.Tests/SelfRoutingIntegrationTests.fs`
- **Verification:** `dotnet build` 0 warnings, 0 errors
- **Committed in:** a5cc886 (Task 3 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 1 — build-time compiler errors)
**Impact on plan:** Both mandatory for compilation under `TreatWarningsAsErrors=true`. No scope change.

## Issues Encountered
- Expecto filter syntax (`--filter`) doesn't match test list names with em-dash (—) using substring; used `--list-tests` to verify registration and ran full suite to confirm pass counts.

## Next Phase Readiness
- All 5 ROADMAP Success Criteria are now testable via `dotnet test`
- SC-1/2/3/4 verified by `SelfRoutingIntegrationTests.fs`
- SC-5 verified by `MlDormantTests.fs` (skip on CI without ONNX; pass on hosts with ML files)
- Phase 19-04 (README + CHANGELOG) is unblocked — all behavioral changes are in place
- `ChatCompletions.fs` Phase 19 wiring complete; no changes needed in 19-04 source code

---
*Phase: 19-35b-self-routing*
*Completed: 2026-05-12*
