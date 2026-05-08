---
phase: 07-failure-detection-and-teacher-labeling
plan: "05"
subsystem: infra
tags: [fsharp, dotnet, di, polly, resilience, http-client, cli, retraining]

# Dependency graph
requires:
  - phase: 07-01-foundation
    provides: RetrainingPorts.fs interfaces (IFailureDetector, ITeacherLabeler, IHardCaseDatasetWriter)
  - phase: 07-02-failure-detector
    provides: FailureDetector adapter (reads JSONL decision logs, returns HardCase list)
  - phase: 07-03-teacher-labeler
    provides: TeacherLabeler adapter (HTTP to 122B, cost cap, retry, ROUTE_35B/ROUTE_122B parsing)
  - phase: 07-04-hard-case-dataset-writer
    provides: HardCaseDatasetWriter adapter (Channel + BackgroundService, dedupe, JSONL append)
provides:
  - CompositionRoot DI registrations for all three Phase 7 adapters
  - Named HttpClient "teacher" with explicit ShouldHandle resilience handler (4xx-skip)
  - Program.fs --retrain CLI command (offline pipeline, exits before Kestrel startup)
  - prompts/teacher-prompt.md (operator-editable teacher prompt template, committed to repo)
  - scripts/seed-hard-cases.fsx (30 synthetic Korean+English hard cases for Phase 8 testing)
  - datasets/ added to .gitignore
affects: [08-retraining-service, phase-8-background-service, phase-9-canary]

# Tech tracking
tech-stack:
  added:
    - Microsoft.Extensions.Http.Resilience 10.5.0 — AddResilienceHandler with explicit ShouldHandle predicate (already pinned in Cli.fsproj)
    - Polly.Core 8.4.2 — RetryStrategyOptions, DelayBackoffType, ResiliencePipelineBuilder<T> (transitive)
  patterns:
    - Named HttpClient "teacher" bypasses QueueDispatcher semaphore (no 122B slot starvation — CONTEXT.md Pitfall 5)
    - ML block in configureServices guarded by Routing.Algorithm = "ml" — prevents ensureEmbeddingFilesPresent from firing in heuristic/retrain modes
    - --retrain offline host: Host.CreateApplicationBuilder with Routing:Algorithm=heuristic override
    - Triple-registration pattern (Concrete + Interface + AddHostedService) applied to HardCaseDatasetWriter — mirrors Phase 5 DecisionLogWriter

key-files:
  created:
    - prompts/teacher-prompt.md
    - scripts/seed-hard-cases.fsx
    - .planning/phases/07-failure-detection-and-teacher-labeling/07-05-SUMMARY.md
  modified:
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
    - src/SmartRouter.Cli/appsettings.json
    - .gitignore

key-decisions:
  - "Named HttpClient 'teacher' registered independently — NOT through QueueDispatcher (prevents 122B semaphore starvation)"
  - "AddResilienceHandler with explicit ShouldHandle predicate: retry HttpRequestException/TaskCanceledException/5xx only; explicitly NEVER retry 4xx (FAIL-02)"
  - "ML block in configureServices guarded on Routing.Algorithm='ml' — offline --retrain path overrides to 'heuristic' to bypass model file check"
  - "prompts/teacher-prompt.md copied verbatim from ~/projs/smart-router-distillation/idea/prompts/teacher_prompt.md (file found, not plan default)"
  - "datasets/ gitignored (training data not committed); prompts/ NOT gitignored (small text file, operator-editable)"

patterns-established:
  - "Phase 7 DI: FailureDetector reads logs/decisions/ dir from IOptions<DecisionLogOptions> — single source of truth for the path"
  - "Offline retrain host: Host.CreateApplicationBuilder + heuristic override + host.StartAsync/StopAsync wrapping pipeline"

# Metrics
duration: ~18min
completed: 2026-05-08
---

# Phase 7 Plan 05: CLI-WIRING Summary

**Three Phase 7 adapters wired into DI with named "teacher" HttpClient (Polly 3x retry, explicit 4xx-skip ShouldHandle), --retrain offline CLI handler in Program.fs (exits before Kestrel), and operator-committed teacher prompt + 30-entry synthetic seed script**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-05-08T21:20:00Z
- **Completed:** 2026-05-08T21:38:00Z
- **Tasks:** 3
- **Files modified:** 6 (3 modified + 2 created + 1 modified .gitignore)

