---
phase: 06-real-ml-routing
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Core/MLPorts.fs
  - src/SmartRouter.Core/Domain.fs
  - src/SmartRouter.Core/ML.fs
  - src/SmartRouter.Core/SmartRouter.Core.fsproj
  - src/SmartRouter.Cli/SmartRouter.Cli.fsproj
  - .gitignore
  - scripts/download-models.sh
  - scripts/export-bge-m3-int8.sh
autonomous: true

must_haves:
  truths:
    - "Core compiles with new MLPorts.fs in correct compile-order position (after Domain.fs, before ML.fs)."
    - "Core/MLPorts.fs declares IEmbedder + IClassifier + ClassifierPrediction with no NuGet imports beyond System.Threading.Tasks (BCL)."
    - "Core/ML.fs exports `makeApplyML : IEmbedder -> IClassifier -> RoutingAlgorithm` closure; the legacy `applyML : RoutingAlgorithm` placeholder remains exported (so MLRoutingTests + CompositionRoot still build) and is the function returned when classifier ports are not yet wired."
    - "Domain.fs RoutingConfig gains `MlThreshold: float32` field with default 0.5f preserved across buildRoutingConfig."
    - "Cli .fsproj has 4 new pinned NuGet PackageReferences: Microsoft.ML.OnnxRuntime 1.25.1, Microsoft.ML.Tokenizers 2.0.0, Microsoft.ML 5.0.0, Microsoft.Extensions.ML 5.0.0."
    - "Core .fsproj has zero ML-stack NuGet refs (FsToolkit.ErrorHandling only — ARCH-01 preserved)."
    - "scripts/download-models.sh + scripts/export-bge-m3-int8.sh exist, are executable, and reference HF repos documented in 06-RESEARCH.md."
    - ".gitignore has `models/` line so the ~580MB ONNX file + auto-generated router.zip never get committed."
    - "`dotnet build` succeeds at warnings-as-errors. `dotnet test` 49/49 still passes (no regression of placeholder ML.applyML behavior)."
    - "scripts/check-routing-isolation.sh continues to pass — Heuristic.fs and ML.fs do NOT cross-import."
  artifacts:
    - path: "src/SmartRouter.Core/MLPorts.fs"
      provides: "IEmbedder + IClassifier ports + ClassifierPrediction record"
      contains: "type IEmbedder"
    - path: "src/SmartRouter.Core/ML.fs"
      provides: "makeApplyML closure pattern + legacy applyML placeholder"
      contains: "let makeApplyML"
    - path: "src/SmartRouter.Core/Domain.fs"
      provides: "RoutingConfig with MlThreshold field"
      contains: "MlThreshold"
    - path: "src/SmartRouter.Cli/SmartRouter.Cli.fsproj"
      provides: "4 pinned ML NuGet PackageReferences"
      contains: "Microsoft.ML.OnnxRuntime"
    - path: ".gitignore"
      provides: "models/ exclusion"
      contains: "models/"
    - path: "scripts/download-models.sh"
      provides: "huggingface-cli download Teradata/bge-m3 to models/embed/"
      contains: "huggingface-cli"
    - path: "scripts/export-bge-m3-int8.sh"
      provides: "self-export reproducibility script (optimum-cli + quantize_dynamic)"
      contains: "quantize_dynamic"
  key_links:
    - from: "src/SmartRouter.Core/ML.fs"
      to: "src/SmartRouter.Core/MLPorts.fs"
      via: "open SmartRouter.Core.MLPorts → IEmbedder / IClassifier types in scope"
      pattern: "open SmartRouter.Core.MLPorts"
    - from: "src/SmartRouter.Core/SmartRouter.Core.fsproj"
      to: "MLPorts.fs / ML.fs / Domain.fs compile-order"
      via: "Domain.fs → Heuristic.fs → MLPorts.fs → ML.fs → Routing.fs → Ports.fs"
      pattern: "Compile Include=\"MLPorts.fs\""
---

<objective>
Phase 6 wave 1: lay the pure-Core seam (IEmbedder + IClassifier ports, makeApplyML closure pattern, RoutingConfig.MlThreshold field), pin the 4 ML NuGet packages onto the Cli project, gitignore the models/ tree, and ship the two operator-run model-acquisition scripts. After this plan, Core has the type-level groundwork for real ML inference but the placeholder applyML still wins at runtime — concrete adapters are wired in 06-02. Existing test suite (49 tests) stays green.

