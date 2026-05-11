# Phase 18 Sticky Escalation — Session ID 소스의 선택지

**작성:** 2026-05-11
**상태:** Phase 18 plan-checker 통과 후, execute 전 디자인 검토 단계
**source:**
- `.planning/phases/18-session-store-and-sticky-escalation/18-RESEARCH.md`
- `.planning/research/SUMMARY.md` (Hermes 가 현재 session_id 안 보냄 — researcher 가 actual source code 확인)
- `.planning/docs/35b-selfrouting.md` §16 (sticky escalation 의 핵심 가치)
- `~/hermes-agent/` (local clone — 빈 디렉토리; 디자인 시점에 코드 분석 못 함)

**대상:** Phase 18 / Phase 20 / v2.0 milestone 전체 sticky-escalation 의 운영 가치를 평가하고 4 가지 선택지 중 결정해야 하는 사람.

---

## 결론 (TL;DR)

Phase 18 의 sticky escalation 구현은 **기술적으로는 안전** (Hermes 가 `X-Session-Id` 안 보내도 backward-compat 으로 stateless 동작) 하지만, **운영적으로는 dormant** (sticky 가 production 에서 절대 발화 안 함). 현재 plan 대로 v2.0 ship 하면 Phase 18 의 9 requirements 가 모두 "구현됐지만 운영자 눈에는 안 보이는" 상태가 된다.

운영 가치를 즉시 실현하려면 **session_id 소스가 필요**. 5 옵션:
- **A** — Plan 그대로; sticky 는 future Hermes PR + Phase 20 fingerprint opt-in 까지 dormant (계획 변경 0)
- **B** — Phase 20 fingerprint default `true` 로 플립; Phase 17/18/19/20 ship 동시에 sticky immediately functional (작은 변경)
- **C** — Fingerprint fallback 을 Phase 18 로 당겨오기; Phase 19 ship 시점 sticky 작동 (Phase 18 확장)
- **D** — Phase 18 + Phase 20 합치기; v2.0 = 3 phases (17/18/19); 가장 큰 재구조화
- **E** (§5 신규) — 35B 가 selfrouting 처럼 "이전과 같은 conversation 인가?" 판단. Fingerprint 의 의미적 한계 (§4.6) 를 35B 의 의미적 추론으로 해결. **v2.x candidate; v2.0 scope 부적합** (Phase 19 prompt + 인프라 확장; 35B boundary 판단 정확도 검증 필요)

### 추천 (정정됨)

§4.6 의 fingerprint 의미적 한계 + §5 의 35B-classify alternative 분석을 반영하여:

> **v2.0 최우선 추천: Option A** — 모든 plan 그대로 진행 (Phase 18 plans + Phase 20 plans + `FingerprintEnabled=false` default 유지).

> **v2.x candidate: Option E** — Hermes PR 이 안 오고 fingerprint cross-contamination 이 실제 관찰되는 문제이면 Phase 21 / v2.1 에서 35B-classify 도입.

**이전 추천 (Option B with default flip) 은 부적절** — fingerprint 는 client 식별 (≠ conversation 식별) 이라 의미적으로 broken. Default 활성화 시 cross-contamination 비용 강제. **진짜 해법 ranking**: (1) X-Session-Id from Hermes (HMRS-FUTURE-01) → (2) 35B-classify (Option E; v2.x) → (3) Fingerprint (B/C/D; 약한 안전망).

v2.0 는 (1) 을 기다림. Phase 18 인프라 ship + Phase 20 fingerprint opt-in 으로 정직하게 두기.

자세한 분석은 §4 (Fingerprint 메커니즘 + 한계), §5 (Option E 35B-classify 대안), 옵션 비교는 §6, 정정된 추천은 §8 참조.

---

## 1. 질문

> Hermes agent 로부터 session id 가 안 와도 Phase 18 의 구현은 문제 없나?
> *"Smart-router is ready to receive X-Session-Id from Hermes Agent"* — 이게 진짜인가?

이 질문은 두 층으로 답할 수 있다:
1. **기술 안전성** — Phase 18 코드가 X-Session-Id 부재 상황에서 에러/exception 없이 동작하나?
2. **운영 가치** — Phase 18 의 sticky escalation 이 production traffic 에서 실제 발화하나?

각각 별도로 보자.

---

## 2. Part 1 — 기술 안전성: ✅ 문제 없음

Phase 18 plans 와 research 가 명시적으로 **graceful degradation** 을 설계했음:

### 백워드-컴팻 invariant 들

| 위치 | 동작 | 출처 |
|---|---|---|
| `X-Session-Id` 헤더 부재 | `CorrelationMiddleware` 가 `HttpContext.Items[SessionIdKey] = ""` 로 저장 | SES-04 |
| `mapWireToRequest` | `req.SessionId = ""` (empty string sentinel) | SES-05 |
| `sessionStore.TryGet("")` | 항상 `None` 반환 — empty session_id 는 store key 가 되지 않음 | PITFALLS.md §3 |
| Algorithm closure sticky check | `match sessionStore.TryGet req.SessionId with | None -> proceed | Some s -> ...` | 18-02 Task 3 |
| Quality fallback Point B write | `if req.SessionId <> "" then sessionStore.Update(...)` — empty 면 skip | SES-07 |
| TTL eviction service | 빈 store 순회 → no-op | SES-08 |
| ROADMAP SC-2 | "v1.x clients without X-Session-Id continue stateless" — 명시적 SC | ROADMAP §18 |

### 결과

v1.x Hermes 클라이언트가 v2.0 smart-router 에 그대로 연결하면:
- ✅ 에러/exception 없음
- ✅ Routing 결과 = v1.x 동작 + Phase 17 Hard Rules (cascade Stage 0)
- ✅ DecisionLog/TraceLog 스키마 변경 없음 (schema_version=1 유지)
- ✅ Quality fallback (Phase 14-16) 정상 동작
- ✅ 113 → 137 → +N (Phase 18 신규 테스트) 기존 테스트 모두 통과

### "smart-router is ready to receive" 는 진짜인가?

진짜다 — **인프라가 ready**. 헤더 받으면 바로 동작한다. 하지만 받는 게 없으니 활성 코드 경로가 없는 것뿐.

---

## 3. Part 2 — 운영 가치: ⚠️ 현재 plan 대로면 dormant

### 데이터 흐름 (현재 Hermes — X-Session-Id 안 보냄)

```
Hermes Agent
    │  POST /v1/chat/completions (no X-Session-Id header)
    ▼
smart-router CorrelationMiddleware
    │  ctx.Items[SessionIdKey] = ""
    ▼
ChatCompletions mapWireToRequest
    │  req.SessionId = ""
    ▼
Phase 17 Hard Rules (Stage 0) — fires if keyword match
    │
    ▼
Algorithm closure (Phase 18 sticky stage)
    │  sessionStore.TryGet("")  →  None
    │  sticky → never overrides decision
    ▼
Default decision (35B/122B)
    │
    ▼
QueueDispatcher → response
    │
    ▼
Point B write
    │  if req.SessionId <> "" then Update  ←  skipped (empty)
    │  sessionStore.Update never called
    ▼
DecisionLog row
    │  routing_reason = (whatever stage decided) — NEVER "sticky_to_122b"
```

### 운영자 관점

```bash
# v2.0 ship 후, 일주일 production 운영
$ jq '.routing_reason' logs/decisions/$(date +%F).jsonl | sort | uniq -c
  1234 "ml"             # Routing.Mode 가 ml 이면; 또는 default 35B if selfrouting
   456 "hard_rule"      # Phase 17 키워드 매치
    78 "explicit_task"
    12 "explicit_model"
     0 "sticky_to_122b"  ← Phase 18 가 만든 enum 값, 발화 0
     0 "self_route"      ← Phase 19 도 같이 dormant 일 가능성
```

**Phase 18 의 9 SES requirements 가 모두 "구현됐지만 trigger 없음"**:
- SES-01 SessionId field — 존재하지만 항상 ""
- SES-02 SessionStore — 존재하지만 empty
- SES-03 122B-wins merge — 호출되지 않음
- SES-04 X-Session-Id read — 헤더가 없어서 empty 만 읽음
- SES-05 sticky cascade stage — TryGet None 으로 pass-through
- SES-06 StickyEscalation DU — formatReason arm 은 존재하지만 emit 안 됨
- SES-07 quality fallback session write — empty session_id 가드로 skip
- SES-08 TTL eviction — 빈 store 에서 no-op
- SES-09 config keys — 설정값 적용되지만 효과 0

