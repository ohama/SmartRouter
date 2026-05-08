---
created: 2026-05-08
description: F#에서 named HttpClient의 BaseAddress/Timeout 설정 시 `services.AddHttpClient(name, fun c -> ...)` 대신 `.ConfigureHttpClient(...)` 체인을 써라
---

# F#에서 named HttpClient를 ConfigureHttpClient 체인으로 등록하기

`services.AddHttpClient("teacher", fun c -> c.BaseAddress <- Uri(url))`은 F#에서 **조용히 실패한다** — `BaseAddress`가 설정되지 않은 채로 등록되어 `CreateClient("teacher")`가 `localhost`로 가는 클라이언트를 돌려준다. `.AddHttpClient(name).ConfigureHttpClient(...)` 체인을 써라.

## The Insight

`AddHttpClient`에는 여러 오버로드가 있다:

- `AddHttpClient(name: string)` — 인자 1개
- `AddHttpClient(name: string, configureClient: Action<HttpClient>)` — 인자 2개
- `AddHttpClient(name: string, configureClient: Action<IServiceProvider, HttpClient>)` — 인자 2개 (다른 시그니처)

F# 람다 `fun c -> ...`는 본질적으로 `FSharpFunc<HttpClient, unit>` 타입이고, **C#의 `Action<T>`로의 자동 변환은 컴파일러 경로에 따라 동작이 달라진다**. 같은 이름의 오버로드가 여러 개 있을 때 F#은 람다를 잘못된 오버로드에 묶거나, 변환에는 성공하지만 호출 시 람다가 실제로 실행되지 않는 경로를 선택할 수 있다.

증상: 빌드 성공, 등록 성공, `CreateClient("teacher")`도 성공 — 그런데 반환된 `HttpClient`의 `BaseAddress`가 `null`이다.

## Why This Matters

실패가 조용하다. `httpClient.SendAsync(request)`에서 `RequestUri`가 absolute URL일 때만 동작하므로, 상대 경로(`/v1/chat/completions`)를 쓰는 순간 `InvalidOperationException: An invalid request URI was provided. Either the request URI must be an absolute URI or BaseAddress must be set.` 가 난다. 또는 fake-Kestrel 테스트에서 무작위 포트(`127.0.0.1:0`)를 쓰는 경우, BaseAddress 누락이 `127.0.0.1` (포트 미지정)으로 나가면서 connection refused로 보인다.

런타임에서야 처음 보이는 이슈고, 단위 테스트가 fake 서버를 의존성 주입으로 받지 않으면 발견이 늦다.

## Recognition Pattern

이 패턴이 의심되는 신호:

- F# 코드에서 `services.AddHttpClient(name, fun c -> ...)` 형태
- 등록은 성공했는데 `CreateClient(name)`이 돌려준 클라이언트의 `BaseAddress`가 `null` / `Timeout`이 default
- fake-Kestrel/integration test에서 "connection refused" 또는 "absolute URI required" 에러
- `IHttpClientFactory.CreateClient`가 명시적 named lookup인데도 unnamed default 동작처럼 보임

## The Approach

**원칙:** F#에서는 `Action<T>` 매개변수를 받는 fluent API를 호출할 때 **별개의 메서드로 체이닝**하는 것이 가장 안정적이다. `AddHttpClient`의 경우 `IHttpClientBuilder`를 반환하므로 `.ConfigureHttpClient(...)` 메서드를 따로 호출할 수 있다.

### Step 1: 잘못된 오버로드를 피한다

```fsharp
// ❌ BAD: F# 람다가 Action<HttpClient>로 안정 변환되지 않음
services.AddHttpClient("teacher", fun c ->
    c.BaseAddress <- Uri("http://127.0.0.1:8001")
    c.Timeout     <- TimeSpan.FromSeconds(30.0))
|> ignore
```

이 코드는 컴파일은 되지만, 람다가 실행되지 않거나 BaseAddress가 설정되지 않을 수 있다.

### Step 2: `.ConfigureHttpClient(...)` 체인으로 바꾼다

