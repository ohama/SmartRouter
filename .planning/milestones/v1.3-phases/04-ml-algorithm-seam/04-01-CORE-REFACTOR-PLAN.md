---
phase: 04-ml-algorithm-seam
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Core/Domain.fs
  - src/SmartRouter.Core/Heuristic.fs
  - src/SmartRouter.Core/ML.fs
  - src/SmartRouter.Core/Routing.fs
  - src/SmartRouter.Core/SmartRouter.Core.fsproj
  - tests/SmartRouter.Tests/RoutingTests.fs
  - scripts/check-routing-isolation.sh
autonomous: true

must_haves:
  truths:
    - "Domain.fs declares `type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` and `RoutingReason` includes the `| ML` case"
    - "Heuristic.fs is a sibling module containing `applyHeuristic` and `scoreComplexity` moved verbatim from Routing.fs (no behavior change)"
    - "ML.fs is a sibling module containing only `applyML` returning `{ Target = Qwen35B; Priority = Low; Reason = ML; IsFallback = false }`"
    - "Heuristic.fs and ML.fs have ZERO references to each other (verified by scripts/check-routing-isolation.sh)"
    - "routeRequest signature is `RoutingConfig -> RoutingAlgorithm -> RouterRequest -> Result<RoutingDecision, RouterError>`"
    - "All 39 existing tests pass after the refactor (RoutingTests 22 + StreamingTests 8 + QueueTests 9) — `dotnet test` reports 39/39"
    - "scripts/check-routing-isolation.sh exits 0 with `OK: routing modules isolated` and matches the existing check-no-async.sh shape"
  artifacts:
    - path: "src/SmartRouter.Core/Domain.fs"
      provides: "RoutingAlgorithm type alias + RoutingReason.ML DU case"
      contains: "type RoutingAlgorithm"
    - path: "src/SmartRouter.Core/Heuristic.fs"
      provides: "applyHeuristic + scoreComplexity (moved from Routing.fs)"
      contains: "module SmartRouter.Core.Heuristic"
    - path: "src/SmartRouter.Core/ML.fs"
      provides: "applyML placeholder (always Qwen35B, Reason = ML)"
      contains: "module SmartRouter.Core.ML"
    - path: "src/SmartRouter.Core/Routing.fs"
      provides: "routeRequest with algorithm parameter; tryModelOverride + tryTaskTable preserved"
      contains: "algorithm config req"
    - path: "src/SmartRouter.Core/SmartRouter.Core.fsproj"
      provides: ".fsproj <Compile> order: Domain.fs, Heuristic.fs, ML.fs, Routing.fs, Ports.fs"
      contains: "Heuristic.fs"
    - path: "scripts/check-routing-isolation.sh"
      provides: "CI grep enforcing zero cross-imports between Heuristic.fs and ML.fs"
      contains: "check-routing-isolation"
  key_links:
    - from: "src/SmartRouter.Core/Routing.fs"
      to: "RoutingAlgorithm in Domain.fs"
      via: "algorithm parameter passed at stage 3"
      pattern: "algorithm config req"
    - from: "tests/SmartRouter.Tests/RoutingTests.fs"
      to: "Heuristic.applyHeuristic"
      via: "route helper passes Heuristic.applyHeuristic explicitly"
      pattern: "Heuristic\\.applyHeuristic"
    - from: ".fsproj <Compile> order"
      to: "F# top-to-bottom compilation"
      via: "Heuristic.fs and ML.fs come BEFORE Routing.fs"
      pattern: "Heuristic\\.fs.*ML\\.fs.*Routing\\.fs"
---

<objective>
Phase 4 Wave 1: Refactor `Routing.fs` into flat sibling modules `Heuristic.fs` and `ML.fs` while preserving exact heuristic behavior. Introduce `RoutingAlgorithm` type alias in `Domain.fs` and `| ML` case on `RoutingReason`. Change `routeRequest` to take a `RoutingAlgorithm` parameter and update every callsite (test helper + 3 direct calls). Add CI grep script enforcing zero cross-imports between Heuristic.fs and ML.fs.

Purpose: This wave is THE refactor. Everything in waves 2 and 3 (config dispatch, CLI override, new tests) compiles against the seam this wave produces. The phase fails if 39 baseline tests do not stay green here.

