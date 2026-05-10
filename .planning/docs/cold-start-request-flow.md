# Cold-start (학습 전) Request Flow — 로그 동선

**작성:** 2026-05-10
**대상:** smart-router 가 fresh clone / 첫 startup 상태 (`models/router.zip` 미존재) 에서 어떻게 동작하는지 알고 싶은 운영자.

## 사전 정정 — 사용자 멘탈 모델 vs 실제 동작

질문: "35B 에서 처리 안 되어서 122B 로 다시 처리되는 경우"

이런 **재시도 (escalation) 경로는 smart-router 에 존재하지 않는다.** 라우팅은 **요청당 한 번** 만 결정된다. 코드 안에서 "35B 실패 → 122B 재시도" 분기는 0 곳:

```bash
grep -rn "FallbackTo122B\|escalate.*122B" src/   # → 0 hits
```

실제 fallback 방향은 **반대** 다 (Phase 10):
- 122B 가 unreachable → 35B 로 transparent reroute (`Reason=FallbackTo35B`, `IsFallback=true`)
- 35B 가 unreachable → 503 에러 (graph_indexing 류 task 는 specifically not downgraded)

따라서 "두 가지 경우" 는 다음으로 재해석한다:

- **시나리오 A**: 분류기가 35B 라고 판정 → 단일 호출 → 35B 응답
- **시나리오 B**: 분류기가 122B 라고 판정 ("35B 로는 부족하다") → 단일 호출 → 122B 응답

**Cold-start 의 추가 함정**: `models/router.zip` 이 `ensureDummyModel` 로 생성된 무작위 가중치 LR 이라서 분류 결과가 **본질적으로 50/50 무작위**. prompt 의 실제 복잡도와 분류 결정은 학습 전에는 상관관계 없음. 여기서 "scenario A/B" 는 **결과 분포의 두 가지 outcome** 으로 본다 — 입력 prompt 의 의미가 아니라.

---

## Phase 1: 인프라 startup (request 도착 전)

`dotnet run --project src/SmartRouter.Cli` 또는 launchd 가 process 를 시작하면 다음 순서로 로그가 stderr (와 `logs/operational/smart-router-*.log`) 에 쌓인다:

```
00:00.000  Logging.configure(IConfiguration) — Serilog 초기화
           Console sink + File sink (logs/operational/) 둘 다 가동.
           appsettings.json:Serilog.MinimumLevel.Override 가 Microsoft.AspNetCore 를 Warning 으로 silence.

00:00.005  CompositionRoot.configureRequestPipeline (DI 등록):
           - ensureEmbeddingFilesPresent: models/embed/bge-m3-int8.onnx + sentencepiece.bpe.model 존재 확인
             ※ 미존재 시 [FTL] Required ML embedding files missing → throw → process exit
           - ensureDummyModel: models/router.zip 미존재 → 생성
             [WRN] No ML classifier model found at models/router.zip. Generating random dummy 1024-dim model.
                   Routing will be ~50/50 until Phase 7-8 generate real training data.
             [INF] Dummy classifier model written to models/router.zip
           - 그 외 adapter 등록 (HealthService, CanaryService, LogRetentionService, RetrainingService 등)

00:00.150  app.Build() 성공
00:00.155  applyLogLevelFromArgs args (--log-level 처리)
00:00.160  validateConfig (Routing.TaskTable 정합성)

00:00.200  Microsoft.Hosting.Lifetime: Now listening on: http://127.0.0.1:4000
           Microsoft.Hosting.Lifetime: Application started.

00:00.210  HealthService starting (BackgroundService)
00:00.220  HealthService: Qwen35B reachable (transitioned from down)        ← 첫 probe 결과
00:00.230  HealthService: Qwen122B reachable (transitioned from down)

00:00.250  CanaryService: starting; canary_version=(none)                   ← models/router-canary.zip 미존재
00:00.260  RetrainingService: starting; interval=60min, count_trigger=500
00:00.270  LogRetentionService starting; poll_interval_minutes=60

00:00.300  [Startup banner]
           SmartRouter starting
               listen            = http://127.0.0.1:4000
               routing.algorithm = ml
               model.version     = ml-0de83f0e          ← 더미 SHA-8 (cold-start)
               canary.version    = (none)
               canary.percent    = 10
               queue.maxconc.122B = 1
               queue.fairnessK   = 10
               teacher.cap.daily = 1000
               log.dir           = logs/operational
```

이 시점에 `/stats baseline_model_version` 도 `"ml-0de83f0e"`. `models/router.zip` SHA-256 의 첫 4 byte 가 무작위라서 cold-start 마다 다른 값이 나온다.

---

