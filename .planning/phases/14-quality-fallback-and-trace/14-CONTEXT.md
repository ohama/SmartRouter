# Phase 14 — Quality-Based Fallback (35B → 122B retry) + Trace Infrastructure

**Locked:** 2026-05-10
**Source:** distillation 디자인 (`~/projs/smart-router-distillation/`) 의 "Failure = Gold Data" 패턴; smart-router 첫 implement.

<domain>
## Phase Boundary

distillation 의 quality-based fallback 디자인을 smart-router 에 도입한다. 35B 가 응답을 줬는데 quality 기준 미달이면 자동으로 122B 로 retry. **non-streaming 만 적용** (streaming 은 chunk 가 이미 client 로 흘러가서 retract 불가).

함께 도입: 운영자가 fallback 동작을 검증할 수 있는 두 도구 — (a) `--cold-start` CLI flag (timestamp backup 후 fresh dummy 모델로 재시작) + (b) `--trace-responses` CLI flag + 별도 `logs/trace/` 파일 (response excerpt + final target + fallback flag). prompt UID = 기존 `prompt_hash` 첫 12 hex.

테스트는 위 두 도구를 활용해 fake-Kestrel 35B/122B 로 두 시나리오 (35B-only success / quality fallback) 를 log 검사 기반으로 검증.

</domain>

<decisions>
## Locked Decisions

### Q1 — Phase 구조: 단일 Phase 14, 6 plans, 5 waves

4 항목 모두 35B↔122B 동작 이해 + 검증 이라는 단일 테마. 5 waves:

- Wave 1 (parallel): **14-01** cold-start CLI + **14-02** prompt UID + trace logging — 인프라 (item 3, 4)
- Wave 2: **14-03** Core types — `RoutingReason.FallbackTo122B` + `isBadResponse` 헬퍼
- Wave 3: **14-04** ChatCompletions 핸들러 quality fallback branch (item 1)
- Wave 4: **14-05** 테스트 — 위 인프라 활용 (item 2)
- Wave 5: **14-06** README + cold-start-flow doc 갱신

이 순서는 사용자의 명시적 요청 ("3, 4 먼저 → 그 위에 2 테스트") 을 반영. impl (item 1) 은 14-03/04 로 분할되어 14-02 와 14-05 사이에 위치.

### Q2 — Streaming 처리: skip

`stream=true` 요청은 quality fallback 적용 **안 함**. SSE chunk 가 이미 `FlushAsync` 로 client 로 송출된 후에는 retract 불가. ChatCompletions 의 streaming branch 는 변경 없음 (chunk forwarding 그대로). non-streaming branch 만 quality check + retry.

Hermes 가 거의 streaming 으로 호출하므로 production 대다수 트래픽은 quality fallback 비대상. Graphify 와 manual curl 류 (non-streaming) 가 대상.

### Q3 — DecisionLog 표현: routing_reason 값만 추가

schema_version=1 유지. **새 routing_reason 값 `"fallback_to_122b"`** 추가. `fallback_used=true` 도 같이. 다음 표가 fallback 두 종류 구별:

| 시나리오 | routing_reason | fallback_used | initial target | final target |
|---|---|---|---|---|
| 정상 ML 라우팅 | `"ml"` | false | (route 결과) | (route 결과) |
| 122B 다운 → 35B (Phase 10) | `"fallback_to_35b"` | true | Qwen122B | Qwen35B |
| **35B quality 미달 → 122B (이 phase)** | `"fallback_to_122b"` | true | Qwen35B | Qwen122B |
| graph_indexing + 122B 다운 → 503 | (DecisionLog 안 적힘 — 503 직전 종료) | n/a | n/a | n/a |

`target` 필드는 **final target** (응답을 실제로 produce 한 모델). `routing_reason` 가 fallback 종류를 구별. Phase 7 FailureDetector 는 `fallback_used=true` 만 보면 두 종류 다 hard case 후보. retraining 은 둘 다 학습 신호로 활용.

### Q4 — `--cold-start` CLI: backup-then-fresh

