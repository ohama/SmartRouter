---
phase: 07-failure-detection-and-teacher-labeling
plan: 01
subsystem: retraining-pipeline
tags: [fsharp, interfaces, ports, stubs, hexagonal, BCL-only, NotImplementedException]

# Dependency graph
requires:
  - phase: 06-real-ml-routing
    provides: MLPorts.fs pattern (IEmbedder/IClassifier BCL-only port interfaces in Core)
  - phase: 05-routing-decision-logging
    provides: DecisionLogWriter pattern (Channel + BackgroundService + IDecisionLogger)
provides:
  - IFailureDetector port interface (Core/RetrainingPorts.fs)
  - ITeacherLabeler port interface (Core/RetrainingPorts.fs)
  - IHardCaseDatasetWriter port interface (Core/RetrainingPorts.fs)
  - RoutingLabel DU (Route35B|Route122B)
  - LabelResult DU (Labeled|Unparseable|Skipped|Failed)
  - HardCase record (correlation_id + context)
  - HardCaseEntry record (datasets/hard-cases.jsonl schema v1)
  - FailureDetector stub adapter (Cli/Adapters/FailureDetector.fs)
  - TeacherLabeler stub adapter + TeacherLabelerOptions (Cli/Adapters/TeacherLabeler.fs)
  - HardCaseDatasetWriter stub adapter + HardCaseDatasetOptions (Cli/Adapters/HardCaseDatasetWriter.fs)
affects:
  - 07-02 (FailureDetector real implementation)
  - 07-03 (TeacherLabeler real implementation)
  - 07-04 (HardCaseDatasetWriter real implementation)
  - 07-05 (DI wiring in CompositionRoot)
  - 08-retraining-loop (BackgroundService consumes all 3 interfaces)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Wave 1 stub pattern: constructor signature is final; method bodies throw NotImplementedException; Wave 2 plans replace bodies exclusively"
    - "BCL-only port invariant: Core/RetrainingPorts.fs uses only System + System.Threading + System.Threading.Tasks; no NuGet in Core"
    - "Parallel Wave 2 unlocked: each of 07-02/03/04 owns one stub file exclusively — no .fsproj write conflicts"

key-files:
  created:
    - src/SmartRouter.Core/RetrainingPorts.fs
    - src/SmartRouter.Cli/Adapters/FailureDetector.fs
    - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
    - src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
  modified:
    - src/SmartRouter.Core/SmartRouter.Core.fsproj
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj

key-decisions:
  - "RetrainingPorts.fs placed after Routing.fs and before Ports.fs in Core.fsproj (per CONTEXT.md — no dependency on MLPorts/ML/Routing; types-first ordering preserved)"
  - "HardCaseDatasetWriter inherits BackgroundService in stub so Plan 07-05 AddHostedService<HardCaseDatasetWriter> registration compiles without change"
  - "TeacherLabelerOptions [<CLIMutable>] record included in stub so constructor signature is final — Plan 07-03 fills in the method body only"
  - "Three Phase 7 adapter entries in Cli.fsproj inserted after QueueDispatcher.fs and before Endpoints/ChatCompletions.fs — matches DecisionLogWriter compile zone"

patterns-established:
  - "Wave 1 stub + Wave 2 fill-in: create compilable skeleton with NotImplementedException bodies; subsequent plans replace exactly one file's bodies without touching .fsproj"
  - "Pure-Core port invariant: BCL-only (System.*) in Core/*.fs; concrete infrastructure in Cli/Adapters/*.fs"

# Metrics
duration: 2min
completed: 2026-05-08
---

# Phase 7 Plan 01: Foundation Summary

**Pure-Core port interfaces (IFailureDetector/ITeacherLabeler/IHardCaseDatasetWriter + 4 supporting types) and 3 Cli adapter stubs in place; Wave 2 plans can now run in parallel with no .fsproj conflicts**

## Performance

- **Duration:** 2 min
- **Started:** 2026-05-08T12:19:11Z
- **Completed:** 2026-05-08T12:21:56Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- Core/RetrainingPorts.fs ships with 3 BCL-only port interfaces + RoutingLabel DU + LabelResult DU + HardCase record + HardCaseEntry record; pure-Core invariant verified (no Serilog/HttpClient/ML/AspNetCore)
- Three Cli adapter stub files compile with NotImplementedException bodies; each implements its interface; constructor signatures are final
- Both .fsproj files updated with correct compile ordering; build is clean at 0 errors + 0 warnings (TreatWarningsAsErrors=true); Phase 6 test baseline unchanged at 50 pass + 10 ignored

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Core/RetrainingPorts.fs (BCL-only port interfaces and types)** - `106841f` (feat)
2. **Task 2: Create three Cli adapter STUB files** - `31eea23` (feat)

**Plan metadata:** (docs commit follows this SUMMARY creation)

## Files Created/Modified
- `src/SmartRouter.Core/RetrainingPorts.fs` - BCL-only module: RoutingLabel DU, LabelResult DU, HardCase record, HardCaseEntry record, IFailureDetector, ITeacherLabeler, IHardCaseDatasetWriter
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` - Added RetrainingPorts.fs after Routing.fs, before Ports.fs
- `src/SmartRouter.Cli/Adapters/FailureDetector.fs` - FailureDetector stub: IFailureDetector, constructor (logsDirectory: string), NotImplementedException body
- `src/SmartRouter.Cli/Adapters/TeacherLabeler.fs` - TeacherLabeler stub + TeacherLabelerOptions [CLIMutable] record: ITeacherLabeler, constructor (httpFactory, options), NotImplementedException body
- `src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs` - HardCaseDatasetWriter stub + HardCaseDatasetOptions [CLIMutable] record: inherits BackgroundService, IHardCaseDatasetWriter, ExecuteAsync returns Task.CompletedTask, AppendAsync NotImplementedException body
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - Added 3 new Compile entries after QueueDispatcher.fs, before Endpoints/ChatCompletions.fs

## Decisions Made
- RetrainingPorts.fs placed after Routing.fs and before Ports.fs in Core.fsproj (per CONTEXT.md decision; RetrainingPorts has no dependency on Routing/ML/Ports so position is flexible; after Routing.fs is the cleanest anchor point)
- HardCaseDatasetWriter inherits BackgroundService at stub stage so the DI registration in Plan 07-05 (`AddHostedService<HardCaseDatasetWriter>`) requires no signature change
- TeacherLabelerOptions [CLIMutable] record declared in the stub file so Plan 07-03 only replaces the LabelAsync body without touching the type definition
- No NuGet changes to either project — all required packages already pinned from prior phases

## Deviations from Plan
None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Wave 2 plans (07-02, 07-03, 07-04) are unblocked and can run in parallel: each plan modifies exactly one stub file's method body without touching .fsproj or the other stubs
- 07-02 owns FailureDetector.fs: implements ExtractHardCases JSONL reader (FAIL-01)
- 07-03 owns TeacherLabeler.fs: implements LabelAsync HTTP + retry + cost-cap (FAIL-02/03)
- 07-04 owns HardCaseDatasetWriter.fs: implements AppendAsync Channel + single-writer (FAIL-04)
- 07-05 wires DI in CompositionRoot and adds appsettings.json sections

---
*Phase: 07-failure-detection-and-teacher-labeling*
*Completed: 2026-05-08*
