---
phase: 11-deployment-documentation
plan: 01
subsystem: api
tags: [fsharp, aspnetcore, json, healthprobe, models-endpoint, deduplication, integration-tests]

# Dependency graph
requires:
  - phase: 10-health-fallback
    provides: IHealthProbe.IsReachable + health-probe named HttpClient already wired in DI
  - phase: 01-foundation
    provides: UpstreamOptions, IHttpClientFactory, QwenUpstreamClient, Adapters.Json.jsonOptions
provides:
  - GET /v1/models endpoint with parallel upstream fetch + IHealthProbe gating + id dedupe
  - 3 integration tests (MODELS-01..03) verifying both-up/dedupe, one-down, both-down scenarios
affects:
  - 11-02 (launchd plist references /v1/models in README health probe semantics)
  - 11-03 (README endpoint table includes /v1/models)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "JsonElement.Clone() lifetime guard — mandatory before use doc exits to prevent silent data corruption"
    - "IHealthProbe.IsReachable sync fast-path gates HTTP fetch — avoids 5s timeout on known-down upstreams"
    - "Last-registration-wins DI override for StubHealthProbe in integration tests (no FakeHealthProbe service replacement needed)"
    - "Task.WhenAll parallel fetch with per-upstream Task.FromResult [] short-circuit for unreachable upstreams"

key-files:
  created:
    - src/SmartRouter.Cli/Endpoints/Models.fs
    - tests/SmartRouter.Tests/ModelsTests.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/Program.fs
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    - tests/SmartRouter.Tests/RouterTests.fs

key-decisions:
  - "/v1/models reuses existing health-probe named HttpClient (5s timeout, no retry, no BaseAddress) — L13, no 12th named client"
  - "JsonElement.Clone() called before use doc exits — L14 silent-corruption guard, non-negotiable"
  - "Both-upstreams-down returns 200 + empty data array — L11, mirrors /stats graceful-degradation (503 reserved for actual server errors)"
  - "StubHealthProbe registered after configureServices — last-registration-wins DI override, deterministic without probe-cycle timing"
  - "Pure-Core invariant preserved — zero src/SmartRouter.Core/ changes"

patterns-established:
  - "JsonElement.Clone() pattern: every JsonElement crossing a use doc boundary must be cloned"
  - "StubHealthProbe DI override: register after configureServices for deterministic test control without real probe cycle"

# Metrics
duration: 8min
completed: 2026-05-09
---

# Phase 11 Plan 01: Models Endpoint Summary

**GET /v1/models with parallel upstream fetch, IHealthProbe.IsReachable gating, id-only dedupe (first-seen wins), JsonElement.Clone() lifetime guard, and 200+empty on both-down — 3 integration tests confirm all three scenarios**

## Performance

- **Duration:** 8 min
- **Started:** 2026-05-09T03:26:15Z
- **Completed:** 2026-05-09T03:35:13Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- Implemented `Endpoints/Models.fs` with parallel fetch via `Task.WhenAll`, `IHealthProbe.IsReachable` gating per upstream (skips 5s timeout on known-dead upstreams), id-only dedupe with `HashSet<string>` first-seen wins, `JsonElement.Clone()` lifetime guard before `use doc` exits, and 200+empty-data graceful degradation when both upstreams are down
- Wired endpoint into `Program.fs` and `SmartRouter.Cli.fsproj` with zero new DI registrations (reuses existing `health-probe` named client, `IHealthProbe`, and `IOptions<UpstreamOptions>`)
- Added 3 integration tests in `ModelsTests.fs`: MODELS-01 (both-up + dedupe → 3 entries), MODELS-02 (one-down via `StubHealthProbe(false, true)` → 1 entry), MODELS-03 (both-down → 200 + empty array)
- Test count: 83 → 86 passed, 17 ignored unchanged, 0 failed

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Endpoints/Models.fs + wire into Program.fs + SmartRouter.Cli.fsproj** - `ec163ad` (feat)
2. **Task 2: Create ModelsTests.fs + wire into RouterTests.rootTests + SmartRouter.Tests.fsproj** - `b7a94e1` (test)

**Plan metadata:** (staged in this final commit) (docs: complete models-endpoint plan)

## Files Created/Modified

- `src/SmartRouter.Cli/Endpoints/Models.fs` (NEW, 100 lines) — GET /v1/models implementation with parallel fetch, IHealthProbe gating, dedupe, Clone() guard
- `tests/SmartRouter.Tests/ModelsTests.fs` (NEW, 208 lines) — 3 integration tests MODELS-01..03 with StubHealthProbe + startFakeUpstream
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` (+1 line) — `<Compile Include="Endpoints/Models.fs" />` after Health.fs
- `src/SmartRouter.Cli/Program.fs` (+2 lines) — `SmartRouter.Cli.Endpoints.Models.mapEndpoints app` registration
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` (+2 lines) — `<Compile Include="ModelsTests.fs" />` after HealthFallbackTests.fs
- `tests/SmartRouter.Tests/RouterTests.fs` (+1 line) — `SmartRouter.Tests.ModelsTests.tests` appended to rootTests

## Decisions Made

