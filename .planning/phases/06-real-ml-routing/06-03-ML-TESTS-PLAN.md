---
phase: 06-real-ml-routing
plan: 03
type: execute
wave: 3
depends_on: ["06-02"]
files_modified:
  - tests/SmartRouter.Tests/MLEmbeddingTests.fs
  - tests/SmartRouter.Tests/MLClassifierTests.fs
  - tests/SmartRouter.Tests/MLRoutingTests.fs
  - tests/SmartRouter.Tests/RouterTests.fs
  - tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
autonomous: true

must_haves:
  truths:
    - "MLEmbeddingTests verifies EMBED-01: BgeM3Embedder produces 1024-dim L2-normalized vectors on three prompts (English, Korean, mixed)."
    - "MLEmbeddingTests verifies EMBED-02 (determinism): same prompt → same vector across two embed calls (bitwise equality)."
    - "MLEmbeddingTests verifies CLS-03: cosine(embed(\"디버깅 도와줘\"), embed(\"debug this\")) > 0.7 AND cosine(embed(\"F# 컴파일러 에러 분석\"), embed(\"analyze F# compiler error\")) > 0.7. Optional: cosine(embed(\"hello\"), embed(\"world\")) < 0.5."
    - "MLEmbeddingTests verifies EMBED-03: 100 warm-path embeddings; p95 latency < 50ms on M-series (skip with ptestCase if embedding files absent or running under non-arm64 emulation)."
    - "MLClassifierTests verifies CLS-01: PredictionEnginePool resolves from DI when Routing:Algorithm=ml; pool.Predict on 1024-dim random vector returns non-null prediction."
    - "MLClassifierTests verifies CLS-02 (bootstrap): in a temp directory, deleting router.zip and starting CompositionRoot regenerates a dummy model atomically; subsequent prediction succeeds."
    - "MLRoutingTests is extended with: model_version test (DecisionLog model_version starts with `ml-` and is 11 chars long, NOT `ml-v0-placeholder`), and a CompositionRoot smoke test that resolves IEmbedder + IClassifier + RoutingAlgorithm from DI without throwing."
    - "All ML-stack tests are wrapped in `testSequenced` (filesystem state — models/ + bin output dir — is shared across tests)."
    - "RouterTests.rootTests includes MLEmbeddingTests.tests + MLClassifierTests.tests; SmartRouter.Tests.fsproj <Compile> list includes both new files BEFORE RouterTests.fs."
    - "Tests gracefully ptestCase-skip if `models/embed/bge-m3-int8.onnx` + `models/embed/sentencepiece.bpe.model` are absent (so dotnet test passes on a fresh clone before scripts/download-models.sh runs)."
  artifacts:
    - path: "tests/SmartRouter.Tests/MLEmbeddingTests.fs"
      provides: "EMBED-01 + EMBED-02 + EMBED-03 + CLS-03 verification"
      contains: "let tests"
    - path: "tests/SmartRouter.Tests/MLClassifierTests.fs"
      provides: "CLS-01 + CLS-02 verification"
      contains: "let tests"
    - path: "tests/SmartRouter.Tests/MLRoutingTests.fs"
      provides: "Extended with model_version hash test + makeApplyML closure DI smoke"
      contains: "model_version"
    - path: "tests/SmartRouter.Tests/RouterTests.fs"
      provides: "rootTests list updated to include 2 new ML test modules"
      contains: "MLEmbeddingTests.tests"
    - path: "tests/SmartRouter.Tests/SmartRouter.Tests.fsproj"
      provides: "Compile order updated for 2 new ML test modules"
      contains: "MLEmbeddingTests.fs"
  key_links:
    - from: "MLEmbeddingTests"
      to: "BgeM3Embedder"
      via: "Construct directly with paths from appsettings.json bin-copy"
      pattern: "new BgeM3Embedder"
    - from: "MLClassifierTests"
      to: "ModelBootstrapper.ensureDummyModel"
      via: "Test sets up temp dir, calls ensureDummyModel, then loads model via PredictionEngine"
      pattern: "ensureDummyModel"
    - from: "MLRoutingTests model_version test"
      to: "ModelBootstrapper.computeModelVersion"
      via: "Resolves RoutingAlgorithmRegistration from DI; asserts ModelVersion = sprintf \"ml-%s\" hash"
      pattern: "ModelVersion"
---