### selfrouting doc §16 의 핵심 가치 — debugging continuity

자료의 의도:
```
session.current_model == 122b 면 → 다음 요청도 122B
```

이 통해 운영자가 얻는 것:
- ✅ **Reasoning continuity** — debugging 세션이 도중에 35B 로 떨어지지 않음
- ✅ **Debugging coherence** — "이전 답이 122B 였는데 다음 follow-up 이 35B" 같은 quality 격차 방지
- ✅ **Stable continuation behavior** — multi-turn 시 모델 선택 일관성

**현재 plan 대로 v2.0 ship 시 위 세 가치가 모두 미실현.** Phase 18 이 dormant 인 동안 Hermes 사용자는:
- Turn 1: "Debug this LLVM segfault" → Hard Rules → 122B
- Turn 2: "Show the stack trace" → Stage 0 패스, default → 35B (continuity 손실)
- Turn 3: "Why is RAX clobbered?" → 다시 122B 가 필요한데 stateless 라 luck-of-the-draw

이게 자료가 경고한 정확한 실패 모드.

### Sticky 가 functional 해지는 조건

세 가지 중 하나가 있어야 함:
1. **Hermes PR** — HMRS-FUTURE-01; Hermes Agent 가 X-Session-Id 보내도록 패치 (외부 의존성; 우리 통제 밖)
2. **Phase 20 fingerprint opt-in** — `Routing.Session.FingerprintEnabled=true` 설정 (현재 default false)
3. **Phase 18 fingerprint 포함** — fingerprint fallback 을 Phase 18 로 당겨오기

현재 plan = 1+2 결합 (2 가 default off 라 운영자 명시 설정 + Phase 20 ship 대기).

---

## 4. Part 3 — Fingerprint 메커니즘 자세히

옵션 B/C/D 모두 "fingerprint fallback" 에 의존한다. 결정 전 이 메커니즘이 정확히 무엇인지, 어떻게 동작하는지, 어디서 깨지는지 알아야 한다.

### 4.1 정의

```
session_key = SHA-256(RemoteIpAddress + "|" + User-Agent)[0..15]
```

- **입력**: HTTP 요청의 `RemoteIpAddress` (Kestrel `ctx.Connection.RemoteIpAddress`) + `User-Agent` 헤더 값
- **구분자**: `|` (파이프) — IP 와 UA 가 우연히 concat 됐을 때 같은 결과를 내는 것을 방지 (예: IP "1.2.3.4" + UA "5.6" ≠ IP "1.2.3" + UA "4.5.6")
- **해시**: SHA-256 (BCL `System.Security.Cryptography.SHA256`)
- **잘라내기**: 첫 8 bytes → 16 hex chars (예: `"a3f8c2d1e9b74a5f"`)
- **결정성**: 같은 IP+UA 면 항상 같은 fingerprint (no nonce, no salt)

### 4.2 F# 구현 스케치

`CorrelationMiddleware.fs` 에 들어갈 함수 (Phase 20 HMRS-02 또는 옵션 C 의 Phase 18 확장 시):

```fsharp
open System.Security.Cryptography
open System.Text

let private fingerprintSessionId (ctx: HttpContext) : string =
    let remoteIp =
        match ctx.Connection.RemoteIpAddress with
        | null -> ""
        | addr -> addr.ToString()  // "127.0.0.1" 또는 "::1" 등
    let userAgent =
        match ctx.Request.Headers.TryGetValue("User-Agent") with
        | true, values when values.Count > 0 -> string values.[0]
        | _ -> ""
    let raw = remoteIp + "|" + userAgent
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(raw)
    let hash = sha.ComputeHash(bytes)
    hash
    |> Array.take 8
    |> Array.map (sprintf "%02x")
    |> String.concat ""

let deriveSessionId (ctx: HttpContext) (opts: SessionOptions) : string =
    // 1순위: explicit X-Session-Id header (Hermes PR 후 사용)
    match ctx.Request.Headers.TryGetValue("X-Session-Id") with
    | true, values when values.Count > 0 && not (String.IsNullOrWhiteSpace values.[0]) ->
        string values.[0]
    // 2순위: fingerprint (FingerprintEnabled 가 true 일 때만)
    | _ when opts.FingerprintEnabled ->
        fingerprintSessionId ctx
    // 3순위: stateless (no session_id)
    | _ ->
        ""
```

`SHA256.Create()` + `ComputeHash(bytes)` 는 BCL 만 사용 — 새 NuGet 없음. ~1 microsecond 수준 latency.

### 4.3 작동 시나리오 (✅ 정상 동작)

**Loopback single-client (가장 흔한 v2.0 use case):**

```
Request 1:
  RemoteIpAddress = "127.0.0.1"
  User-Agent = "hermes-agent/1.0.0"
  → raw = "127.0.0.1|hermes-agent/1.0.0"
  → SHA-256 prefix = "a3f8c2d1e9b74a5f"  (예시)
  → session_id = "a3f8c2d1e9b74a5f"

Request 2 (5 분 후, 같은 Hermes 클라이언트):
  RemoteIpAddress = "127.0.0.1"
  User-Agent = "hermes-agent/1.0.0"
  → 같은 fingerprint "a3f8c2d1e9b74a5f"
  → sessionStore.TryGet("a3f8c2d1e9b74a5f") → Some { LastModel = Qwen122B; ... }
  → sticky → 122B
```

✅ **debugging continuity 실현됨** — Hermes 가 X-Session-Id 안 보내도.

### 4.4 깨지는 시나리오 (⚠️ 알려진 한계)

| # | 시나리오 | 문제 | 회피 |
|---|---|---|---|
| 1 | **Multi-instance 같은 host** (Hermes 두 개 같은 Mac) | IP=127.0.0.1, UA=동일 → fingerprint 공유 → client A 의 122B 승격이 client B 도 영향 | 운영자가 인지하고 single-client 만 사용 |
| 2 | **Reverse-proxy 환경** (nginx/Caddy 앞에) | RemoteIpAddress = proxy IP → 모든 client 가 같은 fingerprint → 전체 traffic 이 하나의 sticky bucket | README §10 명시; future PROXY-01 에서 X-Forwarded-For 지원 |
| 3 | **Mobile/changing IP** | 같은 사용자, 매번 다른 IP → 매 요청 다른 fingerprint → continuity 없음 | 모바일은 X-Session-Id 강제 (Hermes PR 후 native) |
| 4 | **Hermes UA bump** (`hermes-agent/1.0.0` → `1.0.1`) | 업데이트 후 같은 사용자, 다른 fingerprint → 진행 중 debugging 세션 끊김 | UA 안 보내거나 stable 한 prefix 만 사용? — 회피 어려움 |
| 5 | **Empty User-Agent** | 같은 IP 의 모든 UA-less client 가 fingerprint 공유 | 정상 client 는 UA 보냄; 예외만 stateless 처리 |
| 6 | **IPv4 vs IPv6 mixed** (127.0.0.1 vs ::1) | loopback 의 두 표현이 다른 fingerprint | OS/network stack 일관성 가정; localhost 는 보통 한쪽으로만 옴 |
| 7 | **HTTP keep-alive vs 신규 connection** | RemoteIpAddress 는 keep-alive 와 무관하므로 영향 없음 — 다행 | N/A |
| 8 | **NAT 뒤의 여러 client** | 같은 public IP → 같은 fingerprint → 충돌 | NAT 뒤 환경은 X-Session-Id 강제 |
| 9 | **smart-router 재시작** | session store 가 in-memory → 모든 fingerprint session 삭제 → 한 번은 cold sticky | TTL 30분 정책상 수용 가능; "다음 요청에 122B 가 다시 fire 하면 재구축" |

**핵심 관찰**: 1, 2, 8 은 같은 fingerprint 가 다른 user 사이에서 공유되는 **충돌** (correctness 문제). 3, 4 는 같은 user 가 다른 fingerprint 를 갖는 **단절** (가치 손실). 5, 6, 7, 9 는 minor edge case.

### 4.5 v2.0 deployment 에서 실제 위험 평가

