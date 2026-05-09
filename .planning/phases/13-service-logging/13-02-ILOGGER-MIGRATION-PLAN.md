---
phase: 13-service-logging
plan: 02
type: execute
wave: 2
depends_on: ["13-01"]
files_modified:
  - src/SmartRouter.Cli/Adapters/HealthService.fs
  - src/SmartRouter.Cli/Adapters/CanaryService.fs
  - src/SmartRouter.Cli/Adapters/RetrainingService.fs
  - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
  - src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
  - src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
  - src/SmartRouter.Cli/Adapters/CanaryWatchdog.fs
  - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
  - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
  - src/SmartRouter.Cli/Adapters/Validator.fs
  - src/SmartRouter.Cli/Adapters/DatasetMerger.fs
  - src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs
  - src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
  - src/SmartRouter.Cli/Adapters/Retrainer.fs
  - src/SmartRouter.Cli/Adapters/FailureDetector.fs
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/Program.fs
  - tests/SmartRouter.Tests/LoadTests.fs
  - tests/SmartRouter.Tests/QueueTests.fs
  - tests/SmartRouter.Tests/HealthFallbackTests.fs
  - tests/SmartRouter.Tests/LoggingTests.fs
  - tests/SmartRouter.Tests/HardCaseDatasetTests.fs
  - tests/SmartRouter.Tests/RetrainingTests.fs
  - tests/SmartRouter.Tests/MLClassifierTests.fs
autonomous: true

must_haves:
  truths:
    - "All 11 type-based adapter files (HealthService, CanaryService, RetrainingService, TeacherLabeler, HardCaseDatasetWriter, DecisionLogWriter, CanaryWatchdog, QueueDispatcher, QwenUpstreamClient, BgeM3Embedder, FailureDetector) have a constructor parameter `(logger: ILogger<TypeName>)` and use `logger.LogX(...)` instead of `Serilog.Log.X(...)`"
    - "All 4 module-based emission files (Validator, DatasetMerger, ModelBootstrapper, Retrainer) have functions accepting an `(logger: ILogger)` parameter (non-generic; SourceContext set explicitly via Log.ForContext if needed); all callers in src/ AND tests/ pass the appropriate logger or NullLogger.Instance"
    - "ChatCompletions endpoint resolves logger via `ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(\"ChatCompletions\")` — short SourceContext for grep-friendliness"
    - "CompositionRoot.fs and Program.fs continue to use static Serilog.Log.X for the few startup-time emissions where ILogger<T> isn't available — these are explicitly documented as exempt"
    - "DI auto-resolves ILogger<T> via the host's AddLogging() (already implicit when WebApplication is built); no explicit registration needed"
    - "open Serilog statements are reduced to the few files that legitimately need static Log access; Microsoft.Extensions.Logging is opened in adapter files using ILogger<T>"
    - "Output template's {SourceContext} now renders fully-qualified type names (e.g., SmartRouter.Cli.Adapters.HealthService) — visible via test or manual smoke"
    - "dotnet build clean with TreatWarningsAsErrors=true; dotnet test green"
    - "Total emission count (~89) preserved; only API call form changed"
---

<objective>
Migrate all `Serilog.Log.X(...)` static calls in 15 type-based adapters + 1 endpoint + 4 module-based adapters to `ILogger<T>` constructor injection (or `ILogger` function parameter for module-based code). `CompositionRoot.fs` and `Program.fs` continue using static `Log.*` for the startup window where DI isn't available yet (~6 emissions).

This is a mechanical migration — same emission semantics, different API. Atomic build-green at plan end. Tasks chunked by file group; per-task atomic commit per the project's commit convention.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/13-service-logging/13-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Migrate top 5 high-emission adapters (HealthService, CanaryService, RetrainingService, TeacherLabeler, HardCaseDatasetWriter)</name>
  <files>
    - src/SmartRouter.Cli/Adapters/HealthService.fs
    - src/SmartRouter.Cli/Adapters/CanaryService.fs
    - src/SmartRouter.Cli/Adapters/RetrainingService.fs
    - src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
    - src/SmartRouter.Cli/Adapters/HardCaseDatasetWriter.fs
  </files>
  <action>
For each of the 5 files, apply the same migration pattern:

**Step A.** Add `open Microsoft.Extensions.Logging` near the top (after existing `open` lines, before module/type body).

