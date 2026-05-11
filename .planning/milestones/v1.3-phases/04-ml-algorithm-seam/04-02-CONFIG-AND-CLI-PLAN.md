---
phase: 04-ml-algorithm-seam
plan: 02
type: execute
wave: 2
depends_on: ["04-01"]
files_modified:
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  - src/SmartRouter.Cli/Program.fs
  - src/SmartRouter.Cli/appsettings.json
  - tests/SmartRouter.Tests/StreamingTests.fs
autonomous: true

must_haves:
  truths:
    - "appsettings.json has `\"Routing\": { \"Algorithm\": \"heuristic\", ... }` with Algorithm as the first key in the Routing section"
    - "CompositionRoot dispatches `null | \"\" | \"heuristic\" -> Heuristic.applyHeuristic` and `\"ml\" -> ML.applyML`; any other value throws InvalidOperationException at startup"
    - "ChatCompletions endpoint resolves `RoutingAlgorithm` from DI and passes it to `routeRequest`"
    - "Program.fs parses `--routing-algorithm=ml` AND `--routing-algorithm ml` syntax; last-wins via `Array.tryFindIndexBack`; injects via `AddInMemoryCollection` BEFORE configureServices"
    - "Empty value (`--routing-algorithm=`) and invalid value (`--routing-algorithm=foobar`) are rejected at startup with a clear error"
    - "StreamingTests.fs `startTestRouter` adds `\"Routing:Algorithm\", \"heuristic\"` to AddInMemoryCollection (defensive — even if null-default works)"
    - "All 39 baseline tests still pass after wiring (RoutingTests 22 + StreamingTests 8 + QueueTests 9)"
  artifacts:
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "RoutingOptions.Algorithm field + AddSingleton<RoutingAlgorithm> dispatch factory"
      contains: "RoutingAlgorithm"
    - path: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      provides: "Resolve RoutingAlgorithm from RequestServices and pass to routeRequest"
      contains: "GetRequiredService<RoutingAlgorithm>"
    - path: "src/SmartRouter.Cli/Program.fs"
      provides: "--routing-algorithm CLI flag parsing + AddInMemoryCollection override BEFORE configureServices"
      contains: "routing-algorithm"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Routing.Algorithm = heuristic default"
      contains: "Algorithm"
  key_links:
    - from: "src/SmartRouter.Cli/Program.fs"
      to: "builder.Configuration.AddInMemoryCollection"
      via: "CLI flag value injected as Routing:Algorithm before configureServices"
      pattern: "AddInMemoryCollection"
    - from: "src/SmartRouter.Cli/CompositionRoot.fs"
      to: "Heuristic.applyHeuristic | ML.applyML"
      via: "match on RoutingOptions.Algorithm string"
      pattern: "Heuristic\\.applyHeuristic|ML\\.applyML"
    - from: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      to: "routeRequest config algorithm req"
      via: "RoutingAlgorithm resolved from DI per request"
      pattern: "GetRequiredService<RoutingAlgorithm>"
---

<objective>
Phase 4 Wave 2: Wire the seam built in Wave 1 to the running router. Add `Routing.Algorithm` config key, register `RoutingAlgorithm` as a DI singleton with a dispatch factory, resolve it in the ChatCompletions endpoint, and parse `--routing-algorithm` CLI flag in Program.fs (with the AddInMemoryCollection BEFORE configureServices ordering lesson from Phase 2).

Purpose: Without this wave, the seam exists but nothing reads the config or CLI flag. After this wave, the operator can flip algorithms via either appsettings.json or `dotnet run -- --routing-algorithm=ml`. Default behavior stays bit-for-bit identical (heuristic).

Output:
- appsettings.json with `"Algorithm": "heuristic"` first key in Routing section
- CompositionRoot.fs registers RoutingAlgorithm with config-driven dispatch + invalid-value rejection
- ChatCompletions.fs resolves RoutingAlgorithm and passes to routeRequest
- Program.fs CLI parsing covering `=` and space syntax, empty/invalid rejection, last-wins
- StreamingTests.fs defensively adds "Routing:Algorithm", "heuristic" key
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
@.planning/phases/04-ml-algorithm-seam/04-01-SUMMARY.md
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/Program.fs
@src/SmartRouter.Cli/appsettings.json
@tests/SmartRouter.Tests/StreamingTests.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: appsettings.json + CompositionRoot dispatch (RoutingOptions.Algorithm + AddSingleton<RoutingAlgorithm>)</name>
  <files>