**플래그**: `dotnet run --project src/SmartRouter.Cli -- --cold-start`

**동작 (단일 startup 안에서 모두 처리; exit 안 함):**

1. timestamp = `DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")`
2. `models/router.zip` 존재하면 → `models/router.zip.cold-start-backup-{timestamp}` 로 mv
3. `models/router.zip.prev` 존재하면 → 같은 패턴으로 mv
4. `datasets/hard-cases.jsonl` 존재하면 → `datasets/hard-cases.jsonl.cold-start-backup-{timestamp}` 로 mv (옵션; 학습 데이터 보존이지만 cold-start 의 의미가 dataset 까지 reset 이라면 필수)
5. `datasets/training-set.jsonl` 존재하면 → 같은 패턴으로 mv
6. logger.LogInformation("Cold-start backup completed: timestamp={Ts}, files=[...]")
7. 정상 startup 진행 → ensureDummyModel 가 router.zip 미존재 인식 → 새 dummy 생성

**복구**: 운영자가 backup 파일을 mv 로 다시 가져오고 router 재시작.

**보존 정책**: backup 파일은 자동 삭제 안 함. operator 가 수동 정리 (또는 LogRetentionService 가 향후 다룰 수 있음 — Phase 15+ candidate).

### Q5 — `isBadResponse` heuristic: 설정 가능

기본값 (distillation 패턴):
- 응답 길이 < 30 chars
- 응답에 `"TODO"` 포함 (case-insensitive 인지 case-sensitive 인지: case-sensitive 시작; 운영자가 keyword 에 `"todo"` 도 추가 가능)
- 응답에 `"I think"` 포함

**appsettings.json 새 섹션:**

```json
"Routing": {
  ...,
  "QualityFallback": {
    "Enabled": true,
    "MinResponseLength": 30,
    "BadKeywords": [ "TODO", "I think" ]
  }
}
```

**isBadResponse 함수 위치**: `src/SmartRouter.Cli/Adapters/QualityCheck.fs` (NEW). Pure F# function: `string * QualityFallbackOptions -> bool`. 테스트 친화적.

**Enabled=false** 시 quality fallback 자체 disable — ChatCompletions 가 분기 자체를 skip. 위험 시 즉시 끄기.

### Q6 — Trace logging 출력: 별도 `logs/trace/YYYY-MM-DD.jsonl`

**플래그**: `dotnet run --project src/SmartRouter.Cli -- --trace-responses`

**의미**: 켜져 있으면 모든 chat-completions 요청에 대해 `logs/trace/YYYY-MM-DD.jsonl` 에 한 row 추가. 꺼져 있으면 file 자체 안 만듦.

**Schema:**
```jsonc
{
  "schema_version": 1,
  "correlation_id":  "abc12345...",            // DecisionLog 와 동일
  "prompt_uid":      "a3f8c2d1e9b7",           // prompt_hash 첫 12 hex (Q7)
  "prompt_hash":     "a3f8c2d1e9b7...",        // SHA-256 전체 (DecisionLog 와 동일)
  "prompt_excerpt":  "explain recursion ...",  // prompt 첫 200 chars (PII 의식; 부분만)
  "initial_target":  "Qwen35B",                // 라우팅 결정의 첫 target
  "initial_response_excerpt": "TODO ...",      // 35B 응답 첫 500 chars (fallback 발화 시; null otherwise)
  "fallback_kind":   "quality" | "availability" | null,
  "final_target":    "Qwen122B",               // 응답을 실제 produce 한 모델
  "final_response_excerpt": "Recursion is ...",// 최종 응답 첫 500 chars
  "total_latency_ms": 2105.4,
  "timestamp":       "2026-05-10T15:23:01.234Z"
}
```

**비활성 default**: 운영 환경에서 prompt + response 가 file 에 평문으로 쌓이는 것은 PII 위험. 운영자가 디버깅/검증 시 명시적으로 켬.

**파일 retention**: 기본 30일 (LogRetentionService 가 정리; 향후 phase 에서). Phase 14 에는 retention 안 추가.

