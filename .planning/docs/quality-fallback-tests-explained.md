# QF-01 과 QF-02 — 쉬운 설명

**작성:** 2026-05-10
**source:** `tests/SmartRouter.Tests/QualityFallbackTests.fs`
**대상:** quality fallback 테스트가 무엇을 검증하는지 빠르게 이해하고 싶은 사람. 코드보다 의도와 시나리오를 먼저 보고 싶은 경우.
**관련:** 더 깊은 기술적 detail 은 `quality-fallback-integration-tests.md` 참조.

---

## 0. 전체 그림 — 왜 이 두 개를 테스트하는가

smart-router 의 **quality fallback** 기능은 한 줄로:

> "빠른 35B 모델한테 먼저 물어본다. 답이 시원찮으면 자동으로 똑똑한 122B 모델한테 다시 물어본다."

이 기능이 정말로 동작하는지 확인하려면 두 가지 케이스를 봐야 함:

| 케이스 | 의미 | 테스트 |
|---|---|---|
| 35B 가 **좋은** 답을 줌 | 122B 호출 안 해야 함 (속도 + 비용 절약) | **QF-01** |
| 35B 가 **나쁜** 답을 줌 | 122B 가 다시 답해야 함 (품질 보장) | **QF-02** |

---

## 1. 테스트의 무대 설정 — "가짜 LLM 두 대를 불러서 시킨다"

진짜 Qwen 35B/122B 는 GPU 가 필요하고 응답이 매번 다름 → 테스트 못 함.

대신 **가짜 LLM 서버 2대** 를 만듦:
- "이 prompt 가 오면 이 답을 줘" 라고 미리 답을 정해놓음
- 그래서 매번 **똑같은 입력에 똑같은 출력** → 검증 가능

```
                    [router (테스트 대상)]
                   /                      \
        가짜 35B 서버                  가짜 122B 서버
        (내가 미리 정한 답 줌)         (내가 미리 정한 답 줌)
```

이 가짜 서버들은 진짜 HTTP 서버임 (Kestrel) — 진짜 네트워크로 통신함. 그래서 테스트가 production 과 거의 동일.

---

## 2. QF-01 — "35B 가 잘 답한 경우"

### 시나리오를 이야기로 풀면

> 사용자: "재귀에 대해 설명해 줘"
>
> router: (35B 한테 먼저 물어봄)
>
> 가짜 35B: "재귀란 함수가 자기 자신을 더 작은 입력으로 호출하다가 base case 에서 멈추는 것."
>
> router: (이 답 괜찮네. 길이도 충분하고 "TODO" 같은 이상한 단어도 없고)
>
> router → 사용자: 35B 답 그대로 전달.
>
> **122B 는 호출 안 함.**

### 그래서 무엇을 확인하는가

테스트가 검사하는 것 3가지:

**1) 사용자가 받은 답이 35B 답인가?**

```fsharp
Expect.stringContains respBody "Recursion is a function" "35B response forwarded"
```

응답 본문에 "Recursion is a function" 이 들어있어야 함.

**2) DecisionLog 에 "fallback 안 했음" 으로 기록됐는가?**

DecisionLog 는 모든 요청을 한 줄씩 기록하는 JSONL 파일. 운영자가 `jq` 로 grep 하는 그것.

```json
{
  "target": "Qwen35B",
  "routing_reason": "ml",
  "fallback_used": false,
  ...
}
```

테스트:
```fsharp
Expect.equal (dr.GetProperty("target").GetString()) "Qwen35B"
Expect.equal (dr.GetProperty("routing_reason").GetString()) "ml"
Expect.isFalse (dr.GetProperty("fallback_used").GetBoolean())
```

**3) TraceLog 에도 "fallback 없음" 으로 기록됐는가?**

TraceLog 는 디버깅용 상세 로그 (옵션). 35B 답과 122B 답을 다 기록해서 운영자가 사후 분석 가능.

```json
{
  "initial_target": "Qwen35B",
  "final_target": "Qwen35B",
  "fallback_kind": null,
  ...
}
```

`fallback_kind: null` 이 핵심 — fallback 이 발화 안 했다는 신호.