**Step B.** If the file has `open Serilog`, REMOVE it (Microsoft's `ILogger` and Serilog's `ILogger` would otherwise conflict).

**Step C.** Find the type declaration. Add `logger: ILogger<{TypeName}>` as the LAST constructor parameter:

```fsharp
// BEFORE (HealthService.fs example)
type HealthService(httpFactory: IHttpClientFactory, opts: IOptions<HealthOptions>) =
    inherit BackgroundService()

// AFTER
type HealthService(
    httpFactory: IHttpClientFactory,
    opts: IOptions<HealthOptions>,
    logger: ILogger<HealthService>) =
    inherit BackgroundService()
```

The DI container auto-resolves `ILogger<T>` because Microsoft.Extensions.Logging is registered by the host via implicit `AddLogging()` (when WebApplication.CreateBuilder runs).

**Step D.** Replace every `Log.Information(...)`, `Log.Warning(...)`, `Log.Error(...)`, `Log.Debug(...)` call with the equivalent `logger.LogInformation(...)`, etc.

```fsharp
// BEFORE
Log.Information("HealthService starting; polling interval={Interval}s", opts.Value.PollingIntervalSeconds)

// AFTER
logger.LogInformation("HealthService starting; polling interval={Interval}s", opts.Value.PollingIntervalSeconds)
```

Argument forms identical (Serilog and Microsoft.Extensions.Logging both use `MessageTemplate` + arg array).

For exception logging:
```fsharp
// BEFORE
Log.Error(ex, "HealthService: probe loop iteration threw; will retry next tick")

// AFTER
logger.LogError(ex, "HealthService: probe loop iteration threw; will retry next tick")
```

(`LogError(ex, msg, args)` overload exists.)

**Step E.** Per file, run `dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj` after editing. Each file independently keeps build green.

**Step F.** Atomic commit per file:
```bash
git add src/SmartRouter.Cli/Adapters/HealthService.fs
git commit -m "refactor(13-02): migrate HealthService to ILogger<T>"
# repeat for CanaryService, RetrainingService, TeacherLabeler, HardCaseDatasetWriter
```

Per-file commit is recommended (5 commits in this task). If the executor prefers a single commit for the task, that's also acceptable per project convention (`refactor(13-02): migrate 5 high-emission adapters to ILogger<T>`).

**Migration tip — no DI registration changes needed:** `services.AddLogging()` is implicit; ILogger<T> auto-resolves. The existing `services.AddSingleton<HealthService>(...)` etc. registrations work unchanged because DI resolves the new constructor parameter automatically.

**Test fixtures may need attention:** if `RetrainingTests.fs` or similar manually constructs `RetrainingService(opts, emb, vp, lock)` (no DI), it now needs to pass `NullLogger<RetrainingService>.Instance` as the new last argument. Check test files; if they construct manually, add the NullLogger argument. Use `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance`.

For example in `RetrainingTests.fs`:
```fsharp
// BEFORE
let service = RetrainingService(opts, fakeEmbedder, vp, lock)

// AFTER
let service = RetrainingService(opts, fakeEmbedder, vp, lock, NullLogger<RetrainingService>.Instance)
```

Add `open Microsoft.Extensions.Logging.Abstractions` to the test file.
  </action>
  <verify>
