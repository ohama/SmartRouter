# Phase 4: ML Algorithm Seam — Research

**Researched:** 2026-05-08
**Domain:** F# module decomposition, config-driven function dispatch, DI function registration
**Confidence:** HIGH — all findings derived from direct codebase reading; no external dependencies required

---

## Summary

Phase 4 adds a routing-algorithm seam: `routeRequest` becomes parameterized by a function
(`RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision`), existing heuristic
logic moves to `Heuristic.fs`, a placeholder ML module lives in `ML.fs`, and CompositionRoot
reads `Routing.Algorithm` from config to register the function in DI. The CLI flag
`--routing-algorithm=...` overrides config at startup via `AddInMemoryCollection`. The
placeholder `applyML` always returns `{ Target = Qwen35B; Priority = Low; Reason = ML }`.

The primary technical challenge is the F# compilation-order constraint: `Heuristic.fs` and
`ML.fs` must compile before `Routing.fs` (which uses them). The `<Compile>` order in
`SmartRouter.Core.fsproj` must be updated accordingly.

The secondary challenge is the `RoutingReason.ML` DU case addition: `TreatWarningsAsErrors=true`
is set on both Core and Cli projects, so any incomplete `match` expression on `RoutingReason`
becomes a compile error. The good news: ALL existing `match d.Reason with` expressions in
`RoutingTests.fs` use a catch-all `| r -> failtestf ...` arm, so those tests do NOT need
updates. The only places that construct or match `RoutingReason` are in the source modules that
this phase touches anyway.

The overall scope is mechanical and small: ~200 LOC code changes, ~100 LOC tests, three plans
as already outlined in ROADMAP.md.

**Primary recommendation:** Flat file layout (no subdirectory) is simpler than
`src/SmartRouter.Core/Routing/` subdirectory — avoids namespace segment mismatch and matches
the existing `Domain.fs` / `Routing.fs` / `Ports.fs` flat pattern. Use
`SmartRouter.Core.Heuristic` and `SmartRouter.Core.ML` as the module names.

---

## Standard Stack

No new NuGet packages needed for Phase 4. Everything is pure F# with existing dependencies.

### Core (existing, unchanged)
| Component | Version | Purpose |
|-----------|---------|---------|
| F# / .NET 10 | net10.0 | Language + runtime |
| FsToolkit.ErrorHandling | 5.2.0 | Already in Core.fsproj (not used in routing but present) |
| Microsoft.Extensions.DependencyInjection | via ASP.NET | DI for function registration |

### No new packages
Phase 4's `applyML` is a pure function returning a hardcoded `RoutingDecision`. No ML.NET,
no embeddings, no external libraries. Those come in Phase 6.

---

## Architecture Patterns

### F1: Flat File Layout (RECOMMENDED)

The handoff doc shows a `Routing/` subdirectory, but the existing project uses a flat layout.
A flat layout is simpler and avoids F# namespace segment complications:

```
src/SmartRouter.Core/
├── Domain.fs          (unchanged)
├── Heuristic.fs       (NEW — moved from bottom of Routing.fs)
├── ML.fs              (NEW — placeholder applyML)
├── Routing.fs         (CHANGED — imports Heuristic + ML via open, new routeRequest signature)
└── Ports.fs           (unchanged)
```

Module names: `SmartRouter.Core.Heuristic` and `SmartRouter.Core.ML`.

