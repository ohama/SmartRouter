---
created: 2026-05-10
description: Closure 가 가변 값을 capture 하면 source 가 바뀌어도 stale — 값 대신 provider 인터페이스를 받아서 per-call live read
---

# Avoid Closure Capture of Mutable State (Pass a Provider, Read Live)

값이 시간에 따라 바뀔 수 있다면 **값 자체를 closure 에 capture 하지 말고**, 그 값을 알려주는 **인터페이스/getter 를 capture** 한다. 호출 시점마다 live 로 읽는다.

## The Insight

F# `let v = computeOnce()` + `fun req -> { ModelVersion = v }` 패턴은 v 를 **factory 시점에** freeze 한다. 후속 코드가 v 를 만들어 낸 source state 를 바꾸어도 closure 안의 v 는 영원히 첫 값.

상태가 정말로 immutable (configuration, computed constant) 이면 이 패턴이 정확하다. 상태가 mutable (model version, runtime config, cache) 이면 closure 가 첫 snapshot 에 stuck — UI/log/decision 출력이 silent 하게 stale.

해결: capture 시점에 **이 값을 어떻게 얻는가** 만 받고, 호출 시점에 그 source 에서 다시 읽는다. F# 에서는 보통 인터페이스 (port/provider) 가 그 source 다.

## Why This Matters

본 프로젝트 issue #12: `makeApplyML` 가 `(baselineVersion: string) (canaryVersion: string)` 두 개를 받아 closure 에 capture. CompositionRoot 에서 `let baselineVersion = sprintf "ml-%s" (computeModelVersion mlPath)` 가 DI factory 시점에 한 번 계산. RetrainingService 는 retrain 후 `IModelVersionProvider.Update` 를 정확히 호출하지만 closure 는 OLD 문자열 그대로 보관. 결과: DecisionLog JSONL 의 `model_version` 필드가 retrain 후에도 영원히 옛 값.

겉보기 증상: ML 추론은 정상 (분류기는 새 가중치로 동작), `/stats` 는 새 모델 버전을 보고, 그러나 모든 decision log row 에 옛 model_version. **canary cohort 분석이 silent 하게 misattribute.**

UI / metric / log 에서 갱신이 안 보이는 모든 버그가 이 패턴일 가능성. 디버거에서 source object 를 보면 새 값, capture 위치를 보면 옛 값.

## Recognition Pattern

다음 신호 중 둘 이상이면 의심:

- `let X = compute() in ... fun ... -> ... X ...` 패턴 (factory + closure)
- 호출 사이트 가 **mutable 한** state 를 매개로 동작 (in-memory cache, mutable singleton, file watcher)
- 디버그 출력은 옛 값, 별도 endpoint (예: `/stats`) 는 새 값
- "process 재시작 후엔 정상" — capture 시점이 process 시작이라는 강한 신호
- 단위 테스트는 통과 (테스트는 단일 호출만 검증; 갱신 시나리오 미커버)

## The Approach

### Step 1: 의심되는 값을 식별

closure 안에서 사용되는 변수 X 를 보고 묻는다: **"X 가 가리키는 source state 가 process 수명 동안 바뀔 수 있는가?"**

- 변경 가능 → 1단계 위반 후보
- 변경 불가능 (예: 파일 hash 가 시작 시 1회 계산되고 영원히 같음) → OK

### Step 2: source 의 인터페이스 / port 를 식별

X 를 만들어 낸 함수/객체 가 무엇인가? 그것이 이미 인터페이스가 있으면 그것을 받는다. 없으면 만든다 (port 추가). 본 프로젝트에서는 `IModelVersionProvider` 가 이미 존재 — Phase 8 에서 retrain-time live update 위해 추가됨.

```fsharp
// Core port — BCL only
type IModelVersionProvider =
    abstract member CurrentVersion : string with get
    abstract member CanaryVersion  : string with get
    abstract member Update         : newVersion: string -> unit
    abstract member UpdateCanary   : newVersion: string -> unit
```

### Step 3: closure signature 를 변경

