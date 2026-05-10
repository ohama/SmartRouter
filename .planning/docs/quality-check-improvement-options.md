# 35B 응답 품질 판정 알고리즘 — 개선 방안 분석

**작성:** 2026-05-10
**source:** `src/SmartRouter.Cli/Adapters/QualityCheck.fs` (`isBadResponse` heuristic)
**대상:** quality fallback 의 false positive / false negative 를 줄이고 싶거나, distillation 디자인이 의도한 "self-improving router" 단계로 진화시키고 싶은 사람.
**관련:** `quality-fallback-integration-tests.md`, `distillation-fallback-design-references.md`

> **Status (2026-05-10):** Tier 1 (sub-tiers 1-A finish_reason, 1-B case-insensitive keywords, 1-D Korean length correction) and Tier 2 sub-tier 2-A (Shannon entropy) implemented in Phase 15 — see `.planning/phases/15-quality-signal-enrichment/`. Tier 1-C (refusal-pattern default expansion) was DEFERRED — operator opt-in via `BadKeywords` is the chosen path (see `.planning/phases/15-quality-signal-enrichment/15-CONTEXT.md` § "Refusal pattern default 정책"). Tier 2-B (prompt-relative length) and Tier 2-C (logprob threshold) remain candidates for Phase 16+. Tier 3 (122B-as-judge) and Tier 4 (QualityClassifier ML model) are also Phase 16+.

---

## 결론 (TL;DR)

현재 `isBadResponse` 는 (1) 길이 미만 (2) BadKeyword 포함 — 이 두 가지 heuristic 만 본다. 이 알고리즘은 모델이 이미 알려주는 강력한 신호 (`finish_reason`, logprob, token 수) 를 무시하고, prompt context 를 안 보고, closed-loop learning 도 없다. 결과적으로 false positive (정상 답을 bad 로 오판) + false negative (반복 루프, 잘린 응답, refusal 패턴 못 잡음) 가 둘 다 발생.

개선은 4 tier 로 나뉨:
- **Tier 1** (1 일, 거의 무리스크): `finish_reason` 활용 + case-insensitive + refusal 키워드 + 한글 길이 보정
- **Tier 2** (2-3 일): Shannon entropy 반복 감지 + prompt-relative length + logprob threshold
- **Tier 3** (3-5 일): 122B-as-judge (1-token verification on borderline cases)
- **Tier 4** (1-2 phase): 별도 `QualityClassifier` ML.NET 모델 — Loop B 와 동일 인프라 재사용; distillation 디자인의 자연스러운 endpoint

가장 ROI 높은 단일 변경: **`finish_reason` 와이어링** — mlx_lm 이 이미 보내주는 가장 강력한 "잘린 응답" 신호인데 무시되고 있음.

---

## 1. 현재 알고리즘의 실패 모드

```fsharp
// QualityCheck.fs:27-37
let isBadResponse (opts: QualityFallbackOptions) (response: string) : bool =
    if not opts.Enabled then false
    elif response.Length < opts.MinResponseLength then true       // 길이만 봄
    elif obj.ReferenceEquals(opts.BadKeywords, null) then false
    else opts.BadKeywords |> Array.exists (fun kw ->
        not (String.IsNullOrEmpty(kw)) && response.Contains(kw))   // case-sensitive
```

### 1.1 잘못 판정할 시나리오

| 유형 | 예시 | 현재 결과 | 정답 |
|---|---|---|---|
| **False positive** (잘못 fallback 발화) | "Hi!" (3 chars) — 인사 응답 | bad | good |
| | "42" — "6×7?" 답 | bad | good |
| | "I think recursion is..." (정상 답에 "I think" 우연 포함) | bad | good |
| | "안녕하세요. 잘 지내고 있어요." (한글 28 chars but rich content) | bad | good |
| | 사용자가 "TODO 주석을 어떻게 다나요?" 물어 답에 "TODO" 등장 | bad | good |
| **False negative** (bad 응답 못 잡음) | "I cannot help with this." (32 chars, no badword) | good | **bad** |
| | "the the the the the the the..." (반복 루프, 100+ chars) | good | **bad** |
| | `finish_reason="length"` 로 잘린 응답 | good | **bad** |
| | 영어로 답해야 하는데 한국어로 답함 (off-topic 동등) | good | **bad** |
| | Markdown 헤더만 있고 본문 없음: `# Answer\n\n## Steps\n` | good | **bad** |
| **Brittle** | "todo", "Todo", "ToDo" — 대소문자 변형마다 추가 필요 | — | — |

### 1.2 핵심 한계

