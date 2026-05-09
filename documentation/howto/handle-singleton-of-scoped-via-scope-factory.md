---
created: 2026-05-10
description: Singleton 이 Scoped 서비스를 root provider 에서 resolve 하면 ValidateScopes 가 거부 — IServiceScopeFactory 받아서 per-call scope 안에서 resolve
---

# Singleton-of-Scoped DI via IServiceScopeFactory

ASP.NET Core / Microsoft.Extensions.DependencyInjection 에서 **Singleton 이 Scoped 서비스를 직접 받으면 안 된다.** 우회는 `IServiceScopeFactory` 를 받아서 **호출당 scope 를 생성**하고 그 안에서 scoped 를 resolve 하는 것.

## The Insight

DI 수명 (Singleton / Scoped / Transient) 은 **소비 패턴이 아니라 보관 책임** 을 나타낸다. Scoped 서비스는 "request scope 동안만 살아 있는다" 는 계약을 가진다. Singleton 이 root provider 에서 그것을 resolve 하면 그 인스턴스가 process lifetime 동안 살아남게 되어 계약 위반이다.

해결책은 "내가 scope 가 없으니 만들면 된다" — **IServiceScopeFactory** 는 자체가 singleton-safe 이고, `CreateScope()` 호출마다 새 `IServiceScope` 를 발행한다. scoped 서비스는 그 scope 안에서만 resolve + 사용 + dispose.

## Why This Matters

`ServiceProviderOptions(ValidateScopes = true)` (ASP.NET Core Development 기본값) 이면 위반 시 즉시 `InvalidOperationException`. Production 모드는 default 로 `ValidateScopes = false` — 위반이 silent 하게 진행되다가 **scoped 서비스의 라이프타임 보장이 깨지면서 그제야 망가진다**:

- IDbContext (scoped) 를 singleton repository 에 가두면 모든 request 가 같은 connection 을 공유 — concurrency 깨짐
- IFeatureManagement (scoped) 를 캐시하면 request 별 cohort 구분이 사라짐
- Scope 종료 시 dispose 되어야 할 자원이 process 종료까지 안 풀림

본 프로젝트의 issue #2 사례: ICanaryGate (singleton) 가 IVariantFeatureManager (scoped) 를 root provider 에서 resolve. 78 단위 테스트 모두 통과, prod 첫 request 에서 HTTP 500 + `Cannot resolve scoped service 'Microsoft.FeatureManagement.IVariantFeatureManager' from root provider`.

## Recognition Pattern

코드 안에서 singleton 으로 등록된 type 의 생성자 / factory lambda 가 다음을 하면 의심:

- `sp.GetRequiredService<TScoped>()` (factory lambda 안에서)
- `IServiceProvider` 를 ctor 에 받아서 보관 (root provider; 후속 GetRequiredService 가 root scope 위반)
- 외부 라이브러리 type 인데 default lifetime 이 Scoped (Microsoft.FeatureManagement, Entity Framework Core DbContext, 일부 OpenTelemetry 컴포넌트 등)

런타임 증상: `InvalidOperationException` with "Cannot resolve scoped service ... from root provider" — Development 환경에서 `validateScopes:true` 가 켜진 상태에서만 보임. Production 에서는 silent corruption.

## The Approach

원칙: **singleton 은 scoped 인스턴스를 직접 보관하지 말고, scope 를 만드는 능력만 보관한다.**

### Step 1: ctor 시그니처 변경

```fsharp
// BEFORE — broken: scoped fm captured at factory time
type FeatureManagementCanaryGate(
    featureManager : IVariantFeatureManager,   // ← scoped — never capture
    canaryState    : ICanaryState,
    canaryModelPath: string) =
    interface ICanaryGate with
        member _.IsCanaryAsync(cid, ct) =
            task {
                let! enabled = featureManager.IsEnabledAsync<TargetingContext>(...)
                return enabled
            }

// AFTER — singleton-safe: take IServiceScopeFactory (itself singleton)
type FeatureManagementCanaryGate(
    scopeFactory   : IServiceScopeFactory,     // ← singleton; safe to capture
    canaryState    : ICanaryState,
    canaryModelPath: string) =
    interface ICanaryGate with
        member _.IsCanaryAsync(cid, ct) =
            task {
                use scope = scopeFactory.CreateScope()                       // ← per-call scope
                let fm = scope.ServiceProvider.GetRequiredService<IVariantFeatureManager>()
                let! enabled = fm.IsEnabledAsync<TargetingContext>(...)
                return enabled
            }                                                                 // ← scope disposes here
```

