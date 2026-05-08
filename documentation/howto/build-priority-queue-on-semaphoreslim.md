---
created: 2026-05-08
description: SemaphoreSlim 위에 priority queue를 만들 때 caller가 WaitAsync에 park하면 안 되는 이유 — sub-pattern A (dispatcher acquires THEN signals TCS) + 두 큐 + fairness counter
---

# SemaphoreSlim 위에 우선순위 큐 만들기

`SemaphoreSlim.WaitAsync`는 **FIFO**다. caller가 직접 `WaitAsync`을 호출하면 우선순위가 의미를 잃는다. 우선순위는 **별도의 게이트**에서 결정돼야 한다 — caller는 `TaskCompletionSource`에 park하고 dispatcher loop가 우선순위 순서로 깨운다.

## The Insight

우선순위 게이트는 두 단계로 나뉜다:

```
[caller]  enqueue Ticket → tcs.Task에 park (WaitAsync 호출 X)
                ↓
[dispatcher loop]  우선순위 순서로 dequeue → semaphore.WaitAsync → tcs.SetResult
                ↓
[caller]  깨어남 → 작업 수행 → finally semaphore.Release()
```

핵심은: **caller는 semaphore를 직접 만지지 않는다**. dispatcher가 acquire한 슬롯을 signal로 caller에게 넘긴다 (sub-pattern A). 이 분리로 caller의 wait queue 순서와 semaphore의 FIFO가 충돌하지 않는다.

## Why This Matters

이 분리가 없으면:
1. caller마다 `sem.WaitAsync(ct)` → semaphore의 FIFO가 우선순위를 박살낸다 (high가 low 뒤에 도착하면 그대로 뒤에서 기다림)
2. 우선순위 비교를 caller쪽에 두면 "high가 sem.WaitAsync에서 깨면 즉시 실행, low는 다시 enqueue" 같은 race를 만들기 쉽다
3. cancellation이 leak할 가능성이 커진다 — caller가 WaitAsync 중에 cancel되면 dispatcher가 dequeue 후에 alone이 슬롯을 받게 된다

이 프로젝트의 122B 게이트는 sub-pattern A로 구현됐다. caller는 `enqueue122b req` 호출 후 `do! tcs.Task`에서만 park한다. dispatcher가 acquire하고 `tcs.SetResult`로 깨운다. cancel은 ticket의 `Ct`를 dispatcher가 dequeue 시점에 검사해서 처리한다.

## Recognition Pattern

다음 중 하나가 보이면 이 패턴이 필요하다:
- 한정된 자원(local LLM 슬롯, GPU, exclusive lock)에 우선순위로 접근해야 한다
- 단일 `SemaphoreSlim` 위에서 caller priorities를 honor해야 한다
- `Channel<T>` 두 개로 풀려고 했는데 dispatcher 로직이 자꾸 복잡해진다
- 외부 의존성(Redis 등) 없이 in-process에서 두 단계 priority를 구현해야 한다

## The Approach

세 가지 구성 요소를 분리해서 둔다:

| 컴포넌트 | 역할 |
|---|---|
| **두 개의 `Queue<Ticket>`** (high, low) | 우선순위별 FIFO. lock 하나로 보호. |
| **`SemaphoreSlim(1)`** (또는 N) | 실제 자원 게이트. dispatcher만 만진다. |
| **dispatcher loop** | 우선순위로 dequeue → acquire → signal caller's TCS |

### Step 1: Ticket 구조

```fsharp
type Priority = High | Low

type Ticket = {
    Tcs       : TaskCompletionSource<unit>
    Priority  : Priority
    Ct        : CancellationToken    // caller가 cancel하면 dequeue시 검사
    EnqueuedAt: DateTimeOffset
}
```

### Step 2: 우선순위 dequeue + fairness

constant high traffic 하에서 low가 starve하지 않도록 **fairness counter**를 둔다 — K번 연속 high pick 후 강제로 low pick.

```fsharp
let mutable consecutiveHighPicks = 0
let fairnessK = 10  // 설정 가능

let tryDequeueNext () : Ticket option =
    lock state (fun () ->
        // fairness: K번 연속 high 후엔 low 강제
        if consecutiveHighPicks >= fairnessK && lowQueue.Count > 0 then
            consecutiveHighPicks <- 0
            Some (lowQueue.Dequeue())
        elif highQueue.Count > 0 then
            consecutiveHighPicks <- consecutiveHighPicks + 1
            Some (highQueue.Dequeue())
        elif lowQueue.Count > 0 then
            consecutiveHighPicks <- 0
            Some (lowQueue.Dequeue())
        else None
    )
```

### Step 3: caller의 enqueue (semaphore 안 만짐)

```fsharp
let enqueue122b (priority: Priority) (ct: CancellationToken) : Task =
    let tcs = TaskCompletionSource<unit>()
    let ticket = { Tcs = tcs; Priority = priority; Ct = ct; EnqueuedAt = DateTimeOffset.UtcNow }
    lock state (fun () ->
        match priority with
        | High -> highQueue.Enqueue(ticket)
        | Low  -> lowQueue.Enqueue(ticket)
    )
    signal.Release() |> ignore  // dispatcher 깨움
    // caller는 tcs.Task에서 park; semaphore는 만지지 않음
    tcs.Task
```

