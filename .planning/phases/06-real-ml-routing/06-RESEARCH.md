# Phase 6: Real ML Routing — Research

**Researched:** 2026-05-08
**Domain:** ONNX Runtime + ML.NET LbfgsLogisticRegression + bge-m3 int8 + SentencePiece tokenization + F# async/sync composition
**Confidence:** HIGH on stack versions (live-verified via dotnet package search), HIGH on tokenizer choice, MEDIUM on CoreML EP activation path, MEDIUM on exact ONNX export script (live HF repos checked, some gaps noted)

---

## Summary

Phase 6 replaces the placeholder `applyML` with real bge-m3 int8 dynamic-quantized embeddings and ML.NET LbfgsLogisticRegression. All core technical questions were resolved during research. The single hardest architectural problem is the **async/sync impedance mismatch**: `RoutingAlgorithm` is a synchronous function type locked by Phase 4 (ML-01), but `EmbedAsync`/`PredictAsync` are inherently async. The correct resolution is `.GetAwaiter().GetResult()` inside the closure — acceptable for a CPU-bound embedding call on a background thread, but must be combined with `Task.Run` wrapping at the caller to avoid ASP.NET Core deadlock on the synchronization context.

The second critical decision is tokenizer. `Microsoft.ML.Tokenizers 2.0.0` (live-verified) includes `SentencePieceTokenizer.Create(Stream, addBeginOfSentence, addEndOfSentence, specialTokens)` which loads the raw `sentencepiece.bpe.model` binary. bge-m3 uses XLM-RoBERTa tokenizer which IS a SentencePiece BPE model. **Pick `Microsoft.ML.Tokenizers 2.0.0` — BERTTokenizers and BlingFire are not needed.** The key parameters are `addBeginOfSentence: true, addEndOfSentence: false` (XLM-R uses `<s>` BOS token, no EOS for feature extraction) and the tokenizer returns token IDs directly, which are fed as `int64[]` tensors to the ONNX session.

Pre-quantized bge-m3 int8 ONNX models exist on HuggingFace (`gpahal/bge-m3-onnx-int8`, `MahradHosseini/bge-m3-onnx-int8`, `Teradata/bge-m3` at ~542MB). The self-export script is well-understood. Models ship with `tokenizer.json` (HF fast tokenizer) AND `sentencepiece.bpe.model` (raw SP model) — use the raw `.model` file with `Microsoft.ML.Tokenizers`.

**Primary recommendation:** `Microsoft.ML.Tokenizers 2.0.0` + `Microsoft.ML.OnnxRuntime 1.25.1` + `Microsoft.ML 5.0.0` + `Microsoft.Extensions.ML 5.0.0`. Close over embedder+classifier into a synchronous closure using `.GetAwaiter().GetResult()` with `Task.Run` wrapping. Port shapes: `IEmbedder`/`IClassifier` live in a new `Core/MLPorts.fs`. `ML.applyML` stays in `Core/ML.fs` closed over injected F# functions.

---

## Standard Stack

### Core (Phase 6 additions to fsproj)

| Library | Version | Purpose | Confidence |
|---------|---------|---------|------------|
| `Microsoft.ML.OnnxRuntime` | **1.25.1** | Load + run bge-m3 int8 ONNX | HIGH — live NuGet verified |
| `Microsoft.ML.Tokenizers` | **2.0.0** | SentencePiece tokenizer for XLM-R/bge-m3 | HIGH — live NuGet verified; SentencePieceTokenizer.Create confirmed |
| `Microsoft.ML` | **5.0.0** | LbfgsLogisticRegression, ITransformer, MLContext | HIGH — live NuGet verified |
| `Microsoft.Extensions.ML` | **5.0.0** | PredictionEnginePool, watchForChanges | HIGH — live NuGet verified |

### Not Needed (commonly confused)

| Package | Verdict | Reason |
|---------|---------|--------|
| `Microsoft.ML.OnnxRuntime.Extensions` | NOT needed for Phase 6 | Extensions adds ortextensions tokenizer kernels (Python-generated ORT-custom-ops). bge-m3 uses no such ops; SentencePiece tokenization is done in .NET, not as an ONNX op. |
| `BERTTokenizers` | NOT needed | Handles BERT WordPiece vocab. bge-m3 uses SentencePiece BPE — Microsoft.ML.Tokenizers 2.0.0 covers this natively. |
| `BlingFire` | NOT needed | Older multilingual tokenizer library. Lacks the SentencePiece BPE API needed for bge-m3. |
| `SmartComponents.LocalEmbeddings` | NOT needed | Wraps bge-micro-v2 only (23MB, 384-dim). No bge-m3 support. Rejected per distillation docs §3.1. |

### fsproj additions (SmartRouter.Cli.fsproj)

```xml
<!-- ML stack — Phase 6 -->
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.25.1" />
<PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
<PackageReference Include="Microsoft.ML" Version="5.0.0" />
<PackageReference Include="Microsoft.Extensions.ML" Version="5.0.0" />
```

No changes to `SmartRouter.Core.fsproj` — Core has zero ML package deps (ARCH-01).

---

## Architecture Patterns

### Pattern 1: Port definitions — `Core/MLPorts.fs`

These go in a NEW file `src/SmartRouter.Core/MLPorts.fs`, NOT in Ports.fs (to keep Ports.fs focused on upstreams/clock/health). Both ports are pure F# interfaces — no ML package deps in Core.

