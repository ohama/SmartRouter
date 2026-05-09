module SmartRouter.Cli.Adapters.BgeM3Embedder

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open Microsoft.ML.Tokenizers
open SmartRouter.Core.MLPorts

/// bge-m3 embedder adapter — int8 quantized ONNX + XLM-R SentencePiece tokenizer.
/// Produces 1024-dim L2-normalized float32 vectors via mean pooling over attention_mask.
/// Disposable: owns the InferenceSession; should be a DI singleton.
type BgeM3Embedder(onnxPath: string, tokenizerPath: string, maxTokens: int, logger: ILogger<BgeM3Embedder>) =
    do
        if not (File.Exists onnxPath) then
            failwithf
                "ML embedding model not found at %s. Run scripts/download-models.sh first."
                onnxPath
        if not (File.Exists tokenizerPath) then
            failwithf
                "ML tokenizer not found at %s. Run scripts/download-models.sh first."
                tokenizerPath

    // SentencePieceTokenizer.Create takes raw sentencepiece.bpe.model binary (not tokenizer.json).
    // XLM-R / bge-m3 convention: addBeginningOfSentence=true (<s>), addEndOfSentence=false.
    // NOTE: SentencePieceTokenizer does NOT implement IDisposable in Microsoft.ML.Tokenizers 2.0.0.
    let tokenizer : SentencePieceTokenizer =
        use stream = File.OpenRead(tokenizerPath)
        SentencePieceTokenizer.Create(
            stream,
            addBeginningOfSentence = true,
            addEndOfSentence       = false)

    let session = new InferenceSession(onnxPath)

    let encode (text: string) : int64[] =
        // EncodeToIds with maxTokenCount requires out-params for normalizedText + charsConsumed.
        let mutable normalizedText : string = null
        let mutable charsConsumed  : int    = 0
        tokenizer.EncodeToIds(
            text,
            addBeginningOfSentence = true,
            addEndOfSentence       = false,
            maxTokenCount          = maxTokens,
            normalizedText         = &normalizedText,
            charsConsumed          = &charsConsumed)
        |> Seq.map int64
        |> Array.ofSeq

    let runOnnx (ids: int64[]) : float32[] =
        let mask   = Array.create ids.Length 1L
        let seqLen = ids.Length
        let dims   = ReadOnlySpan<int>([| 1; seqLen |])
        let inputIdsTensor = new DenseTensor<int64>(ids,  dims)
        let attnMaskTensor = new DenseTensor<int64>(mask, dims)
        let inputs : NamedOnnxValue list = [
            NamedOnnxValue.CreateFromTensor("input_ids",      inputIdsTensor)
            NamedOnnxValue.CreateFromTensor("attention_mask", attnMaskTensor)
        ]
        use results = session.Run(inputs)
        // Output 0 = last_hidden_state shape [1, seqLen, 1024]
        // AsTensor<float32> returns Tensor<float32> (abstract base); cast to DenseTensor to access Buffer.
        let hidden    = (results |> Seq.head).AsTensor<float32>().ToDenseTensor()
        let hiddenArr = hidden.Buffer.ToArray()   // row-major: seqLen * 1024
        // Mean pool over valid (mask=1) tokens
        let dim = 1024
        let pooled        = Array.zeroCreate<float32> dim
        let mutable valid = 0.0f
        for t in 0 .. seqLen - 1 do
            if mask[t] = 1L then
                valid <- valid + 1.0f
                for d in 0 .. dim - 1 do
                    pooled[d] <- pooled[d] + hiddenArr[t * dim + d]
        // Guard against empty mask (truncation pathological case)
        if valid < 1.0f then valid <- 1.0f
        for d in 0 .. dim - 1 do
            pooled[d] <- pooled[d] / valid
        // L2 normalize
        let mutable acc = 0.0f
        for v in pooled do acc <- acc + v * v
        let norm = sqrt acc
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
            logger.LogInformation("BgeM3Embedder warm-up complete (model: {OnnxPath})", onnxPath)
        with ex ->
            logger.LogWarning(ex, "BgeM3Embedder warm-up failed; continuing")

    interface IEmbedder with
        member _.EmbedAsync(prompt: string, _ct: CancellationToken) : Task<float32[]> =
            task {
                let ids = encode prompt
                return runOnnx ids
            }

    interface IDisposable with
        member _.Dispose() =
            session.Dispose()
            // SentencePieceTokenizer does not implement IDisposable in 2.0.0 — no disposal needed.
            ()