**Writer 패턴**: `TraceLogger : BackgroundService` — Channel + 단일-writer (DecisionLogWriter / HardCaseDatasetWriter mirror). DI 등록은 `--trace-responses` flag 가 켜진 경우에만 (configureRequestPipeline 분기).

### Q7 — Prompt UID: prompt_hash 첫 12 hex

기존 `prompt_hash` (Phase 5 LOG-01: SHA-256 of concatenated message contents) 의 첫 12 hex 를 그대로 UID 로 사용. 새 필드 안 추가; trace JSONL 에는 표시용으로 `prompt_uid` 가 들어가지만 derived value (`prompt_hash[:12]`).

**운영자 사용:**
```bash
# UID 생성 (prompt 모르는 경우 trace 에서 lookup):
PROMPT="explain recursion in Python"
UID=$(echo -n "$PROMPT" | sha256sum | cut -c1-12)
echo $UID    # → 예: a3f8c2d1e9b7

# UID 로 trace 검색:
jq "select(.prompt_uid == \"$UID\")" logs/trace/$(date +%F).jsonl
# → 35B response, fallback_kind, final_target, final response 모두 표시

# DecisionLog 에서 같이 보고 싶으면 (UID 가 prompt_hash[:12] 이라 prefix 검색):
jq "select(.prompt_hash | startswith(\"$UID\"))" logs/decisions/$(date +%F).jsonl
```

12 hex = 48 bits = ~280조 조합. 프로젝트 trace 규모에 충분; collision 시 prompt_hash 64-hex 로 disambiguate.

</decisions>

<specifics>
## Specific Implementation Notes

### Wave 구조 + 의존성

```
Wave 1 (parallel):
  14-01 cold-start CLI       (depends: [])
  14-02 prompt UID + trace   (depends: [])

Wave 2:
  14-03 Core types           (depends: ["14-01", "14-02"])  ← user-ordering; 파일 충돌 없음

Wave 3:
  14-04 ChatCompletions      (depends: ["14-03"])

Wave 4:
  14-05 Tests                (depends: ["14-01", "14-02", "14-04"])

Wave 5:
  14-06 Docs                 (depends: ["14-04", "14-05"])
```

14-01 vs 14-02 파일 충돌: Program.fs 양쪽이 만짐. **Wave 1 안에서는 plan 들이 서로 다른 시점에 commit 하면 됨** (gsd-executor 가 직렬화). gsd-executor 는 각 plan 에 atomic commit 을 만들어 race 안 함. 단, 동시에 Program.fs 의 다른 부분을 만지는 게 명확해야.

- 14-01: Program.fs 시작 부분 + 새 backup 함수 (or `Adapters/ColdStart.fs` 신규)
- 14-02: Program.fs 의 `applyLogLevelFromArgs` 패턴 옆에 `applyTraceFlagFromArgs` 추가 + 새 `Adapters/TraceLogger.fs`

만약 race 위험 보이면 14-01 → 14-02 sequential 로 떨어뜨려도 됨. plan-checker 가 판단.

### 14-01: `--cold-start` 구현

**파일:**
- `src/SmartRouter.Cli/Adapters/ColdStart.fs` (NEW) — `runColdStartBackup (logger: ILogger) : unit`
- `src/SmartRouter.Cli/Program.fs` — args 파싱 + 호출 (Logging.configure 후, configureRequestPipeline 전)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Compile 항목

**runColdStartBackup 본문:**

```fsharp
module SmartRouter.Cli.Adapters.ColdStart

open System
open System.IO
open Microsoft.Extensions.Logging

let runColdStartBackup (logger: ILogger) (rootDir: string) : unit =
    let timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
    let candidates = [
        Path.Combine(rootDir, "models/router.zip")
        Path.Combine(rootDir, "models/router.zip.prev")
        Path.Combine(rootDir, "datasets/hard-cases.jsonl")
        Path.Combine(rootDir, "datasets/training-set.jsonl")
    ]
    let backedUp =
        candidates
        |> List.choose (fun src ->
            if File.Exists(src) then
                let dst = sprintf "%s.cold-start-backup-%s" src timestamp
                File.Move(src, dst)
                Some dst
            else
                None)
    if List.isEmpty backedUp then
        logger.LogInformation("Cold-start: no existing files to backup; ensureDummyModel will generate fresh router.zip")
    else
        logger.LogInformation(
            "Cold-start: backed up {Count} file(s) with timestamp={Ts}: {Files}",
            backedUp.Length, timestamp, String.concat ", " backedUp)
```

