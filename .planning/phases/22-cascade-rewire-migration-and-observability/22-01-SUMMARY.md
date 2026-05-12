---
phase: 22-cascade-rewire-migration-and-observability
plan: "01"
subsystem: session-extraction
tags: [fsharp, session-cascade, observability, interlocked, stats, tier-resolution]

# Dependency graph
requires:
  - phase: 21-hsp-and-cfp-extraction-primitives
    provides: HermesSessionExtract.extractFromSystemPrompt (Tier 2 sysprompt parse) + ContentFingerprint.compute (Tier 3 SHA-256 fingerprint)
provides:
  - ISessionCascadeStats interface + SessionCascadeStats concrete adapter (OBS-01 counters)
  - 3-tier session-key cascade wired in ChatCompletions.fs handler (TIER-01..03)
  - resolveSessionCascade pure helper module function (unit-testable without Kestrel)
  - ISessionCascadeStats DI-registered in BOTH configureRequestPipeline AND configureWithoutMl (TIER-04)
  - 3 new session_extraction_source_* int64 fields on GET /stats (OBS-01)
affects: [22-02, 22-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Unconditional DI registration (NOT mode-gated) for observability counters that apply across all Routing.Mode values"
    - "Pure module-level helper extracted for unit testability: resolveSessionCascade : RouterRequest -> string * string"
    - "Three explicit RecordX() methods on ISessionCascadeStats (NOT string dispatch) — mirrors IQualityCheckStats precedent"

key-files:
  created:
    - src/SmartRouter.Cli/Adapters/SessionCascadeStats.fs
  modified:
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
    - src/SmartRouter.Cli/Endpoints/Stats.fs

key-decisions:
  - "TIER-03 location: inline in ChatCompletions.fs handler at lines 248-254 boundary; no new helper module, no middleware change"
  - "SessionCascadeStats applies to BOTH Routing.Mode values; same concrete class shipped in both DI pipelines (no NoOp)"
  - "StatsWire field placement: APPEND at end of record after selfrouter_skipped (stable JSON ordering)"
  - "resolveSessionCascade placed BEFORE handler function definition (F# forward-reference fix)"
  - "Fully-qualified module references for HermesSessionExtract and ContentFingerprint in resolveSessionCascade (open statements bring members into scope, not module names)"

patterns-established:
  - "Unconditional DI registration: unlike ISelfRouter (mode-gated), ISessionCascadeStats uses same concrete in both pipelines"
  - "Pure helper before handler: module-level let resolveSessionCascade extracted above let handler for F# compile-order correctness"
  - "Stats null-safe guard: GetService<ISessionCascadeStats>() with isNull zero-fallback mirrors established selfRouterStats/judgeStats pattern"

# Metrics
duration: 9min
completed: 2026-05-12
---

# Phase 22 Plan 01: Cascade Rewire + Observability Summary

**Three-tier session cascade (header → sysprompt → SHA-256 fingerprint) wired live in ChatCompletions.fs with ISessionCascadeStats DI singleton exposing session_extraction_source_* counters on /stats**

## Performance

- **Duration:** 9 min
- **Started:** 2026-05-12T04:05:15Z
- **Completed:** 2026-05-12T04:15:13Z
- **Tasks:** 3 code + 1 summary = 4 total
- **Files modified:** 5 (1 new + 4 modified)

## Accomplishments
- New `ISessionCascadeStats` port + `SessionCascadeStats` concrete adapter with Interlocked.Increment + Volatile.Read lock-free pattern (OBS-01)
- `resolveSessionCascade` pure module-level helper (Tier 1 header → Tier 2 sysprompt → Tier 3 SHA-256 fingerprint) placed above `handler` function for F# compile-order correctness
- Cascade block inserted between `mapWireToRequest` and `routeRequest` in ChatCompletions.fs handler — both streaming and non-streaming branches see the resolved session key (TIER-01..03)
- `ISessionCascadeStats` registered in BOTH `configureRequestPipeline` AND `configureWithoutMl` DI pipelines (TIER-04)
- 3 new flat snake_case int64 fields (`session_extraction_source_header`, `_sysprompt`, `_content`) appended to `/stats` response (OBS-01)
- All 188 tests continue passing (cascade runs on every request but Tier 1 always wins in existing fixtures since they set explicit SessionId; no test regressions)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add SessionCascadeStats adapter** - `5e04118` (feat)
2. **Task 2: Wire 3-tier session cascade in ChatCompletions handler** - `fe5c3f0` (feat)
3. **Task 3: Expose session cascade counters via /stats** - `814b46e` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/SmartRouter.Cli/Adapters/SessionCascadeStats.fs` - NEW: ISessionCascadeStats interface + SessionCascadeStats concrete class with Interlocked counters
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - Added `<Compile Include="Adapters/SessionCascadeStats.fs" />` after ContentFingerprint.fs (PITFALL-7 compile order)
- `src/SmartRouter.Cli/CompositionRoot.fs` - DI registration pair in both configureRequestPipeline and configureWithoutMl (TIER-04)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` - resolveSessionCascade helper + cascade block + ISessionCascadeStats resolution + req shadowing
- `src/SmartRouter.Cli/Endpoints/Stats.fs` - 3 new StatsWire fields + snapshotToWireFields defaults + mapEndpoints null-safe resolve + wire construction

## Decisions Made
- **TIER-03 location confirmed:** Inline in ChatCompletions.fs handler at the mapWireToRequest/routeRequest boundary. Middleware rejected (runs pre-body-parse). Separate module rejected (adds file for ~5 lines of logic).
- **resolveSessionCascade before handler:** F# requires definitions before their use sites. The helper must be placed before `let handler` (not before `let mapEndpoints` as the plan originally suggested) because `handler` is the use site.
- **Fully-qualified module references:** `open SmartRouter.Cli.Adapters.HermesSessionExtract` brings `extractFromSystemPrompt` into direct scope (not `HermesSessionExtract.extractFromSystemPrompt`). Used `SmartRouter.Cli.Adapters.HermesSessionExtract.extractFromSystemPrompt req` and `SmartRouter.Cli.Adapters.ContentFingerprint.compute req` for clarity and to avoid any name conflict with `compute` (ContentFingerprint also exports `compute` which could shadow local names).
- **Unconditional DI registration:** Unlike SelfRouter (mode-gated), SessionCascadeStats ships in both Routing.Mode values with the same concrete class. No NoOp needed — the class is 3 int64 fields.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] resolveSessionCascade placed before `handler` not before `mapEndpoints`**
- **Found during:** Task 2 (ChatCompletions.fs cascade wiring)
- **Issue:** Plan instruction placed the helper "just above mapEndpoints". In F#, `let handler` at line ~200 uses `resolveSessionCascade` inside its `task {}` body — so the helper must be defined BEFORE `handler`, not before `mapEndpoints` (which comes after `handler`).
- **Fix:** Inserted `resolveSessionCascade` above the `// ── Handler ──` section comment and before `let handler`.
- **Files modified:** `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`
- **Verification:** `dotnet build` passed with 0 warnings. FS0039 error for undefined `resolveSessionCascade` was the diagnostic.
- **Committed in:** `fe5c3f0` (Task 2 commit)