1. **모델이 이미 알려주는 신호 무시** — `finish_reason`, logprob, token 수 모두 응답에 들어있는데 안 봄.
2. **Prompt context 없음** — "Hello" 와 "Implement quicksort" 에 같은 길이 기준 적용.
3. **Closed-loop learning 없음** — operator 가 BadKeywords 를 trial-and-error 로 큐레이션해야 함.
4. **Loop B 의 라벨링 신호가 quality check 자체로 환류 안 됨** — distillation 디자인의 미완성 부분. 122B teacher 가 이미 hard case 를 라벨링하고 있는데 그 신호가 `isBadResponse` 자체를 개선하는 데 안 쓰임.

---

## 2. 개선 방향 — Tier 별 ROI vs 복잡도

### Tier 1 — 즉시 적용, 낮은 risk (1 일)

#### 1-A. `finish_reason` 활용 (가장 큰 single win)

mlx_lm 응답에 이미 들어있음:
- `"stop"` — 정상 종료
- `"length"` — `max_tokens` 도달 → **잘림 = bad signal 강력**
- `"content_filter"` — 검열 → bad

추가할 config:
```jsonc
"Routing": {
  "QualityFallback": {
    "BadFinishReasons": ["length", "content_filter"]
  }
}
```

JSON 파싱이 `QualityCheck.fs` 에 들어가야 함 (현재는 pure string). 시그니처 변경:
```fsharp
let isBadResponse (opts: QualityFallbackOptions) (finishReason: string option) (response: string) : bool
```

#### 1-B. Case-insensitive 키워드 매칭

```fsharp
response.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0
```

operator 가 `"TODO"` 한 번 추가하면 모든 변형 cover. 약간의 false positive 위험 (e.g., `method.toDo`) 이지만 chat completion 에서는 거의 없음.

#### 1-C. Default keyword 확장 (refusal 패턴)

```jsonc
"BadKeywords": [
  "TODO", "I think",
  "I cannot", "I'm unable", "I don't have access",
  "As an AI", "Sorry, I can't"
]
```

35B 의 흔한 refusal 시나리오 cover. `"As an AI"` 는 user 가 AI 안 물었을 때만 false positive 위험 (드물음).

#### 1-D. 한글 응답 길이 보정

DecisionLog 에 이미 있는 `prompt_korean_char_ratio` 와 같은 응답 측 ratio 계산. 한글/한자 비율 높으면 효과적 길이 = `length × 1.8` (rough heuristic).

```fsharp
let koreanRatio (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let kor = s |> Seq.filter (fun c -> c >= '가' && c <= '힣') |> Seq.length
        float kor / float s.Length

let effectiveLength (s: string) : int =
    let r = koreanRatio s
    int (float s.Length * (1.0 + r * 0.8))
```

또는: `MinResponseLength` 를 한글 ratio 비례로 동적 낮춤.

---

### Tier 2 — 중간 복잡도 (2-3 일)

#### 2-A. 반복 감지 (Shannon entropy)

```fsharp
let charEntropy (s: string) : float =
    if s.Length = 0 then 0.0
    else
        s |> Seq.countBy id
          |> Seq.map snd
          |> Seq.map (fun c ->
              let p = float c / float s.Length
              -p * Math.Log2(p))
          |> Seq.sum

// entropy < 2.5 → "the the the..." 류 token 루프
```

한글/영어 양쪽에서 동작. 정상 텍스트는 보통 entropy 4-5+, 반복 루프는 1-2.

#### 2-B. Prompt-relative length

```fsharp
let suspicious =
    response.Length < (prompt.Length / 10)
    && prompt.Length > 200
```

긴 질문에 짧은 답 = 의심. 짧은 질문 ("Hello") 은 짧은 답 OK.

#### 2-C. Logprob 기반 confidence

mlx_lm 호출 시 `logprobs: true` 추가 → 응답에 per-token logprob.

```fsharp
let avgLogprob (logprobs: float array) : float =
    if logprobs.Length = 0 then 0.0
    else Array.average logprobs

// avgLogprob < -2.5 → 모델이 자신 없음 → bad
```

Trade-off:
- 응답 size 증가
- 약간의 latency
- mlx_lm 의 logprob 지원 여부 사전 확인 필요 (대부분 OpenAI-compat 서버는 지원)

---

### Tier 3 — 큰 구조 변경, 높은 가치 (3-5 일)

> **Phase 16 implemented Tier 3-A** (2026-05-10) — 122B-as-judge for borderline cases.
> See `.planning/phases/16-122b-as-judge-for-borderline-cases/` for plans + summary.
> Tier 3-B (judge result self-distillation as Loop B input) is candidate for Phase 17.