Purpose: separate the type/contract concerns (Core, no NuGet) from the implementation concerns (Cli adapters, NuGet) so that 06-02 can be a clean adapter wiring pass without simultaneously reshaping Core. Also captures the binary-asset boundary (models/ gitignored, scripts/ committed) that the operator-run setup step will fill.

Output: 4 modified F# files (MLPorts.fs new, Domain.fs +1 field, ML.fs +makeApplyML, Core .fsproj compile-order entry), 1 modified Cli .fsproj (4 NuGet pins), 1 modified .gitignore, 2 new shell scripts under scripts/.
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
@src/SmartRouter.Core/Domain.fs
@src/SmartRouter.Core/ML.fs
@src/SmartRouter.Core/SmartRouter.Core.fsproj
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
@src/SmartRouter.Cli/CompositionRoot.fs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Core MLPorts.fs + RoutingConfig.MlThreshold + makeApplyML closure</name>
  <files>
    src/SmartRouter.Core/MLPorts.fs
    src/SmartRouter.Core/Domain.fs
    src/SmartRouter.Core/ML.fs
    src/SmartRouter.Core/SmartRouter.Core.fsproj
    tests/SmartRouter.Tests/RoutingTests.fs
  </files>
  <action>
**REQ-IDs satisfied: EMBED-01 (port shape), CLS-01 (port shape).** Pure-Core groundwork; concrete adapters in 06-02.

1) Create `src/SmartRouter.Core/MLPorts.fs` (new file):

```fsharp
module SmartRouter.Core.MLPorts

open System.Threading
open System.Threading.Tasks

/// Embedding port — takes a prompt, returns 1024-dim L2-normalized float32 vector.
/// Adapter (BgeM3Embedder, in Cli) implements this against OnnxRuntime + SentencePiece.
/// Pure interface: no NuGet imports, no Microsoft.ML reference.
type IEmbedder =
    abstract member EmbedAsync :
        prompt : string * ct : CancellationToken
        -> Task<float32[]>

/// Classifier prediction result.
/// Score: sigmoid probability in [0..1]; >= MlThreshold → Qwen122B.
/// PredictedLabel: ML.NET LR convention (true = positive class = Qwen122B).
[<Struct>]
type ClassifierPrediction =
    { Score          : float32
      PredictedLabel : bool }

/// Classifier port — takes 1024-dim embedding, returns prediction.
/// Adapter (MlNetClassifier, in Cli) implements this against PredictionEnginePool.
type IClassifier =
    abstract member PredictAsync :
        embedding : float32[] * ct : CancellationToken
        -> Task<ClassifierPrediction>
```

2) Edit `src/SmartRouter.Core/Domain.fs` — add `MlThreshold: float32` to `RoutingConfig`:

Old (lines 82-85):
```fsharp
type RoutingConfig =
    { ComplexityThreshold : int
      Keywords            : string list
      TaskTable           : Map<string, ModelId * Priority> }
```
New:
```fsharp
type RoutingConfig =
    { ComplexityThreshold : int
      Keywords            : string list
      TaskTable           : Map<string, ModelId * Priority>
      /// ML routing threshold: P(Qwen122B) ≥ this value → 122B (Phase 6).
      /// Heuristic algorithm ignores this field; only ML reads it.
      MlThreshold         : float32 }
```

Update `defaultRoutingConfig` in Routing.fs to include `MlThreshold = 0.5f`:
```fsharp
let defaultRoutingConfig : RoutingConfig =
    { ComplexityThreshold = 3
      Keywords            = canonicalKeywords
      TaskTable           = canonicalTaskTable
      MlThreshold         = 0.5f }
```

Update RoutingTests.fs if any inline RoutingConfig record literals exist (search: `{ ComplexityThreshold` in tests/) — add `MlThreshold = 0.5f` field. (Most tests use `defaultConfig = defaultRoutingConfig`; only inline literals need touching.)

3) Edit `src/SmartRouter.Core/ML.fs`:

Replace the body so it now exports BOTH the legacy `applyML` placeholder AND the new `makeApplyML` closure pattern. Until 06-02 wires real ports, CompositionRoot continues to use `applyML` directly — wave-1 boundary preserves behavior.

