---
created: 2026-05-09
description: ML.NET 검증 파이프라인은 `TrainTestSplit`을 `Fit` *전*에 호출해야 한다 — split AFTER training은 held-out set을 학습 데이터로 오염시키고, baseline vs candidate 비교를 불공정하게 만든다
---

# ML.NET 검증 파이프라인은 split-FIRST 순서가 필수

`mlContext.Data.TrainTestSplit`을 `Fit` *후에* 호출하면 두 가지 문제가 동시에 발생한다: (1) "held-out" 검증 세트가 학습에 노출되어 metric이 낙관적으로 편향되고, (2) baseline 모델과 candidate 모델이 서로 다른 분포의 데이터로 평가되어 비교가 불공정해진다. 정답은 split → train(TrainSet) → evaluate(TestSet) 순서다.

## The Insight

ML 검증 파이프라인의 정의는 간단하다:

> **held-out set은 학습 중 단 한 번도 보지 않은 데이터여야 한다.**

이걸 깨면 검증 metric이 거짓말을 시작한다. 모델이 train set의 패턴을 외운 만큼 test set이 train set과 겹치면 정확도가 부풀려진다. 그 부풀린 metric을 게이트로 쓰면 (e.g., "accuracy ≥ baseline이면 새 모델 채택") 실제로는 generalization이 나빠지는 모델을 받아들이게 된다.

추가로 — baseline을 candidate의 split으로 평가하는 패턴은 추가적인 함정을 만든다. baseline 모델은 *과거에* 다른 데이터로 학습됐고, 그 학습 분포는 candidate의 학습 분포와 다르다. baseline을 candidate의 split.TestSet으로 평가하면 baseline이 본 적 없는 분포에서 테스트하는 셈이다 — 그 결과 metric은 baseline의 능력이 아니라 distribution shift를 측정한다.

**fair comparison을 위해서는 baseline과 candidate 둘 다 SAME held-out split에서 평가되어야 한다.**

## Why This Matters

검증 게이트가 있는 retraining 시스템에서:

- **새 모델이 항상 통과**: 학습-테스트 누수로 candidate metric이 인위적으로 높아져, 검증 게이트가 의미 없는 도장이 된다. Production 데이터에서 정확도가 떨어지는 모델이 hot-reload되어 traffic을 망친다.
- **새 모델이 항상 거부**: 반대로 baseline이 candidate의 split에서 testing되면 baseline의 분포 mismatch 때문에 candidate가 항상 더 나은 것처럼 보이거나, 반대로 항상 거부될 수 있다. 어느 방향으로 편향될지는 데이터에 따라 다르다.

증상은 거짓-positive와 거짓-negative 둘 다 — 검증이 "동작하는 것처럼" 보이는 production에서, 실제로는 게이트가 신호를 측정하는 게 아니라 노이즈를 측정한다. 한참 운영한 후에 model_version A/B 비교에서 production fallback rate가 검증 metric과 안 맞는다는 걸 발견하고 나서야 알아챈다.

## Recognition Pattern

이 코드 모양이 보이면 의심:

```fsharp
let model = mlContext |> retrain dataView      // ① 전체 dataView로 학습
let split = mlContext.Data.TrainTestSplit(dataView, testFraction = 0.2, seed = 42)  // ② 학습 후 split
let metrics = mlContext.BinaryClassification.Evaluate(model.Transform(split.TestSet))
// split.TestSet은 dataView의 일부라서 학습에 포함됨
```

또는 baseline-vs-candidate 비교에서:

```fsharp
let candidateMetrics = evaluate candidateModel candidateSplit.TestSet
let baselineMetrics = evaluate baselineModel candidateSplit.TestSet
// 두 모델이 같은 split에서 평가되긴 하지만, 이 split은 candidate가 학습한 분포에서 추출됨
// → baseline은 본 적 없는 분포에서 테스트됨
```

특히 다음 시나리오에서 자주 등장한다:

- 한 dataView로 모든 걸 처리하려는 "한 함수에 다 담기" 욕망
- baseline 모델을 production에서 가져왔는데 그 학습 분포 메타데이터가 없을 때
- LbfgsLogisticRegression / Sdca / FastTree 등 fit-once 모델 (online이 아님)

## The Approach

**원칙:** "어떤 데이터가 학습 중에 노출되는가?"를 데이터 흐름에서 명시적으로 추적하라. split을 fit *전에* 만들고, baseline과 candidate를 SAME split의 TestSet으로 평가하라.

### Step 1: dataView를 만든 직후 split

```fsharp
let dataView = mlContext.Data.LoadFromEnumerable(merged: TrainSample[])
let split = mlContext.Data.TrainTestSplit(dataView, testFraction = 0.2, seed = 42)
// 이 시점부터 split.TrainSet과 split.TestSet은 disjoint
```

`testFraction`은 ≥ 0.1 (10%) 권장; `seed`는 재현성을 위해 고정 (config로 노출하면 더 좋음). 같은 seed + 같은 dataView = 같은 split이 보장된다.

### Step 2: split.TrainSet으로만 학습

