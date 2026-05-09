# Phase 12 ↔ Phase 13 Dependency Review

**Drafted:** 2026-05-09
**Author session:** parallel session (write-target = `.planning/preparing/`)
**Purpose:** 두 phase 의 파일 중첩, 실행 순서, 위험 요소 식별. 실행 전에 알아야 할 cross-phase 영향.

---

## 1. Conclusion (TL;DR)

**Phase 12 → Phase 13 강제 순차.** 병렬 실행 불가. CompositionRoot.fs 와 Program.fs 가 두 phase 모두에서 substantial 하게 변경되며, 같은 함수 / 같은 영역을 만짐. Phase 12 가 먼저 구조를 바꾸고 Phase 13 이 그 위에 logging 인프라를 얹어야 깔끔.

순서 바꾸면 (Phase 13 → Phase 12) 가능은 하지만:
- Phase 12-02 가 Phase 13 이 추가한 코드를 다시 정리해야 함 (e.g., Phase 13-04 가 추가한 `--log-level` 옆에서 Phase 12 가 `--routing-algorithm` 을 제거)
- Phase 12-05 의 test fixture rewire 가 Phase 13-02 가 추가한 NullLogger 인자를 보존해야 함
- Phase 13-05 LogRetentionService 가 등록될 함수가 Phase 12 미실행 상태에서는 `configureServices` (단일 함수); Phase 12 후에는 `configureRequestPipeline`. Phase 13 이 먼저면 Phase 12 가 등록을 옮겨야 함

**권장 순서: Phase 12 → Phase 13.** 이 review 는 Phase 12 이미 완료를 가정한 시점에서 Phase 13 plan 의 정확성을 점검.

---

## 2. File-overlap matrix