#### 3-A. 122B-as-judge (lazy verification)

borderline 케이스 (Tier 1+2 통과했지만 confidence 낮음) 만 **별도의 짧은 prompt 로 122B 에 물음**:

```
Question: <user's prompt>
Response: <35B's response>
Is this response correct and helpful? Answer YES or NO.
```

1-token 응답 → 거의 무료. **명백한 bad/good 은 122B 안 부르고 fast path** 로 처리.

```
[35B 답 받음]
    ↓
[Tier 1+2 heuristic]
    ├─ 명백히 bad → 122B 재시도 (현재 quality fallback)
    ├─ 명백히 good → 35B 답 forward
    └─ borderline → 122B-as-judge 호출 (1 token)
                        ├─ NO → 122B 재시도
                        └─ YES → 35B 답 forward
```

**Trade-off:**
- 추가 122B 호출이지만 1 token 만 → 보통의 122B 호출보다 ~50× 빠름
- Cache by `prompt_hash + response_hash` → 같은 응답에 대한 verdict 재사용
- 명백한 케이스는 비용 0

#### 3-B. Embedding similarity (off-topic 탐지)

bge-m3 embedder 가 이미 loaded (router classifier 용). prompt embedding 과 response embedding 의 cosine similarity → off-topic 감지.

```fsharp
let sim = cosineSim (embedder.Embed prompt) (embedder.Embed response)
if sim < 0.3 then bad  // off-topic
```

품질 ≠ 유사도이지만 **언어 mismatch** ("한국어 질문 → 영어 답") 같은 명백한 실패 잡음.

Trade-off: 응답마다 추가 embed 호출 (~100ms). 1+2 통과한 borderline 케이스에만 적용하면 비용 제한적.

---

### Tier 4 — Closed-loop self-improving (distillation 디자인의 endgame; 1-2 phase)

#### 4. `QualityClassifier` — 별도의 학습 분류기

distillation 디자인이 의도했지만 아직 미구현인 부분.

**데이터 소스 (이미 다 있음):**

- `logs/trace/*.jsonl` — `(prompt, initial_response_excerpt, fallback_kind, final_response_excerpt)` rows
- `logs/decisions/*.jsonl` — `routing_reason`, `fallback_used`

**라벨 추출 로직:**

| 신호 | 라벨 |
|---|---|
| `fallback_kind="quality"` AND `final != initial` (122B 재시도가 의미있는 차이를 만듦) | 35B response = **bad** (positive sample) |
| `fallback_kind=null` AND 사용자 컴플레인 없음 | 35B response = **good** (negative sample; 노이즈 있음) |
| TeacherLabeler 가 `ROUTE_122B` 라벨링 한 케이스 | 35B response = **bad** (강한 신호) |
| TeacherLabeler 가 `ROUTE_35B` 라벨링 한 케이스 | 35B response = **good** |

**TeacherLabeler 재활용:** Loop B 가 이미 hard case 를 122B 로 라벨링하고 있음. **같은 라벨링 결과를 quality classifier 학습 데이터로 분기** — 새 인프라 거의 안 만들어도 됨.

**모델:**

```
Input  : bge-m3 embed(prompt) ⊕ bge-m3 embed(response)   // 2048-dim
Output : binary good/bad
Stack  : ML.NET LbfgsLogisticRegression (router classifier 와 동일)
Path   : models/quality-classifier.zip
```

**API 변경:**

```fsharp
// QualityCheck.fs 가 호출하는 인터페이스만 바뀜
type IQualityClassifier =
    abstract member IsBad : prompt: string * response: string -> bool

// 기본 구현은 Tier 1+2 heuristic; Tier 4 활성화 시 ML 구현으로 교체
let isBadResponse (classifier: IQualityClassifier) (prompt: string) (response: string) =
    classifier.IsBad(prompt, response)
```

**효과:**

- BadKeywords 같은 manual 큐레이션 사라짐
- 새로운 35B 실패 패턴 (다음 모델 버전이 이상하게 답하기 시작) 자동 학습
- distillation 의 "Failure = Gold Data" 패턴이 한 단계 더 깊어짐 — fallback 자체가 학습 신호로 환류

**Trade-off:**

- 별도 retraining pipeline 필요 (router classifier 와 분리; 같은 인프라 재사용 가능)
- 응답마다 embedding 호출 (1 회) — bge-m3 는 이미 로드돼 있어서 추가 메모리 0
- 초기 부트스트랩 — 충분한 trace 데이터 (~수천 row) 쌓일 때까지 Tier 1+2 heuristic 으로 fallback
- 카나리 패턴 적용 가능 — `models/quality-classifier-canary.zip` 으로 새 모델 검증

