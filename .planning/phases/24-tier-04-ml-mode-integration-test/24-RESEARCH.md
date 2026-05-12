# Phase 24: TIER-04 ml-mode integration test (gap closure) — Research

**Researched:** 2026-05-12
**Domain:** F# Expecto integration test, DI provider construction, Routing.Mode="ml" configuration
**Confidence:** HIGH — all findings are from direct codebase reads; no external sources needed.

## Summary

Phase 24 adds a single integration test (TC-7) appended to the existing `SessionKeyCascadeTests.fs` testList. The test constructs a DI provider with `Routing.Mode="ml"` and calls `GetRequiredService<ISessionCascadeStats>()`, asserting non-null. This is a pure structural DI-resolution assertion — it does NOT call `resolveSessionCascade` end-to-end (SC-2 optional path is not recommended for this phase; see Scope Decision below).

The critical constraint: `configureRequestPipeline` with `Routing.Mode="ml"` triggers the ML bootstrap block (lines 330–393 of `CompositionRoot.fs`) when the `Routing:ML` config section is present. That block calls `ensureEmbeddingFilesPresent`, which throws if ONNX files are absent. Two strategies exist to avoid this:

1. **Omit `Routing:ML` from config** — the check at line 342 (`if not (obj.ReferenceEquals(mlOpts, null))`) silently skips the ML bootstrap. This is the pattern used by `ModeSwitchTests.fs` (lines 7–9, 30–34) for its `Routing.Mode="ml"` test. The mode switch is still applied (lines 410–416), so the `RoutingAlgorithmRegistration` is registered for `"ml"` mode. Since `ISessionCascadeStats` is registered unconditionally BEFORE the mode switch block (lines 447–456), omitting `Routing:ML` is sufficient.

2. **Skip-guard pattern** — used by `MlDormantTests.fs` when it needs the full ML arm (including `IEmbedder`). Not needed for Phase 24 because we only need `ISessionCascadeStats` resolution, which is mode-independent.

**Primary recommendation:** Append TC-7 to `SessionKeyCascadeTests.fs` testList. Build config by cloning `minimalConfigPairs` and overriding `Routing:Mode` to `"ml"` (DO NOT add `Routing:ML` section). Call `configureRequestPipeline`, build provider, `GetRequiredService<ISessionCascadeStats>()`, assert non-null. No skip-guard needed.

---

## Q1: Current state of SessionKeyCascadeTests.fs

**File:** `tests/SmartRouter.Tests/SessionKeyCascadeTests.fs`

**Structure:**
- Lines 1–16: module declaration + strategy comment
- Lines 17–26: `open` imports — includes `SmartRouter.Cli.Adapters.SessionCascadeStats`
- Lines 28–41: `mkReq` helper
- Lines 43–45: `hermesSystemContent` helper
- Lines 47–128: `minimalConfigPairs` — full config key list with `Routing:Mode = "selfrouting"` (line 57)
- Lines 130–133: `buildConfig()` — builds `IConfiguration` from `minimalConfigPairs`
- Lines 135–143: `buildProvider()` — calls `configureRequestPipeline`, returns `(IDisposable, ISessionStore, RoutingAlgorithmRegistration, RoutingConfig)`
- Lines 147–248: `tests` testList wrapped in `testSequenced`
  - TC-1 (line 151): header wins over sysprompt
  - TC-2 (line 163): sysprompt wins when header empty
  - TC-3 (line 177): content fingerprint fallback
  - TC-4 (line 192): determinism check
  - TC-5 (line 209): sticky escalation through Tier 2 key (uses `buildProvider`)
  - TC-6 (line 239): `SessionCascadeStats` counter increment (direct instantiation)

**testSequenced:** YES — the entire testList is wrapped in `testSequenced` (line 148). TC-7 inherits this.

**rootTests entry:** `RouterTests.fs` line 45: `SmartRouter.Tests.SessionKeyCascadeTests.tests`. The `tests` binding is the `testList "SessionKeyCascadeTests" [...]`. Appending TC-7 inside the existing list does NOT require a new rootTests entry or a new `.fsproj <Compile>` line.

---

## Q2: buildProvider / DI pattern

TC-5 uses `buildProvider` (lines 135–143), which:
1. Creates `ServiceCollection()`
2. Calls `buildConfig()` (reads `minimalConfigPairs` with `Routing:Mode="selfrouting"`)
3. Calls `configureRequestPipeline services config |> ignore`
4. Calls `services.BuildServiceProvider()`
5. Resolves specific services via `GetRequiredService<T>()`
6. Returns `(sp :> IDisposable, store, regn, cfg)`