v2.0 의 배포 환경:
- ✅ `127.0.0.1:4000` loopback only (`OPS-04` v1.x 부터; v2.0 unchanged)
- ✅ Hermes Agent 가 단일 인스턴스로 같은 Mac 에서 실행 (memory note `ref_hermes_agent.md`)
- ✅ Reverse-proxy 없음 (no nginx/Caddy)
- ✅ Graphify 는 task field 로 라우팅 — sticky 영향 안 받음 (explicit task = Stage 2 가 Stage 3 sticky 보다 먼저)

→ **충돌 시나리오 (1, 2, 8) 거의 발생 안 함**

남는 위험 (이전 분석):
- **시나리오 4 (Hermes UA bump)** — Hermes 업데이트 시 모든 client 의 sticky 가 한 번씩 재시작됨. 운영적으로 수용 가능 (30분 TTL 이후 자연 만료와 비슷).
- **시나리오 5 (empty UA)** — curl 같은 도구가 UA 안 보내면 fingerprint 가 IP 만 기반. 운영자가 curl 로 테스트할 때만 영향. 정상 사용 영향 없음.

> **§4.6 의 비판이 §4.5 의 결론을 뒤집는다.** 위의 "충돌 시나리오 거의 발생 안 함" 은 **multi-process/multi-host** 차원에서만 맞음. 동일한 Hermes 프로세스 안의 multi-conversation 충돌은 §4.6 이 자세히 분석.

### 4.6 Fingerprint 의 근본 한계 — Client vs Session 의 분리

**중요한 깨달음**: fingerprint (`IP + UA`) 는 **요청을 보낸 클라이언트 프로세스** 를 식별한다. 그러나 sticky escalation 이 정말로 필요한 것은 **debugging 대화(conversation)** 의 식별이다. 이 둘은 의미적으로 다른 단위다.

#### 4.6.1 의미 분리

| 개념 | 무엇을 식별하나 | 식별 단위 |
|---|---|---|
| **Fingerprint (`IP+UA`)** | 요청을 보내는 OS 프로세스 / TCP 클라이언트 | "이 Hermes 가 이 Mac 에서 동작하는 동안" — 프로세스 수명 |
| **Session (conversation)** | 의미적으로 묶인 multi-turn debugging 대화 | "이 사용자가 LLVM 버그 잡는 동안 5 턴" — 분 단위 |
| **selfrouting doc §16 의 의도** | conversation 단위 continuity | session level |

**핵심 문제**: 한 Hermes 프로세스는 **수명 동안 여러 conversation 을 처리**한다. 같은 fingerprint (= 같은 프로세스) 하에서 conversation A, B, C 가 순차적으로 (또는 인터리브로) 진행될 수 있다.

#### 4.6.2 Hermes 의 실제 동작 모델

`~/hermes-agent` 의 일반적 운영 패턴 (memory note `ref_hermes_agent.md`):
- Hermes Agent 는 **long-running 프로세스** (launchd 또는 수동 시작 후 종일 실행)
- 한 OS 프로세스 안에서 여러 agent loop / conversation 처리 가능
- Hermes 가 내부적으로 conversation/session.id 를 유지하지만 — **smart-router 한테는 안 보냄** (researcher 가 actual source 확인)
- 모든 conversation 의 HTTP 요청이 같은 IP (127.0.0.1) + 같은 UA (`hermes-agent/X.Y.Z`) 로 나옴

⇒ **fingerprint 가 conversation 을 구분하지 못함**. 모든 conversation 이 같은 sticky bucket 공유.

#### 4.6.3 구체 시나리오: Multi-conversation 교차 오염

**Scenario A** — 이상적 케이스 (fingerprint 가치 있음):

```
09:00  Hermes 시작 (fingerprint X 부여)
09:05  Conversation 1 turn 1: "Debug this LLVM segfault"
       → Hard Rules (LLVM) → 122B → sessionStore[X] = 122B
09:06  Conversation 1 turn 2: "Show backtrace"
       → fingerprint X → sticky → 122B ✓ (continuity)
09:08  Conversation 1 turn 3: "What's RAX clobbered by?"
       → fingerprint X → sticky → 122B ✓ (continuity)
09:10  Conversation 1 종료. 사용자가 다른 일 함.
09:40  TTL (30분) 만료. sessionStore[X] = expired.
09:45  Conversation 2 turn 1: "Write README intro"
       → no Hard Rules, no sticky → 35B ✓ (정상)
```

✅ 이상적 — 한 conversation 안에서 sticky 발휘, 다음 conversation 시작 전에 TTL 만료.

**Scenario B** — 현실적 케이스 (cross-contamination 발생):

```
09:00  Hermes 시작 (fingerprint X)
09:05  Conversation 1 turn 1: "Debug this LLVM segfault"
       → Hard Rules → 122B → sessionStore[X] = 122B
09:08  Conversation 1 turn 2-5 (LLVM debugging 진행)
       → 모두 sticky → 122B ✓
09:15  Conversation 1 종료.
09:20  Conversation 2 turn 1: "Format this JSON file"
       (trivial; 정상이라면 35B 가야 함)
       → fingerprint X → sessionStore[X] still = 122B (TTL 만료 전)
       → sticky → 122B ❌ (불필요한 escalation; latency 손실 + 122B 자원 낭비)
09:22  Conversation 2 turn 2: "Add comments"
       → 또 122B ❌
...
09:35  Conversation 2 종료. 모든 trivial 요청이 122B 처리됨.
09:36  Conversation 3 turn 1: "Summarize this paper"
       → 또 122B ❌
```

❌ **Conversation 1 의 LLVM escalation 이 30분 동안 모든 후속 conversation 을 오염시킴**. 사용자는 README 정리, 포맷팅, 요약 등 trivial 작업도 122B 로 처리받음. 122B 가 35B 보다 5-10× 느리므로 (cold start 240s 까지 가능) **사용자 경험 명백히 악화**.

**Scenario C** — Worst case (전체 production 시간 오염):

```
09:00  Hermes 시작
09:15  LLVM debugging conversation → 122B sticky
09:35  LLVM 끝. TTL 25분 남음.
09:40  Trivial conversation 시작 → 122B 사용 (X TTL 만료 안 됨)
       → 매 turn 마다 sessionStore[X].LastAccessedAt 갱신 → TTL 리셋
       → 시간 흐를수록 sticky 가 영구화됨 (각 trivial turn 이 TTL 재충전)
...
17:00  여전히 122B sticky 유지 (8시간 trivial 작업이 모두 122B 로 처리됨)
```

❌ **Sticky 가 self-sustaining 됨**. TTL 이 access 마다 reset 되는 정책이면 (Phase 18 plan 가 그렇게 한다), conversation 이 빈번하면 TTL 사실상 무한.

#### 4.6.4 TTL 조정으로 해결되나?

| TTL 설정 | 효과 | 부작용 |
|---|---|---|
| 30분 (현재 default) | Scenario B 가 자주 발생 | 위 분석 |
| 5분 | Cross-contamination 줄어듦 | 긴 debugging 세션이 도중에 35B 로 떨어짐 (continuity 손실) |
| 1분 | Cross-contamination 거의 없음 | 긴 conversation 의 후반부가 35B 로 — sticky 가치 거의 0 |
| Access-reset 끄기 | 30분 절대 만료 | Scenario B 줄지만 긴 세션도 30분 컷 |

**근본적으로 TTL 로는 안 풀림**. 문제가 시간적이 아니라 의미적 (conversation boundary 가 fingerprint 에 없음).

#### 4.6.5 Fingerprint 가 실제로 제공하는 것

**Fingerprint 가 제공하는 시맨틱**:
> "이 Hermes 프로세스가 최근 TTL 시간 안에 122B 로 escalate 한 적이 있으면, 다음 요청도 122B 로 보낸다."

이건 selfrouting doc §16 의 의도와 다른 시맨틱:
- 의도: "이 **debugging conversation** 이 122B 면 같은 conversation 의 다음 turn 도 122B"
- 실제: "이 **client process** 가 최근 122B 면 같은 process 의 next request 도 122B"

⇒ **client process 단위 sticky** 라는 시맨틱은 자료가 의도한 것이 아님. Fingerprint 는 right shape but wrong granularity.

#### 4.6.6 그럼 fingerprint 는 가치가 0 인가?

완전히 0 은 아님. 다음 케이스에서는 여전히 가치 있음:
- **단일 conversation 사용자** — Hermes 가 한 conversation 만 처리하고 종료 (rare; Hermes 는 long-running 가정)
- **연속 debugging 모드** — 사용자가 종일 하나의 큰 debugging 작업만 한다 (가능)
- **"trivial" 도 122B 가 좋은 환경** — 122B 가 빨라서 over-routing 비용이 작음 (cold start 이후 warm 122B 가 35B 와 latency 차이 없음 — 실제로는 다름)
- **회복력 측면** — 한 번 LLVM 같은 강한 신호 봤으면 잠시 "조심 모드" 로 유지 — 실수보다 보수성 우선