```fsharp
module SmartRouter.Core.ML

open System.Threading
open System.Threading.Tasks
open SmartRouter.Core.Domain
open SmartRouter.Core.MLPorts

/// Bridge async I/O calls back to a synchronous closure body.
/// `Task.Run` ensures the awaited task runs OFF the ASP.NET SyncContext —
/// without this wrapper, `.GetAwaiter().GetResult()` deadlocks on Kestrel
/// when the inner task awaits a continuation that needs the SyncContext.
/// CPU-bound ONNX inference does not capture SyncContext, but Task.Run is
/// the canonical guard against that class of pitfall.
let private runSync (taskFactory: unit -> Task<'a>) : 'a =
    Task.Run<'a>(System.Func<Task<'a>>(taskFactory)).GetAwaiter().GetResult()

/// Real ML routing closure factory. Closes over an embedder + classifier and
/// returns a synchronous `RoutingAlgorithm` matching ML-01 (Phase 4 contract).
/// Decision rule: prediction.Score >= cfg.MlThreshold → Qwen122B, else Qwen35B.
/// Reason is always `ML`; Priority defaults to `Low` (122B priority assignment is
/// task-driven, not classifier-driven, and the ML path has no task signal).
/// IsFallback is always false in this phase (Phase 10 will set it on health-fallback).
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

/// Legacy placeholder retained so CompositionRoot's `"ml"` branch continues
/// to compile in wave 1 (before adapter wiring lands in 06-02).
/// Always picks Qwen35B/Low/ML/IsFallback=false — Phase 4 ML-02 contract.
/// REMOVED in 06-02 once makeApplyML is wired through CompositionRoot.
/// MUST NOT import the Heuristic module (zero cross-imports enforced by ML-04).
let applyML (config: RoutingConfig) (req: RouterRequest) : RoutingDecision =
    ignore config
    ignore req
    { Target     = Qwen35B
      Priority   = Low
      Reason     = ML
      IsFallback = false }
```

4) Edit `src/SmartRouter.Core/SmartRouter.Core.fsproj` — add `MLPorts.fs` to compile order between Heuristic.fs and ML.fs (so ML.fs can `open SmartRouter.Core.MLPorts`):

```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <Compile Include="Heuristic.fs" />
  <Compile Include="MLPorts.fs" />     <!-- NEW: Phase 6 -->
  <Compile Include="ML.fs" />
  <Compile Include="Routing.fs" />
  <Compile Include="Ports.fs" />
</ItemGroup>
```

5) Verify: zero new `<PackageReference>` lines on Core .fsproj (must stay pure: ARCH-01).
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet build src/SmartRouter.Core/SmartRouter.Core.fsproj -c Debug` succeeds (TreatWarningsAsErrors=true).
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds across solution (Cli + Tests still link).
- `cd /Users/ohama/projs/smart-router && dotnet test --no-build` reports 49/49 passing — placeholder applyML still exports + still selects Qwen35B/Low/ML.
- `cd /Users/ohama/projs/smart-router && bash scripts/check-routing-isolation.sh` exits 0 (Heuristic.fs and ML.fs zero cross-imports).
- `cd /Users/ohama/projs/smart-router && bash scripts/check-no-async.sh` exits 0 (Core uses task{}, not async{}).
- `grep -E "Microsoft\\.ML|OnnxRuntime|Tokenizers" src/SmartRouter.Core/SmartRouter.Core.fsproj` returns no matches (ARCH-01 preserved).
  </verify>
  <done>
MLPorts.fs exists with IEmbedder + IClassifier + ClassifierPrediction. Domain.fs RoutingConfig has MlThreshold. ML.fs exports both makeApplyML (new closure) and applyML (legacy placeholder retained). Core compiles. Full test suite (49) green. Core stays NuGet-clean.
  </done>
</task>

<task type="auto">
  <name>Task 2: Cli NuGet pins + .gitignore + model-acquisition scripts</name>
  <files>
    src/SmartRouter.Cli/SmartRouter.Cli.fsproj
    .gitignore
    scripts/download-models.sh
    scripts/export-bge-m3-int8.sh
  </files>
  <action>
**REQ-IDs satisfied: EMBED-01 (NuGet stack), CLS-01 (NuGet stack), EMBED-03 (CoreML EP available via OnnxRuntime 1.25.1).** Operator-run setup scripts feed the embedder and classifier in 06-02.

1) Edit `src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — append 4 ML PackageReferences inside the existing `<ItemGroup>` that holds NuGet refs (the one with `FsToolkit.ErrorHandling`). Pin to versions live-verified 2026-05-08 (per 06-RESEARCH.md):