테스트:
```fsharp
Expect.equal (tr.GetProperty("initial_target").GetString()) "Qwen35B"
Expect.equal (tr.GetProperty("final_target").GetString()) "Qwen35B"
Expect.equal (tr.GetProperty("fallback_kind").ValueKind) JsonValueKind.Null
```

### QF-01 한 줄 요약

> "35B 가 잘 답하면 router 는 122B 를 안 부르고 35B 답을 그대로 전달한다. 그리고 로그에도 그렇게 정직하게 기록된다."

---

## 3. QF-02 — "35B 가 못 답한 경우" (핵심 케이스)

### 시나리오를 이야기로 풀면

> 사용자: "재귀에 대해 자세히 설명해 줘"
>
> router: (35B 한테 먼저 물어봄)
>
> 가짜 35B: "TODO: implement this" ← 게으른 답
>
> router: (어? 이 답 이상한데.
>          - 길이가 30 자 미만이고
>          - "TODO" 라는 금지 단어도 들어있고
>          - → 이건 못 쓰겠다)
>
> router: (122B 가 살아있는지 확인) → OK
>
> router: (122B 한테 같은 질문 다시 함)
>
> 가짜 122B: "재귀란 함수가 자기 자신을 호출하며 base case 에서 종료되는 것."
>
> router → 사용자: **122B 답** 전달.
>
> **사용자는 35B 의 "TODO" 답을 절대 못 봄.**

### 그래서 무엇을 확인하는가

테스트가 검사하는 것 5가지:

**1) 사용자가 받은 답이 122B 답인가? (가장 중요)**

```fsharp
Expect.stringContains respBody "Recursion is a function" "122B (good) response forwarded"
Expect.isFalse (respBody.Contains("TODO: implement")) "35B's bad response NOT forwarded"
```

이게 핵심. 사용자한테는 절대로 "TODO" 답이 새어나가면 안 됨.

**2) DecisionLog 에 "fallback 했음 + 122B 가 최종" 으로 기록됐는가?**

```json
{
  "target": "Qwen122B",
  "routing_reason": "fallback_to_122b",
  "fallback_used": true,
  ...
}
```

특히 `routing_reason: "fallback_to_122b"` — Phase 14 에서 **새로 추가된 값**. 기존에는 `fallback_to_35b` (122B 다운 → 35B 로 도망) 만 있었음.

```fsharp
Expect.equal (dr.GetProperty("target").GetString()) "Qwen122B"
Expect.equal (dr.GetProperty("routing_reason").GetString()) "fallback_to_122b"
Expect.isTrue (dr.GetProperty("fallback_used").GetBoolean())
```

**3) TraceLog 에 "처음엔 35B → 결국 122B" 가 둘 다 기록됐는가?**

```json
{
  "initial_target": "Qwen35B",
  "initial_response_excerpt": "TODO: implement this",
  "fallback_kind": "quality",
  "final_target": "Qwen122B",
  "final_response_excerpt": "Recursion is a function...",
  ...
}
```

이 한 줄에 **사고의 전말** 이 다 들어있음:
- "35B 한테 먼저 갔는데 (`initial_target`)"
- "이런 답을 받았고 (`initial_response_excerpt`)"
- "품질 문제로 fallback 했고 (`fallback_kind: "quality"`)"
- "결국 122B 가 (`final_target`)"
- "이런 답을 줬다 (`final_response_excerpt`)"

```fsharp
Expect.equal (tr.GetProperty("initial_target").GetString()) "Qwen35B"
Expect.equal (tr.GetProperty("final_target").GetString()) "Qwen122B"
Expect.equal (tr.GetProperty("fallback_kind").GetString()) "quality"
Expect.stringContains initialExcerpt "TODO"
Expect.stringContains finalExcerpt "Recursion"
```

### QF-02 한 줄 요약

> "35B 답이 나쁘면 router 는 자동으로 122B 를 다시 부른다. 사용자는 122B 답만 본다. 그리고 양쪽 답이 모두 trace 로그에 보존된다 (사후 분석/학습용)."

---

## 4. 두 테스트의 대비 — 표 한 장으로