가치가 있는 use case 는 있지만 — **자료 §16 의 "debugging continuity" 목표와는 부분적으로만 일치**.

#### 4.6.7 진짜 해결책

자료 §16 의 의미적 sticky 를 실현하려면 **conversation-scoped session_id** 필요:
- Hermes 가 내부 conversation.id 를 X-Session-Id 헤더로 전송 (HMRS-FUTURE-01)
- 그러면 conversation A 와 B 가 다른 session_id → 다른 sticky bucket → cross-contamination 0

⇒ **fingerprint 는 임시 다리 (bridge)** 이지 destination 이 아님. Hermes PR 이 진짜 해법.

#### 4.6.8 옵션 선택에 미치는 영향

이 분석은 §7 추천을 부분적으로 뒤집는다:

| 옵션 | §4.5 까지의 평가 | §4.6 분석 후 재평가 |
|---|---|---|
| **A** (현행; sticky dormant) | 비추천 ("Phase 18 가치 흐릿") | **재평가: 합리적** — fingerprint 가 어차피 의미적으로 broken 하므로 인프라만 ship 하고 Hermes PR 까지 대기 |
| **B** (fingerprint default=true) | 최우선 추천 | **재평가: 보수적이지만 cross-contamination 위험** — 운영자가 multi-conversation 패턴이면 122B 과다 사용; trade-off README 명시 필요 |
| **C** (Phase 18 에 fingerprint 포함) | 차선 | B 와 동일한 limitation |
| **D** (Phase 18+20 합치기) | 비추천 | B 와 동일한 limitation |

**Cross-contamination 위험 vs 가치 trade-off**:
- ✅ Sticky 작동 (옵션 B/C/D) = LLVM debug 의 turn 3 가 35B 로 떨어지는 일 막음 (good)
- ❌ Sticky 오버슛 = LLVM debug 후 30분간 모든 trivial 도 122B (bad)
- 어느 쪽이 더 큰지는 사용자의 conversation 패턴에 달려있음

**운영자 결정 자료** (옵션 B/C/D 채택 시 README 에 documented 필요):
> Sticky escalation via fingerprint approximates "client-level recent activity" not "conversation-level continuity." If your usage pattern is short bursty debugging sessions followed by trivial work, fingerprint sticky will over-escalate the trivial work to 122B. Consider lowering `Routing.Session.TtlMinutes` to 5 or setting `Routing.Session.FingerprintEnabled=false` until Hermes-side X-Session-Id propagation lands.

### 4.7 Privacy 고려사항

- RemoteIp + UA 는 fingerprinting 으로 PII 가능성. **로컬 loopback 만이라 PII 실제 위험 없음** (127.0.0.1 + hermes-agent UA 는 식별성 0).
- SHA-256 hash 는 one-way — operator 도 fingerprint 에서 원본 IP/UA 복원 불가.
- DecisionLog/TraceLog 에 fingerprint 자체는 emit 안 됨. session_id 가 들어가지만, X-Session-Id 출처인지 fingerprint 출처인지 구분 안 됨 (intentional opacity).
- 30 분 TTL 후 자동 만료 — long-term retention 없음.
- 재시작 시 in-memory store 소실 — persistence 없음.

→ Privacy 위험 ≈ 0 in current v2.0 deployment.

### 4.8 X-Session-Id vs Fingerprint — 우선순위

설계상 X-Session-Id 가 항상 우선:

```
헤더 X-Session-Id 존재 + non-empty?
   ├─ Yes → 헤더 값을 session_id 로 사용 (fingerprint 무시)
   └─ No  → FingerprintEnabled=true 인가?
            ├─ Yes → fingerprint 도출
            └─ No  → session_id = "" (stateless)
```

이 우선순위 덕분에:
- **현재 (Hermes 가 X-Session-Id 안 보냄)**: fingerprint fallback 발동 (옵션 B/C/D 에서)
- **미래 (Hermes PR 후 X-Session-Id 보냄)**: 헤더 우선 → fingerprint 자동 무시
- **마이그레이션 무중단**: 운영자가 Hermes 업데이트 시 config 변경 불필요. 자동으로 헤더 기반으로 전환됨.
- 운영자는 Hermes PR 안정 후 `FingerprintEnabled=false` 로 명시 opt-out 가능 (cleanup; 필수 아님).

### 4.9 다른 session_id 소스와 비교

| Approach | 장점 | 단점 | v2.0 적합? |
|---|---|---|---|
| **Explicit X-Session-Id** (HMRS-FUTURE-01) | 가장 정확; client 가 scope 결정; multi-client 안전 | Hermes 측 PR 필요 (외부 의존) | 미래 |
| **IP+UA fingerprint** (HMRS-02 / 옵션 B/C/D) | client 변경 필요 없음; 결정적; loopback 완벽 | Multi-client/reverse-proxy 한계 | **v2.0 현재 최적** |
| HTTP cookie (`Set-Cookie`) | 표준; client-managed; persistent | smart-router 가 쿠키 infra 없음; CORS 복잡; OpenAI SDK 호환 안 됨 | ❌ overkill |
| Query parameter `?session_id=...` | 매우 단순 | URL 오염; OpenAI-compat 클라이언트 안 보냄 | ❌ wire 위반 |
| TCP connection ID | per-connection 자연스러움 | keep-alive vs 새 connection 의 의미가 "세션" 과 다름 | ❌ 의미 안 맞음 |
| `correlation_id` (v1.x existing) | 기존 인프라 활용 | per-request 라 continuity 0 | ❌ 의미상 부적합 |
| **No fingerprint** (옵션 A) | 가장 안전 (위험 0); v1.x stateless 유지 | Phase 18 sticky dormant | 보수적 |

### 4.10 Fingerprint 가 운영자에게 보이는 흔적

운영자가 fingerprint 동작을 확인하는 방법:

```bash
# DecisionLog 에서 sticky_to_122b 가 나타나는지 (fingerprint 가 functional 한 증거)
jq -r '.routing_reason' logs/decisions/$(date +%F).jsonl | sort | uniq -c
# 옵션 A:    0 sticky_to_122b
# 옵션 B/C/D:  N sticky_to_122b (Hermes 의 multi-turn debugging 세션 마다)

# 더 깊게: 같은 prompt 가 두 번 들어왔을 때 두 번째가 sticky 인지
jq 'select(.prompt_hash | startswith("...")) | .routing_reason' logs/decisions/...

# /stats endpoint 에 session store 통계 (Phase 18 plans 에 노출 안 됐다면 추가 가능)
curl http://127.0.0.1:4000/stats | jq '.session_store_entries'  # 예시
```

만약 `sticky_to_122b` 가 0 으로 유지되면:
- 옵션 A: 정상 (의도된 dormancy)
- 옵션 B/C/D: 문제 — `FingerprintEnabled=true` 인지 확인; `Routing.Session.TtlMinutes` 가 너무 짧은지; smart-router 가 너무 자주 재시작되는지

### 4.11 Fingerprint 구현 시 테스트 시나리오

옵션 B/C/D 채택 시 Phase 18 또는 Phase 20 가 추가해야 할 테스트:

```
✓ same IP + same UA → 두 요청이 같은 session_id 받음
✓ same IP + different UA → 다른 session_id
✓ different IP + same UA → 다른 session_id
✓ empty UA (UA 헤더 없음) → fingerprint 가 IP 만 기반 (suffix "")
✓ IPv4 "127.0.0.1" vs IPv6 "::1" → 다른 session_id (deterministic but distinct)
✓ X-Session-Id 헤더 있음 + FingerprintEnabled=true → 헤더가 우선; fingerprint 무시
✓ X-Session-Id 헤더 있음 + FingerprintEnabled=false → 헤더 사용
✓ X-Session-Id 헤더 없음 + FingerprintEnabled=true → fingerprint 적용
✓ X-Session-Id 헤더 없음 + FingerprintEnabled=false → session_id = "" (stateless)
✓ X-Session-Id 헤더 빈 문자열 + FingerprintEnabled=true → fingerprint 적용 (헤더 무효)
✓ X-Session-Id 헤더 whitespace + FingerprintEnabled=true → fingerprint 적용 (trim 후 빈 값)
```

