# Phase 6: Real ML Routing - Context

**Gathered:** 2026-05-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Replace the placeholder `ML.applyML` with real bge-m3 int8 quantized embedding + ML.NET LbfgsLogisticRegression classifier. Adapter ports `IEmbedder` + `IClassifier` defined in Core; concrete adapters (`BgeM3Embedder`, `MlNetClassifier`) live in Cli. `applyML` becomes a closed-over function via `makeApplyML embedder classifier cfg` — keeps the synchronous `RoutingAlgorithm` signature (Phase 4 ML-01 contract) using `Task.Run + GetAwaiter().GetResult()` to bridge async embed/classify calls. First-run bootstrap auto-generates a dummy 1024-dim model so `applyML` doesn't throw before Phase 7-8 produces real training data. `appsettings.json` `Routing.Algorithm` default flips from `"heuristic"` to `"ml"` at the end of this phase. Heuristic stays as dormant emergency fallback (soft-paused 2026-05-08).

</domain>

<decisions>
## Implementation Decisions

### Model storage: gitignore + auto-download from HuggingFace
- `models/` directory in `.gitignore` (router.zip, bge-m3 ONNX, tokenizer files)
- `scripts/download-models.sh` uses `huggingface-cli download Teradata/bge-m3 --local-dir models/embed/` (pre-quantized int8 ~542MB; alternative source: `gpahal/bge-m3-onnx-int8`)
- First-run startup checks for required files; if missing, logs a clear instruction ("run scripts/download-models.sh") and exits with a clear error. NOT auto-downloaded at runtime (keeps router startup fast and predictable; no network dependency in the critical path).
- README (Phase 11) documents the one-time setup step
- Self-export script `scripts/export-bge-m3-int8.sh` is also committed for reproducibility (uses `optimum-cli` + `onnxruntime.quantization.quantize_dynamic` for users who prefer self-export)

### Async/sync impedance: sync closure with Task.Run wrapper (Option A)
- `RoutingAlgorithm` signature stays sync per Phase 4 ML-01 (verified Complete; no rollback)
- `IEmbedder.EmbedAsync` and `IClassifier.PredictAsync` are async (I/O-bound; ONNX inference + tensor work)
- `makeApplyML embedder classifier cfg` returns a synchronous `RoutingAlgorithm` function that wraps the async calls with:
  ```fsharp
  Task.Run(fun () -> embedder.EmbedAsync(...)).GetAwaiter().GetResult()
  ```
- `Task.Run` is mandatory (not bare `.Result`) — explicit thread-pool execution defends against any future SyncContext (test, hosted service, etc.). The pattern is deadlock-safe.
- Cost: ~1 thread-pool thread held per request for ~20-30 ms (embed + classify). Invisible at our traffic profile (single-user Hermes + Graphify).
- The `RoutingAlgorithm` type in Domain.fs stays unchanged.

### `IEmbedder` and `IClassifier` ports — new file `Core/MLPorts.fs`
- Reasoning: Ports.fs already exists with non-ML interfaces (`IUpstreamClient`, `IClock`, `IHealthProbe`); ML-specific ports get their own file to keep concerns separated and to make the soft-paused-heuristic boundary cleaner.
- Shape:
  ```fsharp
  type IEmbedder =
      abstract member EmbedAsync : prompt: string * ct: CancellationToken
                                -> Task<float32[]>   // 1024-dim L2-normalized
  type ClassifierPrediction = { Score: float32; PredictedClass: int }
  type IClassifier =
      abstract member PredictAsync : embedding: ReadOnlyMemory<float32> * ct: CancellationToken
                                  -> Task<ClassifierPrediction>
  ```
- Threshold lives in `RoutingConfig.MlThreshold` (Core); Cli adapter doesn't see it; `makeApplyML` applies it.
- `ReadOnlyMemory<float32>` for the classifier input avoids array copies on the hot path; F# arrays implicitly convert.

### `ML.applyML` → `makeApplyML` closure pattern
- Old: `let applyML : RoutingAlgorithm = fun cfg req -> { Target = Qwen35B; ... }` (placeholder)
- New: `let makeApplyML (embedder: IEmbedder) (classifier: IClassifier) : RoutingAlgorithm = fun cfg req -> ...` (closes over ports)
- CompositionRoot calls `let mlAlgo = ML.makeApplyML embedder classifier; AddSingleton<RoutingAlgorithmRegistration>(...)` with `mlAlgo` as the function
- Keeps Core pure: `IEmbedder`/`IClassifier` are F# interfaces (just records of functions); no NuGet leaks into Core
- The closure-based pattern is parallel to `routeRequest config algorithm req` taking `algorithm` as a parameter — same idea, just baked at composition time