<objective>
Phase 6 wave 3: write the test suite that verifies all 6 Phase 6 REQ-IDs (EMBED-01, EMBED-02, EMBED-03, CLS-01, CLS-02, CLS-03). Two new test modules (MLEmbeddingTests, MLClassifierTests) plus an extension to MLRoutingTests for model_version + DI smoke. All tests gracefully skip via ptestCase when the embedding ONNX files are missing locally so a fresh clone passes `dotnet test` without requiring `scripts/download-models.sh` to have run.

Purpose: closes the verification loop on Phase 6's Success Criteria items 1, 2, 5, 6 (the others are exercised through 06-02's verification). These tests are the contract we promise downstream phases (Phase 7's failure detector reads model_version; Phase 8's hot-reload depends on PredictionEnginePool wiring; Phase 9's canary depends on cohort-tagged DecisionLog).

Output: 2 new test files (MLEmbeddingTests.fs, MLClassifierTests.fs), edits to MLRoutingTests.fs (+2 tests), edits to RouterTests.fs (rootTests list), edits to SmartRouter.Tests.fsproj (compile order).
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
@.planning/phases/06-real-ml-routing/06-02-SUMMARY.md
@src/SmartRouter.Core/MLPorts.fs
@src/SmartRouter.Core/ML.fs
@src/SmartRouter.Cli/Adapters/BgeM3Embedder.fs
@src/SmartRouter.Cli/Adapters/MlNetClassifier.fs
@src/SmartRouter.Cli/Adapters/ModelBootstrapper.fs
@tests/SmartRouter.Tests/MLRoutingTests.fs
@tests/SmartRouter.Tests/RouterTests.fs
@tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
</context>

<tasks>

<task type="auto">
  <name>Task 1: MLEmbeddingTests.fs — EMBED-01, EMBED-02, EMBED-03, CLS-03</name>
  <files>
    tests/SmartRouter.Tests/MLEmbeddingTests.fs
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**REQ-IDs satisfied: EMBED-01, EMBED-02, EMBED-03, CLS-03.** Cosine-similarity verification of bge-m3 multilingual semantics + 100-call latency benchmark.

1) Create `tests/SmartRouter.Tests/MLEmbeddingTests.fs`:

```fsharp
module SmartRouter.Tests.MLEmbeddingTests

open System
open System.IO
open System.Threading
open System.Diagnostics
open Expecto
open SmartRouter.Cli.Adapters.BgeM3Embedder

// ── Embedding-file gate ──────────────────────────────────────────────────────
//
// Tests in this module require the bge-m3 int8 ONNX + tokenizer.
// On a fresh clone before `scripts/download-models.sh` runs, these files
// are absent — we ptestCase-skip rather than fail.

let private modelsRoot =
    // Tests run from bin/Debug/net10.0; appsettings paths are relative to repo root
    let cwd = Directory.GetCurrentDirectory()
    // Walk up to find a directory that has scripts/download-models.sh, OR fall back to cwd
    let rec findRoot (d: string) =
        if File.Exists(Path.Combine(d, "scripts/download-models.sh")) then d
        else
            let parent = Directory.GetParent(d)
            if parent = null then cwd else findRoot parent.FullName
    findRoot cwd

let private onnxPath      = Path.Combine(modelsRoot, "models/embed/bge-m3-int8.onnx")
let private tokenizerPath = Path.Combine(modelsRoot, "models/embed/sentencepiece.bpe.model")

let private filesPresent =
    File.Exists onnxPath && File.Exists tokenizerPath

let private mlTestCase name body =
    if filesPresent then testCase name body
    else ptestCase name body  // pending — operator must run scripts/download-models.sh

// ── Helpers ──────────────────────────────────────────────────────────────────

/// Lazy singleton — construct ONCE per test run; warm-up at construction
/// is amortized across all tests.
let private embedderLazy =
    lazy (new BgeM3Embedder(onnxPath, tokenizerPath, 512) :> SmartRouter.Core.MLPorts.IEmbedder)

let private embed (s: string) : float32[] =
    embedderLazy.Value.EmbedAsync(s, CancellationToken.None).GetAwaiter().GetResult()

let private cosine (a: float32[]) (b: float32[]) : float32 =
    // Both inputs are L2-normalized → dot product equals cosine similarity
    let mutable acc = 0.0f
    for i in 0 .. a.Length - 1 do
        acc <- acc + a[i] * b[i]
    acc

let private l2Norm (v: float32[]) : float32 =
    let mutable acc = 0.0f
    for x in v do acc <- acc + x * x
    sqrt acc

// ── Tests ────────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced <| testList "MLEmbeddingTests" [

        // ── EMBED-01: 1024-dim L2-normalized vectors on en/ko/mixed ──────────
        mlTestCase "EMBED-01: produces 1024-dim L2-normalized vectors on en/ko/mixed prompts" <| fun () ->
            let en = embed "this is an English sentence about programming"
            let ko = embed "한국어로 프로그래밍에 관한 문장입니다"
            let mx = embed "I have a 컴파일러 에러 in F# code"

            for (label, v) in [ "en", en; "ko", ko; "mx", mx ] do
                Expect.equal v.Length 1024 (sprintf "%s: dim = 1024" label)
                let n = l2Norm v
                Expect.isTrue (abs (n - 1.0f) < 1e-3f)
                              (sprintf "%s: ||v|| ≈ 1.0 (got %f)" label n)

            // Sanity: 3 different prompts produce 3 different vectors (not all zeros / not all equal)
            let cEK = cosine en ko
            Expect.isTrue (cEK < 0.999f) "en and ko vectors are not identical"

        // ── EMBED-02: determinism — same prompt → bitwise-equal vector across calls ──
        mlTestCase "EMBED-02: same prompt produces identical vectors across runs" <| fun () ->
            let p = "deterministic test prompt"
            let v1 = embed p
            let v2 = embed p
            Expect.equal v1.Length v2.Length "same length"
            for i in 0 .. v1.Length - 1 do
                Expect.equal v1[i] v2[i] (sprintf "v1[%d] = v2[%d]" i i)

        // ── CLS-03: bge-m3 multilingual cosine similarity ────────────────────
        mlTestCase "CLS-03: ko-en semantic alignment via cosine similarity > 0.7" <| fun () ->
            // Pair 1: technical
            let koA = embed "F# 컴파일러 에러 분석"
            let enA = embed "analyze F# compiler error"
            let cosA = cosine koA enA
            Expect.isTrue (cosA > 0.7f)
                          (sprintf "cos(\"F# 컴파일러 에러 분석\", \"analyze F# compiler error\") > 0.7 (got %f)" cosA)

            // Pair 2: general
            let koB = embed "디버깅 도와줘"
            let enB = embed "debug this"
            let cosB = cosine koB enB
            Expect.isTrue (cosB > 0.7f)
                          (sprintf "cos(\"디버깅 도와줘\", \"debug this\") > 0.7 (got %f)" cosB)

            // Sanity check: unrelated prompts have lower similarity
            let h = embed "hello"
            let w = embed "world"
            let cosHW = cosine h w
            Expect.isTrue (cosHW < 0.95f)
                          (sprintf "cos(\"hello\", \"world\") < 0.95 (got %f)" cosHW)

        // ── EMBED-03: p95 latency < 50ms on warm-path ────────────────────────
        mlTestCase "EMBED-03: p95 embedding latency < 50ms (warm path, 100 samples)" <| fun () ->
            // Warm-up — first 10 calls are likely to include JIT amortization
            for i in 0 .. 9 do
                embed (sprintf "warmup %d" i) |> ignore

            let latencies = ResizeArray<int64>(100)
            for i in 0 .. 99 do
                let prompt = sprintf "request %d: a moderately detailed prompt with some content to embed" i
                let sw = Stopwatch.StartNew()
                embed prompt |> ignore
                sw.Stop()
                latencies.Add(sw.ElapsedMilliseconds)

            let sorted = latencies |> Seq.sort |> Array.ofSeq
            // p95 of 100 samples = index 94 (0-based; ceil(0.95 * 100) - 1)
            let p95 = sorted[94]
            let p50 = sorted[49]
            // Print for telemetry (Expecto captures stdout; runs through the SUMMARY)
            printfn "EMBED-03 latency: p50=%dms, p95=%dms, p99=%dms" p50 p95 (sorted[98])
            Expect.isLessThan p95 50L (sprintf "p95 must be under 50ms warm-path (got %dms)" p95)
    ]
```

