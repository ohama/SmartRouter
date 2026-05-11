# Phase 12 — Heuristic Routing Removal: Context

**Locked:** 2026-05-09 (concurrent session, write-target = `.planning/preparing/`)
**Decisions referenced:** §7 of `12-heuristic-removal-research.md`

<domain>
## Phase Boundary

Routing decision (35B vs 122B) 을 내리는 heuristic 코드 경로를 완전 제거한다. 대상: `Heuristic.fs` 모듈, `RoutingReason.Heuristic of score` DU case, `RoutingConfig.Keywords`/`ComplexityThreshold` 필드, `Routing.Algorithm` 설정 키, `--routing-algorithm` CLI flag, heuristic-internal 테스트, 그리고 `check-routing-isolation.sh` CI guard. ML routing 이 유일한 경로가 된다. archive 브랜치/태그/git history 는 untouched.

</domain>

<decisions>
## Locked Decisions

### Q1 — `--retrain` offline path: configureServices 분할 (Option B)

`CompositionRoot.fs` 의 `configureServices : IServiceCollection -> IConfiguration -> IServiceCollection` 을 두 함수로 분할:

- `configureRequestPipeline services config` — 모든 ML registration (IEmbedder, IClassifier, PredictionEnginePool, RoutingAlgorithmRegistration, MakeApplyMl closure 등) 포함. 정상 HTTP service path 가 호출.
- `configureOfflinePipeline services config` — retrain 에 필요한 것만 (IFailureDetector, ITeacherLabeler, IHardCaseDatasetWriter, named HttpClient "teacher", named HttpClient "qwen122b", DecisionLog directory wiring, BgeM3Embedder 옵션. **ML algorithm wiring 은 제외.** Embedder 는 retrain 의 dataset merger 가 prompt → embedding 변환에 쓰니 필요).

**선택:** Embedder 는 offline 도 필요 (Phase 8 DatasetMerger 가 hardCaseToTrainSample 에서 embed callback 호출). Classifier 는 retrain 에서 새로 train 하니 기존 인스턴스 불필요 — 단 pool 구조는 그대로 가도 무해. **Pragmatic 결정: offline 도 Embedder 는 등록, Classifier 는 등록 안 함, RoutingAlgorithmRegistration 안 등록, MakeApplyMl 안 등록.**

이 분할로 `Routing:Algorithm = "heuristic"` override 트릭 (Program.fs:34-40 의 7줄) 이 사라진다. `--retrain` 분기는 `configureOfflinePipeline` 만 호출.

대안: 공유 함수 `configureCommon` (HttpClient factories, Serilog wiring, decision-logger sink) + `configureRequestExtras` (ML algorithm + Kestrel-only) + `configureOfflineExtras` (retrain ports). 3-함수 구조. 더 DRY 하지만 plan 시 task 수가 늘어남. **권장: 2-함수 구조 (Request, Offline) + duplication 일부 허용.**

### Q2 — Test fixture: test-only RoutingAlgorithm stub 직접 주입 (Option B)

세 test 모듈 (StreamingTests, LoggingTests, HealthFallbackTests) 이 ML 모델 파일 회피용으로 `Routing:Algorithm = "heuristic"` config override 를 사용 중. Q3 에서 그 키 자체가 사라지므로 다른 메커니즘이 강제로 필요.

**선택:** 각 test 모듈이 `RoutingAlgorithmRegistration` 을 직접 inject. Algorithm field = `fun cfg req → { Target = Qwen35B; Priority = Low; Reason = ML; IsFallback = false; ModelVersion = "test-stub" }`. Name = `"ml"`, ModelVersion = `"test-stub"`.

세부:
- `services.AddSingleton<RoutingAlgorithmRegistration>(testStubReg)` 를 test fixture 의 `configureServices` 호출 **이후** 추가하여 last-registration-wins (이미 Phase 9 에서 같은 패턴 사용). `services.RemoveAll<RoutingAlgorithmRegistration>()` 후 `AddSingleton` 도 가능하지만 last-wins 가 더 작은 변경.
- `LoggingTests` 의 `routing_algorithm = "heuristic"` 단언 (line 343) → `routing_algorithm = "ml"` 로 변경.
- ML registration 자체는 여전히 일어나지만 (configureRequestPipeline 호출되니까), `IEmbedder`/`IClassifier` 의 missing-files 가 문제 → fake `IEmbedder` (returns `Array.zeroCreate 1024`) + fake `IClassifier` (returns `{ Score = 0.0f; PredictedLabel = false }`) 도 동시에 inject. **단, RoutingAlgorithm 자체를 stub 으로 주입했으면 IEmbedder/IClassifier 는 호출되지 않는다 (stub algorithm 이 둘을 안 부름).** 따라서 fake 등록은 _DI 등록 시점에 missing-file 으로 throw 하지 않게_ 해야 함 — `BgeM3Embedder` 의 ctor 가 file load 를 lazy 하게 하거나, fake instance 를 직접 inject 하거나 둘 중 하나.