If the planner prefers the subdirectory approach (matching the handoff doc's `Routing/` layout),
both `.fsproj` `<Compile>` paths and module declarations must use `Routing.Heuristic` and
`Routing.ML`. The flat approach is less risky.

### F2: .fsproj Compile Order

`SmartRouter.Core.fsproj` currently:
```xml
<Compile Include="Domain.fs" />
<Compile Include="Routing.fs" />
<Compile Include="Ports.fs" />
```

After Phase 4 (flat layout):
```xml
<Compile Include="Domain.fs" />
<Compile Include="Heuristic.fs" />
<Compile Include="ML.fs" />
<Compile Include="Routing.fs" />
<Compile Include="Ports.fs" />
```

Critical: `Heuristic.fs` and `ML.fs` MUST appear BEFORE `Routing.fs`. `Routing.fs` references
`Heuristic.applyHeuristic` and `ML.applyML` (or defines the type alias that both must satisfy),
so they must be compiled first.

### F3: RoutingAlgorithm Type Alias Location

**Recommendation: Define in `Domain.fs`**, not in `Routing.fs`.

Rationale: `RoutingAlgorithm` is a function type parameterized by `RoutingConfig` and
`RouterRequest`, both already in `Domain.fs`. Placing the type alias there means `Heuristic.fs`,
`ML.fs`, AND `Routing.fs` can all reference it without circular dependencies. If it lives in
`Routing.fs`, then `Heuristic.fs` and `ML.fs` cannot reference the type alias (they compile
before `Routing.fs`).

```fsharp
// Domain.fs — add after RoutingConfig:
/// Function type for pluggable routing algorithms.
/// Both applyHeuristic and applyML must satisfy this signature.
/// RoutingConfig is passed at each call (not captured at startup) so config
/// changes are reflected without re-registering the function.
type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision
```

### F4: Heuristic.fs Contents

Move the following from `Routing.fs` to `Heuristic.fs`:
- `scoreComplexity` (lines 97–122)
- `applyHeuristic` (lines 127–133)

Keep in `Routing.fs` (dispatcher + helpers that everything else depends on):
- `tryParseModelAlias`
- `tryModelOverride`
- `tryParseTaskType`
- `taskToDecision`
- `tryTaskTable`
- `routeRequest` (with new signature)
- `canonicalTaskTable`
- `canonicalKeywords`
- `defaultRoutingConfig`

```fsharp
// src/SmartRouter.Core/Heuristic.fs
module SmartRouter.Core.Heuristic

open SmartRouter.Core.Domain

let scoreComplexity (config: RoutingConfig) (req: RouterRequest) : int =
    // ... exact copy from Routing.fs lines 97-122 ...

let applyHeuristic (config: RoutingConfig) (req: RouterRequest) : RoutingDecision =
    let score  = scoreComplexity config req
    let target = if score >= config.ComplexityThreshold then Qwen122B else Qwen35B
    { Target     = target
      Priority   = Low
      Reason     = Heuristic score
      IsFallback = false }
```

### F5: ML.fs Contents (Placeholder)

```fsharp
// src/SmartRouter.Core/ML.fs
module SmartRouter.Core.ML

open SmartRouter.Core.Domain

/// Placeholder ML routing algorithm.
/// Phase 4: intentionally dumb — always picks Qwen35B with ML reason.
/// The value of this phase is the dispatch seam, not the algorithm.
/// Phase 6 replaces this with a real embedding + classifier.
/// NO import of SmartRouter.Core.Heuristic — zero cross-imports (ML-04).
let applyML (config: RoutingConfig) (req: RouterRequest) : RoutingDecision =
    // Suppress unused-parameter warnings without touching config/req contents.
    // Phase 6 will use both.
    ignore config
    ignore req
    { Target     = Qwen35B
      Priority   = Low
      Reason     = ML
      IsFallback = false }
```

Note: `Reason = ML` requires adding `| ML` to `RoutingReason` in `Domain.fs` first.

### F6: Routing.fs After Refactor

```fsharp
module SmartRouter.Core.Routing

open SmartRouter.Core.Domain
open SmartRouter.Core.Heuristic  // for scoreComplexity reference in tests helpers

// ── Stage 1: explicit model override ─────────────────────────────────────────
// (tryParseModelAlias, tryModelOverride — unchanged)

// ── Stage 2: explicit task table ─────────────────────────────────────────────
// (tryParseTaskType, taskToDecision, tryTaskTable — unchanged)

// ── Pipeline entry point ──────────────────────────────────────────────────────

/// Three-stage routing pipeline. Algorithm parameter selects which function
/// runs at stage 3 (heuristic or ML placeholder). Both satisfy RoutingAlgorithm.
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

// ── Helpers for tests + startup validation ────────────────────────────────────
// (canonicalTaskTable, canonicalKeywords, defaultRoutingConfig — unchanged)
```

The `open SmartRouter.Core.Heuristic` in `Routing.fs` is needed only if `scoreComplexity` or
`applyHeuristic` are referenced in `Routing.fs` helpers (e.g., `defaultRoutingConfig` doesn't
use them — it's fine). Verify: `defaultRoutingConfig` only uses `canonicalKeywords` and
`canonicalTaskTable`, both staying in `Routing.fs`. So the import may not be needed unless
the planner wants to expose `Heuristic.applyHeuristic` through `Routing` module for backwards
compat — not recommended (callers should `open SmartRouter.Core.Heuristic` directly).

### F7: RoutingReason.ML DU Case

```fsharp
// Domain.fs — existing RoutingReason DU, add ML case:
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Heuristic             of score: int
    | Default
    | ML    // ← NEW: decision was made by the ML algorithm (applyML)
```

**Incomplete-match risk with `TreatWarningsAsErrors=true`:**
Existing match expressions on `RoutingReason`:
- `RoutingTests.fs` (7 match expressions) — ALL use `| r -> failtestf ...` catch-all. **No update needed.**
- `QueueTests.fs` / `LoadTests.fs` — use `Reason = Default` for construction only (no match). **No update needed.**
- `ChatCompletions.fs` — uses `decision.Reason` only in Serilog structured-logging calls (passing as object, not pattern-matching). **No update needed.**
- `Domain.fs` — DU definition only. **Edit required** (add `| ML`).
- `ML.fs` — constructs `Reason = ML`. **New file** (uses it but doesn't match on it).

**Conclusion: Adding `| ML` to `RoutingReason` requires only `Domain.fs` edit. No match
expression changes needed anywhere in the existing codebase.**

This is the key safety finding. The existing `| r -> failtestf ...` catch-all arms in
RoutingTests make the DU extension non-breaking.

### F8: CompositionRoot Dispatch

Add `Algorithm: string` to `RoutingOptions`:

```fsharp
[<CLIMutable>]
type RoutingOptions =
    { ComplexityThreshold : int
      TimeoutSeconds      : int
      Keywords            : string[]
      TaskTable           : Dictionary<string, TaskTableEntry>
      ModelAliases        : Dictionary<string, string>
      Algorithm           : string }  // ← NEW: "heuristic" | "ml" | null (default heuristic)
```

Add `RoutingAlgorithm` singleton registration in `configureServices`:

```fsharp
// In configureServices, after AddSingleton<RoutingConfig>:
services.AddSingleton<RoutingAlgorithm>(fun sp ->
    let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
    match opts.Algorithm with
    | null | "" | "heuristic" -> Heuristic.applyHeuristic
    | "ml"                    -> ML.applyML
    | other ->
        let msg =
            sprintf "appsettings.json Routing.Algorithm = \"%s\" is invalid; valid values: \"heuristic\", \"ml\"" other
        raise (InvalidOperationException(msg)))
|> ignore
```

**Registration of F# function type as DI singleton:** `RoutingAlgorithm` is a type alias for
`RoutingConfig -> RouterRequest -> RoutingDecision`, which compiles to
`FSharpFunc<RoutingConfig, FSharpFunc<RouterRequest, RoutingDecision>>`. ASP.NET DI can register
it directly via `AddSingleton<RoutingAlgorithm>` as a factory — this works exactly like
`AddSingleton<RoutingConfig>` that already exists in `CompositionRoot.fs`.

### F9: ChatCompletions.fs Callsite Update

```fsharp
// ChatCompletions.fs mapEndpoints — resolve RoutingAlgorithm from DI:
let mapEndpoints (app: WebApplication) =
    app.MapPost("/v1/chat/completions", Func<HttpContext, Task>(fun ctx ->
        let routingConfig = ctx.RequestServices.GetRequiredService<RoutingConfig>()
        let algorithm     = ctx.RequestServices.GetRequiredService<RoutingAlgorithm>()
        let upstream      = ctx.RequestServices.GetRequiredService<IUpstreamClient>()
        handler routingConfig algorithm upstream ctx)) |> ignore

// ChatCompletions.fs handler signature:
let handler
    (routingConfig : RoutingConfig)
    (algorithm     : RoutingAlgorithm)
    (upstream      : IUpstreamClient)
    (ctx           : HttpContext) : Task =
    // ...
    // Change line 125:
    match routeRequest routingConfig algorithm req with
    // (rest unchanged)
```

### F10: Program.fs CLI Override

The key ordering constraint (SAME as Phase 2's `startTestRouter` lesson): CLI override via
`AddInMemoryCollection` MUST happen AFTER `CreateBuilder` and BEFORE `configureServices`.
`configureServices` calls `.Configure<RoutingOptions>(config.GetSection("Routing"))` which binds
from the config providers; `AddInMemoryCollection` overrides a specific key if inserted before
that call.

```fsharp
[<EntryPoint>]
let main args =
    Logging.configure ()
    try
        try
            let builder = WebApplication.CreateBuilder(args)
            builder.Host.UseSerilog() |> ignore

            // Parse --routing-algorithm BEFORE configureServices
            let routingAlgorithmOverride =
                args
                |> Array.tryFindIndex (fun a ->
                    a.StartsWith("--routing-algorithm=") ||
                    a = "--routing-algorithm")
                |> Option.bind (fun idx ->
                    if args.[idx].StartsWith("--routing-algorithm=") then
                        let v = args.[idx].["--routing-algorithm=".Length..]
                        if v = "" then
                            failwith "--routing-algorithm= requires a value (heuristic or ml)"
                        Some v
                    elif idx + 1 < args.Length then
                        let v = args.[idx + 1]
                        if v.StartsWith("--") then
                            failwith "--routing-algorithm requires a value (heuristic or ml)"
                        Some v
                    else
                        failwith "--routing-algorithm requires a value (heuristic or ml)")

            // Validate and inject override BEFORE configureServices
            match routingAlgorithmOverride with
            | None -> ()
            | Some v ->
                match v with
                | "heuristic" | "ml" -> ()
                | other ->
                    failwithf "--routing-algorithm=%s is invalid; valid values: heuristic, ml" other
                (builder.Configuration :> IConfigurationBuilder)
                    .AddInMemoryCollection([KeyValuePair("Routing:Algorithm", v)])
                |> ignore

            CompositionRoot.configureServices builder.Services builder.Configuration
            |> ignore

            // ... (rest unchanged)
```

**Edge cases:**
- `--routing-algorithm=ml` (equals form): `StartsWith("--routing-algorithm=")` → slice after `=`
- `--routing-algorithm ml` (space form): `Array.tryFindIndex` + `args.[idx+1]`
- `--routing-algorithm=` (empty): fail with clear error
- `--routing-algorithm=foobar` (invalid): fail at startup before `configureServices`
- Multiple occurrences: `tryFindIndex` returns the FIRST occurrence index — last-wins requires
  `Array.tryFindIndexBack`. Recommendation: use `Array.tryFindIndexBack` to get last-wins
  semantics (more natural for CLI overrides).
- `--routing-algorithm` as last arg (no value): `idx + 1 >= args.Length` → fail

### F11: appsettings.json Diff

```json
"Routing": {
  "Algorithm": "heuristic",
  "ComplexityThreshold": 3,
  ...
}
```

Add `"Algorithm": "heuristic"` as the first key in the `Routing` section. This makes the
default explicit; absent key also defaults to "heuristic" via the null-handling in
CompositionRoot (`null | "" | "heuristic" -> Heuristic.applyHeuristic`).

### F12: check-routing-isolation.sh

Mirrors `scripts/check-no-async.sh`. Enforces ML-04 (zero cross-imports):

```bash
#!/usr/bin/env bash
# scripts/check-routing-isolation.sh
# Enforces zero cross-imports between Heuristic.fs and ML.fs.
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
    if grep -n 'open SmartRouter.Core.ML\|SmartRouter\.Core\.ML\.' "$HEURISTIC_FS"; then
        echo "ERROR: Heuristic.fs references ML module — zero cross-imports required (ML-04)" >&2
        FAIL=1
    fi
fi

if [ -f "$ML_FS" ]; then
    if grep -n 'open SmartRouter.Core.Heuristic\|SmartRouter\.Core\.Heuristic\.' "$ML_FS"; then
        echo "ERROR: ML.fs references Heuristic module — zero cross-imports required (ML-04)" >&2
        FAIL=1
    fi
fi

if [ "$FAIL" -eq 1 ]; then
    exit 1
fi

echo "OK: routing modules isolated (Heuristic.fs and ML.fs have zero cross-imports)"
exit 0
```

---

## Test Impact Analysis

### RoutingTests.fs (22 tests) — CHANGES REQUIRED

The `route` helper (line 28):
```fsharp
// BEFORE:
let private route req = routeRequest defaultConfig req

// AFTER:
let private route req = routeRequest defaultConfig Heuristic.applyHeuristic req
```

Also add `open SmartRouter.Core.Heuristic` at the top.

The 7 `match d.Reason with` expressions — **no changes needed** (all use catch-all `| r -> failtestf`).

The 3 direct `routeRequest edited req` calls in config-driven tests (lines 171, 182, 188) also
need updating to `routeRequest edited Heuristic.applyHeuristic req`.

Count of callsites in RoutingTests.fs to update:
- `route req` calls: ~19 (unchanged call, helper updated)
- Direct `routeRequest edited req` calls: 3 (must be updated inline)
- `scoreComplexity defaultConfig ...` call (line 156): stays as-is (`scoreComplexity` moves to
  `Heuristic.fs`; need `open SmartRouter.Core.Heuristic` or qualify as `Heuristic.scoreComplexity`)

### StreamingTests.fs (8 tests) — CHANGES REQUIRED

`startTestRouter` uses `AddInMemoryCollection` with explicit Routing keys. After Phase 4,
`CompositionRoot.configureServices` will try to register `RoutingAlgorithm` based on
`opts.Algorithm`. If `Routing:Algorithm` is absent, `RoutingOptions.Algorithm` binds to `null`
(F# string default for missing CLIMutable field), which the CompositionRoot dispatch handles via
`null | "" | "heuristic" -> Heuristic.applyHeuristic`.

**Verdict: No test changes needed IF the null-case default is implemented.** But adding
`KeyValuePair("Routing:Algorithm", "heuristic")` to the `AddInMemoryCollection` list is the
safer, explicit approach and prevents future confusion. Recommended: add it.

### QueueTests.fs (9 tests) — NO CHANGES NEEDED

`QueueTests` builds its own minimal `WebApplication` for the `/stats` in-process test (Test 9),
but this test does NOT call `CompositionRoot.configureServices` — it builds a custom app with
only `AddSingleton<IStatsProvider>`. The other 8 queue tests use `QueueDispatcher` directly
without going through the routing pipeline. No changes needed.

### LoadTests.fs (2 ptestCaseAsync, pending) — NO CHANGES NEEDED

`LoadTests` uses `RoutingDecision` directly with `Reason = Default` (construction only, no
pattern match). Adding `| ML` to the DU does not break this. No changes needed.

### RouterTests.fs — NO CHANGES NEEDED

`RouterTests` is the entry point (`rootTests` + `[<EntryPoint>]`). It references the test modules
by name. No routing logic. No changes needed.

**Summary:**
| File | Changes |
|------|---------|
| `RoutingTests.fs` | Update `route` helper + 3 direct `routeRequest` calls + add `open SmartRouter.Core.Heuristic` |
| `StreamingTests.fs` | Add `KeyValuePair("Routing:Algorithm", "heuristic")` to `AddInMemoryCollection` (recommended; not strictly required if null-default is handled) |
| `QueueTests.fs` | None |
| `LoadTests.fs` | None |
| `RouterTests.fs` | None |

---

## New Tests Required (Phase 4)

### Test A: ML Dispatch Unit Test
Verify that when algorithm = ML.applyML, the returned decision has `Reason = ML`.
Does NOT need to boot Kestrel — pure function call:

```fsharp
testCase "routeRequest with ML algorithm returns Reason=ML for plain request" <| fun () ->
    let req = mkReq None None "hello" 1
    match routeRequest defaultConfig ML.applyML req with
    | Ok d ->
        match d.Reason with
        | ML -> ()  // expected
        | r -> failtestf "expected ML reason, got %A" r
    | Error e -> failtestf "expected Ok, got %A" e
```

### Test B: Config Dispatch Unit Test (Integration, in-process Kestrel)
Boot router with `Routing:Algorithm = "ml"`, send a request, verify the decision had
`Reason = ML`. But the endpoint doesn't return `RoutingDecision` directly to the client — it
forwards to upstream. This means the test needs either:

**Option D (RECOMMENDED):** Assert at the routing-function level. Since `RoutingAlgorithm`
is registered as a DI singleton, resolve it in a test and call it directly:

```fsharp
testCase "CompositionRoot registers ML.applyML when Algorithm=ml" <| fun () ->
    // Build minimal service collection with Algorithm=ml
    let services = ServiceCollection()
    let config =
        ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair("Routing:Algorithm", "ml")])
            .Build()
    CompositionRoot.configureServices services config |> ignore
    let sp = services.BuildServiceProvider()
    let algorithm = sp.GetRequiredService<RoutingAlgorithm>()
    // The registered function IS ML.applyML; verify by calling it
    let req = defaultRequest  // minimal RouterRequest
    let config = sp.GetRequiredService<RoutingConfig>()
    let decision = algorithm config req
    match decision.Reason with
    | ML -> ()
    | r -> failtestf "expected ML reason from ml-configured router, got %A" r
```

### Test C: CLI Override Test
Verify that `--routing-algorithm=ml` overrides `Routing:Algorithm=heuristic` in config:

```fsharp
testCase "CLI --routing-algorithm=ml overrides config Algorithm=heuristic" <| fun () ->
    // Simulate the Program.fs argument parsing + override logic
    // by constructing the same AddInMemoryCollection override
    let services = ServiceCollection()
    let config =
        ConfigurationBuilder()
            .AddInMemoryCollection([
                KeyValuePair("Routing:Algorithm", "heuristic")  // config says heuristic
                KeyValuePair("Routing:Algorithm", "ml")         // CLI override (wins)
            ])
            .Build()
    // ... same as Test B, assert Reason = ML
```

Note: `AddInMemoryCollection` called twice — the second call's keys override the first (same
key). This simulates the CLI-override ordering.

### Test D: Isolation Grep Test (NOT a unit test — a CI script)
`scripts/check-routing-isolation.sh` (see content above). Run in CI after build.

---

## Code Examples

### Complete routeRequest Signature Change

```fsharp
// Before (Routing.fs line 141):
let routeRequest (config: RoutingConfig) (req: RouterRequest) : Result<RoutingDecision, RouterError> =
    match tryModelOverride req with
    | Some decision -> Ok decision
    | None ->
        match tryTaskTable config req with
        | Error e          -> Error e
        | Ok (Some decision) -> Ok decision
        | Ok None          -> Ok (applyHeuristic config req)

// After:
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

### Domain.fs RoutingReason Addition

```fsharp
// Domain.fs — current RoutingReason:
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Heuristic             of score: int
    | Default

// After Phase 4:
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Heuristic             of score: int
    | Default
    | ML  // ← Phase 4 addition: decision made by ML algorithm (placeholder in Phase 4, real in Phase 6)
```

### Domain.fs RoutingAlgorithm Type Alias (add after RoutingConfig)

```fsharp
/// Function type for pluggable routing algorithms.
/// Satisfying signature: RoutingConfig -> RouterRequest -> RoutingDecision.
/// Heuristic.applyHeuristic and ML.applyML both conform to this type.
type RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision
```

---

## Common Pitfalls

### Pitfall 1: F# .fsproj Compile Order
**What goes wrong:** If `Routing.fs` is placed BEFORE `Heuristic.fs` or `ML.fs` in the
`<Compile>` list, the build fails because `Routing.fs` references symbols from files that
haven't been compiled yet. F# requires strict top-to-bottom compilation order.
**How to avoid:** `Heuristic.fs` first, then `ML.fs`, then `Routing.fs`. Always verify with
`dotnet build` after reordering.
**Warning signs:** Build error: "The value or constructor ... is not defined."

### Pitfall 2: RoutingAlgorithm Type Alias Placement
**What goes wrong:** If `RoutingAlgorithm` is defined in `Routing.fs` instead of `Domain.fs`,
then `Heuristic.fs` and `ML.fs` (which compile before `Routing.fs`) cannot reference the type
alias. Their function signatures would not be typed as `RoutingAlgorithm` — they'd just be
untyped F# function values that happen to match the shape.
**How to avoid:** Define `RoutingAlgorithm` in `Domain.fs`.
**Impact if wrong:** Functions will still type-check via structural typing, but the explicit
type annotation is lost. Tests that resolve `RoutingAlgorithm` from DI may fail if DI can't
find the type.

### Pitfall 3: CLI Override Ordering
**What goes wrong:** The `AddInMemoryCollection` override for `--routing-algorithm` must happen
BEFORE `configureServices`. If it's called after, `IOptions<RoutingOptions>` has already been
configured from the production config values, and the override is invisible to the DI factory.
**How to avoid:** Follow the same ordering as `StreamingTests.fs::startTestRouter` (which
learned this lesson in Phase 2): create builder, add overrides, THEN call configureServices.
**Warning signs:** `--routing-algorithm=ml` has no effect (router still uses heuristic).

### Pitfall 4: RoutingOptions.Algorithm Null vs Empty
**What goes wrong:** When `Routing.Algorithm` is absent from `appsettings.json`, the CLIMutable
`RoutingOptions.Algorithm: string` binds to `null` (not `""`). A match on just `""` will miss
the null case, causing a `NullReferenceException` or falling through to the `other` error branch.
**How to avoid:** Handle ALL three default cases: `null | "" | "heuristic"`.

### Pitfall 5: 22-Test Mass Callsite Update
**What goes wrong:** Updating `route req = routeRequest defaultConfig req` to pass the algorithm
also requires updating the 3 direct `routeRequest` calls in RoutingTests.fs that bypass the
helper. Updating the helper but not the direct calls leaves 3 failing tests.
**How to avoid:** Grep for `routeRequest` in RoutingTests.fs and update ALL occurrences.
Expected: 3 direct calls at lines 171, 182, 188.

### Pitfall 6: scoreComplexity Reference After Move
**What goes wrong:** `scoreComplexity` moves to `Heuristic.fs`. RoutingTests.fs line 156 calls
`scoreComplexity defaultConfig ...` directly. After the move, without adding
`open SmartRouter.Core.Heuristic`, this breaks.
**How to avoid:** Add `open SmartRouter.Core.Heuristic` to RoutingTests.fs. Alternatively
qualify as `Heuristic.scoreComplexity`.

### Pitfall 7: TreatWarningsAsErrors + Incomplete Match
**What goes wrong:** Both Core and Cli projects have `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
Adding `| ML` to `RoutingReason` triggers FS0025 (incomplete pattern match) for any `match`
expression that doesn't cover the new case. Despite no existing match expressions in Core or
Cli being affected (only catch-all arms exist), any new match on `RoutingReason` introduced
in Phase 4 code must cover `ML`.
**How to avoid:** In `ML.fs`, don't match on `RoutingReason`. In test code, use catch-all.

### Pitfall 8: DI Registration of F# Function Type
**What goes wrong:** `RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision`
is an F# type alias. DI registration as `AddSingleton<RoutingAlgorithm>` works because the
type alias expands to a concrete .NET type. However, if the type alias is in a module that
doesn't compile before `CompositionRoot.fs`, the reference fails.
**How to avoid:** `Domain.fs` compiles as part of `SmartRouter.Core.fsproj` which is a
ProjectReference; all its types are available in Cli.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead |
|---------|-------------|-------------|
| Config key override in tests | Custom config injection | `AddInMemoryCollection` (already used in StreamingTests) |
| Algorithm validation at startup | Runtime isinstance check | Match expression with `InvalidOperationException` |
| Cross-import check | Custom linter | `grep` in bash script (mirrors check-no-async.sh) |

---

## Phase 4 Pitfall Summary for Planner

| # | Risk | Severity | Mitigation |
|---|------|----------|------------|
| 1 | .fsproj compile order wrong | **HIGH** | Heuristic.fs, ML.fs, Routing.fs (strict order) |
| 2 | RoutingAlgorithm type alias in wrong file | HIGH | Define in Domain.fs, not Routing.fs |
| 3 | CLI override after configureServices | HIGH | Parse args before configureServices call |
| 4 | null Algorithm not handled in dispatch | MEDIUM | `null \| "" \| "heuristic"` in match |
| 5 | 3 direct routeRequest calls missed in RoutingTests | MEDIUM | Grep for all occurrences |
| 6 | scoreComplexity reference broken after move | MEDIUM | Add `open SmartRouter.Core.Heuristic` in RoutingTests |
| 7 | TreatWarningsAsErrors + new match arm | LOW | All existing matches use catch-all; new code must cover ML |
| 8 | StreamingTests startTestRouter missing Algorithm key | LOW | Add `Routing:Algorithm heuristic` or handle null default |

---

## Open Questions

1. **Flat vs subdirectory layout**
   - What we know: handoff doc shows `Routing/` subdir; existing project uses flat layout
   - What's unclear: planner preference
   - Recommendation: **flat** (less risk, fewer changes to .fsproj paths)

2. **`RoutingAlgorithm` open in `RoutingTests.fs`**
   - Tests currently `open SmartRouter.Core.Routing` (gets everything from Routing.fs)
   - After refactor, `Heuristic.applyHeuristic` is in a separate module
   - Recommendation: add `open SmartRouter.Core.Heuristic` to RoutingTests.fs

3. **`applyML` return target: Qwen35B or Qwen122B?**
   - Context says "always picks 35B"
   - Recommendation: `Qwen35B` (as specified). Distinguishable from heuristic which is config-driven.

4. **Test file for Phase 4 tests**
   - New test file `MLTests.fs` OR add to `RoutingTests.fs`?
   - Recommendation: new `MLRoutingTests.fs` (keeps Phase 4 tests isolated; add to .fsproj + RouterTests.fs rootTests)

---

## Sources

### Primary (HIGH confidence)
- Direct codebase reading: `Domain.fs`, `Routing.fs`, `Ports.fs`, `CompositionRoot.fs`,
  `ChatCompletions.fs`, `Program.fs`, `appsettings.json`, `SmartRouter.Core.fsproj`,
  `SmartRouter.Cli.fsproj`, `SmartRouter.Tests.fsproj`
- Test files: `RoutingTests.fs`, `StreamingTests.fs`, `QueueTests.fs`, `LoadTests.fs`,
  `RouterTests.fs`
- Phase 3 verification: `03-VERIFICATION.md` (confirms 39/39 tests passing baseline)
- Handoff doc: `handoff-to-smart-router.md` §2 (3-layer separation), §4.1 (prompt template),
  §5 (invariants)
- ROADMAP.md Phase 4 requirements (ML-01..04 and 5 success criteria)

### Secondary (HIGH confidence, design-derived)
- F# compilation order: well-known F# constraint (all modules must be declared before use)
- ASP.NET DI with F# function types: same pattern as `AddSingleton<RoutingConfig>` already in codebase

---

## Metadata

**Confidence breakdown:**
- File layout and compile order: HIGH — F# .fsproj is deterministic
- RoutingReason DU extension safety: HIGH — verified all match sites use catch-all
- DI registration of F# function type: HIGH — analogous pattern already in codebase
- CLI arg parsing: HIGH — same technique as StreamingTests AddInMemoryCollection
- Test impact: HIGH — verified by reading all test files

**Research date:** 2026-05-08
**Valid until:** Phase 4 execution (no external dependencies; all findings from local codebase)
