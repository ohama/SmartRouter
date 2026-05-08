---
created: 2026-05-09
description: F# `task{}` 안의 nested try/with에서 `reraise()`는 FS0413으로 거부된다 — `ExceptionDispatchInfo.Capture(ex).Throw()`로 stack trace를 보존하며 재던진다
---

# F# `task{}` try/with에서 OperationCanceledException 통과시키기

`task { try ... with ex -> reraise() }`는 컴파일되지 않는다 — F# 컴파일러는 `task{}` computation expression의 with-clause 안에서 `reraise()`를 거부한다 (FS0413). cancellation을 isolation 블록 위로 통과시키려면 `System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()`를 쓴다.

## The Insight

`reraise()`는 CIL 명령어 (`rethrow`)로 직접 매핑되는데, 이건 **catch 핸들러 본문 안에서만** 유효하다. F#의 `try/with`가 imperative 코드 안에 있으면 컴파일러가 직접 `rethrow` 명령어를 생성한다 — 동작한다.

`task {}` computation expression은 다르다. `try/with`가 state machine으로 변환되면서 with-clause 본문이 별도 메서드로 분리된다. 그 메서드는 catch 핸들러 안이 아니라 catch 핸들러 *밖에서* 호출되므로, `rethrow` CIL 명령어가 의미를 잃는다. 컴파일러가 이를 감지하고 FS0413으로 거부한다.

`ExceptionDispatchInfo.Capture(ex)`는 예외 객체 + 원본 stack trace를 함께 캡처한다. `.Throw()`는 그 정보를 보존하며 다시 던지는 — `reraise()`의 의미적 등가물인데 catch 컨텍스트와 무관하게 어디서든 호출 가능하다.

## Why This Matters

`BackgroundService.ExecuteAsync` 같은 long-running 비동기 루프에서 isolation 패턴이 흔하다:

```fsharp
override _.ExecuteAsync(stoppingToken: CancellationToken) =
    task {
        while not stoppingToken.IsCancellationRequested do
            try
                do! runOneCycle stoppingToken    // 한 사이클이 throw해도 루프는 계속
            with
            | :? OperationCanceledException -> reraise ()  // ← FS0413! 컴파일 안됨
            | ex -> Log.Error(ex, "cycle failed; will retry next tick")
    }
```

의도: graceful shutdown은 즉시 propagate하고, 다른 모든 예외는 isolate한다.
문제: `reraise()`가 task{} 안에서 컴파일 거부됨. 차선책으로 `raise ex`를 쓰면 stack trace가 잘려 디버깅이 더 어려워진다.

증상: 빌드 실패. 컴파일러 에러 메시지는 명확하지만, "왜 reraise가 안 되지?"에서 막히면 ad-hoc 우회로 (예: `raise ex` 사용 → stack trace 손실, 또는 catch 자체를 제거 → isolation 깨짐)를 만들기 쉽다.

## Recognition Pattern

이 패턴이 필요한 신호:

- F# `task {}` 안에 `try/with`가 있고, with-clause 안에서 어떤 예외만 통과시키고 싶을 때
- 빌드 에러: `error FS0413: A 'rethrow' statement may only be used directly in a handler of a try-with`
- BackgroundService / Channel consumer / long-running coroutine에서 cancellation 처리
- "OperationCanceledException is graceful; everything else is recoverable" isolation 패턴

`async {}`에서는 `reraise()`가 동작하지만, `task {}`에서는 안 된다 — F# 6+에서 도입된 `task{}` CE는 다른 state machine 변환을 사용한다.

## The Approach

**원칙:** 통과시키고 싶은 예외 타입을 명시적으로 catch한 뒤, `ExceptionDispatchInfo.Capture(ex).Throw()`로 다시 던진다. 그 호출은 절대 정상적으로 return하지 않으므로 컴파일러가 후속 코드를 dead로 인지한다.

### Step 1: 필요한 namespace open

```fsharp
open System.Runtime.ExceptionServices
```

### Step 2: with-clause에서 ExceptionDispatchInfo로 재던짐