```fsharp
// src/SmartRouter.Core/MLPorts.fs
module SmartRouter.Core.MLPorts

open System.Threading
open System.Threading.Tasks

/// Embedding port — takes a prompt string, returns 1024-dim L2-normalized float32 vector.
/// Adapter (BgeM3Embedder in Cli) implements this against OnnxRuntime.
/// ReadOnlyMemory avoids an allocation vs float32[] if the adapter can wrap its internal buffer.
type IEmbedder =
    abstract member EmbedAsync :
        prompt : string
        -> ct   : CancellationToken
        -> Task<float32[]>

/// Classifier prediction result (binary: 0=Qwen35B, 1=Qwen122B).
[<Struct>]
type ClassifierPrediction =
    { Score        : float32  // sigmoid probability 0..1; >= threshold → Qwen122B
      PredictedLabel : bool   // true = Qwen122B, false = Qwen35B (matches ML.NET convention) }

/// Classifier port — takes 1024-dim embedding, returns prediction.
/// Adapter (MlNetClassifier in Cli) implements this against PredictionEnginePool.
type IClassifier =
    abstract member PredictAsync :
        embedding : float32[]
        -> ct     : CancellationToken
        -> Task<ClassifierPrediction>
```

**Where does threshold logic live?** In `Core/ML.fs` reading a config value — NOT in MlNetClassifier. Rationale: the threshold is an operator policy decision (like ComplexityThreshold in heuristic); it belongs in Core routing logic. MlNetClassifier returns a raw probability score; Core decides the cutoff.

**`RoutingConfig` must gain a `MlThreshold` field** (float32, default 0.5f) in Domain.fs. This is a Phase 6 `RoutingConfig` extension.

### Pattern 2: The async/sync impedance mismatch — CRITICAL DESIGN DECISION

`RoutingAlgorithm = RoutingConfig -> RouterRequest -> RoutingDecision` is synchronous (locked by ML-01, Phase 4). `IEmbedder.EmbedAsync` and `IClassifier.PredictAsync` are async. These must meet.

**Recommended resolution: closure with synchronous blocking via `Task.Run(...).GetAwaiter().GetResult()`.**

Rationale: `.GetAwaiter().GetResult()` on an already-completed task is zero-overhead. The embedding inference (ONNX) is CPU-bound — no I/O waiting. HOWEVER, calling `.GetAwaiter().GetResult()` on a task that is NOT yet complete from an ASP.NET Core request thread with a synchronization context can deadlock (classic ASP.NET deadlock). The safe pattern is to run the embedding call via `Task.Run` first, THEN `.GetAwaiter().GetResult()`:

```fsharp
// In Core/ML.fs — the real applyML closure
let makeApplyML
    (embedder   : IEmbedder)
    (classifier : IClassifier)
    (mlConfig   : MlConfig)       // MlThreshold, MaxTokens
    : RoutingAlgorithm =
    fun (config: RoutingConfig) (req: RouterRequest) ->
        let prompt = req.Messages |> List.map (fun m -> m.Content) |> String.concat " "
        // Task.Run ensures execution off the ASP.NET SyncContext (avoids deadlock)
        let embedding =
            Task.Run(fun () -> embedder.EmbedAsync(prompt, CancellationToken.None).GetAwaiter().GetResult())
                  .GetAwaiter().GetResult()
        let prediction =
            Task.Run(fun () -> classifier.PredictAsync(embedding, CancellationToken.None).GetAwaiter().GetResult())
                  .GetAwaiter().GetResult()
        let target = if prediction.Score >= mlConfig.MlThreshold then Qwen122B else Qwen35B
        { Target     = target
          Priority   = Low
          Reason     = ML
          IsFallback = false }
```

**Alternative considered**: Make `RoutingAlgorithm` async (`RoutingConfig -> RouterRequest -> Task<RoutingDecision>`). REJECTED — would require updating ALL 39 existing heuristic tests, CompositionRoot, ChatCompletions endpoint, and violates ML-01 signed-off design. The synchronous closure + `Task.Run` pattern is used in production ML.NET apps and is documented as the correct approach for CPU-bound inference on the synchronization context.

**Note on CancellationToken**: Phase 6 passes `CancellationToken.None` because the synchronous `RoutingAlgorithm` signature has no CT. Phase 7+ may revisit if cancellation propagation is needed — for now embedding + classify is <50ms so CT is not critical.

### Pattern 3: `makeApplyML` in CompositionRoot

```fsharp
// In CompositionRoot.fs — "ml" branch:
| "ml" ->
    // Build adapters
    let embedder = BgeM3Embedder(mlOpts.EmbeddingModelPath, mlOpts.TokenizerPath, mlOpts.MaxTokens)
    let classifier = MlNetClassifier(sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>())
    let mlConfig = { MlThreshold = mlOpts.Threshold }
    let mlVersion = computeModelVersion mlOpts.ModelPath  // SHA-256 first 8 hex chars
    let alg = ML.makeApplyML (embedder :> IEmbedder) (classifier :> IClassifier) mlConfig
    { Algorithm    = alg
      Name         = "ml"
      ModelVersion = sprintf "ml-%s" mlVersion }
```

The `PredictionEnginePool` is registered SEPARATELY as a singleton (not as IClassifier — pool is pool, classifier wraps it):

```fsharp
services.AddPredictionEnginePool<RouteInput, RoutePrediction>()
    .FromFile(
        modelName    = "router",
        filePath     = mlOpts.ModelPath,   // "models/router.zip"
        watchForChanges = true)
|> ignore
```

