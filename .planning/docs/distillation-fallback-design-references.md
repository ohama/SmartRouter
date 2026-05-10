# `smart-router-distillation` 의 "35B → 122B Fallback" 디자인 — 위치 인벤토리 + smart-router 실제 구현과의 gap

**작성:** 2026-05-10
**source:** `~/projs/smart-router-distillation/`
**대상:** smart-router 가 distillation 디자인의 어떤 부분을 가져왔고 어떤 부분은 의도와 달리 구현했는지 추적하려는 사람.

## 결론 (TL;DR)

`smart-router-distillation` 의 **7 위치** 에 "35B 호출 → 응답 품질 안 좋으면 → 122B 재호출" 의 fallback 패턴이 명시돼 있다. 이 패턴은 distillation 시스템의 **핵심 학습 신호** ("Failure = Gold Data") 다.

그러나 smart-router 의 실제 구현에는 이 quality-check-based fallback 이 **없다.** 대신 **반대 방향** 의 health-probe-based fallback (122B unreachable → 35B reroute) 만 있다 (Phase 10 REL-01..04). 두 fallback 의 의미와 학습 신호가 완전히 다른데, smart-router 는 distillation 의 `fallback_used` 필드 이름은 채택하면서 의미는 다르게 동작한다.

이 문서가 그 gap 을 고증.

---

## 1. distillation 의 fallback 디자인 — 7 위치

### 1.1 `idea/00_overview.md`

```
Goal:
- Use Claude Code as teacher
- Build distilled classifier
- Route between Qwen 35B and 122B
```

(직접 fallback 언급은 없지만 시스템 목적의 토대.)

### 1.2 `idea/01_architecture.md` (16:)

```
Pipeline:
[Prompt] → [Heuristic Layer] → [Distilled Classifier] → [Router Decision] → 35B or 122B

Fallback:
- If 35B fails → retry 122B
```

이게 처음 등장하는 위치. heuristic + classifier 위에 quality-check fallback 이 얹힌 구조.

### 1.3 `idea/07_routere_integration.md` (16-19:)

```
Pipeline:
1. Heuristic check
2. Embedding classifier
3. Route decision

Example:
if tokens < 200 and no code:
    → 35B
else:
    → classifier

Fallback:
if 35B output bad:
    → retry 122B
```

같은 패턴의 재진술 — distillation 디자인의 일관된 모티프.

### 1.4 `idea/auto-retraining-pipeline.txt` (37-73:)

저장 포맷에 `fallback_used: true` 필드가 명시:

```
{
  "prompt": "...",
  "route": "35B",
  "response": "...",
  "latency_ms": 320,
  "retry": true,
  "fallback_used": true,           ← 이 필드가 핵심 학습 신호
  "error": false,
  "user_feedback": null
}
```

그리고 Failure Detection 의 첫 번째 조건이 fallback:

```
실패로 간주할 조건
1) fallback 발생
35B → 실패 → 122B 재요청
👉 가장 중요한 signal

F# pseudo
let isFailure log =
    log.fallback_used ||
    log.error ||
    log.response.Contains("TODO") ||
    log.response.Length < 20
```

### 1.5 `idea/auto-retraining-code.md` (88-107:) — F# 의사코드

```fsharp
type Router(embed, classifier, llm, config) =
    member _.Route(prompt: string) =
        let embedding = embed.Embed(prompt)
        let score = classifier.Predict(embedding)
        let route =
            if score > config.Threshold then Qwen122B
            else Qwen35B

        let response =
            match route with
            | Qwen35B  -> llm.Call35B(prompt)
            | Qwen122B -> llm.Call122B(prompt)

        // ★ 핵심: 35B 응답 품질 체크 후 122B 재호출
        let fallbackUsed, finalResponse =
            if route = Qwen35B && isBadResponse response then
                true, llm.Call122B(prompt)
            else
                false, response

        { Prompt = prompt
          Route = route                  // 첫 결정만 기록 (fallback 발생해도 35B)
          Response = finalResponse       // 최종 응답 (fallback 시 122B 의 것)
          FallbackUsed = fallbackUsed
          ... }

let isBadResponse (resp: string) =
    resp.Contains("TODO") ||
    resp.Length < 30 ||
    resp.Contains("I think")
```

**디자인 의도가 가장 직접적으로 드러나는 위치.** 응답 후 quality heuristic 으로 retry 판단, retry 시 `fallback_used=true` 표시. 이 row 가 retraining 입력으로 흘러들어가 분류기 boundary 를 개선.

### 1.6 `documentation/auto-retraining-research.md` §1.3 (32:)

13단계 전체 흐름의 한 단계로 명시:

```
[User Prompt]
    → Heuristic Filter
    → Embedding (bge/e5)
    → Logistic Classifier
    → Route Decision (35B | 122B)
    → LLM Call
    → (35B fail 시) Fallback to 122B          ← 이 단계
    → JSONL Logging
    → Failure Detection
    → Hard-Case Dataset
    → Teacher Re-labeling
    → Retraining
    → Validation
    → Canary Deploy
    → Hot-swap classifier
```

§1.5 의 핵심 통찰:

