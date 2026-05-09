---
phase: 12-heuristic-removal
plan: 02
type: execute
wave: 2
depends_on: ["12-01"]
files_modified:
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/appsettings.json
autonomous: true

must_haves:
  truths:
    - "CompositionRoot.fs exposes two top-level entry points: configureRequestPipeline (full ML wiring; production HTTP service path) and configureWithoutMl (subset for --retrain + tests; no IClassifier, no RoutingAlgorithmRegistration, no MakeApplyMl, no ensureEmbeddingFilesPresent)"
    - "RoutingOptions record has no Algorithm field; routingAlgoStr variable does not exist; the null|\"\"|\"heuristic\" arm of RoutingAlgorithmRegistration is gone"
    - "validateConfig (or whatever validates Routing options) does NOT mention \"heuristic\" or \"valid values: heuristic, ml\" — Algorithm key is no longer expected by config"
    - "appsettings.json has no \"Algorithm\" key under Routing; no \"ComplexityThreshold\"; no \"Keywords\" (those three are removed; TaskTable, TimeoutSeconds, ML, ModelAliases retained)"
    - "Program.fs has no --routing-algorithm CLI flag parsing block; the only remaining CLI argument handling is --retrain and --trace (or whichever flags are still in use post-Phase 11)"
    - "Program.fs --retrain branch calls configureWithoutMl (NOT configureServices with a heuristic-injected algorithm)"
    - "dotnet build (full solution) succeeds with TreatWarningsAsErrors=true; tests build state may still be broken by heuristic test references — those are addressed in 12-03/04/05"
  artifacts:
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "Split configureRequestPipeline + configureWithoutMl entry points"
      contains: ["configureRequestPipeline", "configureWithoutMl"]
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Cleaned Routing section (no Algorithm/ComplexityThreshold/Keywords)"
    - path: "src/SmartRouter.Cli/Program.fs"
      provides: "main entry without --routing-algorithm flag; --retrain uses configureWithoutMl"
---

<objective>
Rewire Cli composition: split `configureServices` into `configureRequestPipeline` (full ML) and `configureWithoutMl` (offline + tests) per Q1=B; delete the heuristic dispatch arm in `RoutingAlgorithmRegistration` selection per Q3 (RoutingOptions.Algorithm field gone, routingAlgoStr gone, validation error case gone); delete the `Routing.Algorithm` key + heuristic-only fields from `appsettings.json`; delete the `--routing-algorithm` CLI flag entirely per Q4; update `Program.fs` `--retrain` branch to call `configureWithoutMl` directly (no in-memory `Routing:Algorithm = "heuristic"` injection).

After this plan: build green; tests build state TBD (12-03/04/05 fix tests); no service behavior change for production traffic (ML was already the only path; Phase 6 flipped the default).
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/preparing/12-CONTEXT.md
@.planning/phases/12-heuristic-removal/12-CONTEXT.md
@.planning/phases/12-heuristic-removal/12-heuristic-removal-research.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: appsettings.json — delete Routing.Algorithm + ComplexityThreshold + Keywords</name>
  <files>src/SmartRouter.Cli/appsettings.json</files>
  <action>
Edit `src/SmartRouter.Cli/appsettings.json`. The `Routing` section currently contains keys `Algorithm`, `TimeoutSeconds`, `ComplexityThreshold`, `Keywords`, `TaskTable`, `ModelAliases`, `ML`. Delete three keys:

- `"Algorithm": "ml"` (entire line)
- `"ComplexityThreshold": 3` (entire line)
- `"Keywords": [ ... ]` (entire array; multi-line)

Retain: `TimeoutSeconds`, `TaskTable`, `ModelAliases`, `ML`.

The other top-level sections (`Upstreams`, `Queue`, `DecisionLog`, `TeacherLabeler`, `HardCaseDataset`, `Canary`, `feature_management`, `Health`, `Serilog`, `Logging`) untouched.

JSON syntax must remain valid — no trailing commas after the last key in each level.
  </action>
  <verify>
```bash
grep -c '"Algorithm"\|"ComplexityThreshold"\|"Keywords"' src/SmartRouter.Cli/appsettings.json
# expected: 0
python3 -c "import json; json.load(open('src/SmartRouter.Cli/appsettings.json'))" && echo "JSON valid" || echo "JSON BROKEN"
# expected: JSON valid
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: CompositionRoot.fs — RoutingOptions field deletion + heuristic arm + routingAlgoStr removal</name>
  <files>src/SmartRouter.Cli/CompositionRoot.fs</files>
  <action>
Multiple targeted edits in `src/SmartRouter.Cli/CompositionRoot.fs`:

**Edit 1.** `RoutingOptions` record (currently around line 71): delete `Algorithm`, `ComplexityThreshold`, `Keywords` fields. Resulting:

```fsharp
type RoutingOptions =
    { TimeoutSeconds : int
      ML             : MlOptions
      TaskTable      : Dictionary<string, TaskTableEntry>
      ModelAliases   : Dictionary<string, string> }