Output:
- Domain.fs with `RoutingAlgorithm` alias + `RoutingReason.ML` case
- Heuristic.fs and ML.fs as flat sibling modules
- Routing.fs slimmed to dispatcher + helpers; new `routeRequest` signature
- SmartRouter.Core.fsproj with new <Compile> order (Heuristic + ML before Routing)
- RoutingTests.fs migrated to pass Heuristic.applyHeuristic explicitly (route helper + 3 direct calls)
- scripts/check-routing-isolation.sh ready for CI
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/phases/04-ml-algorithm-seam/04-CONTEXT.md
@.planning/phases/04-ml-algorithm-seam/04-RESEARCH.md
@src/SmartRouter.Core/Domain.fs
@src/SmartRouter.Core/Routing.fs
@src/SmartRouter.Core/SmartRouter.Core.fsproj
@tests/SmartRouter.Tests/RoutingTests.fs
@scripts/check-no-async.sh
</context>

<tasks>

<task type="auto">
  <name>Task 1: Domain.fs — add RoutingAlgorithm alias and RoutingReason.ML case</name>
  <files>src/SmartRouter.Core/Domain.fs</files>
  <action>
Edit `src/SmartRouter.Core/Domain.fs`:

1. Locate the `RoutingReason` DU. Add a new case `| ML` AFTER `| Default`. Final shape:
   ```fsharp
   type RoutingReason =
       | ExplicitModelOverride of requestedAlias: string
       | ExplicitTask          of taskType: TaskType
       | Heuristic             of score: int
       | Default
       | ML
   ```

2. After `RoutingConfig` is fully defined (so it can be referenced), add a new function-type alias near the bottom of Domain.fs but before any other types that reference it:
   ```fsharp
   /// Function type for pluggable routing algorithms.
   /// Both Heuristic.applyHeuristic and ML.applyML conform to this shape.
   /// Returns RoutingDecision (NOT Result) — error paths owned by tryTaskTable upstream.
   type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision
   ```

DO NOT touch any other DU or record. DO NOT add a comment to existing cases. Keep the cosmetic style (`|` alignment) consistent with the surrounding code.

Pitfall reminder: TreatWarningsAsErrors=true. RESEARCH.md confirms all existing match expressions on `RoutingReason` use catch-all `| r -> ...` arms, so adding `| ML` does NOT trigger FS0025 in any existing file. Do not introduce new match expressions on RoutingReason in this task.
  </action>
  <verify>
```bash
dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj 2>&1 | tail -10
```
Expected: `Build succeeded` with no FS0025 incomplete-match warnings, no FS0039 unbound-name errors.

Then grep:
```bash
grep -n "type RoutingAlgorithm" src/SmartRouter.Core/Domain.fs
grep -n "| ML" src/SmartRouter.Core/Domain.fs
```
Both should match.
  </verify>
  <done>
