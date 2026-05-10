# 학습 전 / 후 — 어떤 파일이 다른가

**작성:** 2026-05-10
**대상:** 이 프로젝트의 ML 라우팅 동작을 디버그하거나 retrain 결과를 검증하려는 운영자 / 개발자.

## 결론

**`models/router.zip` 한 파일** 이 학습 전/후의 차이점이다. 그 안의 ML.NET LbfgsLogisticRegression 가중치 행렬이 다르다.

embedder (bge-m3) 와 토크나이저는 **사전학습된 foundation model** 로 우리가 학습하지 않는다 — 두 상태에서 동일.

## 두 상태

| 상태 | 파일 내용 출처 | 라우팅 분포 | model_version |
|---|---|---|---|
| **학습 전 (cold-start)** | `ModelBootstrapper.ensureDummyModel` 가 `Random(seed=42)` 로 1024-dim × ±0.001 무작위 가중치 200 샘플 fit | ~50/50 (의미 없음) | `ml-{router.zip 의 SHA-256 첫 8 hex}` |
| **학습 후** | `RetrainingService.runRetrain` 가 `datasets/hard-cases.jsonl` + 누적 training set 으로 LR 재학습 후 atomic `File.Move(overwrite=true)` 로 덮어씀 | 분류기 결정 (예: 25/75 — 122B/35B) | 새 SHA-8 (가중치 변하면 SHA 도 변함) |

## 파일 분류

### 학습 대상 (1 개)

| 파일 | 크기 | 역할 |
|---|---|---|
| `models/router.zip` | ~수 KB | LbfgsLogisticRegression 가중치. cold-start 시 더미 무작위 → retrain 후 데이터 기반 |

### 학습 안 함 — foundation (3 개)

| 파일 | 크기 | 역할 |
|---|---|---|
| `models/embed/bge-m3-int8.onnx` | ~542 MB | 프롬프트 → 1024-dim 임베딩. Teradata/bge-m3 사전학습. `scripts/download-models.sh` 로 받기만 함 |
| `models/embed/sentencepiece.bpe.model` | ~5 MB | XLM-R 호환 SentencePiece 토크나이저 |
| `models/embed/tokenizer.json` | ~17 MB | 토크나이저 메타데이터 (일부 Microsoft.ML.Tokenizers 코드 경로가 선호) |

bge-m3 는 입력 → 벡터 함수가 고정. 그 위의 **얇은 LR 분류기 (router.zip) 만** 학습된다.

### 학습 입력 (2 개)

| 파일 | 역할 |
|---|---|
| `datasets/hard-cases.jsonl` | Phase 7 TeacherLabeler 가 label 한 (prompt, ROUTE_35B \| ROUTE_122B) 페어. 매 retrain 의 새 학습 데이터 |
| `datasets/training-set.jsonl` | Phase 8 의 누적 training set (70/30 blend 의 30 % 부분). 매 retrain 후 갱신 |

### 학습 결과 사이드 파일 (3 개)

| 파일 | 역할 |
|---|---|
| `models/router.zip.prev` | 직전 모델 백업 — Phase 9 promote / rollback 시 atomic swap 의 한 쪽 |
| `datasets/retraining-state.json` | 마지막 retrain 시각, 입력 sample 카운트, 결과 model SHA |
| `logs/retraining-rejections.jsonl` | Validator 가 fallback_rate / accuracy 게이트에서 거부한 candidate 모델 로그 (반려 사유 포함) |

### 카나리 (선택; 0 또는 1 개)

| 파일 | 역할 |
|---|---|
| `models/router-canary.zip` | 카나리 cohort (default 10 %) 가 사용하는 별도 LR 분류기. 존재하면 ICanaryGate 가 활성화. `/canary/promote` 시 → router.zip 으로 atomic move |

## 코드 분기점

cold-start (더미 생성):
```
src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs:64
  let ensureDummyModel (logger: ILogger) (modelPath: string) : unit =
      if not (File.Exists modelPath) then
          // Random(seed=42) → 200 sample × 1024-dim × ±0.001 → LR fit → atomic temp+rename
```

학습 (덮어쓰기):
```
src/SmartRouter.Cli/Adapters/RetrainingService.fs:199-204
  // 8b. Atomic swap: candidate → router.zip
  File.Move(candidatePath, options.ModelPath, overwrite = true)
  let newVersion = computeModelVersion options.ModelPath
  versionProvider.Update(sprintf "ml-%s" newVersion)
```

`versionProvider.Update` 가 `IModelVersionProvider` 의 mutable 필드를 변경 → 다음 request 의 `ML.makeApplyML` 클로저가 live read 로 새 model_version 을 읽음 (issue #12 fix 후).

## 외부에서 구분하는 방법

```bash
# /stats 로 현재 라이브 model_version 확인
curl -s localhost:4000/stats | jq .baseline_model_version
# 첫 startup → "ml-0de83f0e" (예; ensureDummyModel 결과의 SHA-8)
# retrain 후    → 다른 SHA-8 prefix
```

```bash
# DecisionLog 에서 retrain 횟수 = 등장한 model_version 종류 수
jq -r '.model_version' logs/decisions/$(date +%F).jsonl \
  | sort | uniq -c
#   42 ml-0de83f0e   ← 학습 전 시기
#   18 ml-95267bb0   ← 첫 retrain 후
```

```bash
# router.zip 의 실제 SHA-8 (model_version 의 출처)
sha256sum models/router.zip | cut -c1-8
# 또는 /stats 의 baseline_model_version 끝 8 hex 와 동일해야 함
```

```bash
# router.zip 더미 vs 학습본 빠른 비교 (가중치 분포 — 정확하지 않지만 휴리스틱)
unzip -p models/router.zip 'TransformerChain-*' 2>/dev/null | wc -c
# 더미: ~수 KB (200 sample 학습)
# 학습본: hard-cases.jsonl 크기에 따라 다름; 보통 수~수십 KB
```

## 운영 체크리스트

- [ ] cold-start 후 `/stats baseline_model_version` 이 "ml-..." 형태 (8-hex) 인가
- [ ] retrain 트리거 후 (수동 `dotnet run -- --retrain` 또는 PeriodicTimer 자동) `model_version` 이 변경되었는가
- [ ] DecisionLog JSONL 에 새 `model_version` 이 새 row 들에 반영되는가 (issue #12 fix 후 live 갱신)
- [ ] `models/router.zip.prev` 가 직전 모델로 갱신되어 있는가 (rollback 가능 상태)
- [ ] `logs/retraining-rejections.jsonl` 에 거부 로그가 있는가 — 있으면 candidate 가 baseline 보다 안 좋아서 swap 안 일어났다는 뜻

## 관련 문서

- 코드 흐름 — `src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs`, `Retrainer.fs`, `RetrainingService.fs`
- DI 와이어링 — `src/SmartRouter.Cli/CompositionRoot.fs` 의 ML branch
- model_version live read 패턴 — `documentation/howto/avoid-closure-capture-of-mutable-state.md` (issue #12)
- 카나리 promote/rollback 흐름 — `README.md` § 12.3
