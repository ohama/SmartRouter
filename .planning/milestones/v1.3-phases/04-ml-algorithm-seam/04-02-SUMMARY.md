---
phase: 04-ml-algorithm-seam
plan: 02
subsystem: routing
tags: [fsharp, dotnet, dependency-injection, configuration, cli, aspnetcore]

# Dependency graph
requires:
  - phase: 04-01-core-refactor
    provides: RoutingAlgorithm type alias in Domain.fs; Heuristic.fs + ML.fs flat siblings; 3-arg routeRequest seam

provides:
  - RoutingOptions.Algorithm field (CLIMutable string, null-safe)
  - AddSingleton<RoutingAlgorithm> factory with config-driven dispatch (null|""|"heuristic" -> heuristic, "ml" -> ML, other -> fail)
  - ChatCompletions.handler wired with algorithm: RoutingAlgorithm parameter resolved from DI
  - --routing-algorithm CLI flag (= and space syntax, last-wins, empty/invalid rejection, AddInMemoryCollection before configureServices)
  - StreamingTests defensive Routing:Algorithm key
  - appsettings.json Routing.Algorithm: "heuristic" default key
  - Full-solution build green at Wave 2 boundary

affects:
  - 04-03-ml-algorithm-tests
  - any phase touching CompositionRoot.fs or ChatCompletions.fs

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Func<IServiceProvider, T> cast required when T is an F# function-type alias registered via AddSingleton<T>"
    - "open Microsoft.Extensions.Configuration required in Program.fs for IConfigurationBuilder + AddInMemoryCollection extension method"
    - "Qualified module names SmartRouter.Core.Heuristic.applyHeuristic / SmartRouter.Core.ML.applyML at DI dispatch site (no open of algorithm modules in CompositionRoot)"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/appsettings.json
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/Program.fs
    - tests/SmartRouter.Tests/StreamingTests.fs
    - tests/SmartRouter.Tests/RoutingTests.fs

key-decisions:
  - "Func<IServiceProvider, RoutingAlgorithm> explicit cast required for AddSingleton<T> when T is an F# function-type alias — without the Func wrapper, F# currying makes the lambda match IServiceProvider -> RoutingConfig -> RouterRequest -> RoutingDecision, which the DI overload resolver rejects"
  - "Qualified names SmartRouter.Core.Heuristic.applyHeuristic and SmartRouter.Core.ML.applyML used at CompositionRoot dispatch site; no open of algorithm modules — keeps the single dispatch point readable without polluting the module namespace"
  - "open Microsoft.Extensions.Configuration added to Program.fs (was missing) to bring IConfigurationBuilder and AddInMemoryCollection extension into scope"
  - "RoutingTests.fs 04-01 bug: after open SmartRouter.Core.Heuristic, correct usage is applyHeuristic (direct), not Heuristic.applyHeuristic (sub-module that does not exist); all 4 occurrences fixed"

patterns-established:
  - "Algorithm dispatch: null | empty | heuristic -> baseline, ml -> ML, other -> InvalidOperationException at startup (fail-fast before serving any traffic)"
  - "CLI override ordering: parse flag, inject via AddInMemoryCollection, THEN call configureServices — same lesson as Phase 2 startTestRouter"

# Metrics
duration: 15min
completed: 2026-05-08
---

# Phase 4 Plan 02: Config and CLI Summary

**Routing.Algorithm config key + RoutingAlgorithm DI singleton + --routing-algorithm CLI flag wired end-to-end; full-solution build greens at Wave 2 boundary**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-05-08T13:30:00Z
- **Completed:** 2026-05-08T13:45:00Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments

- `appsettings.json` has `"Algorithm": "heuristic"` as first key in Routing section
- `CompositionRoot.fs` registers `AddSingleton<RoutingAlgorithm>` with config-driven dispatch covering all three default-equivalent cases (`null | "" | "heuristic"`) plus `"ml"` and invalid-value rejection at startup
- `ChatCompletions.fs` resolves `RoutingAlgorithm` from DI per request and passes it to the 3-arg `routeRequest` — fixes the Wave 1 Cli build failure from 04-01
- `Program.fs` parses `--routing-algorithm` with both `=` and space syntax, `Array.tryFindIndexBack` last-wins, empty/invalid rejection, and `AddInMemoryCollection` injection BEFORE `configureServices` (scripted line-order check: line 30 < line 49)
- `StreamingTests.fs` `startTestRouter` defensively includes `"Routing:Algorithm", "heuristic"` key
- Full-solution `dotnet build SmartRouter.slnx` — 0 warnings, 0 errors
- 39/39 tests pass; 2 ignored (LoadTests pending)

## Task Commits

Each task was committed atomically:

1. **Task 1: appsettings + CompositionRoot dispatch + ChatCompletions 3-arg** - `87acb5f` (feat)
2. **Task 2: Program.fs CLI + StreamingTests + RoutingTests bug fix** - `0c822e3` (feat)

**Plan metadata:** (pending — next commit)

## Files Created/Modified