`rootDir` = `Environment.CurrentDirectory` (default) OR `AppContext.BaseDirectory` walk-up via Phase 13's path resolution. Operator 가 launchd 환경 (WorkingDirectory 설정) 에서도 동작하도록 CWD 신뢰. dev 환경 (CWD = src/SmartRouter.Cli/) 에서도 그 안의 models/ 만 backup — 의도된 동작 (operator 가 repo root 에서 실행할 때만 영향).

**Program.fs 통합:**

```fsharp
[<EntryPoint>]
let main args =
    try
        try
            // ... existing Logging.configure ...
            
            // Phase 14: --cold-start 처리
            if args |> Array.contains "--cold-start" then
                let logger = ... // bootstrap-window logger
                let rootDir = Environment.CurrentDirectory
                Adapters.ColdStart.runColdStartBackup logger rootDir
                // backup 후 정상 startup 진행 (return 안 함)

            if args |> Array.contains "--retrain" then
                ...
```

`--cold-start` 가 `--retrain` 보다 먼저 처리됨. 두 플래그 동시 사용 가능 (cold-start 후 retrain 시도 — 의미 없지만 검증 차원에서 허용).

### 14-02: prompt UID + trace logging

**파일:**
- `src/SmartRouter.Cli/Adapters/TraceLogger.fs` (NEW) — `ITraceLogger` 인터페이스 + 구현 (BackgroundService + Channel)
- `src/SmartRouter.Cli/CompositionRoot.fs` — `--trace-responses` flag 가 있을 때 ITraceLogger 등록
- `src/SmartRouter.Cli/Program.fs` — args 파싱 + flag 보관 (CompositionRoot 가 IConfiguration / 보관소 통해 인지)
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — handler 가 ITraceLogger 가 등록되어 있으면 .Log 호출 (옵셔널 dependency)
- `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — Compile 항목

**ITraceLogger interface:**

```fsharp
type TraceRecord = {
    schema_version              : int
    correlation_id              : string
    prompt_uid                  : string
    prompt_hash                 : string
    prompt_excerpt              : string
    initial_target              : string
    initial_response_excerpt    : string option
    fallback_kind               : string option
    final_target                : string
    final_response_excerpt      : string
    total_latency_ms            : float
    timestamp                   : DateTimeOffset
}

type ITraceLogger =
    abstract member Log : TraceRecord -> unit  // 비동기; 내부 Channel
```

**flag 보관 메커니즘**: `IConfiguration.AddInMemoryCollection(dict ["Trace:Enabled", "true"])`. CompositionRoot 가 그 키를 읽어 등록 분기.

**Trace JSONL 의 `prompt_uid` 추출**: Phase 5 의 `computePromptHash` 함수 재사용 후 첫 12 char.

**Excerpt truncate**: `s.Substring(0, min s.Length 500)` + `"…"` suffix (truncated 표시).

**ChatCompletions 통합**: handler 가 `ctx.RequestServices.GetService<ITraceLogger>()` 로 옵셔널 resolve — null 이면 trace 안 함. 등록되어 있으면 quality fallback 발화 여부와 무관하게 모든 request 에 대해 Log 호출.

### 14-03: Core types

**파일:**
- `src/SmartRouter.Core/Domain.fs` — `RoutingReason.FallbackTo122B` DU case 추가 (지금 5 cases → 6 cases)
- `src/SmartRouter.Cli/Adapters/DecisionLogger.fs` — `formatReason` exhaustive match에 새 arm `| FallbackTo122B -> "fallback_to_122b"` 추가
- `src/SmartRouter.Cli/Adapters/QualityCheck.fs` (NEW) — pure F# `isBadResponse` + `QualityFallbackOptions` record
- `src/SmartRouter.Cli/CompositionRoot.fs` — `RoutingOptions` 에 `QualityFallback : QualityFallbackOptions` 필드 추가; appsettings.json 바인딩

**Domain.fs 변경:**

```fsharp
type RoutingReason =
    | ExplicitModelOverride of requestedAlias: string
    | ExplicitTask          of taskType: TaskType
    | Default
    | ML
    | FallbackTo35B
    | FallbackTo122B   // NEW Phase 14
