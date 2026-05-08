---
created: 2026-05-08
description: SemaphoreSlim/큐로 동시성 제한된 업스트림에 백그라운드 트래픽을 보낼 때, 게이트를 통과하는 메인 클라이언트 대신 별개의 named HttpClient를 사이드채널로 등록한다
---

# 동시성 게이트를 우회하는 named HttpClient 사이드채널

업스트림이 `SemaphoreSlim(1)` 같은 단일 슬롯 게이트로 보호되어 있을 때, 백그라운드 작업(teacher labeling, retraining 데이터 수집, validation probe 등)이 같은 게이트를 공유하면 **실시간 사용자 트래픽이 굶어 죽는다**. 게이트를 통과하는 클라이언트는 빌드하지 말고, 별개의 named `HttpClient`를 등록해 사이드채널로 써라.

## The Insight

"한 업스트림 = 한 클라이언트"는 직관적이지만 잘못된 단순화다. 같은 호스트라도 **트래픽의 공정성 boundary는 클라이언트가 아니라 게이트**다. 게이트 안의 슬롯은 외부 클라이언트(우선순위 높은 사용자 요청)에게 양보되어야 하고, 백그라운드 작업은 **별도 채널**을 가져야 한다.

이는 본질적으로 "in-band 제어 트래픽 vs out-of-band 제어 트래픽"의 패턴이다. 데이터 채널(사용자 요청)과 제어/feedback 채널(retraining, monitoring)이 같은 슬롯을 두고 경쟁하면 둘 다 손해다.

## Why This Matters

게이트를 공유하면 다음 중 하나가 발생한다:

1. **사용자 요청이 굶는다** — retraining job이 슬롯을 잡고 있으면 사용자는 기다린다. cost cap이 있는 시스템(teacher labeling 1000 calls/day)에서는 사용자가 슬롯 1을 점유한 teacher 호출 1000번을 기다린다.

2. **백그라운드 작업이 굶는다** — 사용자 트래픽이 쏟아지면 retraining이 영영 실행되지 않는다. 24시간 cron이 실제로는 일주일에 한 번 도는 식.

3. **타임아웃 의미가 깨진다** — 게이트 대기시간 + 업스트림 호출시간 = 총 시간이지만, 백그라운드 작업의 30s timeout은 보통 "업스트림이 응답할 시간"이지 "게이트를 기다릴 시간"을 포함하지 않는다.

증상은 운영 단계에 가서야 보이고, 단위 테스트에서는 절대 잡히지 않는다.

## Recognition Pattern

이 패턴이 필요한 시스템:

- **Rate-limited upstream**: API quota, cost cap, 또는 메모리/GPU 제약으로 동시성 제한
- **Background labeling/training/validation**: 사용자가 모르는 사이에 도는 ML 파이프라인
- **Health probing**: 액티브 헬스체크가 사용자 트래픽과 같은 클라이언트를 쓰면 게이트 점유
- **Metrics scraping**: `/v1/models` 폴링이 inference 큐를 막으면 안 됨

특히 `IUpstreamClient` 같은 도메인 포트가 game queue + retry policy + auth header를 모두 묶어놓은 경우, 백그라운드 작업이 그 포트를 재사용하면 **자동으로 게이트에 묶인다**.

## The Approach

**원칙:** 업스트림 호스트는 같아도 **사용 의도가 다르면 클라이언트도 다르게**. DI 컨테이너에 같은 호스트로 가는 두 개의 named HttpClient를 등록하라 — 하나는 게이트(QueueDispatcher / IUpstreamClient)를 거치고, 다른 하나는 직접 호출.

### Step 1: 도메인 포트와 사이드채널을 명시적으로 분리

```fsharp
// 도메인 포트: 사용자 트래픽용, QueueDispatcher가 SemaphoreSlim(1) 게이트
type IUpstreamClient =
    abstract member SendAsync : decision: RoutingDecision * req: RouterRequest * ct: CancellationToken -> Task<...>

// 사이드채널: 백그라운드 작업용, 게이트 우회
type ITeacherLabeler =
    abstract member LabelAsync : prompt: string * cid: string * ct: CancellationToken -> Task<LabelResult>
```

`ITeacherLabeler`는 `IUpstreamClient`를 의존하지 **않는다**. 같은 122B 호스트로 가지만 게이트를 모른다.

### Step 2: named HttpClient 두 개를 등록

```fsharp
// 사용자 트래픽: QueueDispatcher가 이걸 들고 게이트 통과 후 호출
services.AddHttpClient("upstream-122b")
    .ConfigureHttpClient(fun c ->
        c.BaseAddress <- Uri(opts.Upstream122BUrl)
        c.Timeout     <- TimeSpan.FromSeconds(300.0))
    |> ignore

// 백그라운드 사이드채널: TeacherLabeler가 이걸 들고 직접 호출 (게이트 무시)
services.AddHttpClient("teacher")
    .ConfigureHttpClient(fun c ->
        c.BaseAddress <- Uri(opts.TeacherEndpoint)  // 같은 호스트:포트일 수 있음
        c.Timeout     <- TimeSpan.FromSeconds(float opts.TimeoutSeconds))
    .AddResilienceHandler("teacher-pipeline", fun builder ->
        // teacher 전용 retry policy: 4xx는 retry 안 함 (cost cap 낭비)
        builder.AddRetry(...) |> ignore)
    |> ignore
```