2) Edit `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — add `MLEmbeddingTests.fs` to compile order BEFORE `RouterTests.fs`:

```xml
<Compile Include="RoutingTests.fs" />
<Compile Include="StreamingTests.fs" />
<Compile Include="QueueTests.fs" />
<Compile Include="LoadTests.fs" />
<Compile Include="MLRoutingTests.fs" />
<Compile Include="MLEmbeddingTests.fs" />     <!-- NEW: Phase 6 -->
<Compile Include="LoggingTests.fs" />
<Compile Include="RouterTests.fs" />
```

3) Edit `tests/SmartRouter.Tests/RouterTests.fs` — append `MLEmbeddingTests.tests` to `rootTests`:

```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        SmartRouter.Tests.StreamingTests.tests
        SmartRouter.Tests.QueueTests.tests
        SmartRouter.Tests.LoadTests.tests
        SmartRouter.Tests.MLRoutingTests.tests
        SmartRouter.Tests.MLEmbeddingTests.tests       // NEW: Phase 6
        SmartRouter.Tests.LoggingTests.tests
    ]
```
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds.
- With embedding files present: `dotnet test --no-build --filter "MLEmbeddingTests"` reports 4 tests passing (EMBED-01, EMBED-02, CLS-03, EMBED-03).
- Without embedding files: `dotnet test --no-build --filter "MLEmbeddingTests"` reports 4 pending (ptestCase-skipped).
- Full `dotnet test --no-build` reports either 53/53 passing or 49/53 + 4 pending (MLEmbeddingTests state-dependent).
- EMBED-03 console output prints `EMBED-03 latency: p50=Xms, p95=Yms, p99=Zms` — record in 06-03-SUMMARY.md.
  </verify>
  <done>
MLEmbeddingTests.fs has 4 tests covering EMBED-01, EMBED-02, EMBED-03, CLS-03. All wrapped in testSequenced + ptestCase-gated on embedding file presence. Wired into rootTests + .fsproj compile list. Tests pass when files present; gracefully skip when absent.
  </done>
</task>

<task type="auto">
  <name>Task 2: MLClassifierTests.fs — CLS-01 + CLS-02 + extend MLRoutingTests with model_version + DI smoke</name>
  <files>
    tests/SmartRouter.Tests/MLClassifierTests.fs
    tests/SmartRouter.Tests/MLRoutingTests.fs
    tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
    tests/SmartRouter.Tests/RouterTests.fs
  </files>
  <action>
**REQ-IDs satisfied: CLS-01, CLS-02; extends MLRoutingTests with model_version verification + DI smoke.**

1) Create `tests/SmartRouter.Tests/MLClassifierTests.fs`:

```fsharp
module SmartRouter.Tests.MLClassifierTests

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.ML
open Microsoft.ML
open SmartRouter.Cli.Adapters.MlNetClassifier
open SmartRouter.Cli.Adapters.ModelBootstrapper

// ── Test temp-dir hygiene ────────────────────────────────────────────────────

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-classifier-tests-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try
        if Directory.Exists dir then Directory.Delete(dir, recursive = true)
    with _ -> ()

// ── Tests ────────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced <| testList "MLClassifierTests" [

        // ── CLS-02: first-run bootstrap creates router.zip ────────────────────
        testCase "CLS-02: ensureDummyModel creates router.zip when missing (idempotent on second call)" <| fun () ->
            let dir = mkTempDir ()
            try
                let modelPath = Path.Combine(dir, "router.zip")
                Expect.isFalse (File.Exists modelPath) "precondition: file does not exist"

                ensureDummyModel modelPath
                Expect.isTrue  (File.Exists modelPath) "after first call: file exists"
                let firstSize = (FileInfo modelPath).Length

                // Second call is a no-op (already exists)
                ensureDummyModel modelPath
                let secondSize = (FileInfo modelPath).Length
                Expect.equal firstSize secondSize "idempotent: second call did not rewrite the file"
            finally
                cleanupDir dir

        // ── CLS-01: PredictionEnginePool resolves dummy model and predicts on 1024-dim ──
        testCase "CLS-01: PredictionEnginePool loads bootstrapped model and predicts on 1024-dim vector" <| fun () ->
            let dir = mkTempDir ()
            try
                let modelPath = Path.Combine(dir, "router.zip")
                ensureDummyModel modelPath

                // Build a minimal DI container that registers the pool against the temp model
                let services = ServiceCollection()
                services.AddLogging() |> ignore
                services
                    .AddPredictionEnginePool<RouteInput, RoutePrediction>()
                    .FromFile(
                        modelName       = "router",
                        filePath        = modelPath,
                        watchForChanges = false)
                    |> ignore

                use sp = services.BuildServiceProvider()
                let pool = sp.GetRequiredService<PredictionEnginePool<RouteInput, RoutePrediction>>()
                let classifier = MlNetClassifier(pool) :> SmartRouter.Core.MLPorts.IClassifier

                // Random 1024-dim input
                let rng = Random(7)
                let embedding = Array.init 1024 (fun _ -> float32 (rng.NextDouble()))
                let pred = classifier.PredictAsync(embedding, CancellationToken.None).GetAwaiter().GetResult()

                Expect.isTrue (pred.Score >= 0.0f && pred.Score <= 1.0f)
                              (sprintf "Score in [0,1] (got %f)" pred.Score)
            finally
                cleanupDir dir

        // ── CLS-02 helper: computeModelVersion produces 8-hex-char hash ─────
        testCase "CLS-02: computeModelVersion returns 8 lowercase hex chars from router.zip" <| fun () ->
            let dir = mkTempDir ()
            try
                let modelPath = Path.Combine(dir, "router.zip")
                ensureDummyModel modelPath

                let v = computeModelVersion modelPath
                Expect.equal v.Length 8 "8 hex chars (4 bytes of SHA-256)"
                Expect.isTrue (v |> Seq.forall (fun c -> System.Char.IsAsciiHexDigitLower c || System.Char.IsDigit c))
                              (sprintf "all chars are lowercase hex (got %s)" v)
            finally
                cleanupDir dir
    ]