## Phase 2: 공통 request 흐름 (시나리오 A & B 동일)

운영자가 `curl -X POST localhost:4000/v1/chat/completions ...` 를 보내면:

```
00:01.000  [DBG] Microsoft.AspNetCore.Routing — endpoint matched          (← Override 로 Warning 차단; 안 보임)

# 1. CorrelationMiddleware (pipeline 의 첫 middleware)
00:01.001  cid 생성: 32-hex (예: a3f8c2d1e9b74a5f8c2d1e9b7a8d5e2c)
           HttpContext.Items["CorrelationId"] = cid
           LogContext.PushProperty("correlation_id", cid)
           Response.OnStarting → X-Correlation-Id: a3f8c2d1... header 등록

# 2. ChatCompletions.fs handler 진입
00:01.002  ILoggerFactory.CreateLogger("ChatCompletions") 로 logger 획득
           [DBG] /v1/chat/completions hit; correlation_id captured  (─log-level=debug 시에만)

# 3. Wire body → RouterRequest 매핑
00:01.003  body JSON parse (FsToolkit.SystemTextJson + FSharpConverter)
           RouterRequest { Messages=[...]; ModelOverride=None; Task=None; Stream=false; CorrelationId="a3f8c2d1..."; ... }

# 4. routeRequest config algorithm req — 3-stage pure pipeline
00:01.004  Stage 1: tryModelOverride
           req.ModelOverride = None → skip
           Stage 2: tryTaskTable
           req.Task = None → Ok None (fall through)
           Stage 3: algorithm config req → ML.makeApplyML closure
              a. correlationId="a3f8c2d1..." → canaryGate.IsCanaryAsync 호출
                 (cold-start 에서 canary 파일 없음 → File.Exists 체크에서 false 반환; FM 안 부름)
              b. isCanary = false
              c. classifier = baselineClassifier
              d. modelVersion = versionProvider.CurrentVersion = "ml-0de83f0e"     ← live read (issue #12 fix 후)
              e. prompt = req.Messages |> 합쳐서 단일 string
              f. embedder.EmbedAsync(prompt) → 1024-dim float32[] (~20-30ms warm path)
              g. baselineClassifier.PredictAsync(embedding) → ClassifierPrediction { Score=?; PredictedLabel=? }
              h. target = if Score >= cfg.MlThreshold (0.5f) then Qwen122B else Qwen35B
              i. decision = { Target; Priority=Low; Reason=ML; IsFallback=false; ModelVersion="ml-0de83f0e" }
```

이 시점에 `decision.Target` 이 시나리오 A 와 B 를 가른다. cold-start 무작위 가중치이라서 **score 가 어느 쪽으로 떨어질지는 dump model 의 무작위 weight × embedding dot product 에 달려 있다 — prompt 의 의미와 무관**.

---

## 시나리오 A — 분류기 score < 0.5 → 35B 단일 호출

예: `Score = 0.3f` (무작위 가중치 결과)

```
# Phase 10 fallback 체크 (ChatCompletions.fs:243-269)
00:01.025  decision.Target = Qwen35B → 122B 미관련; fallback 분기 skip
           graph_indexing 체크 도 skip (req.Task = None)

# 라우팅 결정 로그 (Phase 13 hot-path 강등 — Debug 레벨)
00:01.026  [DBG] {ChatCompletions} cid=a3f8c2d1... Routing target=Qwen35B reason=ML priority=Low

# QueueDispatcher.CompleteAsync 진입
00:01.027  decision.Target = Qwen35B → 35B bypass (semaphore 안 거치고 직접 inner.CompleteAsync)
           QwenUpstreamClient.CompleteAsync 호출 with named HttpClient "qwen35b"
              POST http://127.0.0.1:8000/v1/chat/completions
              body: { "model": "/path/to/qwen35b", "messages": [...], ... }
              AddResilienceHandler 가 5xx/transient 시 자동 재시도 (1-2 회 backoff)

# 35B 응답 도착
00:01.500  HTTP 200; body = OpenAI-style JSON
           qwen35b 의 latency: 일반적으로 ~400-600 ms

# ChatCompletions: 응답 forwarding + DecisionLog
00:01.501  ctx.Response.WriteAsync(upstreamBody)
00:01.502  Response.OnStarting 발화 → X-Correlation-Id header 추가
00:01.503  decisionLogger.Log(buildDecisionLog ...) — Channel 에 enqueue (논블록)
              { schema_version: 1
                correlation_id: "a3f8c2d1..."
                prompt_hash: "sha256_first8..."
                prompt_korean_char_ratio: 0.0
                routing_algorithm: "ml"
                routing_reason: "ml"
                target: "Qwen35B"
                latency_ms: 502.3
                fallback_used: false
                model_version: "ml-0de83f0e"
                task_type: null
                timestamp: "2026-05-10T00:01:01.500Z" }
           DecisionLogWriter BackgroundService consumer 가 Channel 에서 dequeue → logs/decisions/2026-05-10.jsonl 에 append

# 클라이언트로 response 종료
00:01.504  HTTP 200 + JSON body + X-Correlation-Id header 가 클라이언트에 도착
           [DBG] {Stats} cid= /stats hit; queue_depth_high=0 active_122b=0  (만약 곧이어 /stats 호출 있으면)
```