```

**TreatWarningsAsErrors**: 기존 모든 match 사이트가 catch-all `| r -> ...` 또는 exhaustive `| ML | FallbackTo35B | ... -> ...` 형태인지 14-03 verify 단계가 grep 으로 확인. `formatReason` 만 명시적 match 이고 거기에 arm 추가하면 됨.

**QualityCheck.fs:**

```fsharp
module SmartRouter.Cli.Adapters.QualityCheck

[<CLIMutable>]
type QualityFallbackOptions = {
    Enabled            : bool
    MinResponseLength  : int
    BadKeywords        : string array
}

/// Pure F#: check if response body is "bad" by configured heuristic.
/// Returns false if QualityFallback is disabled (Enabled = false).
let isBadResponse (opts: QualityFallbackOptions) (response: string) : bool =
    if not opts.Enabled then false
    elif response.Length < opts.MinResponseLength then true
    elif opts.BadKeywords |> Array.exists response.Contains then true
    else false
```

**appsettings.json 추가:**

```json
"Routing": {
  ...,
  "QualityFallback": {
    "Enabled": true,
    "MinResponseLength": 30,
    "BadKeywords": [ "TODO", "I think" ]
  }
}
```

**RoutingOptions 확장**: `QualityFallback : QualityFallbackOptions` 필드. CompositionRoot 의 `Configure<RoutingOptions>` 가 자동 binding.

### 14-04: ChatCompletions handler quality fallback

**파일:**
- `src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — non-streaming branch 에 quality check + retry 추가
- `src/SmartRouter.Core/CanaryPorts.fs` 또는 `MLPorts.fs` — 변경 없음

**non-streaming branch (line ~365-395) 수정 sketch:**

```fsharp
// 현재 (line ~371): upstream.CompleteAsync 가 35B 응답 반환
let! result = upstream.CompleteAsync req decision ctx.RequestAborted
match result with
| Error _ -> ... // existing error handling
| Ok body ->
    // ── Phase 14: Quality fallback (35B → 122B retry) ──────────────────
    let qualityFallbackTriggered =
        decision.Target = Qwen35B
        && routingOpts.QualityFallback.Enabled
        && QualityCheck.isBadResponse routingOpts.QualityFallback body
    
    let (finalDecision, finalBody) =
        if qualityFallbackTriggered then
            logger.LogInformation(
                "ChatCompletions: 35B response failed quality check; retrying on 122B; cid={Cid}",
                correlationId)
            let retryDecision = {
                decision with
                    Target = Qwen122B
                    Reason = FallbackTo122B
                    IsFallback = true
            }
            // 122B 가 reachable 한지 확인 — 122B 도 down 이면 35B response 그대로 돌려줌 (no infinite loop)
            if not (healthProbe.IsReachable(Qwen122B)) then
                logger.LogWarning(
                    "ChatCompletions: quality fallback triggered but 122B unreachable; returning 35B response as-is")
                (decision, body)
            else
                let retryResult = upstream.CompleteAsync req retryDecision ct |> ... .Result
                match retryResult with
                | Ok retryBody -> (retryDecision, retryBody)
                | Error _ -> (decision, body)  // 122B 도 실패하면 35B 응답 그대로
        else
            (decision, body)
    
    // 응답 forwarding + DecisionLog
    do! ctx.Response.WriteAsync(finalBody, ctx.RequestAborted)
    let okReason = formatReason finalDecision.Reason
    decisionLogger.Log(buildDecisionLog req regn versionProvider correlationId started 
                       (Some finalDecision) (sprintf "%A" finalDecision.Target) okReason finalDecision.IsFallback)
    
    // Trace logging (옵셔널)
    match traceLogger with
    | Some tl ->
        let initialResponseExcerpt =
            if qualityFallbackTriggered then Some (truncate 500 body) else None
        let fallbackKind =
            if qualityFallbackTriggered then Some "quality"
            elif decision.IsFallback then Some "availability"
            else None
        tl.Log({
            schema_version = 1
            correlation_id = correlationId
            prompt_uid = computePromptHash req.Messages |> fun h -> h.Substring(0, 12)
            prompt_hash = computePromptHash req.Messages
            prompt_excerpt = req.Messages |> List.tryHead |> Option.map (fun m -> truncate 200 m.Content) |> Option.defaultValue ""
            initial_target = sprintf "%A" decision.Target
            initial_response_excerpt = initialResponseExcerpt |> Option.map (truncate 500)
            fallback_kind = fallbackKind
            final_target = sprintf "%A" finalDecision.Target
            final_response_excerpt = truncate 500 finalBody
            total_latency_ms = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            timestamp = DateTimeOffset.UtcNow
        })
    | None -> ()
```

