---
created: 2026-05-08
description: F# task{} finally가 do!를 거부하지만 동기 호출은 허용한다 — DisposeAsync 우회와 plain Release 중 무엇을 쓸지
---

# F# `task {}` finally에서 자원 해제하기

`task {}` CE의 `finally` 블록은 `do!` (비동기 바인딩)을 거부한다 (FS0750). 하지만 **동기 호출은 그냥 통과한다**. 이 차이를 모르면 "F#의 finally는 비동기를 못 쓴다"는 오해로 불필요한 우회를 만들게 된다.

## The Insight

`finally` 안에서 컴파일러가 막는 건 `do!`/`let!` 그 자체일 뿐, 안에 들어가는 함수 호출이 동기인지 비동기인지는 보지 않는다.

- `SemaphoreSlim.Release()` → **동기** (`int` 반환). `finally`에서 그냥 호출 가능.
- `IAsyncDisposable.DisposeAsync()` → **비동기** (`ValueTask` 반환). `do!` 없이는 awaitable이 그대로 버려짐.

같은 "정리"라도 동기/비동기에 따라 처리 패턴이 다르다.

## Why This Matters

이걸 모르면:
1. 동기 정리(예: `sem.Release()`)를 굳이 try/with-each-arm으로 풀어 쓴다 — 코드가 부풀고 정리 누락 위험이 생긴다.
2. 비동기 정리(예: `enumerator.DisposeAsync()`)를 `finally`에 넣으려다 컴파일이 안 되고, 잘못된 대안(예: `.GetAwaiter().GetResult()`을 hot path에 끼워 넣어 데드락 위험)을 만든다.

이 프로젝트의 SSE 스트리밍 코드(Phase 2)는 이걸 잘못 진단하고 모든 catch arm마다 `DisposeAsync`를 명시적으로 넣었다. 동기 `Release()`도 같은 패턴으로 갈 뻔했지만 Phase 3에서 그냥 `finally`에 넣어도 되는 게 확인됐다.

## Recognition Pattern

다음 상황 중 하나에 들어가 있으면 이 글을 읽어야 한다:
- F# `task {}` 안에서 자원을 정리하려는데 컴파일러가 `do!`를 막을 때
- 컴파일러 오류 `FS0750`(`do!`이 허용되지 않는 위치)을 본 적 있을 때
- 비동기 정리를 `finally` 대신 모든 분기(success/error/cancel)에 손으로 복제하고 있을 때

## The Approach

정리할 자원이 **동기**인지 **비동기**인지 먼저 확인한다.

| 자원 종류 | 정리 호출의 반환 타입 | 패턴 |
|---|---|---|
| `SemaphoreSlim` | `int` (동기) | `try ... finally x.Release() \|> ignore` |
| `IDisposable` | `unit` (동기) | `use x = ...` 또는 `try ... finally x.Dispose()` |
| `IAsyncDisposable` | `ValueTask` (비동기) | 각 분기에서 `do! x.DisposeAsync()`을 명시 |
| `HttpClient.SendAsync` 응답 | `IDisposable` (동기) | `use _ = resp` (또는 `taskSeq` 안에서도 동일) |

**비동기 정리에서 hot path 데드락을 피하라.** `.GetAwaiter().GetResult()`은 테스트 teardown 같은 짧고 격리된 블록에서만 쓴다 — request handler에 끼우면 thread pool starvation이 따른다.

### Step 1: 정리 함수의 시그니처를 본다

```fsharp
// SemaphoreSlim
type SemaphoreSlim with
    member Release : unit -> int   // 동기

// IAsyncEnumerator
type IAsyncEnumerator<'T> with
    member DisposeAsync : unit -> ValueTask   // 비동기
```

### Step 2: 동기면 `finally`에 그냥 넣는다

```fsharp
task {
    do! sem.WaitAsync(ct)
    try
        return! work ct
    finally
        sem.Release() |> ignore   // ← 이게 컴파일 된다
}
```

### Step 3: 비동기면 각 분기에서 명시적으로 호출

```fsharp
task {
    let enumerator = stream.GetAsyncEnumerator(ct)
    let mutable result = Ok []
    try
        try
            while! enumerator.MoveNextAsync() do
                // 처리
                ()
        with ex ->
            result <- Error ex
        // 정상 종료 후 dispose
        do! enumerator.DisposeAsync()
        return result
    with ex ->
        // 예외 경로의 dispose도 명시
        do! enumerator.DisposeAsync()
        return Error ex
}
```

`finally`에 `do! DisposeAsync()`을 넣고 싶지만 컴파일이 안 된다 → 위 패턴이 강제되는 이유다.

## Example

같은 "스트림 forwarder"의 두 정리:

```fsharp
// 동기 자원 (SemaphoreSlim) — finally에 그냥 넣는다
let processWithGate (sem: SemaphoreSlim) (ct: CancellationToken) =
    task {
        do! sem.WaitAsync(ct)
        try
            return! upstreamCall ct
        finally
            sem.Release() |> ignore
    }

// 비동기 자원 (IAsyncEnumerator) — 각 경로에서 명시 호출
let forwardStream (stream: IAsyncEnumerable<string>) (ct: CancellationToken) =
    task {
        let enumerator = stream.GetAsyncEnumerator(ct)
        try
            // ... iterate ...
            do! enumerator.DisposeAsync()   // 정상 종료
            return Ok ()
        with
        | :? OperationCanceledException ->
            do! enumerator.DisposeAsync()   // 취소 경로
            return Error Cancelled
        | ex ->
            do! enumerator.DisposeAsync()   // 예외 경로
            return Error (Failure ex.Message)
    }
```

## 체크리스트

- [ ] 정리 함수의 반환 타입 확인 (동기 vs 비동기)
- [ ] 동기면 `finally x.Release() |> ignore` 1줄로 해결되는지 검토
- [ ] 비동기면 success/cancel/error 각 분기에서 `do! DisposeAsync()` 호출
- [ ] hot path에서 `.GetAwaiter().GetResult()` 쓰고 있다면 thread pool 영향 검토
- [ ] `taskSeq {}` 안에서도 동일하게 적용된다 (`finally` 동기 OK, 비동기 분기 명시)

## 관련 문서

- `debug-kestrel-request-aborted.md` — Kestrel 취소 토큰의 발화 시점이 다른 이유