```xml
<!-- ML stack — Phase 6 -->
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.25.1" />
<PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
<PackageReference Include="Microsoft.ML" Version="5.0.0" />
<PackageReference Include="Microsoft.Extensions.ML" Version="5.0.0" />
```

DO NOT add `Microsoft.ML.OnnxRuntime.Extensions`, `BERTTokenizers`, or `BlingFire` — 06-RESEARCH.md "Not Needed" matrix explicitly rules them out.

2) Edit `.gitignore` — append models exclusion below the `logs/` line. Keep the existing comment style:

```
# ML model files — too large to commit (~580MB ONNX + auto-generated router.zip)
# Operators run scripts/download-models.sh once, then routing works locally.
models/
```

3) Create `scripts/download-models.sh` (executable; `chmod +x` after write):

```bash
#!/usr/bin/env bash
# scripts/download-models.sh
# One-time setup: download bge-m3 int8 ONNX + tokenizer from HuggingFace into models/embed/.
# Operator runs this once before first `dotnet run` of the router.
# The router exits with a clear error if these files are missing at startup.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EMBED_DIR="${REPO_ROOT}/models/embed"
mkdir -p "${EMBED_DIR}"

echo "[download-models] Fetching Teradata/bge-m3 (int8 ONNX, ~542 MB) into ${EMBED_DIR}/"

# Requires huggingface-cli; install with: pip install -U "huggingface_hub[cli]"
if ! command -v huggingface-cli > /dev/null 2>&1; then
  echo "ERROR: huggingface-cli not found. Install with: pip install -U 'huggingface_hub[cli]'" >&2
  exit 1
fi

# Teradata/bge-m3 ships int8 model.onnx + sentencepiece.bpe.model + tokenizer.json
huggingface-cli download Teradata/bge-m3 \
  --local-dir "${EMBED_DIR}" \
  --include "model.onnx" "sentencepiece.bpe.model" "tokenizer.json"

# Convention: appsettings.json points at these exact filenames.
# Rename / symlink if the repo's filename differs.
if [ -f "${EMBED_DIR}/model.onnx" ] && [ ! -f "${EMBED_DIR}/bge-m3-int8.onnx" ]; then
  mv "${EMBED_DIR}/model.onnx" "${EMBED_DIR}/bge-m3-int8.onnx"
fi

echo "[download-models] Done. Files in ${EMBED_DIR}/:"
ls -lh "${EMBED_DIR}/"
echo "[download-models] Run: dotnet run --project src/SmartRouter.Cli"
```

4) Create `scripts/export-bge-m3-int8.sh` (executable; reproducibility path for users who want to self-export rather than trust a HF mirror):

```bash
#!/usr/bin/env bash
# scripts/export-bge-m3-int8.sh
# Self-export bge-m3 to int8 ONNX from BAAI/bge-m3 source weights.
# Reproducibility path: yields the same int8 ONNX as scripts/download-models.sh's mirror.
# Requires: Python 3.10+, optimum[onnxruntime], onnxruntime, ~6 GB disk + ~4 GB RAM.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EMBED_DIR="${REPO_ROOT}/models/embed"
FP32_DIR="${EMBED_DIR}/bge-m3-fp32"
mkdir -p "${EMBED_DIR}"

# Step 1: FP32 ONNX export via optimum-cli
echo "[export-bge-m3-int8] Step 1: FP32 ONNX export → ${FP32_DIR}/"
optimum-cli export onnx \
  --model BAAI/bge-m3 \
  --task feature-extraction \
  --opset 17 \
  --framework pt \
  "${FP32_DIR}"

# Step 2: int8 dynamic quantization
echo "[export-bge-m3-int8] Step 2: int8 dynamic quantization → ${EMBED_DIR}/bge-m3-int8.onnx"
python3 - <<EOF
from onnxruntime.quantization import quantize_dynamic, QuantType
quantize_dynamic(
    model_input="${FP32_DIR}/model.onnx",
    model_output="${EMBED_DIR}/bge-m3-int8.onnx",
    weight_type=QuantType.QInt8,
    optimize_model=True,
    op_types_to_quantize=["MatMul", "Gather"]
)
print("Done.")
EOF

# Step 3: copy tokenizer (raw SentencePiece binary used by Microsoft.ML.Tokenizers)
cp "${FP32_DIR}/sentencepiece.bpe.model" "${EMBED_DIR}/sentencepiece.bpe.model"

echo "[export-bge-m3-int8] Files ready:"
ls -lh "${EMBED_DIR}/bge-m3-int8.onnx" "${EMBED_DIR}/sentencepiece.bpe.model"
```