| File | Phase 12 변경 | Phase 13 변경 | 충돌 등급 |
|---|---|---|---|
| `src/SmartRouter.Cli/CompositionRoot.fs` | **12-02:** RoutingOptions.Algorithm 필드 삭제, routingAlgoStr 변수 삭제, heuristic dispatch arm 삭제, validation error 삭제, `configureServices` 를 `configureRequestPipeline` + `configureWithoutMl` 로 분할 | **13-02:** 단일 Log.Warning 호출 잔존 OK (정당; bootstrap 단계). **13-05:** `services.Configure<LogRetentionOptions>(...)` + `services.AddHostedService<LogRetentionService>()` 를 `configureRequestPipeline` 에 추가 | **HIGH** — 같은 함수 본문; 순차 강제 |
| `src/SmartRouter.Cli/Program.fs` | **12-02:** `--routing-algorithm` 플래그 파싱 블록 (lines 117-153) 삭제; `--retrain` 분기를 `configureWithoutMl` 호출로 재배선; heuristic config injection (`Routing:Algorithm = "heuristic"`) 7줄 삭제 | **13-01:** `Logging.configure()` → `Logging.configure(IConfig)`; builder 생성 후로 호출 위치 이동; `UseSerilog()` 추가. **13-04:** `parseLogLevel` + `--log-level=enum` 파싱 추가; `--trace` 마이그레이션 에러. **13-05:** startup banner + `ApplicationStopping.Register` 콜백 등록. | **HIGH** — 다른 코드 위치이지만 같은 파일에 substantial 변경; 순차 강제 |
| `src/SmartRouter.Cli/appsettings.json` | **12-02:** `Routing.Algorithm`, `Routing.ComplexityThreshold`, `Routing.Keywords` 키 3개 삭제 | **13-01:** `Serilog.MinimumLevel.Override` 테이블 추가; `Logging:Directory`, `Logging:RetentionDays`, `DecisionLog:RetentionDays` 추가 | **LOW** — 서로 다른 섹션; 순차이면 충돌 없음 |
| `tests/SmartRouter.Tests/LoggingTests.fs` | **12-05:** `Routing:Algorithm = "heuristic"` config override 제거, `configureWithoutMl` 호출, 직접 `RoutingAlgorithmRegistration` stub 주입; line 343 assertion `"heuristic"` → `"ml"` | **13-02 indirect:** `DecisionLogWriter` 구성자가 `ILogger<DecisionLogWriter>` parameter 추가 → 테스트가 직접 `new DecisionLogWriter(...)` 인스턴스화하면 `NullLogger<T>.Instance` 추가 필요 | **MEDIUM** — Phase 12 가 fixture 를 rewire 하고, Phase 13 이 NullLogger 추가; 순차이면 cleanly 처리 |
| `tests/SmartRouter.Tests/StreamingTests.fs` | **12-05:** 같은 패턴 fixture rewire | **13-02 indirect:** 직접 인스턴스화 사이트가 있으면 NullLogger 추가 | **MEDIUM** — same as LoggingTests |
| `tests/SmartRouter.Tests/HealthFallbackTests.fs` | **12-05:** 같은 패턴 fixture rewire + HealthService + QueueDispatcher 수동 등록 권장 | **13-02:** HealthService ctor 가 `ILogger<HealthService>` parameter 추가 → 수동 인스턴스화면 NullLogger 추가. **13-03:** HealthService 로그가 transition-only — 테스트가 특정 로그 emission 횟수 assert 하면 깨짐 | **HIGH** — 가장 위험한 cross-phase. § 4.E 별도 분석 |
| `tests/SmartRouter.Tests/RouterTests.fs` | **12-03:** rootTests 리스트에서 `RoutingTests.tests` 항목 제거 | **13-06:** rootTests 에 `LogRotationTests.tests` 항목 추가 | **LOW** — 같은 리스트지만 disjoint 항목; 순차 OK |
| `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` | **12-03:** `<Compile Include="RoutingTests.fs" />` 항목 제거 | **13-06:** `<Compile Include="LogRotationTests.fs" />` 항목 추가 | **LOW** — 같은 ItemGroup 이지만 disjoint 항목 |
| `src/SmartRouter.Cli/Adapters/HealthService.fs` | (직접 변경 없음 — 12-05 가 HealthFallbackTests 를 rewire 할 때 HealthService 구성자 시그니처 변경 가정) | **13-02:** ctor 에 `ILogger<HealthService>` 추가. **13-03:** probe loop transition-only 재구조화 | **HIGH (간접)** — Phase 12 의 HealthFallbackTests rewire 가 Phase 13-02 ctor 시그니처를 알아야 함 |
| `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` | (직접 변경 없음) | **13-02:** `ILoggerFactory.CreateLogger("ChatCompletions")` 패턴 도입. **13-03:** 2 emissions Information → Debug | **NONE** — Phase 12 가 안 만짐 |
| `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` | **12-01:** `formatReason` Heuristic arm 삭제 (5-case 매치) | (변경 없음 — 13-02 가 DecisionLogWriter 만 변경) | **NONE** — 다른 파일/함수 |
| `src/SmartRouter.Cli/Adapters/DecisionLogWriter.fs` | (변경 없음) | **13-02:** ctor 에 `ILogger<DecisionLogWriter>` 추가 | **NONE** |
| `src/SmartRouter.Cli/Core/Heuristic.fs` | **12-01:** DELETE | (변경 없음) | **NONE** |
| `src/SmartRouter.Cli/Core/Domain.fs` | **12-01:** RoutingReason DU 5 cases (Heuristic 제거); RoutingConfig 2 fields (Keywords/ComplexityThreshold 제거) | (변경 없음) | **NONE** |
| `src/SmartRouter.Cli/Core/Routing.fs` | **12-01:** canonicalKeywords 삭제; defaultRoutingConfig 2 필드 | (변경 없음) | **NONE** |
| `tests/SmartRouter.Tests/RoutingTests.fs` | **12-03:** DELETE | (변경 없음) | **NONE** |
| `tests/SmartRouter.Tests/MLRoutingTests.fs` | **12-04:** 3 testCase prune; `Routing:Algorithm` override 제거 | (변경 없음) | **NONE** |
| `scripts/check-routing-isolation.sh` | **12-06:** DELETE | (변경 없음) | **NONE** |
| `.gitignore` | **12-06:** `src/**/logs/` 추가 | (변경 없음) | **NONE** |
| `README.md` | (변경 없음) | **13-06:** "Operational Logging" 섹션 추가 | **NONE** |
| `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` | (변경 없음) | **13-01:** Serilog.Sinks.File + Settings.Configuration 추가. **13-05:** LogRetentionService.fs `<Compile>` 추가 | **NONE** |
| `src/SmartRouter.Cli/Adapters/Logging.fs` | (변경 없음) | **13-01:** 전면 rewrite | **NONE** |
| 19개 emission adapter (CanaryService, RetrainingService, TeacherLabeler, ...) | (변경 없음 — Phase 12 의 CanaryGate/CanaryMetrics/QwenUpstreamClient/CanaryPorts 4개 코멘트만 정리) | **13-02:** 모두 ILogger<T> 마이그레이션 | **LOW** — 코멘트 정리는 다른 위치; 같은 파일 내 disjoint edits |