Integration test (옵션 B 의 경우 Phase 18 또는 Phase 20):
```
- Hermes-스타일 fake client (no X-Session-Id, stable UA) 2 requests:
  1번째 prompt 가 Hard Rules 매치 → 122B + sessionStore.Update("fingerprint-X", Qwen122B)
  2번째 trivial prompt → sticky check → TryGet "fingerprint-X" → Some Qwen122B → 122B with routing_reason="sticky_to_122b"
```

### 4.12 Fingerprint 의 평가 결론 — 정정된 시각

**§4.5 까지의 평가** (multi-process/multi-host 차원만 고려):
- ✅ Fingerprint 는 functional + safe + 결정적
- ✅ 알려진 충돌 시나리오 (1, 2, 8) 거의 발생 안 함
- ⚠️ UA bump (시나리오 4) 가 유일한 실질적 단점

**§4.6 분석 후 정정된 평가** (Hermes 의 long-running multi-conversation 본질 고려):
- ⚠️ Fingerprint = **client 식별 ≠ session(conversation) 식별** — 의미적 mismatch
- ❌ Hermes 의 normal 사용 패턴 (하루 동안 여러 conversation 처리) 에서 **cross-contamination 자주 발생**
- ❌ TTL 조정으로 풀리지 않음 (시간적 문제가 아니라 의미적 boundary 부재 문제)
- ⚠️ Scenario B (LLVM debug 후 trivial 작업이 30분간 122B 로 over-escalate) 가 운영적으로 가장 흔함
- ✅ 단일 conversation / 연속 debugging 시나리오에서는 여전히 가치
- ✅ X-Session-Id 와 자연 공존 — 미래 Hermes PR 시 무중단 마이그레이션
- ✅ Privacy 위험 ≈ 0 (loopback)
- ✅ BCL 만 사용 (no new NuGet); ~microsecond latency

**솔직한 결론**:

> Fingerprint 는 selfrouting doc §16 이 의도한 "debugging conversation continuity" 를 **부분적으로만** 구현한다. Hermes 측 X-Session-Id 가 진짜 해법이고, fingerprint 는 "Hermes PR 까지의 임시 다리" 로 봐야 한다.
>
> "임시 다리" 가 가치 있는지는 사용자 패턴에 달려있음:
> - **Continuous-debugging 사용자** (종일 하나의 큰 작업): 가치 있음
> - **Mixed-workload 사용자** (debug + trivial 섞임): cross-contamination 비용 > 가치
> - **Trivial-heavy 사용자** (가끔만 debug): fingerprint 가 active 면 거의 영향 없음
>
> v2.0 deployment 의 실제 사용자가 어느 category 인지 모름 (Hermes 의 daily traffic 측정 안 됐음).

**옵션 추천 재평가**:
- **A** (sticky dormant + Hermes PR 대기): **이전 비추천 → 현재 합리적**. Fingerprint 의 의미적 broken 을 ship 하지 않음. Phase 20 docs phase 가 "Hermes PR 가 와야 sticky 활성화" 명시.
- **B** (fingerprint default=true): **이전 최우선 → 현재 trade-off 있음**. 사용자 패턴에 따라 over-escalation 발생 가능. README 에 cross-contamination 위험 명시 + `FingerprintEnabled=false` opt-out 강조 필요.
- **A 변형: A' (fingerprint default=false, but ship infra)**: §7 추천의 새 후보 — Phase 20 fingerprint 를 ship 하되 default off; 운영자가 자기 패턴 보고 enable. 이게 가장 균형있는 선택일 수 있음 (= 현재 Phase 20 plan 의 정확한 default 동작).

**중요한 인사이트**: **현재 Phase 20 plan (FingerprintEnabled default false) 이 사실 가장 정확한 선택**이었을 수 있음. Option B 가 default 를 `true` 로 플립하자고 한 건 §4.6 분석을 충분히 고려 안 한 것. **Phase 20 plan 그대로 두는 게 (옵션 A) 가장 보수적/안전한 선택**으로 재평가됨.

---

## 5. Part 4 — Alternative: 35B 기반 Conversation Boundary Detection (Option E)

**사용자 제안**: selfrouting paradigm 을 conversation boundary 까지 확장 — 35B 한테 "이전 요청과 같은 session 인가?" 묻기. Fingerprint 의 process-level 식별 한계를 의미적 판단으로 해결.

이 섹션이 Option A-D 가 다루는 fingerprint 기반 접근의 **대안** 을 분석. 결론을 미리 말하면: **개념적으로 매력적이지만 v2.0 scope 에 부적합; v2.x candidate**.

### 5.1 핵심 아이디어

35B 가 매 요청마다 두 질문에 답:
1. **ROUTE_SAFE / ROUTE_UNSAFE** — 기존 selfrouting (Phase 19) 질문
2. **SESSION_CONTINUE / SESSION_NEW** (NEW) — 이전 요청의 continuation 인가?

session_id 결정:
- `SESSION_CONTINUE` → 이전 session_id 재사용 → sticky 활성
- `SESSION_NEW` → 새 session_id 생성 → sticky bucket 리셋

**Why this is semantically correct**: 35B 는 prompt content 를 의미적으로 이해함. "show stack trace" 가 직전 "debug LLVM segfault" 의 continuation 인지 알 수 있음. Fingerprint 의 process-level boundary 문제 (§4.6) 가 사라짐.

### 5.2 구현 스케치 (Phase 19 selfrouter 의 prompt 확장)

```
prompts/self-router-prompt.md (v2):

Previous request: {{PREVIOUS_PROMPT}} (or "[none]" if first)
Current request: {{CURRENT_PROMPT}}

Reply on two lines exactly:
ROUTE: SAFE | UNSAFE
SESSION: CONTINUE | NEW
```

35B response (`max_tokens=12`, `temp=0`):
```
ROUTE: UNSAFE
SESSION: CONTINUE
```

Parser 4-arm matrix:
| ROUTE | SESSION | 결과 |
|---|---|---|
| SAFE | NEW | 35B 처리, 새 session 시작 |
| SAFE | CONTINUE | 35B 처리, 같은 session 유지 |
| UNSAFE | NEW | 122B 처리, 새 session 시작 |
| UNSAFE | CONTINUE | 122B 처리, 같은 session (sticky 활성) |
| 파싱 실패 | 파싱 실패 | safety-biased default → UNSAFE + NEW |

**추가 인프라**:
- `RecentPromptBuffer` — client identifier 별 마지막 N개 prompt 저장 (in-memory `ConcurrentDictionary`; 30분 TTL)
- 여전히 **client identifier** 필요 (`X-Session-Id` || fingerprint || correlation_id) — buffer key
- 첫 요청 (no recent) → 자동 NEW

### 5.3 Fingerprint vs Option E 비교

| 차원 | Fingerprint (B/C/D) | 35B Session Classifier (E) |
|---|---|---|
| 식별 단위 | Client process | Semantic conversation |
| Hermes multi-conversation 처리 | ❌ Cross-contamination (§4.6 Scenario B) | ✅ Conversation boundary 인식 |
| 추가 latency per request | ~0ms (in-memory hash) | ~10-50ms (35B classify; Phase 19 와 통합 가능) |
| Streaming 지원 | ✅ (hash sync) | ❌ (Phase 19 와 동일 skip 정책) |
| 35B HTTP calls | 0 추가 (Phase 19 만) | Phase 19 selfrouter call 에 통합 가능 — 0 추가 |
| Bootstrap (첫 요청) | 즉시 fingerprint | 자동 NEW (recent buffer 비어있음) |
| Client identifier 의존 | ✅ 자체적 | 여전히 필요 (buffer keying) |
| 35B 오류 risk | 없음 | 있음 (overconfidence; misclassify) |
| 의미적 정확성 | 낮음 (process-level) | 높음 (conversation-level) |
| 인프라 복잡도 | 낮음 (SessionStore + middleware) | 높음 (SessionStore + selfrouter + RecentPromptBuffer) |

### 5.4 강점

1. **자료 §16 의도와 정확히 일치** — debugging continuity 가 semantic conversation 단위
2. **Fingerprint 의 cross-contamination 해결** — LLVM debug 후 README 정리 → 35B 가 "SESSION: NEW" 답할 가능성 높음
3. **selfrouting paradigm 일관성** — "model 한테 물어보면 된다" 원칙 확장
4. **Hermes PR 불필요** — Hermes 가 X-Session-Id 안 보내도 의미적 sticky 동작
5. **통합 selfrouter call** — Phase 19 의 한 번 호출에 두 질문 끼워 넣으면 추가 HTTP 비용 0

