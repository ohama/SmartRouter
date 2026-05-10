# Phase 15: Quality Signal Enrichment - Context

**Gathered:** 2026-05-10
**Status:** Ready for planning

<domain>
## Phase Boundary

Phase 14 의 `Adapters/QualityCheck.fs` 의 `isBadResponse` 휴리스틱을 5 개 차원으로 풍부화한다:

1. **`finish_reason` 활용** — mlx_lm 응답의 `choices[0].finish_reason` 이 `"length"` (max_tokens 도달; 잘림) 또는 `"content_filter"` 일 때 bad
2. **case-insensitive 키워드 매칭** — `BadKeywords` 가 `OrdinalIgnoreCase` 비교
3. **Refusal 패턴은 default 에 추가하지 않음** — operator 가 명시 opt-in (Phase 14 default `["TODO", "I think"]` 그대로)
4. **한글 응답 길이 보정** — Hangul 코드포인트 `'가'..'힣'` 비율 비례로 effective length 증가 (`length × (1 + koreanRatio × 0.8)`); 한자/일본어는 처리 안 함
5. **Shannon entropy 반복 감지** — `charEntropy(response) < EntropyThreshold` 일 때 bad (default 2.5)

모든 변경은 `src/SmartRouter.Cli/Adapters/QualityCheck.fs` (Cli 어댑터) 안에서만; ARCH-01 보존 (Core 무영향).

**범위 밖 (별도 phase 또는 deferred):**
- 122B-as-judge — Phase 16
- ML-based QualityClassifier — Phase 17
- Prompt-relative length, logprob threshold — 별도 검토 (Phase 16+ 후보)

</domain>

<decisions>
## Implementation Decisions

### Phase 14 → 15 디자인 변경 정책

- **Case-sensitivity 뒤집기**: `BadKeywords` 매칭이 case-insensitive (`StringComparison.OrdinalIgnoreCase`) 로 변경. Phase 14 의 design decision ("operator adds lowercase variants explicitly") 명시적 reversal.
  - 영향: operator 가 `"TODO"` 한 번 추가하면 `"todo"`, `"Todo"`, `"ToDo"` 변형 모두 cover.
  - False positive 위험 (e.g., `method.toDo` 식별자) 은 chat completion 응답에서 거의 발생 안 하므로 수용.

- **새 config keys 의 default 정책 — Silent enable**:
  - `BadFinishReasons` default `["length", "content_filter"]` 활성화. 명시 안 한 operator 도 자동으로 finish_reason 검사 받음.
  - `EntropyThreshold` default `2.5` 활성화. 명시 안 한 operator 도 자동으로 entropy 검사 받음.
  - 정당화: Phase 14 QF-01/QF-02 통과 (QSE-06) 가 backward-compat 핵심 보장. 새 검사가 Phase 14 통과 시나리오를 false positive 시키지 않음. 새 default 가 production 에 silent 적용되어 bad 응답이 더 잘 잡힘 — 운영자 입장에서 "더 똑똑해진" 경험.

- **Migration 표시 톤 — CHANGELOG Changed 섹션 명시**:
  - CHANGELOG entry: "Changed: quality fallback now triggers on `finish_reason='length'`, case-insensitive keyword match, low Shannon entropy responses, and Korean-aware length threshold"
  - README §5.5 "Quality fallback" subsection 의 "Trigger conditions" 항목에 5 차원 추가 명시
  - Breaking change 표시는 안 함 (operator 행위 강제 안 함; default 가 더 strict 해진 것일 뿐).

- **Backward-compat 약속 강도 — QSE-06 그대로**:
  - 시그니처 변경은 호출자 한 곳 (`ChatCompletions.fs`) 만 영향 — 동시 업데이트.
  - QF-01 (good 35B response) + QF-02 (TODO trigger fallback) 테스트 그대로 통과.
  - Wire-level config 의 strict 동등성 보장은 안 함 — Phase 14 appsettings.json 그대로 두면 새 default 들이 silent 활성화되어 fallback 행위가 더 적극적으로 변할 수 있음 (Q3 의 정책과 일관).

### 검사 우선순위 + 디버그가능성