---

## 3. Sequencing implications

### 3.1 Phase 12 가 Phase 13 의 전제

Phase 13-05 의 must_have 한 줄:
> "LogRetentionService is registered in DI via configureRequestPipeline (NOT configureWithoutMl)"

**`configureRequestPipeline` 함수는 Phase 12-02 가 만든다.** Phase 12 미실행 상태에서는 `configureServices` 단일 함수만 존재. 그러니 13-05 가 등록할 곳을 알려면 Phase 12 가 끝나 있어야 함.

만약 Phase 13 을 먼저 실행한다면:
- 13-05 는 `configureServices` 에 등록할 수밖에 없음
- 이후 Phase 12-02 가 `configureServices` 를 분할할 때 LogRetentionService 등록을 어느 쪽에 넣을지 다시 결정해야 함
- 정답이 명백 (`configureRequestPipeline`) 이라 큰 문제는 아니지만 "Phase 12 가 Phase 13 이 추가한 코드를 옮기는" 일이 발생

**결론: Phase 12 먼저.**

### 3.2 Phase 13 의 ILogger<T> 마이그레이션 (13-02) 은 Phase 12 를 인지해야 함

Phase 12-05 가 HealthFallbackTests / StreamingTests / LoggingTests 의 fixture 를 rewire — `services.AddSingleton<RoutingAlgorithmRegistration>(testStubReg)` 추가 등.

Phase 13-02 가 같은 test 파일에서 직접 `new DecisionLogWriter(...)` 등을 만나면 NullLogger 추가 필요.

순서 Phase 12 → Phase 13 으로:
- Phase 12-05 가 rewire 한 fixture 가 정착
- Phase 13-02 가 그 위에서 manual instantiation 사이트를 찾아 NullLogger 추가
- 두 번의 fixture 변경이 누적 — 하지만 commits 는 분리되니 reviewable

순서 Phase 13 → Phase 12 으로:
- Phase 13-02 가 NullLogger 인자 추가
- Phase 12-05 가 fixture 를 rewire — heuristic config override 제거하고 stub registration 추가; manual instantiation 의 NullLogger 인자는 보존해야 함
- Phase 12-05 executor 가 NullLogger 인자를 인지하고 보존하는 책임 추가

**결론: Phase 12 먼저가 인지 부담이 적음.**

### 3.3 appsettings.json 충돌 분석

Phase 12 가 삭제하는 키: `Routing.Algorithm`, `Routing.ComplexityThreshold`, `Routing.Keywords` (모두 `Routing` 섹션 안)
Phase 13 가 추가하는 키: `Logging.Directory`, `Logging.RetentionDays`, `DecisionLog.RetentionDays`, `Serilog.MinimumLevel.Override` (4개 모두 `Routing` 섹션 외)

**Disjoint.** 순서 무관 — 다만 동일 파일이라 git rebase 시 trivial conflict 발생할 수는 있음. 순차이면 무관.

---

## 4. Specific risks

### Risk A — CompositionRoot 의 routingAlgoStr 분기와 ML wiring 의 이전 위치

Phase 12-02 의 must_have:
> "the null|\"\"|\"heuristic\" arm of RoutingAlgorithmRegistration is gone"

이 arm 삭제 후 `services.AddSingleton<RoutingAlgorithmRegistration>(...)` 가 `configureRequestPipeline` 안에 unconditional 으로 들어감. Phase 13-05 의 LogRetentionService 등록도 같은 함수.

**위험:** Phase 12-02 executor 가 `configureRequestPipeline` 의 끝 / 시작 / 중간 어디에 LogRetentionService 등록 자리를 남길지 명확하지 않음. Phase 13-05 plan 은 "AddHostedService<LogRetentionService>() 추가" 만 명시; 정확한 위치 미지정.

**완화:** Phase 13-05 plan 에 위치 힌트 추가 — "RoutingAlgorithmRegistration 등록 직후, ChatCompletions 엔드포인트 등록 전" 정도. 또는 `configureRequestPipeline` 끝쪽 (여러 BackgroundService 등록 클러스터). 현 plan 은 충분히 명시되어 있음 (functional 위치는 어디든 무관 — DI 등록 순서는 의미 없음).

**실제 위험: LOW** — DI 등록은 unordered.

### Risk B — Program.fs 에 두 phase 의 코드 변경이 동시에

