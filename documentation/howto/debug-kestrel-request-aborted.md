---
created: 2026-05-08
description: ctx.RequestAborted는 token.Cancel()이 아니라 TCP 소켓 close에 발화한다 — Kestrel 취소 테스트가 hang하는 진짜 이유
---

# Kestrel `RequestAborted` 디버깅

`HttpContext.RequestAborted`는 클라이언트가 **TCP 소켓을 닫을 때** 발화한다. `CancellationTokenSource.Cancel()`을 호출해도 발화하지 않는다. 이걸 모르면 "내 cancellation 테스트가 절대 동작하지 않는다"는 미스터리에 빠진다.

## The Insight

`HttpContext.RequestAborted`은 **Kestrel 트랜스포트 레이어 신호**다 — 응답 스트림의 underlying TCP socket이 닫혔을 때 Kestrel이 발화한다. 이 토큰은 외부 `CancellationTokenSource`와 연결돼 있지 않다.

따라서 `HttpClient` 측의 `cts.Cancel()`은 클라이언트의 outgoing operation만 취소한다. socket을 명시적으로 닫지 않으면 Kestrel은 모른다.

## Why This Matters

SSE pass-through, long-poll, websocket 같은 장기 연결 핸들러는 `RequestAborted`을 watch해서 upstream 작업을 정리한다. 테스트가 이걸 못 발화시키면:
1. 클라이언트 cancel은 했는데 서버 핸들러가 계속 돌고 있다고 보인다 (사실은 핸들러가 socket close를 기다리는 중)
2. 테스트 timeout으로 hang
3. "취소가 안 되는 버그"라고 잘못 진단해서 코드를 쥐어짠다

이 프로젝트 Phase 2에서 mid-stream cancellation 테스트가 hang했다. 진짜 원인은 테스트가 `cts.Cancel()`만 호출하고 `response.Dispose()`을 안 했기 때문이었다. socket이 살아있으니 `RequestAborted`는 발화하지 않았다.

## Recognition Pattern

다음 증상 중 하나라도 보면 이 글을 읽어야 한다:
- `ctx.RequestAborted.Register(...)` 콜백이 cancel 테스트에서 절대 호출되지 않는다
- `HttpResponseMessage`을 소비하던 클라이언트의 `cts.Cancel()` 후에도 서버 측에서 stream을 계속 쓰는 게 로그에 찍힌다
- "5초 timeout" 어설션이 항상 timeout으로만 통과한다 (정상 cancel path를 한 번도 안 거침)
- ASP.NET Core minimal API + `HttpClient` 통합 테스트에서 mid-stream cancel을 검증해야 한다

## The Approach

테스트에서 클라이언트 측 cancel을 모사할 때 두 가지 신호를 분리해서 생각한다:

| 시그널 | 발화 조건 | 영향 |
|---|---|---|
| `HttpClient`의 `cts.Cancel()` | `HttpClient.SendAsync(..., ct)`이 던짐 | 클라이언트의 outgoing operation 중단 |
| `HttpContext.RequestAborted` | TCP socket close (FIN 또는 RST) | 서버 핸들러의 cleanup 트리거 |

서버 측 `RequestAborted`가 발화돼야 하는 테스트라면 **`HttpResponseMessage`을 dispose해서 socket을 닫는다**. cts cancel만으로는 부족하다.

### Step 1: 핸들러가 보는 게 뭔지 확인

```fsharp
// 서버 endpoint
app.MapPost("/stream", fun (ctx: HttpContext) ->
    ctx.RequestAborted.Register(fun () ->
        // 이게 호출되려면 socket이 닫혀야 한다
        printfn "client disconnected"
    ) |> ignore
    // ... stream forward ...
)
```

### Step 2: 테스트는 socket을 명시적으로 닫는다

```fsharp
// ❌ BAD: cts.Cancel()만으로는 RequestAborted가 발화 안 함
let cts = new CancellationTokenSource()
let! response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
let! stream = response.Content.ReadAsStreamAsync()
// 청크 몇 개 읽고...
cts.Cancel()  // 클라이언트 측만 취소; 서버는 모름
```

```fsharp
// ✅ GOOD: response.Dispose()로 socket close → RequestAborted 발화
let cts = new CancellationTokenSource()
let! response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
let! stream = response.Content.ReadAsStreamAsync()
// 청크 몇 개 읽고...
response.Dispose()  // socket close → 서버의 RequestAborted 발화
```

### Step 3: 양쪽 다 필요하면 둘 다 한다

```fsharp
cts.Cancel()        // 진행 중인 client read 중단
response.Dispose()  // socket close → server RequestAborted
```

`HttpResponseMessage`은 `IDisposable`이고 `using` 스코프에서도 닫히지만, 테스트는 소유 시점을 통제하기 위해 명시적으로 호출한다.

## Example

이 프로젝트 Phase 2의 mid-stream cancellation 테스트:

```fsharp
testCaseAsync "client disconnect aborts upstream within 5s" <| async {
    let abortFiredTcs = TaskCompletionSource<unit>()

    // 페이크 upstream Kestrel 서버 — RequestAborted 발화를 hook
    use! fakeUpstream = startFakeUpstream (fun ctx ->
        ctx.RequestAborted.Register(fun () ->
            abortFiredTcs.TrySetResult() |> ignore
        ) |> ignore
        emitChunksWith50msDelay ctx)

    use! router = startRouter (fakeUpstreamUrl = fakeUpstream.Url)
    use client = new HttpClient(BaseAddress = router.Url)

    // 스트리밍 시작
    use cts = new CancellationTokenSource()
    let! response =
        client.GetAsync("/v1/chat/completions", HttpCompletionOption.ResponseHeadersRead, cts.Token)
        |> Async.AwaitTask
    let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
    let reader = new StreamReader(stream)

    // 5개 청크 읽고 → 클라이언트 disconnect
    for _ in 1..5 do
        let! _ = reader.ReadLineAsync() |> Async.AwaitTask
        ()
    response.Dispose()  // ← socket close; RequestAborted 발화의 진짜 트리거

    // upstream의 RequestAborted가 5초 안에 발화하는지 확인
    let! fired = Async.AwaitTask(abortFiredTcs.Task.WaitAsync(TimeSpan.FromSeconds 5.0))
    Expect.equal fired () "upstream should observe disconnect within 5s"
}
```

`response.Dispose()`을 빼면 어설션이 5초 timeout으로 hang하고, "취소가 작동 안 함"으로 잘못 진단된다.

## 체크리스트

- [ ] `HttpClient` cancel 토큰과 `HttpContext.RequestAborted`가 다른 신호임을 인지
- [ ] 테스트에서 mid-stream cancel을 모사할 때 `response.Dispose()` 호출
- [ ] 서버 핸들러가 `RequestAborted.Register`로 cleanup 거는지 확인
- [ ] cancellation 테스트의 timeout이 의미 있는 시간(예: 5s, 정상 propagation의 10x)으로 설정됐는지 확인 — 너무 짧으면 CI 부하에서 flake

## 관련 문서

- `setup-aspnetcore-config-override-test.md` — in-process Kestrel 테스트 하니스
- `handle-fsharp-task-finally-disposal.md` — F# task{}의 비동기 자원 정리