5) `chmod +x scripts/download-models.sh scripts/export-bge-m3-int8.sh`.
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet restore src/SmartRouter.Cli/SmartRouter.Cli.fsproj` — succeeds, no version conflicts (especially System.Memory between OnnxRuntime 1.25.1 and Microsoft.ML 5.0.0; if conflict surfaces, document via `dotnet add package` warning + ask before pinning).
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds across solution at warnings-as-errors.
- `cd /Users/ohama/projs/smart-router && dotnet test --no-build` still reports 49/49 passing.
- `cd /Users/ohama/projs/smart-router && bash -n scripts/download-models.sh && bash -n scripts/export-bge-m3-int8.sh` (syntax check) — no errors.
- `cd /Users/ohama/projs/smart-router && test -x scripts/download-models.sh && test -x scripts/export-bge-m3-int8.sh` (executable bits set).
- `cd /Users/ohama/projs/smart-router && grep -q "^models/$" .gitignore` (gitignore has the line).
- `cd /Users/ohama/projs/smart-router && git check-ignore -v models/embed/bge-m3-int8.onnx` (would report the .gitignore rule matching, even though file does not yet exist).
  </verify>
  <done>
Cli .fsproj has 4 new pinned ML PackageReferences. .gitignore excludes models/. Both shell scripts exist + are executable + pass syntax check. `dotnet restore` resolves all packages. Full test suite still 49/49.
  </done>
</task>

</tasks>

<verification>
**Wave 1 acceptance bar:**
1. `dotnet build` succeeds across solution at warnings-as-errors.
2. `dotnet test --no-build` reports 49/49 passing (placeholder applyML behavior preserved; no regression).
3. `scripts/check-no-async.sh` and `scripts/check-routing-isolation.sh` both exit 0.
4. `grep -E "Microsoft\\.ML|OnnxRuntime|Tokenizers" src/SmartRouter.Core/SmartRouter.Core.fsproj` returns no matches (ARCH-01).
5. `cat src/SmartRouter.Core/MLPorts.fs | grep -E "type IEmbedder|type IClassifier|ClassifierPrediction"` confirms the three type declarations.
6. `cat src/SmartRouter.Core/ML.fs | grep -E "let makeApplyML|let applyML"` confirms both exports.
7. `cat src/SmartRouter.Cli/SmartRouter.Cli.fsproj | grep -c "Microsoft\\.ML"` returns 3 (Microsoft.ML.OnnxRuntime + Microsoft.ML.Tokenizers + Microsoft.ML; Microsoft.Extensions.ML matches separately) — verify the actual 4 pins.
8. `git check-ignore -v models/` reports a matching rule.
</verification>

<success_criteria>
- IEmbedder + IClassifier ports exist in Core (pure F# interfaces, no NuGet leak).
- makeApplyML closure factory exists in ML.fs alongside the legacy placeholder.
- RoutingConfig gained MlThreshold field; defaultRoutingConfig sets it to 0.5f.
- 4 ML NuGet packages pinned in Cli .fsproj (versions verified 2026-05-08).
- models/ gitignored; download + export scripts shipped + executable.
- Existing 49 tests stay green.
- Core stays NuGet-clean.
</success_criteria>

<output>
After completion, create `.planning/phases/06-real-ml-routing/06-01-SUMMARY.md` covering:
- Port shapes settled (IEmbedder + IClassifier signatures)
- RoutingConfig.MlThreshold default value
- makeApplyML closure pattern locked
- 4 NuGet versions pinned (record exact versions in case 06-02 needs to bump)
- Scripts shipped (location + how operator runs them)
- Any restore conflicts encountered between OnnxRuntime + Microsoft.ML (document workaround if any)
- Confirmation: 49/49 tests green; no behavioral regression
</output>