(실제 코드는 plan 14-04 가 더 정밀하게 작성. 이건 sketch.)

**streaming branch (line ~283-360)**: 변경 없음. quality fallback 안 함. 명시적 주석 추가 — "streaming 에 quality fallback 미적용 (chunk 가 이미 client 로 송출됨; retract 불가)".

### 14-05: Tests using cold-start + trace UID

**파일:**
- `tests/SmartRouter.Tests/QualityFallbackTests.fs` (NEW) — 2 testCase + helper

**테스트 시나리오:**

**Test 1: 35B 좋은 응답 → fallback 안 함**

1. fake-Kestrel 35B 띄움. response = `"Recursion is a function calling itself..."` (길이 > 30, "TODO" "I think" 미포함)
2. router 띄움 (configureWithoutMl + 가짜 ML이 35B 라우팅하도록 stub)
3. POST `/v1/chat/completions` body `{ "messages": [...], "stream": false }` (--trace-responses 켠 상태로 router 시작)
4. `prompt_uid = sha256(prompt)[:12]` 계산
5. `logs/trace/<date>.jsonl` 에서 `select(.prompt_uid == UID)` 로 row 찾음
6. assert: `initial_target = "Qwen35B"`, `final_target = "Qwen35B"`, `fallback_kind = null`, `initial_response_excerpt = null`
7. DecisionLog row: `target = "Qwen35B"`, `routing_reason = "ml"`, `fallback_used = false`

**Test 2: 35B "TODO" 응답 → 122B fallback 발화**

1. fake-Kestrel 35B response = `"TODO: implement this"` (> 30 chars 이지만 "TODO" 포함)
2. fake-Kestrel 122B response = `"Recursion is a function..."` (good)
3. 같은 router 셋업 (35B 라우팅 stub + 122B health probe stub returns true)
4. POST `/v1/chat/completions` (stream=false; --trace-responses 켜짐)
5. UID 로 trace row 찾음
6. assert:
   - `initial_target = "Qwen35B"` (라우팅 결정 시점)
   - `initial_response_excerpt` = "TODO: implement this..." (truncate)
   - `fallback_kind = "quality"`
   - `final_target = "Qwen122B"`
   - `final_response_excerpt` = "Recursion..." (truncate)
7. DecisionLog row: `target = "Qwen122B"`, `routing_reason = "fallback_to_122b"`, `fallback_used = true`

**테스트 보조: cold-start 가 dummy 모델 보장 — 무작위지만 stub 으로 35B 라우팅을 강제하니 cold-start 필수 아님. testSequenced + temp dir 패턴.**

테스트는 ML 모델 파일 불필요 (stub-classifier 로 35B forced). bge-m3 ONNX 도 불필요 (configureWithoutMl pattern + manual stub registration).

