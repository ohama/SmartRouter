---
phase: 12-heuristic-removal
plan: 05
type: execute
wave: 3
depends_on: ["12-02"]
files_modified:
  - tests/SmartRouter.Tests/StreamingTests.fs
  - tests/SmartRouter.Tests/LoggingTests.fs
  - tests/SmartRouter.Tests/HealthFallbackTests.fs
autonomous: true

must_haves:
  truths:
    - "StreamingTests.fs, LoggingTests.fs, HealthFallbackTests.fs no longer reference 'heuristic' (no string literal, no config override, no comment claim)"
    - "Each test fixture calls CompositionRoot.configureWithoutMl (NOT configureRequestPipeline / configureServices) so ML init is skipped without needing model files"
    - "Each test fixture injects a test-stub RoutingAlgorithmRegistration directly via services.AddSingleton AFTER configureWithoutMl returns — last-registration-wins makes the stub active"
    - "Test-stub Algorithm: fun _cfg _req → { Target = Qwen35B; Priority = Low; Reason = ML; IsFallback = false; ModelVersion = \"test-stub\" }; Name = \"ml\"; ModelVersion = \"test-stub\""
    - "LoggingTests assertion line 343 changed from 'heuristic' to 'ml' (routing_algorithm field reflects test-stub)"
    - "All three test files compile and pass; total test count for these three modules unchanged (just rewired)"
  artifacts:
    - path: "tests/SmartRouter.Tests/StreamingTests.fs"
      provides: "Streaming integration tests using configureWithoutMl + test-stub algorithm"
    - path: "tests/SmartRouter.Tests/LoggingTests.fs"
      provides: "Decision-log integration tests with routing_algorithm=ml assertion"
    - path: "tests/SmartRouter.Tests/HealthFallbackTests.fs"
      provides: "Health/fallback tests using configureWithoutMl"
---

<objective>
Migrate three test fixtures (StreamingTests, LoggingTests, HealthFallbackTests) from the now-deleted `Routing:Algorithm = "heuristic"` config-override mechanism to the Q2=B test-only `RoutingAlgorithmRegistration` direct injection. Each fixture calls `CompositionRoot.configureWithoutMl` to skip ML init (no model files needed), then injects a test-stub `RoutingAlgorithmRegistration` whose Algorithm always routes to Qwen35B with Reason=ML.

LoggingTests' decision-log assertion changes from `routing_algorithm = "heuristic"` to `routing_algorithm = "ml"` (the test-stub's Name field).
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
  <name>Task 1: StreamingTests.fs — rewire fixture</name>
  <files>tests/SmartRouter.Tests/StreamingTests.fs</files>
  <action>
**Edit 1.** Remove the `Routing:Algorithm` KeyValuePair from the AddInMemoryCollection block (currently line 130). Also remove `Routing:ComplexityThreshold` and `Routing:Keywords:0` (and any subsequent Keywords:N entries) — those keys no longer exist post-Q3:

```fsharp
// BEFORE
.AddInMemoryCollection(dict [
    KeyValuePair("Upstreams:Model35B",  ...)
    KeyValuePair("Upstreams:Model122B", ...)
    KeyValuePair("Routing:Algorithm",            "heuristic")    // ← REMOVE
    KeyValuePair("Routing:ComplexityThreshold", "3")            // ← REMOVE
    KeyValuePair("Routing:TimeoutSeconds",       "300")
    KeyValuePair("Routing:Keywords:0",           "recursive")  // ← REMOVE
    ...
])

// AFTER
.AddInMemoryCollection(dict [
    KeyValuePair("Upstreams:Model35B",  ...)
    KeyValuePair("Upstreams:Model122B", ...)
    KeyValuePair("Routing:TimeoutSeconds",       "300")
    ...
])
```

**Edit 2.** Replace the call to `CompositionRoot.configureServices` (or whichever variant is being called) with `CompositionRoot.configureWithoutMl`. After that call, add the test-stub registration:

```fsharp
CompositionRoot.configureWithoutMl services configuration |> ignore

// Test-stub: deterministic routing to Qwen35B; no ML model files required.
let testStubAlgorithm : SmartRouter.Core.Domain.RoutingAlgorithm =
    fun _cfg _req ->
        { Target       = SmartRouter.Core.Domain.Qwen35B
          Priority     = SmartRouter.Core.Domain.Low
          Reason       = SmartRouter.Core.Domain.ML
          IsFallback   = false
          ModelVersion = "test-stub" }

let testStubReg : SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration =
    { Algorithm    = testStubAlgorithm
      Name         = "ml"
      ModelVersion = "test-stub" }

services.AddSingleton<SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration>(testStubReg) |> ignore
```

