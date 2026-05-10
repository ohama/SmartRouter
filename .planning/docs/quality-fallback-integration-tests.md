# Quality Fallback Integration Tests — `QualityFallbackTests.fs` 상세 분석

**작성:** 2026-05-10
**source:**
- `tests/SmartRouter.Tests/QualityFallbackTests.fs` (470 줄, 2 testCase)
- `src/SmartRouter.Cli/Adapters/QualityCheck.fs` (`isBadResponse` + `QualityFallbackOptions`)
- `src/SmartRouter.Cli/Adapters/TraceLogger.fs` (TraceRecord + ITraceLogger)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` (non-streaming quality fallback path)
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` (`formatReason` 6-arm match)
- `src/SmartRouter.Core/Domain.fs` (`RoutingReason.FallbackTo122B`)

**대상:** quality fallback 동작을 운영 관점에서 디버깅하거나, 비슷한 fake-Kestrel + JSONL-grep 스타일 통합 테스트를 다른 phase 에 추가하려는 사람.

---

## 결론 (TL;DR)

`QualityFallbackTests.fs` 는 distillation 디자인의 **"Failure = Gold Data"** 패턴 — 35B 응답이 나쁘면 자동으로 122B 재시도 — 을 **운영자가 production 에서 사고 진단하는 방식 (`jq` 로 JSONL grep) 그대로** 검증한다.

2 개의 testCase:
- **QF-01** — 35B good response → fallback 비발화. DecisionLog `routing_reason="ml"` + Trace `fallback_kind=null` 확인.
- **QF-02** — 35B "TODO: implement this" → quality check 실패 → 122B 재시도 → 122B response 가 client 에 forward. DecisionLog `routing_reason="fallback_to_122b"` + Trace `fallback_kind="quality"` + `initial_response_excerpt` 에 "TODO" 포함 확인.

핵심 검증 전략은 **`File.ReadAllLines` + `JsonDocument.Parse` + `findRow predicate`** — 운영자가 `jq` 로 prompt UID 검색하는 명령 1:1 대응.

---

## 1. 인프라 헬퍼 (3 개)

### 1.1 `startFakeUpstream` (lines 44-64)

가짜 Kestrel 서버를 임의 free port (`127.0.0.1:0`, OS assignment) 에 in-process 로 띄움. 호출자가 `respond: HttpContext -> Task<unit>` 람다로 응답 본문 제어. 35B + 122B 두 개를 따로 띄움.

**중요한 path-dispatch 디테일** (executor deviation 으로 발견):
- `QwenUpstreamClient.CompleteAsync` 가 lazy 하게 한 번 `/v1/models` probe 를 함 (per upstream).
- 초기 skeleton 은 모든 path 에 canned chat-completions JSON 반환 → `/v1/models` 응답 파싱 실패 → 502.
- 수정: `ctx.Request.Path.Value` 로 분기:
  - `/v1/models` → `{"data":[{"id":"/fake/qwen35b"}]}`
  - 그 외 → canned chat response (test 별로 good/bad 다름)

### 1.2 `startTestRouter` (lines 77-283)

실제 router 를 in-process 로 띄움. Production CompositionRoot 와 같은 DI 그래프지만 외부 의존성 (ML 모델 파일, mlx_lm.server, GPU) 은 stub.

**핵심 stub 6 개:**

| stub | 목적 |
|---|---|
| `stubAlgorithm` | `decision.Target = Qwen35B` + `Reason = ML` 강제 — ML 분류기 로직과 무관하게 35B 로 라우팅 (가독성) |
| `stubHealthProbe` | 두 upstream 모두 `IsReachable = true` — quality fallback 의 122B 재시도가 `healthProbe.IsReachable(Qwen122B)` 체크를 통과해야 함 |
| `NullCanaryGate` + `NoOpCanaryMetrics` | 카나리 비활성 |
| `Trace:Enabled = true` | `--trace-responses` 플래그 켠 상태 동등 |
| `QualityFallback.Enabled = true` + `MinResponseLength=30` + `BadKeywords=["TODO", "I think"]` | 기본값 활성화 |
| 임시 디렉토리 (`Path.GetTempPath()/smart-router-qf01-{Guid}`) | `logs/decisions/` + `logs/trace/` 격리; `testSequenced` 와 결합해 병렬 race 방지 |

