---
phase: 04-ml-algorithm-seam
plan: 03
type: execute
wave: 3
depends_on: ["04-01", "04-02"]
files_modified:
  - tests/SmartRouter.Tests/MLRoutingTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
autonomous: true

must_haves:
  truths:
    - "MLRoutingTests.fs exists as a new test file (NOT appended to RoutingTests.fs) with tests covering: ML.applyML always returns Qwen35B/Low/ML; routeRequest dispatches the algorithm parameter (heuristic vs ML); config Routing:Algorithm=ml registers ML.applyML in DI; CLI --routing-algorithm=ml overrides config-set heuristic"
    - "All tests in MLRoutingTests.tests are wrapped in testSequenced (CONTEXT.md locked decision)"
    - "appsettings.json is copied to the test bin output directory via SmartRouter.Tests.fsproj `<None Include='appsettings.json'><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>` so Tests 4 and 5 can call AddJsonFile('appsettings.json') and satisfy validateConfig"
    - "MLRoutingTests is wired into SmartRouter.Tests.fsproj <Compile> AND into RouterTests.fs `rootTests` list (mirroring the explicit rootTests pattern from Phase 1)"
    - "All baseline tests still pass; the new ML tests pass; total = 39 + N (N >= 4 from this plan)"
    - "scripts/check-routing-isolation.sh exits 0 (final verification of ML-04)"
  artifacts:
    - path: "tests/SmartRouter.Tests/MLRoutingTests.fs"
      provides: "Phase 4 dedicated test module — placeholder behavior + dispatch + CLI override"
      contains: "module SmartRouter.Tests.MLRoutingTests"
      min_lines: 60
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "MLRoutingTests.fs in <Compile> list AND appsettings.json copied to test bin via <None Include><CopyToOutputDirectory>"
      contains: "MLRoutingTests.fs"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "rootTests includes SmartRouter.Tests.MLRoutingTests.tests"
      contains: "MLRoutingTests.tests"
  key_links:
    - from: "tests/SmartRouter.Tests/MLRoutingTests.fs"
      to: "src/SmartRouter.Cli/CompositionRoot.fs"
      via: "ServiceCollection + AddInMemoryCollection + configureServices to verify DI dispatch"
      pattern: "configureServices"
    - from: "tests/SmartRouter.Tests/RouterTests.fs"
      to: "MLRoutingTests.tests"
      via: "rootTests list entry"
      pattern: "MLRoutingTests\\.tests"
---

<objective>
Phase 4 Wave 3: Author the dedicated test module that proves the seam works. Cover (a) placeholder behavior of `ML.applyML`, (b) dispatch through `routeRequest` with algorithm parameter, (c) DI dispatch from `Routing:Algorithm = "ml"` config, and (d) CLI override (`--routing-algorithm=ml` beats config-set heuristic via the same AddInMemoryCollection injection that Program.fs uses).

Purpose: Without these tests, ML-01..ML-04 are not verified. Tests live in a NEW file (MLRoutingTests.fs) to keep Phase 4 ownership clear and avoid bloating RoutingTests.fs.

Output:
- tests/SmartRouter.Tests/MLRoutingTests.fs with 4+ test cases covering ML-01..04 mechanics
- tests/SmartRouter.Tests/SmartRouter.Tests.fsproj updated to compile the new file
- tests/SmartRouter.Tests/RouterTests.fs `rootTests` list extended with `SmartRouter.Tests.MLRoutingTests.tests`
- All 39 baseline tests + new ML tests passing
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
@.planning/phases/04-ml-algorithm-seam/04-02-SUMMARY.md
@src/SmartRouter.Core/Domain.fs
@src/SmartRouter.Core/Heuristic.fs
@src/SmartRouter.Core/ML.fs
@src/SmartRouter.Core/Routing.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@tests/SmartRouter.Tests/RoutingTests.fs
@tests/SmartRouter.Tests/RouterTests.fs
@tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
</context>

<tasks>

