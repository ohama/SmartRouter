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