```bash
for f in HealthService CanaryService RetrainingService TeacherLabeler HardCaseDatasetWriter; do
  echo "=== $f ==="
  grep -c "ILogger<$f>" "src/SmartRouter.Cli/Adapters/$f.fs"
  grep -c "logger\.Log[A-Z]" "src/SmartRouter.Cli/Adapters/$f.fs"
  grep -c "Serilog\.Log\.\|^open Serilog$" "src/SmartRouter.Cli/Adapters/$f.fs"
done
# Per file: ILogger<T> count >= 1; logger.Log count >= 1; Serilog.Log + open Serilog = 0
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# expected: Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Migrate next 5 adapters (DecisionLogWriter, CanaryWatchdog, QueueDispatcher, QwenUpstreamClient, FailureDetector)</name>
  <files>
    - src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs
    - src/SmartRouter.Cli/Adapters/CanaryWatchdog.fs
    - src/SmartRouter.Cli/Adapters/QueueDispatcher.fs
    - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    - src/SmartRouter.Cli/Adapters/FailureDetector.fs
  </files>
  <action>
Same pattern as Task 1. Per-file: add `open Microsoft.Extensions.Logging`, remove `open Serilog`, add `logger: ILogger<TypeName>` ctor parameter, rewrite all `Log.X` to `logger.LogX`.

For `QueueDispatcher`, the type signature is more complex (multiple ctors / multiple parameters). Make sure the new `logger` parameter is the LAST one to minimize positional-argument breakage in callers. If the project's existing convention puts logger first (it doesn't — current 0 ILogger<T> usage), follow whatever convention emerges from Task 1.

Check test fixtures that construct these types directly:
- `LoadTests.fs` constructs `QueueDispatcher(...)` — add NullLogger
- `QueueTests.fs` constructs `QueueDispatcher(...)` — add NullLogger
- `HealthFallbackTests.fs` may construct `QueueDispatcher` — check
- `LoggingTests.fs` may construct `DecisionLogWriter` — check
- `HardCaseDatasetTests.fs` might construct `HardCaseDatasetWriter` — check
- Various fixture-related test files

For each test file that manually constructs, add `NullLogger<T>.Instance` as the new last argument.

Per-file commit:
```bash
git commit -m "refactor(13-02): migrate {File} to ILogger<T>"
```

---

**Cross-phase note (Phase 12 dependency):**

Phase 12-05 (선행 phase, 이미 실행됨 가정) 가 다음 test 파일들의 fixture 를 rewire 했음:
- `tests/SmartRouter.Tests/StreamingTests.fs`
- `tests/SmartRouter.Tests/LoggingTests.fs`
- `tests/SmartRouter.Tests/HealthFallbackTests.fs`

특히 HealthFallbackTests 에는 manual HealthService 인스턴스화 + manual QueueDispatcher 인스턴스화 사이트가 추가되었을 가능성이 높음 (Phase 12-05 Task 3 의 option (b) 권장). 이 plan 의 ILogger 마이그레이션이 그 manual 인스턴스화 사이트들을 만나면 NullLogger 인자 추가가 cascading change 의 일부.

**작업 패턴:**
1. Phase 12-05 가 추가한 `services.AddSingleton<RoutingAlgorithmRegistration>(testStubReg)` 같은 라인은 **그대로 보존** — 이 plan 은 logging 만 변경.
2. `new HealthService(httpFactory, opts)` → `new HealthService(httpFactory, opts, NullLogger<HealthService>.Instance)` 같은 ctor 호출 사이트의 인자 보강.
3. `new QueueDispatcher(...)` → 마지막 인자로 `NullLogger<QueueDispatcher>.Instance` 추가.

**Verify:** Phase 12-05 의 must_have ("Test-stub Algorithm: ... Name = \"ml\"; ModelVersion = \"test-stub\"") 가 깨지지 않은 채 build green 인지 확인.

```bash
grep -c "RoutingAlgorithmRegistration\|testStubReg" tests/SmartRouter.Tests/StreamingTests.fs tests/SmartRouter.Tests/LoggingTests.fs tests/SmartRouter.Tests/HealthFallbackTests.fs
# expected: each file >= 1 (Phase 12-05 결과 보존)
```
  </action>
  <verify>
```bash
for f in DecisionLogWriter CanaryWatchdog QueueDispatcher QwenUpstreamClient FailureDetector; do
  echo "=== $f ==="
  grep -c "ILogger<$f>" "src/SmartRouter.Cli/Adapters/$f.fs"
  grep -c "Serilog\.Log\.\|^open Serilog$" "src/SmartRouter.Cli/Adapters/$f.fs"
done
# All should show ILogger<T> count >= 1; Serilog references = 0
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
dotnet build tests/SmartRouter.Tests/SmartRouter.Tests.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Migrate BgeM3Embedder + ChatCompletions endpoint</name>
  <files>
    - src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
    - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
  </files>
  <action>
**For BgeM3Embedder.fs:** same pattern as Task 1.

**For ChatCompletions.fs (endpoint, not a class):**

ChatCompletions is a function/handler, not a type. The handler is registered via `app.MapPost("/v1/chat/completions", handler)`. The handler receives `HttpContext` and resolves dependencies via `ctx.RequestServices.GetRequiredService<T>()`.

**Use `ILoggerFactory.CreateLogger("ChatCompletions")` (chosen pattern; do NOT use a marker type):**