```fsharp
try
    do! runOneCycle stoppingToken
with
| :? OperationCanceledException as oce ->
    ExceptionDispatchInfo.Capture(oce).Throw()
    // 도달 불가능, 컴파일러는 알지만 unit 식이 필요한 경우 () 추가
    ()
| ex ->
    Log.Error(ex, "cycle failed; will retry next tick")
```

### Step 3: 검증

빌드 통과 + Cancellation 시 stack trace 보존 확인:

```fsharp
// 테스트: stoppingToken.Cancel() 호출 → ExecuteAsync에서 OCE 발생
// → catch에서 Capture+Throw → outer task가 OCE로 await 실패
// stack trace가 원래 throw 지점부터 보존되어 있어야 함
```

`raise ex` 차선책이라면 stack trace가 catch 위치부터 다시 시작된다. `Capture+Throw`는 원본을 유지한다.

## Example

실제 잡힌 케이스 (smart-router RetrainingService.fs, 2026-05-09):

```fsharp
// ❌ BAD — F# FS0413, 컴파일 거부
override _.ExecuteAsync(stoppingToken: CancellationToken) =
    task {
        while not stoppingToken.IsCancellationRequested do
            try
                do! runRetrain stoppingToken
            with
            | :? OperationCanceledException -> reraise ()
            | ex -> Log.Error(ex, "retrain failed; next tick will retry")
    }

// ✅ GOOD — Capture+Throw로 OCE 통과
open System.Runtime.ExceptionServices

override _.ExecuteAsync(stoppingToken: CancellationToken) =
    task {
        while not stoppingToken.IsCancellationRequested do
            try
                do! runRetrain stoppingToken
            with
            | :? OperationCanceledException as oce ->
                ExceptionDispatchInfo.Capture(oce).Throw()
            | ex ->
                Log.Error(ex, "retrain failed; next tick will retry")
    }
```

## C#과 비교

C#의 `async/await`도 같은 제약이 있다 — `throw;` (재던짐) 대신 `ExceptionDispatchInfo`를 쓰는 게 권장된다 (catch 핸들러 밖으로 호출 위치를 옮길 때):

```csharp
catch (OperationCanceledException oce)
{
    ExceptionDispatchInfo.Capture(oce).Throw();
    throw;  // unreachable, compiler appeasement
}
```

F# `task{}`도 동일한 의도를 가진다 — state machine 분리 때문에 `rethrow` 직접 호출이 안 되는 것.

## 차선책과 트레이드오프

- **`raise ex`** — 컴파일은 통과하지만 stack trace가 catch 시점에서 끊긴다. 디버깅이 어려워짐. 절대 권장하지 않음.
- **catch 자체 제거** — isolation이 깨지고 다른 예외가 BackgroundService를 죽인다. RETRAIN-06 같은 isolation 요구사항을 위반.
- **`async {}` 변환** — F# `async`는 `reraise()`를 허용한다. 하지만 ASP.NET Core/Microsoft.Extensions.Hosting은 `Task` 기반이라 변환 비용이 크고, `task{}`가 codebase 컨벤션이면 일관성이 깨진다.

## 체크리스트

- [ ] `task {}` 안의 `try/with`에서 `reraise()`를 쓰고 있는가? (FS0413 발생)
- [ ] 그렇다면 `open System.Runtime.ExceptionServices` 추가했는가?
- [ ] Capture+Throw 패턴으로 교체했는가?
- [ ] Stack trace 보존이 중요하면 `raise ex` 같은 차선책 대신 Capture+Throw를 썼는가?
- [ ] OperationCanceledException은 통과시키고 다른 예외는 swallow하는 isolation 의도가 코드에서 명확한가?

## 관련 문서

- `handle-fsharp-task-finally-disposal.md` — F# task{} finally에서 동기/비동기 정리 (다른 task{} 제약)
- `handle-fsharp-try-with-semicolon-trap.md` — `try X with _ -> (); Y`의 분기 binding trap (sync try/with)