```

**Edit 2.** `buildRoutingConfig` function: stop reading `opts.ComplexityThreshold` and `opts.Keywords`. Build `RoutingConfig` with only `TaskTable` and `MlThreshold`:

```fsharp
let buildRoutingConfig (opts: RoutingOptions) : RoutingConfig =
    let mlThreshold =
        if obj.ReferenceEquals(opts.ML, null) then 0.5f
        else if opts.ML.Threshold = 0.0f then 0.5f
        else opts.ML.Threshold
    { TaskTable   = ... (existing build from opts.TaskTable Dictionary)
      MlThreshold = mlThreshold }
```

(Existing TaskTable conversion logic from Dictionary→Map is preserved verbatim; only the field names being assigned change.)

**Edit 3.** Remove the `routingAlgoStr` variable definition (currently lines 293–296) and replace its usage sites. Currently:

```fsharp
let routingAlgoStr =
    if obj.ReferenceEquals(routingOpts, null) then "heuristic"
    else if String.IsNullOrWhiteSpace(routingOpts.Algorithm) then "heuristic"
    else routingOpts.Algorithm.ToLowerInvariant()
```

Delete entirely. The three downstream usage sites:
- Line 299: `if not (...) && routingAlgoStr = "ml" then` → just `if not (...) then` (ML wiring becomes unconditional).
- Line 573: `| "ml" when ...` match arm → conditional becomes unconditional; no string match needed. Restructure as a plain `if not (obj.ReferenceEquals(routingOpts.ML, null)) then load model_version with SHA prefix else use placeholder`.
- Line 589: `if routingAlgoStr = "ml" then` → unconditional.

**Edit 4.** Remove the `RoutingAlgorithmRegistration` heuristic arm (currently lines 348–391). The `match` block:

```fsharp
match routingAlgoStr with
| null | "" | "heuristic" -> ... heuristic registration ...
| "ml" -> ... ML registration ...
| other -> failwithf "appsettings.json Routing.Algorithm = \"%s\" is invalid; valid values: \"heuristic\", \"ml\"" other
```

becomes a single ML branch. Delete the `match`; emit ML registration unconditionally:

```fsharp
services.AddSingleton<RoutingAlgorithmRegistration>(
    Func<IServiceProvider, RoutingAlgorithmRegistration>(fun sp ->
        // ML registration code from the existing "ml" arm verbatim
    ))
```

The existing `Backwards-compatible alias: register the bare RoutingAlgorithm function ...` block (lines 396–402) stays.

**Edit 5.** Comment cleanup. Update three comments:
- Line 112 ("heuristic-only deployments can omit Routing.ML entirely") → "ML routes use this when Routing.ML.Threshold is missing/zero".
- Line 270 ("Heuristic mode gets a no-op pair") → "Default mode (without canary feature) gets a no-op pair".
- Line 288 ("guard the ML block on Routing.Algorithm = \"ml\"") → delete the entire DEVIATION comment block; ML wiring is unconditional now.

**Edit 6.** Rename top-level entry point. The current single function `let configureServices (services: IServiceCollection) (config: IConfiguration) : IServiceCollection = ...` becomes two functions:

```fsharp
// Existing logic moves into configureRequestPipeline.
let configureRequestPipeline (services: IServiceCollection) (config: IConfiguration) : IServiceCollection =
    // verbatim what configureServices used to do, minus the heuristic arm

// New function — subset for --retrain offline path + tests that don't want ML model files.
let configureWithoutMl (services: IServiceCollection) (config: IConfiguration) : IServiceCollection =
    // Common: HTTP factories (named "qwen35b", "qwen122b", "qwen35b-stream", "qwen122b-stream", "teacher", "health-probe")
    // Common: decision-logger sink wiring (DecisionLogWriter triple-registration)
    // Common: retrain ports — IFailureDetector, ITeacherLabeler, IHardCaseDatasetWriter (HardCaseDatasetWriter triple-registration)
    // Common: TeacherLabelerOptions IOptions binding
    // Common: HardCaseDatasetOptions IOptions binding
    // Common: BgeM3EmbedderOptions IOptions binding (Embedder is needed by DatasetMerger; option binding only — Embedder instance NOT registered as IEmbedder until caller decides)
    // EXCLUDE: ensureEmbeddingFilesPresent call (no model file checks)
    // EXCLUDE: BgeM3Embedder instance registration as IEmbedder
    // EXCLUDE: PredictionEnginePool registration
    // EXCLUDE: MlNetClassifier registration as IClassifier
    // EXCLUDE: makeApplyMl closure registration
    // EXCLUDE: RoutingAlgorithmRegistration registration
    // EXCLUDE: HealthService registration (Phase 10) — health probing only matters for live request path
    // EXCLUDE: CanaryService / CanaryMetrics / CanaryGate registration (Phase 9) — canary needs request path
    // EXCLUDE: RetrainingService BackgroundService registration (Phase 8) — --retrain runs synchronously, no PeriodicTimer
    // INCLUDE: ModelVersionProvider (used by retrain to read current model SHA)
    services