Domain.fs contains both `type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` and `| ML` case on RoutingReason. Project compiles standalone (build succeeds — Routing.fs may temporarily reference symbols that haven't moved yet; if so, this task can be paired with Task 2 in a single edit-then-build cycle, but Domain.fs in isolation must be syntactically valid).
  </done>
</task>

<task type="auto">
  <name>Task 2: Create Heuristic.fs + ML.fs; refactor Routing.fs to use algorithm parameter; update .fsproj order</name>
  <files>
src/SmartRouter.Core/Heuristic.fs
src/SmartRouter.Core/ML.fs
src/SmartRouter.Core/Routing.fs
src/SmartRouter.Core/SmartRouter.Core.fsproj
  </files>
  <action>
This task is atomic — all four files change together because F# compile order is strict.

**Step A — Create `src/SmartRouter.Core/Heuristic.fs`:**

Module declaration: `module SmartRouter.Core.Heuristic`. Open `SmartRouter.Core.Domain`. Move `scoreComplexity` (current Routing.fs lines ~97–122) and `applyHeuristic` (current lines ~127–133) VERBATIM. Keep all signatures identical:

```fsharp
module SmartRouter.Core.Heuristic

open SmartRouter.Core.Domain

let scoreComplexity (config: RoutingConfig) (req: RouterRequest) : int =
    // ... exact body from Routing.fs (length scoring, keyword scan, code-block detect, message count) ...

let applyHeuristic (config: RoutingConfig) (req: RouterRequest) : RoutingDecision =
    let score  = scoreComplexity config req
    let target = if score >= config.ComplexityThreshold then Qwen122B else Qwen35B
    { Target     = target
      Priority   = Low
      Reason     = Heuristic score
      IsFallback = false }
```

CRITICAL: do not change behavior. Copy the existing function bodies char-for-char. Use Read on Routing.fs first to extract the exact source.

**Step B — Create `src/SmartRouter.Core/ML.fs`:**

```fsharp
module SmartRouter.Core.ML

open SmartRouter.Core.Domain

/// Placeholder ML routing algorithm.
/// Phase 4: intentionally dumb — always picks Qwen35B with ML reason.
/// The value of this phase is the dispatch seam, not the algorithm.
/// Phase 6 will replace this with a real embedder + classifier.
/// MUST NOT import SmartRouter.Core.Heuristic (zero cross-imports — ML-04).
let applyML (config: RoutingConfig) (req: RouterRequest) : RoutingDecision =
    ignore config
    ignore req
    { Target     = Qwen35B
      Priority   = Low
      Reason     = ML
      IsFallback = false }
```

NOTE: do NOT add `open SmartRouter.Core.Heuristic` here — that defeats ML-04.

**Step C — Refactor `src/SmartRouter.Core/Routing.fs`:**

1. DELETE `scoreComplexity` and `applyHeuristic` from this file (they live in Heuristic.fs now).
2. KEEP `tryParseModelAlias`, `tryModelOverride`, `tryParseTaskType`, `taskToDecision`, `tryTaskTable`, `canonicalTaskTable`, `canonicalKeywords`, `defaultRoutingConfig`.
3. Change `routeRequest` to accept the algorithm parameter:
   ```fsharp
   let routeRequest
       (config    : RoutingConfig)
       (algorithm : RoutingAlgorithm)
       (req       : RouterRequest)
       : Result<RoutingDecision, RouterError> =
       match tryModelOverride req with
       | Some decision -> Ok decision
       | None ->
           match tryTaskTable config req with
           | Error e            -> Error e
           | Ok (Some decision) -> Ok decision
           | Ok None            -> Ok (algorithm config req)
   ```
4. DO NOT add `open SmartRouter.Core.Heuristic` to Routing.fs — `routeRequest` no longer references heuristic functions; the algorithm is passed in.

**Step D — Update `src/SmartRouter.Core/SmartRouter.Core.fsproj`:**

Replace the existing `<Compile>` group with this exact order:
```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <Compile Include="Heuristic.fs" />
  <Compile Include="ML.fs" />
  <Compile Include="Routing.fs" />
  <Compile Include="Ports.fs" />
</ItemGroup>
```
(Heuristic.fs and ML.fs MUST appear before Routing.fs. F# compilation is top-to-bottom.)

DO NOT touch package references, target framework, or other ItemGroups.

**Note on interim build checks:** Steps A–D form one atomic edit. Do NOT add a build check between A+B (creating Heuristic.fs/ML.fs) and C+D (refactor + .fsproj order) — the new files are not yet listed in the .fsproj, and the old Routing.fs still references functions that have moved, so an interim build provides no useful signal. The post-Step-D `dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj` in `<verify>` produces F# errors with file:line precision pointing to whichever new file has a syntax problem (or to Routing.fs if a stale reference remains). That single check is sufficient — adding intermediate checks would slow execution without improving diagnosability.
  </action>
  <verify>
```bash
dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj 2>&1 | tail -10
```
Expected: `Build succeeded`. Errors during this task indicate either:
- compile order wrong (move Heuristic.fs/ML.fs above Routing.fs)
- a missing function reference in Routing.fs (someone left a `scoreComplexity` call behind — should be gone)

Cross-import check (early signal — proper Bash script comes in Task 5):
```bash
grep -n 'SmartRouter\.Core\.ML\|open SmartRouter\.Core\.ML' src/SmartRouter.Core/Heuristic.fs && echo "FAIL Heuristic references ML" || echo "OK Heuristic clean"
grep -n 'SmartRouter\.Core\.Heuristic\|open SmartRouter\.Core\.Heuristic' src/SmartRouter.Core/ML.fs && echo "FAIL ML references Heuristic" || echo "OK ML clean"
```
Expected: both print `OK ... clean`.
  </verify>
  <done>
Heuristic.fs and ML.fs exist as flat siblings of Routing.fs. SmartRouter.Core.fsproj <Compile> order is Domain → Heuristic → ML → Routing → Ports. `routeRequest` takes `algorithm: RoutingAlgorithm`. `dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj` succeeds. Heuristic.fs and ML.fs do not reference each other.
  </done>
</task>

<task type="auto">
  <name>Task 3: Update RoutingTests.fs callsites — route helper + 3 direct routeRequest calls + open Heuristic</name>
  <files>tests/SmartRouter.Tests/RoutingTests.fs</files>
  <action>
The `routeRequest` signature changed; without updates this single test file will break 22 tests.

1. Add `open SmartRouter.Core.Heuristic` near the existing `open SmartRouter.Core.Routing` import block at the top of the file. (Required because `scoreComplexity` moved to Heuristic.fs.)

2. Update the `route` helper (around line 28). Before:
   ```fsharp
   let private route req = routeRequest defaultConfig req
   ```
   After:
   ```fsharp
   let private route req = routeRequest defaultConfig Heuristic.applyHeuristic req
   ```

3. Locate the 3 direct `routeRequest edited req` callsites (RESEARCH.md lists approximate lines 171, 182, 188 — find them by greppping `routeRequest edited` in the file). Update each to:
   ```fsharp
   routeRequest edited Heuristic.applyHeuristic req
   ```

4. Confirm `scoreComplexity` is now resolved via `Heuristic.scoreComplexity` OR via the `open` you added in step 1. Do NOT qualify it manually if the `open` is in place — keep the call as `scoreComplexity defaultConfig ...` (the open brings it into scope).

Pitfall reminder: 22 tests use this file. Missing one direct call breaks 3 tests; missing the helper breaks ~19 tests. Grep `routeRequest` after editing and confirm zero unupdated 2-arg call patterns remain (`routeRequest defaultConfig req` or `routeRequest edited req` are the patterns to look for and eliminate).
  </action>
  <verify>
```bash
# Fail-fast grep: any remaining 2-arg routeRequest calls?
grep -nE 'routeRequest [a-zA-Z]+ req\s*$' tests/SmartRouter.Tests/RoutingTests.fs ; echo "Exit: $?"
```
Expected: no matches (exit 1). If anything matches, those are missed callsites.

```bash
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | tail -20
```
Expected: existing 39 tests still pass (22 RoutingTests + 8 StreamingTests + 9 QueueTests). LoadTests' 2 ptestCaseAsync remain ignored.

Note: if StreamingTests.fs fails because RoutingOptions.Algorithm is null, that means CompositionRoot dispatch isn't done yet (Task 4 in plan 04-02 will add it). Plan 04-01 doesn't add CompositionRoot dispatch — but build/tests should still pass because RoutingOptions.Algorithm doesn't exist yet, so nothing reads it. If a green test run is impossible at this boundary, scope verification to `dotnet build` only and let plan 04-02 close the loop.
  </verify>
  <done>
RoutingTests.fs has `open SmartRouter.Core.Heuristic`, the `route` helper passes `Heuristic.applyHeuristic`, and all 3 direct `routeRequest` calls pass `Heuristic.applyHeuristic`. `dotnet build` succeeds for the test project. If `dotnet test` runs, all 39 baseline tests pass.
  </done>
</task>

<task type="auto">
  <name>Task 4: Create scripts/check-routing-isolation.sh (CI grep enforcing ML-04)</name>
  <files>scripts/check-routing-isolation.sh</files>
  <action>
Create the script as a sibling to `scripts/check-no-async.sh`. Mirror its style (set -euo pipefail, exit codes 0/1/2, OK message on success).

Content:
```bash
#!/usr/bin/env bash
# scripts/check-routing-isolation.sh
# Enforces zero cross-imports between Heuristic.fs and ML.fs (ML-04).
# Mirrors scripts/check-no-async.sh shape.
# Exit 0 if clean; exit 1 on any violation; exit 2 if Core dir missing.

set -euo pipefail

CORE_DIR="src/SmartRouter.Core"
HEURISTIC_FS="${CORE_DIR}/Heuristic.fs"
ML_FS="${CORE_DIR}/ML.fs"

if [ ! -d "$CORE_DIR" ]; then
    echo "ERROR: $CORE_DIR does not exist (run from repository root)" >&2
    exit 2
fi

FAIL=0

if [ -f "$HEURISTIC_FS" ]; then
    if grep -nE 'open SmartRouter\.Core\.ML|SmartRouter\.Core\.ML\.' "$HEURISTIC_FS" ; then
        echo "" >&2
        echo "ERROR: $HEURISTIC_FS references ML module — zero cross-imports required (ML-04)" >&2
        FAIL=1
    fi
fi

if [ -f "$ML_FS" ]; then
    if grep -nE 'open SmartRouter\.Core\.Heuristic|SmartRouter\.Core\.Heuristic\.' "$ML_FS" ; then
        echo "" >&2
        echo "ERROR: $ML_FS references Heuristic module — zero cross-imports required (ML-04)" >&2
        FAIL=1
    fi
fi

if [ "$FAIL" -eq 1 ]; then
    exit 1
fi

echo "OK: routing modules isolated (Heuristic.fs and ML.fs have zero cross-imports)"
exit 0
```

Make it executable:
```bash
chmod +x scripts/check-routing-isolation.sh
```
  </action>
  <verify>
```bash
ls -l scripts/check-routing-isolation.sh
# Expected: -rwxr-xr-x (executable bit set)

bash scripts/check-routing-isolation.sh
# Expected: "OK: routing modules isolated (...)" and exit 0
echo "Exit: $?"
```

Negative-test (optional sanity check — DO NOT commit):
```bash
# Temporarily inject a violation, confirm script catches it, revert.
echo "open SmartRouter.Core.ML" >> src/SmartRouter.Core/Heuristic.fs
bash scripts/check-routing-isolation.sh ; echo "Exit: $?"  # expect 1
sed -i '' '$d' src/SmartRouter.Core/Heuristic.fs  # remove the appended line
bash scripts/check-routing-isolation.sh ; echo "Exit: $?"  # back to 0
```
  </verify>
  <done>
scripts/check-routing-isolation.sh exists, is executable, and reports `OK: routing modules isolated` for the Heuristic.fs and ML.fs created in Task 2. Exit code 0.
  </done>
</task>

</tasks>

<verification>
After all four tasks complete:

**IMPORTANT — boundary scope:** Full-solution `dotnet build SmartRouter.slnx` is expected to FAIL after Plan 04-01 because `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` still uses the OLD 2-arg `routeRequest routingConfig req` signature. The Cli callsite is updated in Plan 04-02 Task 1 Step C; full-solution build is expected to GREEN at the Plan 04-02 boundary, NOT at the 04-01 boundary. Therefore verification here is scoped to Core + Tests only.

```bash
# 1. Build SmartRouter.Core only (the refactored target).
dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj 2>&1 | tail -5
# Expected: Build succeeded.

# 2. Build SmartRouter.Tests (depends only on Core, NOT on Cli).
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tail -5
# Expected: Build succeeded.

# 3. Run tests in the test project — 39 must pass (LoadTests 2 ignored).
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-build 2>&1 | tail -10
# Expected: Passed: 39, Failed: 0, Ignored: 2.

# 4. Routing isolation enforced.
bash scripts/check-routing-isolation.sh
# Expected: "OK: routing modules isolated (...)" + exit 0.

# 5. .fsproj compile order is correct.
grep -A6 '<ItemGroup>' src/SmartRouter.Core/SmartRouter.Core.fsproj | grep -E 'Domain\.fs|Heuristic\.fs|ML\.fs|Routing\.fs|Ports\.fs'
# Expected order: Domain → Heuristic → ML → Routing → Ports.

# 6. RoutingAlgorithm alias and ML DU case present.
grep -n 'type RoutingAlgorithm\|| ML' src/SmartRouter.Core/Domain.fs
# Expected: at least one match for each.

# DO NOT run `dotnet build` (whole solution) or `dotnet build SmartRouter.slnx` here —
# the Cli project will fail to compile until Plan 04-02 Task 1 Step C lands.
```
</verification>

<success_criteria>
- Domain.fs contains `type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` and `RoutingReason` includes `| ML`.
- Heuristic.fs (module SmartRouter.Core.Heuristic) holds `applyHeuristic` and `scoreComplexity` with no behavior change.
- ML.fs (module SmartRouter.Core.ML) holds only `applyML` returning Qwen35B/Low/ML and does not import Heuristic.
- Routing.fs's `routeRequest` takes a `RoutingAlgorithm` parameter; tryModelOverride and tryTaskTable are unchanged.
- SmartRouter.Core.fsproj compiles in order Domain → Heuristic → ML → Routing → Ports.
- RoutingTests.fs route helper passes `Heuristic.applyHeuristic`; all 3 direct `routeRequest edited req` calls updated; `open SmartRouter.Core.Heuristic` added.
- scripts/check-routing-isolation.sh exits 0 with `OK: routing modules isolated`.
- `dotnet test` reports 39 passed, 2 ignored (load tests pending), 0 failed.
- ML-01 satisfied (alias + both functions conform). ML-04 satisfied (isolation grep clean).
</success_criteria>

<output>
After completion, create `.planning/phases/04-ml-algorithm-seam/04-01-SUMMARY.md` describing:
- Files created/modified
- 39/39 tests still passing
- check-routing-isolation.sh exit 0
- Any deviations from the plan (e.g., unexpected test breakage and how it was resolved)
- Forward-link reminder: 04-02 (config + DI dispatch + CLI override) is unblocked.
</output>