**Pragmatic:** test fixture 는 `services.AddSingleton<IEmbedder>(fakeEmbedder).AddSingleton<IClassifier>(fakeClassifier).AddSingleton<RoutingAlgorithmRegistration>(testStubReg)` 를 configureRequestPipeline 호출 **후**에 또 등록 → last-registration-wins 가 fake 들을 활성화. ML init 의 file-load 코드 (ensureEmbeddingFilesPresent) 는 그래도 한 번 돈다 → 이걸 회피하려면 **Q1 의 configureOfflinePipeline 형태의 "no-ML" variant 를 test 에서도 호출**하면 됨. **결정: test fixture 도 configureOfflinePipeline 호출 후 stub algorithm 직접 등록**. ML init file-load 우회.

이 결정 → Q1 의 configureOfflinePipeline 은 "retrain" 전용이 아니라 "ML init 회피" 일반화 함수가 됨. 이름을 `configureWithoutMl` 정도로 더 일반화하는 게 맞을 수도. **권장:**
- `configureRequestPipeline` (full ML, production)
- `configureWithoutMl` (offline retrain + tests; no ensureEmbeddingFilesPresent, no IClassifier, optional fake IEmbedder injectable post-config)

### Q3 — `Routing.Algorithm` 설정 키: 완전 제거

`appsettings.json` 의 `"Algorithm": "ml"` 줄 삭제. `RoutingOptions.Algorithm : string` 필드 (CompositionRoot.fs:72) 삭제. `routingAlgoStr` 변수 (line 295) 와 모든 분기 (line 299, 348-358, 391, 573, 589) 삭제. ML wiring 은 무조건 실행.

Phase 12 에서는 Validation error message ("valid values: heuristic, ml") 도 삭제. 미래에 algorithm 이 추가될 때 키를 다시 도입하는 것은 새 phase 의 일.

### Q4 — `--routing-algorithm` CLI flag: 완전 제거

Program.fs lines 117-153 (parse + validate + AddInMemoryCollection 주입) 전체 삭제. 더 이상 valid value 없음 → flag 가 의미 없음.

### Q5 — RoutingTests.fs: 전체 삭제

`tests/SmartRouter.Tests/RoutingTests.fs` 파일 자체 삭제. `Tests.fsproj` 에서 `<Compile Include="RoutingTests.fs" />` 줄 삭제. `RouterTests.rootTests` 리스트에서 `RoutingTests.tests` 항목 삭제.

**손실 인지:**
- Stage-1 (model override parsing, alias case-insensitivity) unit-level 커버리지 사라짐.
- Stage-2 (task table dispatch, unknown task error, edited config taskTable) unit-level 커버리지 사라짐.
- Stage-3 (heuristic) 커버리지는 의도적 손실 (코드 자체가 사라지므로).

**완화:** Stage 1+2 는 integration tests (StreamingTests, LoggingTests, HealthFallbackTests, MLRoutingTests) 가 간접적으로 검증함 (model override 가 있는 request 가 들어오면 Endpoint level 에서 정확한 target 으로 라우팅되는 것을 확인). Phase 12 이후 stage 1+2 의 micro-level 회귀 위험은 약간 증가하지만 user 의 명시적 선택.

### Q6 — `scripts/check-routing-isolation.sh`: 삭제

Heuristic.fs 가 사라지므로 isolation 가드 의미 없음. 파일 삭제.

### Q7 — `archive/heuristic-baseline` branch + `v0.5-heuristic-baseline` tag: 보존

Phase 12 코드 변경에서 git branch/tag 는 일절 건드리지 않음. `git branch -D archive/heuristic-baseline` 같은 명령 없음. Tag 도 그대로.

</decisions>

<specifics>
## Specific Implementation Notes

### Wave / plan structure (planner 가 사용할 sketch)

