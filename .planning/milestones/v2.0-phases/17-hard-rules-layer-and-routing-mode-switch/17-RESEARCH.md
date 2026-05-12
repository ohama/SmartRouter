# Phase 17: Hard Rules Layer + Routing.Mode Switch — Research

**Researched:** 2026-05-11
**Domain:** F# hexagonal routing pipeline — DU extension, pure BCL Core module, config-flag branch in CompositionRoot
**Confidence:** HIGH (all findings derived from direct source reads of the live codebase)

---

## Summary

Phase 17 ships two distinct but coupled changes: a pure keyword-matching pre-routing layer
(`HardRules.fs` in Core) and a config-flag gate (`Routing.Mode`) that branches
`CompositionRoot.configureRequestPipeline` into selfrouting vs ML paths. Both changes are
grounded in existing codebase patterns and require no new NuGet packages.

**HardRules** is a single pure function (`applyHardRules : RouterRequest -> RoutingDecision
option`) inserted as Stage 0 in `Routing.routeRequest`. It checks `String.Contains` across
all message content for six hardcoded keywords (LLVM, MLIR, compiler, segfault, optimization,
concurrency) and returns `Some` with `Reason=HardRule` immediately, bypassing Stages 1-3.
Per locked decision HR-02, the keyword list is NOT config-driven — it is hardcoded in
`HardRules.fs` and changing it requires a source edit.

**Routing.Mode** is a new `appsettings.json` string key (`"selfrouting"` | `"ml"`, default
`"selfrouting"`) read in `CompositionRoot.configureRequestPipeline` via
`config.["Routing:Mode"]` (same pattern as `Routing:Judge:Enabled` in Phase 16 and
`Trace:Enabled` in Phase 14). The mode gates only the `RoutingAlgorithmRegistration` factory
— ML DI (BgeM3Embedder, MlNetClassifier, RetrainingService) remains registered and running in
BOTH modes. Only the algorithm function inside the registration changes.

**Primary recommendation:** Build 17-01 (Core only: HardRules.fs + DU extension + Routing.fs
Stage 0 + DecisionLogger arm) as a fully self-contained plan. All five files that need
touching are well-understood and the change is compiler-enforced end-to-end.
`TreatWarningsAsErrors=true` with exhaustive DU match guarantees the build fails if any
`match reason with` site misses the new HardRule arm.

---

## Standard Stack

No new NuGet packages. All v2.0 features are implementable with the existing dependency set.

### Core
| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| `String.Contains(kw, StringComparison.OrdinalIgnoreCase)` | BCL net10.0 | Case-insensitive keyword scan | All 6 keywords are ASCII; no locale sensitivity; simpler and faster than Regex for 6 terms |
| F# DU exhaustive match with `TreatWarningsAsErrors=true` | compiler-enforced | Forces every `match reason with` site to handle `HardRule` | FS0025 (non-exhaustive match) becomes a build error; zero runtime gaps possible |
| `config.["Routing:Mode"]` flat key read | `IConfiguration` (existing) | Read Mode string without binding a new Options type | Mirrors existing `config.["Routing:Judge:Enabled"]` and `config.["Trace:Enabled"]` patterns in CompositionRoot lines 548-550, 511-513 |

### Supporting
| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| `RoutingOptions.Mode : string` (mutable field) | F# CLIMutable record | Bind `Routing.Mode` from JSON | Needed if Mode is bound through IOptions<RoutingOptions>; Phase 13-05 lesson: CLIMutable requires `mutable` for JSON binding. HOWEVER: see Architecture Pattern 2 — config.["Routing:Mode"] direct read may be cleaner for a single scalar |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `config.["Routing:Mode"]` direct read | New `ModeOptions` record + `IOptions<ModeOptions>` | Options binding adds a RoutingOptions.Mode mutable field; direct read is simpler for a single string scalar; mirrors Phase 14 `Trace:Enabled` and Phase 16 `Judge.Enabled` patterns |
| Hardcoded keyword list in HardRules.fs | `appsettings.json:Routing.HardRules.Keywords` array | Config-driven keywords could be misconfigured; locked decision (HR-02, STATE.md line 78) says keywords are hardcoded as safety mechanism |

**Installation:** No new packages required.

---

## Architecture Patterns

### Recommended Project Structure (new files only)

