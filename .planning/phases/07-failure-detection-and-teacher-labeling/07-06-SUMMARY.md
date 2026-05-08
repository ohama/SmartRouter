---
phase: 07-failure-detection-and-teacher-labeling
plan: 06
subsystem: testing
tags: [expecto, fsharp, fake-kestrel, jsonl, channel, background-service, ihttpclientfactory, json-fsharp-converter]

# Dependency graph
requires:
  - phase: 07-failure-detection-and-teacher-labeling
    provides: FailureDetector + TeacherLabeler + HardCaseDatasetWriter real implementations (Plans 07-02/03/04); CompositionRoot DI registrations + appsettings.json sections (Plan 07-05)
provides:
  - 16 new Expecto tests (6 + 6 + 4) covering FAIL-01..FAIL-04
  - FailureDetectorTests.fs — JSONL parse/filter/multi-file/malformed-line tolerance
  - TeacherLabelerTests.fs — fake-Kestrel teacher endpoint + ROUTE_ parsing + cost cap + missing template
  - HardCaseDatasetTests.fs — single-write + dedupe + 50-concurrent integrity + graceful drain
  - Two adapter bug fixes uncovered by tests (CapCounter STJ + Flush/Dispose F# parsing trap)
affects: phase-08-retraining-loop, phase-09-canary-deployment

# Tech tracking
tech-stack:
  added: []
  patterns:
    - testSequenced for all Phase 7 tests (Console.SetOut races + temp dir/file isolation)
    - Fake-Kestrel teacher endpoint mirrors StreamingTests.startTestRouter pattern
    - Explicit BackgroundService lifecycle (StartAsync/StopAsync) without IHost wrapper for HardCaseDatasetTests
    - mkLabeler helper uses AddHttpClient(name).ConfigureHttpClient(...) — F# lambda does not bind reliably to AddHttpClient's Action<HttpClient> overload

key-files:
  created:
    - tests/SmartRouter.Tests/FailureDetectorTests.fs
    - tests/SmartRouter.Tests/TeacherLabelerTests.fs
    - tests/SmartRouter.Tests/HardCaseDatasetTests.fs
  modified:
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs
    - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs (capJsonOpts JsonFSharpConverter fix)
    - src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs (Flush/Dispose F# parsing trap fix)

key-decisions:
  - "Wave 3 sister-plan corrections committed under 07-06 because the tests revealed the bugs"
  - "AddHttpClient F# lambda binding requires .ConfigureHttpClient(...) chain, not the (name, configureClient) overload"
  - "Private [<CLIMutable>] F# records require JsonFSharpConverter for STJ deserialization (parameterless ctor not public)"

patterns-established:
  - "F# parsing trap: `try X with _ -> (); Y` only runs Y in the exception arm — split into two statements when both X and Y must always execute"
  - "Drain test pattern: Thread.Yield() before StopAsync exercises both steady-state and drain paths in BackgroundService"

# Metrics
duration: ~25 min (across two interrupted sessions)
completed: 2026-05-08
---

# Phase 7-06: Tests Summary

**16 new Expecto tests cover FAIL-01..FAIL-04 — JSONL filter, fake-Kestrel teacher endpoint with all four LabelResult cases, Channel + dedupe + 50-concurrent integrity for the dataset writer; two adapter bugs surfaced and fixed.**

## Performance

- **Duration:** ~25 min (across interrupted sessions)
- **Completed:** 2026-05-08T22:03:53Z
- **Tasks:** 3 test files + .fsproj + rootTests wiring + 2 fix commits
- **Files modified:** 7 (3 created + 4 modified)

## Accomplishments

- **FailureDetectorTests.fs (6 tests):** empty-directory, non-existent-directory, all-fallback-false, single-fallback-true → one HardCase, malformed-line tolerance, multi-file combine
- **TeacherLabelerTests.fs (6 tests):** ROUTE_35B → Labeled Route35B; ROUTE_122B → Labeled Route122B; chatty-no-sentinel → Unparseable; malformed JSON → Unparseable; missing prompt template → Skipped; pre-set cost cap → Skipped without HTTP call
- **HardCaseDatasetTests.fs (4 tests):** single AppendAsync writes one valid JSONL line; dedupe on (correlation_id, prompt_hash); 50 concurrent AppendAsync produces 50 valid non-interleaved lines; graceful StopAsync drains in-flight entries
- **Two real adapter bugs surfaced and fixed:** CapCounter STJ (private record + JsonFSharpConverter) + HardCaseDatasetWriter Flush/Dispose F# parsing trap

## Task Commits

1. **Test 1 — FailureDetectorTests.fs (6 tests)** — `f92145d` (test)
2. **Test 2 — TeacherLabelerTests.fs (6 tests with fake Kestrel)** — `7d202b6` (test)
3. **Test 3 — HardCaseDatasetTests.fs (4 tests with explicit BackgroundService lifecycle)** — `aa2cbd8` (test)
4. **Wire-up — Tests.fsproj + RouterTests.rootTests** — `f831b52` (test)
5. **Fix — adapter bugs (CapCounter STJ + Flush/Dispose pattern)** — `80aae85` (fix)
6. **Test refinement — HttpClient overload + drain timing** — `a0fc381` (test)

**Plan metadata:** pending — committed alongside this SUMMARY in `docs(07-06): complete tests plan`

## Files Created/Modified

- `tests/SmartRouter.Tests/FailureDetectorTests.fs` — 6 fixture-based JSONL tests (no HTTP, temp-dir isolation)
- `tests/SmartRouter.Tests/TeacherLabelerTests.fs` — 6 tests with fake-Kestrel teacher endpoint covering all 4 LabelResult cases
- `tests/SmartRouter.Tests/HardCaseDatasetTests.fs` — 4 tests with explicit `StartAsync`/`StopAsync` lifecycle (no IHost wrapper)
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — 3 new `<Compile>` entries before `RouterTests.fs`
- `tests/SmartRouter.Tests/RouterTests.fs` — `rootTests` augmented with the 3 new test module references (PITFALL-26: Expecto auto-discovery forbidden)
- `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` — `capJsonOpts` registers `JsonFSharpConverter()` for the private `[<CLIMutable>]` `CapCounter` record
- `src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` — `writer.Flush()` / `writer.Dispose()` split into two explicit `try/with` statements

## Decisions Made

- **AddHttpClient lambda overload trap:** the F# `fun c -> ...` lambda does not bind to the `Action<HttpClient>` overload of `AddHttpClient(name, configureClient)` reliably. Fix: `services.AddHttpClient("teacher").ConfigureHttpClient(fun c -> ...)`. The `BaseAddress` was sometimes left unset, causing tests to hit `localhost` instead of the fake-Kestrel port.
- **Private F# record + STJ:** `[<CLIMutable>]` makes the parameterless constructor available for STJ but it remains `private` when the type itself is `private`. STJ's default `ObjectDefaultConverter` uses public reflection and fails. `JsonFSharpConverter` uses `Microsoft.FSharp.Reflection` which sees private members.
- **F# parsing trap on `try/with` semicolons:** `fun w -> try w.Flush() with _ -> (); w.Dispose()` parses as `try ... with _ -> ((); w.Dispose())` — `Dispose()` only runs on Flush failure. Always split into separate statements when both must execute.
- **Drain test timing:** `Thread.Yield()` between AppendAsync calls and `StopAsync` lets the consumer process some items before drain, exercising both the steady-state and the drain phase. Without it, all items remain in the channel and only the drain phase is tested.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Test discovery surfaced two adapter bugs**
- **Found during:** Test 2 (TeacherLabelerTests, daily cost-cap path) and Test 3 (HardCaseDatasetTests, graceful drain)
- **Issue:**
  - `TeacherLabeler.capJsonOpts` could not deserialize `CapCounter` because the private `[<CLIMutable>]` record is not visible to STJ's default reflection.
  - `HardCaseDatasetWriter` writer disposal: `try w.Flush() with _ -> (); w.Dispose()` only invoked `Dispose()` inside the `with` clause, leaking the `StreamWriter` in the happy path.
- **Fix:** Add `JsonFSharpConverter()` to `capJsonOpts.Converters`; split disposal into two explicit `try/with` statements.
- **Files modified:** `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs`, `src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs`
- **Verification:** Full suite — 66 passed + 10 ignored, 0 failed.
- **Committed in:** `80aae85`

**2. [Rule 1 - Bug] Test refinements for binding + timing**
- **Found during:** Test 2 + Test 3 stabilization
- **Issue:** F# lambda overload binding (HttpClient BaseAddress unset) and graceful-drain test only covered the drain phase.
- **Fix:** `.ConfigureHttpClient(...)` chain; `Thread.Yield()` between AppendAsync and StopAsync.
- **Files modified:** `tests/SmartRouter.Tests/TeacherLabelerTests.fs`, `tests/SmartRouter.Tests/HardCaseDatasetTests.fs`
- **Committed in:** `a0fc381`

---

**Total deviations:** 2 auto-fixed (both Rule 1 — bugs surfaced by the tests they shipped)
**Impact on plan:** Tests served their purpose — flushed two real adapter bugs into the open before Phase 8 consumes the writer.

## Issues Encountered

- Session-interrupted execution: 07-06 originally spawned alongside 07-05 in Wave 3 but the executor agent's session ended before committing the two follow-up fixes and writing SUMMARY.md. The orchestrator picked up the uncommitted fixes (`git status` showed modified `TeacherLabeler.fs` / `HardCaseDatasetWriter.fs` / `TeacherLabelerTests.fs` / `HardCaseDatasetTests.fs`), inspected the diffs, ran the test suite (66 pass + 10 ignored), and committed the fixes under `fix(07-06):` and `test(07-06):` to close out the plan.

## Test Count Delta

- Before Phase 7: 50 passed + 10 ignored
- After 07-06: **66 passed + 10 ignored, 0 failed** (+16 tests = 6 + 6 + 4)
- Build: 0 errors, 0 warnings (`TreatWarningsAsErrors=true` everywhere)

## Next Phase Readiness

- All FAIL-01..FAIL-04 must-haves test-covered.
- Phase 8 (Retraining Loop) can consume `IHardCaseDatasetWriter` + `IFailureDetector` + `ITeacherLabeler` directly via DI (triple-registration in CompositionRoot).
- `dotnet run --project src/SmartRouter.Cli -- --retrain` is the operator-driven offline pipeline; `dotnet fsi scripts/seed-hard-cases.fsx` primes synthetic data when fallback_used is dormant (current state until Phase 10).
- No blockers.

---
*Phase: 07-failure-detection-and-teacher-labeling*
*Completed: 2026-05-08*
