module SmartRouter.Cli.Adapters.DatasetMerger

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Serilog
open SmartRouter.Core.RetrainingPorts
open SmartRouter.Cli.Adapters.Retrainer   // TrainSample lives here (compile order: Retrainer.fs precedes DatasetMerger.fs)

// ── JSON options — must match HardCaseDatasetWriter.jsonOpts ──────────────────
// SnakeCaseLower (matches PromptText -> prompt_text); JsonFSharpConverter for `string option`.
let private jsonOpts =
    let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
    o.Converters.Add(JsonFSharpConverter())
    o

// ── readHardCases ─────────────────────────────────────────────────────────────
//
// Reads datasets/hard-cases.jsonl with FileShare.ReadWrite so reads work concurrently
// with HardCaseDatasetWriter's FileShare.None writes (Pitfall 3 from RESEARCH.md).
// Returns [||] when file is absent (first-retrain bootstrap path).
// Malformed lines are logged Warning and skipped (mirrors HardCaseDatasetWriter.seedDedupe).

let readHardCases (path: string) : HardCaseEntry[] =
    if not (File.Exists path) then
        [||]
    else
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use reader = new StreamReader(stream, Encoding.UTF8)
        let entries = ResizeArray<HardCaseEntry>()
        let mutable line = reader.ReadLine()
        while not (isNull line) do
            if not (String.IsNullOrWhiteSpace(line)) then
                try
                    let entry = JsonSerializer.Deserialize<HardCaseEntry>(line, jsonOpts)
                    entries.Add(entry)
                with ex ->
                    Log.Warning(ex, "DatasetMerger: skipping malformed line in {Path}", path)
            line <- reader.ReadLine()
        entries.ToArray()

// ── readTrainingSet ───────────────────────────────────────────────────────────
//
// Same shape as readHardCases but for datasets/training-set.jsonl.
// Returns [||] when file is absent (first retrain — bootstrap case from CONTEXT.md Lock 3).

let readTrainingSet (path: string) : HardCaseEntry[] =
    readHardCases path  // identical schema; same FileShare; same dedupe-of-malformed semantics

// ── hardCaseToTrainSample ─────────────────────────────────────────────────────
//
// Converts HardCaseEntry to TrainSample. The embedder lives in Cli (BgeM3Embedder);
// this function takes an embedding callback so DatasetMerger stays free of the embedder dependency.
// Plan 08-02 supplies the callback by closing over `IEmbedder.EmbedAsync`.
//
// Label conversion: HardCaseEntry.Label is int (0=Route35B, 1=Route122B); TrainSample.Label is bool.
// Convention: Label=true => Route122B (positive class).

let hardCaseToTrainSample (embed: string -> float32[]) (entry: HardCaseEntry) : TrainSample =
    { Features = embed entry.PromptText
      Label    = entry.Label = 1 }

// ── merge ─────────────────────────────────────────────────────────────────────
//
// 70/30 stratified merge with class balance enforcement.
// CONTEXT.md Lock 3: when oldSamples is empty, skip the 70/30 ratio and return
//   new samples (post-rebalance) — do NOT inject synthetic noise.
// CONTEXT.md Lock: ratio >=30% each class; rebalance via oversample-with-replacement.

let private rebalance (samples: TrainSample[]) (rng: Random) : TrainSample[] =
    let class1 = samples |> Array.filter (fun s -> s.Label)
    let class0 = samples |> Array.filter (fun s -> not s.Label)
    if class0.Length = 0 || class1.Length = 0 then
        // Pathological: only one class present — return as-is and log.
        // Validator will reject the resulting model.
        Log.Warning(
            "DatasetMerger: only one class present (class0={C0}, class1={C1}); cannot rebalance",
            class0.Length, class1.Length)
        samples
    else
        let target = max class0.Length class1.Length
        let oversample (arr: TrainSample[]) (n: int) =
            if arr.Length >= n then arr
            else Array.append arr (Array.init (n - arr.Length) (fun _ -> arr.[rng.Next(arr.Length)]))
        let bal0 = oversample class0 target
        let bal1 = oversample class1 target
        Log.Information(
            "DatasetMerger: rebalanced minority class via oversampling — class0 {C0in}->{C0out}, class1 {C1in}->{C1out}",
            class0.Length, bal0.Length, class1.Length, bal1.Length)
        Array.append bal0 bal1 |> Array.sortBy (fun _ -> rng.Next())

let private classBalanceOk (samples: TrainSample[]) : bool =
    if samples.Length = 0 then false
    else
        let total  = float samples.Length
        let class1 = samples |> Array.filter (fun s -> s.Label) |> Array.length |> float
        let class0 = total - class1
        let r0 = class0 / total
        let r1 = class1 / total
        r0 >= 0.30 && r1 >= 0.30

let merge
    (oldSamples : TrainSample[])
    (newSamples : TrainSample[])
    (rng        : Random)
    : TrainSample[] =

    // ── Bootstrap path: no "old" dataset (first retrain). ────────────────────
    if oldSamples.Length = 0 then
        Log.Information(
            "DatasetMerger: no existing training set found; using {N} new samples as bootstrap (70/30 split skipped)",
            newSamples.Length)
        if classBalanceOk newSamples then
            newSamples |> Array.sortBy (fun _ -> rng.Next())
        else
            rebalance newSamples rng

    // ── Standard path: 70/30 merge. ──────────────────────────────────────────
    else
        let totalTarget = oldSamples.Length + newSamples.Length
        let oldTarget   = int (float totalTarget * 0.7)
        let newTarget   = totalTarget - oldTarget   // exact integer complement

        let sampleN (arr: TrainSample[]) (n: int) : TrainSample[] =
            if arr.Length = 0 then [||]
            elif arr.Length >= n then
                arr |> Array.sortBy (fun _ -> rng.Next()) |> Array.take n
            else
                // Need oversampling: repeat with replacement.
                Array.init n (fun _ -> arr.[rng.Next(arr.Length)])

        let oldPart = sampleN oldSamples oldTarget
        let newPart = sampleN newSamples newTarget
        let merged  = Array.append oldPart newPart

        if classBalanceOk merged then
            merged |> Array.sortBy (fun _ -> rng.Next())
        else
            Log.Warning(
                "DatasetMerger: class balance violated after 70/30 merge (class0/class1 ratios <30%); rebalancing")
            rebalance merged rng

// ── saveTrainingSet ───────────────────────────────────────────────────────────
//
// Writes the merged dataset back to datasets/training-set.jsonl for next cycle.
// Atomic via .tmp + File.Move(overwrite=true). Same schema as hard-cases.jsonl
// (HardCaseEntry) so readTrainingSet/readHardCases share a deserializer.
//
// Plan 08-02 calls this AFTER successful model write; if it fails, the next
// retrain re-merges from hard-cases.jsonl + the previous training-set.jsonl
// (idempotent — validation will gate the model write either way).

let saveTrainingSet (path: string) (entries: HardCaseEntry[]) : unit =
    let dir = Path.GetDirectoryName(path)
    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore

    let tmp = path + ".tmp"
    use stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
    use writer = new StreamWriter(stream, Encoding.UTF8)
    writer.AutoFlush <- false
    for entry in entries do
        writer.WriteLine(JsonSerializer.Serialize(entry, jsonOpts))
    writer.Flush()
    writer.Dispose()
    stream.Dispose()
    File.Move(tmp, path, overwrite = true)
