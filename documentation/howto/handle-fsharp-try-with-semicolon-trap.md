---
created: 2026-05-08
description: F# `try X with _ -> (); Y`는 Y가 예외 분기에만 실행된다 — 세미콜론은 with-clause 안쪽으로 묶임
---

# F# `try/with` 세미콜론 파싱 트랩

`try X with _ -> (); Y`는 보기엔 "X 실패해도 Y는 항상 실행"처럼 읽히지만, 실제로는 **Y가 예외 분기 안쪽에 묶인다**. 정상 분기에서는 Y가 실행되지 않는다.

## The Insight

F#의 `try ... with` 식은 **expression**이지 statement가 아니다. `with` 패턴 절의 본문은 그 다음 `let`이나 다른 식의 시작을 만나기 전까지 계속 이어진다. 세미콜론은 본문을 종료시키지 않고 **분기 안쪽에서 시퀀싱**한다.

```fsharp
// 작성한 사람의 의도: X를 try, Y는 항상 실행
try X with _ -> (); Y

// 실제 파싱:
try X with _ -> (X 실패 시: () 다음 Y)
// 정상 분기에서 Y는 실행되지 않음
```

## Why This Matters

리소스 정리 코드가 happy-path에서 실행되지 않는다 — `StreamWriter.Dispose()`, lock 해제, 파일 close 등이 누락되면 핸들 누출이 발생하고 다음 쓰기에서 `IOException: The process cannot access the file...`로 보인다.

증상은 조용하다 — 빌드 통과, 정상 흐름 테스트 통과, 에러 케이스 테스트도 통과 (예외 분기에서는 Y가 실행되니까). **장시간 실행 후 핸들 고갈**이나 **테스트 간 파일 잠금 충돌**로 표면화된다.

## Recognition Pattern

이 트랩이 의심되는 코드 패턴:

```fsharp
// "한 줄로 try/with + cleanup" 형태가 보이면 의심
fun w -> try w.Flush() with _ -> (); w.Dispose()

// 또는 여러 cleanup을 세미콜론으로 잇는 형태
try doWork() with _ -> log("failed"); cleanup1(); cleanup2()
```

특히 다음 상황에서 자주 발생한다:

- `Option.iter` / `List.iter` / 람다 안에서 한 줄로 cleanup 작성
- C#의 `try { X; } catch { } finally { Y; }`를 F#으로 옮길 때 직역
- `using` 대신 명시적 `Dispose()`를 호출할 때

## The Approach

**원칙:** "always-run cleanup이 try/with 옆에 붙어 있으면 분기에 묶일 수 있다"고 의심하라. 시각적으로 한 줄에 보여도 컴파일러는 `with` 본문의 끝을 다음 식의 시작으로 판단하므로, **명시적 분리**가 유일한 방어다.

세 가지 안전한 형태가 있다:

### Form 1: 두 개의 try/with로 분리

```fsharp
fun w ->
    try w.Flush() with _ -> ()
    try w.Dispose() with _ -> ()
```

각 cleanup이 자기 try/with를 가지면 항상 실행된다.

### Form 2: try/finally 사용

```fsharp
fun w ->
    try
        try w.Flush() with _ -> ()
    finally
        w.Dispose()
```

`finally`는 분기와 무관하게 실행된다. cleanup이 throw할 가능성이 없으면 `try/with` 없이 바로 `finally`에 넣어도 된다.

### Form 3: `use` binding (가장 안전)

```fsharp
let writeAll path contents =
    use w = new StreamWriter(path: string)
    contents |> Seq.iter w.WriteLine
    // scope 끝에서 자동 Dispose
```

리소스가 함수 스코프 안에서 살고 죽는다면 `use`가 정답이다. 람다 안에서 명시적으로 정리해야 하는 경우(BackgroundService drain phase처럼 lifetime이 비대칭일 때)만 Form 1/2를 쓴다.

## Example

실제로 잡힌 버그 (smart-router HardCaseDatasetWriter, 2026-05-08):

```fsharp
// ❌ BAD: Dispose가 예외 분기에만 실행됨
writer |> Option.iter (fun w ->
    try w.Flush() with _ -> (); w.Dispose())

// 파싱 결과:
// fun w ->
//     try w.Flush()
//     with _ ->
//         (); w.Dispose()  // ← Dispose는 여기 안쪽
```

증상: 정상 종료 시 `StreamWriter`가 닫히지 않아 다음 테스트가 같은 파일을 열 때 `IOException`. 단위 테스트는 시퀀셜 + 임시 디렉토리라 운이 좋아 통과했지만, 50-concurrent integrity 테스트에서 간헐적 실패.

```fsharp
// ✅ GOOD: 두 개의 try/with로 분리
writer |> Option.iter (fun w ->
    try w.Flush() with _ -> ()
    try w.Dispose() with _ -> ())
```

이제 `Flush()`가 throw해도 `Dispose()`가 항상 실행된다.

## 검증법

수정 후 의도대로 동작하는지 확인하려면:

```fsharp
let probe () =
    let mutable disposed = false
    try
        try failwith "boom" with _ -> ()
        disposed <- true
    finally ()
    disposed  // true이면 always-run, false이면 분기에 묶인 상태
```

`true`가 나와야 정상.

## 체크리스트

- [ ] `try ... with _ -> (...); X` 패턴이 코드에 있는가?
- [ ] 람다(`fun w -> ...`) 안에 한 줄 try/with + cleanup이 있는가?
- [ ] cleanup이 happy-path와 error-path 모두에서 실행되는가? (의도 확인)
- [ ] 가능하면 `use` binding으로 대체했는가?
- [ ] 그렇지 않으면 두 개의 try/with 또는 try/finally로 분리했는가?

## 관련 문서

- `handle-fsharp-task-finally-disposal.md` — `task {}` finally에서 동기/비동기 정리 분기 (F# task computation expression의 finally 제약)