<task type="auto">
  <name>Task 1: Author tests/SmartRouter.Tests/MLRoutingTests.fs covering ML-01..04 mechanics</name>
  <files>tests/SmartRouter.Tests/MLRoutingTests.fs</files>
  <action>
Create new file `tests/SmartRouter.Tests/MLRoutingTests.fs` with module `SmartRouter.Tests.MLRoutingTests` and an exported `let tests : Test = testList "MLRoutingTests" [ ... ]`. Use Expecto, follow the same `testCase` style as existing RoutingTests.fs.

Required `open` statements (mirror RoutingTests.fs imports plus DI):
```fsharp
module SmartRouter.Tests.MLRoutingTests

open System.Collections.Generic
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SmartRouter.Core.Domain
open SmartRouter.Core.Heuristic
open SmartRouter.Core.ML
open SmartRouter.Core.Routing
open SmartRouter.Cli  // for CompositionRoot
```

Reuse the existing `mkReq` / `defaultConfig` patterns from RoutingTests.fs. If those are `let private`, copy a minimal `mkReq` helper into MLRoutingTests.fs OR change the visibility — pick whichever is the smaller diff. Recommendation: copy the minimal helper (avoids changing test surface area).

**Required test cases (minimum 4):**

**Test 1 — ML-01: Both functions conform to RoutingAlgorithm.**
Pure compile-time + value check: assigning both functions to a `RoutingAlgorithm`-typed binding compiles, and both produce a RoutingDecision when called.
```fsharp
testCase "Heuristic.applyHeuristic and ML.applyML both satisfy RoutingAlgorithm" <| fun () ->
    let h : RoutingAlgorithm = Heuristic.applyHeuristic
    let m : RoutingAlgorithm = ML.applyML
    let req = mkReq None None "hello" 1
    let dh = h defaultConfig req
    let dm = m defaultConfig req
    Expect.isTrue (dh.Target = Qwen35B || dh.Target = Qwen122B)
                  "heuristic returns a valid model target"
    Expect.equal dm.Target Qwen35B "ML placeholder always returns Qwen35B"
```

**Test 2 — ML.applyML placeholder behavior.**
For varied inputs, ML.applyML returns `{ Target = Qwen35B; Priority = Low; Reason = ML; IsFallback = false }`.
```fsharp
testCase "ML.applyML always returns Qwen35B/Low/ML/IsFallback=false" <| fun () ->
    let inputs =
        [ mkReq None None "hello" 1
          mkReq None None "very long complex compiler MLIR LLVM dependency graph closure conversion ..." 1
          mkReq None (Some "graph_indexing") "x" 1 ]
    for req in inputs do
        let d = ML.applyML defaultConfig req
        Expect.equal d.Target Qwen35B "Target = Qwen35B for any input"
        Expect.equal d.Priority Low "Priority = Low"
        Expect.equal d.IsFallback false "IsFallback = false"
        match d.Reason with
        | ML -> ()
        | r -> failtestf "expected Reason = ML, got %A" r
```

**Test 3 — routeRequest dispatches algorithm parameter (heuristic vs ML on the SAME request).**
Same plain prompt → routeRequest with Heuristic.applyHeuristic AND with ML.applyML; assert ML path produces Reason=ML, and the Heuristic path produces a Reason that is NOT ML (Heuristic _ for non-trivial scoring or Default if score < threshold and the dispatcher routed via stage 3).
```fsharp
testCase "routeRequest dispatches the algorithm parameter" <| fun () ->
    let req = mkReq None None "hello" 1  // no override, no task → stage 3 reached

    // ML path: must report Reason = ML.
    match routeRequest defaultConfig ML.applyML req with
    | Ok d ->
        match d.Reason with
        | ML -> ()
        | r -> failtestf "expected Reason = ML on ML path, got %A" r
    | Error e -> failtestf "expected Ok on ML path, got Error %A" e

    // Heuristic path: must NOT report Reason = ML (proves the parameter actually drives dispatch).
    match routeRequest defaultConfig Heuristic.applyHeuristic req with
    | Ok d ->
        match d.Reason with
        | Heuristic _ | Default -> ()  // either is acceptable for a trivial prompt
        | ML -> failtest "Heuristic path returned Reason = ML — algorithm parameter was not respected"
        | r -> failtestf "expected Heuristic _ or Default on heuristic path, got %A" r
    | Error e -> failtestf "expected Ok on heuristic path, got Error %A" e
```

