---
phase: 12-heuristic-removal
plan: 04
type: execute
wave: 3
depends_on: ["12-02"]
files_modified:
  - tests/SmartRouter.Tests/MLRoutingTests.fs
autonomous: true

must_haves:
  truths:
    - "MLRoutingTests.fs no longer references applyHeuristic anywhere"
    - "MLRoutingTests.fs no longer has the 'Heuristic.applyHeuristic and ML.makeApplyML closure both satisfy RoutingAlgorithm' test"
    - "MLRoutingTests.fs no longer has the heuristic-vs-ML divergence test"
    - "MLRoutingTests.fs no longer has the 'CLI --routing-algorithm=ml overrides config Algorithm=heuristic' test"
    - "Remaining tests that use AddInMemoryCollection do NOT include 'Routing:Algorithm' key (Q3 removed it; setting it would be a no-op but is misleading — must clean up)"
    - "open SmartRouter.Core.Heuristic line is removed from MLRoutingTests.fs"
    - "Test count in MLRoutingTests drops by 3 (the three deleted tests above)"
    - "dotnet test passes for the remaining MLRoutingTests; ML-gated tests still ptest-skip when model files absent"
---

<objective>
Prune `tests/SmartRouter.Tests/MLRoutingTests.fs` to remove all heuristic-related tests and references. Three tests deleted outright (both-satisfy, divergence, --routing-algorithm-override). Remaining tests cleaned of `Routing:Algorithm` overrides (no longer a valid config key). The `open SmartRouter.Core.Heuristic` import removed (module no longer exists).
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/12-heuristic-removal/12-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Remove open Heuristic + delete 3 heuristic-related testCase blocks</name>
  <files>tests/SmartRouter.Tests/MLRoutingTests.fs</files>
  <action>
**Step 1.** Delete the `open SmartRouter.Core.Heuristic` line at the top of the file (currently around line 4-6 in the open block).

**Step 2.** Delete the testCase block on line 43:

```fsharp
testCase "Heuristic.applyHeuristic and ML.makeApplyML closure both satisfy RoutingAlgorithm" <| fun () ->
    let h : RoutingAlgorithm = applyHeuristic
    ...
```

The entire `testCase ... <| fun () -> ... ` body. Delete from `testCase` through the closing of that single test (typically the next `testCase` keyword or the testList closing).

**Step 3.** Delete the testCase block around line 137 — the heuristic-vs-ML divergence demonstration:

```fsharp
testCase "..." <| fun () ->
    ...
    let heuristicResult = routeRequest cfg applyHeuristic req
    ...
```

If the test asserts something specific about ML behavior independent of the comparison, the executor may rewrite to assert ML-only — but the simpler/safer move is to delete the test outright (heuristic-vs-ML comparison was the test's purpose).

**Step 4.** Delete the testCase block around line 173:

```fsharp
mlTestCase "CLI --routing-algorithm=ml overrides config Algorithm=heuristic" <| fun () ->
    ...
    "Routing:Algorithm", "ml"   // simulates --routing-algorithm=ml CLI override
    ...
```

Entire test deleted.

**Step 5.** For the remaining tests that have `Routing:Algorithm = "ml"` in their AddInMemoryCollection (lines 156, 204, 224 — three remaining sites): delete those KeyValuePair entries. The Routing:Algorithm key is no longer a valid config entry; including it does nothing but misleads readers.

```fsharp
// BEFORE
.AddInMemoryCollection(dict [
    "Routing:Algorithm", "ml"
    "Upstreams:Model35B", ...
    ...
])

// AFTER
.AddInMemoryCollection(dict [
    "Upstreams:Model35B", ...
    ...
])
```

**Step 6.** Verify the test that asserts `reg.Name` (line 209). Currently:

```fsharp
Expect.equal reg.Name "ml" "Name = ml"
```

This stays — `RoutingAlgorithmRegistration.Name` is still "ml" (the only valid value post-Q3).
  </action>
  <verify>
```bash
grep -c "applyHeuristic\|open SmartRouter\.Core\.Heuristic" tests/SmartRouter.Tests/MLRoutingTests.fs
# expected: 0
grep -c "Routing:Algorithm" tests/SmartRouter.Tests/MLRoutingTests.fs
# expected: 0
grep -c "routing-algorithm" tests/SmartRouter.Tests/MLRoutingTests.fs
# expected: 0
grep -c "testCase\|mlTestCase\|ptestCase" tests/SmartRouter.Tests/MLRoutingTests.fs
# expected: drops by 3 from previous count
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tail -3
# expected: Build succeeded.
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~MLRouting" --no-restore 2>&1 | tail -3
# expected: passes; reduced count
```
  </verify>
</task>

</tasks>

<verification>
- [x] MLRoutingTests.fs has no applyHeuristic references; no open Heuristic
- [x] 3 heuristic-related tests deleted; remaining tests have no Routing:Algorithm key
- [x] Tests project builds + runs; MLRoutingTests count drops by 3
</verification>
