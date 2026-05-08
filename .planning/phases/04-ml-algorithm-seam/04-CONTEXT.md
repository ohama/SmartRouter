# Phase 4: ML Algorithm Seam - Context

**Gathered:** 2026-05-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Refactor `Routing.fs` to support pluggable routing algorithms. Introduce two flat-layout sibling modules: `Heuristic.fs` (existing scoring logic moved out, no behavior change) and `ML.fs` (new placeholder that always returns `Qwen35B` with `Reason = ML`). Add a `RoutingAlgorithm` function-type alias in `Domain.fs` so both modules compile against a stable contract. `routeRequest` gains an algorithm parameter; CompositionRoot dispatches `"heuristic" | "ml"` from `Routing.Algorithm` config; Program.fs parses `--routing-algorithm` CLI flag as a config override before service registration. Heuristic remains the default. The placeholder ML is intentionally dumb — the deliverable is the *seam*, not the model.

</domain>

<decisions>
## Implementation Decisions

### File layout: flat siblings (no `Routing/` subdirectory)
- Files: `src/SmartRouter.Core/{Domain.fs, Heuristic.fs, ML.fs, Routing.fs, Ports.fs}`
- Rationale: subdirectory adds namespace complexity for zero benefit; ML.fs has only the placeholder; future Phase 6 will add `Embedder.fs` / `Classifier.fs` adapters in Cli (NOT Core) so Core stays flat
- `.fsproj` `<Compile>` order (DETERMINISTIC, F# is order-sensitive): `Domain.fs` → `Heuristic.fs` → `ML.fs` → `Routing.fs` → `Ports.fs`

### `RoutingAlgorithm` type alias lives in `Domain.fs`
- Both `Heuristic.fs` and `ML.fs` compile *before* `Routing.fs` and need to satisfy the type — so the alias must be defined upstream of both
- Definition: `type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision`
- Note the return type is **`RoutingDecision`, not `Result<RoutingDecision, RouterError>`** — error paths (unknown task, missing model alias) are owned by the upstream `tryTaskTable` / `tryModelOverride` stages; algorithm only fires when those return `None` so it never has reason to fail

### `RoutingReason` DU extension
- Add `| ML` case to `RoutingReason` in Domain.fs
- Existing 7 `match d.Reason with` sites in `RoutingTests.fs` all use catch-all `| r -> failtestf ...` — DU extension is safe under `TreatWarningsAsErrors=true`
- ML.fs's placeholder returns `{ Reason = ML; ... }`; Heuristic.fs continues returning `{ Reason = Heuristic; ... }` (or `Default` for ambiguous-tie)

### `routeRequest` signature change
- Before: `routeRequest : RoutingConfig -> RouterRequest -> Result<RoutingDecision, RouterError>`
- After: `routeRequest : RoutingConfig -> RoutingAlgorithm -> RouterRequest -> Result<RoutingDecision, RouterError>`
- The algorithm parameter is invoked only when both override and task table return `None`
- Mechanical update across 1 endpoint callsite (`ChatCompletions.fs`) and 22 test callsites (RoutingTests.fs `route` helper + 3 direct calls)

### Config schema: `Routing.Algorithm`
- New string key in `appsettings.json`: `"Routing": { "Algorithm": "heuristic", ... }`
- `RoutingOptions.Algorithm: string` (no nullable annotation — F# binds missing/null to `null`)
- CompositionRoot dispatch:
  - `"heuristic" | "" | null -> Heuristic.applyHeuristic`
  - `"ml" -> ML.applyML`
  - any other value -> `InvalidOperationException` at startup with message `"Routing.Algorithm must be 'heuristic' or 'ml', got: {value}"` (fail fast)
- Default-on-absent is `"heuristic"` — bit-for-bit existing behavior preserved

### CLI override: `--routing-algorithm`
- Both syntaxes supported: `--routing-algorithm=ml` and `--routing-algorithm ml`
- Empty value (`--routing-algorithm=`) → reject at startup
- Invalid value → reject at startup with same error as config validation
- Multiple flag occurrences: last wins (`Array.tryFindIndexBack`)
- Parsed in `Program.fs` BEFORE `CompositionRoot.configureServices` is called; injected via `builder.Configuration.AddInMemoryCollection(dict ["Routing:Algorithm", value])` so the IOptions binding picks up the override (same ordering lesson as Phase 2's `startTestRouter`)

### Module isolation: `scripts/check-routing-isolation.sh`
- Mirrors `check-no-async.sh` shape (existing CI grep)
- Greps `src/SmartRouter.Core/Heuristic.fs` for any `open SmartRouter.Core.ML` or `ML.applyML` reference → fails build if found
- Same for `src/SmartRouter.Core/ML.fs` referencing Heuristic
- Exit 0 with `OK: routing modules isolated`

### Test strategy
- New file: `tests/SmartRouter.Tests/MLRoutingTests.fs` (NOT appended to RoutingTests.fs) — clearer phase ownership; tests the seam mechanics, not the heuristic decisions
- Tests:
  - **ML-01**: `Heuristic.applyHeuristic` and `ML.applyML` both have type `RoutingAlgorithm` (compiles when assigned to `RoutingAlgorithm`-typed binding)
  - **ML-02**: Placeholder `ML.applyML` returns `{ Target = Qwen35B; Reason = ML; Priority = Low }` for any input
  - **ML-03 dispatch test**: spin in-process Kestrel with `"Routing:Algorithm" = "ml"` → POST a request that would otherwise be heuristic-routed → assert the resolved `RoutingAlgorithm` from DI is `ML.applyML` (probe via singleton hash compare OR use a counter-fake injected at test time)
  - **ML-03 CLI test**: same in-process Kestrel pattern but pass `--routing-algorithm=ml` to args; config sets `"heuristic"`; CLI wins
  - **ML-04 isolation grep**: invoke `scripts/check-routing-isolation.sh` from a test (or document as CI verification only)
- All wrapped in `testSequenced` (Console.SetOut + Kestrel port discipline)

### Phase 5 forward-link (not in scope, but lock the decision now)
- `model_version` field that Phase 5 will write to JSONL needs a value during Phase 4. Use the constant string `"heuristic-v1"` for heuristic decisions and `"ml-v0-placeholder"` for placeholder ML decisions — surfaced via `RoutingConfig.ModelVersion` if needed, OR computed at log-time in Phase 5. Defer the wiring to Phase 5; just ensure the placeholder ML returns enough info that Phase 5 can tag correctly

### Claude's Discretion
- Exact F# file naming within Heuristic.fs and ML.fs (function names: `applyHeuristic` / `applyML` are locked, but helpers like `scoreComplexity` may be renamed when moved)
- Whether to test ML-04 grep via Bash from F# or document it as CI-only
- Test naming convention (e.g., "dispatch reads ml when configured" vs "ML applyML invoked when Algorithm=ml")
- Exact error message text for invalid algorithm values (just must include the offending value and the valid options)

</decisions>

<specifics>
## Specific Ideas

- Mirror Phase 2's `AddInMemoryCollection` ordering lesson: CLI override happens BEFORE `configureServices` in Program.fs
- Mirror blueCode's commit protocol: per-task atomic commits, never `git add .`
- The placeholder ML is BOTH a placeholder AND a contract test — it proves the seam works even before any real ML lands. Don't over-engineer it.
- Use existing `Reason = ML` DU case (not a new placeholder-specific reason) so Phase 6 swap is invisible to downstream code

</specifics>

<deferred>
## Deferred Ideas

- Real ML model (embeddings + classifier) — Phase 6
- `model_version` field schema and JSONL writer — Phase 5
- `Routing.ML` config sub-section (`ModelPath`, `EmbeddingModelPath`, `Threshold`) — Phase 6 will add; Phase 4 only needs `Routing.Algorithm`
- Health-aware fallback when ML model file is missing → Heuristic — Phase 6 will define; Phase 4 placeholder doesn't need a model file
- Telemetry/stats per algorithm — Phase 5 + 9 (canary cohort comparison)

</deferred>

---

*Phase: 04-ml-algorithm-seam*
*Context gathered: 2026-05-08*