### `BgeM3Embedder` adapter (Cli)
- Loads `models/embed/bge-m3-int8.onnx` via `Microsoft.ML.OnnxRuntime.InferenceSession`
- Tokenization: `Microsoft.ML.Tokenizers 2.0.0`'s `SentencePieceTokenizer.Create(modelStream, addBeginOfSentence=true, addEndOfSentence=true, specialTokens)` with `models/embed/sentencepiece.bpe.model` (raw SP binary, NOT `tokenizer.json` which is HF Python-only format)
- XLM-R special tokens: `<s>` (id=0) at start, `</s>` (id=2) at end; padding token id=1
- Pipeline: tokenize → input_ids + attention_mask tensors → ONNX inference → mean-pool token embeddings using attention_mask → L2 normalize → 1024-dim float32[]
- Cap input at `Routing.ML.MaxTokens` (default 512; bge-m3 supports 8192 but routing latency budget < quality at long context)
- Warm-up: at startup, run one inference with a fixed prompt ("hello") to amortize JIT + model-load cost before first user request

### `MlNetClassifier` adapter (Cli)
- Uses `Microsoft.Extensions.ML.PredictionEnginePool<EmbedFeatures, ClassifierOutput>` registered via `AddPredictionEnginePool<...>().FromFile(modelName, filePath, watchForChanges: true)`
- `watchForChanges: true` enables Phase 8 hot-reload (when retraining writes a new router.zip, in-flight requests complete on old model, new requests use new model — atomic swap)
- LR sigmoid output → `Score` field 0..1; `PredictedClass = if Score > 0.5 then 1 else 0`
- Threshold (`cfg.MlThreshold`, default 0.5) decides 35B (0) vs 122B (1) — applied in `makeApplyML`, not in the adapter

### First-run dummy model bootstrap
- At startup (after config but before host start), check if `models/router.zip` exists
- If missing: generate dummy ML.NET LR model with random 1024-dim weights `Array.init 1024 (fun _ -> rand.NextSingle() * 0.001f - 0.0005f)`, bias 0
- Save via `mlContext.Model.Save(model, schema, "models/router.zip")`
- Log a WARNING explaining: this is a placeholder; Phase 7-8 will replace with real training data; ~50/50 routing until then
- Bootstrap lives in a new adapter `ModelBootstrapper.fs` invoked from CompositionRoot at startup
- Bootstrap is idempotent (no-op if file exists)

### `model_version` for Phase 5 DecisionLog cohort tagging
- For ML algorithm: SHA-256 of `models/router.zip` content, first 8 hex chars → `f"ml-{hash}"` (e.g., `"ml-a3f2c1b9"`)
- Computed once at startup (or on PredictionEnginePool reload event); cached
- Stored on `RoutingAlgorithmRegistration.ModelVersion` (the field already exists from Phase 5)
- For heuristic: stays `"heuristic-v1"` (no model file)
- Phase 8's retraining → new file → new hash → automatic cohort distinction in JSONL logs

### CLS-03 verification approach: embedding cosine similarity
- The original CLS-03 wording (routing-decision divergence) cannot be verified with dummy random-weight classifier (decisions are coincidental)
- Replaced (in REQUIREMENTS.md) with: cosine similarity test on bge-m3 multilingual semantics
- Concrete test cases (in MLEmbeddingTests.fs or MLRoutingTests.fs):
  - `cosine(embed("디버깅 도와줘"), embed("debug this")) > 0.7` — proves Korean ↔ English semantic alignment
  - `cosine(embed("F# 컴파일러 에러 분석"), embed("analyze F# compiler error")) > 0.7` — same
  - Optionally: `cosine(embed("hello"), embed("world")) < 0.5` — sanity check (different concepts should have lower similarity)
- This is the right test for Phase 6 because it directly verifies "bge-m3 is producing meaningful multilingual vectors" — the actual claim CLS-03 wants to make
- Full routing-decision divergence (heuristic vs ML on Korean prompts) is automatically verified later by Phase 8's validation gate when a trained classifier exists

### `Routing.Algorithm` default flip — staged at end of Phase 6
- Currently `appsettings.json` has `"Routing.Algorithm": "heuristic"`
- Plan 06-02 (after embedder + classifier confirmed working) flips this to `"ml"`
- Heuristic-related tests (RoutingTests.fs 22 tests) and StreamingTests.fs that depend on heuristic behavior must continue passing — they explicitly set `Routing:Algorithm=heuristic` via AddInMemoryCollection where they need heuristic. Verify in plan that no test relies on the implicit default.
- After flip: ML is the production default; tests that need heuristic explicitly request it; heuristic stays in code as dormant fallback per the 2026-05-08 soft-pause decision

