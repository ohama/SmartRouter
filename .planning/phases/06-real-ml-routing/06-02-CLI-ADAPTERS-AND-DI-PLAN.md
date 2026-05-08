---
phase: 06-real-ml-routing
plan: 02
type: execute
wave: 2
depends_on: ["06-01"]
files_modified:
  - src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
  - src/SmartRouter.Cli/Adapters/MlNetClassifier.fs
  - src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs
  - src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs
  - src/SmartRouter.Cli/CompositionRoot.fs
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - src/SmartRouter.Cli/appsettings.json
  - src/SmartRouter.Core/ML.fs
autonomous: true

must_haves:
  truths:
    - "POST /v1/chat/completions with `Routing.Algorithm=ml` runs the real `makeApplyML` closure: embed → classify → threshold → RoutingDecision{Reason=ML}."
    - "Cold start: if `models/router.zip` is missing, ModelBootstrapper writes a dummy 1024-dim LR model (random weights, balanced labels) atomically (temp + rename) BEFORE PredictionEnginePool registers; first request never throws FileNotFoundException."
    - "If `models/embed/bge-m3-int8.onnx` or `models/embed/sentencepiece.bpe.model` is missing at startup, the router logs a clear error pointing at `scripts/download-models.sh` and exits non-zero (no cryptic ONNX load error)."
    - "BgeM3Embedder warms up by running a single throwaway inference at construction so the first user request hits a JIT-warm session."
    - "MlNetClassifier wraps PredictionEnginePool with watchForChanges:true; future router.zip writes (Phase 8) cause atomic hot-reload."
    - "RoutingAlgorithmRegistration.ModelVersion for ML is `ml-{first 8 hex chars of SHA-256(router.zip)}` (e.g., `ml-a3f2c1b9`); recomputed once at startup."
    - "appsettings.json carries a new `Routing.ML` section (ModelPath, EmbeddingModelPath, TokenizerPath, Threshold, MaxTokens, UseCoreMLEP); RoutingConfig.MlThreshold reads from `Routing.ML.Threshold`."
    - "appsettings.json `Routing.Algorithm` default flips from `\"heuristic\"` to `\"ml\"` AS THE LAST EDIT of the plan (after embedder + classifier verified working in dotnet build/test)."
    - "Heuristic stays in the codebase as dormant fallback: setting `Routing.Algorithm=heuristic` (config or `--routing-algorithm=heuristic` CLI flag) still routes through Heuristic.applyHeuristic with no behavioral regression."
    - "Existing 49 tests still pass: StreamingTests + LoggingTests already explicitly set `Routing:Algorithm=heuristic` so the default flip does not break them; MLRoutingTests Test 4+5 explicitly override to `ml` so they still find ML branch; MLRoutingTests Test 2 (placeholder always-Qwen35B) is updated to assert real-behavior contract instead — see Task 4."
    - "Pure-Core invariant preserved: `Core/ML.fs` still imports nothing from Microsoft.ML / OnnxRuntime / Microsoft.ML.Tokenizers — verified by grep. The legacy placeholder `applyML` in ML.fs is removed in Task 3 once `makeApplyML` is wired."
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs"
      provides: "IEmbedder implementation: ONNX session + SentencePiece tokenizer + mean-pool + L2 normalize"
      contains: "interface IEmbedder"
    - path: "src/SmartRouter.Cli/Adapters/MlNetClassifier.fs"
      provides: "IClassifier implementation: PredictionEnginePool wrapper"
      contains: "interface IClassifier"
    - path: "src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs"
      provides: "Idempotent first-run dummy router.zip generator + ML embedding-model startup check"
      contains: "ensureDummyModel"
    - path: "src/SmartRouter.Cli/CompositionRoot.fs"
      provides: "ml-branch wiring: ensureDummyModel → AddPredictionEnginePool → BgeM3Embedder → MlNetClassifier → makeApplyML closure → RoutingAlgorithmRegistration"
      contains: "ML.makeApplyML"
    - path: "src/SmartRouter.Cli/appsettings.json"
      provides: "Routing.ML config section + Algorithm default = ml"
      contains: "\"Algorithm\": \"ml\""
  key_links:
    - from: "CompositionRoot.fs `\"ml\"` branch"
      to: "ModelBootstrapper.ensureDummyModel"
      via: "called as FIRST line of `\"ml\"` branch — BEFORE AddPredictionEnginePool registers"
      pattern: "ModelBootstrapper\\.ensureDummyModel"
    - from: "CompositionRoot.fs `\"ml\"` branch"
      to: "ML.makeApplyML"
      via: "embedder, classifier resolved from DI; passed to ML.makeApplyML; result is the RoutingAlgorithm function"
      pattern: "ML\\.makeApplyML"
    - from: "appsettings.json"
      to: "RoutingConfig.MlThreshold"
      via: "buildRoutingConfig reads opts.ML.Threshold and sets RoutingConfig.MlThreshold"
      pattern: "MlThreshold\\s*=\\s*opts\\.ML\\.Threshold"
    - from: "Adapters/RoutingAlgorithm.fs"
      to: "computeModelVersion"
      via: "RoutingAlgorithmRegistration.ModelVersion = sprintf \"ml-%s\" (computeModelVersion modelPath)"
      pattern: "computeModelVersion"