### 5.5 약점

1. **35B 의 boundary 판단 오류** — 자료 §7 ("router thinking too deeply → router failed") 가 conversation classifier 에도 적용. Conversation boundary 판단은 SAFE/UNSAFE 보다 더 nuanced (같은 codebase 의 다른 함수 디버깅 = 같은 session? 다른 session?).

2. **Bootstrap problem** — 첫 요청은 recent 없음 → 자동 SESSION_NEW. 첫 122B escalation 이후의 follow-up 부터만 sticky 활성. Phase 18 spec ("session 첫 요청부터") 과 미묘하게 다름.

3. **Client identifier 가 여전히 필요** — 35B 한테 "previous prompt" 를 주려면 누군가가 "이 client 의 previous" 인지 알아야 함. Fingerprint 또는 X-Session-Id 또는 correlation_id 가 buffer key. ⇒ **35B classifier 는 fingerprint 의 보완이지 완전 대체가 아님**.

4. **Streaming branch 적용 불가** — Phase 19 self-classify 가 streaming skip 하듯, session-classify 도 동일 이유로 skip. 따라서 streaming 요청의 conversation continuity 는 여전히 미해결.

5. **Latency overhead** — Phase 19 의 ~10-50ms 가 통합 prompt 로 인해 ~15-60ms 로 증가. Per-request 단위로는 누적.

6. **35B 의 self-reference** — 35B 가 자기 자신의 routing 을 결정 (Phase 19 와 같은 self-reference). 35B 가 자기 conversation 인지 객관적으로 판단하기 어려움.

7. **추가 인프라**: `RecentPromptBuffer` 새 store 필요. Phase 18 SessionStore + Phase 19 SelfRouter cache + 이 새 buffer → 세 개의 client-keyed store.

8. **테스트 복잡도** — fake-Kestrel selfrouter 가 SAFE/UNSAFE + CONTINUE/NEW 4-way response 처리.

### 5.6 Phase 18 영향: 거의 0

Option E 는 **Phase 18 영향 거의 없음** — 35B-classifier 는 Phase 19+ 확장 또는 새 phase.

Phase 18 (sticky infrastructure) 는 그대로 진행:
- `RouterRequest.SessionId` field — Option E 에서도 필요 (35B-classify 결과로 session_id 생성)
- `SessionStore` — 동일 필요
- `CorrelationMiddleware` X-Session-Id read — 여전히 1순위 (헤더 있으면 사용)
- sticky cascade stage — 동일

차이는 **session_id 소스 fallback chain 만**:
- Option A: X-Session-Id only (Hermes PR 까지 dormant)
- Option B/C/D: X-Session-Id || fingerprint
- **Option E**: X-Session-Id || (client_token + 35B-classify refinement)

⇒ **Option E 채택 시에도 Phase 18 plans 그대로 ship 가능**. 35B-classify 는 Phase 19 selfrouter 의 prompt 확장 또는 Phase 21 신규 작업.

### 5.7 시나리오 — Option E 가 Fingerprint cross-contamination 해결하는 방식

§4.6.3 Scenario B 를 Option E 로 다시 실행:

```
09:00  Hermes 시작 (client_token X, e.g., fingerprint 또는 correlation_id-derived)
09:05  Request 1: "Debug this LLVM segfault"
       recent_buffer[X] = []  →  selfrouter prompt: previous="[none]", current="Debug..."
       35B: ROUTE=UNSAFE, SESSION=NEW
       → session_id = sha256("X|new1") → 122B 로 routing → sessionStore[session_id_new1] = 122B
       recent_buffer[X] = ["Debug LLVM segfault"]

09:08  Request 2: "Show backtrace"
       selfrouter prompt: previous="Debug LLVM segfault", current="Show backtrace"
       35B: ROUTE=UNSAFE, SESSION=CONTINUE
       → session_id = session_id_new1 (재사용) → sticky check → 122B ✅
       recent_buffer[X] = ["Show backtrace", "Debug LLVM segfault"]

09:20  Request 3: "Format this JSON" (Scenario B 의 trivial)
       selfrouter prompt: previous="Show backtrace", current="Format this JSON"
       35B: ROUTE=SAFE, SESSION=NEW   ← 35B 가 conversation 단절 인식!
       → session_id = sha256("X|new2") → 35B 로 routing ✅
       (이전 LLVM session 의 sticky 가 새 conversation 에 영향 안 미침)
```

✅ **Fingerprint Scenario B 의 cross-contamination 해결**. 35B 가 의미적으로 "LLVM debug 와 JSON 포맷팅은 다른 conversation" 이라고 정확히 답함.

### 5.8 Option E 의 시나리오 — 실패 모드

**Scenario E-fail-1: 35B 가 borderline 잘못 분류**

```
Request 1: "Debug LLVM segfault in foo()" → UNSAFE + NEW → 122B + sticky
Request 2: "Debug another LLVM segfault, this time in bar()"
   35B 의 판단:
     (a) CONTINUE — "LLVM debug 작업의 연속" → sticky → 122B (조금 over-stick 이지만 안전)
     (b) NEW — "다른 function 다른 버그" → 새 session → 122B (sticky 효과 없지만 routing 자체는 정확)
   둘 다 correctness 적으로 문제 없음 (UNSAFE 라 어차피 122B)
```

**Scenario E-fail-2: 35B over-CONTINUE**

```
Request 1: "Debug LLVM" → 122B + sticky bucket A
Request 2: "Also debug some MLIR stuff" 
   35B: SESSION=CONTINUE (둘 다 compiler 영역이라 묶음)
   → bucket A 재사용 → 122B  (correctness OK; LLVM/MLIR 둘 다 122B 적합)
Request 3: "Now write a haiku about debugging"
   35B: SESSION=CONTINUE? (모호한 케이스)
   if CONTINUE → 122B 로 over-escalate
   if NEW → 35B 로 routing (정확)
```

35B 의 boundary 판단이 ambiguous 케이스에서 약함. Hermes 의 실제 conversation 패턴 측정 없이 평가 어려움.

### 5.9 v2.0 timeline 평가

Option E 매력적이지만 **v2.0 scope 에 부적합**:

| 이유 | 영향 |
|---|---|
| Phase 18 plans = plan-checker PASS 상태 | Restructure 시 비용 |
| Phase 19 plans = plan-checker PASS 상태 | Prompt template + parser + cache 모두 확장 필요 |
| 35B boundary 판단의 정확도 미검증 | Hermes 실제 conversation 데이터 없이 design choice 어려움 |
| Bootstrap problem 추가 검증 | 첫 요청 처리 정책 결정 필요 |
| 통합 prompt vs separate call trade-off | benchmark 없이 결정 어려움 |
| 새 인프라 (RecentPromptBuffer) | 추가 plan 필요 |

**v2.0**: Option A 그대로 — Phase 18 ship, fingerprint default false.

**v2.x candidate**: Option E 를 Phase 21 또는 v2.1 milestone 으로 검토:
- Phase 19 selfrouter 운영 데이터 보고 결정 (얼마나 많은 borderline case? cache hit rate?)
- Fingerprint cross-contamination 이 실제 관찰 가능한 문제이면 Option E 가치 큼
- Hermes PR 이 먼저 오면 Option E 불필요 (X-Session-Id 가 conversation-scoped 이므로 충분)

### 5.10 Open questions for Option E (Phase 21 검토 시)

Phase 21 / v2.1 milestone 으로 가게 되면 planner-level 결정:

1. **Single combined prompt vs separate calls** — selfrouter 한 호출에 ROUTE + SESSION 묶기 vs 두 별도 호출
2. **Recent buffer size** — last 1, 3, 5 prompts? (메모리 vs 정확성 trade-off)
3. **Bootstrap behavior** — 첫 요청 자동 NEW 또는 fingerprint sticky fallback
4. **Parse failure handling** — safety-biased default 정책
5. **Cache strategy** — `(current_prompt_hash, recent_prompt_hash)` tuple key; recent 변할 때 cache hit rate 낮음
6. **Benchmark** — Hermes 의 실제 conversation pattern 측정 후 35B 정확도 ground truth
7. **bge-m3 embedding similarity 와 비교** — Phase 17 의 ML dormancy 와 충돌하지 않게? embedding 도 의미적 boundary 감지의 cheaper 대안 (~50ms; 35B-classify 와 latency 비슷; ML dormant 충돌)