**Test 4 — ML-02: CompositionRoot dispatches ML.applyML when Routing:Algorithm=ml.**
Build a ServiceCollection + IConfiguration that loads the real `appsettings.json` (so `validateConfig` finds all 7 canonical TaskTable entries, Keywords, etc.) and overrides only `Routing:Algorithm=ml` via `AddInMemoryCollection`. Call `configureServices`; resolve `RoutingAlgorithm`; assert Reason=ML.

**LOCKED CHOICE:** appsettings.json is copied to the test bin directory via `<None Include>` in `SmartRouter.Tests.fsproj` (added in Task 2). The "build the entire Routing section in AddInMemoryCollection" alternative is REJECTED — too verbose and drifts from the real config when it changes.

```fsharp
testCase "CompositionRoot registers ML.applyML when Routing:Algorithm=ml" <| fun () ->
    let services = ServiceCollection()
    let testConfig =
        ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional = false)  // baseline Routing config (heuristic) — copied to bin via fsproj
            .AddInMemoryCollection(dict [ "Routing:Algorithm", "ml" ])  // override to ml
            .Build()
    CompositionRoot.configureServices services testConfig |> ignore
    use sp = services.BuildServiceProvider()
    let algorithm = sp.GetRequiredService<RoutingAlgorithm>()
    let runtimeConfig = sp.GetRequiredService<RoutingConfig>()
    let req = mkReq None None "hello" 1
    let decision = algorithm runtimeConfig req
    match decision.Reason with
    | ML -> ()
    | r -> failtestf "expected Reason = ML from ml-configured DI, got %A" r
```

**Test 5 — ML-03: CLI override beats config (last-wins via AddInMemoryCollection ordering).**
Simulate Program.fs's behavior: AddJsonFile loads appsettings.json (Algorithm=heuristic baseline); AddInMemoryCollection adds Algorithm=ml AFTER. The later layer wins (this is the same mechanism Program.fs uses). Use the same appsettings.json-on-bin pattern as Test 4 to satisfy `validateConfig` for the full Routing section.

```fsharp
testCase "CLI --routing-algorithm=ml overrides config Algorithm=heuristic" <| fun () ->
    let services = ServiceCollection()
    let testConfig =
        ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional = false)  // baseline: Algorithm=heuristic + full Routing section
            .AddInMemoryCollection(dict [ "Routing:Algorithm", "ml" ])  // simulates --routing-algorithm=ml CLI override
            .Build()
    CompositionRoot.configureServices services testConfig |> ignore
    use sp = services.BuildServiceProvider()
    let algorithm = sp.GetRequiredService<RoutingAlgorithm>()
    let runtimeConfig = sp.GetRequiredService<RoutingConfig>()
    let req = mkReq None None "hello" 1
    let decision = algorithm runtimeConfig req
    match decision.Reason with
    | ML -> ()
    | r -> failtestf "expected Reason = ML from CLI-overridden router, got %A" r
```

**Wrap the entire test list in `testSequenced` (per CONTEXT.md locked decision: "All wrapped in `testSequenced` (Console.SetOut + Kestrel port discipline).").** Final shape:
```fsharp
let tests : Test =
    testSequenced <| testList "MLRoutingTests" [
        testCase "..." <| fun () -> ...
        // ... rest of tests
    ]
```
Even though Tests 1–3 are pure and Tests 4–5 only touch a ServiceCollection, the locked decision is uniform: every Phase-4 test list is sequenced. This protects against future tests in this file that DO touch the file system or Kestrel ports without requiring a structural rewrite.