```fsharp
module SmartRouter.Cli.Endpoints.ChatCompletions

open Microsoft.Extensions.Logging

let handle (ctx: HttpContext) : Task = task {
    let logger =
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ChatCompletions")
    ...
    logger.LogInformation("Routing target={Target} reason={Reason} priority={Priority} stream=true", ...)
    ...
}
```

Resulting SourceContext: `ChatCompletions` — short, grep-friendly, matches the convention used by 13-03 endpoint hits.

Apply to all 5 emissions in ChatCompletions.fs. Resolve the logger ONCE at the top of the handler (not per-emission) to avoid repeated `GetRequiredService` calls; reuse the resolved `logger` across the function body.
  </action>
  <verify>
```bash
grep -c "ILogger<BgeM3Embedder>\|logger\.Log" src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
grep -c "ILoggerFactory\|CreateLogger\|logger\.Log" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# Both should show ILogger usage; no Serilog.Log static calls remain
grep -c "^open Serilog$" src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
# expected: 0 each
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 4: Migrate 4 function-based modules (Validator, DatasetMerger, ModelBootstrapper, Retrainer)</name>
  <files>
    - src/SmartRouter.Cli/Adapters/Validator.fs
    - src/SmartRouter.Cli/Adapters/DatasetMerger.fs
    - src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs
    - src/SmartRouter.Cli/Adapters/Retrainer.fs
  </files>
  <action>
These are F# `module` (not `type`), so functions don't have a constructor. Two options:

**Option A (Recommended).** Add `(logger: ILogger)` as a function parameter to each emitting function:

```fsharp
// BEFORE (Validator.fs)
let computeBaseline (modelPath: string) : float * float =
    if not (File.Exists modelPath) then
        Log.Warning("Validator.computeBaseline: {ModelPath} missing; using neutral baseline (acc=0, fbRate=1)", modelPath)
        (0.0, 1.0)
    else ...

// AFTER
let computeBaseline (logger: ILogger) (modelPath: string) : float * float =
    if not (File.Exists modelPath) then
        logger.LogWarning("Validator.computeBaseline: {ModelPath} missing; using neutral baseline (acc=0, fbRate=1)", modelPath)
        (0.0, 1.0)
    else ...
```

Caller responsibility: pass `ILogger`. RetrainingService (which calls Validator) already has `ILogger<RetrainingService>` from Task 1; can pass it directly:

```fsharp
let baseline = Validator.computeBaseline logger modelPath
```

The SourceContext in output will be `SmartRouter.Cli.Adapters.RetrainingService` (caller's logger), not `Validator` — acceptable trade-off; the message itself ("Validator.computeBaseline: ...") makes the source clear.

**Alternative (Option B):** Each function module gets a lazy logger:

```fsharp
// BEFORE
module Validator
open Serilog

let computeBaseline (modelPath: string) = ... Log.Warning(...) ...
```

```fsharp
// AFTER
module SmartRouter.Cli.Adapters.Validator
open Microsoft.Extensions.Logging

let mutable internal validatorLogger : ILogger = NullLogger.Instance :> ILogger

let init (factory: ILoggerFactory) =
    validatorLogger <- factory.CreateLogger("SmartRouter.Cli.Adapters.Validator")