src/SmartRouter.Cli/appsettings.json
src/SmartRouter.Cli/CompositionRoot.fs
src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  </files>
  <action>
**Step A — `src/SmartRouter.Cli/appsettings.json`:**

Add `"Algorithm": "heuristic"` as the FIRST key inside the `"Routing"` object. Example:
```json
"Routing": {
  "Algorithm": "heuristic",
  "ComplexityThreshold": 3,
  "TimeoutSeconds": 300,
  "Keywords": [ ... ],
  "TaskTable": { ... },
  "ModelAliases": { ... }
}
```
Do NOT touch any other section. Preserve existing trailing-comma / formatting style.

**Step B — `src/SmartRouter.Cli/CompositionRoot.fs`:**

1. Add `Algorithm: string` to the `RoutingOptions` `[<CLIMutable>]` record. The field is a plain string (no nullable annotation — F# binds missing keys to `null` for CLIMutable string fields). Place it as the FIRST field for parity with the JSON ordering, OR last — both work; pick whichever creates the smallest diff against the current record literal.

2. After the existing `services.AddSingleton<RoutingConfig>(...)` registration, add a new `RoutingAlgorithm` singleton factory. Use **qualified names** at the dispatch site (`SmartRouter.Core.Heuristic.applyHeuristic` and `SmartRouter.Core.ML.applyML`, abbreviated to `Heuristic.applyHeuristic` / `ML.applyML` since `SmartRouter.Core.Domain` is already opened in this file). Do NOT add `open SmartRouter.Core.Heuristic` or `open SmartRouter.Core.ML` — qualifying at the call site is clearer because Heuristic and ML are short module names and the dispatch is the one place they are referenced. (Plan 04-01 Task 3 also adopts qualified-name style by using `Heuristic.applyHeuristic` directly via `open SmartRouter.Core.Heuristic` — this plan avoids the extra `open` because the call site is local.)

   ```fsharp
   services.AddSingleton<RoutingAlgorithm>(fun sp ->
       let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
       match opts.Algorithm with
       | null | "" | "heuristic" -> Heuristic.applyHeuristic
       | "ml"                    -> ML.applyML
       | other ->
           let msg =
               sprintf
                   "appsettings.json Routing.Algorithm = \"%s\" is invalid; valid values: \"heuristic\", \"ml\""
                   other
           raise (System.InvalidOperationException(msg)))
   |> ignore
   ```

3. CRITICAL: handle ALL three default-equivalent cases — `null | "" | "heuristic"` — because:
   - `null` happens when `Routing.Algorithm` is absent from configuration providers.
   - `""` happens if the operator sets the env var to empty string.
   - `"heuristic"` is the explicit default.

**Step C — `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`:**

1. Add `open SmartRouter.Core.Domain` if not already present (need `RoutingAlgorithm`).
2. In `mapEndpoints`, resolve `RoutingAlgorithm` from `ctx.RequestServices`:
   ```fsharp
   let algorithm = ctx.RequestServices.GetRequiredService<RoutingAlgorithm>()
   ```
3. Update the `handler` signature to take `algorithm: RoutingAlgorithm` as a parameter (positionally placed between `routingConfig` and `upstream` for readability — matches RESEARCH.md F9).
4. Update the `routeRequest routingConfig req` callsite inside handler to `routeRequest routingConfig algorithm req`.

Pitfall reminder: per-request resolution is fine because `RoutingAlgorithm` is a singleton — `GetRequiredService<RoutingAlgorithm>` returns the same function reference every call. No allocation cost.
  </action>
  <verify>
```bash
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -10
# Expected: Build succeeded.

# Confirm dispatch factory registers all three default-equivalent cases.
grep -nE 'null\s*\|\s*""\s*\|\s*"heuristic"' src/SmartRouter.Cli/CompositionRoot.fs
# Expected: one match.

# Confirm ChatCompletions resolves RoutingAlgorithm.
grep -n 'GetRequiredService<RoutingAlgorithm>' src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# Expected: at least one match.

# Smoke test the heuristic-default path.
dotnet test --no-build 2>&1 | tail -5
# Expected: 39 passing (or build dependency issue — see Task 3 for StreamingTests fix).
```
  </verify>
  <done>
appsettings.json has `Algorithm: "heuristic"` in Routing. CompositionRoot.fs has RoutingOptions.Algorithm field and AddSingleton<RoutingAlgorithm> factory dispatching null/empty/heuristic to Heuristic.applyHeuristic, "ml" to ML.applyML, others to InvalidOperationException. ChatCompletions.fs resolves RoutingAlgorithm and passes to routeRequest. Build succeeds.
  </done>
</task>

<task type="auto">
  <name>Task 2: Program.fs CLI parsing for --routing-algorithm (=, space, last-wins, invalid rejection) + StreamingTests defensive Algorithm key</name>
  <files>
src/SmartRouter.Cli/Program.fs
tests/SmartRouter.Tests/StreamingTests.fs
  </files>
  <action>
**Step A — `src/SmartRouter.Cli/Program.fs`:**

The override MUST happen AFTER `WebApplication.CreateBuilder(args)` and BEFORE `CompositionRoot.configureServices` (same ordering lesson Phase 2's startTestRouter learned). If the order is wrong, IOptions<RoutingOptions> binds before the override is visible.

Find the existing `let builder = WebApplication.CreateBuilder(args)` line. AFTER it (and after `builder.Host.UseSerilog()` if present) but BEFORE `CompositionRoot.configureServices`, insert:

```fsharp
// Parse --routing-algorithm CLI flag (last-wins). Supports:
//   --routing-algorithm=ml        (equals form)
//   --routing-algorithm ml        (space form)
// Reject empty/invalid values at startup before any service registration.
let routingAlgorithmOverride : string option =
    args
    |> Array.tryFindIndexBack (fun a ->
        a = "--routing-algorithm" || a.StartsWith("--routing-algorithm="))
    |> Option.map (fun idx ->
        let arg = args.[idx]
        if arg.StartsWith("--routing-algorithm=") then
            let v = arg.["--routing-algorithm=".Length..]
            if v = "" then
                failwith "--routing-algorithm= requires a value (heuristic or ml)"
            v
        elif idx + 1 < args.Length then
            let v = args.[idx + 1]
            if v.StartsWith("--") then
                failwith "--routing-algorithm requires a value (heuristic or ml)"
            v
        else
            failwith "--routing-algorithm requires a value (heuristic or ml)")

// Validate the value and inject into configuration BEFORE configureServices.
match routingAlgorithmOverride with
| None -> ()
| Some v ->
    match v with
    | "heuristic" | "ml" -> ()
    | other ->
        failwithf
            "--routing-algorithm=%s is invalid; valid values: heuristic, ml"
            other
    (builder.Configuration :> Microsoft.Extensions.Configuration.IConfigurationBuilder)
        .AddInMemoryCollection(
            dict [ "Routing:Algorithm", v ])
    |> ignore
```

CRITICAL DETAILS:
- `Array.tryFindIndexBack` (NOT `tryFindIndex`) — last occurrence wins, the natural CLI semantic.
- The IConfigurationBuilder cast is required (extension method on the interface, not the concrete type) — same lesson from Phase 2 where StreamingTests added it.
- The override must happen before `CompositionRoot.configureServices builder.Services builder.Configuration`.

**Step B — `tests/SmartRouter.Tests/StreamingTests.fs`:**

In `startTestRouter`'s `AddInMemoryCollection` payload, add the entry:
```fsharp
"Routing:Algorithm", "heuristic"
```
Place it next to other Routing keys. This is DEFENSIVE — the null-default in CompositionRoot already handles a missing key — but explicit is better than implicit and protects against future test-config drift.
  </action>
  <verify>
```bash
dotnet build 2>&1 | tail -5
# Expected: Build succeeded.

# Sanity: CLI parsing structure exists.
grep -n 'tryFindIndexBack\|--routing-algorithm' src/SmartRouter.Cli/Program.fs
# Expected: matches showing the parse + validate block.

# Sanity: order — the parse block is BEFORE configureServices (scripted, no eyeballing).
OVERRIDE_LINE=$(grep -n 'routingAlgorithmOverride\|AddInMemoryCollection.*Routing:Algorithm' src/SmartRouter.Cli/Program.fs | head -1 | cut -d: -f1)
CONFIG_LINE=$(grep -n 'configureServices' src/SmartRouter.Cli/Program.fs | head -1 | cut -d: -f1)
if [ -z "$OVERRIDE_LINE" ] || [ -z "$CONFIG_LINE" ]; then
    echo "FAIL: missing required pattern (override=$OVERRIDE_LINE, configureServices=$CONFIG_LINE)"
    exit 1
elif [ "$OVERRIDE_LINE" -lt "$CONFIG_LINE" ]; then
    echo "OK: AddInMemoryCollection (line $OVERRIDE_LINE) precedes configureServices (line $CONFIG_LINE)"
else
    echo "FAIL: ordering wrong (override at $OVERRIDE_LINE, configureServices at $CONFIG_LINE)"
    exit 1
fi
# Expected: "OK: ..." line; the script exits non-zero if order is wrong.

# Sanity: StreamingTests carries the explicit key.
grep -n 'Routing:Algorithm' tests/SmartRouter.Tests/StreamingTests.fs
# Expected: one match with "heuristic".

# Full test run.
dotnet test --no-build 2>&1 | tail -10
# Expected: 39 passing, 2 ignored (LoadTests pending).
```

Manual smoke (optional — not strictly required for the plan):
```bash
# Heuristic default — should bind to Heuristic.applyHeuristic.
dotnet run --project src/SmartRouter.Cli -- --routing-algorithm=heuristic 2>&1 &
PID=$!; sleep 2; kill $PID 2>/dev/null

# ML override — should bind to ML.applyML; full integration test exists in plan 04-03.
dotnet run --project src/SmartRouter.Cli -- --routing-algorithm=ml 2>&1 &
PID=$!; sleep 2; kill $PID 2>/dev/null

# Invalid — should fail fast.
dotnet run --project src/SmartRouter.Cli -- --routing-algorithm=foobar 2>&1 | head -5
# Expected: error message mentioning "foobar" and the valid values.
```
  </verify>
  <done>
Program.fs parses both `--routing-algorithm=...` and `--routing-algorithm ...` syntaxes; uses `Array.tryFindIndexBack` for last-wins; rejects empty and invalid values; injects via `AddInMemoryCollection` BEFORE `configureServices`. StreamingTests.fs `startTestRouter` includes `"Routing:Algorithm", "heuristic"` in its config. Build succeeds; 39 baseline tests still pass.
  </done>
</task>

</tasks>

<verification>
After both tasks complete:

```bash
# 1. Build clean.
dotnet build 2>&1 | tail -5
# Expected: Build succeeded.

# 2. All baseline tests still pass (the seam is now fully wired through Cli + DI).
dotnet test --no-build 2>&1 | tail -10
# Expected: Passed: 39, Failed: 0, Ignored: 2.

# 3. Routing isolation script still passes.
bash scripts/check-routing-isolation.sh
# Expected: "OK: routing modules isolated (...)" + exit 0.

# 4. Algorithm key present in appsettings.
python3 -c 'import json; print(json.load(open("src/SmartRouter.Cli/appsettings.json"))["Routing"]["Algorithm"])'
# Expected: heuristic.

# 5. Dispatch factory in CompositionRoot covers null, "", "heuristic", "ml", and invalid.
grep -nE 'null|"" *\||"heuristic"|"ml"|InvalidOperationException' src/SmartRouter.Cli/CompositionRoot.fs
# Expected: matches showing all branches.

# 6. CLI override in Program.fs uses tryFindIndexBack and AddInMemoryCollection.
grep -nE 'tryFindIndexBack|AddInMemoryCollection' src/SmartRouter.Cli/Program.fs
# Expected: matches.
```
</verification>

<success_criteria>
- appsettings.json `Routing.Algorithm` = `"heuristic"`.
- RoutingOptions has `Algorithm: string` field; CompositionRoot dispatches `null | "" | "heuristic" -> Heuristic.applyHeuristic`, `"ml" -> ML.applyML`, other -> InvalidOperationException.
- ChatCompletions endpoint resolves `RoutingAlgorithm` from DI and passes it to `routeRequest`.
- Program.fs supports both `--routing-algorithm=ml` and `--routing-algorithm ml`, rejects empty/invalid, last-wins, AddInMemoryCollection injection happens BEFORE configureServices.
- StreamingTests.fs `startTestRouter` includes `"Routing:Algorithm", "heuristic"` in AddInMemoryCollection.
- All 39 baseline tests pass; build succeeds; check-routing-isolation.sh exits 0.
- ML-02 satisfied (config dispatch). ML-03 implementation satisfied (CLI override mechanism — verification test lives in plan 04-03).
</success_criteria>

<output>
After completion, create `.planning/phases/04-ml-algorithm-seam/04-02-SUMMARY.md` describing:
- Files modified
- Default behavior preserved (heuristic at all touchpoints)
- ML algorithm reachable via config OR CLI flag
- 39/39 baseline tests still passing
- Forward-link reminder: 04-03 (Phase 4 tests) is unblocked.
</output>