`watchForChanges: true` is already correct per Phase 8 prep — the `PredictionEnginePool` must be registered as singleton (which `AddPredictionEnginePool` does by default). Do NOT register it as scoped.

### Pattern 4: `BgeM3Embedder` adapter — key code shapes

```fsharp
// src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
module SmartRouter.Cli.Adapters.BgeM3Embedder

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open Microsoft.ML.Tokenizers
open SmartRouter.Core.MLPorts

type BgeM3Embedder(onnxPath: string, tokenizerPath: string, maxTokens: int) =
    // Load tokenizer from raw sentencepiece.bpe.model binary
    // addBeginOfSentence=true (<s>), addEndOfSentence=false (XLM-R feature-extraction convention)
    let tokenizer =
        use stream = File.OpenRead(tokenizerPath)
        SentencePieceTokenizer.Create(stream, addBeginOfSentence = true, addEndOfSentence = false)

    // Load ONNX session (int8 quantized — SessionOptions default is fine for CPU)
    let session = new InferenceSession(onnxPath)

    // Warm-up call (amortize JIT + model cold-start BEFORE first user request)
    do
        let warmup = "hello"
        use _ = Task.Run(fun () ->
            let ids = tokenizer.EncodeToIds(warmup, addBeginningOfSentence = true, addEndOfSentence = false)
                      |> Seq.map int64 |> Array.ofSeq
            let mask = Array.create ids.Length 1L
            let inputIdsTensor  = new DenseTensor<int64>(ids,  ReadOnlySpan([| 1; ids.Length |]))
            let attnMaskTensor  = new DenseTensor<int64>(mask, ReadOnlySpan([| 1; mask.Length |]))
            let inputs = [
                NamedOnnxValue.CreateFromTensor("input_ids",      inputIdsTensor)
                NamedOnnxValue.CreateFromTensor("attention_mask", attnMaskTensor)
            ]
            use results = session.Run(inputs)
            ())
        |> ignore

    let meanPool (hiddenArr: float32[]) (seqLen: int) (dim: int) (maskArr: int64[]) : float32[] =
        let pooled = Array.zeroCreate dim
        let mutable validTokens = 0.0f
        for t in 0 .. seqLen - 1 do
            if maskArr[t] = 1L then
                validTokens <- validTokens + 1.0f
                for d in 0 .. dim - 1 do
                    pooled[d] <- pooled[d] + hiddenArr[t * dim + d]
        pooled |> Array.map (fun v -> v / validTokens)

    let l2Normalize (v: float32[]) : float32[] =
        let norm = sqrt (v |> Array.sumBy (fun x -> x * x))
        if norm < 1e-8f then v
        else v |> Array.map (fun x -> x / norm)

    interface IEmbedder with
        member _.EmbedAsync(prompt: string, _ct: CancellationToken) : Task<float32[]> =
            task {
                // Tokenize — truncate at MaxTokens
                let ids =
                    tokenizer.EncodeToIds(prompt, addBeginningOfSentence = true, addEndOfSentence = false,
                                          maxTokenCount = maxTokens, normalizedString = null, tokenCountExceededOffset = null)
                    |> Seq.map int64
                    |> Array.ofSeq
                let mask = Array.create ids.Length 1L
                let seqLen = ids.Length
                let inputIdsTensor  = new DenseTensor<int64>(ids,  ReadOnlySpan([| 1; seqLen |]))
                let attnMaskTensor  = new DenseTensor<int64>(mask, ReadOnlySpan([| 1; seqLen |]))
                let inputs = [
                    NamedOnnxValue.CreateFromTensor("input_ids",      inputIdsTensor)
                    NamedOnnxValue.CreateFromTensor("attention_mask", attnMaskTensor)
                ]
                use results = session.Run(inputs)
                // Output 0: last_hidden_state float32[1, seqLen, 1024]
                let hidden = results[0].AsTensor<float32>()
                let hiddenArr = hidden.ToArray()   // seqLen * 1024 elements, row-major
                let embedding = hiddenArr |> meanPool seqLen 1024 mask |> l2Normalize
                return embedding
            }

    interface IDisposable with
        member _.Dispose() =
            session.Dispose()
            tokenizer.Dispose()  // SentencePieceTokenizer implements IDisposable as of 2.0
```

**ONNX input/output names for bge-m3**: `input_ids`, `attention_mask` (NO `token_type_ids` — XLM-R does not use segment ids). Output 0 is `last_hidden_state` shape `[batch, seq, 1024]`. Output 1 and 2 are sparse/ColBERT — ignored for dense-only routing.

**Note on `EncodeToIds` signature**: In `Microsoft.ML.Tokenizers 2.0.0`, the overload used in warm-up above may need adjustment. Use the overload that accepts `addBeginningOfSentence` and `addEndOfSentence` booleans. If that overload is not available on the tokenizer directly (it IS on SentencePieceTokenizer per the docs), fall back to the base `Tokenizer.EncodeToIds(string, bool, bool)`.

### Pattern 5: `MlNetClassifier` adapter