---

<objective>
Phase 6 wave 2: ship the three concrete Cli adapters (BgeM3Embedder, MlNetClassifier, ModelBootstrapper), wire them through CompositionRoot's `"ml"` branch, expose the new `Routing.ML` section in appsettings.json, compute SHA-hash-based `model_version`, and AT THE END of the plan flip `Routing.Algorithm` default to `"ml"`. After this plan, sending any request with the production default routes through the real ML pipeline (bge-m3 int8 embedding → ML.NET LR classifier → threshold → RoutingDecision).

Purpose: this is the moment Phase 6 actually delivers value — the placeholder `applyML` is replaced with real inference, and the production default flips to the ML path. The careful ordering (bootstrapper BEFORE pool registration; default flip AFTER everything else verifies) avoids the two dominant failure modes (FileNotFoundException at startup; tests breaking unexpectedly).

Output: 3 new adapter files, edits to CompositionRoot.fs + RoutingAlgorithm.fs + ML.fs (legacy placeholder removed) + appsettings.json (+1 section, default flipped) + .fsproj (compile-order entries for the 3 new adapters).
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/REQUIREMENTS.md
@.planning/phases/06-real-ml-routing/06-CONTEXT.md
@.planning/phases/06-real-ml-routing/06-RESEARCH.md
@.planning/phases/06-real-ml-routing/06-01-SUMMARY.md
@src/SmartRouter.Core/MLPorts.fs
@src/SmartRouter.Core/ML.fs
@src/SmartRouter.Core/Domain.fs
@src/SmartRouter.Cli/CompositionRoot.fs
@src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs
@src/SmartRouter.Cli/appsettings.json
</context>

<tasks>

<task type="auto">
  <name>Task 1: BgeM3Embedder + MlNetClassifier + ModelBootstrapper adapters</name>
  <files>
    src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
    src/SmartRouter.Cli/Adapters/MlNetClassifier.fs
    src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  </files>
  <action>
**REQ-IDs satisfied: EMBED-01 (1024-dim L2-normalized vector), EMBED-02 (deterministic), CLS-01 (PredictionEnginePool), CLS-02 (first-run bootstrap).** Concrete implementations follow 06-RESEARCH.md Patterns 4-6.

1) Create `src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs`:

```fsharp
module SmartRouter.Cli.Adapters.BgeM3Embedder

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open Microsoft.ML.Tokenizers
open Serilog
open SmartRouter.Core.MLPorts

/// bge-m3 embedder adapter — int8 quantized ONNX + XLM-R SentencePiece tokenizer.
/// Produces 1024-dim L2-normalized float32 vectors via mean pooling over attention_mask.
/// Disposable: owns the InferenceSession; should be a DI singleton.
type BgeM3Embedder(onnxPath: string, tokenizerPath: string, maxTokens: int) =
    do
        if not (File.Exists onnxPath) then
            failwithf
                "ML embedding model not found at %s. Run scripts/download-models.sh first."
                onnxPath
        if not (File.Exists tokenizerPath) then
            failwithf
                "ML tokenizer not found at %s. Run scripts/download-models.sh first."
                tokenizerPath

    let tokenizer =
        use stream = File.OpenRead(tokenizerPath)
        // XLM-R / bge-m3 SentencePiece convention for feature extraction:
        // BOS = <s> (id 0), no EOS for pooled embedding
        SentencePieceTokenizer.Create(
            stream,
            addBeginOfSentence = true,
            addEndOfSentence = false)

    let session = new InferenceSession(onnxPath)

    let encode (text: string) : int64[] =
        // Truncate-aware encode. 2.0.0 supports `maxTokenCount` overload.
        let ids = tokenizer.EncodeToIds(text, maxTokenCount = maxTokens)
        ids |> Seq.map int64 |> Array.ofSeq

    let runOnnx (ids: int64[]) : float32[] =
        let mask = Array.create ids.Length 1L
        let seqLen = ids.Length
        let dims = ReadOnlySpan<int>([| 1; seqLen |])
        let inputIdsTensor = new DenseTensor<int64>(ids, dims)
        let attnMaskTensor = new DenseTensor<int64>(mask, dims)
        let inputs : NamedOnnxValue list = [
            NamedOnnxValue.CreateFromTensor("input_ids",      inputIdsTensor)
            NamedOnnxValue.CreateFromTensor("attention_mask", attnMaskTensor)
        ]
        use results = session.Run(inputs)
        // Output 0 = last_hidden_state shape [1, seqLen, 1024]
        let hidden = (results |> Seq.head).AsTensor<float32>()
        let hiddenArr = hidden.ToArray()  // row-major seqLen * 1024
        // Mean pool over valid (mask=1) tokens
        let dim = 1024
        let pooled = Array.zeroCreate<float32> dim
        let mutable validTokens = 0.0f
        for t in 0 .. seqLen - 1 do
            if mask[t] = 1L then
                validTokens <- validTokens + 1.0f
                for d in 0 .. dim - 1 do
                    pooled[d] <- pooled[d] + hiddenArr[t * dim + d]
        // Guard against empty mask (truncation pathological case)
        if validTokens < 1.0f then validTokens <- 1.0f
        for d in 0 .. dim - 1 do
            pooled[d] <- pooled[d] / validTokens
        // L2 normalize
        let norm =
            let mutable acc = 0.0f
            for v in pooled do acc <- acc + v * v
            sqrt acc
        if norm < 1e-8f then pooled
        else
            for d in 0 .. dim - 1 do
                pooled[d] <- pooled[d] / norm
            pooled

    // Warm-up — runs once at construction to amortize JIT + model cold-start
    do
        try
            let warmIds = encode "hello"
            runOnnx warmIds |> ignore
            Log.Information("BgeM3Embedder warm-up complete (model: {OnnxPath})", onnxPath)
        with ex ->
            Log.Warning(ex, "BgeM3Embedder warm-up failed; continuing")

    interface IEmbedder with
        member _.EmbedAsync(prompt: string, _ct: CancellationToken) : Task<float32[]> =
            task {
                let ids = encode prompt
                return runOnnx ids
            }

    interface IDisposable with
        member _.Dispose() =
            session.Dispose()
            (tokenizer :> IDisposable).Dispose()
```

