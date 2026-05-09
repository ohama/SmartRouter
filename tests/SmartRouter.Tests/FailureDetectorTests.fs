module SmartRouter.Tests.FailureDetectorTests

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.Extensions.Logging.Abstractions
open SmartRouter.Cli.Adapters.FailureDetector
open SmartRouter.Core.RetrainingPorts

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-failure-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

/// Build one DecisionLog JSONL line with given fallback_used + correlation_id.
/// Other fields filled with stable test values matching the snake_case wire shape.
let private makeJsonLine (correlationId: string) (fallbackUsed: bool) : string =
    let fallback = if fallbackUsed then "true" else "false"
    sprintf
        "{\"schema_version\":1,\"correlation_id\":\"%s\",\"prompt_hash\":\"abc123\",\"prompt_korean_char_ratio\":0.0,\"routing_algorithm\":\"ml\",\"routing_reason\":\"ml\",\"target\":\"Qwen35B\",\"latency_ms\":42.0,\"fallback_used\":%s,\"model_version\":\"ml-test\",\"task_type\":null,\"timestamp\":\"2026-05-08T00:00:00+00:00\"}"
        correlationId fallback

let private extractHardCases (dir: string) : HardCase list =
    let detector = FailureDetector(dir, NullLogger<FailureDetector>.Instance) :> IFailureDetector
    detector.ExtractHardCases(CancellationToken.None).GetAwaiter().GetResult()

let tests =
    testSequenced (
        testList "FailureDetector" [

            testCase "empty directory returns empty list" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let result = extractHardCases dir
                    Expect.isEmpty result "empty dir → 0 hard cases"
                finally cleanupDir dir

            testCase "non-existent directory returns empty list" <| fun _ ->
                let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-nonexistent-" + Path.GetRandomFileName())
                let result = extractHardCases dir
                Expect.isEmpty result "missing dir → 0 hard cases"

            testCase "all entries with fallback_used=false returns empty list" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "2026-05-08.jsonl")
                    let lines = [
                        makeJsonLine "cid-1" false
                        makeJsonLine "cid-2" false
                        makeJsonLine "cid-3" false
                    ]
                    File.WriteAllLines(path, lines)
                    let result = extractHardCases dir
                    Expect.isEmpty result "all-false → 0 hard cases"
                finally cleanupDir dir

            testCase "single fallback_used=true entry returns one HardCase" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "2026-05-08.jsonl")
                    let lines = [
                        makeJsonLine "cid-1" false
                        makeJsonLine "cid-2" true
                        makeJsonLine "cid-3" false
                    ]
                    File.WriteAllLines(path, lines)
                    let result = extractHardCases dir
                    Expect.equal (List.length result) 1 "exactly one match"
                    Expect.equal result.Head.CorrelationId "cid-2" "correct correlation_id surfaced"
                    Expect.equal result.Head.PromptText None "prompt text always None for log-extracted hard cases (LOG-01 stores hash only)"
                finally cleanupDir dir

            testCase "malformed JSON lines skipped without crashing" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "2026-05-08.jsonl")
                    let lines = [
                        makeJsonLine "cid-1" true
                        "this is not json at all { ] }"
                        ""                     // blank line — explicitly OK
                        makeJsonLine "cid-2" true
                    ]
                    File.WriteAllLines(path, lines)
                    let result = extractHardCases dir
                    Expect.equal (List.length result) 2 "two valid matches; malformed + blank skipped"
                finally cleanupDir dir

            testCase "multiple JSONL files in directory all read" <| fun _ ->
                let dir = mkTempDir ()
                try
                    File.WriteAllLines(
                        Path.Combine(dir, "2026-05-06.jsonl"),
                        [ makeJsonLine "day-1-cid-1" true
                          makeJsonLine "day-1-cid-2" false ])
                    File.WriteAllLines(
                        Path.Combine(dir, "2026-05-07.jsonl"),
                        [ makeJsonLine "day-2-cid-1" true
                          makeJsonLine "day-2-cid-2" true ])
                    let result = extractHardCases dir
                    Expect.equal (List.length result) 3 "3 fallback_used=true across 2 files"
                finally cleanupDir dir
        ])