### 14-06: README + cold-start-flow doc 갱신

**파일:**
- `README.md` — §5.4 "Tuning ML routing" 다음에 §5.5 "Quality fallback" 추가; §9 logging 에 `--trace-responses` + `logs/trace/` 추가; §12 Operations 에 `--cold-start` 명시
- `.planning/docs/cold-start-request-flow.md` — Phase 14 후 quality fallback 이 real 이라는 점 반영; "35B 처리 안 되어서 122B 로" 시나리오 가 이제 실제로 존재함을 명시
- `.planning/docs/distillation-fallback-design-references.md` — Phase 14 가 distillation 의도를 가져왔음 명시; gap 표 업데이트 (이제 일치)

### Test count delta 예상

- `QualityFallbackTests.fs` 2 새 testCase
- 다른 파일 영향 없음 (단, DecisionLog 의 `routing_reason` 가능값 변경에 따른 LoggingTests assertion 점검 필요할 수 있음 — `"fallback_to_122b"` 는 새 값이라 기존 assertion 깨질 일 없음)

새 baseline: 80 → **82 passed + 16 ignored + 0 failed**.

### Constraint inheritance

- ARCH-01 (Pure-Core BCL only): 유지. RoutingReason DU case 추가는 BCL only. QualityCheck.fs 는 Cli (`isBadResponse` 가 string operations only — Cli 적합).
- TreatWarningsAsErrors=true: DecisionLogger.formatReason exhaustive match 변경 시 모든 사이트 점검.
- F# `task {}` only: 유지. quality fallback 의 retry 호출도 task block 안에서.
- Per-task atomic commits, never `git add -A`: 유지.
- testSequenced wrapper: 새 QualityFallbackTests 도 적용.
- OBS-04 (Serilog → stderr only): 변동 없음.
- Streaming OBS-04 invariant 보존 (stream branch 무수정).

### Claude's discretion

- ColdStart.fs 안에 `runColdStartBackup` 만 둘지 다른 helper 도 둘지
- 14-04 의 정확한 코드 layout (qualityFallbackTriggered let-binding 위치 등)
- TraceLogger 내부 Channel capacity (DecisionLogWriter mirror 면 1000)
- Health probe 를 quality fallback 이 사용하는 정확한 호출 패턴 (sync IsReachable vs async IsReachableAsync)
- `truncate` helper 의 위치 (Adapters/Json.fs 일반화 vs ChatCompletions 내 inline)

</specifics>

<deferred>
## Deferred Ideas

- **Streaming quality fallback** — chunk-level 가능하지만 client UX 깨짐. 향후 phase 시 careful design 필요. distillation 도 streaming 안 다룸.
- **Claude API 를 quality scorer 로** — 더 정확하지만 cost + latency 증가. v2 candidate.
- **Trace JSONL retention** — Phase 13 LogRetentionService 가 logs/trace/ 도 정리하도록 확장. 1줄 추가하면 됨; Phase 14 종료 후 follow-up.
- **fallback_kind 명시적 schema 필드** — 현재 routing_reason 으로 구별; 향후 schema_version=2 시 명시화 가능.
- **Quality scoring metrics** (Prometheus) — v2; CanaryWatchdog 와 통합되어 quality fallback rate 도 트래킹.
- **per-prompt 캐시 (동일 prompt 의 fallback 결과 reuse)** — 학습 신호 측면에서 의미 있음. v2.
- **Cold-start backup 자동 정리** — LogRetentionService 가 N 일 초과 backup 파일 prune. 1줄 추가; deferred.
- **Operator 친화적 cold-start CLI: dry-run mode** — `--cold-start --dry-run` 으로 어떤 파일이 backup 될지 미리 확인. v2 polish.

</deferred>

---

*Phase: 14-quality-fallback-and-trace*
*Context locked: 2026-05-10*
*Source: distillation idea/ docs (Phase 14 가 처음 implement); user 의 4 항목 요구사항 + 7 lock 결정*