요약 (시나리오 A):
- 총 latency: ~500 ms (35B inference)
- 35B 만 호출됨 (semaphore queue bypass)
- DecisionLog: target=Qwen35B, latency_ms~500, fallback_used=false, model_version=ml-0de83f0e
- 122B 무관

---

## 시나리오 B — 분류기 score ≥ 0.5 → 122B 단일 호출

예: `Score = 0.7f`. 분류기가 "이건 122B 가 처리해야 한다" 고 판정. cold-start 의 무작위 가중치 결과이지 prompt 분석 결과가 아님.

```
# 같은 Phase 1 + 2 흐름 거쳐 decision.Target = Qwen122B 까지 동일

# Phase 10 fallback 체크
00:01.025  decision.Target = Qwen122B → healthProbe.IsReachable(Qwen122B) 체크
           이 시점 122B 가 reachable (HealthService 가 startup 직후 transitioned-from-down 로 감지) → fallback 안 함

# graph_indexing 체크
           req.Task = None ≠ "graph_indexing" → 503 분기 skip

# 라우팅 결정 로그
00:01.026  [DBG] {ChatCompletions} cid=a3f8c2d1... Routing target=Qwen122B reason=ML priority=Low

# QueueDispatcher.CompleteAsync 진입 — 122B 는 큐 통과
00:01.027  Target=Qwen122B + Priority=Low → low-priority queue 에 ticket enqueue
           동시에 다른 122B 요청 0건 → SemaphoreSlim(1).WaitAsync(ct) 즉시 통과
           [DBG] {QueueDispatcher} cid=a3f8c2d1... ticket-acquired-immediate semaphore_available=0
           QwenUpstreamClient.CompleteAsync 호출 with named HttpClient "qwen122b"
              POST http://127.0.0.1:8001/v1/chat/completions
              ...

# 122B 응답 도착
00:03.500  HTTP 200; body
           qwen122b 의 latency: 일반적으로 ~2-3 초 (prompt 길이 따라 더)

# DecisionLog 기록 + 응답 forwarding (시나리오 A 와 동일한 메커니즘)
00:03.501  ctx.Response.WriteAsync(upstreamBody)
00:03.502  decisionLogger.Log(...)
              { ...
                target: "Qwen122B"
                latency_ms: 2503.7
                model_version: "ml-0de83f0e"
                fallback_used: false
                ... }

# Semaphore release (try/finally)
00:03.503  sem122b.Release() → 다음 대기 중 ticket 이 있다면 즉시 grant
           [DBG] {QueueDispatcher} cid=a3f8c2d1... released; queue_depth_high=0 queue_depth_low=0
```

요약 (시나리오 B):
- 총 latency: ~2.5 초 (122B inference + queue + semaphore)
- 122B 호출됨 (35B 안 호출)
- 동시 122B 요청 있었으면 큐에서 대기 (FairnessK=10, Priority=Low → high 가 10번 통과 후 우리 차례)
- DecisionLog: target=Qwen122B, latency_ms~2500, fallback_used=false, model_version=ml-0de83f0e
- 35B 무관

---

## "35B 실패 → 122B" 가 *실제로* 일어나는 비슷한 시나리오 (정정)

존재하지 않지만, 코드 안에서 가장 비슷한 동작은:

### (A) 35B 5xx + Polly 재시도

35B 가 transient 502 → AddResilienceHandler 가 자동 backoff 재시도 (1-2 회). **35B 만** 재시도. 122B 안 부름. 결국 다 실패하면 502 를 클라이언트에 전달.

```
[INF] {QwenUpstreamClient} cid=... POST http://127.0.0.1:8000 — HTTP 502 (attempt 1)
[INF] {QwenUpstreamClient} cid=... retrying after 1.2s
[INF] {QwenUpstreamClient} cid=... POST http://127.0.0.1:8000 — HTTP 502 (attempt 2)
[INF] {QwenUpstreamClient} cid=... retrying after 2.4s
[INF] {QwenUpstreamClient} cid=... POST http://127.0.0.1:8000 — HTTP 200 (attempt 3)
```