| Wave | Plan | What |
|---|---|---|
| 1 | 12-01 | Core deletion + Cli mechanical fallout. 5 files. Atomic — build green at plan end. |
| 2 | 12-02 | Cli rewire: `configureWithoutMl` split (Q1) + heuristic arm deletion (Q3) + appsettings.json key deletion (Q3) + `--routing-algorithm` flag deletion (Q4). Depends on 12-01. |
| 3 (parallel) | 12-03 | RoutingTests.fs deletion + Tests.fsproj prune (Q5). Depends on 12-02. |
| 3 (parallel) | 12-04 | MLRoutingTests prune (heuristic-vs-ML divergence test, both-satisfy test, --routing-algorithm override test, heuristic-fixture overrides). Depends on 12-02. |
| 3 (parallel) | 12-05 | StreamingTests + LoggingTests + HealthFallbackTests fixture migration to test-stub RoutingAlgorithm (Q2). LoggingTests' `routing_algorithm = "heuristic"` assertion → `"ml"`. Depends on 12-02. |
| 4 | 12-06 | Cleanup: `scripts/check-routing-isolation.sh` delete (Q6) + stray `src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl` delete + 4 historical comment cleanups (CanaryGate.fs, CanaryMetrics.fs, QwenUpstreamClient.fs, CanaryPorts.fs). Verification greps. Depends on 12-03/04/05. |

### Naming for the configureServices split

Author recommendation: **`configureRequestPipeline`** and **`configureWithoutMl`**.

- `configureRequestPipeline services config` = full production wiring (Kestrel-bound HTTP service, all ML adapters).
- `configureWithoutMl services config` = subset for `--retrain` and tests: HTTP factories, retrain ports, decision-logger sink, optional `IEmbedder` injectable post-config. Excludes `IClassifier`, `RoutingAlgorithmRegistration`, `MakeApplyMl`, `ensureEmbeddingFilesPresent`.

If executor finds clearer names while implementing (e.g. `configureFull` / `configureMinimal`), they may rename — but commit message should justify deviation from CONTEXT names.

### Routing.fs `defaultRoutingConfig` after field deletion

```fsharp
let defaultRoutingConfig : RoutingConfig =
    { TaskTable           = canonicalTaskTable
      MlThreshold         = 0.5f }
```

`Keywords`, `ComplexityThreshold` 필드 사라짐. `canonicalKeywords` 상수도 삭제 (consumer 사라짐).

### Routing.fs `routeRequest` after `RoutingAlgorithm` becomes the only path

Signature 무변화. Stage 3 fallthrough 가 항상 `algorithm config req` 호출 — `algorithm` 은 항상 ML closure (또는 test stub). 즉 코드 변경 없음. 외부 시그니처는 미래 확장성을 위해 유지.

### `Domain.fs` 변경

```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    // | Heuristic of score: int   ← DELETE
    | Default
    | ML
    | FallbackTo35B

type RoutingConfig =
    { // ComplexityThreshold : int   ← DELETE
      // Keywords            : string list   ← DELETE
      TaskTable           : Map<string, ModelId * Priority>
      MlThreshold         : float32 }
```

### `DecisionLogger.fs` `formatReason` 변경

```fsharp
let formatReason (reason: RoutingReason) : string =
    match reason with
    | ExplicitModelOverride alias -> sprintf "explicit_model:%s" alias
    | ExplicitTask taskType       -> sprintf "explicit_task:%A" taskType
    // | Heuristic score          ← DELETE
    | Default                     -> "default"
    | ML                          -> "ml"
    | FallbackTo35B               -> "fallback_to_35b"
```

`TreatWarningsAsErrors=true` 환경에서 FS0025 (incomplete match) 가 발생하지 않도록 Heuristic case 패턴이 코드 어디에도 남아 있지 않음을 확인. grep 으로 verify.

### `appsettings.json` 변경

```json
"Routing": {
  "Algorithm": "ml",          ← DELETE this line
  "TimeoutSeconds": 300,
  "ComplexityThreshold": 3,   ← DELETE this line
  "Keywords": [...],          ← DELETE this section
  "TaskTable": {...},
  "ModelAliases": {...},
  "ML": {...}
}
```

`Routing.fs` 의 `defaultRoutingConfig` 는 `Keywords` 필드가 사라지므로 `canonicalKeywords` 도 같이 사라짐. `appsettings.json` 의 `Keywords` 배열도 따라 삭제.

### `RoutingTests.fs` 삭제 후 SmartRouter.Tests.fsproj

```xml
<Compile Include="RoutingTests.fs" />   ← DELETE
```

`RouterTests.fs` 의 `rootTests` 리스트에서 해당 항목 삭제.

### Test fixture migration (Q2=B)

세 test 모듈에 공통 helper 도입 (각 test 모듈 안에 인라인 OR 새 `TestHelpers.fs`):