### 5.11 Option E 의 핵심 통찰 (저장 가치)

이 분석에서 가장 중요한 깨달음:

> **Sticky escalation 의 진짜 어려움은 "client 식별" 이 아니라 "conversation 식별"** 이다. Fingerprint 는 client 만, X-Session-Id (Hermes PR) 는 conversation 까지 식별. 35B-classify 는 **inference 로 conversation 을 도출** 하는 제3의 경로.
>
> 세 접근의 가치 ranking:
> 1. **X-Session-Id (Hermes 측)**: 정확한 conversation 정보를 client 가 직접 제공. 가장 정확.
> 2. **35B-classify**: 의미적 추론. 비싸지만 X-Session-Id 없을 때 가능한 가장 좋은 대안.
> 3. **Fingerprint**: client 만 식별. 의미적 boundary 모름. 가장 약함.
>
> v2.0 의 Option A 가 (1) 을 기다리는 선택이라면, Option E 는 (2) 를 v2.x 에서 implement 하는 선택. Fingerprint (B/C/D) 는 (3) 의 약한 안전망.

---

## 6. Part 5 — 네 가지 옵션

### Option A — Plan 그대로 (변경 0)

**계획 변경:** 없음. Phase 18 + Phase 19 + Phase 20 모두 현재 plan 대로 ship.

**v2.0 ship 후 동작:**
- Phase 18 sticky escalation: **dormant** (X-Session-Id 안 옴 / fingerprint default off)
- 운영자가 `Routing.Session.FingerprintEnabled=true` 설정 + restart 하면 그제서야 sticky 작동
- 또는 미래 Hermes PR 까지 대기

**장점:**
- 계획 변경 0 — 가장 빠른 ship
- Phase 18 plans 가 plan-checker PASS — 즉시 execute 가능
- Architectural clarity — sticky 와 session_id 소스가 분리된 phase

**단점:**
- v2.0 ship 시 Phase 18 가 보이지 않음 (DecisionLog 에 `sticky_to_122b` 0 회)
- 운영자가 "왜 sticky 안 발화하지?" 묻기 전에 Phase 20 config + restart 필요
- 자료가 의도한 debugging continuity 가 ship 직후 미실현
- Phase 18 의 9 reqs 가 "shipped but unused" 로 표시됨

**누구한테 맞나:**
- "v2.0 의 paradigm 자체가 selfrouting 인프라 ship 이라고 명시; sticky 는 인프라; trigger 는 차차" 입장
- 운영 가치보다 architectural cleanness 우선
- Hermes PR 이 곧 올 거란 expectation

---

### Option B — Phase 20 fingerprint default flip (`true`)

**계획 변경:** Phase 20 의 `Routing.Session.FingerprintEnabled` default 만 `false` → `true` 로 변경. 다른 모든 plan 그대로.

**v2.0 ship 후 동작:**
- Phase 17 + 18 + 19 + 20 모두 ship 됨 (full v2.0 release)
- Default 설정: `FingerprintEnabled=true`
- Hermes (no X-Session-Id) 요청 시 fingerprint 추출 (`RemoteIp + UA SHA-256`)
- session_id 가 IP+UA 로 도출됨 → sticky **immediately functional**
- 운영자가 explicit opt-out (`FingerprintEnabled=false`) 만 가능

**장점:**
- v2.0 ship 시 sticky **즉시 작동** — Phase 18 가치 day-1 실현
- 계획 변경 최소 (default value 1 줄 수정)
- v2.0 deployment 가 loopback (`127.0.0.1:4000`) 만이라 IP+UA 충돌 위험 거의 없음
- Hermes PR 이 나중에 와도 X-Session-Id 가 fingerprint 보다 우선이라 자연스럽게 흡수됨

**단점:**
- v1.x 의 "stateless by default" 약속 부분적 위반 — 운영자 입장에서 sticky 가 silent on
- Multi-client loopback (여러 hermes 인스턴스 같은 host) 의 경우 동일 IP+UA → 같은 sticky bucket 공유; 흔하지 않지만 가능
- Reverse-proxy 환경 (현재는 없지만 미래) 에서 RemoteIp = proxy IP 가 되는 함정

**누구한테 맞나:**
- 실용적인 운영자 시점 — "v2.0 ship 됐는데 sticky 안 보이면 의미 없음"
- Loopback single-client 가 사실상 100% 인 경우
- "fingerprint 로 충분 / X-Session-Id Hermes PR 은 future polish" 입장

**README 메시지:**
> "v2.0 ships fingerprint-based session detection by default. For multi-client deployments or after Hermes PR ships native X-Session-Id, set `Routing.Session.FingerprintEnabled=false` to require the header explicitly."

---

### Option C — Fingerprint 를 Phase 18 로 당겨오기

**계획 변경:** Phase 18 에 fingerprint fallback 작업 추가. Phase 20 은 docs/README/CHANGELOG-only 로 축소.

**Phase 18 새 구조:**
- 18-01: Core (변경 없음)
- 18-02: Cli wiring + fingerprint helper (추가됨)
- 18-03: TTL eviction + tests (확장됨; fingerprint 테스트 추가)

또는 새 plan 추가:
- 18-04: Fingerprint fallback + tests + README §10 일부

**v2.0 ship 후 동작:**
- Phase 18 ship 시점 (= Phase 19 전) 이미 sticky **functional with fingerprint**
- Phase 20 는 docs phase — Hermes Integration §10 rewrite + CHANGELOG 마무리
- Phase 19 SelfRouter 가 sticky check 할 때 fingerprint session 이 이미 작동

**장점:**
- Phase 18 자체로 완성도 있음 — sticky 인프라 + trigger 둘 다 같은 phase 에서 ship
- Phase 19 ship 시점에 sticky 가 production trigger 가 있음 (Phase 19 cascade 가 sticky check 후 classify)
- Phase 20 가 docs-only 라 작아짐 (현재 2 plans → 1 plan)

**단점:**
- Phase 18 크기 증가 (~3 plans → ~3-4 plans)
- "Hermes Agent Integration" Phase 의 정체성이 흐려짐 (Phase 20 가 docs 전용이 됨)
- 4-phase 아키텍처의 깔끔한 narrative 가 약간 깨짐

**누구한테 맞나:**
- Phase boundary 의 "ship 가능한 단위" 정의를 엄격하게 보는 시점
- "각 phase 가 운영자에게 보이는 가치를 ship 해야 함" 원칙
- Phase 20 의 별도 phase 가치가 낮다고 판단

---

### Option D — Phase 18 + Phase 20 합치기

**계획 변경:** Phase 20 를 Phase 18 로 흡수. v2.0 = 3 phases (17/18/19).

**Phase 18 새 구조 (확장):**
- "Session Store + Sticky Escalation + Hermes Integration"
- ~5 plans 정도 (현재 18 의 3 + Phase 20 의 2)
- 18-01: Core (Domain.fs SessionId + DU)
- 18-02: SessionStore + CorrelationMiddleware + sticky cascade
- 18-03: TTL eviction + SessionStoreTests + StickyEscalationTests
- 18-04: Fingerprint fallback (HMRS-01/02 from Phase 20) + tests
- 18-05: README §10 rewrite + CHANGELOG + REQUIREMENTS HMRS-FUTURE-01 docs

**Phase 19 변경 없음**. Phase 20 사라짐.

**v2.0 ship 후 동작:**
- Option C 와 같음 — Phase 18 가 ship 되는 순간 sticky functional
- v2.0 milestone = 3 phases 로 narrative 가 더 짧고 명확

**장점:**
- v2.0 milestone 의 narrative 가 가장 깔끔 — "Hard Rules, Session-Aware Routing, Self-Classify" 3 stages
- Sticky escalation 의 entire surface (인프라 + 입력 소스 + docs) 가 한 phase 에 모임
- v2.0 ship 시 모든 sticky 가치 day-1 실현
- 전체 milestone 시간 단축 가능 (phase 4 → 3)

**단점:**
- Phase 18 가 가장 큰 phase 가 됨 (~5 plans)
- 가장 큰 재구조화 — ROADMAP + REQUIREMENTS 모두 수정 (HMRS-* → SES-* 또는 SES+HMRS 결합)
- 이미 plan-checker PASS 한 18 plans 를 다시 작성해야 함

