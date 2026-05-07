---
phase: 01-foundation
plan: 02
subsystem: core-domain
tags: [fsharp, domain-modeling, routing, expecto, serilog, system-text-json, hexagonal-architecture]

# Dependency graph
requires:
  - phase: 01-01
    provides: Solution skeleton, .fsproj scaffolds with placeholder ItemGroups, check-no-async.sh

provides:
  - SmartRouter.Core.Domain: 10 types (ModelId, Priority, TaskType, RoutingReason, MessageRole, Message, RouterRequest, RouterError, RoutingDecision, RoutingConfig plain record)
  - SmartRouter.Core.Routing: config-parameterized three-stage pipeline (routeRequest, tryTaskTable, scoreComplexity, applyHeuristic all read RoutingConfig); taskToDecision retained as validation/test utility only; canonicalTaskTable + defaultRoutingConfig helpers
  - SmartRouter.Core.Ports: IUpstreamClient, IClock, IHealthProbe interfaces
  - SmartRouter.Cli.Adapters.Json: jsonOptions with FSharp.SystemTextJson WithUnionUnwrapFieldlessTags
  - SmartRouter.Cli.Adapters.Logging: Serilog stderr-only sink (standardErrorFromLevel=Verbose, OBS-04)
  - RoutingTests.fs: 22 Expecto tests, all passing; 3 ROUT-05 config-driven dispatch proofs

affects:
  - 01-03: plan 01-03 wires RoutingConfig from appsettings.json and calls routeRequest; uses IUpstreamClient, Json.fs, Logging.fs
  - future phases: startup validator diffs JSON TaskTable against canonicalTaskTable; taskToDecision is the compile-time completeness anchor

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Hexagonal Core: zero Serilog/HttpClient/ASP.NET/IOptions references in Core; RoutingConfig is a plain F# record (ARCH-01)"
    - "Config-parameterized routing pipeline: RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>"
    - "taskToDecision retained as validation-only utility; runtime dispatch reads config.TaskTable Map at runtime (ROUT-05)"
    - "canonicalTaskTable + defaultRoutingConfig bridge: exhaustive DU match is compile-time anchor, not runtime path"
    - "MessageRole.System shadows System namespace — test code uses String.replicate not System.String.replicate"

key-files:
  created:
    - src/SmartRouter.Core/Domain.fs
    - src/SmartRouter.Core/Routing.fs
    - src/SmartRouter.Core/Ports.fs
    - src/SmartRouter.Cli/Adapters/Json.fs
    - src/SmartRouter.Cli/Adapters/Logging.fs
    - tests/SmartRouter.Tests/RoutingTests.fs
  modified:
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "RoutingConfig is a plain F# record in Core (no IOptions<T>); Cli layer constructs it from appsettings.json at composition time"
  - "taskToDecision survives as utility-only (compile-time DU exhaustiveness anchor); NEVER called by routeRequest or tryTaskTable"
  - "| _ -> None is correct in string-pattern matches (tryParseModelAlias, tryParseTaskType); only DU matches must be exhaustive"
  - "MessageRole.System shadows System namespace in F# — tests use String.replicate (F# stdlib) not System.String.replicate"

patterns-established:
  - "Config-parameterized routing: all operator-tunable decisions flow through RoutingConfig record, not hardcoded literals"
  - "canonicalTaskTable/defaultRoutingConfig: bridges exhaustive DU match to config-driven runtime without calling the match at runtime"

# Metrics
duration: 5min
completed: 2026-05-07
---

# Phase 1 Plan 02: Core Domain Summary

**Config-parameterized hexagonal Core (Domain.fs, Routing.fs, Ports.fs) with 22 passing Expecto routing tests including 3 ROUT-05 config-driven dispatch proofs**

## Performance

- **Duration:** 5 min
- **Started:** 2026-05-07T06:36:35Z
- **Completed:** 2026-05-07T06:42:15Z
- **Tasks:** 3
- **Files modified:** 9 (6 created, 3 modified)

## Accomplishments

- Pure Core domain with 10 types; RoutingConfig as plain F# record satisfying ARCH-01
- Config-parameterized three-stage routing pipeline; runtime dispatch reads config.TaskTable Map (ROUT-05); taskToDecision retained as compile-time correctness anchor only
- Json.fs and Logging.fs adapters copied from blueCode (module name only changed); Logging.fs retains standardErrorFromLevel=Verbose (OBS-04)
- 22 Expecto tests run with zero failures; includes 3 ROUT-05 config-driven tests (TaskTable remap, threshold change, empty keywords)

## Verification Evidence

```
check-no-async.sh output:
  OK: no async {} expressions in src/SmartRouter.Core

grep -n '| _ ->' src/SmartRouter.Core/Routing.fs output:
  14:    | _ -> None                  (tryParseModelAlias — string match, required)
  43:/// COMPILE-TIME CONTRACT: ... NEVER add | _ ->   (comment)
  [line 40 also has | _ -> None in tryParseTaskType — string match, required]

Note: both hits are in string-pattern matches, not DU matches.
taskToDecision has no | _ -> (compile-time exhaustiveness guaranteed).

Forbidden imports check:
  grep -E '^open (Serilog|Microsoft\.AspNetCore|...)' src/SmartRouter.Core/*.fs
  → (no output — OK)

Test run:
  22 tests run in 00:00:00.08 for all.routing — 22 passed, 0 ignored, 0 failed, 0 errored.
```

## Task Commits