```fsharp
let candidateModel = retrain mlContext split.TrainSet candidatePath l2
// retrain의 시그니처가 IDataView를 받아야 함 — TrainSample[] 같은 raw 컬렉션을 받으면
// 함수 안에서 다시 LoadFromEnumerable + 통째로 학습할 위험이 있음
```

함수 시그니처가 `MLContext -> IDataView -> string -> ITransformer` 같은 형태가 되도록 강제하면, 호출자가 split을 "잊어버리고" 전체 dataView를 넘기는 실수를 막기 쉽다.

### Step 3: 같은 split.TestSet으로 baseline + candidate 평가

```fsharp
let baselineMetrics = evaluate mlContext baselineModel split.TestSet
let candidateMetrics = evaluate mlContext candidateModel split.TestSet
// 두 모델이 정확히 같은 데이터로 평가됨 — fair comparison
```

baseline 모델은 별도 파일에서 로드 (e.g., `mlContext.Model.Load(prevModelPath, &_)`). baseline이 *어떤 분포로 학습됐는지*는 이 비교에서 무관하다 — 우리는 *현재 시점의* held-out에서 두 모델의 능력을 측정할 뿐이다.

### Step 4: 게이트는 두 metric의 비교

```fsharp
let accept =
    candidateMetrics.Accuracy >= baselineMetrics.Accuracy &&
    (1.0 - candidateMetrics.PositiveRecall) <= (1.0 - baselineMetrics.PositiveRecall)
if accept then atomicSwap candidatePath finalPath
else File.Delete candidatePath
```

`fallback_rate := 1.0 - PositiveRecall` 같은 도메인 metric은 baseline의 같은 metric과 직접 비교 가능하다 — 둘 다 같은 split에서 측정됐으니까.

## Example

실제 잡힌 케이스 (smart-router Phase 8 plan-checker iteration 1, 2026-05-09):

```fsharp
// ❌ BAD — 원래 plan은 train-then-split이었음
let runRetrain () = task {
    let dataView = mlContext.Data.LoadFromEnumerable(merged)
    let model = retrain dataView candidatePath l2          // ① 전체 학습
    let split = mlContext.Data.TrainTestSplit(dataView, 0.2, 42)  // ② 사후 split
    let baselineMetrics = evaluate baselineModel split.TestSet
    let candidateMetrics = evaluate model split.TestSet
    // 의도된 코멘트: "Train과 held-out이 겹침 — v1에서는 수용 가능"
    // 실제로는 RETRAIN-03의 "validated against held-out set" 요구사항을 위반
    ...
}

// ✅ GOOD — split FIRST로 강제 (CONTEXT.md Lock 5)
let runRetrain () = task {
    let dataView = mlContext.Data.LoadFromEnumerable(merged)
    let split = mlContext.Data.TrainTestSplit(dataView, testFraction = 0.2, seed = 42)
    let candidateModel = Retrainer.retrain mlContext split.TrainSet candidatePath l2
    let baselineMetrics = Validator.evaluate mlContext baselineModel split.TestSet
    let candidateMetrics = Validator.evaluate mlContext candidateModel split.TestSet
    // 두 모델 모두 같은 held-out으로 평가 — 공정한 비교
    ...
}
```

추가로 retrain 함수 자체의 시그니처를 바꿔서 호출자 실수를 차단:

```fsharp
// retrain 시그니처: IDataView를 받음 (raw TrainSample[] 아님)
let retrain (mlCtx: MLContext) (trainView: IDataView) (path: string) (l2: float32) : ITransformer =
    // mlCtx.Data.LoadFromEnumerable 호출 없음 — 호출자가 이미 IDataView를 build함
    // → 호출자는 split.TrainSet을 명시적으로 넘겨야 함
    ...
```

## Trade-offs

- **첫 retrain 시 baseline 없음**: dummy baseline을 생성하거나 (e.g., random 1024-dim weights — Phase 6의 `ensureDummyModel`), 또는 첫 retrain은 항상 채택하고 두 번째부터 게이트를 적용. 후자가 더 단순.
- **Hold-out 크기**: testFraction=0.2가 표준. 너무 작으면 metric의 분산이 커지고, 너무 크면 학습 데이터가 줄어든다. 데이터셋이 ≥1000 샘플이면 0.2 안전.
- **Cross-validation 필요?**: production 게이트에서는 단일 split이 보통 충분. CV는 모델 비교 연구에 유용하지만 retraining hot path에서는 비용이 높음.

## 체크리스트

- [ ] `TrainTestSplit` 호출이 `Fit` (또는 `retrain`) 호출 *전*인가?
- [ ] retrain 함수가 `IDataView`를 받고 호출자가 `split.TrainSet`을 명시적으로 넘기는가?
- [ ] baseline과 candidate를 SAME `split.TestSet`으로 평가하는가?
- [ ] testFraction과 seed를 config로 노출했는가? (재현성)
- [ ] 첫 retrain edge case (baseline 없음)이 명시적으로 처리되는가?
- [ ] 코드 코멘트에 "train과 held-out이 겹침 — 수용 가능" 같은 변명이 없는가? (있다면 위반 신호)

## 관련 문서

- `use-semaphoreslim-not-mutex-for-async-idempotency.md` — retraining 동시성 게이트 (Lock 7)
