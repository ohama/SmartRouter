---
created: 2026-05-08
description: in-process Kestrel 테스트에서 IConfiguration 오버라이드가 production URL을 가리키는 이유 — AddInMemoryCollection은 configureServices보다 먼저
---

# ASP.NET Core in-process Kestrel 테스트 하니스

`WebApplication.CreateBuilder()`로 in-process 테스트 router를 띄우고 `IConfiguration`을 오버라이드해도, **`IOptions<T>` 바인딩이 production 값을 가리킨다.** 이건 `AddInMemoryCollection`이 `configureServices`보다 **나중에** 호출됐기 때문이다. 순서가 결정한다.

## The Insight

`IOptions<T>`은 DI 등록 시점에 `IConfiguration`의 현재 상태를 캡처해서 binding factory를 만든다. `services.Configure<MyOptions>(config.GetSection(...))`이 실행될 때 `config`의 source 리스트가 고정된다. 나중에 source를 추가해도 이미 등록된 binding은 영향받지 않는다.

따라서 test 오버라이드를 작동시키려면 **DI 등록보다 먼저 config provider를 추가해야 한다**.

## Why This Matters

이 함정에 빠지면:
1. 테스트가 production URL(`localhost:8000`, `localhost:8001` 같은 진짜 서비스)에 연결하려 한다
2. 진짜 서비스가 안 떠 있으면 `connection refused`로 실패 → "테스트가 깨짐"으로 잘못 진단
3. 진짜 서비스가 떠 있으면 fake upstream을 우회해 진짜 모델을 호출 → 결정적이지 않은 응답으로 어설션이 random하게 깨짐
4. 더 나쁘게는 fake upstream이 emit한 청크와 진짜 모델 청크가 섞여 디버깅이 거의 불가능

원인을 못 찾으면 IConfiguration 오버라이드를 포기하고 production `appsettings.json`을 테스트마다 덮어 쓰는 hack을 만들게 된다.

## Recognition Pattern

다음 중 하나를 본 적 있으면 이 글이 필요하다:
- 테스트에서 `Configuration.AddInMemoryCollection`으로 URL을 오버라이드했는데 여전히 진짜 서비스에 연결한다
- `IOptions<T>.Value`가 production 값으로 깨진다 — 분명 in-memory에서 다른 값을 넣었는데
- in-process Kestrel + DI + IOptions 조합으로 통합 테스트를 작성하려 한다
- "test config가 안 먹는다"는 미스터리

## The Approach

`WebApplication.CreateBuilder()`이 만든 `IConfigurationBuilder`에 source는 시간순으로 쌓인다. 마지막 source가 우선한다 — 단, **DI 시점 전에 추가됐을 때만**.

다섯 단계 순서를 항상 이렇게 둔다:

| Step | 동작 | 왜 |
|---|---|---|
| 1 | `builder = WebApplication.CreateBuilder()` | 비어 있는 IConfiguration 시작 |
| 2 | `builder.WebHost.UseUrls("http://127.0.0.1:0")` | OS-assigned 랜덤 포트 |
| 3 | `builder.Configuration.AddInMemoryCollection(testValues)` | **반드시 4 이전** |
| 4 | `CompositionRoot.configureServices(builder.Services, builder.Configuration)` | IOptions binding 캡처 시점 |
| 5 | `let app = builder.Build()` then `mapEndpoints app` | 라우트 등록 |

`configureServices`가 `services.Configure<UpstreamOptions>(config.GetSection("Upstreams"))`을 호출할 때 `config`엔 이미 InMemory source가 들어 있어야 한다.

### Step 1: in-memory 오버라이드는 cast가 필요할 수 있다

`AddInMemoryCollection`은 `IConfigurationBuilder` 확장 메서드다. `ConfigurationManager`(WebApplicationBuilder가 노출하는 구체 타입)는 인터페이스를 implement하지만 확장 메서드 resolution은 가끔 실패한다.

```fsharp
// 명시적으로 IConfigurationBuilder로 캐스트
let configBuilder = builder.Configuration :> IConfigurationBuilder
configBuilder.AddInMemoryCollection(
    dict [
        "Upstreams:Qwen35BBaseUrl", fakeUpstreamUrl
        "Upstreams:Qwen122BBaseUrl", fakeUpstreamUrl
        "Routing:ComplexityThreshold", "3"
        // ... 모든 필수 키 포함
    ]
) |> ignore
```