```
src/SmartRouter.Core/
├── Domain.fs               # +HardRule DU case (7th); no other changes to RouterRequest in Phase 17
├── HardRules.fs            # NEW: pure function, BCL-only, no DI
└── Routing.fs              # +Stage 0 call to HardRules.applyHardRules

src/SmartRouter.Cli/Adapters/
└── DecisionLogger.fs       # +7th arm in formatReason; "hard_rule" JSONL value

src/SmartRouter.Cli/
└── CompositionRoot.fs      # +RoutingOptions.Mode field; Mode-gated RoutingAlgorithmRegistration; appsettings.json Mode key

tests/SmartRouter.Tests/
└── HardRulesTests.fs       # NEW: pure unit tests for the 6 keywords + cascade ordering
```

### Pattern 1: HardRules.fs — Pure Core Module

**What:** A BCL-only module in `SmartRouter.Core` with a single public function.

**Key constraints from ARCH-01:** No Serilog, no HttpClient, no ASP.NET Core, no Microsoft.ML.
The function is synchronous (no `task {}` needed — no IO).

**Implementation:**
```fsharp
// Source: SmartRouter.Core/HardRules.fs
module SmartRouter.Core.HardRules

open SmartRouter.Core.Domain

let private keywords =
    [ "LLVM"; "MLIR"; "compiler"; "segfault"; "optimization"; "concurrency" ]

/// Stage 0 pre-routing: case-insensitive keyword scan over all message content.
/// Returns Some RoutingDecision (Target=Qwen122B, Reason=HardRule, Priority=High)
/// if any keyword matches; None otherwise.
/// Pure function — no IO, no DI, no mutation.
let applyHardRules (req: RouterRequest) : RoutingDecision option =
    let combined =
        req.Messages
        |> List.map (fun m -> m.Content)
        |> System.String.concat " "
    let matched =
        keywords
        |> List.exists (fun kw ->
            combined.Contains(kw, System.StringComparison.OrdinalIgnoreCase))
    if matched then
        Some { Target       = Qwen122B
               Priority     = High
               Reason       = HardRule
               IsFallback   = false
               ModelVersion = "" }
    else
        None
```

**Why `List.exists` not `List.tryFind`:** The function only needs `bool` — which keyword
matched is not propagated (Reason = HardRule, not HardRule of matchedKeyword).
Per HR-04, the DU case is `HardRule` (no payload).

### Pattern 2: Routing.fs Stage 0 Insertion

**What:** `routeRequest` gains a new Stage 0 match arm before `tryModelOverride`.

**Current structure (3-stage):**
```fsharp
let routeRequest config algorithm req =
    match tryModelOverride req with           // Stage 1
    | Some decision -> Ok decision
    | None ->
        match tryTaskTable config req with    // Stage 2
        | Error e            -> Error e
        | Ok (Some decision) -> Ok decision
        | Ok None            -> Ok (algorithm config req)   // Stage 3
```

**New structure (4-stage, Stage 0 prepended):**
```fsharp
let routeRequest config algorithm req =
    // Stage 0: Hard Rules — keyword scan; bypasses all other stages (HR-03)
    match HardRules.applyHardRules req with
    | Some decision -> Ok decision
    | None ->
    // Stage 1: explicit model override (unchanged)
    match tryModelOverride req with
    | Some decision -> Ok decision
    | None ->
    // Stage 2: explicit task table (unchanged)
    match tryTaskTable config req with
    | Error e            -> Error e
    | Ok (Some decision) -> Ok decision
    | Ok None            -> Ok (algorithm config req)   // Stage 3
```

**Critical cascade ordering:** Stage 0 (Hard Rules) runs BEFORE Stage 1 (model override).
This means `model=35b` + "LLVM in prompt" → Hard Rules wins, not model override.
Per HR-03 and STATE.md decision 5: this is INTENTIONAL — Hard Rules is a safety mechanism.

However, HR-06 requires that explicit model override AND explicit task field BYPASS Hard Rules
in unit tests. This is a spec conflict: HR-03 says Hard Rules is Stage 0 (before all), but
HR-06 says override + task bypass it. The resolution visible in the v2.0 ARCHITECTURE.md and
PITFALLS.md is that the cascade ORDER in `Routing.fs` places Hard Rules at Stage 0, but the
test spec (HR-06) describes the ordering verification — meaning the tests confirm that
explicit model override at Stage 1 and task table at Stage 2 each produce their OWN decisions
independent of Hard Rules. Given the code structure, `tryModelOverride` runs AFTER
`applyHardRules` — so HR-03 (Stage 0) actually DOES take precedence over model override.