(Wrap in `let` bindings inside the existing fixture function body. The `services.AddSingleton` call must come AFTER `configureWithoutMl` since `configureWithoutMl` does NOT register `RoutingAlgorithmRegistration` — there's no last-wins competition; this is the sole registration.)

**Edit 3.** Tests that depend on Stage-3 routing path (heuristic) — verify that the test-stub's deterministic Qwen35B target is consistent with each test's pre/post assertions. For tests that just stream content and don't care about routing, this is a no-op. If any test asserts a specific Target other than Qwen35B for a Stage-3 fallthrough, that test must be updated to assert Qwen35B (or skipped if it was specifically testing routing decisions — but those would have lived in RoutingTests.fs which is deleted).

**Edit 4.** Comment cleanup. The "// Routing — heuristic avoids ML.zip dependency" comment (or similar) is no longer accurate. Replace with "// Routing — test-stub algorithm avoids ML model file dependency".
  </action>
  <verify>
```bash
grep -c "heuristic\|Heuristic" tests/SmartRouter.Tests/StreamingTests.fs
# expected: 0
grep -c "Routing:Algorithm\|Routing:ComplexityThreshold\|Routing:Keywords" tests/SmartRouter.Tests/StreamingTests.fs
# expected: 0
grep -c "configureWithoutMl\|testStubReg" tests/SmartRouter.Tests/StreamingTests.fs
# expected: >= 2
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~Streaming" --no-restore 2>&1 | tail -3
# expected: passes
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: LoggingTests.fs — rewire fixture + update routing_algorithm assertion</name>
  <files>tests/SmartRouter.Tests/LoggingTests.fs</files>
  <action>
**Edit 1.** Same fixture rewire as StreamingTests Task 1: remove `Routing:Algorithm`/`Routing:ComplexityThreshold`/`Routing:Keywords:N` keys (line 177 + neighbors), call `configureWithoutMl`, register test-stub. Use the SAME `testStubAlgorithm` / `testStubReg` snippet.

**Edit 2.** Update assertion at line 343 (the routing_algorithm field check):

```fsharp
// BEFORE
Expect.equal (root.GetProperty("routing_algorithm").GetString())
              "heuristic"
              "routing_algorithm = heuristic (default config)"

// AFTER
Expect.equal (root.GetProperty("routing_algorithm").GetString())
              "ml"
              "routing_algorithm = ml (test-stub registration)"
```

**Edit 3.** Verify the test that checks `model_version` field. The test-stub's `ModelVersion = "test-stub"` will appear in the JSONL. If a test asserts `model_version = "heuristic-v1"` or similar, update to `"test-stub"`. If no such assertion exists, no change needed.
  </action>
  <verify>
```bash
grep -c "heuristic\|Heuristic" tests/SmartRouter.Tests/LoggingTests.fs
# expected: 0
grep -c "configureWithoutMl\|testStubReg" tests/SmartRouter.Tests/LoggingTests.fs
# expected: >= 2
grep -c "\"ml\".*routing_algorithm\|routing_algorithm.*\"ml\"" tests/SmartRouter.Tests/LoggingTests.fs
# expected: >= 1 (the assertion)
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~Logging" --no-restore 2>&1 | tail -3
# expected: passes
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: HealthFallbackTests.fs — rewire fixture</name>
  <files>tests/SmartRouter.Tests/HealthFallbackTests.fs</files>
  <action>
Same rewire as Task 1 / 2:
- Remove `Routing:Algorithm = "heuristic"` (line 80) + adjacent `Routing:ComplexityThreshold` + `Routing:Keywords:N`.
- Call `configureWithoutMl` instead of the old configureServices.
- Add test-stub `RoutingAlgorithmRegistration` registration after the configureWithoutMl call.

**However, HealthFallbackTests has a special concern:** the FallbackTo35B path requires that `IHealthProbe` and `QueueDispatcher` (with fallback wiring) be registered. `configureWithoutMl` (per 12-02 spec) EXCLUDES `HealthService` registration since health probing only matters for the live request path. **For HealthFallbackTests, this is a problem** — the tests need IHealthProbe + QueueDispatcher fallback active.

Resolution: HealthFallbackTests calls `configureRequestPipeline` (the full version) AFTER injecting fake `IEmbedder` + `IClassifier` registrations BEFORE the configureRequestPipeline call. The fakes prevent ML init from failing (no model files needed because the fakes shadow the real implementations).

**Recommended fixture pattern for HealthFallbackTests:**

```fsharp
// BEFORE configureRequestPipeline: register fake ML components.
let fakeEmbedder = { new IEmbedder with member _.EmbedAsync(_, _) = Task.FromResult(Array.zeroCreate 1024) }
let fakeClassifier = { new IClassifier with member _.PredictAsync(_, _) = Task.FromResult({ Score = 0.0f; PredictedLabel = false }) }
services.AddSingleton<IEmbedder>(fakeEmbedder).AddSingleton<IClassifier>(fakeClassifier) |> ignore

CompositionRoot.configureRequestPipeline services configuration |> ignore

// AFTER: override RoutingAlgorithmRegistration with test-stub (last-wins).
services.AddSingleton<RoutingAlgorithmRegistration>(testStubReg) |> ignore
```

Note: `configureRequestPipeline` will still try to call `ensureEmbeddingFilesPresent` at registration time. To avoid that, EITHER:
- (a) HealthFallbackTests calls a new third entry point `configureRequestPipelineWithFakeMl` (added to CompositionRoot.fs in 12-02; the executor of 12-02 may need to add this if 12-05 surfaces the need),
- (b) HealthFallbackTests calls `configureWithoutMl` AND additionally registers HealthService + QueueDispatcher manually,
- (c) the fake IEmbedder/IClassifier are registered BEFORE configureRequestPipeline so its `services.AddSingleton<IEmbedder>(...)` calls become last-wins overrides (but `ensureEmbeddingFilesPresent` runs unconditionally in 12-02's design so this fails).

**Recommended for the executor: option (b).** `configureWithoutMl` + manual additional registrations for HealthService and QueueDispatcher fallback wiring. Modest duplication, but cleanest separation.

Alternatively: review 12-02 plan and add a `configureForHealthFallbackTest` entry point that's `configureWithoutMl` + Health/Queue/QueueDispatcher. If 12-02 hasn't been executed yet, advise the planner to consider this addition.

If executor finds option (a) cleaner, they may cross-coordinate with 12-02 to add the third entry. Document the choice in the plan SUMMARY.

---

**Cross-phase note (Phase 13 dependency):**

Phase 13-02 (후속 phase, 후속 실행) 가 HealthService ctor 에 새 parameter 를 추가할 예정:

```fsharp
// Phase 13-02 후 (Phase 12 시점에는 아직 없음):
type HealthService(
    httpFactory: IHttpClientFactory,
    opts: IOptions<HealthOptions>,
    logger: ILogger<HealthService>) =      // NEW in Phase 13-02
```

이 plan (12-05) 의 manual HealthService 인스턴스화 사이트는 **Phase 12 시점의 ctor 시그니처 기준** 으로 작성. Phase 13-02 executor 가 후속 실행 시 NullLogger<HealthService>.Instance 인자를 추가하는 책임. Phase 12-05 executor 는 미리 추가하지 말 것 — Phase 12 build 가 깨짐.

QueueDispatcher 도 마찬가지: Phase 13-02 가 ctor 에 ILogger<QueueDispatcher> 추가할 예정. Phase 12-05 의 manual QueueDispatcher 등록은 현 시그니처 기준으로 작성하고, Phase 13-02 가 NullLogger 추가하는 cascading update 를 catch 한다.
  </action>
  <verify>
```bash
grep -c "heuristic\|Heuristic" tests/SmartRouter.Tests/HealthFallbackTests.fs
# expected: 0
grep -c "Routing:Algorithm" tests/SmartRouter.Tests/HealthFallbackTests.fs
# expected: 0
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --filter "FullyQualifiedName~Health" --no-restore 2>&1 | tail -3
# expected: passes (5 HLTH tests + any FallbackTo35B tests)
```
  </verify>
</task>

</tasks>

<verification>
- [x] All three test files: zero heuristic references, zero Routing:Algorithm config keys
- [x] Test-stub RoutingAlgorithmRegistration injected post-configureWithoutMl in each
- [x] LoggingTests: routing_algorithm assertion updated to "ml"
- [x] HealthFallbackTests: HealthService + QueueDispatcher fallback wiring still active (resolution per Task 3 note)
- [x] All three test modules pass
</verification>
