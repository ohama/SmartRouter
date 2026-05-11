---
phase: 06-real-ml-routing
plan: 01
subsystem: ml-routing
tags: [fsharp, onnxruntime, mlnet, bge-m3, embeddings, classifier, nuget, gitignore, shell-scripts]

# Dependency graph
requires:
  - phase: 04-ml-algorithm-seam
    provides: RoutingAlgorithm type alias, ML.applyML placeholder, ML-01/ML-04 contracts
  - phase: 05-routing-decision-logging
    provides: 49/49 test baseline (pre-ML integration); DecisionLog infrastructure

provides:
  - IEmbedder + IClassifier + ClassifierPrediction in Core (pure F# interfaces, BCL only)
  - makeApplyML closure factory in ML.fs (bridges async ports to synchronous RoutingAlgorithm)
  - runSync helper in ML.fs (Task.Run + GetAwaiter().GetResult() pattern)
  - RoutingConfig.MlThreshold field (default 0.5f; read by makeApplyML, ignored by heuristic)
  - MLPorts.fs in correct compile order (Domain → Heuristic → MLPorts → ML → Routing → Ports)
  - 4 ML NuGet pins on Cli project (OnnxRuntime 1.25.1, Tokenizers 2.0.0, ML 5.0.0, Extensions.ML 5.0.0)
  - models/ gitignored (excludes ~580MB ONNX + router.zip)
  - scripts/download-models.sh (huggingface-cli download Teradata/bge-m3)
  - scripts/export-bge-m3-int8.sh (self-export from BAAI/bge-m3 via optimum-cli + quantize_dynamic)

affects:
  - 06-02 (wires BgeM3Embedder + MlNetClassifier adapters; removes applyML placeholder)
  - 06-03 (E2E integration tests using real ONNX + ML.NET; requires models/ populated)

# Tech tracking
tech-stack:
  added:
    - Microsoft.ML.OnnxRuntime 1.25.1 (Cli only)
    - Microsoft.ML.Tokenizers 2.0.0 (Cli only)
    - Microsoft.ML 5.0.0 (Cli only)
    - Microsoft.Extensions.ML 5.0.0 (Cli only)
  patterns:
    - Port/adapter seam: pure Core interfaces (IEmbedder, IClassifier) + Cli adapters (Phase 6-02)
    - runSync helper: Task.Run + GetAwaiter().GetResult() bridges async adapters to sync RoutingAlgorithm
    - Wave 1 boundary: Core/Tests green; Cli Cli CLI continues to compile (applyML placeholder retained until 06-02)
    - Pure-Core invariant (ARCH-01): zero Microsoft.ML.* in Core .fs files and .fsproj

key-files:
  created:
    - src/SmartRouter.Core/MLPorts.fs
    - scripts/download-models.sh
    - scripts/export-bge-m3-int8.sh
  modified:
    - src/SmartRouter.Core/Domain.fs (MlThreshold field added to RoutingConfig)
    - src/SmartRouter.Core/ML.fs (makeApplyML closure + runSync; legacy applyML retained)
    - src/SmartRouter.Core/Routing.fs (defaultRoutingConfig initializes MlThreshold = 0.5f)
    - src/SmartRouter.Core/SmartRouter.Core.fsproj (MLPorts.fs in compile order)
    - src/SmartRouter.Cli/SmartRouter.Cli.fsproj (4 ML NuGet pins)
    - src/SmartRouter.Cli/CompositionRoot.fs (MlThreshold in RoutingOptions + buildRoutingConfig)
    - .gitignore (models/ exclusion)

key-decisions:
  - "IEmbedder takes embedding as float32[] not ReadOnlyMemory<float32> — simpler BCL type, adapters can always wrap if needed"
  - "IClassifier takes float32[] (not ReadOnlyMemory) for symmetry with IEmbedder and simpler F# interop"
  - "runSync uses Task.Run factory lambda not Task.Run(fun () -> t) — avoids capturing already-started Task on SyncContext; canonical guard pattern"
  - "CompositionRoot buildRoutingConfig defaults MlThreshold to 0.5f when JSON value is 0.0 (default float) — no appsettings.json change required"
  - "applyML legacy placeholder retained in wave 1 so CompositionRoot ml branch continues to compile — removed in 06-02"
  - "NuGet pins added to Cli only (not Core) — ARCH-01 preserved; Core remains FsToolkit.ErrorHandling only"
  - "No System.Memory conflict between OnnxRuntime 1.25.1 and Microsoft.ML 5.0.0 — restore resolved cleanly"

patterns-established:
  - "Port pattern: Core defines pure F# interface; Cli project implements with NuGet adapters (Phase 6-02)"
  - "runSync helper: private in ML.fs; only used by makeApplyML; documents SyncContext deadlock pitfall"
  - "Wave boundary: Cli continues to build at wave 1 by retaining placeholder; wave 2 (06-02) swaps the adapter"

# Metrics
duration: 5min
completed: 2026-05-08
---

# Phase 6 Plan 01: Core-Ports-and-NuGet Summary

**IEmbedder + IClassifier + ClassifierPrediction ports in pure Core; makeApplyML closure factory with runSync helper; RoutingConfig.MlThreshold=0.5f; 4 ML NuGet pins on Cli; models/ gitignored; huggingface + self-export scripts shipped**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-05-08T07:38:59Z
- **Completed:** 2026-05-08T07:43:57Z
- **Tasks:** 2
- **Files modified:** 9 (6 modified, 3 created)

## Accomplishments

- Pure-Core seam established: IEmbedder + IClassifier + ClassifierPrediction in MLPorts.fs (BCL only, zero NuGet); ARCH-01 preserved
- makeApplyML closure factory + runSync helper in ML.fs — Phase 4 RoutingAlgorithm contract satisfied; legacy applyML retained for wave-1 Cli compatibility
- RoutingConfig.MlThreshold: float32 added to Domain.fs; defaultRoutingConfig = 0.5f; CompositionRoot buildRoutingConfig reads from appsettings.json with 0.5f default
- 4 ML NuGet packages pinned to Cli .fsproj (OnnxRuntime 1.25.1, Tokenizers 2.0.0, ML 5.0.0, Extensions.ML 5.0.0) — no System.Memory conflict; restore clean
- Binary asset boundary: models/ gitignored; download-models.sh (Teradata/bge-m3 HF mirror) + export-bge-m3-int8.sh (BAAI/bge-m3 self-export) both executable and syntax-checked

## Task Commits

1. **Task 1: Core ML ports seam** - `393aaf6` (feat)
2. **Task 2: Cli NuGet pins + gitignore + scripts** - `0ef3c27` (feat)

**Plan metadata:** (pending docs commit)

## Files Created/Modified

- `src/SmartRouter.Core/MLPorts.fs` - IEmbedder + IClassifier + ClassifierPrediction (pure F# BCL, no NuGet)
- `src/SmartRouter.Core/Domain.fs` - RoutingConfig.MlThreshold : float32 field added
- `src/SmartRouter.Core/ML.fs` - makeApplyML closure factory + runSync helper; applyML placeholder retained
- `src/SmartRouter.Core/Routing.fs` - defaultRoutingConfig initializes MlThreshold = 0.5f
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` - MLPorts.fs added in compile order between Heuristic.fs and ML.fs
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` - 4 ML NuGet pins added (Phase 6 section)
- `src/SmartRouter.Cli/CompositionRoot.fs` - MlThreshold field in RoutingOptions; buildRoutingConfig initializes it (default 0.5f)
- `.gitignore` - models/ exclusion added
- `scripts/download-models.sh` - huggingface-cli download Teradata/bge-m3 int8 to models/embed/
- `scripts/export-bge-m3-int8.sh` - optimum-cli + quantize_dynamic self-export from BAAI/bge-m3

## NuGet Versions Pinned (exact, for 06-02 reference)

| Package | Version | Notes |
|---------|---------|-------|
| Microsoft.ML.OnnxRuntime | 1.25.1 | CoreML EP available on macOS/Apple Silicon (EMBED-03) |
| Microsoft.ML.Tokenizers | 2.0.0 | SentencePiece support for bge-m3 tokenizer |
| Microsoft.ML | 5.0.0 | LR classifier via PredictionEnginePool |
| Microsoft.Extensions.ML | 5.0.0 | PredictionEnginePool DI registration |

No System.Memory conflict between OnnxRuntime 1.25.1 and Microsoft.ML 5.0.0 — `dotnet restore` resolved cleanly.

## Decisions Made

- **IEmbedder/IClassifier use float32[] not ReadOnlyMemory** — simpler BCL type, no wrapper allocation overhead in tests; Cli adapters wrap to ReadOnlyMemory if OnnxRuntime requires it
- **runSync uses Task.Run factory lambda** (`Task.Run<'a>(Func<Task<'a>>(taskFactory))`) — avoids capturing an already-started Task onto the ASP.NET SyncContext; canonical deadlock-prevention pattern
- **CompositionRoot defaults MlThreshold to 0.5f when opts.MlThreshold = 0.0f** — CLIMutable float32 defaults to 0.0f when JSON key absent; this avoids requiring appsettings.json to be updated in wave 1
- **applyML placeholder retained in wave 1** — CompositionRoot `"ml"` branch still compiles (references ML.applyML); swapped out in 06-02 Task 3

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] CompositionRoot.fs buildRoutingConfig must initialize MlThreshold**
- **Found during:** Task 1 (adding MlThreshold to RoutingConfig record)
- **Issue:** RoutingConfig is a record; adding a new field breaks every construction site that does not name it. CompositionRoot.fs line 70-72 `{ ComplexityThreshold = ...; Keywords = ...; TaskTable = ... }` would fail to compile with a missing-field error. Plan noted RoutingTests.fs might need updating but did not explicitly call out CompositionRoot.fs.
- **Fix:** Added `MlThreshold : float32` to `RoutingOptions` (the JSON-binding record) with a comment; updated `buildRoutingConfig` to initialize `MlThreshold = if opts.MlThreshold = 0.0f then 0.5f else opts.MlThreshold` (defensive: CLIMutable float32 defaults to 0.0f when JSON key absent).
- **Files modified:** src/SmartRouter.Cli/CompositionRoot.fs
- **Verification:** `dotnet build` succeeds at 0 warnings + 0 errors; 49/49 tests pass
- **Committed in:** 393aaf6 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 2 - missing critical compilation fix)
**Impact on plan:** Required for correctness — record field addition breaks compilation without it. No scope creep.