`use scope = ...` 가 `IServiceScope.Dispose()` 를 호출 종료 시 보장. scope 안에서 resolve 한 모든 scoped 서비스가 같이 dispose 된다.

### Step 2: DI 등록 업데이트

```fsharp
services.AddSingleton<ICanaryGate>(fun sp ->
    let scopeFactory = sp.GetRequiredService<IServiceScopeFactory>()  // ← singleton resolve, OK
    let st = sp.GetRequiredService<ICanaryState>()                    // ← singleton, OK
    FeatureManagementCanaryGate(scopeFactory, st, path) :> ICanaryGate)
    |> ignore
```

### Step 3: 테스트와 production 둘 다 지원하는 ctor

테스트에서는 IVariantFeatureManager 를 명시적으로 만들어 주입하고 싶을 수 있다 (scope 만드는 게 번거로움). Multi-ctor 패턴:

```fsharp
type FeatureManagementCanaryGate
    internal
    (
        checkFeature   : string -> CancellationToken -> Task<bool>,
        canaryState    : ICanaryState,
        canaryModelPath: string
    ) =

    /// 테스트용 — 호출자가 fm 을 직접 보관
    new (fm: IVariantFeatureManager, state: ICanaryState, path: string) =
        let check (cid: string) (ct: CancellationToken) =
            task {
                let ctx = TargetingContext(UserId = cid)
                return! fm.IsEnabledAsync<TargetingContext>(name, ctx, ct)
            }
        FeatureManagementCanaryGate(check, state, path)

    /// Production — per-call scope
    new (scopeFactory: IServiceScopeFactory, state: ICanaryState, path: string) =
        let check (cid: string) (ct: CancellationToken) =
            task {
                use scope = scopeFactory.CreateScope()
                let fm = scope.ServiceProvider.GetRequiredService<IVariantFeatureManager>()
                let ctx = TargetingContext(UserId = cid)
                return! fm.IsEnabledAsync<TargetingContext>(name, ctx, ct)
            }
        FeatureManagementCanaryGate(check, state, path)
    ...
```

내부 primary ctor 는 `checkFeature` delegate 를 받고, public ctor 두 개가 각각 자신의 패턴으로 delegate 를 합성. 테스트는 fm 직접 주입, production 은 scope factory 주입.

## Example

전체 Phase 9 canary gate 의 production-vs-test 양쪽 호환 구조: `src/SmartRouter.Cli/Adapters/CanaryGate.fs` (commit `95e9c06`).

### 테스트로 검증

`BuildServiceProvider(ServiceProviderOptions(ValidateScopes = true))` 로 unit test 안에서 prod 와 같은 검사를 켜면 위반이 즉시 throw. (관련 howto: `test-di-scopes-with-validatescopes.md`)

```fsharp
let opts = ServiceProviderOptions(ValidateScopes = true)
use sp = services.BuildServiceProvider(opts)
let gate = sp.GetRequiredService<ICanaryGate>()
let result = gate.IsCanaryAsync("test-cid", CancellationToken.None).GetAwaiter().GetResult()
// pre-fix: throws InvalidOperationException; post-fix: returns false cleanly
```

## 체크리스트

- [ ] 의심 후보: factory lambda 가 `sp.GetRequiredService<T>()` 를 호출하는데 T 가 외부 라이브러리 type 인지 확인 (default lifetime Scoped 가능성)
- [ ] `BuildServiceProvider(ServiceProviderOptions(ValidateScopes = true))` 로 빌드해서 위반 발견되는지 확인 — Development 모드 default 와 동일
- [ ] 의심되는 singleton 의 ctor 를 IServiceScopeFactory 받게 리팩터
- [ ] 호출 본문에서 `use scope = scopeFactory.CreateScope()` 후 scope 안에서 scoped resolve
- [ ] 테스트에서 다른 ctor (직접 인스턴스 주입) 를 retain 하고 싶으면 multi-ctor 패턴

## 관련 문서

- `test-di-scopes-with-validatescopes.md` — 위반을 unit test 에서 재현하는 패턴
- `wire-fsharp-namedhttpclient-with-configurehttpclient.md` — DI 등록 시 F# 람다 오버로드 트랩