- `src/SmartRouter.Cli/appsettings.json` — Added `"Algorithm": "heuristic"` as first key in Routing section
- `src/SmartRouter.Cli/CompositionRoot.fs` — Added `Algorithm: string` to `RoutingOptions`; added `AddSingleton<RoutingAlgorithm>` dispatch factory with `Func<IServiceProvider, RoutingAlgorithm>` wrapper
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — Added `algorithm: RoutingAlgorithm` param to `handler`; resolves from DI in `mapEndpoints`; passes to `routeRequest`
- `src/SmartRouter.Cli/Program.fs` — Added `open Microsoft.Extensions.Configuration`; added `--routing-algorithm` CLI flag parsing block before `configureServices`
- `tests/SmartRouter.Tests/StreamingTests.fs` — Added `"Routing:Algorithm", "heuristic"` to `startTestRouter` AddInMemoryCollection
- `tests/SmartRouter.Tests/RoutingTests.fs` — Fixed pre-existing 04-01 bug: `Heuristic.applyHeuristic` → `applyHeuristic` (4 occurrences; open already in scope)

## Decisions Made

- **Func wrapper for function-type alias DI**: `AddSingleton<RoutingAlgorithm>(Func<IServiceProvider, RoutingAlgorithm>(fun sp -> ...))` — required because F# currying caused the factory lambda to match `IServiceProvider -> RoutingConfig -> RouterRequest -> RoutingDecision` (4-arg) instead of `Func<IServiceProvider, RoutingAlgorithm>`. Explicit `Func` cast fixes overload resolution.
- **Qualified names at dispatch site**: `SmartRouter.Core.Heuristic.applyHeuristic` and `SmartRouter.Core.ML.applyML` used in CompositionRoot (no new `open` statements) — plan requirement maintained.
- **open Microsoft.Extensions.Configuration in Program.fs**: Was missing; needed for both `IConfigurationBuilder` cast and `AddInMemoryCollection` extension method.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] RoutingTests.fs: Heuristic.applyHeuristic unresolved after open SmartRouter.Core.Heuristic**
- **Found during:** Task 2 (full-solution build verification)
- **Issue:** Pre-existing 04-01 bug — tests were passing in 04-01 only due to `--no-build` flag with cached binaries. `open SmartRouter.Core.Heuristic` brings `applyHeuristic` directly into scope; `Heuristic.applyHeuristic` looks for a sub-module named `Heuristic` inside the opened module, which doesn't exist. 4 call sites affected (lines 29, 171, 182, 189).
- **Fix:** Replaced all 4 occurrences of `Heuristic.applyHeuristic` with `applyHeuristic`
- **Files modified:** `tests/SmartRouter.Tests/RoutingTests.fs`
- **Verification:** Full-solution build succeeded; 39/39 tests pass
- **Committed in:** `0c822e3` (Task 2 commit)

**2. [Rule 1 - Bug] Func wrapper required for AddSingleton<RoutingAlgorithm>**
- **Found during:** Task 1 (CompositionRoot build)
- **Issue:** F# function-type alias as DI service type — `AddSingleton<RoutingAlgorithm>(fun sp -> ...)` causes F# to infer the lambda as a curried `IServiceProvider -> RoutingConfig -> RouterRequest -> RoutingDecision` (4-arg), not `Func<IServiceProvider, RoutingAlgorithm>`. DI overload resolver rejects this. Explicit type annotation inside the lambda did not help.
- **Fix:** Wrapped factory lambda in `Func<IServiceProvider, RoutingAlgorithm>(fun sp -> ...)`
- **Files modified:** `src/SmartRouter.Cli/CompositionRoot.fs`
- **Verification:** Cli build succeeded with 0 warnings
- **Committed in:** `87acb5f` (Task 1 commit)

**3. [Rule 1 - Bug] open Microsoft.Extensions.Configuration missing in Program.fs**
- **Found during:** Task 2 (Program.fs build)
- **Issue:** `IConfigurationBuilder` and `AddInMemoryCollection` extension method not resolved — `Microsoft.Extensions.Configuration` namespace not opened
- **Fix:** Added `open Microsoft.Extensions.Configuration` to Program.fs; simplified cast to `(builder.Configuration :> IConfigurationBuilder)` (no full qualification needed)
- **Files modified:** `src/SmartRouter.Cli/Program.fs`
- **Verification:** Cli build succeeded with 0 warnings
- **Committed in:** `0c822e3` (Task 2 commit)

---

**Total deviations:** 3 auto-fixed (3 pre-existing or compile-time bugs)
**Impact on plan:** All three fixes required for correctness / build success. No scope creep.

## Issues Encountered

None beyond the auto-fixed bugs above.

## Next Phase Readiness

- 04-03 (Phase 4 tests for ML algorithm seam) is fully unblocked
- Operator can now switch algorithms via `appsettings.json` `Routing.Algorithm: "ml"` OR `dotnet run -- --routing-algorithm=ml`
- Default behavior is bit-for-bit identical (heuristic at all touchpoints)
- check-routing-isolation.sh and check-no-async.sh both pass

---
*Phase: 04-ml-algorithm-seam*
*Completed: 2026-05-08*