## Accomplishments

- All three Phase 7 adapters (FailureDetector, TeacherLabeler, HardCaseDatasetWriter) registered as DI singletons with interface aliases; HardCaseDatasetWriter triple-registered (mirrors DecisionLogWriter Phase 5 pattern)
- Named "teacher" HttpClient registered with `AddResilienceHandler` — explicit `ShouldHandle` predicate retries only on HttpRequestException/TaskCanceledException/5xx, never on 4xx (FAIL-02 compliance)
- `--retrain` CLI branch added to Program.fs: builds minimal Host (no Kestrel), resolves the 3 ports, iterates hard cases, labels each via teacher, appends entries; exits 0; friendly empty-set message
- `prompts/teacher-prompt.md` copied verbatim from `~/projs/smart-router-distillation/idea/prompts/teacher_prompt.md` and committed to repo (operator-editable)
- `scripts/seed-hard-cases.fsx` writes 30 synthetic entries (Korean+English mixed, technical+casual, ~50/50 labels) to `datasets/hard-cases.jsonl`

## Task Commits

Each task was committed atomically:

1. **Task 1: CompositionRoot DI wiring + named 'teacher' HttpClient** - `88f367a` (feat)
2. **Task 2: Program.fs --retrain handler** - `d74027f` (feat)
3. **Task 3: prompts/teacher-prompt.md + scripts/seed-hard-cases.fsx** - `0f7a6c5` (chore)

**Plan metadata:** (docs commit — see below)

## Files Created/Modified

- `src/SmartRouter.Cli/CompositionRoot.fs` — Added 7 new imports + TeacherLabelerOptions/HardCaseDatasetOptions bindings + named "teacher" HttpClient with AddResilienceHandler + FailureDetector/TeacherLabeler/HardCaseDatasetWriter triple-registration; added ML block algorithm guard (deviation auto-fix)
- `src/SmartRouter.Cli/Program.fs` — Added `open Microsoft.Extensions.Hosting`; added --retrain branch before WebApplication.CreateBuilder with full offline pipeline + heuristic override
- `src/SmartRouter.Cli/appsettings.json` — Added TeacherLabeler and HardCaseDataset sections between DecisionLog and Serilog
- `.gitignore` — Added `datasets/` entry after `models/` block
- `prompts/teacher-prompt.md` (NEW) — Verbatim copy of teacher prompt template from smart-router-distillation project
- `scripts/seed-hard-cases.fsx` (NEW) — 30 synthetic hard-case entries (Korean+English, balanced labels), writes to `datasets/hard-cases.jsonl`

## Decisions Made

- **Teacher prompt source:** File found at `~/projs/smart-router-distillation/idea/prompts/teacher_prompt.md`; copied verbatim (not using plan's default). Content is identical to plan default (same wording).
- **Named HttpClient registration:** Used explicit `ShouldHandle` predicate on `HttpRetryStrategyOptions` instead of `AddStandardResilienceHandler` default — makes 4xx-skip behavior visible at the registration site and aligned with locked FAIL-02 spec.
- **ML block algorithm guard:** configureServices now checks `Routing.Algorithm = "ml"` before calling `ensureEmbeddingFilesPresent` — see Deviations section.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] ML model file check fires unconditionally, blocking --retrain execution**

- **Found during:** Task 2 (Program.fs --retrain handler verification)
- **Issue:** `configureServices` calls `ensureEmbeddingFilesPresent` whenever `Routing.ML` section exists in appsettings.json, regardless of `Routing.Algorithm`. The `--retrain` offline path reuses `configureServices` (same DI per plan intent) but doesn't need IEmbedder/IClassifier. In dev/CI environments where `scripts/download-models.sh` hasn't been run, the hard-fail blocks --retrain.
- **Fix:**
  1. Added `routingAlgoStr` local variable in `configureServices` that reads `Routing.Algorithm` from config
  2. Changed ML block guard from `if not (obj.ReferenceEquals(mlOpts, null)) then` to `if not (obj.ReferenceEquals(mlOpts, null)) && routingAlgoStr = "ml" then`
  3. In the `--retrain` branch in Program.fs, inject `AddInMemoryCollection(dict ["Routing:Algorithm", "heuristic"])` override so the ML block is skipped