2) Create `src/SmartRouter.Cli/Adapters/MlNetClassifier.fs`:

```fsharp
module SmartRouter.Cli.Adapters.MlNetClassifier

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.ML
open Microsoft.ML.Data
open SmartRouter.Core.MLPorts

/// ML.NET schema types — must be `[<CLIMutable>]` so reflection-based prop set works.
[<CLIMutable>]
type RouteInput = {
    [<VectorType(1024)>]
    Features : float32[]
    Label    : bool
}

[<CLIMutable>]
type RoutePrediction = {
    [<ColumnName("PredictedLabel")>]
    Predicted   : bool
    Score       : float32
    Probability : float32
}

/// IClassifier wrapper around PredictionEnginePool.
/// Pool is singleton (registered via AddPredictionEnginePool); this adapter
/// is also singleton; both share lifetime so watchForChanges hot-reload (Phase 8) works.
type MlNetClassifier(pool: PredictionEnginePool<RouteInput, RoutePrediction>) =
    interface IClassifier with
        member _.PredictAsync(embedding: float32[], _ct: CancellationToken) : Task<ClassifierPrediction> =
            task {
                let input = { Features = embedding; Label = false }
                let pred = pool.Predict(modelName = "router", example = input)
                return
                    { Score          = pred.Probability
                      PredictedLabel = pred.Predicted }
            }
```

3) Create `src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs`:

```fsharp
module SmartRouter.Cli.Adapters.ModelBootstrapper

open System
open System.IO
open Microsoft.ML
open Microsoft.ML.Data
open Serilog
open SmartRouter.Cli.Adapters.MlNetClassifier  // RouteInput schema

/// Hard-fail if embedding model files are missing. Must be called BEFORE
/// BgeM3Embedder is constructed to give the operator a clear error message.
let ensureEmbeddingFilesPresent (onnxPath: string) (tokenizerPath: string) : unit =
    let missing = [
        if not (File.Exists onnxPath)      then yield onnxPath
        if not (File.Exists tokenizerPath) then yield tokenizerPath
    ]
    if not (List.isEmpty missing) then
        let lines = String.concat ", " missing
        let msg =
            sprintf
                "Required ML embedding files missing: %s. Run scripts/download-models.sh and retry."
                lines
        // Log Fatal so it shows up clearly even if unchecked exceptions get swallowed mid-host
        Log.Fatal("{Message}", msg)
        raise (FileNotFoundException(msg))

/// Idempotent: writes models/router.zip ONLY if missing.
/// Generates 200 random 1024-dim samples with balanced labels and trains an LR
/// model. Routing will be ~50/50 until Phase 7-8 retrain on real data.
/// Atomic write via temp+rename so PredictionEnginePool's watcher never sees partial.
let ensureDummyModel (modelPath: string) : unit =
    if not (File.Exists modelPath) then
        Log.Warning(
            "No ML classifier model found at {ModelPath}. Generating random dummy 1024-dim model. " +
            "Routing will be ~50/50 until Phase 7-8 generate real training data.",
            modelPath)
        let dir = Path.GetDirectoryName(modelPath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        let mlContext = MLContext(seed = Nullable<int>(42))
        let rng = Random(42)
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

        let tmp = modelPath + ".tmp"
        mlContext.Model.Save(model, dataView.Schema, tmp)
        if File.Exists modelPath then File.Delete modelPath
        File.Move(tmp, modelPath)
        Log.Information("Dummy classifier model written to {ModelPath}", modelPath)

/// Compute model_version: SHA-256 of router.zip, first 8 hex chars (4 bytes).
/// Returns "unknown" if file missing — should not happen post-ensureDummyModel.
let computeModelVersion (modelPath: string) : string =
    if File.Exists modelPath then
        use sha = System.Security.Cryptography.SHA256.Create()
        use stream = File.OpenRead(modelPath)
        let hash = sha.ComputeHash(stream)
        hash
        |> Array.take 4
        |> Array.map (fun b -> sprintf "%02x" b)
        |> String.concat ""
    else
        "unknown"
```