Phase 12-02 가 Program.fs 의 두 영역 변경:
1. `--retrain` 분기 (lines 21-105) — heuristic config injection 7줄 삭제 + `configureWithoutMl` 호출
2. `--routing-algorithm` flag 파싱 (lines 117-153) — 전체 블록 삭제

Phase 13 이 Program.fs 의 여러 영역 변경:
- 13-01: `Logging.configure()` 호출 위치 이동 + IConfig 인자
- 13-04: `parseLogLevel` 함수 + `--log-level` 파싱 추가 (Phase 12 가 삭제한 영역과 비슷한 위치 — argument parsing 영역)
- 13-05: startup banner + ApplicationStopping 콜백 등록

**위험:** Phase 12-02 가 `--routing-algorithm` 블록을 삭제 후 13-04 가 `--log-level` 블록을 그 자리에 추가. 같은 영역. Phase 12 먼저면: 12-02 가 깨끗한 자리에 13-04 가 들어감. 반대면: 13-04 가 추가 후 12-02 가 옆에서 삭제 — 두 블록의 분리 라인이 정확하지 않으면 `--log-level` 까지 삭제될 위험.

**완화:** Phase 13-04 plan 의 verify 단계가 `grep "routing-algorithm"` 0 hit 를 검증; Phase 12-02 plan 의 verify 가 `grep "log-level"` 가 0 hit 가 **아닌**지 (1+ hit 보장) 확인하는 테스트 명시 권장.

**실제 위험 (Phase 12 → Phase 13 순서 가정):** LOW.

### Risk C — Test 카운트 baseline 가 Phase 13 의 +13 보장

Phase 12 후 테스트: 86 → ~61-66 (RoutingTests 22 + MLRoutingTests 3 = 25 삭제, 일부 통합 효과 있을 수 있음)
Phase 13 후 테스트: ~61-66 + 13 = ~74-79

원래 86 → 최종 74-79. 순 감소 ~7-12.

**위험:** Phase 13-06 의 must_have 가 "previous baseline + 13" 이라고 명시. "previous baseline" 이 86 이라 가정하면 Phase 13 가 이미 Phase 12 후 baseline 변경을 반영해야 함.

**완화:** Phase 13-06 plan 의 "previous baseline" 을 명시적으로 정의 — "Phase 12 완료 후 baseline (~63 ± 3); Phase 13 종료 시 +13 = ~76". CONTEXT 의 "baseline + 13" 표현이 ambiguous; executor 가 측정해서 기록.

**실제 위험: LOW** — verify 단계는 정확한 숫자가 아닌 "+13" 증가만 검증해도 충분.

### Risk D — HealthFallbackTests 의 cross-phase 충돌 (가장 큰 위험)

세 가지 변화가 같은 test 파일에 누적:

**Phase 12-05** (fixture rewire):
- `Routing:Algorithm = "heuristic"` config override 제거
- `configureWithoutMl` 호출
- HealthService + QueueDispatcher 수동 등록 (CONTEXT note 권장)
- testStubReg 직접 주입

**Phase 13-02** (ILogger migration):
- HealthService 가 ctor 에 `ILogger<HealthService>` parameter 추가
- HealthFallbackTests 의 수동 HealthService 등록 사이트 (Phase 12-05 가 추가한) 가 NullLogger 인자 필요

**Phase 13-03** (transition-only logging):
- HealthService 의 probe loop 가 INFO emission 을 transition-시에만; steady-state 는 DEBUG
- HealthFallbackTests 가 specific 한 log emission 횟수 / 메시지를 assert 하면 깨질 수 있음

**현재 Phase 12-05 plan Task 3 의 핵심 해결책:**
> "HealthFallbackTests calls configureWithoutMl AND additionally registers HealthService + QueueDispatcher manually"

이 수동 등록 사이트가 Phase 13-02 ctor 변경을 모르면 build break.

**완화:**
1. Phase 12-05 plan Task 3 에 추가 note: "Phase 13-02 (이후 실행) 가 HealthService ctor 에 ILogger<HealthService> 를 추가할 것; 그 시점에 NullLogger<HealthService>.Instance 인자 추가 필요. 미리 작성하지 말고 13-02 executor 가 catch."
2. Phase 13-02 plan Task 1 의 verify 가 `dotnet build tests/...` 를 명시적으로 포함 — fixture 의 manual construction site 가 누락되면 즉시 발견.
3. Phase 13-03 plan Task 2 가 HealthFallbackTests 실행 후 통과 여부 검증 — 만약 transition-only 가 특정 assertion 을 깨면 이때 발견.

