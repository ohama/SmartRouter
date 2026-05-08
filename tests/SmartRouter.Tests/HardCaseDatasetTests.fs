module SmartRouter.Tests.HardCaseDatasetTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open SmartRouter.Cli.Adapters.HardCaseDatasetWriter
open SmartRouter.Core.RetrainingPorts

let private mkTempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "smart-router-tests-dataset-" + Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir (dir: string) =
    try if Directory.Exists(dir) then Directory.Delete(dir, recursive = true)
    with _ -> ()

let private mkEntry (cid: string) (hash: string) (label: int) : HardCaseEntry =
    { SchemaVersion          = 1
      CorrelationId          = cid
      PromptHash             = hash
      PromptText             = "test prompt"
      Label                  = label
      Source                 = "test"
      TeacherResponseExcerpt = Some "ROUTE_35B"
      LabeledAt              = DateTimeOffset.UtcNow
      PromptKoreanCharRatio  = 0.0
      RoutingAlgorithm       = "ml"
      Target                 = if label = 0 then "Qwen35B" else "Qwen122B" }

/// Build, start, drive, and stop a HardCaseDatasetWriter against a temp file path.
/// Returns the (closed) lines from the file after StopAsync drains.
let private runWith (path: string) (capacity: int) (act: IHardCaseDatasetWriter -> Task<unit>) : string list =
    let opts = { Path = path; ChannelCapacity = capacity }
    let writer = new HardCaseDatasetWriter(opts)
    writer.StartAsync(CancellationToken.None).GetAwaiter().GetResult()
    try
        (act (writer :> IHardCaseDatasetWriter)).GetAwaiter().GetResult()
    finally
        writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult()
        (writer :> IDisposable).Dispose()
    if File.Exists(path) then
        File.ReadAllLines(path)
        |> Array.toList
        |> List.filter (fun s -> not (String.IsNullOrWhiteSpace(s)))
    else
        []

let tests =
    testSequenced (
        testList "HardCaseDatasetWriter" [

            testCase "AppendAsync writes one valid JSONL line" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let entry = mkEntry "cid-1" "hash-1" 0
                    let lines = runWith path 100 (fun w ->
                        task {
                            do! w.AppendAsync(entry, CancellationToken.None)
                        })
                    Expect.equal (List.length lines) 1 "exactly one line written"
                    // Validate the written line parses as JSON and has expected fields.
                    use doc = JsonDocument.Parse(lines.Head)
                    Expect.equal (doc.RootElement.GetProperty("correlation_id").GetString()) "cid-1" "correlation_id roundtrip"
                    Expect.equal (doc.RootElement.GetProperty("prompt_hash").GetString())    "hash-1" "prompt_hash roundtrip"
                    Expect.equal (doc.RootElement.GetProperty("schema_version").GetInt32())  1        "schema_version=1"
                    Expect.equal (doc.RootElement.GetProperty("label").GetInt32())            0        "label=0 (Route35B)"
                    Expect.equal (doc.RootElement.GetProperty("source").GetString())          "test"   "source field present"
                finally cleanupDir dir

            testCase "duplicate (correlation_id, prompt_hash) not appended twice" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let entry1 = mkEntry "cid-1" "hash-1" 0
                    let entry2 = mkEntry "cid-1" "hash-1" 1   // same dedupe key, different label — still dropped
                    let lines = runWith path 100 (fun w ->
                        task {
                            do! w.AppendAsync(entry1, CancellationToken.None)
                            do! w.AppendAsync(entry2, CancellationToken.None)
                        })
                    Expect.equal (List.length lines) 1 "dedupe drops the second write"
                finally cleanupDir dir

            testCase "50 concurrent AppendAsync calls produce 50 valid non-interleaved JSONL lines" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let lines =
                        runWith path 100 (fun w ->
                            task {
                                let appends =
                                    [| for i in 1 .. 50 ->
                                        let e = mkEntry (sprintf "cid-%d" i) (sprintf "hash-%d" i) (i % 2)
                                        (w.AppendAsync(e, CancellationToken.None) :> Task) |]
                                do! Task.WhenAll(appends)
                            })
                    Expect.equal (List.length lines) 50 "all 50 entries persisted"
                    // Each line must parse as JSON — proves no interleaving.
                    let allParse =
                        lines
                        |> List.forall (fun ln ->
                            try
                                use _doc = JsonDocument.Parse(ln)
                                true
                            with _ -> false)
                    Expect.isTrue allParse "every line parses as JSON (no interleaving)"
                finally cleanupDir dir

            testCase "graceful StopAsync drains in-flight entries" <| fun _ ->
                let dir = mkTempDir ()
                try
                    let path = Path.Combine(dir, "ds.jsonl")
                    let opts = { Path = path; ChannelCapacity = 100 }
                    let writer = new HardCaseDatasetWriter(opts)
                    writer.StartAsync(CancellationToken.None).GetAwaiter().GetResult()
                    try
                        // Burst 10 entries
                        let iface = writer :> IHardCaseDatasetWriter
                        for i in 1 .. 10 do
                            let e = mkEntry (sprintf "cid-%d" i) (sprintf "hash-%d" i) 0
                            iface.AppendAsync(e, CancellationToken.None).GetAwaiter().GetResult()
                    finally
                        // StopAsync MUST drain the channel
                        writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult()
                        (writer :> IDisposable).Dispose()
                    let lines =
                        if File.Exists(path) then
                            File.ReadAllLines(path) |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.toList
                        else []
                    Expect.equal (List.length lines) 10 "all 10 entries flushed on graceful shutdown"
                finally cleanupDir dir
        ])
