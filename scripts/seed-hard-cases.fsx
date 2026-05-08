// scripts/seed-hard-cases.fsx
//
// Synthetic hard-case seed for Phase 8's retraining loop.
// Run after first router startup and download-models.sh.
//
// Usage:
//   dotnet fsi scripts/seed-hard-cases.fsx
//
// Writes ~30 entries to datasets/hard-cases.jsonl. Each entry has source="synthetic"
// and correlation_id="seed-NNN" so downstream consumers can filter or weight them.
//
// Idempotency: re-runs overwrite the file (append = false) — acceptable for v1
// since this is a seed script. Operators who want to preserve handcrafted entries
// should use the live HardCaseDatasetWriter via --retrain CLI or Phase 8 BackgroundService.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

let datasetsDir = "datasets"
let outPath     = Path.Combine(datasetsDir, "hard-cases.jsonl")

let computeHash (s: string) : string =
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(s)
    let hash  = sha.ComputeHash(bytes)
    hash |> Array.map (sprintf "%02x") |> String.concat ""

let koreanRatio (s: string) : float =
    if s.Length = 0 then 0.0
    else
        let n = s |> Seq.filter (fun c -> c >= '가' && c <= '힣') |> Seq.length
        float n / float s.Length

// (prompt, label_string) pairs — label is "Qwen35B" or "Qwen122B"
let hardCases : (string * string) list = [
    // English — easy / casual → 35B
    "what is 2+2",                                                                "Qwen35B"
    "summarize this paragraph in one sentence",                                   "Qwen35B"
    "list the days of the week",                                                  "Qwen35B"
    "what time zone is Seoul in",                                                 "Qwen35B"
    "translate 'hello' to Spanish",                                               "Qwen35B"
    "give me a one-line bash command to count lines in a file",                   "Qwen35B"

    // English — hard / technical → 122B
    "explain F# compiler error 'value restriction' with a minimal repro",         "Qwen122B"
    "design a hexagonal architecture for a multi-tenant SaaS billing service",    "Qwen122B"
    "debug a deadlock in a Go service using two channels and a SemaphoreSlim",    "Qwen122B"
    "refactor a 1500-line ASP.NET controller into clean vertical slices",         "Qwen122B"
    "trace an LLVM IR optimization pass that removes a side-effecting call",      "Qwen122B"
    "explain MLIR dialect lowering for a custom domain-specific operation",       "Qwen122B"

    // Korean — easy / casual → 35B
    "안녕하세요",                                                                  "Qwen35B"
    "오늘 날씨가 어때요",                                                          "Qwen35B"
    "한국어로 인사하는 법",                                                        "Qwen35B"
    "1 더하기 1은 무엇인가요",                                                     "Qwen35B"
    "서울에서 가장 유명한 음식 한 가지만 알려주세요",                              "Qwen35B"

    // Korean — hard / technical → 122B
    "F# 컴파일러 타입 추론 에러를 해결하기 위한 단계별 디버깅 전략을 설명해주세요", "Qwen122B"
    "이 코드의 메모리 누수 원인을 분석하고 패치 방법을 제안해주세요",              "Qwen122B"
    "트랜잭션 격리 수준을 설명하고 각 수준에서 발생하는 이상현상을 정리해주세요",  "Qwen122B"
    "헥사고날 아키텍처와 클린 아키텍처의 차이점을 비교 분석해주세요",              "Qwen122B"

    // Mixed Korean+English — varying difficulty
    "F# task {} 에러 메시지 'control flow not allowed' 해결 방법",                 "Qwen122B"
    "react useState hook 사용법 짧게 알려줘",                                      "Qwen35B"
    "Kubernetes pod CrashLoopBackOff 원인 진단 절차",                              "Qwen122B"
    "python list comprehension 예시 하나만",                                       "Qwen35B"

    // Code-heavy English → 122B
    "given this stack trace, identify the race condition: ...",                   "Qwen122B"
    "rewrite this O(n^2) algorithm in O(n log n)",                                "Qwen122B"

    // Short factual → 35B
    "capital of France",                                                          "Qwen35B"
    "current year",                                                               "Qwen35B"
    "who wrote 1984",                                                             "Qwen35B"
]

Directory.CreateDirectory(datasetsDir) |> ignore

let opts = JsonSerializerOptions(WriteIndented = false)

let writeEntries () =
    use sw = new StreamWriter(outPath, append = false, encoding = UTF8Encoding(false))
    let mutable idx = 1
    for (prompt, label) in hardCases do
        let labelInt = if label = "Qwen122B" then 1 else 0
        let entry =
            {| schema_version            = 1
               correlation_id            = sprintf "seed-%03d" idx
               prompt_hash               = computeHash prompt
               prompt_text               = prompt
               label                     = labelInt
               source                    = "synthetic"
               teacher_response_excerpt  = (null : string)   // serialize as null — no teacher response for synthetic entries
               labeled_at                = DateTimeOffset.UtcNow.ToString("o")
               prompt_korean_char_ratio  = koreanRatio prompt
               routing_algorithm         = "synthetic"
               target                    = label |}
        sw.WriteLine(JsonSerializer.Serialize(entry, opts))
        idx <- idx + 1
    idx - 1

let count = writeEntries ()
printfn "Wrote %d synthetic hard-case entries to %s" count outPath