> **"Failure = Gold Data"**
>
> random 데이터보다 fallback 발생한 hard case 가 분류기 boundary 를 가장 많이 개선시킨다. drift 도 자동 대응됨.

§1.4 의 컴포넌트 #4:
- Failure Detector: `fallback ∨ error ∨ short-response ∨ "TODO"/"I think"`

### 1.7 `documentation/howto/design-two-loop-router.md` (75-:)

"두 개의 독립된 루프" 디자인 패턴 howto. fallback 로그가 학습 데이터로 자동 변환되는 사상 기록:

```fsharp
let fallbackUsed, finalResponse =
    if initialRoute = Small && isBadResponse response then
        true, llm.CallLarge(prompt)
    else
        false, response
```

### 1.8 `documentation/handoff-to-smart-router.md` §1 (33:)

distillation → smart-router 핸드오프 문서. 핵심 차별점을 다음과 같이 설명:

> 핵심 차별점: **"실패를 먹고 성장하는 라우터"** — fallback 로그를 학습 데이터로 재활용

이 문서가 smart-router Phase 4-9 의 source. fallback 구조를 그대로 가져갈 의도였음을 보여줌.

---

## 2. smart-router 의 실제 구현 — 무엇이 다른가

| 항목 | distillation 디자인 (의도) | smart-router 구현 (현재) |
|---|---|---|
| Routing 결정 횟수 | 요청당 1번 (decision time) + 1번 (post-response quality check) | 요청당 **1 번만** (decision time) |
| Quality check 의 존재 | `isBadResponse` 함수 (TODO / 길이 < 30 / "I think") | **없음** — 35B 응답을 그대로 forward |
| Fallback 트리거 | 35B response 가 quality 기준 미달 | 122B 가 health probe 미통과 (반대 방향) |
| Fallback 방향 | 35B → 122B (escalation) | **122B → 35B** (degradation) |
| `fallback_used=true` 의 의미 | "응답 품질이 나빠서 122B 가 다시 처리함" | "122B 가 죽어서 35B 가 대신 처리함" |
| 학습 신호로의 가치 | hard case 발견 (분류기가 35B 라고 잘못 판단한 prompt) | infrastructure 신호 (122B 가용성) — 분류기 학습엔 noise |
| Teacher labeler 가 담당하는 hard case | 진짜 분류기 errror 한 prompt | 122B downtime 동안 들어온 임의 prompt — 진짜 hard 인지 모름 |

### 2.1 코드 위치 — smart-router 의 fallback (반대 방향)

```fsharp
// src/SmartRouter.Cli/Adapters/QueueDispatcher.fs:243-253
let decision =   // shadows the parameter
    if decision.Target = Qwen122B
       && not (healthProbe.IsReachable(Qwen122B))
       && not isGraphIndexing then
        logger.LogWarning(
            "QueueDispatcher.CompleteAsync: 122B unreachable; rerouting task={Task} to 35B (fallback)",
            req.Task)
        { decision with
            Target     = Qwen35B
            Reason     = FallbackTo35B
            IsFallback = true }
    else
        decision

// src/SmartRouter.Cli/Endpoints/ChatCompletions.fs:264-269 — 같은 로직 (HTTP entry)
```

이 분기가 발화하는 조건: `decision.Target = Qwen122B` AND `IHealthProbe.IsReachable(Qwen122B) = false`. 즉 **분류기는 122B 라고 결정했고**, 실제로는 122B 가 unreachable. 35B 로 demote.

distillation 의 디자인은 이 분기와 **반대**:
- 분류기가 35B 라고 결정했고, 35B 가 응답을 줬는데, 그 응답 품질이 나쁘다면 122B 로 escalate.

smart-router 에는 이 로직이 0줄.

### 2.2 검색으로 확인

```bash
cd ~/projs/smart-router
grep -rn "isBadResponse\|FallbackTo122B\|35B.*fail.*122B" src/
# → 0 hits
```

---

## 3. 왜 gap 이 있는가 — 추측

`handoff-to-smart-router.md` §0 ("한눈에 보는 결론") 은 통합 시점에 "Phase 1~2 완료, Phase 3 진행 중" 이라고 명시 — 그 시점에 distillation 의 fallback 디자인이 이미 있었다. 하지만 smart-router 의 PROJECT.md 에는 quality-check-based fallback 이 명시적 요구사항으로 들어가지 않았고, 대신 Phase 10 의 REL-01..04 가 **infrastructure-level fallback** (122B downtime 처리) 으로 채워졌다.

다시 말해 같은 단어 "fallback" 이 두 시스템에서 다른 개념을 가리킨다:
- distillation: **품질-기반** quality fallback — "응답 보고 결정"
- smart-router: **가용성-기반** availability fallback — "헬스 보고 결정"

`fallback_used` 필드 이름이 양쪽에서 동일해서 외형적으로는 같은 시스템처럼 보이지만, 그 값이 `true` 가 되는 조건이 본질적으로 다르다. Loop B (Phase 7-8) 의 retraining 입력이 distillation 디자인 의도와 정확히 일치하지 않는다.

---