let computeBaseline (modelPath: string) = ... validatorLogger.LogWarning(...) ...
```

Caller (`CompositionRoot` after host build): `Validator.init (services.GetRequiredService<ILoggerFactory>())`.

**Recommendation: Option A.** Explicit, testable, no module-state. SourceContext slight loss but messages are self-describing.

Apply Option A to all 4 files. Update all callers.

**Caller enumeration (must update each):**

| Module-based file | Caller files (must pass `logger` arg) |
|---|---|
| `Adapters/Validator.fs` | `Adapters/RetrainingService.fs` (calls `Validator.computeBaseline` and `Validator.validate`); `tests/RetrainingTests.fs` (calls Validator directly in some tests) |
| `Adapters/DatasetMerger.fs` | `Adapters/RetrainingService.fs` (calls `DatasetMerger.merge` and `DatasetMerger.hardCaseToTrainSample`); `tests/RetrainingTests.fs` |
| `Adapters/Retrainer.fs` | `Adapters/RetrainingService.fs` (calls `Retrainer.retrain`); `tests/RetrainingTests.fs` |
| `Adapters/ModelBootstrapper.fs` | `CompositionRoot.fs` (calls `ensureEmbeddingFilesPresent`, `ensureDummyModel`, `computeModelVersion` at startup); `tests/MLClassifierTests.fs` if it calls ModelBootstrapper directly |

For RetrainingService callers: pass the existing `logger: ILogger<RetrainingService>` directly (RetrainingService already has it from Task 1).

For CompositionRoot callers (ModelBootstrapper): CompositionRoot retains static `Log.*` for the bootstrap window, so pass a temporary logger built from `services.BuildServiceProvider().GetRequiredService<ILoggerFactory>().CreateLogger("ModelBootstrapper")` OR factor the ModelBootstrapper calls into a function that runs after `app.Build()` instead of during `configureServices`. Executor judgement; document the choice.

For test callers: pass `NullLogger.Instance` (non-generic).

After updating, `dotnet build` must succeed. Verify by `grep -rn "ModelBootstrapper\.\|Validator\.\|DatasetMerger\.\|Retrainer\." src/ tests/` and inspect each call site has the logger argument.
  </action>
  <verify>
```bash
grep -c "logger\.Log\|ILogger" src/SmartRouter.Cli/Adapters/Validator.fs src/SmartRouter.Cli/Adapters/DatasetMerger.fs src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs src/SmartRouter.Cli/Adapters/Retrainer.fs
# expected: each file >= 1
grep -c "Serilog\.Log\.\|^open Serilog$" src/SmartRouter.Cli/Adapters/Validator.fs src/SmartRouter.Cli/Adapters/DatasetMerger.fs src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs src/SmartRouter.Cli/Adapters/Retrainer.fs
# expected: 0 each
dotnet build src/SmartRouter.Cli/SmartRouter.Cli.fsproj 2>&1 | tail -3
# Build succeeded.
```
  </verify>
</task>

<task type="auto">
  <name>Task 5: Confirm CompositionRoot + Program.fs static Log retention</name>
  <files>
    - src/SmartRouter.Cli/CompositionRoot.fs
    - src/SmartRouter.Cli/Program.fs
  </files>
  <action>
These two files retain static `Log.*` for the bootstrap window (DI not yet resolvable for ILogger<T>). Verify and document:

CompositionRoot.fs has 1 Log.Warning call (around line 137). Acceptable to leave as static `Log.Warning` — this is during DI registration, before any service is constructed.

Program.fs has ~5 Log.* calls in the --retrain branch. These run AFTER `host.StartAsync()`, so DI IS available; could resolve `ILogger<{ProgramScope}>` via `host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Retrain")`. This is the cleaner path. Consider migrating these:

```fsharp
let retrainLogger =
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Retrain")
retrainLogger.LogInformation("Retrain: extracted {N} hard case(s)", List.length hardCases)
```

Apply this migration to Program.fs --retrain branch. Banner emission (post-Phase 13-05) will use the same pattern.

The very early startup messages (before host.StartAsync) keep static `Log.Warning(...)` — acceptable.

After this task: zero `Serilog.Log.X` calls in adapters; ≤ 2-3 in CompositionRoot/Program for genuinely bootstrap-time emissions.
  </action>
  <verify>
```bash
echo "=== Total static Serilog.Log usage in src/ (CompositionRoot + Program only acceptable) ==="
grep -rn "Serilog\.Log\.\|^Log\." src/SmartRouter.Cli/ | grep -v "Adapters/Logging\.fs\|//.*Serilog"
# expected: < 5 total hits, all in CompositionRoot.fs or Program.fs
echo "=== ILogger<T> usage count ==="
grep -rn "ILogger<" src/SmartRouter.Cli/ | wc -l
# expected: ~17-18 (one per adapter type + one per endpoint marker)
dotnet build 2>&1 | tail -3
dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | tail -3
# both: succeed/pass
```
  </verify>
</task>

</tasks>

<verification>
- [x] 15 type-based adapters use ILogger<T> ctor injection
- [x] 4 module-based adapters use ILogger function parameter (Option A)
- [x] ChatCompletions endpoint uses ILoggerFactory.CreateLogger("ChatCompletions")
- [x] CompositionRoot + Program retain static Log only for bootstrap window (~5 emissions)
- [x] Test fixtures pass NullLogger<T>.Instance to manual constructions
- [x] dotnet build clean; dotnet test green
- [x] Output template's {SourceContext} renders fully-qualified type names (visible in test output)
</verification>