이름이 다르면 `IHttpClientFactory.CreateClient(name)`이 별개의 인스턴스를 돌려준다. 핸들러 파이프라인, BaseAddress, Timeout, 그리고 무엇보다 **호출 path가 다르다**.

### Step 3: 백그라운드 어댑터에서 named 클라이언트 직접 사용

```fsharp
type TeacherLabeler(httpFactory: IHttpClientFactory, options: TeacherLabelerOptions) =
    interface ITeacherLabeler with
        member _.LabelAsync(promptText, correlationId, ct) = task {
            // ✅ named "teacher" client — 게이트 없음
            let client = httpFactory.CreateClient("teacher")
            let! resp = client.PostAsync("/v1/chat/completions", body, ct)
            // ...
        }
```

### Step 4: 자가 검증 — 게이트 누수 grep

```bash
# 사이드채널 어댑터가 도메인 포트를 의존하지 않아야 함
grep -n "IUpstreamClient\|QueueDispatcher" src/SmartRouter.Cli/Adapters/TeacherLabeler.fs
# 출력: 주석에서만 등장해야 한다 — 함수 호출은 없어야 함
```

이 grep을 CI에 두면 미래에 누군가 "편의상" 도메인 포트를 재사용하는 것을 막을 수 있다.

## Example

smart-router의 실제 구조 (Phase 7, 2026-05-08):

```
사용자 요청 (POST /v1/chat/completions)
    ↓
ChatCompletions 핸들러
    ↓ IUpstreamClient (도메인 포트)
QueueDispatcher (SemaphoreSlim(1) gate, priority queue)
    ↓ HttpClient "upstream-122b"
122B (localhost:8001)
    ↑
    │ (별개 채널)
TeacherLabeler.LabelAsync (백그라운드 라벨링)
    ↓ HttpClient "teacher" (게이트 무시)
122B (localhost:8001)  ← 같은 호스트, 다른 채널
```

teacher labeling이 1000 calls/day cap에 도달하기까지 도는 동안에도 사용자 inference는 우선순위 큐에서 즉시 슬롯을 잡는다. 둘이 같은 122B를 두드리지만, 122B 자체가 동시성 제한을 가지고 있을 수 있으므로 **122B 서버 측 제약과 게이트의 의미는 분리**된다 — 게이트는 router의 정책이고, 서버 제약은 서버의 정책.

### Trade-off: 122B가 진짜 한 번에 한 요청만 처리할 수 있다면?

이 경우 두 채널이 122B 서버에서 직렬화된다 — 백그라운드 호출이 응답할 때까지 사용자 호출이 대기한다. 그래도 패턴은 유효하다:

1. **timeout이 분리되어 있다** — teacher 30s, user inference 300s. 하나가 다른 하나를 영원히 막지 않음.
2. **retry policy가 분리되어 있다** — teacher는 4xx retry 금지(cost cap), user는 transient retry 허용.
3. **메트릭이 분리되어 있다** — `model_version=teacher-call` vs `model_version=user-call`로 logging.
4. **fairness 정책이 명시적이다** — 향후 background-prio queue를 도입하더라도 분리된 클라이언트가 있어야 정책 차별화가 가능.

서버가 진짜 큐 1을 가진다면 **router에 또다른 게이트(예: per-channel SemaphoreSlim)**를 추가하면 되지만, **사이드채널은 기존 user-traffic 게이트를 공유하면 안 된다**.

## 체크리스트

- [ ] 백그라운드 작업이 `IUpstreamClient` (또는 도메인 포트)를 의존하는가? — 그렇다면 게이트에 묶인다
- [ ] DI에 named HttpClient 두 개 이상 등록되어 있는가? (`upstream-*` + `teacher`/`probe`/`monitor` 등)
- [ ] 각 named client가 자기 timeout / retry policy / handler chain을 가지는가?
- [ ] 백그라운드 어댑터에서 `IUpstreamClient` / `QueueDispatcher`를 grep으로 막는 CI 체크가 있는가?
- [ ] 122B(또는 업스트림 서버) 자체의 동시성 제약이 있다면, 그 제약을 router 정책에서 어떻게 다룰지 명시했는가?

## 관련 문서

- `build-priority-queue-on-semaphoreslim.md` — 게이트 자체의 구현 (사용자 트래픽 채널)
- `wire-fsharp-namedhttpclient-with-configurehttpclient.md` — F#에서 named HttpClient를 BaseAddress 누락 없이 등록하기