**Resolution for the planner:** The locked decision in STATE.md line 35 lists the cascade as:
"Stage 0 Hard Rules → Stage 1 explicit model override → Stage 2 explicit task table → Stage 3
sticky → Stage 4 self-classify → Stage 5 default 35B". This confirms Hard Rules wins even
over model override. HR-06 tests that "explicit model override AND explicit task BYPASS Hard
Rules" — this contradicts HR-03. **Open question for planner: which spec wins?** The
ROADMAP.md Phase 17 Plan 17-01 says "explicit override + explicit task cascade ordering
verified by tests" but the cascade spec in STATE.md makes Hard Rules truly Stage 0. The
recommended resolution: Hard Rules fires FIRST per HR-03 (strict safety); HR-06 tests are
updated to test the cascade in the CORRECT order (Hard Rule > model override). This should be
flagged explicitly in the plan.

### Pattern 3: RoutingReason DU Extension

**Current DU (6 cases) in `Domain.fs` lines 31-37:**
```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Default
    | ML
    | FallbackTo35B
    | FallbackTo122B
```

**New DU (7th case appended):**
```fsharp
    | HardRule              // Phase 17: keyword match → immediate 122B
```

**All exhaustive match sites that MUST be updated (compiler will catch all with FS0025):**

| File | Location | Current arms | Update needed |
|------|----------|--------------|---------------|
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | `formatReason` function, line 31 | 6 arms | Add `\| HardRule -> "hard_rule"` |

That is the only current exhaustive match site. `QueueDispatcher.fs` uses `FallbackTo35B`
as a constructor (line 281, 358) but does not have an exhaustive match on `RoutingReason` —
it constructs values, not matches them. `ChatCompletions.fs` calls `formatReason` (which is
the exhaustive site). No other files contain `match reason with` or `match.*RoutingReason`.

**Verification:** `grep -n "match.*reason\|match.*RoutingReason\|FallbackTo35B\|FallbackTo122B"
src/SmartRouter.Cli/Adapters/*.fs src/SmartRouter.Core/*.fs` showed exactly 7 lines, all in
DecisionLogger.fs and Domain.fs. The QueueDispatcher.fs hits are constructors, not matches.

### Pattern 4: Routing.Mode Config Wiring

**The config read pattern** mirrors Phase 16 `Routing:Judge:Enabled` (CompositionRoot.fs line 549):
```fsharp
// Existing Phase 16 pattern (lines 548-550):
let judgeEnabled =
    let raw = config.["Routing:Judge:Enabled"]
    not (isNull raw) && raw.Equals("true", StringComparison.OrdinalIgnoreCase)

// Phase 17 pattern (new, analogous):
let routingMode =
    let raw = config.["Routing:Mode"]
    if isNull raw then "selfrouting"
    else raw.Trim().ToLowerInvariant()

// Startup validation (fail-fast on invalid value):
match routingMode with
| "selfrouting" | "ml" -> ()
| other ->
    raise (InvalidOperationException(
        sprintf "appsettings.json Routing.Mode value '%s' is not recognized; valid values are 'selfrouting' or 'ml'" other))
```

**Where in CompositionRoot:** The mode read and validation should happen in the
`RoutingAlgorithmRegistration` factory lambda (existing lines ~394-428). Currently that
factory unconditionally builds the ML registration. Phase 17 adds a `match routingMode with`
branch: `"ml"` → existing ML path; `"selfrouting"` → stub selfrouting registration.

**The stub selfrouting algorithm for Phase 17** (full SelfRouter lands in Phase 19): a
`RoutingAlgorithm` closure that returns the existing default behavior (Qwen35B, Default
reason). This is the minimal Phase 17 deliverable — it makes the mode switch functional
without requiring the Phase 19 SelfRouter adapter.

```fsharp
// Phase 17 stub — will be replaced in Phase 19 by makeSelfRoutingAlgorithm
let stubSelfRoutingAlgorithm : RoutingAlgorithm =
    fun _config _req ->
        { Target       = Qwen35B
          Priority     = Low
          Reason       = Default
          IsFallback   = false
          ModelVersion = "selfrouting-v1" }
```