---

## 3. 추천

### 권장 경로 (incremental)

| Phase | 범위 | 시간 | 효과 |
|---|---|---|---|
| **Phase 15a** | Tier 1-A (`finish_reason`) + 1-B (case-insensitive) + 1-C (refusal keywords) + 2-A (entropy) | 1-2 일 | False negative 절반 이상 cover; 코드 변경 < 200 줄 |
| **Phase 15b** (선택) | Tier 3-A (122B-as-judge) | 3-5 일 | 거의 모든 false 잡음; 1-token 호출이라 비용 낮음 |
| **Phase 16** (큰 약속) | Tier 4 (`QualityClassifier`) | 1-2 phase | distillation 디자인 완성; self-improving 라우터 |

### 핵심 trade-off

| 경로 | 코드 변경 | 효과 | 리스크 |
|---|---|---|---|
| Tier 1+2 | < 200 줄 | False negative 큰 폭 감소; false positive 약간 감소 | 거의 없음 (heuristic 이라 검증 쉬움) |
| Tier 3-A (judge) | 1 새 adapter | 거의 모든 false 잡음 | 122B 1-token 호출 비용; cache 미스 시 latency |
| Tier 4 (classifier) | 새 classifier 파이프라인 | distillation 디자인 완성; 자동 학습 | bootstrap 데이터 필요; 별도 retraining 운영 |

### 가장 쓸모 있는 한 가지

**Tier 1-A — `finish_reason` 와이어링.**

mlx_lm 이 이미 보내주는 가장 강력한 bad signal 인데 현재 무시되고 있음. 실패 모드 절반 정도가 잘린 응답인 것을 고려하면 ROI 가 가장 높음. 시그니처 변경만 하면 되고 (`finishReason: string option` 추가), 외부 의존성 0, 추가 latency 0.

---

## 4. 구현 시 주의점

### 4.1 시그니처 변경 (`QualityCheck.fs`)

현재 pure string-only. Tier 1-A 가면 `finish_reason` 도 받아야 하므로 호출자 (`ChatCompletions.fs` non-streaming branch) 수정 필요. JSON 파싱 비용은 미미.

### 4.2 schema_version

Tier 4 에서 새로운 `routing_reason` 값 추가될 가능성 (예: `"quality_judge_passed"`). 추가만 하면 backward compatible — schema bump 불필요. **이름만 추가**, 의미 변경하지 말 것.

### 4.3 트레이스 로그 확장

Tier 3-A (judge) 시 trace 에 새 필드 추가 권장:
- `judge_called: bool`
- `judge_verdict: "yes" | "no" | null`
- `judge_latency_ms: float | null`

운영자가 borderline 케이스 비율을 모니터링 가능.

### 4.4 부트스트랩 (Tier 4)

QualityClassifier 가 충분히 학습되기 전 (`< 500 sample`) 까지는 Tier 1+2 heuristic 으로 fallback. `IQualityClassifier` 인터페이스 뒤에 두 구현 (Heuristic, ML) 두고 sample 수에 따라 switch.

### 4.5 README 업데이트 필요 영역 (CLAUDE.md 12 영역)

Tier 1-A 적용 시:
- §5.5 Quality fallback — `finish_reason="length"` 트리거 조건 추가
- §7 Configuration Reference — `Routing.QualityFallback.BadFinishReasons` 키 추가

Tier 3-A 시: §5.5 에 judge step 흐름 추가

Tier 4 시: §6 ML Feedback Loop 에 새 classifier section 추가; §11 Operations 에 retrain workflow 추가

---

## 5. distillation 디자인과의 매핑

`distillation-fallback-design-references.md` 가 식별한 7 위치 중:

| distillation 위치 | smart-router 현재 | 개선 후 |
|---|---|---|
| `isBadResponse` heuristic | ✓ Phase 14 (`QualityCheck.fs`) | Tier 1-3 로 풍부화 |
| 35B fail → 122B retry | ✓ Phase 14 | 그대로 |
| 35B response 보존 (학습 데이터) | ✓ TraceLog `initial_response_excerpt` | Tier 4 학습 입력으로 직접 사용 |
| Failure label → classifier 학습 | ✗ **미구현** (TeacherLabeler 는 router classifier 만 학습시킴) | Tier 4 = QualityClassifier 도 같이 학습 |
| Closed-loop self-improvement | ✗ **미구현** | Tier 4 로 완성 |

Tier 4 가 들어가면 distillation 디자인의 의도가 처음으로 완전히 구현됨.