- **반환 타입 — Structured Verdict (F# DU)**:
  ```fsharp
  type BadReason =
      | LengthBelow of effectiveLen: int
      | KeywordMatch of keyword: string
      | FinishReasonMatch of value: string
      | LowEntropy of score: float
  type Verdict = Good | Bad of BadReason
  ```
  - 호출자 (`ChatCompletions.fs` quality fallback 분기) 가 `Bad reason` 패턴 매치로 trace 에 reason 직접 포함 가능.
  - bool 반환은 사용 안 함 — Phase 14 시그니처 명시적 변경 (호출자 한 곳; 동시 업데이트).
  - `BadReason` DU case 명은 trace `bad_reason` 문자열 직렬화에 그대로 매핑.

- **TraceLog 신규 필드 `bad_reason: string`**:
  - 값 예: `"length=12"`, `"keyword=TODO"`, `"finish_reason=length"`, `"entropy=1.8"` (`Bad case` → 직렬화)
  - 정상 (`Verdict=Good`) 또는 fallback 미발화 시 null
  - schema_version=1 유지 — 필드 추가만 (rename/remove 안 함; backward-compat). Phase 14 trace consumer 무영향.
  - 운영자 워크플로우: `jq 'select(.prompt_uid == "...") | .bad_reason'` 로 어느 검사가 잡았는지 즉시 확인.

- **검사 순서 — Cheap-first (early-exit 최적화)**:
  ```
  finish_reason 매치    (string compare 1회 — 가장 cheap)
       ↓ none
  effectiveLength < min (Hangul ratio + 곱셈 — 매우 cheap)
       ↓ pass
  entropy < threshold   (O(n) char-count + log — moderate)
       ↓ pass
  BadKeywords 매치       (array.exists × IndexOf — 가장 expensive)
       ↓ none
  Good
  ```
  - 평균 latency 최소화: 명백한 케이스 (잘림, 짧은 응답) 가 가장 빨리 결정됨.
  - early-exit 으로 cascade 형태 — 첫 매치가 winning reason.

- **Operational metric — `/stats` 에 quality_check_hits.* 카운터 노출**:
  - `IStatsProvider` (Phase 3 Adapters/QueueDispatcher.fs 기존 인터페이스) 확장 또는 별도 `IQualityStatsProvider` 신설 (planner 결정).
  - 카운터: `quality_check_hits.finish_reason`, `quality_check_hits.length`, `quality_check_hits.entropy`, `quality_check_hits.keyword` (4 개).
  - `/stats` JSON 응답에 추가 — `StatsWire` snake_case 패턴 미러.
  - 운영자가 한 번의 `curl /stats` 로 "주로 어느 검사가 fallback 트리거하는지" 확인 가능 → BadKeywords/EntropyThreshold 튜닝 의사결정.

### 한글 길이 보정 범위 (early-decided)

- **Hangul 만 처리** — 코드포인트 `'가'..'힣'` (가-힣) 만 카운트.
- 한자 (CJK Unified Ideographs `一`..`鿿`), 일본어 (Hiragana `぀`..`ゟ`, Katakana `゠`..`ヿ`) 는 **처리 안 함** — operator 의 traffic 이 한국어 + 영어가 주이므로 (Phase 6 EMBED-03 결정 시 확인된 사실).
- multiplier `0.8` 은 fix (config key 안 노출). 향후 튜닝 필요 시 별도 phase 에서 노출 검토.
- `effectiveLength = int (length × (1.0 + koreanRatio × 0.8))`.
- Test 시나리오: `"안녕하세요. 잘 지내고 있어요."` (28 chars, 한글 ratio ~0.8) → effective ~50 → `MinResponseLength=30` 통과.

### Refusal pattern default 정책 (early-decided)

- **Default `BadKeywords` 에 refusal 패턴 추가하지 않음** — Phase 14 default `["TODO", "I think"]` 그대로 유지.
- 정당화:
  - `"As an AI"` 가 user 가 AI 에 대해 명시 질문 시 false positive 위험.
  - `"I cannot"` 이 legitimate refusal 케이스 ("I cannot find this in the provided context") 와 구분 불가.
  - operator 가 자기 traffic 패턴 분석 후 명시 opt-in 하는 것이 안전.
- README §7 `Routing.QualityFallback.BadKeywords` 표 description 에 "operator can add refusal patterns like `\"I cannot\"`, `\"As an AI\"` if appropriate for traffic" 권고문 추가.
- **별도 config 키 (`RefusalKeywords`) 도 만들지 않음** — `BadKeywords` 한 키로 통일; 단순함 우선.

### Claude's Discretion

- **`charEntropy` 구현 디테일** — `Seq.countBy id` 의 정확한 BCL 동작 (string → seq<char>); F# 표준 라이브러리만 사용; performance benchmark 는 planner 가 결정.
- **`StatsSnapshot` 확장 vs 별도 record** — 기존 `StatsSnapshot` 에 4 필드 추가 vs `QualityCheckStats` 별도 record. snake_case 직렬화 일관성만 보장.
- **`isBadResponse` ↔ `analyzeResponse` 함수명** — Verdict 반환하는 함수명을 `isBadResponse` 로 유지할지 (이름 부정확) `analyzeResponse` 또는 `evaluate` 로 변경할지. 호출자 한 곳이라 영향 적음.
- **`bad_reason` 직렬화 형식** — `"keyword=TODO"` (= 구분자) 또는 `"keyword:TODO"` (콜론) — operator 가 jq 로 split 하기 좋은 형식 선택.
- **검사 cascade 의 F# 구현** — `match`-with-when 가드 vs nested if-else vs Option chain. Cheap-first 순서만 보장.
- **Test 전략** — 단위 테스트 (각 검사 개별; pure function 검증 쉬움) vs 통합 테스트 (fake-Kestrel 으로 finish_reason="length" 흐름 검증). planner 가 비중 결정. QSE-06 (QF-01/QF-02 회귀) 는 통합 테스트 필수.

</decisions>

<specifics>
## Specific Ideas

- **`bad_reason` 운영자 워크플로우 reference**:
  ```bash
  # 어떤 검사가 가장 자주 fallback 트리거하는지 한 번에 확인
  jq -r 'select(.bad_reason != null) | .bad_reason | split("=")[0]' \
    logs/trace/$(date +%F).jsonl | sort | uniq -c | sort -rn

  # 특정 검사 (예: entropy) 가 잡은 응답들의 prompt_excerpt 확인
  jq 'select(.bad_reason | startswith("entropy=")) | {prompt_excerpt, initial_response_excerpt, bad_reason}' \
    logs/trace/$(date +%F).jsonl
  ```

- **`/stats` quality_check_hits counter 운영자 워크플로우**:
  - 정기적으로 `curl /stats | jq .quality_check_hits` 모니터링.
  - 갑자기 `keyword` 카운터 spike → 35B 가 새로운 BadKeyword 패턴 응답 시작 (모델 deployment 변화?).
  - `length` 카운터가 95%+ → MinResponseLength 가 너무 높을 가능성 → 튜닝.
  - `entropy` 0 → EntropyThreshold 가 너무 낮을 수 있음 → 정상 응답이 통과 못 잡힘.

- **운영자가 default 를 override 하고 싶을 때** — README §7 에 1 행으로 표시:
  ```jsonc
  "Routing": {
    "QualityFallback": {
      "BadKeywords": ["TODO", "I think", "I cannot", "As an AI"],
      "EntropyThreshold": 2.0,
      "BadFinishReasons": []
    }
  }
  ```
  세 가지 새 default 모두 명시 override 가능.

- **distillation 디자인 reference**: `.planning/docs/quality-check-improvement-options.md` Tier 1+2 의 6 개 sub-tier (1-A, 1-B, 1-C, 1-D, 2-A, 2-B) 중 Phase 15 는 1-A, 1-B, 1-D, 2-A 4 개 + Phase 14 design reversal 1 개 = 5 차원. 1-C (refusal default) 는 의식적 제외, 2-B (prompt-relative length) 는 deferred.

</specifics>

<deferred>
## Deferred Ideas

다음 아이디어는 Phase 15 논의에서 명시 제외됨 — 향후 phase 또는 별도 검토:

- **Refusal pattern 을 별도 config key (`RefusalKeywords`)** — `BadKeywords` 와 분리해서 operator 가 옵트인 가능. 현재는 `BadKeywords` 한 키로 통일 결정. 향후 운영 데이터로 refusal false positive 비중 측정 후 재논의 가능.

- **CJK 전체 길이 보정 (한자, 일본어 포함)** — 운영자 traffic 이 한국어+영어이므로 deferred. 만약 향후 일본어/중국어 traffic 들어오면 phase 신설.

- **`koreanRatio × 0.8` multiplier 의 config 노출** — 0.8 은 추정값. fix 로 시작. operator 가 한글 응답에서 false positive/negative 비중 보고 튜닝 필요 시 별도 phase.

- **Prompt-relative length** — `response_length / prompt_length` ratio 기반 검사 (긴 질문에 짧은 답 = 의심). `quality-check-improvement-options.md` Tier 2-B. Phase 16 에서 borderline classifier 로 흡수 가능 — 별도 phase 안 만들고 Phase 16 이 함께 처리하는 것 검토.

- **Logprob threshold** — `quality-check-improvement-options.md` Tier 2-C. mlx_lm 의 `logprobs: true` 지원 사전 확인 필요. 응답 size 증가 + latency 트레이드오프. Phase 16 또는 별도 검토.

- **Operator 가 검사별 enable/disable** — 4 개 검사 (finish_reason, length, entropy, keyword) 를 각각 켜고 끌 수 있게. 현재는 silent enable + 임계값 조정 (`EntropyThreshold=0` 으로 사실상 disable 가능 등) 만. 향후 명시 disable 키 (`EnabledChecks: ["finish_reason","length"]`) 검토.

- **`bad_reason` 의 i18n / locale-aware 메시지** — 운영자가 한국어 환경에서 reason 도 한국어로? 현재는 영어 (`"length=12"` 등). deferred — operator 1 명 (한국어/영어 사용자) 이라 i18n 불필요.

</deferred>

---

*Phase: 15-quality-signal-enrichment*
*Context gathered: 2026-05-10*
*Reference: `.planning/docs/quality-check-improvement-options.md` Tier 1+2*