```fsharp
// src/SmartRouter.Cli/Adapters/MlNetClassifier.fs
module SmartRouter.Cli.Adapters.MlNetClassifier

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.ML
open Microsoft.ML.Data
open SmartRouter.Core.MLPorts

[<CLIMutable>]
type RouteInput = {
    [<VectorType(1024)>]
    Features : float32[]
    Label    : bool   // dummy for prediction; required by ML.NET schema
}

[<CLIMutable>]
type RoutePrediction = {
    [<ColumnName("PredictedLabel")>]
    Predicted   : bool
    Score       : float32
    Probability : float32
}

type MlNetClassifier(pool: PredictionEnginePool<RouteInput, RoutePrediction>) =
    interface IClassifier with
        member _.PredictAsync(embedding: float32[], _ct: CancellationToken) : Task<ClassifierPrediction> =
            task {
                let input = { Features = embedding; Label = false }
                let pred = pool.Predict(modelName = "router", example = input)
                return { Score = pred.Probability; PredictedLabel = pred.Predicted }
            }
```

### Pattern 6: Dummy model bootstrap

At startup (before the ML registration completes), check for `models/router.zip`. If absent, generate and save a random LR model:

```fsharp
// src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs
module SmartRouter.Cli.Adapters.ModelBootstrapper

open System
open System.IO
open Microsoft.ML
open Microsoft.ML.Data
open Serilog

[<CLIMutable>]
type DummyInput = {
    [<VectorType(1024)>]
    Features : float32[]
    Label    : bool
}

let ensureDummyModel (modelPath: string) : unit =
    if not (File.Exists(modelPath)) then
        Log.Warning(
            "No ML model found at {ModelPath}. Generating random dummy 1024-dim model. " +
            "Routing will be ~50/50 until Phase 7-8 generate real training data.",
            modelPath)
        let dir = Path.GetDirectoryName(modelPath)
        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore

        let mlContext = MLContext(seed = 42)
        let rng = Random(42)
        // 200 samples with random features and balanced labels
        let samples = [|
            for i in 0 .. 199 ->
                { Features = Array.init 1024 (fun _ -> float32 (rng.NextDouble() * 0.002 - 0.001))
                  Label    = i % 2 = 0 }
        |]
        let dataView = mlContext.Data.LoadFromEnumerable(samples)
        let pipeline =
            mlContext.BinaryClassification.Trainers.LbfgsLogisticRegression(
                labelColumnName   = "Label",
                featureColumnName = "Features",
                l2Regularization  = 1.0f)
        let model = pipeline.Fit(dataView)
        let schema = dataView.Schema

        // Atomic write via temp file + rename (avoids partial-file load by PredictionEnginePool watcher)
        let tmp = modelPath + ".tmp"
        mlContext.Model.Save(model, schema, tmp)
        File.Move(tmp, modelPath, overwrite = true)
        Log.Information("Dummy model written to {ModelPath}", modelPath)
```

Called from CompositionRoot BEFORE `AddPredictionEnginePool` registers the pool (otherwise the pool fails on missing file).

### Pattern 7: `model_version` computation

Compute once at startup by reading the file and hashing:

```fsharp
let computeModelVersion (modelPath: string) : string =
    if File.Exists(modelPath) then
        use stream = File.OpenRead(modelPath)
        let hash = System.Security.Cryptography.SHA256.Create().ComputeHash(stream)
        hash |> Array.take 4 |> Array.map (sprintf "%02x") |> String.concat ""
    else
        "unknown"
// Result: "ml-a1b2c3d4" style (8 hex chars = 4 bytes = enough for cohort tagging)
```

Compute ONCE at startup, store in `RoutingAlgorithmRegistration.ModelVersion`. Re-compute when `PredictionEnginePool` hot-reloads (Phase 8 concern — for Phase 6, once at startup is sufficient). Do NOT re-compute per request.

---

## bge-m3 ONNX Export Script

Pre-quantized models are available on HuggingFace and can be used directly. However, the canonical self-export script must live at `scripts/export-bge-m3-int8.sh`:

```bash
#!/usr/bin/env bash
# scripts/export-bge-m3-int8.sh
# Exports bge-m3 to int8 ONNX. Requires Python 3.10+, optimum[onnxruntime], onnxruntime.
# Approximate output size: ~542-580 MB (int8) vs 2.3 GB (FP32)
set -euo pipefail

MODEL_DIR="models"
mkdir -p "$MODEL_DIR"

# Option A: Self-export (requires ~6 GB disk + ~4 GB RAM during export)
# Step 1: Export to FP32 ONNX (opset 17)
optimum-cli export onnx \
  --model BAAI/bge-m3 \
  --task feature-extraction \
  --opset 17 \
  --framework pt \
  "$MODEL_DIR/bge-m3-fp32/"

# Step 2: Int8 dynamic quantization
python3 - <<'EOF'
from onnxruntime.quantization import quantize_dynamic, QuantType, QuantizationMode
quantize_dynamic(
    model_input="models/bge-m3-fp32/model.onnx",
    model_output="models/bge-m3-int8.onnx",
    weight_type=QuantType.QInt8,
    optimize_model=True,
    # Quantize all matmul/gemm ops
    op_types_to_quantize=["MatMul", "Gather", "EmbedLayerNormalization"]
)
print("Done. Output: models/bge-m3-int8.onnx")
EOF

# Copy tokenizer files needed by .NET
cp "$MODEL_DIR/bge-m3-fp32/sentencepiece.bpe.model" "$MODEL_DIR/bge-m3-tokenizer.model"
echo "Files ready: models/bge-m3-int8.onnx + models/bge-m3-tokenizer.model"

# Option B: Download pre-quantized (faster, no Python ML stack needed)
# huggingface-cli download gpahal/bge-m3-onnx-int8 --local-dir models/bge-m3-prebuilt/
# Then symlink: ln -s bge-m3-prebuilt/model.onnx models/bge-m3-int8.onnx
#               cp bge-m3-prebuilt/sentencepiece.bpe.model models/bge-m3-tokenizer.model
```