**`IHealthProbe` curried 시그니처 (executor deviation):**
- 인터페이스는 curried: `IsReachableAsync target ct`
- 초기 skeleton 이 tupled `(_target, _ct)` 로 작성 → FS 컴파일 에러
- 수정: `member _.IsReachableAsync(_target) _ct = Task.FromResult(true)`

**`TraceLogger` triple-reg:**
- `configureWithoutMl` 는 ITraceLogger 등록 안 함 (production 은 `Trace:Enabled` config 키로 conditional)
- 테스트 fixture 는 unconditional triple-reg: `TraceLogger` (concrete) → `ITraceLogger` (interface) → `IHostedService` (BackgroundService start)

**`QualityFallbackOptions` DI 배치 (14-04 deviation):**
- 초기 계획: `RoutingOptions.QualityFallback` nested
- 실제: standalone DI singleton (`configureRequestPipeline` + `configureWithoutMl` 양쪽 등록)
- 이유: `ChatCompletions.fs` 가 `CompositionRoot` 의존 없이 `GetRequiredService<QualityFallbackOptions>()` 로 직접 가져올 수 있게

### 1.3 `computePromptUid` + `findRow` (lines 285-313)

```fsharp
let private computePromptUid (prompt: string) : string =
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(prompt)
    let hash  = sha.ComputeHash(bytes)
    hash |> Array.take 6 |> Array.map (sprintf "%02x") |> String.concat ""
```

- 6 bytes = 12 hex chars → `prompt_hash[:12]` 와 동일
- **단일 메시지 전제**: production `computePromptHash` 는 `messages |> List.map .Content |> String.concat ""` 를 해시. test 가 single-message payload `{"messages":[{"role":"user","content":"<prompt>"}]}` 만 사용하므로 production 의 `String.concat` of one element = 그 element → 동일 결과.

```fsharp
let private findRow (jsonlPath: string) (predicate: JsonElement -> bool) : JsonElement option =
    if not (File.Exists(jsonlPath)) then None
    else
        File.ReadAllLines(jsonlPath)
        |> Array.tryPick (fun line ->
            if String.IsNullOrWhiteSpace(line) then None
            else
                let doc = JsonDocument.Parse(line)
                if predicate doc.RootElement then Some (doc.RootElement.Clone()) else None)
```

`JsonDocument.Parse` 사용 — string-Contains 회피. 운영자의 `jq 'select(.field == "...")'` 와 1:1 대응.

---

## 2. QF-01 — "35B good response → no fallback fires"

### 2.1 입력

| 요소 | 값 |
|---|---|
| prompt | `"explain recursion"` |
| 35B fake response | `"Recursion is a function calling itself with smaller inputs until a base case is reached."` (90+ chars; "TODO" 미포함; "I think" 미포함) |
| 122B fake response | `"122B should not be called"` — 호출되지 않아야 함 |

### 2.2 라우팅 흐름

```
client → POST /v1/chat/completions
  → router.ChatCompletions.handler
  → stubAlgorithm → decision.Target = Qwen35B
  → QueueDispatcher → QwenUpstreamClient → 35B fake-Kestrel
  → 35B response: "Recursion is a function..."
  → isBadResponse(content, opts) → false (length > 30 + no bad keyword)
  → final response = 35B response 그대로 forward
  → DecisionLog write: routing_reason="ml", fallback_used=false
  → TraceLog write: initial_target=Qwen35B, final_target=Qwen35B, fallback_kind=null
  → client receives 200 + 35B JSON
```

### 2.3 검증 (`Thread.Sleep(500)` 으로 async log flush 대기 후)

| 파일 | 항목 | 기대값 |
|---|---|---|
| HTTP 응답 | `respBody.Contains("Recursion is a function")` | true |
| HTTP status | | `200 OK` |
| DecisionLog | `target` | `"Qwen35B"` |
| DecisionLog | `routing_reason` | `"ml"` (no fallback) |
| DecisionLog | `fallback_used` | `false` |
| TraceLog | `prompt_uid` | `computePromptUid promptText` 와 일치 |
| TraceLog | `initial_target` | `"Qwen35B"` |
| TraceLog | `final_target` | `"Qwen35B"` |
| TraceLog | `fallback_kind` | `JsonValueKind.Null` (quality fallback 비발화 신호) |