### (B) 122B → 35B fallback (반대 방향; Phase 10)

분류기가 122B 라고 결정했는데 (cold-start 면 무작위), HealthService 가 122B 를 unreachable 로 표시 → ChatCompletions / QueueDispatcher 가 shadow-rebind:

```
[WRN] {QueueDispatcher} cid=... 122B unreachable; rerouting task=None to 35B (fallback)
[DBG] {ChatCompletions} cid=... Routing target=Qwen35B reason=FallbackTo35B priority=Low
[DBG] {QwenUpstreamClient} cid=... POST http://127.0.0.1:8000 (fallback target)
... DecisionLog: target=Qwen35B, fallback_used=true, routing_reason=fallback_to_35b, model_version=ml-0de83f0e
```

`fallback_used=true` 가 Loop B (Phase 7+8) 의 학습 입력 신호. 이 row 는 다음 retrain 의 hard case 후보.

### (C) graph_indexing + 122B unreachable → 503 (no downgrade)

```
[WRN] {ChatCompletions} cid=... 122B unreachable + task=graph_indexing → 503 (NEVER downgrade)
HTTP/1.1 503 Service Unavailable
{"error": {"type": "model_unavailable", "message": "..."}}
```

graph_indexing 은 35B 로 reroute 하면 graph 일관성이 깨지므로 일부러 503 으로 끝낸다 (REL-04).

---

## 학습 후엔 무엇이 달라지나

cold-start 의 핵심 문제: **분류기가 무작위 가중치라 prompt 의 의미와 routing 결정 사이에 상관관계 없음.** 분포는 ~50/50.

retrain 사이클 후:
1. `datasets/hard-cases.jsonl` (Phase 7 TeacherLabeler 가 122B 로 label한 페어) 가 충분히 쌓이면
2. RetrainingService (Phase 8) 가 LR 재학습 → atomic `File.Move(overwrite=true)` 로 router.zip 덮어쓰기
3. PredictionEnginePool (`watchForChanges:true`) 가 새 모델을 hot-swap
4. `IModelVersionProvider.Update(sprintf "ml-%s" newSHA)` → 다음 request 부터 `decision.ModelVersion = "ml-{newSHA}"` (issue #12 fix)
5. 이후 분류기가 prompt 의 embedding 패턴 ↔ 라우팅 정답 사이의 통계적 관계를 반영 — 분포가 의미있는 (prompt complexity correlated) 것으로 변화

DecisionLog 에서 `model_version` 이 변하는 시점이 retrain 정착 시점:

```bash
jq -r '.model_version' logs/decisions/2026-05-10.jsonl | sort | uniq -c
# 예시
#   142 ml-0de83f0e    ← cold-start 무작위 모델 시기
#    87 ml-95267bb0    ← 첫 retrain 후 (TeacherLabeler-driven)
#    23 ml-c4a9ff21    ← 추가 retrain
```

`model_version` 변경 시점을 cohort 기준 으로 잡아 routing quality (fallback rate, latency, task-target 일치도) 를 비교 → Phase 8 Validator + Phase 9 Canary 가 자동으로 검증.

---

## 운영 체크리스트 (cold-start 진단)

- [ ] startup 로그에 `[WRN] No ML classifier model found ... Generating random dummy 1024-dim model` 이 보이는가 (cold-start 신호)
- [ ] startup banner 의 `model.version = ml-{8hex}` 가 process 재시작 시 마다 바뀌는가 (무작위 가중치 SHA)
- [ ] 첫 100 request 의 target 분포 확인:
  ```bash
  jq -r '.target' logs/decisions/$(date +%F).jsonl | head -100 | sort | uniq -c
  ```
  ~50/50 ± 10% 면 cold-start 정상. 80/20 이상으로 한 쪽 치우치면 LR fit 이 의도와 다름 (random seed 문제 등) 을 의심
- [ ] `fallback_used=true` row 가 누적되고 있는가 (Loop B 학습 데이터 축적):
  ```bash
  jq 'select(.fallback_used == true)' logs/decisions/$(date +%F).jsonl | wc -l
  ```
- [ ] retrain 후 DecisionLog 의 `model_version` 이 변경되었는가 — 위 jq 명령으로 확인 (issue #12 fix 후 live)

---

## 관련 문서

- `.planning/docs/ml-trained-vs-untrained-files.md` — 학습 전/후 어떤 파일이 다른가 (router.zip 단일)
- `documentation/howto/avoid-closure-capture-of-mutable-state.md` — model_version live read 패턴 (issue #12)
- `README.md` § 5 Routing Pipeline — 3-stage 결정 흐름 (override → task table → ML)
- `README.md` § 9 Debugging — DecisionLog 12-field schema
