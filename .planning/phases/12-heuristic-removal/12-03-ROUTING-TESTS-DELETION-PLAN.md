---
phase: 12-heuristic-removal
plan: 03
type: execute
wave: 3
depends_on: ["12-02"]
files_modified:
  - tests/SmartRouter.Tests/RoutingTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  - tests/SmartRouter.Tests/RouterTests.fs
autonomous: true

must_haves:
  truths:
    - "tests/SmartRouter.Tests/RoutingTests.fs file no longer exists (git rm)"
    - "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj has no <Compile Include=\"RoutingTests.fs\" /> entry"
    - "RouterTests.fs rootTests list does not contain RoutingTests.tests"
    - "Test count drops by ~22 (the entire RoutingTests testCase set) — verified post-execution"
    - "dotnet build (tests project) succeeds; dotnet test runs without ModuleNotFound or undefined-reference errors related to RoutingTests"
---

<objective>
Delete `tests/SmartRouter.Tests/RoutingTests.fs` entirely (Q5=전체 삭제). Update `SmartRouter.Tests.fsproj` Compile order. Update `RouterTests.fs` `rootTests` list to remove `RoutingTests.tests` entry.

User accepts the loss of stage-1 (model override) and stage-2 (task table) micro-level coverage. Integration tests (StreamingTests, LoggingTests, HealthFallbackTests, MLRoutingTests) provide indirect coverage of these stages through end-to-end request flows.
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
  <name>Task 1: Delete RoutingTests.fs + remove from Tests.fsproj</name>
  <files>
    - tests/SmartRouter.Tests/RoutingTests.fs (DELETED)
    - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
  </files>
  <action>
**Step 1.** Delete the file:

```bash
git rm tests/SmartRouter.Tests/RoutingTests.fs
```

**Step 2.** Edit `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` to remove the `<Compile Include="RoutingTests.fs" />` line (currently the first compile entry, line 7). Remaining compile order:

```xml
<ItemGroup>
    <Compile Include="StreamingTests.fs" />
    <Compile Include="QueueTests.fs" />
    <Compile Include="LoadTests.fs" />
    <Compile Include="MLRoutingTests.fs" />
    <Compile Include="MLEmbeddingTests.fs" />
    <Compile Include="MLClassifierTests.fs" />
    <Compile Include="LoggingTests.fs" />
    <Compile Include="FailureDetectorTests.fs" />
    <Compile Include="TeacherLabelerTests.fs" />
    <Compile Include="HardCaseDatasetTests.fs" />
    <Compile Include="RetrainingTests.fs" />
    <Compile Include="CanaryTests.fs" />
    <Compile Include="HealthFallbackTests.fs" />
    <Compile Include="RouterTests.fs" />
</ItemGroup>
```

(Comments after Compile lines may be retained or trimmed per executor judgement.)
  </action>
  <verify>
```bash
test ! -f tests/SmartRouter.Tests/RoutingTests.fs && echo OK || echo MISSING
grep -c "RoutingTests\.fs" tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
# expected: 0
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: RouterTests.fs — remove RoutingTests.tests from rootTests list</name>
  <files>tests/SmartRouter.Tests/RouterTests.fs</files>
  <action>
Edit `tests/SmartRouter.Tests/RouterTests.fs`. Find the `rootTests` list and remove the entry that references `RoutingTests.tests`. The list typically looks like:

```fsharp
let rootTests =
    testList "smart-router" [
        RoutingTests.tests          // ← REMOVE THIS LINE
        StreamingTests.tests
        QueueTests.tests
        ...
    ]
```

Result:

```fsharp
let rootTests =
    testList "smart-router" [
        StreamingTests.tests
        QueueTests.tests
        ...
    ]
```

If `RouterTests.fs` opens `SmartRouter.Tests.RoutingTests` at the top, that `open` line must also be removed (otherwise FS0039: namespace `RoutingTests` not defined).
  </action>
  <verify>
```bash
grep -c "RoutingTests\.tests\|open SmartRouter\.Tests\.RoutingTests\|open RoutingTests" tests/SmartRouter.Tests/RouterTests.fs
# expected: 0
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Run tests to confirm count drop</name>
  <files>(read-only)</files>
  <action>
Run the test suite (skipping ML-gated tests if model files absent) and observe the new count.

```bash
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | tail -5
```

Expected: pass count drops by ~20-22 (from 86 to roughly 64-66 without embeddings; from 93 to roughly 71-73 with embeddings). Some assertions in 12-04/12-05 may still be broken (e.g. `routing_algorithm = "heuristic"` in LoggingTests) — those are addressed in Wave 3 parallel plans. If tests fail, log the failure list to `12-03-test-output.txt` so 12-04/05 executor can inspect.

If the test run shows failures unrelated to heuristic (e.g. embedding files missing), that's expected — confirm via filename and continue.
  </action>
  <verify>
```bash
grep -c "RoutingTests\." tests/SmartRouter.Tests/*.fs
# expected: 0 (no remaining references in any test file)
```
  </verify>
</task>

</tasks>

<verification>
- [x] RoutingTests.fs deleted from disk + removed from .fsproj + removed from RouterTests.rootTests
- [x] Tests project builds clean
- [x] Test count drops by ~22 (heuristic-internal + stage-pipeline tests gone)
- [x] No undefined-reference errors related to RoutingTests
</verification>