**Optional Test 6 — ML-04: invoke check-routing-isolation.sh from F#.** Skip if grepping is brittle; the script runs in CI and is verified manually. Documented as CI-only verification (RESEARCH.md option D).
  </action>
  <verify>
```bash
# File exists and has the expected module declaration.
grep -n 'module SmartRouter.Tests.MLRoutingTests\|let tests' tests/SmartRouter.Tests/MLRoutingTests.fs
# Expected: matches.

# Test count sanity (>=4 testCase entries).
grep -cn 'testCase' tests/SmartRouter.Tests/MLRoutingTests.fs
# Expected: >= 4.

# CONTEXT.md compliance: testSequenced wraps the test list.
grep -F 'testSequenced' tests/SmartRouter.Tests/MLRoutingTests.fs
# Expected: at least one match.
```

(Full test execution is verified after Task 2 wires the file in.)
  </verify>
  <done>
MLRoutingTests.fs exists with at least 4 test cases covering placeholder behavior, routeRequest dispatch, config dispatch, and CLI override. Module name is `SmartRouter.Tests.MLRoutingTests` with exported `let tests : Test = testList "MLRoutingTests" [ ... ]`.
  </done>
</task>

<task type="auto">
  <name>Task 2: Wire MLRoutingTests.fs into SmartRouter.Tests.fsproj <Compile> and RouterTests.fs rootTests</name>
  <files>
tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**Step A — `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj`:**

Two additions:

1. Add `<Compile Include="MLRoutingTests.fs" />` to the test project's <Compile> ItemGroup. Place it AFTER `RoutingTests.fs` and BEFORE `RouterTests.fs` (which is the entrypoint and imports all test modules). The order should be:
- RoutingTests.fs
- StreamingTests.fs
- QueueTests.fs
- LoadTests.fs
- MLRoutingTests.fs  ← NEW
- RouterTests.fs (entrypoint, last)

(Exact existing order may differ — preserve all existing entries; just insert MLRoutingTests.fs immediately before RouterTests.fs.)

2. Add a `<None Include>` entry to copy `appsettings.json` from the Cli project to the test bin output directory. This unblocks Tests 4 and 5 which call `AddJsonFile("appsettings.json")` to satisfy `validateConfig`'s requirement for the full Routing section (TaskTable + Keywords + ModelAliases). Add a new `<ItemGroup>` (or extend an existing `<None>` group):

```xml
<ItemGroup>
  <None Include="..\..\src\SmartRouter.Cli\appsettings.json" Link="appsettings.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

(Path is relative to the test project; `Link` puts the copied file at the bin root so `AddJsonFile("appsettings.json")` resolves with no path. If a different convention is in use, adapt the relative path but keep the `Link="appsettings.json"` for predictable resolution.)

**Step B — `tests/SmartRouter.Tests/RouterTests.fs`:**

Add `SmartRouter.Tests.MLRoutingTests.tests` to the `rootTests` list. The existing rootTests pattern (TEST-07) is:
```fsharp
let rootTests : Test list =
    [ SmartRouter.Tests.RoutingTests.tests
      SmartRouter.Tests.StreamingTests.tests
      SmartRouter.Tests.QueueTests.tests
      SmartRouter.Tests.LoadTests.tests ]
```
Append the new entry:
```fsharp
let rootTests : Test list =
    [ SmartRouter.Tests.RoutingTests.tests
      SmartRouter.Tests.StreamingTests.tests
      SmartRouter.Tests.QueueTests.tests
      SmartRouter.Tests.LoadTests.tests
      SmartRouter.Tests.MLRoutingTests.tests ]
```

(Match the actual style of the existing rootTests literal in this codebase — the snippet above is illustrative.)
  </action>
  <verify>
```bash
# Confirm .fsproj order: MLRoutingTests.fs is before RouterTests.fs.
grep -nE 'MLRoutingTests\.fs|RouterTests\.fs' tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
# Expected: MLRoutingTests.fs line < RouterTests.fs line.