4) Edit `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — add the 3 new adapter files in compile order BEFORE CompositionRoot.fs and BEFORE Endpoints (CompositionRoot is the consumer):

```xml
<!-- Adapters (Infrastructure) — must precede Endpoints and Program -->
<Compile Include="Adapters/Json.fs" />
<Compile Include="Adapters/Logging.fs" />
<Compile Include="Adapters/DecisionLogger.fs" />
<Compile Include="Adapters/DecisionLogWriter.fs" />
<Compile Include="Adapters/CorrelationMiddleware.fs" />
<Compile Include="Adapters/RoutingAlgorithm.fs" />
<Compile Include="Adapters/MlNetClassifier.fs" />          <!-- NEW: schema types come first -->
<Compile Include="Adapters/BgeM3Embedder.fs" />            <!-- NEW: depends on Core/MLPorts only -->
<Compile Include="Adapters/ModelBootstrapper.fs" />        <!-- NEW: depends on MlNetClassifier (RouteInput) -->
<Compile Include="Adapters/QwenUpstreamClient.fs" />
<Compile Include="Adapters/QueueDispatcher.fs" />
<Compile Include="Endpoints/ChatCompletions.fs" />
<Compile Include="Endpoints/Stats.fs" />
<Compile Include="CompositionRoot.fs" />
<Compile Include="Program.fs" />
```

(MlNetClassifier ships the RouteInput record that ModelBootstrapper imports; that's why MlNetClassifier compiles first.)
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds at warnings-as-errors.
- `cd /Users/ohama/projs/smart-router && grep -E "Microsoft\\.ML|OnnxRuntime|Tokenizers" src/SmartRouter.Core/SmartRouter.Core.fsproj` returns no matches (Pure-Core invariant).
- `cd /Users/ohama/projs/smart-router && grep -E "Microsoft\\.ML|OnnxRuntime|Tokenizers" src/SmartRouter.Core/ML.fs` returns no matches (Pure-Core invariant for source).
- `cd /Users/ohama/projs/smart-router && grep -E "Microsoft\\.ML|OnnxRuntime|Tokenizers" src/SmartRouter.Core/MLPorts.fs` returns no matches (Pure-Core invariant for source).
- `cd /Users/ohama/projs/smart-router && bash scripts/check-routing-isolation.sh` exits 0.
- Existing 49 tests still pass: `cd /Users/ohama/projs/smart-router && dotnet test --no-build` (06-02 has not yet wired adapters into DI; placeholder ML.applyML still serves the `"ml"` branch — see Task 3).
  </verify>
  <done>
Three adapters compile. BgeM3Embedder warm-up runs at construction; throws clear error if files missing. MlNetClassifier wraps PredictionEnginePool<RouteInput, RoutePrediction>. ModelBootstrapper exports ensureDummyModel + ensureEmbeddingFilesPresent + computeModelVersion. All in Cli; Core untouched in this task.
  </done>
</task>

<task type="auto">
  <name>Task 2: appsettings.json Routing.ML section + RoutingOptions binding + buildRoutingConfig wiring</name>
  <files>
    src/SmartRouter.Cli/appsettings.json
    src/SmartRouter.Cli/CompositionRoot.fs
  </files>
  <action>
**REQ-IDs touched: EMBED-01 (config wiring), CLS-01 (config wiring).** Adds the Routing.ML section and routes its values into RoutingConfig.MlThreshold + the adapter constructors. Does NOT yet flip `Algorithm` default — that happens in Task 4 only after the rest verifies green.

1) Edit `src/SmartRouter.Cli/appsettings.json` — add `ML` subsection inside `Routing` (alongside ComplexityThreshold, Keywords, etc.). Keep `"Algorithm": "heuristic"` for now (flip in Task 4):

```json
"Routing": {
  "Algorithm": "heuristic",
  "ComplexityThreshold": 3,
  "TimeoutSeconds": 300,
  "ML": {
    "ModelPath": "models/router.zip",
    "EmbeddingModelPath": "models/embed/bge-m3-int8.onnx",
    "TokenizerPath": "models/embed/sentencepiece.bpe.model",
    "Threshold": 0.5,
    "MaxTokens": 512,
    "UseCoreMLEP": false
  },
  "Keywords": [ ... existing ... ],
  "TaskTable": { ... existing ... },
  "ModelAliases": { ... existing ... }
}
```

2) Edit `src/SmartRouter.Cli/CompositionRoot.fs`:

a) Add a new `[<CLIMutable>]` record `MlOptions` near the existing `RoutingOptions` record (above `buildRoutingConfig`):

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

b) Add `ML : MlOptions` field to `RoutingOptions`:

```fsharp
[<CLIMutable>]
type RoutingOptions =
    { Algorithm           : string
      ComplexityThreshold : int
      TimeoutSeconds      : int
      Keywords            : string[]
      TaskTable           : Dictionary<string, TaskTableEntry>
      ModelAliases        : Dictionary<string, string>
      ML                  : MlOptions }
