---
created: 2026-05-09
description: 비동기 idempotency 게이트는 `SemaphoreSlim(1,1).Wait(0)`을 쓴다 — `System.Threading.Mutex`는 thread affinity가 있어서 `task{}`/`async{}` await 지점을 넘어가면 다른 스레드에서 release되어 깨진다
---

# 비동기 idempotency 게이트는 `Mutex` 대신 `SemaphoreSlim(1,1)`

"한 번에 하나만 실행되는 작업"을 제어할 때 `System.Threading.Mutex`를 떠올리기 쉽지만, **Mutex는 thread affinity가 있어 비동기 코드와 호환되지 않는다**. `task{}` 또는 `async{}`에서는 `SemaphoreSlim(1, 1)`을 쓴다 — `Wait(0)`로 즉시-skip-if-busy, `WaitAsync()`로 큐잉.

## The Insight

`System.Threading.Mutex`는 OS kernel object를 감싸는 wrapper다. POSIX/Windows kernel mutex는 **acquired thread만 release할 수 있다**는 강한 invariant를 가진다 — 다른 thread가 release하면 `ApplicationException: Object synchronization method was called from an unsynchronized block of code`가 발생한다.

비동기 코드는 이 invariant와 정면 충돌한다. `task { let! _ = mutex.WaitOne(); do! someAsyncWork(); mutex.ReleaseMutex() }`에서:

1. Thread A가 `WaitOne()` 호출 → Mutex 획득
2. `do! someAsyncWork()` 도달 → state machine이 continuation 등록 후 thread A 반환
3. async work 완료 → continuation이 ThreadPool에서 실행 (보통 thread B)
4. `ReleaseMutex()` 호출 → thread B가 Mutex release 시도 → ApplicationException

`SemaphoreSlim`은 이 제약이 없다. SemaphoreSlim은 user-mode counter (Interlocked + Wait queue)이고 어떤 thread에서든 release 가능하다.

## Why This Matters

idempotency가 깨지는 시나리오는 운영 단계에서야 보인다:

- **Mutex 사용 + 운 좋은 happy path**: ThreadPool이 한가하면 await가 inline-completed되어 같은 thread에 머무는 경우가 자주 발생. 테스트에서는 잘 동작.
- **부하가 올라가면**: ThreadPool이 바빠져 continuation이 다른 thread로 dispatch → ApplicationException → BackgroundService crash → systemd/launchd가 restart → log에 "second" 같은 keyword로 검색해야 발견.

또는 더 미묘한 케이스: Mutex가 process-wide (named mutex)면 process crash 시 abandoned 상태가 되어 `WaitOne()`이 `AbandonedMutexException`으로 throw됨. 일반적으로 retraining 같은 단일-프로세스-singleton 작업에서 이런 cross-process 보호는 필요하지도 않은데, 잘못된 도구로 잘못된 문제를 푸는 셈.

증상: 빌드/단위 테스트 통과 → 운영 배포 → 부하 시점에 random crash → diagnose 어려움.

## Recognition Pattern

이 패턴이 의심되는 코드:

```fsharp
// ❌ 비동기 코드에 Mutex
let mutex = new Mutex()
task {
    if mutex.WaitOne(0) then
        try
            do! longRunningAsync()
        finally
            mutex.ReleaseMutex()  // ← 다른 thread에서 release될 수 있음
}
```

또는:

- `BackgroundService` / `task{}` / `async{}` 안에서 `Mutex`/`new Mutex()`/`Mutex.OpenExisting`이 보임
- "concurrent triggers must serialize" / "idempotency lock" / "single-instance" 같은 요구사항
- Cross-thread Mutex release 관련 ApplicationException stack trace

cross-process 보호가 정말로 필요할 때만 named Mutex를 고려 — 그리고 그 경우엔 `task{}` 안에서 쓰지 않는다 (별도 sync wrapper를 두거나, file lock 같은 다른 메커니즘을 사용).

## The Approach

**원칙:** "이 lock이 비동기 await를 가로지르는가?"를 먼저 묻는다. Yes면 SemaphoreSlim. No (순수 sync 코드)면 Mutex 또는 lock 키워드 OK.

### 패턴 1: Skip-if-busy (트리거 두 번 들어와도 한 번만 실행)

```fsharp
let gate = new SemaphoreSlim(1, 1)

let runOnce () = task {
    if not (gate.Wait(0)) then    // 즉시 try-acquire; 실패 시 false
        Log.Warning "another retrain in progress; skipping this trigger"
        return ()
    try
        do! longRunningWork()
    finally
        gate.Release() |> ignore
}
```

- `Wait(0)` (timeout=0)은 sync 호출이지만 SemaphoreSlim은 non-blocking이라 안전 (kernel transition 없음).
- `gate.Release()`는 어떤 thread에서든 OK.