### Step 4: dispatcher loop (semaphore.WaitAsync는 여기서만)

```fsharp
let dispatcherLoop () = task {
    while not stopRequested do
        do! signal.WaitAsync()       // 새 ticket 알림 대기
        match tryDequeueNext () with
        | None -> ()
        | Some t ->
            if t.Ct.IsCancellationRequested then
                t.Tcs.TrySetCanceled(t.Ct) |> ignore  // 슬롯 안 잡고 discard
            else
                do! sem122b.WaitAsync(CancellationToken.None)  // ← 여기서만 acquire
                if t.Ct.IsCancellationRequested then
                    sem122b.Release() |> ignore  // acquire 후 cancel 보면 즉시 release
                    t.Tcs.TrySetCanceled(t.Ct) |> ignore
                else
                    t.Tcs.TrySetResult() |> ignore  // caller 깨움 (caller가 이제 슬롯 보유)
}
```

### Step 5: caller의 작업 + Release

```fsharp
let workWithSlot (priority: Priority) (ct: CancellationToken) (work: unit -> Task<'T>) = task {
    do! enqueue122b priority ct  // tcs.Task에서 park
    // 여기 도달하면 dispatcher가 슬롯 잡아서 넘겨준 상태
    // 이제부터 release할 책임은 caller에게
    try
        return! work ()
    finally
        sem122b.Release() |> ignore  // 동기 호출, finally에서 OK
}
```

### Step 6: cancellation의 두 경로 모두 검증

테스트는 두 cancel 경로를 모두 커버한다:
1. **pre-dequeue cancel**: ticket이 큐에 있는 동안 cancel. dispatcher가 dequeue 시 `t.Ct.IsCancellationRequested` 검사하고 슬롯 안 잡고 discard.
2. **post-dequeue mid-acquire cancel**: dispatcher가 dequeue 후 `sem.WaitAsync(None)`에서 blocking 중일 때 cancel. acquire 후 다시 검사해서 슬롯 즉시 release.

두 경로 모두 끝나면 `sem.CurrentCount`가 원래 값(1)으로 돌아가야 한다.

## Example

이 프로젝트의 122B QueueDispatcher (간추림):

```fsharp
type QueueDispatcher(inner: IUpstreamClient, options: QueueOptions) =
    let highQueue = Queue<Ticket>()
    let lowQueue = Queue<Ticket>()
    let state = obj()
    let signal = new SemaphoreSlim(0)        // dispatcher wake
    let sem122b = new SemaphoreSlim(1, 1)    // 실제 게이트
    let mutable consecutiveHighPicks = 0

    let dispatcherLoop = task {
        while not stopRequested do
            do! signal.WaitAsync()
            match tryDequeueNext () with
            | None -> ()
            | Some t when t.Ct.IsCancellationRequested ->
                t.Tcs.TrySetCanceled(t.Ct) |> ignore
            | Some t ->
                do! sem122b.WaitAsync(CancellationToken.None)
                if t.Ct.IsCancellationRequested then
                    sem122b.Release() |> ignore
                    t.Tcs.TrySetCanceled(t.Ct) |> ignore
                else
                    t.Tcs.TrySetResult() |> ignore
    }

    interface IUpstreamClient with
        member _.CompleteAsync req decision ct = task {
            // 35B는 게이트 우회
            match decision.Target with
            | Qwen35B -> return! inner.CompleteAsync req decision ct
            | Qwen122B ->
                do! enqueue122b decision.Priority ct
                // 슬롯 보유; timeout은 acquire 후에 시작
                use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(float options.PerRequestTimeoutSeconds))
                use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                try
                    return! inner.CompleteAsync req decision linkedCts.Token
                finally
                    sem122b.Release() |> ignore
        }
```

## 체크리스트

- [ ] caller는 `sem.WaitAsync`을 직접 호출하지 않는다 — `tcs.Task`에만 park
- [ ] dispatcher만 `sem.WaitAsync` 호출
- [ ] 두 큐 (high/low) 분리; lock 하나로 보호
- [ ] fairness counter (K=10 정도) 두고 starvation 방지
- [ ] cancellation 두 경로 검증: pre-dequeue, post-dequeue mid-acquire
- [ ] timeout은 acquire 후에 시작 — 큐 대기 시간이 timeout budget을 태우면 안 됨
- [ ] `sem.Release()`은 `try/finally`에 (동기 호출이라 `task{}` finally에서 그대로 OK)
- [ ] 35B 같은 bypass 경로가 있으면 첫 줄에 `match decision.Target`로 분기

## 관련 문서

- `handle-fsharp-task-finally-disposal.md` — `sem.Release()`이 `finally`에 들어갈 수 있는 이유
- `setup-aspnetcore-config-override-test.md` — 큐 동작 검증 테스트 하니스