### Step 2: validation이 production keys를 요구하면 모두 채워라

`CompositionRoot.configureServices`이 startup-time validation을 하면 (예: 필수 task가 모두 매핑됐는지 확인), 테스트 InMemory도 모든 키를 채워야 한다. 하나라도 빠지면 DI singleton factory가 throw해서 테스트 endpoint가 HTTP 500을 뱉는다.

### Step 3: mapEndpoints을 명시적으로 호출

```fsharp
let app = builder.Build()
ChatCompletions.mapEndpoints app
Stats.mapEndpoints app
do! app.StartAsync() |> Async.AwaitTask
```

`mapEndpoints`을 빼먹으면 router는 어떤 라우트도 등록 안 된 채 떠 있고, 모든 테스트가 silently 404를 받는다. 이건 must_haves에 명시적으로 넣어둘 만한 wiring이다.

### Step 4: teardown은 반드시 try/finally

```fsharp
try
    do! runTests ()
finally
    // task{} finally는 do!이 안 되니 GetAwaiter().GetResult()
    app.StopAsync().GetAwaiter().GetResult()
    fakeUpstream.StopAsync().GetAwaiter().GetResult()
    (app :> IAsyncDisposable).DisposeAsync().GetAwaiter().GetResult()
```

teardown을 빼먹으면 다음 테스트가 "port already in use"로 실패한다 — 진짜 어설션이 아니라 인프라 leak이 원인.

## Example

이 프로젝트의 `startTestRouter` 헬퍼:

```fsharp
let startTestRouter (fakeUpstreamUrl: string) : Async<TestRouter> = async {
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

    // STEP 1 (반드시 STEP 2 이전): IConfiguration 오버라이드
    // MUST happen before configureServices — overrides bind here
    let configBuilder = builder.Configuration :> IConfigurationBuilder
    configBuilder.AddInMemoryCollection(
        dict [
            "Upstreams:Qwen35BBaseUrl", fakeUpstreamUrl
            "Upstreams:Qwen122BBaseUrl", fakeUpstreamUrl
            "Routing:ComplexityThreshold", "3"
            "Routing:Keywords:0", "compiler"
            "Routing:Keywords:1", "mlir"
            "Routing:TaskTable:retrieval", "35b"
            "Routing:TaskTable:summary", "35b"
            "Routing:TaskTable:reasoning", "122b"
            "Routing:TaskTable:graph_indexing", "122b"
            "Routing:TaskTable:compiler_debug", "122b"
            "Routing:TaskTable:architecture_analysis", "122b"
            "Routing:TaskTable:dependency_analysis", "122b"
            "Routing:ModelAliases:35b", "35b"
            "Routing:ModelAliases:122b", "122b"
            "Queue:FairnessK", "10"
            "Queue:MaxConcurrent122B", "1"
            "Queue:PerRequestTimeoutSeconds", "300"
        ]
    ) |> ignore

    // STEP 2: 이제 IConfiguration이 production keys를 모두 가지고 있다
    SmartRouter.Cli.CompositionRoot.configureServices
        builder.Services
        builder.Configuration |> ignore

    // STEP 3: build + map routes
    let app = builder.Build()
    SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app
    SmartRouter.Cli.Endpoints.Stats.mapEndpoints app

    // STEP 4: start
    do! app.StartAsync() |> Async.AwaitTask

    let actualUrl = app.Urls |> Seq.head  // 실제 OS-assigned 포트
    return { App = app; Url = actualUrl }
}
```

## 체크리스트

- [ ] `AddInMemoryCollection` → `configureServices` 순서 (반대로 하면 production 바인딩)
- [ ] `IConfigurationBuilder`로 명시적 캐스트 (확장 메서드 resolution)
- [ ] startup validation이 요구하는 모든 키를 InMemory에 채움
- [ ] `mapEndpoints` 호출 (없으면 모든 테스트가 silently 404)
- [ ] teardown try/finally (없으면 port leak으로 후속 테스트 깨짐)
- [ ] 각 테스트는 `testSequenced` 안에 — Console.SetOut + 글로벌 state race 방지

## 관련 문서

- `debug-kestrel-request-aborted.md` — Kestrel 테스트에서 cancel 시뮬레이션
- `handle-fsharp-task-finally-disposal.md` — teardown의 비동기 dispose 처리