```

c) Edit `buildRoutingConfig` to set MlThreshold from `opts.ML.Threshold` (with a defensive default of 0.5f if `opts.ML` is null because the ML section is absent):

```fsharp
let buildRoutingConfig (opts: RoutingOptions) : RoutingConfig =
    let taskMap = ...  // existing
    let mlThreshold =
        // Defensive default: heuristic-only deployments can omit Routing.ML entirely
        if obj.ReferenceEquals(opts.ML, null) then 0.5f
        else opts.ML.Threshold

    { ComplexityThreshold = opts.ComplexityThreshold
      Keywords            = List.ofArray opts.Keywords
      TaskTable           = taskMap
      MlThreshold         = mlThreshold }
```

d) `validateConfig` — no change needed for this task (ML fields are optional from the validator's perspective; the bootstrapper handles the fail-fast).
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds (warnings-as-errors).
- `cd /Users/ohama/projs/smart-router && dotnet test --no-build` 49/49 still passes (RoutingConfig binding still works; placeholder ML branch unchanged).
- `cd /Users/ohama/projs/smart-router && jq '.Routing.ML' src/SmartRouter.Cli/appsettings.json` returns the new section with all 6 keys.
- StreamingTests + LoggingTests still pass (their AddInMemoryCollection does not include `Routing:ML:*` keys, so RoutingOptions.ML binds to a default-initialized MlOptions record; the `obj.ReferenceEquals(opts.ML, null)` guard handles this).
  </verify>
  <done>
appsettings.json has Routing.ML subsection. RoutingOptions binds it via `ML: MlOptions`. buildRoutingConfig populates RoutingConfig.MlThreshold with a defensive null-guard. Existing tests still 49/49.
  </done>
</task>

<task type="auto">
  <name>Task 3: CompositionRoot ml-branch wiring (bootstrapper → pool → embedder → classifier → makeApplyML) + remove legacy applyML placeholder</name>
  <files>
    src/SmartRouter.Cli/CompositionRoot.fs
    src/SmartRouter.Cli/Adapters/RoutingAlgorithm.fs
    src/SmartRouter.Core/ML.fs
  </files>
  <action>
**REQ-IDs satisfied: EMBED-01 (DI wiring), CLS-01 (DI wiring), CLS-02 (bootstrap order).** This is the load-bearing wiring task. Ordering matters: bootstrapper MUST run before AddPredictionEnginePool registers (else the pool throws FileNotFoundException at first resolution).

1) Edit `src/SmartRouter.Cli/CompositionRoot.fs`:

a) Add opens at the top:

```fsharp
open Microsoft.Extensions.ML
open SmartRouter.Core.MLPorts
open SmartRouter.Cli.Adapters.BgeM3Embedder
open SmartRouter.Cli.Adapters.MlNetClassifier
open SmartRouter.Cli.Adapters.ModelBootstrapper
```

b) Inside `configureServices`, add the ML wiring block AFTER `RoutingConfig` registration but BEFORE the existing `RoutingAlgorithmRegistration` registration (rewrite the latter to consume the new ports):

```fsharp
// ── ML wiring ──────────────────────────────────────────────────────────────
//
// Order matters:
//   1. ensureEmbeddingFilesPresent — fail-fast with operator-friendly error if files missing
//   2. ensureDummyModel            — generate router.zip if missing (idempotent)
//   3. AddPredictionEnginePool     — registers pool; resolution checks file at first .Predict()
//
// Steps 1+2 run synchronously at configure time so the pool registration has
// guaranteed file presence. Both are no-ops on second startup.
let mlOpts = config.GetSection("Routing:ML").Get<MlOptions>()
if not (obj.ReferenceEquals(mlOpts, null)) then
    ensureEmbeddingFilesPresent mlOpts.EmbeddingModelPath mlOpts.TokenizerPath
    ensureDummyModel mlOpts.ModelPath

    services
        .AddPredictionEnginePool<RouteInput, RoutePrediction>()
        .FromFile(
            modelName       = "router",
            filePath        = mlOpts.ModelPath,
            watchForChanges = true)
        |> ignore

    // BgeM3Embedder — singleton; warm-up runs at construction.
    services.AddSingleton<IEmbedder>(fun _sp ->
        new BgeM3Embedder(
            mlOpts.EmbeddingModelPath,
            mlOpts.TokenizerPath,
            mlOpts.MaxTokens) :> IEmbedder)
        |> ignore

    services.AddSingleton<IClassifier>(fun sp ->
        let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
        MlNetClassifier(pool) :> IClassifier)
        |> ignore