### `Routing.ML` config section in appsettings.json
```json
"Routing": {
  "Algorithm": "ml",                              // flipped at end of Phase 6
  "ML": {
    "ModelPath": "models/router.zip",
    "EmbeddingModelPath": "models/embed/bge-m3-int8.onnx",
    "TokenizerPath": "models/embed/sentencepiece.bpe.model",
    "Threshold": 0.5,
    "MaxTokens": 512,
    "UseCoreMLEP": false
  },
  // existing keys (Threshold, Keywords, TaskTable, ModelAliases) retained for the dormant heuristic
}
```

### NuGet pins (live-verified 2026-05-08)
- `Microsoft.ML.OnnxRuntime` 1.25.1 (CPU EP default; CoreML EP available via `OrtSessionOptions.AppendExecutionProvider("CoreML")` if EMBED-03 latency exceeded)
- `Microsoft.ML.Tokenizers` 2.0.0 (BCL-aligned; SentencePiece via `SentencePieceTokenizer.Create`)
- `Microsoft.ML` 5.0.0 (LbfgsLogisticRegression, model save/load)
- `Microsoft.Extensions.ML` 5.0.0 (PredictionEnginePool with watchForChanges)
- All four pins added to `SmartRouter.Cli.fsproj`; Core gets nothing new (stays pure)

### Pure-Core invariant preserved
- `Core/MLPorts.fs`: F# interfaces only; no NuGet refs
- `Core/ML.fs`: `makeApplyML` closure; calls injected functions; no Microsoft.ML references
- `Core/Domain.fs`: no changes (RoutingConfig may gain `MlThreshold`, but that's a plain int/float field)
- All NuGet packages and concrete adapters live in Cli
- `check-routing-isolation.sh` continues to enforce zero cross-imports between Heuristic.fs and ML.fs

### Claude's Discretion
- Exact F# module/file naming within Cli (BgeM3Embedder.fs vs Adapters/BgeM3Embedder.fs)
- Internal LR model schema (single feature column "Embedding" of float32[1024]; output column "Score")
- Latency benchmark fixture (existing `MLRoutingTests.fs` vs new `MLEmbeddingTests.fs`)
- Random seed for dummy model weights (any deterministic seed; reproducible regression baseline)
- `MlThreshold` default (0.5 is fine for v1; Phase 8 validator may tune)

</decisions>

<specifics>
## Specific Ideas

- Mirror Phase 5's adapter-naming pattern: `Adapters/BgeM3Embedder.fs`, `Adapters/MlNetClassifier.fs`, `Adapters/ModelBootstrapper.fs`
- The `Task.Run + GetAwaiter().GetResult()` block should be a small named function in ML.fs (e.g., `let runSync (t: Task<'a>) : 'a = Task.Run(fun () -> t).GetAwaiter().GetResult()`) for readability
- Use `JsonNamingPolicy.SnakeCaseLower` for any new ML config records (matches Stats.fs / DecisionLog.fs precedent)
- Latency benchmark: 100 warm-path embeddings, report p50/p95/p99; assert p95 < 50ms
- Korean cosine test prompts should be both technical (matches our domain — "F# compiler error", "MLIR lowering") AND general ("debug this", "explain this") for breadth

</specifics>

<deferred>
## Deferred Ideas

- CoreML execution provider — Plan 06-02 mentions config flag (`UseCoreMLEP`) but does NOT implement; only flips on if EMBED-03 benchmark fails. Real wiring deferred to a future phase or post-v1 perf tuning.
- Full routing-decision divergence test (heuristic vs ML on Korean prompts) — deferred to Phase 8 validation gate when trained classifier exists
- Threshold tuning via validation set — Phase 8 retraining responsibility
- Model file integrity verification (SHA-256 expected) at startup — defer; corrupt files surface as ONNX load errors which are caught
- Embedding cache (LRU by prompt_hash) — out of scope; nice perf optimization for v2 if profiling shows duplicate prompts
- Multi-model embedding fallback (e.g., bge-m3 → bge-small for Korean-light traffic) — explicit v2 (ML2-04)
- Auto-download (HF CLI integration as a startup step) — operator-run setup script; no auto-download at runtime to avoid network dependency in critical path
- ONNX session warmup parallelism (run warmup on a background task in parallel with host start) — perf nice-to-have; not load-bearing

</deferred>

---

*Phase: 06-real-ml-routing*
*Context gathered: 2026-05-08*