**appsettings.json change:** Add `"Mode": "selfrouting"` to the `"Routing"` section.

### Pattern 5: CompositionRoot Branch Strategy — Option B (Layered, not if/else)

Per MODE-03, ML adapters (BgeM3Embedder, MlNetClassifier, RetrainingService, CanaryService)
REMAIN registered and RUNNING even when `Routing.Mode="selfrouting"`. Only the
`RoutingAlgorithmRegistration.Algorithm` function changes.

This means: **No if/else split of `configureRequestPipeline`**. The existing unconditional ML
wiring block (lines 339-428 of CompositionRoot.fs) stays completely unconditional. Only the
`RoutingAlgorithmRegistration` factory reads the mode and branches.

The comment on line 337 currently says "ML wiring is unconditional". Phase 17 does not change
this invariant — it only changes which `RoutingAlgorithmRegistration` gets registered.

**Why this matters for RetrainingService:** RetrainingService accumulates training data
regardless of mode. If we gated ML DI registration entirely on `Routing.Mode="ml"`, the
training dataset would stop growing when mode is `"selfrouting"`. MODE-03 explicitly says
this should NOT happen.

### Pattern 6: fsproj Compile Order

**SmartRouter.Core.fsproj** — current compile order:
```
Domain.fs       → defines RoutingReason (no HardRule yet)
MLPorts.fs
CanaryPorts.fs
RetrainingPorts.fs
ML.fs
Routing.fs      → calls algorithm (currently no HardRules call)
Ports.fs
```

**Required new order:**
```
Domain.fs       → add HardRule DU case
MLPorts.fs
CanaryPorts.fs
RetrainingPorts.fs
HardRules.fs    ← INSERT HERE (after Domain.fs, before ML.fs; depends on Domain only)
ML.fs           → unchanged
Routing.fs      → calls HardRules.applyHardRules; must come after HardRules.fs
Ports.fs
```

**SmartRouter.Cli.fsproj** — no new files in Phase 17 (HardRules.fs is Core-only). The
`DecisionLogger.fs` is already compiled in position 7 and just needs its `formatReason`
function updated in-place. `CompositionRoot.fs` is updated in-place. No new `<Compile>`
entries needed in Cli.fsproj for Phase 17.

### Anti-Patterns to Avoid

- **Putting HardRules.fs in SmartRouter.Cli:** Violates ARCH-01 and makes the function
  impossible to test without ASP.NET scaffolding. It belongs in Core — pure BCL, no DI.
- **Gating ML DI registration on Routing.Mode:** Violates MODE-03. RetrainingService must
  continue accumulating data regardless of mode. Only the algorithm function changes, not
  the ML adapter registrations.
- **Using `async {}` in HardRules.fs:** HardRules is synchronous (string ops only).
  ARCH-02 mandates `task {}` for async, but HardRules needs neither.
- **Adding `| _ ->` to `formatReason`:** Defeats the compiler-enforced exhaustive match.
  Any future DU case addition would silently fall through. NEVER add a catch-all arm.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Case-insensitive multi-keyword scan | Custom Regex / Aho-Corasick | `String.Contains(kw, OrdinalIgnoreCase)` | 6 ASCII keywords; String.Contains is O(n·m) but n=prompt length and m=6; simpler, zero deps |
| Config validation at startup | Custom validator class | `InvalidOperationException` throw in `configureRequestPipeline` or `RoutingAlgorithmRegistration` factory | Existing pattern: `buildRoutingConfig` throws `InvalidOperationException` on bad task entries (CompositionRoot line 103); mirrored by `validateConfig` function (line 165) |
| Exhaustive DU match enforcement | Runtime checks | F# compiler + `TreatWarningsAsErrors=true` | FS0025 becomes a build error; the compiler IS the enforcement |

**Key insight:** The F# compiler with `TreatWarningsAsErrors=true` eliminates the entire class
of "forgot to handle new DU case" bugs. Adding `HardRule` to `RoutingReason` without updating
`formatReason` will fail the build — zero runtime gaps.

---

## Common Pitfalls

### Pitfall 1: HR-03 vs HR-06 Spec Conflict on Cascade Ordering