### 패턴 2: Queue-and-wait (모든 트리거 결국 실행)

```fsharp
let runQueued () = task {
    do! gate.WaitAsync()   // 비동기 wait — continuation이 다른 thread여도 OK
    try
        do! longRunningWork()
    finally
        gate.Release() |> ignore
}
```

- skip 의미가 아니라 직렬화 의미. 트리거가 빨리 들어오면 큐가 자라므로 max queue size 모니터링이 필요할 수 있음.

### 패턴 3: Cross-process가 정말 필요할 때

별도 `--retrain` CLI process가 같은 router.zip을 동시에 retrain하지 않도록 보호하려면:

- File-based advisory lock (`File.Open` with `FileShare.None`)
- Process-wide named Mutex을 sync wrapper로 (await 없는 좁은 critical section만)
- 또는 단순히 "한 번에 하나만 실행"하도록 외부 orchestration 책임으로 미룸

대부분의 single-host retraining은 in-process SemaphoreSlim만으로 충분하다.

## Example

실제 결정 (smart-router Phase 8 RESEARCH.md, 2026-05-08):

```fsharp
// ❌ BAD — Phase 8 연구 초기에 검토했지만 reject
//   System.Threading.Mutex로 retraining idempotency
let retrainMutex = new Mutex(initiallyOwned = false, name = "smart-router-retrain")

override _.ExecuteAsync(stoppingToken) =
    task {
        while not stoppingToken.IsCancellationRequested do
            do! Task.Delay(TimeSpan.FromHours(1.0), stoppingToken)
            if retrainMutex.WaitOne(0) then    // ← acquired by current thread
                try
                    do! runRetrain stoppingToken   // ← await crosses to ThreadPool thread
                finally
                    retrainMutex.ReleaseMutex()    // ← ApplicationException possible
    }

// ✅ GOOD — Phase 8 implementation
let private retrainGate = new SemaphoreSlim(1, 1)

override _.ExecuteAsync(stoppingToken) =
    task {
        while not stoppingToken.IsCancellationRequested do
            do! Task.Delay(TimeSpan.FromHours(1.0), stoppingToken)
            if not (retrainGate.Wait(0)) then
                Log.Warning "skipping trigger: another retrain in progress"
            else
                try
                    do! runRetrain stoppingToken
                with
                | :? OperationCanceledException as oce ->
                    ExceptionDispatchInfo.Capture(oce).Throw()
                | ex ->
                    Log.Error(ex, "retrain failed; will retry next tick")
                gate.Release() |> ignore
    }
```

테스트에서 동시성 검증:

```fsharp
// 두 동시 트리거 — 하나만 통과, 다른 하나는 skip log
let t1 = service.RunNowAsync(CancellationToken.None) :> Task
let t2 = service.RunNowAsync(CancellationToken.None) :> Task
Task.WhenAll(t1, t2).Wait()

// CapturingSink로 Serilog 캡처
let starts = capturedLogs |> Seq.filter (fun m -> m.Contains "starting retrain") |> Seq.length
let skips  = capturedLogs |> Seq.filter (fun m -> m.Contains "skipping trigger") |> Seq.length
Expect.equal starts 1 "exactly one retrain ran"
Expect.isGreaterThan skips 0 "second trigger logged a skip"
```

## Trade-offs

- **In-process only**: SemaphoreSlim은 같은 process 내 thread만 보호한다. 다른 process가 같은 file/모델을 만지면 별도 보호가 필요.
- **Memory pressure**: SemaphoreSlim은 각 wait당 small allocation (queue node). Skip-if-busy 패턴 (`Wait(0)`)은 wait queue를 사용하지 않아 영향 없음.
- **Lost release on exception**: `try/finally`에서 `Release` 빼먹으면 데드락. F# `use` binding은 SemaphoreSlim에 직접 적용 안 되니 `try/finally` 필수.

## 체크리스트

- [ ] `Mutex`를 `task{}` 또는 `async{}` 안에서 쓰고 있는가? → SemaphoreSlim으로 교체
- [ ] Skip-if-busy 의도면 `Wait(0)` (sync, non-blocking)
- [ ] Queue-and-wait 의도면 `WaitAsync()` (async)
- [ ] `try/finally`로 `Release` 보장하는가?
- [ ] cross-process 보호가 정말 필요한가? 아니면 in-process로 충분한가?

## 관련 문서

- `propagate-cancellation-through-fsharp-task-trywith.md` — `task{}` try/with에서 OperationCanceledException 통과시키기 (위 예시의 ExceptionDispatchInfo 패턴)
- `build-priority-queue-on-semaphoreslim.md` — SemaphoreSlim의 다른 사용 패턴 (priority queue 구현)