**Cross-file 일관성 검증:** `prompt_uid` 가 `prompt_hash[:12]` 와 동일 → 운영자가 한 UID 로 양쪽 파일 검색 가능. 이 invariant 가 깨지면 prod 사고 시 `jq` join 이 실패 → 진단 불가능.

---

## 3. QF-02 — "35B 'TODO' → quality fallback fires → 122B serves response"

### 3.1 입력

| 요소 | 값 |
|---|---|
| prompt | `"explain recursion in detail"` (다른 prompt → 다른 UID; QF-01 과 격리) |
| 35B fake response | `"TODO: implement this"` (20 chars; **"TODO" 키워드 매치** → bad) |
| 122B fake response | `"Recursion is a function calling itself, terminating at a base case."` (good) |

### 3.2 라우팅 흐름

```
client → POST /v1/chat/completions
  → stubAlgorithm → decision.Target = Qwen35B
  → 35B fake → "TODO: implement this"
  → isBadResponse(content, opts) → true ("TODO" 매치)
  → healthProbe.IsReachable(Qwen122B) = true (stub)
  → retry decision built:
      { Target = Qwen122B
        Reason = FallbackTo122B
        IsFallback = true
        ModelVersion = versionProvider.CurrentVersion }
  → QueueDispatcher → 122B fake-Kestrel
  → 122B response: "Recursion is a function calling itself, terminating at a base case."
  → final response = 122B response (35B 의 "TODO" 응답은 client 로 안 감)
  → DecisionLog write: routing_reason="fallback_to_122b", fallback_used=true, target=Qwen122B
  → TraceLog write: initial_target=Qwen35B, initial_response_excerpt="TODO: implement this",
                    fallback_kind="quality", final_target=Qwen122B, final_response_excerpt="Recursion..."
  → client receives 200 + 122B JSON
```

### 3.3 검증 — 두 부분

**Client 응답 (가장 중요한 user-facing 검증):**

| 항목 | 기대값 |
|---|---|
| HTTP status | `200 OK` |
| `respBody.Contains("Recursion is a function")` | true (122B response forwarded) |
| `respBody.Contains("TODO: implement")` | **false** — 35B 의 bad response 가 client 로 안 새는 것 확인 |

**DecisionLog 한 줄:**

| 필드 | 기대값 |
|---|---|
| `target` | `"Qwen122B"` (final target; 응답을 produce 한 모델) |
| `routing_reason` | `"fallback_to_122b"` (Phase 14 신규 enum 값) |
| `fallback_used` | `true` |

**TraceLog 한 줄 (`--trace-responses` 활성 시 캡처):**

| 필드 | 기대값 |
|---|---|
| `initial_target` | `"Qwen35B"` (라우팅 결정 시점의 첫 target) |
| `initial_response_excerpt` | `"TODO"` 포함 (truncate 500 chars; 35B 의 bad response 가 trace 에는 보존 — 운영자 디버깅용) |
| `fallback_kind` | `"quality"` (vs `"availability"` for 122B-down 시나리오) |
| `final_target` | `"Qwen122B"` |
| `final_response_excerpt` | `"Recursion"` 포함 |

이 trace row 가 운영자가 prompt UID 로 grep 했을 때 보는 그 row — **35B 가 무엇을 답했고, 왜 fallback 됐고, 122B 가 무엇을 답했는지 한 row 에 다 있음**.

---

## 4. 검증 철학: "운영자가 production 에서 보는 것 그대로"

테스트가 검증하는 건 함수 시그니처가 아니라 **JSONL 파일에 무엇이 쌓였는가**. 운영자가 prod 사고 시 다음 명령으로 진단:

```bash
UID=$(echo -n "explain recursion in detail" | sha256sum | cut -c1-12)
jq "select(.prompt_uid == \"$UID\")" logs/trace/$(date +%F).jsonl
jq "select(.prompt_hash | startswith(\"$UID\"))" logs/decisions/$(date +%F).jsonl
```

이 명령으로 보는 출력 = 테스트가 `findRow + JsonDocument.Parse` 로 검증하는 것.

**즉 테스트가 통과한다 = 운영자가 그 워크플로우로 실제 사고를 진단할 수 있다는 보장.**

---

## 5. 의도된 미커버 시나리오

다음 시나리오는 **의도적으로** testCase 로 안 작성:

### 5.1 Streaming + quality fallback (`stream=true`)
- ChatCompletions 의 streaming branch 는 quality check 자체가 없음 (chunks 가 client 로 이미 송출됨; retract 불가).
- `INTENTIONALLY SKIPPED` 코드 주석 + README §5.5 명시.
- 테스트 작성 안 함 — 테스트할 코드 분기 자체가 없으므로.

### 5.2 122B unreachable + 35B bad → 35B response 그대로 forward
- 코드는 `healthProbe.IsReachable(Qwen122B) = false` 시 graceful degrade 처리.
- 테스트 안 작성 — 분명한 corner case 이지만 명시적 코드 분기로 cover; QF-02 의 `IsReachable=true` stub 이 active path 만 검증.

### 5.3 122B retry returns Error (HTTP 5xx)
- 코드는 `retryResult` 가 `Error _` 일 때 `(initialDecision, initialBody)` 로 graceful degrade.
- 테스트 안 작성 — idem.

### 5.4 `QualityFallback.Enabled = false` (kill switch)
- `isBadResponse` 가 즉시 `false` 반환 → fallback 절대 발화 안 함.
- 테스트 안 작성 — 단순 분기.

### 5.5 Trace logger 미등록 (production `Trace:Enabled=false`)
- ChatCompletions 가 `GetService<ITraceLogger>()` (null-safe) 사용 → null 시 trace 블록 skip.
- 이 시나리오는 **기존 80 개 테스트에서 implicit 하게 cover** — `RouterTests.fs` 가 ITraceLogger 등록 안 함; 그 80 개 시나리오에서 `Trace:Enabled` 없이도 통과 → null-safe 검증.
- 명시적 testCase 안 추가; `startTestRouter` 주석에 명시.

---

## 6. 실행 결과

- **테스트 카운트**: 80 → 82 passed (+2)
- **실행 시간**: 둘 다 ~ 수백 ms (in-process fake-Kestrel; 실제 mlx_lm.server 무관)
- **격리**: `testSequenced` + per-test temp dir → 병렬 race 없음
- **재현 가능성**: 외부 의존성 (ML 모델 파일, network, GPU) 0 — `dotnet test` 만으로 매번 통과

---

## 7. 코드 위치 reference

| 항목 | 파일 |
|---|---|
| 테스트 파일 | `tests/SmartRouter.Tests/QualityFallbackTests.fs` |
| `isBadResponse` | `src/SmartRouter.Cli/Adapters/QualityCheck.fs` |
| `RoutingReason.FallbackTo122B` | `src/SmartRouter.Core/Domain.fs` |
| `formatReason` 6-arm match | `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` |
| Quality fallback path (non-streaming) | `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` |
| `TraceLogger` BackgroundService | `src/SmartRouter.Cli/Adapters/TraceLogger.fs` |
| `--trace-responses` CLI flag | `src/SmartRouter.Cli/Program.fs` (`applyTraceFlagFromArgs`) |
| `Trace:Enabled` conditional triple-reg | `src/SmartRouter.Cli/CompositionRoot.fs` (`configureRequestPipeline`) |

---

## 8. distillation 디자인과의 매핑

`distillation-fallback-design-references.md` 가 식별한 7 위치 중:

| distillation 위치 | smart-router 구현 |
|---|---|
| "35B 호출 → 응답 품질 안 좋으면 → 122B 재호출" 패턴 | ✓ `ChatCompletions.fs` non-streaming branch |
| `routing_reason="fallback_to_122b"` 시그널 | ✓ `Domain.fs` 6th DU + `formatReason` |
| Trace 에 35B 응답 보존 (학습 데이터로 재사용 가능) | ✓ `TraceLogger.fs` `initial_response_excerpt` 필드 |
| 학습 신호로서의 `fallback_used=true` | ✓ DecisionLog 기존 필드 재사용 (의미 통일) |

이전까지 smart-router 의 `fallback_used=true` 는 **availability fallback (122B → 35B)** 만 가리켰음. Phase 14 가 distillation 의 의도한 quality fallback 도 같은 필드로 표현하게 통일 — `routing_reason` 값이 `fallback_to_35b` vs `fallback_to_122b` 로 방향 구분. 학습 파이프라인 (TeacherLabeler) 이 양쪽 모두 sample 로 수집 가능.

스키마 버전 stable 유지 (no bump) — `routing_reason` enum 확장만으로 backward compatible.
