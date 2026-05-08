---
created: 2026-05-09
description: Fake async double에 `Task.Yield()`를 강제하지 않으면 `Task.FromResult` 같은 동기-완료 task가 동시성 race를 무효화한다 — 동시 호출 두 개가 순차 실행되어 SemaphoreSlim 같은 게이트가 노출되지 않음
---

# Fake async double에 `Task.Yield()` 강제하기

`Task.FromResult(value)`는 *이미 완료된* Task를 반환한다. 동기적으로 완료되므로 `do! fake.MethodAsync()` 호출이 await 지점이 되지 않는다 — coroutine이 실제로 suspend되지 않고 직선으로 진행된다. 이 동작은 동시성 테스트에서 race condition을 무효화시킨다. 의도적으로 `do! Task.Yield()`를 fake 안에 삽입해야 진짜 비동기처럼 동작한다.

## The Insight

C# `async`/F# `task{}`의 await semantic은:

> Task가 이미 완료 상태면 await는 inline으로 진행된다 (suspension 없음). 미완료면 continuation을 등록하고 caller에게 thread를 반환한다.

production async 코드는 거의 항상 미완료 Task를 반환한다 — HTTP 호출, 파일 I/O, ML inference 모두 실제 work를 한다. 따라서 await 지점에서 진짜 suspend가 발생하고, 다른 task가 schedule될 기회가 생긴다.

테스트 더블은 종종 이 work를 단축한다 — `Task.FromResult(canned)` 또는 `Task.CompletedTask`로 즉시 반환. 이 task는 *이미 완료* 상태이므로 await가 inline 진행된다. 결과적으로 fake를 사용하는 호출자의 전체 task가 **동기적으로 실행된다** — coroutine이 한 번도 yield하지 않는다.

이게 동시성 테스트에서 문제가 된다. 두 task를 `Task.Run`으로 동시에 시작해도, 첫 번째 task가 fake를 호출하는 모든 await에서 inline 진행하면 **첫 번째 task가 끝날 때까지 두 번째 task는 시작조차 못 한다**. SemaphoreSlim/lock/mutex/race는 관찰되지 않는다.

## Why This Matters

이 함정은 false-pass 테스트로 나타난다 — 테스트가 통과하지만 실제로는 동시성 invariant가 검증되지 않은 상태:

- "concurrent triggers serialize" 테스트가 항상 pass — 실제로는 직렬화가 보장되지 않아도 통과 (fake가 sync라 두 트리거가 직렬 실행됨)
- "race condition 안 일어남" 테스트가 항상 pass — race가 unobservable이라 그렇게 보일 뿐
- production 부하 시점에 진짜 async 동작으로 race 발생 → 테스트는 통과하는데 운영에서 깨짐

이 패턴이 발견된 case (smart-router Phase 8 RetrainingTests test4, RETRAIN-05): "동시 트리거 두 개가 들어오면 한 개만 실행되고 다른 하나는 skip 로그를 찍어야 한다." FakeEmbedder가 `Task.FromResult(vec)`를 반환했더니 첫 트리거의 retrain이 통째로 sync 완료된 후 두 번째 트리거가 시작 — `SemaphoreSlim.Wait(0)`이 항상 성공 → "skip" 로그가 한 번도 안 찍힘 → 테스트 fail. SemaphoreSlim 자체는 잘 동작했지만, 테스트 환경에서 race 자체가 만들어지지 않았던 것.

## Recognition Pattern

이 함정이 의심되는 신호:

- Fake 또는 in-memory test double의 async 메서드가 `Task.FromResult` / `Task.CompletedTask`만 반환
- 동시성을 검증하는 테스트인데 race가 한 번도 트리거 안 됨 (logged events count가 예상과 다름)
- 동시 두 task가 측정 가능한 타이밍 차이 없이 실행됨 (e.g., `Task.WhenAll(t1, t2)`가 sequential time과 같음)
- ConfigureAwait / SynchronizationContext와 무관하게 항상 같은 thread에서 실행되는 fake

특히 다음 시나리오:

- BackgroundService 동시성 테스트
- SemaphoreSlim/lock skip-if-busy 검증
- Channel producer/consumer race 테스트
- Cancellation token이 mid-operation에 전파되는지 검증

## The Approach

**원칙:** Fake double에서도 **최소 한 번은 진짜 suspend point를 만든다**. `do! Task.Yield()`가 가장 가벼운 suspend (ThreadPool로 yield하고 즉시 schedule).

### Step 1: Fake 메서드 안에 `Task.Yield()` 삽입

```fsharp
// ❌ BAD — fake가 동기 완료
type FakeEmbedder(canned: float32[]) =
    interface IEmbedder with
        member _.EmbedAsync(text, ct) = Task.FromResult(canned)

// ✅ GOOD — fake가 진짜 suspend
type FakeEmbedder(canned: float32[]) =
    interface IEmbedder with
        member _.EmbedAsync(text, ct) =
            task {
                do! Task.Yield()    // 강제 suspend — race가 관찰 가능해짐
                return canned
            }
```

`Task.Yield()`는 어떤 work도 하지 않고 단지 ThreadPool로 yield한다 — 다른 task가 schedule될 기회를 만든다. continuation은 즉시 enqueue되므로 latency overhead는 microsecond 수준.