```fsharp
// ✅ GOOD: 별개의 메서드 호출로 ambiguity 제거
services.AddHttpClient("teacher")
    .ConfigureHttpClient(fun c ->
        c.BaseAddress <- Uri("http://127.0.0.1:8001")
        c.Timeout     <- TimeSpan.FromSeconds(30.0))
    |> ignore
```

`AddHttpClient(name)`은 `IHttpClientBuilder`만 반환한다 — 오버로드 충돌 없음. 그 위에 `.ConfigureHttpClient(Action<HttpClient>)` 한 가지만 존재하므로 F# 람다 변환이 명확하게 결정된다.

### Step 3: 검증한다

```fsharp
let factory = sp.GetRequiredService<IHttpClientFactory>()
let client = factory.CreateClient("teacher")
printfn $"BaseAddress = {client.BaseAddress}"  // 절대 null이 아니어야 함
printfn $"Timeout = {client.Timeout}"
```

테스트에서 fake-Kestrel을 쓴다면 `client.BaseAddress` 가 fake 서버의 실제 포트를 가리키는지 명시적으로 assert하라.

## Example

실제로 잡힌 버그 (smart-router TeacherLabelerTests, 2026-05-08):

```fsharp
// ❌ BAD: BaseAddress가 fake-Kestrel URL로 설정되지 않음
let mkLabeler (baseUrl: string) (promptPath: string) (datasetsDir: string) (cap: int) =
    let services = ServiceCollection()
    services.AddHttpClient("teacher", fun c ->
        c.BaseAddress <- Uri(baseUrl)
        c.Timeout     <- TimeSpan.FromSeconds(5.0))
        |> ignore
    let sp = services.BuildServiceProvider()
    // ... CreateClient("teacher")로 받은 client.BaseAddress = null
```

증상: `TeacherLabeler.LabelAsync`에서 `httpClient.PostAsync("/v1/chat/completions", ...)` 호출이 `localhost:80`(설정 안 됨)으로 가서 connection refused. 테스트가 `Failed` 케이스로 떨어지지만 의도한 `Labeled` 또는 `Unparseable`이 검증되지 않음.

```fsharp
// ✅ GOOD: ConfigureHttpClient 체인
let mkLabeler (baseUrl: string) (promptPath: string) (datasetsDir: string) (cap: int) =
    let services = ServiceCollection()
    services.AddHttpClient("teacher")
        .ConfigureHttpClient(fun c ->
            c.BaseAddress <- Uri(baseUrl)
            c.Timeout     <- TimeSpan.FromSeconds(5.0))
        |> ignore
    let sp = services.BuildServiceProvider()
    // CreateClient("teacher").BaseAddress == baseUrl ✓
```

## Production 코드와 테스트 코드 동시에 쓰는 경우

production CompositionRoot도 같은 패턴을 쓴다 — retry policy(`AddResilienceHandler`)나 추가 핸들러를 같은 builder에서 체이닝할 수 있다:

```fsharp
services.AddHttpClient("teacher")
    .ConfigureHttpClient(fun c ->
        c.BaseAddress <- Uri(opts.Endpoint)
        c.Timeout     <- TimeSpan.FromSeconds(float opts.TimeoutSeconds))
    .AddResilienceHandler("teacher-pipeline", fun builder ->
        builder.AddRetry(fun retryOpts ->
            retryOpts.MaxRetryAttempts <- 3
            retryOpts.ShouldHandle     <- shouldHandlePredicate
            // ...
        )
        |> ignore)
    |> ignore
```

## 체크리스트

- [ ] F#에서 `AddHttpClient(name, lambda)` 형태를 쓰고 있는가?
- [ ] 그렇다면 `.ConfigureHttpClient(...)` 체인으로 분리했는가?
- [ ] `CreateClient(name)`이 돌려준 client의 `BaseAddress`를 테스트에서 assert하는가?
- [ ] retry/resilience handler를 추가할 때 같은 builder에서 체이닝되는가?

## 관련 문서

- `setup-aspnetcore-config-override-test.md` — in-process Kestrel 테스트의 IConfiguration 오버라이드 (테스트에서 named HttpClient를 fake 서버로 가리키는 패턴과 함께 자주 등장)
- `bypass-concurrency-gated-upstream-with-named-httpclient.md` — named HttpClient를 사이드채널로 쓰는 아키텍처 패턴