```fsharp
// inline in each test module after services build
let private testStubAlgorithm : SmartRouter.Core.Domain.RoutingAlgorithm =
    fun _cfg _req ->
        { Target       = SmartRouter.Core.Domain.Qwen35B
          Priority     = SmartRouter.Core.Domain.Low
          Reason       = SmartRouter.Core.Domain.ML
          IsFallback   = false
          ModelVersion = "test-stub" }

let private testStubRegistration : SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration =
    { Algorithm    = testStubAlgorithm
      Name         = "ml"
      ModelVersion = "test-stub" }

// In test setup, after configureWithoutMl call:
services.AddSingleton<SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration>(testStubRegistration)
```

LoggingTests' assertion change:
```fsharp
// line 343
Expect.equal (root.GetProperty("routing_algorithm").GetString())
              "ml"   // was "heuristic"
              "routing_algorithm = ml (test-stub)"
```

### Verification grep checklist (Phase 12 final)

- 0 hits: `grep -rn "applyHeuristic\|scoreComplexity\|canonicalKeywords" src/ tests/`
- 0 hits: `grep -rn "Heuristic " src/ tests/` (이전 `RoutingReason.Heuristic` DU case)
- 0 hits: `grep -rn "\"heuristic\"" src/SmartRouter.Cli/ tests/SmartRouter.Tests/` (string literal)
- 0 hits: `grep -rn "routing-algorithm\|--routing-algorithm" src/ tests/`
- 0 hits: `grep -rn "Routing\.Algorithm\|Routing:Algorithm" src/SmartRouter.Cli/ tests/SmartRouter.Tests/`
- file absent: `src/SmartRouter.Core/Heuristic.fs`
- file absent: `tests/SmartRouter.Tests/RoutingTests.fs`
- file absent: `scripts/check-routing-isolation.sh`
- `dotnet build` clean with `TreatWarningsAsErrors=true`
- `dotnet test` green; new test count = old (86) − removed_count

### Test count delta estimate

- RoutingTests.fs 전체 삭제 (Q5): −22 (전체 testCase 수 — RoutingTests 의 모든 항목)
- MLRoutingTests prune (Q4 + heuristic-related): −3 (both-satisfy, divergence, --routing-algorithm override)
- StreamingTests / LoggingTests / HealthFallbackTests: 0 (rewired, count 동일)

Net: **86 → ~61 passed** (with embeddings: 93 → ~68). 큰 손실로 보이지만 RoutingTests 의 삭제 비중이 높음 (사용자 명시적 선택). Phase 12 verifier 가 이 새 baseline 을 record.

### Constraint inheritance

- ARCH-01 (Pure-Core BCL-only): 변동 없음. Heuristic.fs 자체가 BCL-only 였음.
- TreatWarningsAsErrors=true: 유지.
- Per-task atomic commits, never `git add -A`: 유지.
- F# `task {}` only convention: 변동 없음.

### Claude's discretion

- 인라인 helper vs 별도 TestHelpers.fs 결정 (Q2 stub).
- `configureWithoutMl` vs `configureOffline` vs `configureMinimal` 등 함수명.
- Wave 3 의 3개 plan 을 더 잘게 쪼개는 것이 task 단위 명확성에 도움이 되면 가능.
- `appsettings.json` `Routing` 섹션이 비게 되면 (Algorithm + ComplexityThreshold + Keywords 셋 다 삭제) 섹션 자체를 어떻게 정리할지 (`Keywords: []` 빈 배열 남기기 vs `Routing.TaskTable/ModelAliases/ML` 만 남기기): TaskTable 등은 유지하므로 섹션 자체는 유지, Algorithm/ComplexityThreshold/Keywords 키만 삭제.

</specifics>

<deferred>
## Deferred Ideas

- Future re-introduction of multiple routing algorithms (e.g. ML-A vs ML-B A/B): 그때 `Routing.Algorithm` 키 다시 도입. 이번 phase 에서는 future-proof knob 안 둠.
- Heuristic 을 fallback as-fallback (ML 모델 corrupt 시 heuristic 으로 강등) 하는 안전망: 명시적으로 거부 — Phase 10 의 health/fallback 이 이 역할 (model 미로드 시 35B 로 직행).
- RoutingTests.fs 의 stage-1/2 unit-level coverage 재도입 (Q5 의 손실 보완): 별도 phase 의 일이며 우선도 낮음. Integration tests 가 간접 보장.
- `scripts/check-routing-isolation.sh` 의 generic 후속 — 새로운 모듈 isolation 가드가 필요해지면 그때 별도 스크립트로.
- 상위 README / spec docs (graphify_smart_router_prompt.md 등) 의 heuristic 멘션 정리: Phase 12 범위 외 (사용자가 명시적으로 routing-decision 코드만 대상으로 한정).

</deferred>

---

*Phase: 12-heuristic-removal*
*Context locked: 2026-05-09*
*Source: §7 of 12-heuristic-removal-research.md*