## 4. 만약 quality-check fallback 을 추가한다면 — 위치 및 부담

### 4.1 implementation 위치

`src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` 의 non-streaming 분기 (`OkResponse` 처리 직후):

```fsharp
// AFTER upstream.CompleteAsync returns Ok body
if decision.Target = Qwen35B && isBadResponse body then
    logger.LogInformation(
        "ChatCompletions: 35B response failed quality check; retrying on 122B; cid={Cid}",
        correlationId)
    let retryDecision = { decision with Target = Qwen122B; Reason = FallbackTo122B; IsFallback = true }
    let! retryResult = upstream.CompleteAsync req retryDecision ctx.RequestAborted
    ...
```

streaming 분기는 더 어렵다 — chunk 가 이미 client 로 흘러나간 후 quality 판단 → retry 시 client 가 두 응답을 받음. 보통 streaming 시 quality fallback 은 disable 하거나 streaming 종료 후 다음 request 부터 적용.

### 4.2 새로 필요한 것

- `RoutingReason.FallbackTo122B` DU case 추가 (Domain.fs)
- `isBadResponse` 함수 (간단한 heuristic) — 또는 Claude API 로 quality scoring (heavy)
- DecisionLog 의 `routing_reason` 에 `fallback_to_122b` 추가
- Phase 7 FailureDetector 가 `fallback_used=true` 의 의미를 distinguish: availability 인지 quality 인지 — 추가 필드 (`fallback_kind: "availability" | "quality"`) 필요
- Phase 8 RetrainingService 가 quality-fallback row 만 hard case 로 채택하도록 분기 추가

이건 substantial 한 phase 단위 작업. 현재 v1 milestone 에는 안 들어 있음. 향후 Phase 14+ candidate.

### 4.3 가치 평가

장점:
- distillation 디자인 의도와 일치 — Loop B 가 진짜 hard case 를 학습
- 분류기 boundary 가 진짜로 잘못 판정한 prompt 를 자동 발견

단점:
- streaming 응답에 적용 어려움
- 응답 latency 가 35B + 122B 합산 (worst case)
- `isBadResponse` heuristic 이 false positive 시 122B 호출 낭비
- "응답 품질" 자체가 정량화 어려움 (TODO / 길이 / "I think" 류는 toy 수준)

---

## 5. 운영자가 지금 할 수 있는 것

distillation 디자인의 quality fallback 을 implement 하지 않은 채로:

1. **infrastructure fallback 만 의식**: `fallback_used=true` 는 "122B downtime 동안 들어온 prompt" 일 뿐. 학습 신호로는 약함.
2. **수동 hard case 추가**: 운영자가 수동으로 prompt 의 응답 품질을 보고 hard case 를 `datasets/hard-cases.jsonl` 에 직접 append (위에 sed/jq 로) — Loop B 의 다음 retrain 이 그 row 들을 채택.
3. **Claude API 를 teacher 로 사용**: TeacherLabeler 의 `Endpoint` 를 122B 대신 Claude 로 향하게 (`appsettings.json:TeacherLabeler.Endpoint`) — Claude 가 prompt 의 진짜 정답 (35B 가능 여부) 을 라벨. 비용 측면에서 cost cap 필수.

---

## 6. 위치 요약 표

| 파일 | 라인 | 내용 | 종류 |
|---|---|---|---|
| `~/projs/smart-router-distillation/idea/00_overview.md` | — | "Use Claude Code as teacher; route between 35B and 122B" | 시스템 목적 |
| `~/projs/smart-router-distillation/idea/01_architecture.md` | 15-16 | "Fallback: If 35B fails → retry 122B" | 디자인 |
| `~/projs/smart-router-distillation/idea/07_routere_integration.md` | 16-19 | "if 35B output bad → retry 122B" | 디자인 |
| `~/projs/smart-router-distillation/idea/auto-retraining-pipeline.txt` | 37-73 | `fallback_used` 필드 명세 + isFailure 의사코드 | 데이터 흐름 |
| `~/projs/smart-router-distillation/idea/auto-retraining-code.md` | 88-107 | F# Router type 의 fallbackUsed 분기 + isBadResponse | 의사코드 |
| `~/projs/smart-router-distillation/documentation/auto-retraining-research.md` | 21, 32, 51, 68 | 13-단계 흐름 + "Failure = Gold Data" 통찰 | 연구 문서 |
| `~/projs/smart-router-distillation/documentation/howto/design-two-loop-router.md` | 75 | F# 의사코드 (위와 같은 패턴) | howto |
| `~/projs/smart-router-distillation/documentation/handoff-to-smart-router.md` | 33 | "실패를 먹고 성장하는 라우터" 핵심 차별점 | 핸드오프 |

---

## 7. 관련 문서

- `.planning/docs/cold-start-request-flow.md` — smart-router 의 실제 request flow + 두 시나리오 + 사용자 멘탈 모델 정정
- `.planning/docs/ml-trained-vs-untrained-files.md` — 학습 전/후 어떤 파일이 다른가
- `~/projs/smart-router-distillation/documentation/handoff-to-smart-router.md` — 통합 시점의 의도 (Phase 4-9 ML arc 의 source)