**Tokenizer file needed**: `sentencepiece.bpe.model` (raw SentencePiece binary). This is included in the `bge-m3-fp32/` export output and in all HF pre-quantized repos alongside `tokenizer.json`. Use the `.model` file (not `tokenizer.json`) for `Microsoft.ML.Tokenizers.SentencePieceTokenizer.Create`.

**File sizes**:
- `model.onnx` (FP32 export): ~2.27 GB
- `bge-m3-int8.onnx` (after quantization): ~542-580 MB (verified via Teradata/bge-m3 HF repo at 542.57 MB)
- `sentencepiece.bpe.model`: ~5 MB

**Storage decision: gitignore + script (see Open Questions section).**

---

## Routing.ML Config Section Schema

Add to `appsettings.json` under `"Routing"`:

```json
"ML": {
  "ModelPath":          "models/router.zip",
  "EmbeddingModelPath": "models/bge-m3-int8.onnx",
  "TokenizerPath":      "models/bge-m3-tokenizer.model",
  "Threshold":          0.5,
  "MaxTokens":          512,
  "UseCoreMLEP":        false
}
```

Add to `RoutingOptions` record in `CompositionRoot.fs`:

```fsharp
[<CLIMutable>]
type MlOptions =
    { ModelPath          : string
      EmbeddingModelPath : string
      TokenizerPath      : string
      Threshold          : float32
      MaxTokens          : int
      UseCoreMLEP        : bool }
```

Add `ML : MlOptions` field to `RoutingOptions`. Bind via `config.GetSection("Routing:ML").Get<MlOptions>()`.

---

## Korean-Divergence Test Prompt Set (CLS-03)

Three prompts with predicted divergence direction between heuristic and bge-m3 ML:

| # | Prompt | Heuristic decision | bge-m3 ML predicted | Diverges? |
|---|--------|-------------------|---------------------|-----------|
| 1 | `"F# 컴파일러 에러 분석해 줘"` | 35B (no English keywords match) | 122B (multilingual semantics align with "compiler error analysis") | **YES** — BEST |
| 2 | `"디버깅 도와줘"` | 35B (no English keywords; short prompt) | 122B (cross-lingual similarity to "help me debug" ≈ 0.78 per distillation docs §1.7.1) | **YES — use this** |
| 3 | `"MLIR lowering 단계에서 문제"` | 122B ("mlir" keyword present in English) | 122B (multilingual + MLIR token) | NO divergence |
| 4 | `"한국어로 짧게 답변해줘"` | 35B (short, no keywords) | 35B (simple request) | NO divergence |
| 5 | `"타입 추론 오류 왜 발생하는 거야?"` | 35B (no English "type inference" keyword) | 122B (Korean "타입 추론" = "type inference" in multilingual embedding space) | **YES — secondary** |

**For CLS-03 test**: use prompts #1 and #2. Prompt #1 (`"F# 컴파일러 에러 분석해 줘"`) is the strongest because:
- Heuristic has zero matching keywords (all Korean)
- bge-m3 reads "F# compiler error analysis" multilingually and scores it near 122B-class prompts
- With the dummy bootstrap model (random weights), this test will NOT pass (random ~50/50)
- Test must use a bge-m3 model + a classifier trained on at least a few samples — OR assert that the dummy model diverges *probabilistically* (statistically unlikely, not guaranteed)

**Practical CLS-03 test approach**: Rather than requiring a trained classifier (Phase 7-8 produce training data), CLS-03 can be satisfied by asserting that the bge-m3 embedding of `"디버깅 도와줘"` has cosine similarity ≥ 0.6 with the embedding of `"help me debug this crash"` — proving the multilingual model is working. The actual routing divergence test can use a hand-crafted mini-classifier (2 training samples) seeded to produce the expected outcome.

---

## Latency Budget: 50ms p95 (EMBED-03)

### Warm-path target
- bge-m3 int8 on M-series CPU: documented 20-30ms per embedding (distillation docs §1.7.7)
- ML.NET LR prediction: <1ms
- Total ML path: ~25-35ms warm → well under 50ms p95

### Benchmark approach
Place in `MLRoutingTests.fs` (NOT LoadTests.fs — LoadTests is for concurrency; this is latency):

```fsharp
testCase "EMBED-03: p95 embedding latency under 50ms warm" <| fun () ->
    // Run 110 embeddings on varied prompts; skip first 10 (cold); measure p95 of warm 100
    let embedder : IEmbedder = ...  // resolve from DI or construct directly
    let prompts = Array.init 110 (fun i -> sprintf "prompt %d: some content here" i)
    let latencies = ResizeArray<int64>()
    for p in prompts do
        let sw = System.Diagnostics.Stopwatch.StartNew()
        embedder.EmbedAsync(p, CancellationToken.None).GetAwaiter().GetResult() |> ignore
        sw.Stop()
        latencies.Add(sw.ElapsedMilliseconds)
    let warm = latencies |> Seq.skip 10 |> Seq.sort |> Array.ofSeq
    let p95 = warm[int (float warm.Length * 0.95)]
    Expect.isLessThan p95 50L "p95 embedding latency must be under 50ms"
```

### CoreML EP fallback (if p95 > 50ms)
If CPU latency exceeds budget under load, activate CoreML EP in the ONNX session options:

```fsharp
let sessionOpts = new SessionOptions()
if useCoreMLEP then
    // Requires macOS 10.15+; bundled in Microsoft.ML.OnnxRuntime 1.25.1 for osx-arm64
    sessionOpts.AppendExecutionProvider_CoreML(
        uint32 CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH) |> ignore
let session = new InferenceSession(onnxPath, sessionOpts)
```

`CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH` (=1) allows CoreML to run on control-flow subgraphs. The `Microsoft.ML.OnnxRuntime` 1.25.1 NuGet package includes arm64 native libraries and the CoreML EP for macOS. No separate package is needed.

---

## Routing.Algorithm Default Flip

**Recommendation: flip `"heuristic"` to `"ml"` in `appsettings.json` ONLY; keep tests using explicit `Routing:Algorithm` overrides.**

Rationale: The existing tests that need heuristic should explicitly set `"Routing:Algorithm": "heuristic"` via `AddInMemoryCollection`. Tests that need ML explicitly set `"Routing:Algorithm": "ml"`. The base `appsettings.json` default flips to `"ml"` as the production default (per operator decision 2026-05-08). This is already done by existing tests that pass `AddInMemoryCollection(dict ["Routing:Algorithm", "ml"])` — no test refactoring needed, just changing the default in the JSON file.

Test files that currently rely on `appsettings.json: "Algorithm": "heuristic"` baseline: `MLRoutingTests.fs` tests 4+5 both override to `"ml"` explicitly via `AddInMemoryCollection`. No other test files depend on the Algorithm default. **Flip is safe.**

---

## Test Plan