**실제 위험: MEDIUM-HIGH.** Phase 13-02 executor 의 인지가 필수. 위의 완화 1번 (Phase 12-05 plan note) 이 핵심.

### Risk E — Test stub algorithm 의 ModelVersion = "test-stub" vs Phase 13 LoggingTests 가정

Phase 12-05 에서 testStubReg.ModelVersion = "test-stub". JSONL DecisionLog 의 model_version 필드가 "test-stub" 으로 출력.

Phase 13-06 LogRotationTests 가 ModelVersion 을 assert 하지 않으므로 무관. (LoggingTests Phase 5 는 model_version 을 assert 하는데, Phase 12-05 가 testStub 으로 바꾸면서 그 assertion 도 업데이트해야 함 — 12-05 plan Task 2 Edit 3 에 명시되어 있음.)

**실제 위험: NONE** — Phase 12 plan 이 이미 처리.

### Risk F — Startup banner 의 routing.algorithm 필드 (Phase 12 후 invariant)

Phase 13-05 startup banner emits `routing.algorithm = "ml"`. Phase 12 후 이 값은 항상 "ml" — invariant.

**위험:** banner 가 invariant 필드를 출력해서 정보 가치 ↓.

**완화:** banner 에 유지 (운영자에게 "ML 로 동작 중" 확인 가치는 있음); 또는 Phase 13-05 plan task 2 에서 routing.algorithm 필드 삭제 검토. 현 plan 은 유지하는 방향.

**실제 위험: NONE (cosmetic only).**

### Risk G — `configureWithoutMl` 의 DI 누락 — Phase 13 에서 발견될 가능성

Phase 12-02 가 `configureWithoutMl` 을 만들 때 어떤 DI 등록이 누락되면 (e.g., HealthService 를 빼먹음 — 의도이지만 → HealthFallbackTests 깨짐), Phase 13 의 후속 plan 이 그 불완전한 함수를 사용.

**완화:** Phase 12-02 plan 의 verify 가 `--retrain` 만이 아니라 test fixtures 까지 build 통과하는지 확인. 12-05 plan 이 후속이라 이때 발견.

**실제 위험: LOW** — Phase 12 자체 verify 단계로 catch.

### Risk H — Phase 13-02 가 `Adapters/Logging.fs` 에 ILogger 적용?

Phase 13-02 plan 의 files_modified 목록에 `Adapters/Logging.fs` 는 없음 (Logging.fs 는 13-01 이 rewrite, 13-02 의 ILogger migration 대상이 아님). Logging.fs 는 static `Log.Logger` 를 set 하는 bootstrap 모듈; ILogger<Logging> 같은 건 의미 없음. **OK**.

### Risk I — `Program.fs` 의 static Log 잔존

Phase 13-02 plan Task 5 가 명시: Program.fs 의 `Log.*` 호출 일부 (--retrain branch) 를 `host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Retrain")` 으로 마이그레이션. 그러나 startup window 의 `Log.Information` (예: "Phase 13 startup") 은 static 유지.

**위험:** Phase 13-05 startup banner 가 `app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")` 사용 — host 가 build 된 시점이라 OK. 하지만 만약 banner 에서 한 줄 더 추가하다가 host 가 build 안 된 시점에 emission 시도하면 NullReferenceException.

**완화:** 13-05 plan 의 banner 위치 명시 — "AFTER `let app = builder.Build()`"; 모든 DI lookup 이 그 시점 이후. 현 plan 명확.

**실제 위험: LOW.**

### Risk J — `Logging:Directory` 의 path resolution

Phase 13-01 plan 이 `Path.Combine(logDir, "smart-router-.log")` 사용. logDir 이 상대경로 (`logs/operational`) 이면 `Path.Combine` 은 그대로 상대경로 반환 — 이후 `WriteTo.File` 이 process 의 CurrentDirectory 기준으로 해석.

**Phase 12 와 무관** — Phase 13 자체 issue. CurrentDirectory 가 launchd 환경에서 (`WorkingDirectory` plist key) 안전하게 설정되어야 함. Phase 11 의 launchd plist 가 이미 처리 (Phase 11 plan 의 must_have 에 "WorkingDirectory is /Users/ohama/llm-system/services/smart-router").

**실제 위험: NONE (Phase 11 dependency 충족).**

---

## 5. Recommended execution order

### 5.1 Sequential (recommended)