- **Files modified:** `src/SmartRouter.Cli/CompositionRoot.fs`, `src/SmartRouter.Cli/Program.fs`
- **Verification:** `dotnet run --project src/SmartRouter.Cli -- --retrain` runs to completion printing "Retrain: 0 hard cases found; run scripts/seed-hard-cases.fsx for synthetic data." with exit code 0; "Now listening on.*4000" not present in output (Kestrel not started)
- **Committed in:** `d74027f` (Task 2 commit)

**2. [Rule 1 - Bug] `use` binding at script module level causes FS0524 warning**

- **Found during:** Task 3 (scripts/seed-hard-cases.fsx)
- **Issue:** `use sw = new StreamWriter(...)` at top level of .fsx script triggers FS0524 "use binding in a module is treated as let binding" — produces a warning but TreatWarningsAsErrors doesn't apply to fsi scripts; fixed anyway for clean output
- **Fix:** Wrapped the loop body in a `writeEntries ()` function with the `use sw` binding inside; returned count; called from top level with `let count = writeEntries ()`
- **Files modified:** `scripts/seed-hard-cases.fsx`
- **Verification:** `dotnet fsi scripts/seed-hard-cases.fsx` runs with 0 warnings, outputs "Wrote 30 synthetic hard-case entries to datasets/hard-cases.jsonl"
- **Committed in:** `0f7a6c5` (Task 3 commit)

**3. [Rule 1 - Bug] UTF-8 BOM in StreamWriter output causes JSON parse failure**

- **Found during:** Task 3 (seed script JSON verification)
- **Issue:** `new StreamWriter(path, ..., encoding = Encoding.UTF8)` uses `UTF8Encoding(true)` (with BOM) by default; `python3 json.loads` raises JSONDecodeError on BOM-prefixed JSONL
- **Fix:** Changed to `UTF8Encoding(false)` (BOM-less, per JSONL/JSON convention)
- **Files modified:** `scripts/seed-hard-cases.fsx`
- **Verification:** `head -1 datasets/hard-cases.jsonl | python3 -c "import sys,json; e=json.loads(sys.stdin.read()); assert e['source']=='synthetic'; assert e['schema_version']==1; print('OK')"` prints OK
- **Committed in:** `0f7a6c5` (Task 3 commit)

---

**Total deviations:** 3 auto-fixed (1 blocking, 2 bugs)
**Impact on plan:** All auto-fixes necessary for correctness. The ML algorithm guard (deviation 1) is architecturally sound — it makes the ML wiring conditional on intent, matching the existing RoutingAlgorithmRegistration "ml" branch. No scope creep.

## Issues Encountered

- `dotnet fsi --check` is unsupported in the installed dotnet fsi version — skipped per plan instructions; seed script verified by direct execution instead.
- Sister plan 07-06 tests are present (16 new Phase 7 tests added) and show 1 failed + 8 errored — these are 07-06's new tests, not regressions from this plan. Original 50 tests all pass + 10 ignored baseline unchanged.

## Build and Test Status

- **Build:** 0 errors, 0 warnings (`dotnet build SmartRouter.slnx`)
- **Tests (baseline):** 50 passed + 10 ignored — Phase 7 plan 05 introduces no new test files (sister plan 07-06 adds tests)
- **Tests (with 07-06 present):** 57 passed + 10 ignored + 1 failed + 8 errored — failures are all in 07-06's new Phase 7 test files (TeacherLabeler + HardCaseDatasetWriter tests); not caused by this plan's changes

## Next Phase Readiness

- Phase 7 DI integration complete: all three adapters injectable from any future consumer
- Phase 8's `RetrainingService : BackgroundService` can resolve `IFailureDetector`, `ITeacherLabeler`, `IHardCaseDatasetWriter` directly from the same DI container — no additional wiring needed
- Operator can run `dotnet fsi scripts/seed-hard-cases.fsx` once to prime `datasets/hard-cases.jsonl`, then `dotnet run -- --retrain` to exercise the full offline pipeline
- Teacher prompt is committed and operator-editable at `prompts/teacher-prompt.md` without recompiling

---
*Phase: 07-failure-detection-and-teacher-labeling*
*Completed: 2026-05-08*