# Confirm rootTests entry.
grep -n 'MLRoutingTests.tests' tests/SmartRouter.Tests/RouterTests.fs
# Expected: at least one match.

# Confirm appsettings.json is wired to copy to test bin.
grep -nE 'appsettings\.json|CopyToOutputDirectory' tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
# Expected: matches showing the <None Include> + CopyToOutputDirectory entry.

# Build + run all tests.
dotnet build 2>&1 | tail -5
# Expected: Build succeeded.

# Confirm appsettings.json reaches the test bin directory after build.
ls tests/SmartRouter.Tests/bin/Debug/net*/appsettings.json 2>/dev/null && echo "OK: appsettings.json in test bin"
# Expected: file present + OK message.

dotnet test --no-build 2>&1 | tail -10
# Expected: 39 baseline + N new ML tests passing; 2 LoadTests ignored; 0 failed.

# Routing isolation script — final ML-04 verification.
bash scripts/check-routing-isolation.sh
# Expected: "OK: routing modules isolated (...)" + exit 0.
```
  </verify>
  <done>
SmartRouter.Tests.fsproj has MLRoutingTests.fs in <Compile> before RouterTests.fs. RouterTests.fs `rootTests` list includes `SmartRouter.Tests.MLRoutingTests.tests`. `dotnet test` reports baseline 39 + N new ML tests passing (43+ total), 2 ignored, 0 failed. check-routing-isolation.sh exits 0.
  </done>
</task>

</tasks>

<verification>
After both tasks complete (final phase verification):

```bash
# 1. Build clean.
dotnet build 2>&1 | tail -5
# Expected: Build succeeded.

# 2. Tests — baseline + ML tests.
dotnet test --no-build 2>&1 | tail -10
# Expected: Passed: >= 43 (39 baseline + >=4 ML), Failed: 0, Ignored: 2.

# 3. Routing isolation enforced (ML-04).
bash scripts/check-routing-isolation.sh
# Expected: exit 0 with OK message.

# 4. ML test module compiled and registered.
grep -n 'MLRoutingTests' tests/SmartRouter.Tests/SmartRouter.Tests.fsproj tests/SmartRouter.Tests/RouterTests.fs
# Expected: matches in both files.

# 5. Phase 4 success-criteria coverage:
#    - ML-01 satisfied (RoutingAlgorithm alias + both functions conform; Test 1)
#    - ML-02 satisfied (config dispatch — Test 4)
#    - ML-03 satisfied (CLI override — Test 5; mechanism in plan 04-02)
#    - ML-04 satisfied (isolation grep — script + Wave 1 Heuristic.fs/ML.fs no cross-imports)
```
</verification>

<success_criteria>
- MLRoutingTests.fs exists with >= 4 test cases covering ML.applyML placeholder behavior, routeRequest algorithm dispatch, CompositionRoot config dispatch (Routing:Algorithm=ml → ML.applyML), and CLI override (later AddInMemoryCollection layer wins).
- The new file is registered in SmartRouter.Tests.fsproj <Compile> before RouterTests.fs.
- RouterTests.fs `rootTests` list includes `SmartRouter.Tests.MLRoutingTests.tests`.
- `dotnet test` reports >= 43 passing tests (39 baseline + >=4 ML), 0 failed, 2 ignored (LoadTests).
- `scripts/check-routing-isolation.sh` exits 0 (final ML-04 verification).
- Phase 4 success criteria #1–#5 (ROADMAP.md Phase 4) are all satisfied.
</success_criteria>

<output>
After completion, create `.planning/phases/04-ml-algorithm-seam/04-03-SUMMARY.md` describing:
- New test file path + test count
- Final test result (e.g., 43/43 + 2 ignored)
- ML-01..04 each verified with the test that proves it
- Phase 4 declared complete
</output>