## Issues Encountered

None — build, tests, and all script checks passed on first attempt.

## NuGet Restore Notes

`dotnet restore src/SmartRouter.Cli/SmartRouter.Cli.fsproj` after adding 4 new PackageReferences completed in ~18s with no version conflicts. OnnxRuntime 1.25.1 and Microsoft.ML 5.0.0 share System.Memory but resolved without pinning either. No `dotnet add package` warnings emitted.

## Wave 1 Boundary State

After this plan:
- Core builds: green (0 warnings, 0 errors)
- Tests: 49/49 pass (applyML placeholder still returns Qwen35B/Low/ML/IsFallback=false)
- Cli builds: green (CompositionRoot still calls ML.applyML; makeApplyML not yet wired)
- Wave 2 (06-02): wires BgeM3Embedder + MlNetClassifier adapters; removes applyML placeholder

Note: The objective states "Cli build is EXPECTED to FAIL" at wave 1 boundary. In practice, because `applyML` was **retained** (not removed) in this plan, the Cli continues to compile cleanly. The expected failure would only occur if `applyML` had been removed, which 06-02 Task 3 will do when it wires makeApplyML.

## Next Phase Readiness

- 06-02 has everything it needs: IEmbedder + IClassifier ports defined; NuGet packages already in Cli; makeApplyML ready to receive concrete adapters
- Operator setup: run `scripts/download-models.sh` once before 06-02 smoke test to populate models/embed/
- appsettings.json does not need updating for wave 1 (MlThreshold defaults to 0.5f via buildRoutingConfig)

---
*Phase: 06-real-ml-routing*
*Completed: 2026-05-08*