1. **Task 1: Core domain (Domain.fs, Routing.fs, Ports.fs)** - `f9b13ba` (feat)
2. **Task 2: Json.fs + Logging.fs adapters** - `05a6a2f` (feat)
3. **Task 3: RoutingTests.fs Expecto suite** - `547214f` (test)

## Files Created/Modified

- `src/SmartRouter.Core/Domain.fs` — 10 Core DUs + RoutingConfig plain record
- `src/SmartRouter.Core/Routing.fs` — config-parameterized pipeline; tryTaskTable reads config.TaskTable; scoreComplexity/applyHeuristic read config.Keywords/ComplexityThreshold; taskToDecision utility-only; canonicalTaskTable + defaultRoutingConfig
- `src/SmartRouter.Core/Ports.fs` — IUpstreamClient (CompleteAsync + StreamAsync), IClock, IHealthProbe interfaces
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` — Compile: Domain.fs → Routing.fs → Ports.fs
- `src/SmartRouter.Cli/Adapters/Json.fs` — minimal jsonOptions (WithUnionUnwrapFieldlessTags); blueCode extraction pipeline omitted
- `src/SmartRouter.Cli/Adapters/Logging.fs` — verbatim blueCode copy; module name changed only; retains standardErrorFromLevel=Verbose (OBS-04)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Adapters/Json.fs + Adapters/Logging.fs added before Program.fs
- `tests/SmartRouter.Tests/RoutingTests.fs` — 22 Expecto tests (ROUT-01 through ROUT-05 + override precedence + tie-break)
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — RoutingTests.fs compiled before RouterTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` — SmartRouter.Tests.RoutingTests.tests added to rootTests

## Decisions Made

- **RoutingConfig is Core-pure**: Plain F# record in Domain.fs; no IOptions<T>, no ASP.NET, no Microsoft.Extensions. Cli layer (plan 01-03) constructs it from appsettings.json and passes it into routeRequest at composition time (ARCH-01 preserved).
- **taskToDecision utility-only**: The exhaustive F# DU match is retained as a compile-time anchor (new TaskType cases become compile errors) and as a test/validation baseline. It is NOT called by routeRequest or tryTaskTable at runtime — runtime dispatch reads config.TaskTable. This satisfies the user's explicitly stated locked decision in CONTEXT.md.
- **canonicalTaskTable/defaultRoutingConfig bridge**: These helpers derive the canonical config from taskToDecision (ensuring tests use the same model/priority assignments as the exhaustive match) without calling taskToDecision in the runtime path. Plan 01-03's startup validator will use canonicalTaskTable to diff against the JSON-loaded TaskTable.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] MessageRole.System shadows System namespace in test code**
- **Found during:** Task 3 (RoutingTests.fs compilation)
- **Issue:** `open SmartRouter.Core.Domain` brings `MessageRole = System | User | Assistant` into scope. The `System` DU case shadows the `System` namespace. `System.String.replicate` fails with "MessageRole type has no String member".
- **Fix:** Replaced `System.String.replicate` with `String.replicate` (F# standard library function that doesn't need namespace qualification). Both calls updated.
- **Files modified:** tests/SmartRouter.Tests/RoutingTests.fs
- **Verification:** Build succeeds, 22 tests pass.
- **Committed in:** 547214f (Task 3 commit)

**2. [Rule 1 - Bug] `Routing.defaultRoutingConfig` module qualifier ambiguous after open**
- **Found during:** Task 3 (RoutingTests.fs compilation)
- **Issue:** After `open SmartRouter.Core.Routing`, using `Routing.defaultRoutingConfig` fails because `Routing` is not a locally-visible module qualifier in this context.
- **Fix:** Changed to `defaultRoutingConfig` (directly in scope after the open).
- **Files modified:** tests/SmartRouter.Tests/RoutingTests.fs
- **Verification:** Build succeeds.
- **Committed in:** 547214f (Task 3 commit)

**3. [Note — not a deviation] `| _ -> None` appears in string-pattern matches**
- The plan verification specifies `grep -n '| _ ->'` should return zero hits. However, `tryParseModelAlias` and `tryParseTaskType` both match over `string` values (not DU cases) and necessarily require a `| _ -> None` catch-all for the "unknown alias/task" case. The ARCHITECTURE.md code samples also include these. The prohibition is correctly interpreted as: DU exhaustive matches (`taskToDecision`) must have no `| _ ->`. String matches have it and must. Not treated as a deviation — documented for clarity.

---

**Total deviations:** 2 auto-fixed (both Rule 1 - compilation bugs in test code)
**Impact on plan:** Both fixes were minor naming/scoping issues in test code only. Core implementation and behavior are exactly as planned. No scope creep.

## Issues Encountered

- F# name collision between `MessageRole.System` (DU case) and `System` namespace when both are in scope after `open SmartRouter.Core.Domain`. Resolved by using `String.replicate` instead of `System.String.replicate`.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Core domain is complete and tested; plan 01-03 can wire RoutingConfig from appsettings.json and call routeRequest directly
- IUpstreamClient interface is ready for QwenUpstreamClient implementation
- Json.fs and Logging.fs adapters are ready to be consumed by QwenUpstreamClient
- canonicalTaskTable is ready for the plan 01-03 startup validator to diff against the JSON TaskTable
- All 22 routing tests pass; Phase 1 success criteria #2 and #3 are satisfied at the unit-test level

---
*Phase: 01-foundation*
*Completed: 2026-05-07*