TC-6 (lines 239–247) bypasses DI entirely — it directly instantiates `SessionCascadeStats()` and casts to `ISessionCascadeStats`. Not the pattern for TC-7 (we need to prove DI resolution, not just object construction).

**Pattern for TC-7** (copy-paste skeleton):

```fsharp
// TC-7 — TIER-04: ISessionCascadeStats resolves in ml-mode DI provider.
// Constructs a provider with Routing.Mode="ml" (no Routing:ML section so
// the ML bootstrap block is skipped — same technique as ModeSwitchTests.fs).
testCase "TC-7: ISessionCascadeStats resolves non-null in Routing.Mode=\"ml\" DI provider" <| fun () ->
    let mlModePairs =
        minimalConfigPairs
        |> List.map (fun kv ->
            if kv.Key = "Routing:Mode"
            then KeyValuePair("Routing:Mode", "ml")
            else kv)
    let config =
        ConfigurationBuilder()
            .AddInMemoryCollection(mlModePairs :> System.Collections.Generic.IEnumerable<KeyValuePair<string, string>>)
            .Build() :> IConfiguration
    let services = ServiceCollection()
    SmartRouter.Cli.CompositionRoot.configureRequestPipeline services config |> ignore
    use sp = services.BuildServiceProvider()
    let stats = sp.GetRequiredService<ISessionCascadeStats>()
    Expect.isNotNull stats "ISessionCascadeStats must resolve non-null in Routing.Mode=ml provider"
```

**Key**: `List.map` override of `Routing:Mode` key in `minimalConfigPairs`. No `Routing:ML` section added — this makes `mlOpts` resolve to `null`, bypassing `ensureEmbeddingFilesPresent` entirely.

---

## Q3: Routing.Mode="ml" configuration requirements

**What configureRequestPipeline requires at minimum:**
- `Upstreams:Model35B` + `Upstreams:Model122B` — needed for HttpClient `BaseAddress` at registration time (NullRef if absent)
- `Routing:TaskTable:*` — 7 canonical tasks required by `buildRoutingConfig` / `validateConfig`
- `Routing:ModelAliases:*` — required by `buildRoutingConfig`
- `Routing:Mode` — `"selfrouting"` or `"ml"` (fail-fast on any other value)
- `Routing:Session:TtlMinutes` / `MaxEntries` — used by `SessionOptions` binding for `SessionStore`
- Various other keys in `minimalConfigPairs` for QualityFallback, Judge, Queue, DecisionLog, Health, TeacherLabeler, HardCaseDataset, Retraining, Canary, feature_management, Logging

**What is NOT required for TC-7:**
- `Routing:ML:*` — deliberately OMITTED so `mlOpts = null` at line 342, skipping the ML bootstrap block. No ONNX files, no skip-guard needed.
- `Routing:SelfRouter:*` — used by the `"selfrouting"` arm only (lines 472+); the `"ml"` arm skips that block entirely.

**`minimalConfigPairs` already has all required keys** (it includes `Routing:Session:TtlMinutes/MaxEntries` at lines 84–85). TC-7 can use it directly with a single override of `Routing:Mode` to `"ml"`.

**Validation behavior:** Line 410–416 in `CompositionRoot.fs` reads `config.["Routing:Mode"]`, normalizes to lowercase, and throws `InvalidOperationException` on unrecognized values. `"ml"` is explicitly accepted at line 411.

---

## Q4: ISessionCascadeStats DI registration — both pipelines

**`configureRequestPipeline` (lines 447–456):**
```fsharp
services.AddSingleton<SessionCascadeStats>() |> ignore
services.AddSingleton<ISessionCascadeStats>(
    Func<IServiceProvider, ISessionCascadeStats>(fun sp ->
        sp.GetRequiredService<SessionCascadeStats>() :> ISessionCascadeStats))
|> ignore
```
Registered **unconditionally** — before the mode-switch block (line 394+). Applies to BOTH `"selfrouting"` and `"ml"`.

**`configureWithoutMl` (lines 1230–1234):**
```fsharp
services.AddSingleton<SessionCascadeStats>() |> ignore
services.AddSingleton<ISessionCascadeStats>(
    Func<IServiceProvider, ISessionCascadeStats>(fun sp ->
        sp.GetRequiredService<SessionCascadeStats>() :> ISessionCascadeStats))
|> ignore
```
Identical code, same pattern.

**`SessionCascadeStats` is defined in:** `src/SmartRouter.Cli/Adapters/SessionCascadeStats.fs`
- Module: `SmartRouter.Cli.Adapters.SessionCascadeStats`
- Type: `type SessionCascadeStats()` — parameterless, no constructor dependencies
- Interface: `ISessionCascadeStats` — four methods: `RecordHeader()`, `RecordSysprompt()`, `RecordContent()`, `GetStats() : struct (int64 * int64 * int64)`