```

c) REWRITE the `RoutingAlgorithmRegistration` factory in CompositionRoot.fs (currently calls `SmartRouter.Core.ML.applyML`) to call `SmartRouter.Core.ML.makeApplyML embedder classifier` and to compute the SHA-hashed model_version:

```fsharp
services.AddSingleton<RoutingAlgorithmRegistration>(
    Func<IServiceProvider, RoutingAlgorithmRegistration>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<RoutingOptions>>().Value
        match opts.Algorithm with
        | null | "" | "heuristic" ->
            { Algorithm    = SmartRouter.Core.Heuristic.applyHeuristic
              Name         = "heuristic"
              ModelVersion = "heuristic-v1" }
        | "ml" ->
            // ML branch — adapters resolved at request time would be wasteful;
            // we resolve once here and close over them in the closure factory.
            let embedder   = sp.GetRequiredService<IEmbedder>()
            let classifier = sp.GetRequiredService<IClassifier>()
            let mlPath     = opts.ML.ModelPath
            let modelHash  = computeModelVersion mlPath
            { Algorithm    = SmartRouter.Core.ML.makeApplyML embedder classifier
              Name         = "ml"
              ModelVersion = sprintf "ml-%s" modelHash }
        | other ->
            let msg =
                sprintf
                    "appsettings.json Routing.Algorithm = \"%s\" is invalid; valid values: \"heuristic\", \"ml\""
                    other
            raise (System.InvalidOperationException(msg))))
|> ignore
```

2) Edit `src/SmartRouter.Core/ML.fs` — REMOVE the legacy `applyML : RoutingAlgorithm` placeholder (CompositionRoot no longer references it). Keep the `runSync` helper + `makeApplyML` factory. The file shrinks to:

```fsharp
module SmartRouter.Core.ML

open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.Domain
open SmartRouter.Core.MLPorts

let private runSync (taskFactory: unit -> Task<'a>) : 'a =
    Task.Run<'a>(System.Func<Task<'a>>(taskFactory)).GetAwaiter().GetResult()

let makeApplyML
    (embedder   : IEmbedder)
    (classifier : IClassifier)
    : RoutingAlgorithm =
    fun (cfg: RoutingConfig) (req: RouterRequest) ->
        let prompt =
            req.Messages
            |> List.map (fun m -> m.Content)
            |> String.concat " "
        let embedding =
            runSync (fun () -> embedder.EmbedAsync(prompt, CancellationToken.None))
        let prediction =
            runSync (fun () -> classifier.PredictAsync(embedding, CancellationToken.None))
        let target =
            if prediction.Score >= cfg.MlThreshold then Qwen122B
            else Qwen35B
        { Target     = target
          Priority   = Low
          Reason     = ML
          IsFallback = false }
```

3) `Adapters/RoutingAlgorithm.fs` — no structural change required; the `ModelVersion` field already exists. Update its docstring to reflect the new `ml-{hash}` format (was `ml-v0-placeholder`).
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds at warnings-as-errors.
- `cd /Users/ohama/projs/smart-router && grep -E "applyML\\b" src/SmartRouter.Core/ML.fs` returns no `let applyML` line (only `makeApplyML` survives).
- `cd /Users/ohama/projs/smart-router && grep -E "Microsoft\\.ML|OnnxRuntime|Tokenizers" src/SmartRouter.Core/ML.fs` returns no matches (Pure-Core invariant).
- `cd /Users/ohama/projs/smart-router && bash scripts/check-routing-isolation.sh` exits 0.
- `cd /Users/ohama/projs/smart-router && bash scripts/check-no-async.sh` exits 0.
- Manual smoke: `cd /Users/ohama/projs/smart-router && rm -f models/router.zip && dotnet run --project src/SmartRouter.Cli -- --routing-algorithm=ml &` (run in background); within 5s log shows "No ML classifier model found... Generating random dummy 1024-dim model" then "Dummy classifier model written" then BgeM3Embedder warm-up complete (if models/embed/* exist; else fails fast with "Run scripts/download-models.sh"); then `curl http://127.0.0.1:4000/stats` returns JSON; kill the process.
  </verify>
  <done>
CompositionRoot's `"ml"` branch wires real adapters: bootstrapper runs first, AddPredictionEnginePool registers, BgeM3Embedder + MlNetClassifier registered as IEmbedder + IClassifier singletons, RoutingAlgorithmRegistration.Algorithm is the makeApplyML closure, ModelVersion is `ml-{8hexchars}`. Legacy applyML placeholder removed from Core/ML.fs. Build + check-no-async + check-routing-isolation green.
  </done>
</task>

<task type="auto">
  <name>Task 4: Update MLRoutingTests Test 2 + flip Routing.Algorithm default to "ml"</name>
  <files>
    tests/SmartRouter.Tests/MLRoutingTests.fs
    src/SmartRouter.Cli/appsettings.json
  </files>
  <action>
**REQ-IDs touched: ML-02 contract update for Phase 6 (placeholder behavior gone).** This is the last edit of the plan: only run after Tasks 1-3 verify green. Two atomic edits:

1) Edit `tests/SmartRouter.Tests/MLRoutingTests.fs` — Test 2 (`"ML.applyML always returns Qwen35B/Low/ML/IsFallback=false"`) and Test 1's `applyML` reference are now broken because `applyML` no longer exists. Two changes:

a) Test 1: replace direct `applyML` reference with a closure built from a fake `IEmbedder` + fake `IClassifier`:

```fsharp
testCase "Heuristic.applyHeuristic and ML.makeApplyML closure both satisfy RoutingAlgorithm" <| fun () ->
    let h : RoutingAlgorithm = applyHeuristic
    let fakeEmb =
        { new SmartRouter.Core.MLPorts.IEmbedder with
            member _.EmbedAsync(_, _) =
                System.Threading.Tasks.Task.FromResult(Array.create 1024 0.1f) }
    let fakeCls =
        { new SmartRouter.Core.MLPorts.IClassifier with
            member _.PredictAsync(_, _) =
                System.Threading.Tasks.Task.FromResult(
                    { Score = 0.4f; PredictedLabel = false } : SmartRouter.Core.MLPorts.ClassifierPrediction) }
    let m : RoutingAlgorithm = SmartRouter.Core.ML.makeApplyML fakeEmb fakeCls
    let req = mkReq None None "hello" 1
    let dh = h defaultConfig req
    let dm = m defaultConfig req
    Expect.isTrue (dh.Target = Qwen35B || dh.Target = Qwen122B) "heuristic returns a valid model target"
    Expect.equal dm.Reason ML "ML closure reports Reason = ML"
    Expect.isTrue (dm.Target = Qwen35B || dm.Target = Qwen122B) "ML closure returns a valid model target"
```

b) Test 2 — REPLACE the placeholder-behavior test with a contract test on the closure factory: with score < threshold → Qwen35B; with score ≥ threshold → Qwen122B; Reason always ML; Priority always Low; IsFallback always false. Use deterministic fakes:

```fsharp
testCase "ML.makeApplyML returns Qwen122B when score >= threshold, Qwen35B otherwise" <| fun () ->
    let fakeEmb =
        { new SmartRouter.Core.MLPorts.IEmbedder with
            member _.EmbedAsync(_, _) =
                System.Threading.Tasks.Task.FromResult(Array.create 1024 0.1f) }
    let mkClassifier (score: float32) =
        { new SmartRouter.Core.MLPorts.IClassifier with
            member _.PredictAsync(_, _) =
                System.Threading.Tasks.Task.FromResult(
                    { Score = score; PredictedLabel = score >= 0.5f } : SmartRouter.Core.MLPorts.ClassifierPrediction) }
    let cfg = { defaultConfig with MlThreshold = 0.5f }
    let req = mkReq None None "hello" 1

    // score below threshold → 35B
    let lowAlgo = SmartRouter.Core.ML.makeApplyML fakeEmb (mkClassifier 0.3f)
    let dLow = lowAlgo cfg req
    Expect.equal dLow.Target Qwen35B  "score 0.3 < 0.5 → 35B"
    Expect.equal dLow.Reason ML        "Reason = ML"
    Expect.equal dLow.Priority Low     "Priority = Low"
    Expect.equal dLow.IsFallback false "IsFallback = false"

    // score at/above threshold → 122B
    let highAlgo = SmartRouter.Core.ML.makeApplyML fakeEmb (mkClassifier 0.8f)
    let dHigh = highAlgo cfg req
    Expect.equal dHigh.Target Qwen122B "score 0.8 ≥ 0.5 → 122B"
    Expect.equal dHigh.Reason ML       "Reason = ML"
```

c) Tests 3, 4, 5 — keep as-is. Test 3 (`routeRequest dispatches the algorithm parameter`) needs `applyML`; replace with the same fake-classifier-built closure pattern shown above. Tests 4+5 already use DI through CompositionRoot → with Routing:Algorithm=ml override, the real makeApplyML wiring runs. They will require either:
   - `models/embed/bge-m3-int8.onnx` + `models/embed/sentencepiece.bpe.model` to be present locally (CI-only setup), OR
   - Skip (with `ptestCase`) if the embedding files are missing — preferred, since it lets every developer run dotnet test without first running scripts/download-models.sh.

Recommended pattern at top of MLRoutingTests:

```fsharp
let private mlEmbeddingFilesPresent =
    System.IO.File.Exists "models/embed/bge-m3-int8.onnx"
    && System.IO.File.Exists "models/embed/sentencepiece.bpe.model"

let private mlTestCase name body =
    if mlEmbeddingFilesPresent then testCase name body
    else ptestCase name body  // pending — print "skipped: download-models.sh not run"
```

Use `mlTestCase` for Tests 4+5 (DI-based ML branch). Tests 1, 2, 3 (closure-factory-direct) keep `testCase` because they use fakes and don't need the real ONNX file.

2) Edit `src/SmartRouter.Cli/appsettings.json` — flip `Routing.Algorithm` from `"heuristic"` to `"ml"`. THIS IS THE LAST EDIT OF THE PLAN. Per 06-CONTEXT.md decision 8 ("staged at end of Plan 06-02"):