```
Time →
[12-01] → [12-02] → [12-03 ∥ 12-04 ∥ 12-05] → [12-06]
                                                  ↓
                                             (Phase 12 done)
                                                  ↓
[13-01] → [13-02] → [13-03 ∥ 13-04] → [13-05] → [13-06]
```

총 9 wave (Phase 12 의 4 + Phase 13 의 5).

### 5.2 Phase 12 plan 에 추가할 cross-phase note

Phase 12-05 (fixture migration) plan 의 Task 3 (HealthFallbackTests) 끝부분에 추가 권장:

> "**Cross-phase note:** Phase 13-02 (후속) 가 HealthService ctor 에 `logger: ILogger<HealthService>` parameter 를 추가할 예정. HealthFallbackTests 의 manual HealthService 인스턴스화 사이트는 Phase 13-02 executor 가 NullLogger<HealthService>.Instance 인자를 추가하는 책임. Phase 12-05 executor 는 미리 추가하지 말 것 (현재 ctor 시그니처 기준으로 작성)."

이 한 줄로 Risk D 의 90% 완화.

### 5.3 Phase 13 plan 에 추가할 cross-phase note

Phase 13-02 plan Task 2 (DecisionLogWriter, CanaryWatchdog, QueueDispatcher 등) 끝부분에 추가 권장:

> "**Cross-phase note:** Phase 12-05 (선행) 가 LoggingTests/StreamingTests/HealthFallbackTests 의 fixture 를 rewire 했으니, 이 plan 의 manual instantiation 사이트는 그 rewire 결과 위에서 작업. `services.AddSingleton<RoutingAlgorithmRegistration>(testStubReg)` 같은 추가 라인은 보존; NullLogger 인자만 ctor 호출에 추가."

Phase 13-05 plan Task 1 (LogRetentionService DI registration) 끝부분에 추가 권장:

> "**Cross-phase note:** Phase 12-02 (선행) 가 `configureServices` 를 `configureRequestPipeline` + `configureWithoutMl` 로 분할했음. LogRetentionService 등록은 production-only 이므로 `configureRequestPipeline` 에 들어감. `configureWithoutMl` (--retrain + tests) 에는 등록 안 함."

이 두 note 가 Risk B + Risk G 완화.

---

## 6. Decision required

이 review 결과 plan 들에 cross-phase note 를 추가할 지 정하세요.

**Option A — Plan 수정** (권장)
- Phase 12-05 plan Task 3 에 "HealthService ctor 변경 인지 note" 추가
- Phase 13-02 plan Task 2 에 "Phase 12 fixture rewire 보존 note" 추가
- Phase 13-05 plan Task 1 에 "configureRequestPipeline 등록 note" 추가
- 작은 텍스트 변경; 실행 안전성 ↑

**Option B — 현 plan 그대로**
- 현재도 critical-path verify 단계 충분. Cross-phase context 는 executor (gsd-executor agent) 가 plan 간 SUMMARY.md 를 읽으며 자연 학습.
- note 추가 안 하면 Risk D 가 medium 수준으로 잔존. 다른 risks 는 이미 LOW.

---

## 7. Summary table

| Risk | 등급 | Phase 12 → Phase 13 순서 시 | 현 plan 으로 자동 완화? |
|---|---|---|---|
| A. CompositionRoot DI 등록 위치 | LOW | DI 등록은 unordered | Yes |
| B. Program.fs --routing-algorithm vs --log-level 인접 | LOW | Phase 12 가 먼저 자리를 비움 | Yes (verify grep) |
| C. Test count baseline 모호 | LOW | "+13" 증가만 검증 | Yes |
| **D. HealthFallbackTests cross-phase** | **MEDIUM-HIGH** | Phase 13-02 executor 가 인지 필요 | **No (note 권장)** |
| E. Test stub ModelVersion vs LoggingTests | NONE | Phase 12 plan 이 처리 | Yes |
| F. Banner routing.algorithm invariant | NONE | cosmetic only | Yes |
| G. configureWithoutMl DI 누락 | LOW | Phase 12 verify 가 catch | Yes |
| H. Logging.fs ILogger 적용 여부 | NONE | 의도적 제외 | Yes |
| I. Program.fs static Log 잔존 | LOW | banner 위치 명확 | Yes |
| J. Logging:Directory path resolution | NONE | Phase 11 plist WorkingDirectory | Yes |

**핵심 액션 아이템:** Risk D 의 cross-phase note 를 plan 들에 추가할 지 결정.

---

*Drafted: 2026-05-09*
*Source: 12-CONTEXT.md, 13-CONTEXT.md, 6+6 plan 파일들*
