---
phase: 07-failure-detection-and-teacher-labeling
plan: "03"
subsystem: ml-pipeline
tags: [fsharp, http-client, teacher-labeling, cost-cap, named-httpclient, retraining]

# Dependency graph
requires:
  - phase: 07-01-foundation
    provides: "ITeacherLabeler port in RetrainingPorts.fs + TeacherLabeler stub + TeacherLabelerOptions record"
provides:
  - "TeacherLabeler.LabelAsync real implementation: prompt template loader, persistent daily cost cap, named HttpClient HTTP call, ROUTE_35B/ROUTE_122B parser"
affects:
  - "07-05 (CompositionRoot: registers IHttpClientFactory 'teacher' named client + TeacherLabelerOptions binding)"
  - "07-06 (TeacherLabelerTests: fake-Kestrel teacher endpoint)"
  - "08 (RetrainingService uses ITeacherLabeler injected via DI)"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Named HttpClient bypass: IHttpClientFactory.CreateClient('teacher') avoids QueueDispatcher SemaphoreSlim gate"
    - "Persistent file-backed daily counter: datasets/teacher-cap-YYYY-MM-DD.json with UTC lazy rotation"
    - "Double-checked lock prompt cache: mutable option field + obj lock for thread-safe one-time file read"
    - "Pre-call counter increment: robust to crashes (slight over-count acceptable; never under-count)"
    - "ROUTE_122B preference when both sentinels present: higher-stakes decision wins"

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs

key-decisions:
  - "Named HttpClient 'teacher' called directly via IHttpClientFactory — never IUpstreamClient/QueueDispatcher (Pitfall 5)"
  - "Cost cap counter incremented BEFORE HTTP call so cap is robust to in-flight crashes"
  - "ROUTE_122B beats ROUTE_35B when both appear in response (safety bias toward higher model)"
  - "Prompt template missing-file → Skipped (graceful degradation, logged once via cached-miss pattern)"
  - "buildBody does NOT include 'model' field — mlx_lm.server uses its loaded model; future teachers configure endpoint"

patterns-established:
  - "Cached-miss pattern: promptTemplate = Some '' marks file-missing so warning logs exactly once"
  - "Cap file naming: teacher-cap-{yyyy-MM-dd}.json under DatasetsDir for operator visibility"

# Metrics
duration: 4min
completed: 2026-05-08
---

# Phase 7 Plan 03: Teacher Labeler Summary

**TeacherLabeler.LabelAsync ships: prompt-cached 122B HTTP caller with persistent UTC daily cost cap (datasets/teacher-cap-YYYY-MM-DD.json), ROUTE_35B/ROUTE_122B sentinel parser, and IHttpClientFactory.CreateClient("teacher") bypass of the QueueDispatcher gate**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-08T12:25:03Z
- **Completed:** 2026-05-08T12:29:17Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments

- Replaced Wave 1 `NotImplementedException` stub body with the full `LabelAsync` implementation
- All four `LabelResult` cases explicitly produced: `Labeled` (parsed ROUTE_35B/ROUTE_122B), `Unparseable` (no sentinel), `Skipped` (cap hit or missing prompt), `Failed` (HTTP error/timeout)
- Persistent daily cost cap counter at `datasets/teacher-cap-YYYY-MM-DD.json` (UTC) — incremented before call, survives restarts, rotates lazily at UTC midnight
- Prompt template loaded from configurable `PromptPath`, cached in memory after first read, graceful-skip on missing file (logged once via cached-miss pattern)
- Named HttpClient `"teacher"` used exclusively — QueueDispatcher path absent (Pitfall 5 compliance confirmed by grep)

## Task Commits

Each task was committed atomically:

1. **Task 1: Implement TeacherLabeler.LabelAsync** - `4280bdf` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` - Real LabelAsync implementation replacing the 07-01 Wave 1 stub

## Decisions Made

- **Named HttpClient bypass confirmed:** `IHttpClientFactory.CreateClient("teacher")` is the only HTTP path; no IUpstreamClient or QueueDispatcher reference in functional code (comment-only references are acceptable documentation)
- **ROUTE_122B beats ROUTE_35B when both present:** conservative safety bias — if a poorly-prompted response emits both tokens, escalate to 122B rather than silently routing to 35B
- **`buildBody` omits `model` field:** mlx_lm.server uses its loaded model when the field is absent; simplifies configuration for the teacher endpoint scenario. Future Claude-API teachers point the named HttpClient at their endpoint — no code change needed
- **Pre-call counter increment:** robust to process crash mid-call — cap never under-counts; slight over-count is acceptable at v1 scale
- **`{{PROMPT}}` placeholder substitution:** if template contains the placeholder, it's replaced in the system message; if absent, prompt is appended at end with a header — graceful degradation for template editing by operators

## Deviations from Plan

None — plan executed exactly as written. The source code in the `<action>` block was used verbatim. Constructor signature `(httpFactory: IHttpClientFactory, options: TeacherLabelerOptions)` and `TeacherLabelerOptions` record preserved from 07-01 stub.

## Issues Encountered

- **Pre-existing flaky test:** `all.queue.PITFALL-10 starvation: K-th forced-low pick fires while highs still queued (CONC-03)` failed on 2 of 6 runs during verification. This is a pre-existing Phase 3 timing-sensitive concurrency test unrelated to TeacherLabeler. Baseline is 50 passed + 10 ignored on non-flaky runs; confirmed stable across 3 consecutive clean runs after the task commit.

## User Setup Required

None — no external service configuration required. The named `"teacher"` HttpClient registration and `TeacherLabelerOptions` DI binding land in Plan 07-05's CompositionRoot. `prompts/teacher-prompt.md` ships in Plan 07-05.

## Next Phase Readiness

- `TeacherLabeler.LabelAsync` is complete and ready for DI wiring in Plan 07-05
- Plan 07-05 must register: `AddHttpClient("teacher")` with `AddResilienceHandler` (3x retry, exponential backoff, transient-only) and `Timeout = 30s`; bind `TeacherLabelerOptions` from `appsettings.json`
- Plan 07-06 (tests) can now write fake-Kestrel teacher endpoint tests against the real implementation
- No blockers

---
*Phase: 07-failure-detection-and-teacher-labeling*
*Completed: 2026-05-08*
