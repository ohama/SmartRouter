---
created: 2026-05-10
description: BuildServiceProvider(ServiceProviderOptions(ValidateScopes=true)) 를 unit test 에서 사용해 prod-only DI 수명 위반을 잡아낸다
---

# Test DI Scope Violations with `ValidateScopes = true`

Microsoft.Extensions.DependencyInjection 의 **ValidateScopes** 검사를 unit test 에서 명시적으로 켜서 prod 환경에서만 surface 되는 DI 수명 위반을 사전에 잡는다.

## The Insight

`services.BuildServiceProvider()` (no options) 는 `ValidateScopes` 를 default 로 **false**. ASP.NET Core 의 `WebApplication.CreateBuilder()` 는 Development 모드에서만 자동으로 true 로 설정. Production 모드 + 일반 unit test (`new ServiceCollection().BuildServiceProvider()`) 모두 검사가 꺼져 있어, **singleton-resolves-scoped 와 같은 수명 위반이 silent 하게 통과** 한다.

`ServiceProviderOptions(ValidateScopes = true)` 를 명시하면:

1. Singleton factory lambda 가 root provider 에서 scoped 를 resolve 하면 throw
2. Singleton 이 다른 singleton 을 통해 transitive 하게 scoped 를 resolve 해도 throw
3. Scoped 가 root scope 에서 resolve 되면 throw

이 검사는 ASP.NET Core 의 Development 기본값과 같다. 즉, unit test 에서 이 옵션을 켜면 **dev 머신과 같은 수준의 DI 위생 검사** 가 작동한다.

## Why This Matters

본 프로젝트 issue #2 사례: 78개 단위 테스트 모두 통과했지만 production 첫 request 에서 HTTP 500 + `Cannot resolve scoped service 'IVariantFeatureManager' from root provider`. 원인은 ICanaryGate (singleton) 가 IVariantFeatureManager (scoped) 를 root provider 에서 resolve. 단위 테스트들은 자체적으로 ServiceCollection 을 만들고 가짜 IUpstreamClient 만 주입했기 때문에 production DI 그래프 전체를 elevate 하지 않았다.

이 클래스의 버그를 처음으로 catch 하려면 두 가지 중 하나가 필요하다:

- **Option A**: `WebApplicationFactory<Program>` 으로 진짜 host 를 띄우고 HTTP 요청 보내기 — 무거움 (ML 모델 파일 필요, 외부 의존성)
- **Option B**: `ServiceCollection` 으로 production DI 그래프와 동일한 등록을 한 후 `BuildServiceProvider(ServiceProviderOptions(ValidateScopes=true))` — 가벼움 (FM + canary state 만 필요), 100ms 안에 완료

본 프로젝트의 `ProductionDiTests.fs` 가 Option B. 결과: 위반이 발생하면 `InvalidOperationException` throw, 발생 안 하면 cleanly 통과.

## Recognition Pattern

다음 시그널이 보이면 ValidateScopes 게이트가 필요:

- 단위 테스트 모두 green 인데 prod 첫 request 에서 500 + DI 관련 exception
- "works on my machine" 인데 launchd / docker / Production 환경에서 실패
- DI 등록에 외부 라이브러리 (`Microsoft.FeatureManagement`, `EntityFrameworkCore`, `OpenTelemetry`) 가 섞여 있음 — 이들은 default lifetime 이 Scoped 인 type 을 흔히 등록
- Singleton 의 factory lambda 안에서 `sp.GetRequiredService<T>()` 가 한 번 이상 호출됨

## The Approach

### Step 1: 의심 가는 production DI 등록 부분만 isolate

전체 host 를 띄울 필요는 없다. 위반 가능성이 있는 등록 사이트를 좁혀서 그 부분만 ServiceCollection 에 미러링.

```fsharp
let services = ServiceCollection()

// Production 과 동일한 등록 — ICanaryGate (singleton) + IVariantFeatureManager (scoped)
services.AddScopedFeatureManagement().WithTargeting<StubTargetingAccessor>() |> ignore
services.AddSingleton<ICanaryState>(...) |> ignore
services.AddSingleton<ICanaryGate>(fun sp ->
    let scopeFactory = sp.GetRequiredService<IServiceScopeFactory>()
    let st = sp.GetRequiredService<ICanaryState>()
    FeatureManagementCanaryGate(scopeFactory, st, "/tmp/canary.zip") :> ICanaryGate)
    |> ignore
```

외부 의존성 (HTTP client, ML 모델 파일) 은 stub 으로 대체. 핵심은 **DI 등록 사이트** 가 production 과 동일하다는 것.