The test file already opens `SmartRouter.Cli.Adapters.SessionCascadeStats` (line 26), so no new open statement is needed.

---

## Q5: resolveSessionCascade accessibility

**Location:** `ChatCompletions.fs` line 201:
```fsharp
let resolveSessionCascade (req: RouterRequest) : string * string =
```
This is a **module-level public binding** (no `private` keyword). The function is already called directly in TC-1 through TC-4 (e.g., line 157: `SmartRouter.Cli.Endpoints.ChatCompletions.resolveSessionCascade req`).

**Accessibility from tests:** FULLY ACCESSIBLE. TC-1..TC-4 already call it directly. SC-2 (cascade fires in ml-mode end-to-end) is feasible if the planner wants it — but see Scope Decision below.

---

## Q6: MlDormantTests.fs pattern — is it relevant?

`MlDormantTests.fs` constructs a provider with `Routing.Mode="ml"` AND includes `Routing:ML` section (lines 78–80). It uses a **skip-guard** (`mlFilesPresent()` check) because the full ML arm needs `IEmbedder` (ONNX files).

Phase 24 does NOT need this pattern. By omitting `Routing:ML` from config, `mlOpts` resolves to `null` at `configureRequestPipeline` line 342, so the ML bootstrap block (including `ensureEmbeddingFilesPresent`) is completely skipped. `ISessionCascadeStats` is registered at lines 447–456, which run BEFORE the ML block. Result: the provider builds cleanly, `ISessionCascadeStats` resolves, no ONNX files required, no skip-guard needed.

---

## Q7: RouterTests.fs wiring

`RouterTests.fs` line 45:
```fsharp
SmartRouter.Tests.SessionKeyCascadeTests.tests  // Phase 22 (Plan 22-03)
```

`SessionKeyCascadeTests.tests` is the `testSequenced <| testList "SessionKeyCascadeTests" [...]` binding. Appending TC-7 **inside** the existing testList automatically includes it. No new rootTests entry, no new `.fsproj <Compile>` item required.

---

## Q8: testSequenced compliance

`SessionKeyCascadeTests.fs` line 148: `testSequenced <| testList "SessionKeyCascadeTests" [`. TC-7 is inside this list and therefore inherits testSequenced. CLAUDE.md PITFALL-27 compliance is satisfied automatically.

---

## Q9: Pitfalls

**Pitfall 1: Routing:ML section triggers ONNX check**
- What: If `Routing:ML` keys are added to the test config, `ensureEmbeddingFilesPresent` runs and throws on any CI/host without ONNX files.
- Mitigation: Omit `Routing:ML` entirely. `mlOpts = null` at line 342, ML block skipped.
- Confidence: HIGH — ModeSwitchTests.fs comment lines 7–9 explicitly documents this technique.

**Pitfall 2: Routing:SelfRouter keys needed?**
- What: The `"selfrouting"` arm reads `Routing:SelfRouter` options. If the mode is `"ml"`, the `if routingMode = "selfrouting"` block (line 472) is skipped entirely. No SelfRouter keys needed.
- `minimalConfigPairs` includes `Routing:SelfRouter:*` (lines 86–89) but they're irrelevant for `"ml"` — harmless to include.
- Confidence: HIGH.

**Pitfall 3: Missing Routing:Session keys**
- What: `configureRequestPipeline` registers `SessionStore` which reads `Routing:Session` options.
- Mitigation: `minimalConfigPairs` already includes `Routing:Session:TtlMinutes="30"` and `Routing:Session:MaxEntries="10000"` (lines 84–85). No gap.
- Confidence: HIGH.