**2. [Rule 3 - Blocking] Fully-qualified module references for HermesSessionExtract and ContentFingerprint**
- **Found during:** Task 2 (ChatCompletions.fs cascade wiring)
- **Issue:** `open SmartRouter.Cli.Adapters.HermesSessionExtract` brings `extractFromSystemPrompt` directly into scope but does NOT make the `HermesSessionExtract` module name available as a qualifier. Using `HermesSessionExtract.extractFromSystemPrompt` fails with FS0039. Same for `ContentFingerprint.compute`.
- **Fix:** Used fully-qualified references `SmartRouter.Cli.Adapters.HermesSessionExtract.extractFromSystemPrompt req` and `SmartRouter.Cli.Adapters.ContentFingerprint.compute req` in the `resolveSessionCascade` helper.
- **Files modified:** `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`
- **Verification:** `dotnet build` passed with 0 warnings after using fully-qualified names.
- **Committed in:** `fe5c3f0` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 — blocking compile errors)
**Impact on plan:** Both fixes are F# language mechanics — not scope changes. Behavior is identical to the plan's intent. No scope creep.

## Issues Encountered
- Initial placement of `resolveSessionCascade` after `mapEndpoints` caused FS0039 on `resolveSessionCascade` (undefined forward reference in `handler` body). Fixed by moving helper before `handler`.
- F# module `open` semantics: `open M.N.Foo` brings `Foo`'s members directly into scope but not the module name `Foo` itself. Addressed by fully-qualifying module references.

## User Setup Required
None - no external service configuration required. Cascade runs transparently on every request.

## Next Phase Readiness
- Plan 22-02 can proceed: cascade is operational and verified (188 tests green). HMRS-02 IP+UA fingerprint code remains in tree (unchanged) for 22-02 to delete cleanly.
- Plan 22-03 can write SessionKeyCascadeTests.fs against `resolveSessionCascade` (now module-visible, not `private`) and assert OBS-01 counter increments.
- Phase 23 (DOC-01..04): README §8 `/stats` counter table update and §10 Hermes Integration rewrite ready to proceed once 22-02 and 22-03 land.

---
*Phase: 22-cascade-rewire-migration-and-observability*
*Completed: 2026-05-12*