값 대신 provider 를 받는다. 호출 시점에 `provider.CurrentVersion` 으로 live read.

```fsharp
// BEFORE — captures string at factory time; never updates
let makeApplyML
    (...)
    (baselineVersion : string)   // ← frozen
    (canaryVersion   : string)   // ← frozen
    : RoutingAlgorithm =
    fun cfg req ->
        let modelVersion = if isCanary then canaryVersion else baselineVersion
        ...

// AFTER — captures provider; reads live per call
let makeApplyML
    (...)
    (versionProvider : IModelVersionProvider)   // ← live source
    : RoutingAlgorithm =
    fun cfg req ->
        let modelVersion =
            if isCanary then versionProvider.CanaryVersion       // ← per-call
            else            versionProvider.CurrentVersion        // ← per-call
        ...
```

provider 인스턴스 자체는 capture 해도 무방 — singleton 이고 mutable 한 건 **provider 가 보관하는 필드** 일 뿐 provider 의 identity 는 process 수명 동안 그대로.

### Step 4: DI 와인 업데이트

CompositionRoot 의 factory 가 provider 를 makeApplyML 에 전달.

```fsharp
let vp = sp.GetRequiredService<IModelVersionProvider>()
// 시작 시 on-disk 값으로 seed (첫 request 가 정확한 값을 보도록)
vp.Update(sprintf "ml-%s" (computeModelVersion mlPath))
{ Algorithm = makeApplyML embedder ... canaryGate vp
  ... }
```

이후 RetrainingService.Update / CanaryService.UpdateCanary 가 provider 를 mutate 하면 다음 routing 호출이 새 값을 본다.

### Step 5: 테스트로 회귀 방지

가변 stub 으로 update → re-call → assert 패턴:

```fsharp
let providerImpl = TestVersionProvider("ml-aaaaaaaa", "")
let vp = providerImpl :> IModelVersionProvider
let algo = makeApplyML ... vp

let d1 = algo cfg req1
Expect.equal d1.ModelVersion "ml-aaaaaaaa" "first call"

vp.Update("ml-bbbbbbbb")    // simulate retrain landing
let d2 = algo cfg req2
Expect.equal d2.ModelVersion "ml-bbbbbbbb" "post-update reflects new value"
```

이 테스트가 pre-fix 에는 실패 (closure 가 string 을 capture 했으니 d2.ModelVersion 도 "ml-aaaaaaaa") , post-fix 에서 통과.

## Example

전체 fix: `src/SmartRouter.Core/ML.fs` (commit `debb94a`). 테스트: `tests/SmartRouter.Tests/MLLiveVersionTests.fs`.

```fsharp
// Bad: signature suggests "give me the current version" but captures it forever
let makeApplyML embedder ... baselineVersion canaryVersion =
    fun cfg req -> { ModelVersion = baselineVersion; ... }

// Good: signature says "I will ASK for the version each time"
let makeApplyML embedder ... (versionProvider: IModelVersionProvider) =
    fun cfg req -> { ModelVersion = versionProvider.CurrentVersion; ... }
```

핵심: 시그니처가 의도를 그대로 드러낸다. `string` parameter 는 "값 한 번 받아서 쓰겠다", `IModelVersionProvider` parameter 는 "필요할 때마다 묻겠다".

## 체크리스트

- [ ] closure 안의 모든 free variable 을 나열
- [ ] 각 변수에 대해 묻기: "source state 가 process 수명 동안 mutate 되는가?"
- [ ] mutate 되는 변수에 대해 source 의 인터페이스 / port 가 있는지 확인 (없으면 추가)
- [ ] closure signature 를 값 → provider 로 변경
- [ ] capture 시점에 provider 를 seed (첫 호출 전에 정확한 값이 들어가도록)
- [ ] gauge: post-update 호출이 새 값을 보는지 unit test 로 검증

## 관련 문서

- `handle-singleton-of-scoped-via-scope-factory.md` — capture 가 위험한 또 다른 시나리오 (DI scope 위반)
- `test-di-scopes-with-validatescopes.md` — closure-capture 의 lifetime 측면 검사