```

2) Edit `tests/SmartRouter.Tests/MLRoutingTests.fs` — append two tests after the existing five:

```fsharp
// ── Phase 6: model_version is hash-based, not the placeholder ────────────────
testCase "Phase 6: RoutingAlgorithmRegistration.ModelVersion = sprintf \"ml-%s\" (8 hex chars)" <| fun () ->
    // Skip when embedding files are absent — DI-based test goes through the real ml-branch wiring
    if not (System.IO.File.Exists "models/embed/bge-m3-int8.onnx"
            && System.IO.File.Exists "models/embed/sentencepiece.bpe.model") then
        skiptest "embedding files missing — run scripts/download-models.sh"

    let services = ServiceCollection()
    let testConfig =
        ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional = false)
            .AddInMemoryCollection(dict [ "Routing:Algorithm", "ml" ])
            .Build()
    configureServices services testConfig |> ignore
    use sp = services.BuildServiceProvider()
    let reg = sp.GetRequiredService<SmartRouter.Cli.Adapters.RoutingAlgorithm.RoutingAlgorithmRegistration>()
    Expect.equal reg.Name "ml" "Name = ml"
    Expect.isTrue (reg.ModelVersion.StartsWith "ml-") (sprintf "starts with ml- (got %s)" reg.ModelVersion)
    Expect.equal reg.ModelVersion.Length 11 "ml- + 8 hex chars = 11 chars total"
    Expect.notEqual reg.ModelVersion "ml-v0-placeholder" "no longer the Phase 4 placeholder"

// ── Phase 6: DI smoke — IEmbedder + IClassifier resolve from ml-branch ───────
testCase "Phase 6: DI ml-branch resolves IEmbedder + IClassifier without throwing" <| fun () ->
    if not (System.IO.File.Exists "models/embed/bge-m3-int8.onnx"
            && System.IO.File.Exists "models/embed/sentencepiece.bpe.model") then
        skiptest "embedding files missing — run scripts/download-models.sh"

    let services = ServiceCollection()
    let testConfig =
        ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional = false)
            .AddInMemoryCollection(dict [ "Routing:Algorithm", "ml" ])
            .Build()
    configureServices services testConfig |> ignore
    use sp = services.BuildServiceProvider()
    let emb = sp.GetRequiredService<SmartRouter.Core.MLPorts.IEmbedder>()
    let cls = sp.GetRequiredService<SmartRouter.Core.MLPorts.IClassifier>()
    Expect.isNotNull emb "IEmbedder resolves"
    Expect.isNotNull cls "IClassifier resolves"
```

3) Edit `tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — add `MLClassifierTests.fs` BEFORE `RouterTests.fs`:

```xml
<Compile Include="MLEmbeddingTests.fs" />
<Compile Include="MLClassifierTests.fs" />     <!-- NEW: Phase 6 -->
<Compile Include="LoggingTests.fs" />
<Compile Include="RouterTests.fs" />
```

4) Edit `tests/SmartRouter.Tests/RouterTests.fs` — append to `rootTests`:

```fsharp
let rootTests : Test list =
    [
        SmartRouter.Tests.RoutingTests.tests
        SmartRouter.Tests.StreamingTests.tests
        SmartRouter.Tests.QueueTests.tests
        SmartRouter.Tests.LoadTests.tests
        SmartRouter.Tests.MLRoutingTests.tests
        SmartRouter.Tests.MLEmbeddingTests.tests
        SmartRouter.Tests.MLClassifierTests.tests      // NEW: Phase 6
        SmartRouter.Tests.LoggingTests.tests
    ]
```
  </action>
  <verify>
- `cd /Users/ohama/projs/smart-router && dotnet build` succeeds.
- `cd /Users/ohama/projs/smart-router && dotnet test --no-build --filter "MLClassifierTests"` reports 3 tests passing (CLS-02 bootstrap + idempotent, CLS-01 pool predict, CLS-02 hash 8 hex).
  - These tests run in temp dirs and do NOT need the embedding ONNX files; CLS-02 + CLS-01 use temp router.zip only.
- `cd /Users/ohama/projs/smart-router && dotnet test --no-build --filter "MLRoutingTests"` reports 7 tests (5 existing + 2 new):
  - With embedding files: 7/7 pass.
  - Without: 5 pass + 2 skipped (skiptest).
- Full `dotnet test --no-build`:
  - With embedding files: 56/56 (49 existing + 4 EMBED + 3 CLS).
  - Without: 49 + 3 (CLS - temp dir, no skip) + 0 (EMBED skipped) = 52 pass + 4 pending (EMBED) + 2 skipped (MLRoutingTests new ones).
- `cd /Users/ohama/projs/smart-router && grep -c "Phase 6" tests/SmartRouter.Tests/MLRoutingTests.fs` returns at least 2 (the two new tests).
  </verify>
  <done>
MLClassifierTests.fs has 3 tests (CLS-02 bootstrap, CLS-01 predict, CLS-02 hash). MLRoutingTests.fs has 2 new tests (model_version hash format, DI smoke). All wired into rootTests + .fsproj. Tests pass green at full + partial states.
  </done>
</task>

</tasks>

<verification>
**Wave 3 acceptance bar:**
1. `dotnet build` succeeds across solution at warnings-as-errors.
2. `dotnet test --no-build` (with embedding files present) reports 56/56 passing:
   - 22 RoutingTests
   - 8 StreamingTests
   - 9 QueueTests
   - 2 LoadTests (pending — opt-in)
   - 7 MLRoutingTests (5 existing + 2 new)
   - 4 MLEmbeddingTests
   - 3 MLClassifierTests
   - 5 LoggingTests
3. `dotnet test --no-build` (without embedding files; fresh clone) reports 50/56 passing + 6 pending:
   - 4 MLEmbeddingTests pending
   - 2 MLRoutingTests new tests skipped (skiptest)
4. `scripts/check-no-async.sh` and `scripts/check-routing-isolation.sh` both exit 0.
5. EMBED-03 console output reports p95 < 50ms on M-series with embedding files; this evidence is captured in 06-03-SUMMARY.md.
6. `grep -c "ptestCase\|skiptest" tests/SmartRouter.Tests/MLEmbeddingTests.fs tests/SmartRouter.Tests/MLRoutingTests.fs` confirms graceful skip is wired.
</verification>

<success_criteria>
- All 6 Phase 6 REQ-IDs covered by at least one test.
- Tests pass green at full configuration (embedding files present).
- Tests gracefully skip on a fresh clone before scripts/download-models.sh runs.
- p95 latency benchmark < 50ms verified on M-series CPU.
- model_version is hash-based (`ml-{8hex}`), not `ml-v0-placeholder`.
- Cosine similarity > 0.7 on two ko↔en pairs proves bge-m3 multilingual semantics.
</success_criteria>

<output>
After completion, create `.planning/phases/06-real-ml-routing/06-03-SUMMARY.md` covering:
- 2 new test files + 2 new tests added to MLRoutingTests
- Test count breakdown (full / partial)
- p95 latency measurement (record actual values: p50, p95, p99)
- Cosine similarity values for the two CLS-03 ko-en pairs (record actual scores)
- Confirmation: all 6 Phase 6 REQ-IDs covered (table mapping REQ-ID → test name)
- Phase 6 acceptance bar: all 6 Success Criteria from ROADMAP.md proven
</output>