**What goes wrong:** HR-03 says Hard Rules is Stage 0 (before explicit model override). HR-06
says explicit model override and explicit task BYPASS Hard Rules. These are contradictory.
The current `Routing.fs` pipeline (tryModelOverride before algorithm) means Stage 0 = before
tryModelOverride. If Hard Rules truly fires before model override, then `model=35b` + LLVM
prompt → Hard Rules wins → 122B. HR-06 wants the opposite.

**Root cause:** The requirements were written from different perspectives (HR-03 from the
cascade perspective, HR-06 from the "override wins" usability perspective).

**How to avoid:** The locked STATE.md cascade order is authoritative: Stage 0 Hard Rules →
Stage 1 model override. HR-03 wins. The unit tests in HR-06 should be written to verify
that Hard Rules fires for no-override prompts, and that the FULL cascade is tested (not that
override beats Hard Rules — which it doesn't under the locked spec).

**Warning signs:** If the planner writes tests that assert `model=35b` + LLVM → 35B, that
test will fail (correctly) because Hard Rules fires first. The planner must resolve this
before writing the test plan.

### Pitfall 2: RoutingOptions Record Mutation — CLIMutable + mutable

**What goes wrong:** Adding `Mode: string` to `RoutingOptions` (the CLIMutable record in
CompositionRoot.fs line 76-82) requires the field to be `mutable`. Without `mutable`, the
JSON binder (System.Text.Json) silently ignores the field, leaving `Mode` as `null` or
`""`, and the routing mode read reads wrong/null.

**Root cause:** F# records with `[<CLIMutable>]` require each field to be explicitly
`mutable` for reflection-based JSON binding to write to it (Phase 13-05 lesson, confirmed
in existing code — all RoutingOptions fields use `mutable`).

**How to avoid:** Add `mutable Mode : string` to `RoutingOptions`. Alternatively, read the
mode directly from `config.["Routing:Mode"]` (which bypasses CLIMutable binding entirely)
— this is the recommended approach per the Phase 16 Judge.Enabled pattern.

### Pitfall 3: HardRules.fs Position in Core.fsproj

**What goes wrong:** If `HardRules.fs` is placed AFTER `Routing.fs` in Core.fsproj,
`Routing.fs` cannot reference `HardRules.applyHardRules` (F# requires referenced modules
to appear earlier in compile order). The build fails with a name-not-found error.

**How to avoid:** Insert `<Compile Include="HardRules.fs" />` AFTER `RetrainingPorts.fs`
and BEFORE `ML.fs` in `SmartRouter.Core.fsproj`. HardRules.fs depends only on Domain.fs
(for `RouterRequest` and `RoutingDecision`), which is the first file. `Routing.fs` (which
calls `HardRules.applyHardRules`) must stay AFTER `HardRules.fs`.

### Pitfall 4: Test Fixture Request Content Conflicts with Hard Rules Keywords

**What goes wrong:** If any existing test sends a request whose content contains one of the
6 keywords (LLVM, MLIR, compiler, segfault, optimization, concurrency), the Hard Rules Stage
0 will intercept it and route to 122B instead of the test's expected decision.

**Verified:** Grep of test files for the 6 keywords found only two non-test-relevant hits in
LoadTests.fs (English comments about "concurrency cap") and MLEmbeddingTests.fs ("analyze F#
compiler error" as a string label — but this is an embedding similarity test, NOT passed
through `routeRequest`, so it is NOT affected).

**Tests that call `routeRequest` directly (pure routing tests):** MLRoutingTests.fs uses
content `"hello"` — no keyword conflict.

**Tests that use the fake Kestrel host (StreamingTests, LoggingTests, QualityFallbackTests,
etc.):** These send content via `fakePort` HTTP. Once HardRules.fs is wired into
`Routing.routeRequest`, ALL requests going through the full stack will be subject to Hard
Rules. The test stub content is `"hello"` or similar — no keyword conflict identified.

**Warning sign:** If a test fails after Phase 17 with unexpected 122B routing, check the
prompt content for keyword matches.

### Pitfall 5: startup Banner + configureServices Alias Migration

**What goes wrong:** Program.fs currently calls `configureServices` (the backwards-compat
alias at CompositionRoot.fs line 1139 → `configureRequestPipeline`). The startup banner
reads `regn.Name` and `regn.ModelVersion` from `RoutingAlgorithmRegistration`. After Phase
17, the banner should show `"selfrouting"` as the algorithm name when
`Routing.Mode="selfrouting"`. This requires the banner code to resolve
`RoutingAlgorithmRegistration` correctly — which it will, since `configureRequestPipeline`
registers it.

**Also:** `configureServices` is a backwards-compat alias for `configureRequestPipeline`
(STATE.md pending-todos: "Remove configureServices backwards-compat alias after ModelsTests
migration"). Phase 17 should NOT break this alias — `configureServices` simply calls
`configureRequestPipeline`, which now includes the mode-branched registration. The alias
requires no changes.

### Pitfall 6: RoutingConfig Unchanged — No HardRulesConfig in Phase 17

**Critical finding:** The v2.0 ARCHITECTURE.md and STACK.md mention adding `HardRules:
HardRulesConfig` to `RoutingConfig`. However, Phase 17 locks the keyword list as hardcoded
(HR-02). Therefore `RoutingConfig` does NOT need a new field in Phase 17 — the keywords
are in HardRules.fs as a module-level constant, not passed through config.

`tryHardRule config req` as shown in ARCHITECTURE.md's pseudo-code would imply
`RoutingConfig.HardRules` — but HR-02 says NOT config-driven. The correct implementation:
`HardRules.applyHardRules req` (no config parameter). `routeRequest`'s signature is
unchanged: `RoutingConfig -> RoutingAlgorithm -> RouterRequest -> Result<RoutingDecision, RouterError>`.

This avoids a breaking change to `RoutingConfig` (which would require updating every
`defaultRoutingConfig` construction in tests) and keeps HardRules truly encapsulated.

---

## Code Examples

Verified patterns from codebase reads:

### HardRules.fs — Complete Implementation
```fsharp
// Source: to be created at src/SmartRouter.Core/HardRules.fs
module SmartRouter.Core.HardRules

open SmartRouter.Core.Domain

/// Default keyword list (HR-02: hardcoded, not configurable).
/// Case-insensitive String.Contains match across all message content.
let private keywords =
    [ "LLVM"; "MLIR"; "compiler"; "segfault"; "optimization"; "concurrency" ]

/// Stage 0 pre-routing: returns Some RoutingDecision if any keyword matches
/// all message content; None if no match (cascade continues to Stage 1).
/// Pure function — no IO, no DI, no mutable state.
let applyHardRules (req: RouterRequest) : RoutingDecision option =
    let combined =
        req.Messages
        |> List.map (fun m -> m.Content)
        |> System.String.concat " "
    let matched =
        keywords
        |> List.exists (fun kw ->
            combined.Contains(kw, System.StringComparison.OrdinalIgnoreCase))
    if matched then
        Some { Target       = Qwen122B
               Priority     = High
               Reason       = HardRule
               IsFallback   = false
               ModelVersion = "" }
    else
        None
```

### formatReason 7th Arm (DecisionLogger.fs)
```fsharp
// Current (6 arms, line 30-37):
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias
    | ExplicitTask taskType       -> sprintf "explicit_task:%A" taskType
    | Default                     -> "default"
    | ML                          -> "ml"
    | FallbackTo35B               -> "fallback_to_35b"
    | FallbackTo122B              -> "fallback_to_122b"   // NEW Phase 14

// After Phase 17 (7 arms — HardRule appended):
    | HardRule                    -> "hard_rule"          // NEW Phase 17
```

### RoutingAlgorithmRegistration Mode Branch (CompositionRoot.fs)
```fsharp
// Pattern: read mode string with defensive default; validate; branch registration
let routingMode =
    let raw = config.["Routing:Mode"]
    if isNull raw || String.IsNullOrWhiteSpace(raw) then "selfrouting"
    else raw.Trim().ToLowerInvariant()

// Fail-fast validation (mirrors buildRoutingConfig pattern at line 100-111)
match routingMode with
| "selfrouting" | "ml" -> ()
| other ->
    raise (InvalidOperationException(
        sprintf "appsettings.json Routing.Mode value '%s' is not recognized; valid values are 'selfrouting' or 'ml'" other))

// Inside RoutingAlgorithmRegistration factory (replaces the existing unconditional ML path):
services.AddSingleton<RoutingAlgorithmRegistration>(
    Func<IServiceProvider, RoutingAlgorithmRegistration>(fun sp ->
        match routingMode with
        | "ml" ->
            // Existing ML closure — unchanged
            { Algorithm    = SmartRouter.Core.ML.makeApplyML ...
              Name         = "ml"
              ModelVersion = baselineVersion }
        | _ ->  // "selfrouting" (default)
            // Phase 17 stub — replaced by real makeSelfRoutingAlgorithm in Phase 19
            { Algorithm    = fun _cfg _req ->
                               { Target       = Qwen35B
                                 Priority     = Low
                                 Reason       = Default
                                 IsFallback   = false
                                 ModelVersion = "selfrouting-v1" }
              Name         = "selfrouting"
              ModelVersion = "selfrouting-v1" }))
|> ignore
```

### HardRulesTests.fs Pattern
```fsharp
// Source: tests/SmartRouter.Tests/HardRulesTests.fs (new file, Phase 17)
module SmartRouter.Tests.HardRulesTests

open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Core.HardRules

let private mkReq content =
    { Messages      = [ { Role = User; Content = content } ]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      UnknownFields  = Map.empty }

let tests : Test =
    testList "HardRulesTests" [
        testCase "LLVM triggers HardRule → 122B" <| fun () ->
            let result = applyHardRules (mkReq "debug LLVM pass")
            Expect.isSome result "should match"
            Expect.equal result.Value.Target Qwen122B "target = 122B"
            Expect.equal result.Value.Reason HardRule "reason = HardRule"
            Expect.equal result.Value.Priority High "priority = High"

        testCase "compiler triggers (case-insensitive)" <| fun () ->
            let result = applyHardRules (mkReq "Compiler error in my code")
            Expect.isSome result "case-insensitive match"

        testCase "no match passes through" <| fun () ->
            let result = applyHardRules (mkReq "what is 2+2")
            Expect.isNone result "no keyword → None"

        // Note: HR-06 cascade ordering tests (override + task bypass) belong in
        // Routing.fs integration tests since they test routeRequest, not applyHardRules alone.
    ]
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Phase 12 deleted `Routing.Algorithm` config key + `--routing-algorithm` CLI flag | Phase 17 re-introduces `Routing.Mode` config key for selfrouting-vs-ML | v2.0 pivot 2026-05-11 | Different concept: Phase 12 removed the heuristic-vs-ML split entirely; Phase 17 adds a selfrouting-vs-ML dormancy gate |
| ML routing unconditional in `configureRequestPipeline` (v1.x) | ML DI unchanged; only `RoutingAlgorithmRegistration.Algorithm` branches on mode | Phase 17 | Cleaner than splitting configureRequestPipeline — all ML infrastructure stays registered |
| `RoutingReason` 6-case DU (FallbackTo122B added in Phase 14) | 7-case DU with HardRule | Phase 17 | Compiler enforces all match sites; one new arm in `formatReason` |

**Deprecated/outdated:**
- `configureServices` alias: still present as backwards-compat; Phase 17 does not touch it
  but it should be noted in the plan as carry-over tech debt (ModelsTests migration pending)

---

## Open Questions

1. **HR-03 vs HR-06 cascade ordering conflict**
   - What we know: HR-03 = Stage 0 (Hard Rules before everything); HR-06 = override + task
     bypass Hard Rules; STATE.md decision 5 = Stage 0 Hard Rules first; ROADMAP.md Phase 17
     plan says "explicit override + explicit task cascade ordering verified by tests"
   - What's unclear: Do the Phase 17 unit tests verify that Hard Rules DOES fire before model
     override (i.e., HR-03 is tested directly), or that model override BYPASSES Hard Rules
     (which would contradict HR-03)?
   - Recommendation: Resolve in 17-01 plan by explicitly stating "Hard Rules fires before
     model override per HR-03; HR-06 tests the positive case (keyword match → 122B) not the
     override case; the override-vs-HardRule behavior is documented in the plan as 'Hard Rules
     wins' per STATE.md"

2. **RoutingOptions.Mode field vs config.["Routing:Mode"] direct read**
   - What we know: Both work; Phase 16 Judge uses direct `config.["Routing:Judge:Enabled"]`;
     RoutingOptions is bound separately via IOptions
   - What's unclear: If Mode is added to RoutingOptions, tests that override config via
     `AddInMemoryCollection` would need `KeyValuePair("Routing:Mode", "selfrouting")` — this
     is already needed anyway for test fixture completeness
   - Recommendation: Use `config.["Routing:Mode"]` direct read in `RoutingAlgorithmRegistration`
     factory (same pattern as Phase 16); do NOT add `Mode` to `RoutingOptions` record to avoid
     a breaking change to RoutingOptions construction sites in tests

3. **Startup banner update for "selfrouting" algorithm**
   - What we know: Banner at Program.fs line 304 prints `regn.Name` (will be "selfrouting"
     or "ml")
   - What's unclear: Should the banner also print `Routing.Mode` explicitly? The existing
     banner already shows `routing.algorithm = {regn.Name}` — after Phase 17, this will show
     "selfrouting" or "ml" which IS the mode
   - Recommendation: No banner change needed; `regn.Name` carries the mode implicitly

4. **HardRulesTests.fs placement in rootTests**
   - What we know: `RouterTests.fs` has the explicit rootTests list (PITFALL-26); every new
     test module must be added to both the fsproj Compile list AND rootTests
   - What's unclear: Ordering preference — HardRulesTests should compile before RouterTests.fs
     (which holds rootTests) per the fsproj convention
   - Recommendation: Add `HardRulesTests.fs` to fsproj after `JudgeIntegrationTests.fs` and
     add it to `rootTests` in RouterTests.fs

---

## Sources

### Primary (HIGH confidence)
- `src/SmartRouter.Core/Domain.fs` — RoutingReason DU (6 cases); RouterRequest structure; RoutingDecision shape — read directly
- `src/SmartRouter.Core/Routing.fs` — 3-stage routeRequest pipeline; tryModelOverride; tryTaskTable — read directly
- `src/SmartRouter.Core/SmartRouter.Core.fsproj` — compile order (Domain → MLPorts → CanaryPorts → RetrainingPorts → ML → Routing → Ports) — read directly
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — formatReason 6-arm match; sole exhaustive RoutingReason match site — read directly
- `src/SmartRouter.Cli/CompositionRoot.fs` — configureRequestPipeline full structure; Phase 16 Judge.Enabled pattern; RoutingOptions record; Mode read pattern; ML wiring unconditional comment; RoutingAlgorithmRegistration factory — read directly
- `src/SmartRouter.Cli/appsettings.json` — Routing section structure; no Mode key yet — read directly
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — adapter compile order — read directly
- `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — test compile order; no HardRulesTests yet — read directly
- `tests/SmartRouter.Tests/RouterTests.fs` — rootTests explicit list — read directly
- `tests/SmartRouter.Tests/StreamingTests.fs` — fixture pattern; configureWithoutMl call; test stub algorithm — read directly
- `.planning/STATE.md` — v2.0 locked decisions; cascade order; keyword list — read directly
- `.planning/ROADMAP.md` — Phase 17 goals, success criteria, plan descriptions — read directly
- `.planning/REQUIREMENTS.md` — HR-01..06, MODE-01..04 full text — read directly
- `.planning/research/ARCHITECTURE.md` — v2.0 architectural patterns; HardRules pattern; cascade flow — read directly
- `.planning/research/PITFALLS.md` — cascade ordering bug pitfall #8; ML dormant drift pitfall #11 — read directly
- `.planning/research/STACK.md` — Hard Rules implementation pattern; ML dormancy pattern; no new NuGet — read directly

### Secondary (MEDIUM confidence)
- Grep of all *.fs files for `match.*reason`, `RoutingReason`, `FallbackTo35B`, `FallbackTo122B` — confirmed DecisionLogger.fs is the ONLY exhaustive match site
- Grep of test files for 6 Hard Rules keywords — confirmed no test prompt content conflicts

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all BCL; zero new NuGet; patterns directly observed in codebase
- Architecture: HIGH — derived from live source reads; fsproj order confirmed
- Pitfalls: HIGH — HR-03/HR-06 conflict is a real spec ambiguity documented here; others
  grounded in codebase patterns (CLIMutable mutable, FS0025 enforcement)

**Research date:** 2026-05-11
**Valid until:** 2026-06-11 (stable codebase; 30-day validity)