### Step 2: ValidateScopes 옵션을 명시적으로 켜고 BuildServiceProvider

```fsharp
let opts = ServiceProviderOptions(ValidateScopes = true)
use sp = services.BuildServiceProvider(opts)
```

이 한 줄이 검증의 핵심. 옵션 없이 build 하면 검사가 생략된다.

### Step 3: 의심되는 type 을 resolve + 호출

resolve 만 하지 말고 **실제로 호출** 해야 한다. ValidateScopes 는 lazy — resolve 시점에 위반을 감지하지 못할 수 있고 (factory lambda 가 actual call path 에서 scoped 를 try 할 때 발화하는 경우), 호출까지 시뮬레이션해야 안전.

```fsharp
let gate = sp.GetRequiredService<ICanaryGate>()
let result = gate.IsCanaryAsync("test-cid", CancellationToken.None).GetAwaiter().GetResult()
Expect.isFalse result "no DI exception (canary file absent → short-circuit)"
```

### Step 4: 위반 시나리오와 정상 시나리오 둘 다 테스트

본 프로젝트 패턴 (`tests/SmartRouter.Tests/ProductionDiTests.fs`):

- Test 1 — short-circuit path (canary file 없음 → 호출이 빨리 return). DI 위반이 있다면 `gate.IsCanaryAsync` 진입 전 `GetRequiredService<ICanaryGate>` 단계에서 throw 가능.
- Test 2 — full path (canary file 있음 + percentage>0). gate 가 실제로 IVariantFeatureManager 를 resolve 하는 코드 경로까지 진입.

두 테스트 모두 throw 없이 통과해야 fix 검증 완료.

## Example

```fsharp
module SmartRouter.Tests.ProductionDiTests

open Microsoft.Extensions.DependencyInjection
open Microsoft.FeatureManagement
open Expecto

type private StubTargetingAccessor() =
    interface ITargetingContextAccessor with
        member _.GetContextAsync() =
            ValueTask<TargetingContext>(TargetingContext(UserId = "test-user"))

let tests = testSequenced (testList "production-di" [
    testCase "ICanaryGate.IsCanaryAsync does not throw scoped-from-root DI exception" <| fun () ->
        let services = ServiceCollection()
        services
            .AddScopedFeatureManagement()
            .WithTargeting<StubTargetingAccessor>()
            |> ignore
        services.AddSingleton<ICanaryState>(fun _ -> CanaryState(0) :> ICanaryState) |> ignore
        services.AddSingleton<IConfiguration>(buildMinimalConfig ()) |> ignore
        services.AddSingleton<ICanaryGate>(fun sp ->
            let scopeFactory = sp.GetRequiredService<IServiceScopeFactory>()
            let st = sp.GetRequiredService<ICanaryState>()
            FeatureManagementCanaryGate(scopeFactory, st, "/tmp/nonexistent.zip") :> ICanaryGate)
            |> ignore

        let opts = ServiceProviderOptions(ValidateScopes = true)
        use sp = services.BuildServiceProvider(opts)
        let gate = sp.GetRequiredService<ICanaryGate>()

        // Pre-fix: throws InvalidOperationException right here
        // Post-fix: cleanly returns false (canary file absent → short-circuit)
        let result = gate.IsCanaryAsync("test-cid", CancellationToken.None).GetAwaiter().GetResult()
        Expect.isFalse result "no DI exception"
])
```

이 테스트가 pre-fix 에 fail, post-fix 에 pass — 회귀 방지의 본질.

## 체크리스트

- [ ] 의심되는 prod DI 등록 사이트 (singleton + 외부 lib scoped 의존) 를 식별
- [ ] 등록만 미러링한 ServiceCollection 을 만들어 외부 의존성을 stub 으로 대체
- [ ] `BuildServiceProvider(ServiceProviderOptions(ValidateScopes = true))` 사용 — 옵션 없이 build 하면 검사 안 됨
- [ ] resolve 만 하지 말고 실제로 호출까지 — lazy validation 회피
- [ ] short-circuit 경로 와 full-path 경로 둘 다 테스트
- [ ] testSequenced 로 감싸서 다른 테스트의 console/file race 안 받기

## 관련 문서

- `handle-singleton-of-scoped-via-scope-factory.md` — 위반의 일반적인 fix 패턴
- `setup-aspnetcore-config-override-test.md` — IConfiguration AddInMemoryCollection 우선순위 (테스트 setup 시 필요)