**Pitfall 4: IServiceProvider disposal in test**
- What: The DI provider registers background services (`SessionStore` TTL loop, `DecisionLogWriter`). `sp.Dispose()` is required or the test runner gets orphaned threads.
- Mitigation: Use `use sp = services.BuildServiceProvider()` (F# `use` binding auto-disposes at end of testCase scope). TC-5 uses `try/finally disposable.Dispose()` pattern — either works; `use` is cleaner for TC-7 which has no try/finally.
- Confidence: HIGH.

**Pitfall 5: TD-2 (IEmbedder carry-over)**
- What: Audit TD-2 mentions `IEmbedder` errors in ml-mode. But `IEmbedder` is only registered inside the `if not (obj.ReferenceEquals(mlOpts, null))` block (line 343). With no `Routing:ML` section, `IEmbedder` is never registered — no null-deref or DI error for `ISessionCascadeStats` resolution.
- Confidence: HIGH — TD-2 is irrelevant to Phase 24.

---

## Q10: ISessionCascadeStats API surface used in tests

From `SessionCascadeStats.fs` (lines 20–29):
- `RecordHeader()` / `RecordSysprompt()` / `RecordContent()` — increment counters
- `GetStats() : struct (int64 * int64 * int64)` — returns `(headerCount, syspromptCount, contentCount)`

TC-6 uses:
```fsharp
let stats = SessionCascadeStats() :> ISessionCascadeStats
stats.RecordHeader()
stats.RecordSysprompt()
stats.RecordSysprompt()
let struct (h, s, c) = stats.GetStats()
```

TC-7 only needs `GetRequiredService<ISessionCascadeStats>()` + `Expect.isNotNull stats "..."`. No counter calls needed for SC-1.

---

## Q11: Scope Decision

**Recommendation: TC-7 only (SC-1). Do NOT add the paired counter test (SC-2).**

Rationale:
- SC-1 is the audit's explicit ask: "No test constructs a DI provider with Routing.Mode=ml and asserts ISessionCascadeStats resolves non-null." TC-7 directly closes that gap.
- SC-2 (cascade fires in ml-mode end-to-end) would add a `resolveSessionCascade` call. But TC-1..TC-4 already exhaustively test `resolveSessionCascade` — those tests are mode-independent (the function has no mode branch). Adding a mode-gated wrapper around them provides zero additional coverage.
- SC-2 would also require a second `ISessionCascadeStats` instance (to call `RecordSysprompt()` + `GetStats()`), pulling in more setup for a guarantee already proven by TC-6 + the DI registration code.
- The marginal value of SC-2 is near zero. The marginal cost (additional assertions, reader confusion about why a cascade call is in a DI test) is nonzero.
- Phase description says "Optional but recommended." Given the zero marginal coverage gain, skip it.

**Final scope:** 1 test, 1 assertion, test count 186 → 187.

---

## Recommended Test Structure

```fsharp
// TC-7 — TIER-04: ISessionCascadeStats resolves in ml-mode DI provider.
// Closes the TD-1 gap: replaces structural proof (registration code present
// in configureRequestPipeline lines 447-456) with an executable assertion.
//
// Config: minimalConfigPairs with Routing:Mode overridden to "ml".
// No Routing:ML section → mlOpts = null at CompositionRoot line 342 →
// ML bootstrap block skipped entirely → no ONNX files needed, no skip-guard.
testCase "TC-7: ISessionCascadeStats resolves non-null in Routing.Mode=\"ml\" DI provider" <| fun () ->
    let mlModePairs =
        minimalConfigPairs
        |> List.map (fun kv ->
            if kv.Key = "Routing:Mode"
            then System.Collections.Generic.KeyValuePair("Routing:Mode", "ml")
            else kv)
    let config =
        ConfigurationBuilder()
            .AddInMemoryCollection(mlModePairs :> IEnumerable<KeyValuePair<string, string>>)
            .Build() :> IConfiguration
    let services = ServiceCollection()
    SmartRouter.Cli.CompositionRoot.configureRequestPipeline services config |> ignore
    use sp = services.BuildServiceProvider()
    let stats = sp.GetRequiredService<ISessionCascadeStats>()
    Expect.isNotNull stats
        "ISessionCascadeStats must resolve non-null in Routing.Mode=ml provider (TIER-04)"
```

**File edit:** Append inside the `testList "SessionKeyCascadeTests" [...]` block after TC-6 closing `]` (current last `]` is at line 248, but BEFORE the outer `]` of the testList). Specifically: insert the new testCase after line 247 and before line 248 `]`.

**No other file changes required:**
- `RouterTests.fs` — no change (TC-7 is inside existing `tests` binding)
- `SmartRouter.Tests.fsproj` — no change (no new file)
- `README.md` — no change (pure internal test; no observable behavior changes)

---

## Sources

- `tests/SmartRouter.Tests/SessionKeyCascadeTests.fs` — full read, HIGH confidence
- `tests/SmartRouter.Tests/MlDormantTests.fs` — full read, HIGH confidence
- `tests/SmartRouter.Tests/ModeSwitchTests.fs` lines 1–218 — HIGH confidence
- `tests/SmartRouter.Tests/RouterTests.fs` — full read, HIGH confidence
- `src/SmartRouter.Cli/CompositionRoot.fs` lines 330–456, 1099–1249 — HIGH confidence
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` lines 195–208 — HIGH confidence
- `src/SmartRouter.Cli/Adapters/SessionCascadeStats.fs` — full read, HIGH confidence

## RESEARCH COMPLETE