| 항목 | QF-01 (good) | QF-02 (bad → fallback) |
|---|---|---|
| **35B 답** | "Recursion is a function..." (90 자, 정상) | "TODO: implement this" (20 자, 금지단어) |
| **122B 호출됨?** | ❌ 안 함 | ✅ 함 |
| **사용자가 받는 답** | 35B 답 | **122B 답** (35B 의 TODO 는 못 봄) |
| **DecisionLog `target`** | `Qwen35B` | `Qwen122B` |
| **DecisionLog `routing_reason`** | `ml` | `fallback_to_122b` ← 신규 |
| **DecisionLog `fallback_used`** | `false` | `true` |
| **TraceLog `initial_target`** | `Qwen35B` | `Qwen35B` |
| **TraceLog `final_target`** | `Qwen35B` | `Qwen122B` ← 다름 |
| **TraceLog `fallback_kind`** | `null` | `"quality"` |
| **TraceLog `initial_response_excerpt`** | (없음) | `"TODO: implement..."` ← 35B 답 보존 |

---

## 5. 왜 "JSONL 파일을 직접 읽어서" 검증하는가

### 일반적인 단위 테스트라면

```fsharp
// 함수 호출하고 반환값 확인
let result = router.Route(request)
Expect.equal result.Target Qwen122B
```

### 이 테스트는 그렇게 안 함. 대신:

1. 실제 router 를 in-process 로 띄움
2. 실제 HTTP 요청 보냄
3. 응답 받음
4. **로그 파일을 열어서** JSON 한 줄을 파싱
5. 그 JSON 의 필드 값을 검증

### 왜?

운영자가 **production 사고를 진단하는 방식과 똑같이** 테스트하기 위해.

운영 중 사용자가 "내 답이 이상해요" 라고 신고하면 운영자가 하는 행동:

```bash
# prompt 의 UID 계산
UID=$(echo -n "내 질문" | sha256sum | cut -c1-12)

# trace 로그에서 UID 매칭되는 줄 찾음
jq "select(.prompt_uid == \"$UID\")" logs/trace/$(date +%F).jsonl

# decision 로그에서도 찾음
jq "select(.prompt_hash | startswith(\"$UID\"))" logs/decisions/$(date +%F).jsonl
```

테스트가 하는 일은 **이 명령들의 F# 버전**:

```fsharp
let uid = computePromptUid promptText  // 같은 SHA-256 첫 12hex
let traceRow = findRow tracePath (fun e ->
    e.GetProperty("prompt_uid").GetString() = uid)
```

**즉:**
> "테스트가 통과한다" = "운영자가 사고 시 UID 로 로그 grep 해서 진단할 수 있다" 가 보장됨.

기능 자체뿐 아니라 **운영 워크플로우** 까지 같이 테스트하는 셈.

---

## 6. 테스트가 실제로 발견한 버그들

이 테스트를 작성하면서 실제로 잡힌 버그 2개:

**버그 1 — fake 서버가 `/v1/models` probe 에 응답 못 함**
- router 가 lazy 하게 한 번 `/v1/models` 를 호출해서 모델 ID 를 캐시함
- 초기 가짜 서버는 모든 path 에 chat 응답 줌 → `/v1/models` 응답 파싱 실패 → 502
- 수정: path 보고 분기 (`/v1/models` 면 모델 리스트, 나머지는 chat 응답)

**버그 2 — IHealthProbe 시그니처 안 맞음**
- `IsReachableAsync` 가 curried 함수 (`target -> ct -> Task<bool>`)
- 처음에 tupled 로 작성 (`(target, ct)`) → F# 컴파일 에러
- 수정: `IsReachableAsync(_target) _ct = Task.FromResult(true)`

테스트가 없었으면 production 에서 quality fallback 첫 발화 때 둘 다 터졌을 것.

---

## 7. 한 문단으로 정리

> QF-01 은 "정상 케이스" 를 검증한다 — 35B 가 잘 답하면 122B 안 부르고 그대로 전달, 로그도 정직. QF-02 는 "fallback 케이스" 를 검증한다 — 35B 가 "TODO" 같은 게으른 답을 주면 자동으로 122B 가 다시 답하고 사용자는 122B 답만 본다. 두 케이스 모두 **로그 파일을 직접 읽어서** 검증하므로, 테스트가 통과한다 = 운영자가 사고 시 `jq` 로 진단할 수 있다는 뜻이다. 이 두 테스트로 distillation 디자인의 "Failure = Gold Data" 패턴이 production 에서 정말로 동작하고 흔적도 남긴다는 것이 보증된다.