### Existing tests that need updating
- `MLRoutingTests.fs` test 2: `"ML.applyML always returns Qwen35B"` — this will FAIL once applyML is real (it no longer always returns 35B). Must be updated or deleted. **Delete or replace with real-behavior assertion.**
- No other MLRoutingTests fail (tests 1, 3, 4, 5 are structural/dispatch tests that don't depend on placeholder behavior).

### New test file: `MLEmbeddingTests.fs` (or add to MLRoutingTests.fs)

Organized by requirement:

```
EMBED-01: IEmbedder produces 1024-dim L2-normalized vectors (3 prompts: en/ko/mixed)
          — assert Array.length = 1024
          — assert norm ≈ 1.0 (within 1e-3)
          — assert 3 different prompts produce 3 different vectors (not all zeros)

EMBED-02: IClassifier loads dummy model and predicts on 1024-dim input
          — load a pre-generated dummy router.zip
          — predict on random 1024-dim embedding
          — result.Score is in [0..1], result.PredictedLabel is bool

CLS-01: BgeM3Embedder adapter resolves from DI when Routing:Algorithm=ml
CLS-02: MlNetClassifier adapter resolves from DI; PredictionEnginePool registered
CLS-03: Korean divergence — bge-m3 embedding of "디버깅 도와줘" has cosine similarity
        ≥ 0.6 with "help me debug this crash" (proves multilingual model, not random weights)
        AND: with a micro-trained classifier, heuristic and ML produce different decisions
        on "F# 컴파일러 에러 분석해 줘"

BOOTSTRAP: models/router.zip missing → startup auto-creates dummy model → applyML doesn't throw
MODEL_VERSION: after ML path runs, DecisionLog model_version = "ml-{8hexchars}" (not "ml-v0-placeholder")
EMBED-03: p95 latency benchmark (100 warm embeddings) < 50ms
```

All ML tests must be in `testSequenced` (they touch shared filesystem state: `models/`). Use a temp directory override for model paths in tests to avoid clobbering production `models/`.

---

## Common Pitfalls

### Pitfall 1: SentencePiece tokenizer loads `.model`, not `tokenizer.json`
`Microsoft.ML.Tokenizers.SentencePieceTokenizer.Create` takes a `Stream` of the raw SentencePiece `.proto` binary (`sentencepiece.bpe.model`). It does NOT parse HuggingFace `tokenizer.json` (fast tokenizer format). The `tokenizer.json` is for the Python transformers library. Always copy `sentencepiece.bpe.model` from the export, NOT `tokenizer.json`.

**Warning sign**: `InvalidOperationException: Failed to parse protobuf` or random garbage token IDs when encoding Korean text.

### Pitfall 2: ONNX int8 quantization with wrong op_types
`optimum-cli onnxruntime quantize --avx2` uses AVX2 instructions which may not be present on ARM (M-series). Use `quantize_dynamic` with default CPU operators instead. On M-series, AVX2-quantized models run via x86 emulation (slower). Prefer `--arm64` flag if available in the installed optimum version, OR use onnxruntime's native `quantize_dynamic` without AVX2 hint.

**Warning sign**: Model loads but CPU latency is 3-4x worse than expected on M-series.

### Pitfall 3: `[<CLIMutable>]` missing on F# record types
ML.NET uses reflection to set properties on prediction input/output types. F# records are immutable by default. Without `[<CLIMutable>]`, ML.NET silently fails to populate output fields, returning default values (Score=0, Probability=0). Add `[<CLIMutable>]` to BOTH `RouteInput` AND `RoutePrediction`.

**Warning sign**: `pool.Predict(...)` returns `Probability = 0.0f` for all inputs.

### Pitfall 4: PredictionEnginePool registered as non-singleton
`AddPredictionEnginePool` returns an `IServiceCollection` that registers the pool as singleton. If you accidentally wrap in `AddScoped`, each request creates a new pool instance (expensive) and `watchForChanges` is reset. Always use `AddSingleton`/`AddPredictionEnginePool` (which is singleton by default).

**Warning sign**: Memory climbs linearly with requests; model reload events appear every request.

### Pitfall 5: Dummy model bootstrap writes AFTER pool registration
`AddPredictionEnginePool.FromFile` calls `File.Exists` at startup when the pool is first resolved. If `ensureDummyModel` runs AFTER the pool is registered but BEFORE first resolution, it works. But if the pool is resolved during `services.BuildServiceProvider()` itself (some DI containers do eager validation), the file must exist before `configureServices` returns. **Safe pattern**: call `ensureDummyModel` as the FIRST line of the `"ml"` branch in CompositionRoot before any pool registration.

**Warning sign**: `FileNotFoundException: models/router.zip` at startup.

### Pitfall 6: Mean pooling divides by zero on empty attention mask
If `maxTokens` truncates ALL tokens (extremely short prompt with tokenizer overhead), `validTokens` is 0 and division produces NaN. Add guard: `if validTokens < 1.0f then validTokens <- 1.0f`.

**Warning sign**: Embedding contains NaN; classifier returns NaN probability; routing always falls to default branch.

### Pitfall 7: ONNX output index for bge-m3 dense embeddings
bge-m3 ONNX (exported with `--task feature-extraction`) may output ONLY `last_hidden_state` (shape `[1, seq, 1024]`) as output 0. However, some pre-quantized HF repos include all three outputs (dense + sparse + ColBERT). Read `results[0]` for `last_hidden_state`. If using a pre-built repo that outputs a different order, verify with `session.OutputMetadata` at startup.

### Pitfall 8: ASP.NET Core synchronization context deadlock
Calling `.GetAwaiter().GetResult()` directly on an async method inside the synchronous `applyML` closure, when called from the ASP.NET request handler on the ASP.NET SyncContext, will deadlock if the async method `await`s something that needs to return to the SyncContext. **Always use `Task.Run(fun () -> ...).GetAwaiter().GetResult()`** to move execution off the SyncContext first. CPU-bound ONNX inference does not have this issue per se, but the `task {}` CE in `EmbedAsync` may capture the SyncContext. The `Task.Run` wrapper is the safest pattern.

### Pitfall 9: models/ directory in .gitignore
The bge-m3 int8 ONNX file is ~542MB — do NOT commit. Add `models/` to `.gitignore` (only `models/` dir, not the script). The dummy `router.zip` is also auto-generated at runtime — do not commit it. `scripts/export-bge-m3-int8.sh` DOES get committed.

### Pitfall 10: ML.NET 5.0.0 `VectorType` attribute and `[<VectorType(1024)>]` in F#
The `VectorType` attribute must match the actual feature vector dimension exactly. If the model was trained with 384-dim features but inference uses 1024-dim, ML.NET will throw at `pool.Predict` time. Verify `[<VectorType(1024)>]` on `RouteInput.Features` and that the saved `.zip` model schema matches.

---

## Open Questions for User Before Planning

1. **Model storage: gitignore + auto-download vs Git LFS vs manual**
   - Recommendation: gitignore `models/` + commit `scripts/export-bge-m3-int8.sh` + add a `scripts/download-bge-m3-int8.sh` that uses `huggingface-cli download gpahal/bge-m3-onnx-int8 --local-dir models/` as the fast path.
   - Git LFS adds billing complexity; 542MB is too large to commit bare.
   - Operator decision needed: Which HF pre-quantized repo to use (gpahal, MahradHosseini, or self-export)? Or does the operator want to self-export for reproducibility?
   - **Impact on plan**: affects what goes in 06-01-PLAN steps.

2. **Tokenizer file name in repo**: `sentencepiece.bpe.model` vs `tokenizer.model`
   - The export script copies it to `models/bge-m3-tokenizer.model`. The config key `TokenizerPath` points there. No user decision needed — just confirming convention.

3. **async/sync mismatch resolution: confirmed?**
   - Research recommends `Task.Run(...).GetAwaiter().GetResult()` closure pattern.
   - Alternative (change RoutingAlgorithm to async) would be a large Phase 4 rollback.
   - **User must confirm** the synchronous closure pattern is acceptable before planning starts.

4. **ML.NET vs raw OrtSession for LR inference**
   - ML.NET + PredictionEnginePool adds overhead: serialization to DataView, pool management, schema validation. Total overhead for LR: ~0.5-2ms.
   - Raw OrtSession for LR would require exporting the LR weights as an ONNX model separately — unusual; ML.NET keeps the LR inside the `.zip`.
   - **Recommendation: ML.NET + PredictionEnginePool** — the overhead is negligible vs embedding latency, and PredictionEnginePool's `watchForChanges` is a Phase 8 requirement.
   - No user decision needed unless strict <1ms classifier budget is required.

5. **model_version hash: 8-char SHA-256 confirmed?**
   - Research recommends 8 hex chars (4 bytes). Sufficient for cohort tagging (Phase 9). Not a user decision unless they prefer full hash.

6. **CLS-03 Korean divergence test with dummy model: how to satisfy?**
   - The dummy bootstrap model has random weights → routing is ~50/50, not guaranteed to diverge from heuristic on specific prompts.
   - Option A: CLS-03 tests that bge-m3 EMBEDDING (not full pipeline) correctly represents Korean semantics (cosine similarity test) — this is testable without a trained classifier.
   - Option B: Seed the dummy model with biased weights that make "F# 컴파일러 에러 분석해 줘" route 122B.
   - Option C: CLS-03 deferred to Phase 7 when real training data exists.
   - **Recommend Option A** (embedding cosine similarity) for Phase 6 with Option C reserved for Phase 8's validation gate. User confirmation needed.

---

## Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Microsoft.ML.Tokenizers 2.0.0` | `BERTTokenizers 1.2.0` | BERTTokenizers handles WordPiece only — cannot load SentencePiece `.model` file. Not suitable for bge-m3. |
| `Microsoft.ML.Tokenizers 2.0.0` | `BlingFire` | BlingFire can handle XLM-R but requires `.bin` tokenizer binary from a separate BlingFire-format export, not the HF SentencePiece `.model`. Extra conversion step. |
| `Microsoft.ML 5.0.0` + PredictionEnginePool | Raw logistic regression in F# | Bypasses ML.NET entirely. Simpler for Phase 6 but loses `watchForChanges` hot-reload (Phase 8 requirement). Not recommended. |
| gitignore models/ + script | Git LFS | LFS adds billing complexity; no benefit for a local-only project with one developer. |
| `Task.Run().GetAwaiter().GetResult()` | Make RoutingAlgorithm async | Async change would break ML-01 and require updating ~40 test callsites. Not worth it for Phase 6. |

---

## Sources

### Primary (HIGH confidence — live-verified)
- `dotnet package search Microsoft.ML.OnnxRuntime` — **1.25.1** (2026-05-08)
- `dotnet package search Microsoft.ML` — **5.0.0** (2026-05-08)
- `dotnet package search Microsoft.Extensions.ML` — **5.0.0** (2026-05-08)
- `dotnet package search Microsoft.ML.Tokenizers` — **2.0.0** (2026-05-08)
- `dotnet package search BERTTokenizers` — **1.2.0** (2026-05-08; confirmed not needed)
- [Microsoft Docs: SentencePieceTokenizer.Create](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers.sentencepiecetokenizer.create?view=ml-dotnet-preview) — confirmed `Stream, addBeginOfSentence, addEndOfSentence, specialTokens` signature
- [Microsoft Docs: SentencePieceTokenizer class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers.sentencepiecetokenizer?view=ml-dotnet-preview) — `EncodeToIds`, `BeginningOfSentenceToken`, `EndOfSentenceToken` properties confirmed
- [Microsoft Docs: PredictionEnginePool in ASP.NET Core](https://learn.microsoft.com/en-us/dotnet/machine-learning/how-to-guides/serve-model-web-api-ml-net) — `AddPredictionEnginePool.FromFile(modelName, filePath, watchForChanges)` signature confirmed
- [ONNX Runtime CoreML EP docs](https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html) — macOS 10.15+ requirement, `AppendExecutionProvider_CoreML` C# API confirmed

### Secondary (MEDIUM confidence)
- [HuggingFace: gpahal/bge-m3-onnx-int8](https://huggingface.co/gpahal/bge-m3-onnx-int8) — pre-quantized int8 model confirmed available; opset 17 + O2 optimization
- [HuggingFace: Teradata/bge-m3](https://huggingface.co/Teradata/bge-m3) — int8 file size ~542.57 MB confirmed
- [smart-router-distillation/documentation/embedding-classifier-decision-deep-dive.md](file:///Users/ohama/projs/smart-router-distillation/documentation/embedding-classifier-decision-deep-dive.md) — §1.7.1 Korean embedding, §1.7.6 bge-m3 SentencePiece specifics, §1.7.7 latency estimates (20-30ms int8 CPU)
- [smart-router-distillation/documentation/auto-retraining-research.md](file:///Users/ohama/projs/smart-router-distillation/documentation/auto-retraining-research.md) — §3.1 package matrix (SmartComponents rejected, OnnxRuntime direct recommended)
- HuggingFace quantization forum — int8 accuracy regression typically <2%, confirmed

### Tertiary (LOW confidence — verify at build time)
- ONNX export opset compatibility: opset 17 with `quantize_dynamic` — spot-checked via forum threads; confirmed working but exact operator set for XLM-R at opset 17 should be verified during 06-01
- CoreML EP availability in `Microsoft.ML.OnnxRuntime 1.25.1` NuGet on osx-arm64 — search confirmed arm64 binary inclusion but exact `AppendExecutionProvider_CoreML` API surface in the NuGet .NET wrapper needs verification at build time

---

## Metadata

**Confidence breakdown:**
- Standard stack versions: HIGH — live NuGet verified
- Tokenizer decision: HIGH — Microsoft.ML.Tokenizers 2.0.0 SentencePieceTokenizer.Create signature verified from official docs
- Port shapes + F# patterns: HIGH — grounded in existing codebase + distillation research
- Async/sync resolution: HIGH — well-known ASP.NET Core pattern; Task.Run pattern is canonical
- ONNX export script: MEDIUM — optimum-cli parameters confirmed, opset 17 confirmed, but test-run at export time needed
- CoreML EP C# API: MEDIUM — documented but exact method name in 1.25.1 .NET binding needs build-time verification
- Korean divergence test prompts: MEDIUM — based on documented similarity scores from distillation research (§1.7.1)

**Research date:** 2026-05-08
**Valid until:** 2026-06-08 (NuGet versions should be stable; Microsoft.ML.Tokenizers API may add features)