### Step 2: 검증 — race가 정말 관찰되는지

테스트에서 두 동시 task의 ordering이 다양해지는지 확인:

```fsharp
let observed = ConcurrentBag<int>()
let mkTask id = task {
    do! fake.EmbedAsync("x", CancellationToken.None) |> Task.ignore
    observed.Add(id)
}
let t1 = mkTask 1
let t2 = mkTask 2
Task.WhenAll(t1, t2).Wait()
// observed에 [1; 2] 또는 [2; 1] 둘 다 나타나는지 (interleaved)
// fake가 sync면 항상 [1; 2] 일관됨 → race 없음 → 검증 무효
```

### Step 3: 의도적으로 sync인 fake가 필요할 땐 명시

가끔은 fake가 동기적 완료여야 하는 케이스도 있다 (e.g., "캐시 hit이 즉시 응답한다" 검증). 그런 경우 코드에 명시:

```fsharp
type FakeCacheHitEmbedder(canned: float32[]) =
    /// Returns Task.FromResult — synchronous completion is intentional
    /// to model an in-memory cache hit. Do NOT use this fake for
    /// concurrency tests; pair with FakeEmbedder + Task.Yield() instead.
    interface IEmbedder with
        member _.EmbedAsync(_, _) = Task.FromResult(canned)
```

코멘트가 미래의 자신/팀에게 함정을 알려준다.

### Step 4: 다른 suspend trick들

상황에 따라 다른 suspend 방법:

- `Task.Yield()` — 즉시 yield, 가장 가벼움
- `Task.Delay(1)` — 짧은 sleep, 약간 더 강제적이지만 wall-clock time 추가 (slow tests)
- `await someRealAsyncOp()` — production-like, fake가 다른 fake를 호출할 때 자연스러움
- `await new TaskCompletionSource<>().Task` — 영원히 suspend (test에서 timeout 검증용)

대부분의 경우 `Task.Yield()`가 정답.

## Example

실제 잡힌 case (smart-router RetrainingTests test4_concurrentTriggers, 2026-05-09):

```fsharp
// ❌ BAD — 첫 retrain이 통째로 sync 실행 → 두 번째 트리거가 본 시점에는 SemaphoreSlim 비어있음
type FakeEmbedder() =
    interface IEmbedder with
        member _.EmbedAsync(_, _) =
            Task.FromResult(Array.zeroCreate<float32> 1024)

// 테스트: 두 동시 RunNowAsync — 둘 다 SemaphoreSlim.Wait(0)에서 성공 → 둘 다 retrain
// CapturingSink: "starting retrain" 2회, "skipping trigger" 0회 → assertion 실패

// ✅ GOOD — Task.Yield()로 실제 suspend point 생성
type FakeEmbedder() =
    interface IEmbedder with
        member _.EmbedAsync(_, _) =
            task {
                do! Task.Yield()   // 첫 트리거가 여기서 suspend → 두 번째가 schedule됨
                return Array.zeroCreate<float32> 1024
            }

// 이제: 첫 트리거가 SemaphoreSlim.Wait(0) 후 EmbedAsync에서 yield
//       두 번째 트리거가 schedule되어 SemaphoreSlim.Wait(0) 호출 → 실패 (semaphore taken)
//       두 번째 트리거가 "skipping trigger" 로그 → CapturingSink가 캡처
//       첫 트리거가 retrain 완료 → SemaphoreSlim.Release → 끝
// CapturingSink: "starting retrain" 1회, "skipping trigger" 1회 → assertion 성공
```

## Trade-offs

- **Test slowdown**: `Task.Yield()`는 ThreadPool roundtrip이라 microsecond 단위 latency 추가. 수천 개 fake 호출이 있는 테스트면 누적될 수 있음 — 그 경우 fake 안에서 yield 빈도를 조절 (e.g., 첫 호출에만 yield).
- **Non-determinism**: 진짜 race를 만들면 테스트가 더 flaky해질 수 있음. 이를 받아들이거나, 결정성을 위해 explicit signal (TaskCompletionSource)을 사용.
- **CPU-bound fakes**: production async가 CPU-bound (e.g., embedding 계산)인 경우 fake도 같은 걸 모방하려면 `await Task.Run(...)` 또는 `await Task.Yield()` 후 sync 작업 수행.

## 체크리스트

- [ ] Fake async 메서드가 `Task.FromResult` / `Task.CompletedTask`만 반환하는가?
- [ ] 그 fake가 동시성 테스트에서 사용되는가?
- [ ] 사용된다면 fake 안에 `do! Task.Yield()` (또는 동등한 suspend) 추가했는가?
- [ ] 두 동시 task의 logged-event 순서나 timing이 예상대로 변동하는가? (race가 진짜 관찰됨)
- [ ] 의도적으로 sync인 fake에는 코멘트로 의도 명시했는가?

## 관련 문서

- `use-semaphoreslim-not-mutex-for-async-idempotency.md` — 동시성 게이트 자체의 구현 (이 howto는 그걸 *테스트하는* 방법)
- `propagate-cancellation-through-fsharp-task-trywith.md` — F# task{} 비동기 패턴들