**누구한테 맞나:**
- Milestone narrative 의 강함 우선시
- "Phase 단위 = 응집된 운영 가치 ship 단위" 원칙
- ROADMAP 재정리 비용 감수 가능

---

## 7. 비교 표

| 기준 | A (현행) | B (default flip) | C (Phase 18 확장) | D (3-phase 합침) |
|---|---|---|---|---|
| 계획 변경량 | 0 | 1 줄 (config default) | ~1 plan 추가 | ROADMAP+REQUIREMENTS 재정리 |
| v2.0 ship 시 sticky | dormant | **functional** | **functional** | **functional** |
| Phase 18 시점 trigger | 없음 (Phase 20 후 opt-in) | 없음 (Phase 20 ship 후) | **있음** | **있음** |
| Phase 19 시점 trigger | 없음 | 없음 (Phase 20 후) | **있음** | **있음** |
| Plans (v2.0 총) | 12 | 12 | 13 | 12-13 |
| Phases | 4 | 4 | 4 | 3 |
| Phase 20 의 역할 | full Hermes Integration | full Hermes Integration | docs-only | 사라짐 |
| Multi-client 위험 | N/A | 작음 (loopback) | 작음 (loopback) | 작음 (loopback) |
| Hermes PR 자연 흡수 | ✅ X-Session-Id 우선 | ✅ X-Session-Id 우선 | ✅ X-Session-Id 우선 | ✅ X-Session-Id 우선 |
| 18 PLAN.md 재작업 | 0 | 0 | 일부 (1 task 추가) | 거의 전부 |
| 사용자 통제 | "stateless by default" | "fingerprint by default" | "fingerprint by default" | "fingerprint by default" |

---

## 8. 추천

> **§4.6 의 분석이 §7 의 이전 추천을 뒤집는다.** Fingerprint 가 의미적 conversation continuity 가 아니라 client process taint 라는 깨달음을 반영한 정정된 추천:

### 최우선 추천 (정정됨): **Option A**

이전에 비추천이었으나, §4.6 의 cross-contamination 분석 후 가장 보수적이고 정직한 선택으로 재평가:

- Fingerprint 가 의미적으로 broken 한 임시 다리 — ship 안 하는 게 정직
- Phase 18 인프라는 ship 됨 (Hermes PR 시점 즉시 활성화 가능)
- v2.0 ship 시 sticky 가 "보이지 않음" 은 약점이지만, **잘못 작동하는 것보다 안 작동하는 게 낫다**
- 운영자에게 README §10 으로 명시: "X-Session-Id 헤더 옵트인 또는 Hermes-side PR 대기"
- `Routing.Session.FingerprintEnabled` 가 default `false` 로 ship 되는 = **현재 Phase 20 plan 의 정확한 상태**
- 계획 변경 0 — Phase 18 plans + Phase 20 plans 모두 그대로

### 차선 추천: **Option B (Phase 20 fingerprint 를 default 유지하되 ship)**

이건 Option A 와 거의 동일 — Phase 20 plan 그대로 ship, `FingerprintEnabled=false` default 유지:
- 운영자가 자기 사용 패턴 알고 명시 opt-in (`true` 설정 + restart)
- README §10 의 cross-contamination 위험 명시가 핵심 — 운영자가 trade-off 이해하고 enable
- "continuous-debugging 패턴" 사용자만 켜라는 가이드

> **이전 추천 (Option B with default=true) 은 §4.6 분석 후 부적절**. Default `true` 는 모든 운영자에게 over-escalation 위험을 silent 으로 강제함. 따라서 **default `false` 가 옳음**.

### Phase 18 자체에 대한 추천: **현재 plans 그대로 진행**

§4.6 분석은 Phase 18 인프라 ship 자체는 비판하지 않음. 비판은 fingerprint 를 default trigger 로 쓰는 것에 대한 것. 따라서:
- Phase 18 plans (plan-checker PASS 상태) 그대로 `/gsd:execute-phase 18` 진행
- SES-01..09 모두 구현
- 결과: sticky 인프라 in place, X-Session-Id 우선순위 동작 (헤더 보내면 즉시 활성)

### 비추천: **Option C, D**

- Phase 18 에 fingerprint 를 당겨오는 것 (C/D) 도 fingerprint 의 의미적 한계는 똑같음
- 계획 재구조화 비용만 추가; 운영 가치 추가는 미미
- Hermes PR 의 미래에 X-Session-Id 가 들어오면 자연 해결되므로 굳이 Phase 18 시점에 fingerprint 강제 활성화할 이유 없음

### 비추천: **Option B (default flip to true)**

§4.6 분석 후 이 옵션은 cross-contamination 위험을 silent 강제. 운영자 명시 opt-in 이 옳음.

### 정리

| 옵션 | §4.5 평가 (이전) | §4.6 평가 (정정) | 변화 |
|---|---|---|---|
| A | 비추천 | **최우선 추천** | ⬆️ 큼 |
| B (default=true) | 최우선 추천 | 비추천 | ⬇️ 큼 |
| B (default=false 유지) | (변형 아님) | **차선 추천** | NEW |
| C | 차선 | 비추천 | ⬇️ |
| D | 비추천 | 비추천 | 변화 없음 |

**가장 정직한 한 줄 결론**: **현재 모든 plans 그대로 진행** (Phase 18 ship + Phase 20 의 `FingerprintEnabled=false` default 유지). 운영자가 자기 사용 패턴 보고 명시 opt-in 결정. Hermes PR (HMRS-FUTURE-01) 이 진짜 해법이고, 그게 올 때까지 sticky 는 부분 활성 상태로 둔다.

---

## 9. 의사결정 시 고려할 점

1. **v2.0 ship timeline**: 빠를수록 A 또는 B 가 유리 (재작업 적음)
2. **Hermes PR 의 ETA**: 곧 (~수주) 이면 A 도 수용 가능; 멀거나 미정이면 B/C/D 가 가치 큼
3. **Operator UX 우선순위**: "config 안 만지고 즉시 작동" 이 중요하면 B/C/D
4. **Architectural narrative**: 4-phase 의 깔끔함 vs 3-phase 의 간결함
5. **Multi-client 시나리오의 미래 가능성**: 현재 loopback single-client; 미래에 reverse-proxy 면 fingerprint 가 broken — README documented 로 충분한가?

---

## 10. 결정 후 후속 작업

| 옵션 | 후속 작업 |
|---|---|
| A | 없음 — `/gsd:execute-phase 18` 바로 진행. README §10 (Phase 20) 에 sticky dormancy 명시. |
| B | Phase 20 plans 의 fingerprint default 를 `true` 로 변경 (1 줄). README §7 documented opt-out instructions. `/gsd:execute-phase 18` 바로 진행. |
| C | Phase 18 plans 에 fingerprint task 추가 (18-04 또는 18-03 확장). REQUIREMENTS.md SES-* + HMRS-01/02 재매핑. Phase 20 plans 를 docs-only 로 축소. plan-checker 재실행. |
| D | ROADMAP v2.0 재구조화 (4-phase → 3-phase). REQUIREMENTS.md HMRS-* → SES-* 통합. Phase 20 plans 삭제. Phase 18 plans 대폭 확장 (~5 plans). plan-checker 재실행. STATE.md 업데이트. |

---

## 11. Cross-references

- `.planning/phases/18-session-store-and-sticky-escalation/18-RESEARCH.md` — Phase 18 세부 분석; 10 RouterRequest 위치; cascade 충돌 해결
- `.planning/research/SUMMARY.md` — Hermes 가 현재 session header 안 보냄 (researcher actual source code 분석)
- `.planning/research/PITFALLS.md` §3 — null session_id 가 store entry 안 만드는 invariant
- `.planning/docs/35b-selfrouting.md` §16 — sticky escalation 의 원래 의도 (debugging continuity)
- `.planning/REQUIREMENTS.md` HMRS-01/02 — Phase 20 fingerprint requirements (default false)
- `.planning/REQUIREMENTS.md` HMRS-FUTURE-01 — Hermes 측 PR (Hermes Agent 가 X-Session-Id 보내도록) — v2.x 트래커
- v1.x precedent: Phase 12 (heuristic SOFT-PAUSED — 코드는 retain, runtime 비활성) — Option A 의 "dormant infrastructure" 패턴이 v1.x 에서도 적용된 사례