- **health-probe HttpClient reuse (L13):** Used `factory.CreateClient("health-probe")` — the existing 5s timeout, no-retry, no-BaseAddress client is correct for `/v1/models`. Zero new named clients added.
- **JsonElement.Clone() (L14):** Mandatory — `use doc = JsonDocument.Parse(...)` disposes memory backing all extracted `JsonElement`s when the function returns; `.Clone()` creates independently-owned copies. Without this, the dedupe loop dereferences freed memory silently.
- **200 on both-down (L11):** Returns `{"object":"list","data":[]}` not 503. 503 is reserved for actual router-side server errors; missing upstream data is graceful degradation.
- **StubHealthProbe pattern:** Registered with `services.AddSingleton<IHealthProbe>(stub)` after `configureServices` — last-registration-wins DI makes tests deterministic without waiting for the real HealthService probe cycle to converge.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed FS0597 consecutive-args on GetArrayLength() calls in test assertions**
- **Found during:** Task 2 (build after creating ModelsTests.fs)
- **Issue:** `Expect.equal data.GetArrayLength() 3 "msg"` — F# parser sees `data.GetArrayLength` and `()` as separate arguments to `Expect.equal`, triggering FS0597
- **Fix:** Wrapped call in parens: `Expect.equal (data.GetArrayLength()) 3 "msg"` (3 occurrences)
- **Files modified:** tests/SmartRouter.Tests/ModelsTests.fs
- **Verification:** Build succeeded with 0 errors after fix
- **Committed in:** b7a94e1 (Task 2 commit)

**2. [Rule 1 - Bug] Fixed IsReachableAsync interface implementation (curried vs tuple)**
- **Found during:** Task 2 (FS0856 build error)
- **Issue:** Plan's stub wrote `member _.IsReachableAsync(target, ct)` (tuple form), but `IHealthProbe` interface declares `abstract member IsReachableAsync : target : ModelId -> ct : CancellationToken -> Task<bool>` (curried)
- **Fix:** Changed to `member _.IsReachableAsync target _ct =` (curried form matching the interface)
- **Files modified:** tests/SmartRouter.Tests/ModelsTests.fs
- **Verification:** Build succeeded with 0 errors after fix
- **Committed in:** b7a94e1 (Task 2 commit)

**3. [Rule 1 - Bug] Dropped builder.Logging.ClearProviders() (extension method not in scope)**
- **Found during:** Task 2 (FS0039 build error — ILoggingBuilder has no ClearProviders without Microsoft.Extensions.Logging open)
- **Issue:** `ClearProviders()` is an extension method from `Microsoft.Extensions.Logging` namespace; test project doesn't import it (and plan notes it's cosmetic)
- **Fix:** Removed the `builder.Logging.ClearProviders() |> ignore` line entirely (plan explicitly said to drop it if it caused a resolution issue)
- **Files modified:** tests/SmartRouter.Tests/ModelsTests.fs
- **Verification:** Build succeeded; fake upstreams work correctly without suppressed logging
- **Committed in:** b7a94e1 (Task 2 commit)

---

**Total deviations:** 3 auto-fixed (all Rule 1 — bugs in verbatim plan code)
**Impact on plan:** All three fixes necessary for compilation. No scope change. Plan code was validated in isolation but had interface-signature mismatches and F# syntax issues that surface only when compiled against the actual project.

## Build Status

```
dotnet build SmartRouter.slnx -nologo --tl:off
→ Build succeeded. 0 warnings, 0 errors.
```

## Test Count

| Scenario | Without embeddings | With embeddings |
|---|---|---|
| Pre-Phase 11 baseline | 83 passed + 17 ignored | 90 passed + 10 ignored |
| Post-Plan 11-01 | **86 passed + 17 ignored** | 93 passed + 10 ignored |
| Delta | +3 | +3 |

## Lock Compliance

| Lock | Status |
|---|---|
| L11 — both-down returns 200 + empty data array | CONFIRMED |
| L12 — dedupe by id only, first-seen wins (HashSet<string>) | CONFIRMED |
| L13 — reuses health-probe named HttpClient, no new named client | CONFIRMED |
| L14 — JsonElement.Clone() before use doc exits | CONFIRMED (grep returns 3 hits — 1 in fetchModels loop, 2 would be in docs but actual is 1 in acc.Add) |
| ARCH-01 — Pure-Core invariant | CONFIRMED (`git diff --stat src/SmartRouter.Core/` = 0 changes) |

## Pure-Core Invariant

```
git diff --name-only src/SmartRouter.Core/ | wc -l → 0
```

Zero Core changes. All Phase 11-01 work is purely additive in the Cli layer.

## Issues Encountered

None beyond the 3 auto-fixed compilation issues documented above.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- Phase 11-02 (launchd plist + deploy scripts) is unblocked — depends on this plan's `/v1/models` endpoint existing
- Phase 11-03 (README) is unblocked — can reference `/v1/models` in endpoint table
- Both 11-02 and 11-03 are file-disjoint; safe to run in parallel (Wave 2)
- No blockers or concerns

---
*Phase: 11-deployment-documentation*
*Completed: 2026-05-09*