```json
"Routing": {
  "Algorithm": "ml",
  ...
}
```

Verify: StreamingTests + LoggingTests already explicitly set `Routing:Algorithm=heuristic` via AddInMemoryCollection (LoggingTests.fs:177; StreamingTests.fs:130) so the default flip does NOT break them. RoutingTests uses pure functions through `routeRequest defaultConfig applyHeuristic req` (no DI), so it's untouched. MLRoutingTests Tests 4+5 explicitly set `Routing:Algorithm=ml` → still pick the ML branch. Tests 1,2,3 use closure factory directly → unaffected.
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds at warnings-as-errors.
- `cd /Users/ohama/projs/smart-router && dotnet test --no-build` reports green:
  - If `models/embed/*` files present: 49/49 pass (Tests 4+5 hit real ML path).
  - If `models/embed/*` files absent: 47/49 pass + 2 pending (Tests 4+5 skipped via ptestCase).
- `cd /Users/ohama/projs/smart-router && jq -r '.Routing.Algorithm' src/SmartRouter.Cli/appsettings.json` prints `ml`.
- Manual smoke (with embedding files present): `cd /Users/ohama/projs/smart-router && dotnet run --project src/SmartRouter.Cli &` → `curl -X POST http://127.0.0.1:4000/v1/chat/completions -d '{"messages":[{"role":"user","content":"hi"}]}'` → response logs JSONL `routing_algorithm=ml` + `model_version=ml-XXXXXXXX`; kill process.
- `cd /Users/ohama/projs/smart-router && grep -E "Routing:Algorithm" tests/SmartRouter.Tests/{StreamingTests,LoggingTests}.fs` confirms both set "heuristic" explicitly (no test ripple).
  </verify>
  <done>
MLRoutingTests Test 1+2+3 use makeApplyML closure with fake ports (exercise threshold contract). Tests 4+5 are mlTestCase (skip if embedding files absent). appsettings.json `Routing.Algorithm` default flipped to `"ml"`. Existing tests still green at full or with 2 pending.
  </done>
</task>

</tasks>

<verification>
**Wave 2 acceptance bar:**
1. `dotnet build` succeeds across solution at warnings-as-errors.
2. `dotnet test --no-build` reports green: 49/49 if embedding files present locally, else 47/49 + 2 pending.
3. `scripts/check-no-async.sh` and `scripts/check-routing-isolation.sh` both exit 0.
4. `grep -E "Microsoft\\.ML|OnnxRuntime|Tokenizers" src/SmartRouter.Core/*.fs src/SmartRouter.Core/SmartRouter.Core.fsproj` returns no matches (Pure-Core invariant preserved).
5. `grep "applyML\\b" src/SmartRouter.Core/ML.fs` matches only `makeApplyML` (legacy placeholder removed).
6. `jq -r '.Routing.Algorithm' src/SmartRouter.Cli/appsettings.json` prints `ml`.
7. Manual smoke (with embedding files): `dotnet run --project src/SmartRouter.Cli` boots; first request logs `routing_algorithm=ml` + `model_version=ml-{8hex}` in JSONL; heuristic still reachable via `--routing-algorithm=heuristic` CLI override.
8. Cold start with `models/router.zip` deleted: bootstrapper regenerates dummy LR model, no FileNotFoundException.
9. Cold start with `models/embed/*.onnx` missing: clear "Run scripts/download-models.sh" error, non-zero exit.
</verification>

<success_criteria>
- `Routing.Algorithm=ml` is the production default.
- The real ML pipeline (BgeM3Embedder + MlNetClassifier + makeApplyML closure) serves requests.
- First-run dummy router.zip generated atomically when missing.
- Embedding-file fail-fast with operator-friendly error.
- model_version is SHA-256 hash-based (`ml-{8hex}`), recomputed once at startup.
- Heuristic remains reachable as dormant fallback via explicit config or CLI flag.
- Pure-Core invariant preserved (no Microsoft.ML / OnnxRuntime / Tokenizers in Core).
- Existing 49 tests green (or 47/49 + 2 pending if embedding files absent).
</success_criteria>

<output>
After completion, create `.planning/phases/06-real-ml-routing/06-02-SUMMARY.md` covering:
- Three new adapter files + their responsibilities
- DI wiring order in CompositionRoot's `"ml"` branch (ensureEmbeddingFilesPresent → ensureDummyModel → AddPredictionEnginePool → IEmbedder → IClassifier → RoutingAlgorithmRegistration)
- model_version format locked: `ml-{first 8 hex chars of SHA-256(router.zip)}`
- appsettings.json Routing.ML schema + Algorithm default flip
- MLRoutingTests Tests 1+2+3 reshaped to closure-factory contract; Tests 4+5 ptestCase-gated on embedding files
- Test impact report: StreamingTests/LoggingTests untouched, RoutingTests untouched, MLRoutingTests rewritten in 4 spots
- Confirmation: build green; tests green at full-config (49/49) or partial (47/49 + 2 pending)
</output>