```

Implementation tip: factor the common parts into a private `configureCore services config` helper that both public entry points call. Then `configureRequestPipeline = configureCore + ML wiring + Health/Canary/Retraining BackgroundServices + RoutingAlgorithmRegistration`. `configureWithoutMl = configureCore + retrain ports only`.

Public `configureServices` name kept as alias of `configureRequestPipeline` for callers that haven't been migrated yet (StreamingTests, LoggingTests, HealthFallbackTests will be migrated in 12-05; until then they call the alias).

**Edit 7 — alias for backwards compatibility within Phase 12:**

```fsharp
// Compatibility alias: existing callers (Program.fs main, test fixtures) still call configureServices.
// 12-05 migrates the test fixtures to call configureWithoutMl explicitly. After 12-05, the alias may be removed.
let configureServices = configureRequestPipeline
```

(F# top-level let bindings are not partial-applicable as-is; if compiler complains use `let configureServices services config = configureRequestPipeline services config` instead.)
  </action>
  <verify>
```bash
grep -c "RoutingOptions.Algorithm\|opts\.Algorithm\|routingAlgoStr" src/SmartRouter.Cli/CompositionRoot.fs
# expected: 0
grep -c "ComplexityThreshold\|opts\.Keywords" src/SmartRouter.Cli/CompositionRoot.fs
# expected: 0
grep -c "configureRequestPipeline\|configureWithoutMl" src/SmartRouter.Cli/CompositionRoot.fs
# expected: >= 2 (definitions present)
grep -c "valid values: \"heuristic\", \"ml\"\|valid values: heuristic, ml" src/SmartRouter.Cli/CompositionRoot.fs
# expected: 0
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -5
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Program.fs — delete --routing-algorithm flag + update --retrain to use configureWithoutMl</name>
  <files>src/SmartRouter.Cli/Program.fs</files>
  <action>
**Edit 1.** Delete the `--routing-algorithm` flag parsing + validation + injection block (currently lines 117–153). Specifically the entire block starting with the comment `// Parse --routing-algorithm CLI flag (last-wins). Supports:` through the `dict [ "Routing:Algorithm", v ]` injection. After deletion, the next code (the canary feature_management percentage block at line 155+) follows directly after the `args` parsing of `--retrain`.

**Edit 2.** Update `--retrain` branch (currently lines 21–105) to call `CompositionRoot.configureWithoutMl` instead of `CompositionRoot.configureServices`. Specifically:

Currently lines 33–42:
```fsharp
// Override Routing.Algorithm to "heuristic" for the offline retrain path.
// ...long comment...
(retrainBuilder.Configuration :> IConfigurationBuilder)
    .AddInMemoryCollection(dict [ "Routing:Algorithm", "heuristic" ])
|> ignore
CompositionRoot.configureServices retrainBuilder.Services retrainBuilder.Configuration |> ignore
```

Becomes:
```fsharp
CompositionRoot.configureWithoutMl retrainBuilder.Services retrainBuilder.Configuration |> ignore
```

(The 7-line override block is gone entirely; `configureWithoutMl` skips ML init by construction — Q1=B mechanism replaces the runtime Routing:Algorithm injection.)

**Edit 3.** Comment cleanup — line 157 referencing "heuristic mode default": rephrase or delete.

The rest of `Program.fs` (canary percentage sync block, configureServices call for the main host, app.Run, etc.) is unchanged.
  </action>
  <verify>
```bash
grep -c "routing-algorithm\|--routing-algorithm" src/SmartRouter.Cli/Program.fs
# expected: 0
grep -c "Routing:Algorithm.*heuristic\|\"Routing:Algorithm\".*\"heuristic\"" src/SmartRouter.Cli/Program.fs
# expected: 0
grep -c "configureWithoutMl" src/SmartRouter.Cli/Program.fs
# expected: 1  (in the --retrain branch)
grep -c "configureRequestPipeline\|configureServices" src/SmartRouter.Cli/Program.fs
# expected: 1  (the main app branch keeps using whichever name survives Edit 7 of Task 2)
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 4: Solution-level build sanity check</name>
  <files>(read-only verification)</files>
  <action>
Run a full solution build to confirm Cli + Core compile. Tests project may still fail (12-03/04/05 will fix tests).

```bash
dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj && \
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj && \
echo "Cli + Core OK"
```

If tests build also passes (because 12-03/04/05 don't touch dependent compile-order references), great — but it's NOT required at this plan's end.
  </action>
  <verify>
```bash
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | grep -E "Build succeeded|error" | head -5
# expected: "Build succeeded." present; no "error" lines
```
  </verify>
</task>

</tasks>

<verification>
- [x] appsettings.json: Routing section has no Algorithm, ComplexityThreshold, Keywords
- [x] CompositionRoot.fs: split into configureRequestPipeline + configureWithoutMl; no routingAlgoStr; no heuristic dispatch arm
- [x] Program.fs: no --routing-algorithm flag; --retrain uses configureWithoutMl
- [x] Cli + Core build clean
- [x] Tests project build state TBD (12-03/04/05 fix)
</verification>
