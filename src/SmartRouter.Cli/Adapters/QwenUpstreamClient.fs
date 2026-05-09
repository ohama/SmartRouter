module SmartRouter.Cli.Adapters.QwenUpstreamClient

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Microsoft.Extensions.Options
open Serilog
open SmartRouter.Core.Domain
open SmartRouter.Core.Ports
open SmartRouter.Cli.Adapters.Json

// ── Configuration types ──────────────────────────────────────────────────────

/// Upstream URL configuration bound from appsettings.json "Upstreams" section.
/// Used by QwenUpstreamClient to resolve the correct base URL per ModelId.
[<CLIMutable>]
type UpstreamOptions =
    { Model35B  : string   // "http://127.0.0.1:8000"
      Model122B : string } // "http://127.0.0.1:8001"

// ── HF-id trap defense (lifted from blueCode QwenHttpClient.fs) ─────────────

/// Parse the best id from a GET /v1/models response body.
///
/// Heuristic: some servers (notably mlx_lm.server) advertise multiple ids per
/// model entry — e.g. a HuggingFace repo id ("Qwen/Qwen2.5-Coder-32B") alongside
/// the local absolute path ("/Users/.../qwen35b"). Sending the HF id back in the
/// POST "model" field triggers the server's HF Hub fallback, which refetches the
/// tokenizer and overwrites the currently-loaded Instruct tokenizer with the Base
/// variant, destroying chat-template behavior (PITFALL-1 / ROUT-07).
///
/// We prefer ids that start with '/' (absolute filesystem paths on macOS/Linux)
/// because those keep the server on its locally-loaded tokenizer. HF repo ids are
/// of shape "Org/Name" (slash in the middle, never at the start) so there is no
/// collision.
///
/// Fallback: when no path-like id is present (vllm, llama.cpp, and other single-id
/// servers), we return the first non-empty id.
///
/// Returns None on missing data array, empty data array, no usable ids, or JSON
/// parse error.
///
/// PUBLIC for testability (pure, no IO — mirrors blueCode pattern).
let tryParseModelId (json: string) : string option =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        match root.TryGetProperty("data") with
        | true, data when data.ValueKind = JsonValueKind.Array && data.GetArrayLength() > 0 ->
            // Collect all non-empty string ids, in original array order.
            let ids =
                seq {
                    for i in 0 .. data.GetArrayLength() - 1 do
                        let entry = data.[i]
                        match entry.TryGetProperty("id") with
                        | true, el when el.ValueKind = JsonValueKind.String ->
                            let s = el.GetString()
                            if not (String.IsNullOrEmpty(s)) then yield s
                        | _ -> ()
                }
                |> List.ofSeq

            // Prefer the first path-like id (absolute filesystem path → keeps
            // loaded tokenizer, avoids HF Hub fallback on mlx_lm.server).
            // Fall back to the first usable id for single-id servers.
            match ids |> List.tryFind (fun s -> s.StartsWith("/")) with
            | Some pathId -> Some pathId
            | None        -> List.tryHead ids
        | _ -> None
    with _ ->
        None

// ── Probe upstream /v1/models once per process per port ─────────────────────

/// Probe http://<baseUrl>/v1/models and extract the local-path model id.
/// Uses CancellationToken.None — the probe is shared across all callers to the
/// same port and MUST NOT be cancelled by one caller's ct.
///
/// Returns Ok modelId on success, Error on any failure (HTTP error, missing id,
/// JSON parse error). Error here means the upstream is likely down or misconfigured;
/// CompleteAsync maps this to ModelUnavailable.
let private probeModelIdAsync (httpFactory: IHttpClientFactory) (clientName: string) (baseUrl: string) : Task<Result<string, RouterError>> =
    task {
        try
            let client = httpFactory.CreateClient(clientName)
            use! resp = client.GetAsync(baseUrl + "/v1/models", CancellationToken.None)

            if not resp.IsSuccessStatusCode then
                Log.Warning(
                    "GET {Url}/v1/models returned {Status}; model id unknown",
                    baseUrl, int resp.StatusCode)
                return Error (ModelUnavailable (Qwen35B, $"GET /v1/models returned HTTP {int resp.StatusCode}"))
            else
                let! json = resp.Content.ReadAsStringAsync(CancellationToken.None)
                match tryParseModelId json with
                | Some id ->
                    Log.Information("Probed {Url}/v1/models → model id = {ModelId}", baseUrl, id)
                    return Ok id
                | None ->
                    Log.Warning("GET {Url}/v1/models returned parseable JSON but data[0].id missing/empty; POST will likely 4xx", baseUrl)
                    return Error (ModelUnavailable (Qwen35B, "invalid /v1/models response"))
        with ex ->
            Log.Warning(ex, "GET {Url}/v1/models failed", baseUrl)
            return Error (ModelUnavailable (Qwen35B, $"probe failed: {ex.Message}"))
    }

// ── Wire format helpers ──────────────────────────────────────────────────────

let private roleString : MessageRole -> string =
    function
    | MessageRole.System    -> "system"
    | MessageRole.User      -> "user"
    | MessageRole.Assistant -> "assistant"

// ── QwenUpstreamClient ──────────────────────────────────────────────────────

/// Implements IUpstreamClient by forwarding requests to the appropriate
/// Qwen upstream via named HttpClients managed by IHttpClientFactory.
///
/// Per-process lazy probe: the first CompleteAsync call to each upstream fires
/// GET /v1/models to resolve the local-path model id (HF-id trap defense).
/// Subsequent calls to the same upstream reuse the cached Task result.
///
/// ARCH-06 note: registered as DI Singleton — stateless service; lazy probe
/// cache is process-lifetime. Named HttpClient instances are NOT cached in
/// fields — CreateClient is called per request (PITFALL-15).
type QwenUpstreamClient(httpFactory: IHttpClientFactory, opts: IOptions<UpstreamOptions>) =

    // Lazy probes per upstream. Fires ONCE on first access per process.
    // LazyThreadSafetyMode.ExecutionAndPublication guarantees single-probe
    // semantics under parallel calls. CancellationToken.None: probe is shared.
    let probe35b: Lazy<Task<Result<string, RouterError>>> =
        Lazy<Task<Result<string, RouterError>>>(
            fun () -> probeModelIdAsync httpFactory "upstream35b" opts.Value.Model35B)

    let probe122b: Lazy<Task<Result<string, RouterError>>> =
        Lazy<Task<Result<string, RouterError>>>(
            fun () -> probeModelIdAsync httpFactory "upstream122b" opts.Value.Model122B)

    let resolveClientName (target: ModelId) (stream: bool) =
        match target, stream with
        | Qwen35B,  false -> "upstream35b"
        | Qwen35B,  true  -> "upstream35b-stream"
        | Qwen122B, false -> "upstream122b"
        | Qwen122B, true  -> "upstream122b-stream"

    let resolveProbe (target: ModelId) (stream: bool) =
        let clientName = resolveClientName target stream
        let url, probe =
            match target with
            | Qwen35B  -> opts.Value.Model35B,  probe35b
            | Qwen122B -> opts.Value.Model122B, probe122b
        probe, clientName, url

    /// Non-streaming POST to upstream /v1/chat/completions.
    /// 1. Forces the lazy /v1/models probe to resolve the upstream local-path model id.
    /// 2. Builds request body with sampling defaults (client values win over defaults).
    /// 3. Merges req.UnknownFields verbatim into the serialized body (API-04).
    /// 4. POSTs to {upstreamUrl}/v1/chat/completions and returns the full response body.
    member _.CompleteAsync (req: RouterRequest) (decision: RoutingDecision) (ct: CancellationToken) : Task<Result<string, RouterError>> =
        task {
            let target = decision.Target
            let probe, clientName, upstreamUrl = resolveProbe target false   // non-stream

            // 1. Resolve upstream model id (lazy probe — fires at most once per process per upstream)
            let! probeResult = probe.Value
            match probeResult with
            | Error e -> return Error e
            | Ok modelId ->

            // 2. Build wire messages
            let msgs =
                req.Messages
                |> List.map (fun m ->
                    {| role = roleString m.Role; content = m.Content |})
                |> List.toArray

            // 3. Build request body dict — sampling defaults apply when client omits a field.
            //    Client values win: temperature/top_p/max_tokens are passed through if Some.
            //    top_k=20 and presence_penalty=0.0 are always added (Qwen 3.5 requires them).
            let bodyDict = Dictionary<string, obj>()
            bodyDict.["model"]           <- modelId
            bodyDict.["messages"]        <- msgs
            bodyDict.["stream"]          <- false  // Phase 1: never stream
            bodyDict.["temperature"]     <- (req.Temperature |> Option.defaultValue 0.7) :> obj
            bodyDict.["top_p"]           <- (req.TopP        |> Option.defaultValue 0.8) :> obj
            bodyDict.["top_k"]           <- 20 :> obj
            bodyDict.["presence_penalty"] <- 0.0 :> obj

            match req.MaxTokens with
            | Some n -> bodyDict.["max_tokens"] <- n :> obj
            | None   -> ()

            // 4. Merge UnknownFields last — client-supplied non-OpenAI fields flow through verbatim (API-04).
            //    These overwrite defaults if they collide, which is intentional (client is authority).
            for kv in req.UnknownFields do
                bodyDict.[kv.Key] <- kv.Value :> obj

            let bodyJson = JsonSerializer.Serialize(bodyDict, jsonOptions)

            Log.Debug("POST {Url}/v1/chat/completions body: {Body}", upstreamUrl, bodyJson)

            // 5. POST — do NOT cache the client instance (PITFALL-15)
            let client = httpFactory.CreateClient(clientName)
            try
                use reqMsg = new HttpRequestMessage(HttpMethod.Post, upstreamUrl + "/v1/chat/completions")
                reqMsg.Content <- new StringContent(bodyJson, Encoding.UTF8, "application/json")

                use! resp = client.SendAsync(reqMsg, ct)

                if not resp.IsSuccessStatusCode then
                    let! errorBody = resp.Content.ReadAsStringAsync(ct)
                    let snippet = if errorBody.Length > 200 then errorBody.Substring(0, 200) else errorBody
                    return Error (ModelUnavailable (target, $"HTTP {int resp.StatusCode}: {snippet}"))
                else
                    let! responseJson = resp.Content.ReadAsStringAsync(ct)
                    return Ok responseJson

            with
            | :? HttpRequestException as ex ->
                return Error (ModelUnavailable (target, ex.Message))
            | :? TaskCanceledException as ex when ex.CancellationToken = ct ->
                return Error (InvalidRequest "client cancelled")
            | :? TaskCanceledException ->
                // HttpClient.Timeout fires with an internal token — distinguish from client cancel
                return Error (ModelUnavailable (target, "upstream timeout"))
        }

    /// Streaming POST to upstream /v1/chat/completions.
    /// Uses taskSeq {} to produce an IAsyncEnumerable<Result<string, RouterError>>.
    ///
    /// SSE pitfalls addressed:
    ///   STRM-01 / PITFALL-2: HttpCompletionOption.ResponseHeadersRead — no body buffering.
    ///   STRM-06 / PITFALL-4: use _ = resp immediately after let! — disposal scope covers full read loop.
    ///   PITFALL-14: let! + use _ pattern for Task<IDisposable> in taskSeq {}.
    ///
    /// Yields Ok line for each non-blank SSE event line from the upstream stream.
    /// Yields a single Error _ on probe failure or non-2xx response; sequence terminates.
    /// Never throws — all failure paths yield Error _ (consistent with CompleteAsync contract).
    member _.StreamAsync (req: RouterRequest) (decision: RoutingDecision) (ct: CancellationToken) : IAsyncEnumerable<Result<string, RouterError>> =
        taskSeq {
            let target = decision.Target
            let probe, clientName, upstreamUrl = resolveProbe target true    // stream

            // 1. Resolve upstream model id (lazy probe — fires at most once per process per upstream).
            let! probeResult = probe.Value
            match probeResult with
            | Error e ->
                yield Error e
                // Yield the error and let the sequence terminate naturally.
            | Ok modelId ->

            // 2. Build wire messages.
            let msgs =
                req.Messages
                |> List.map (fun m ->
                    {| role = roleString m.Role; content = m.Content |})
                |> List.toArray

            // 3. Build request body dict — same shape as CompleteAsync but stream=true.
            let bodyDict = Dictionary<string, obj>()
            bodyDict.["model"]            <- modelId
            bodyDict.["messages"]         <- msgs
            bodyDict.["stream"]           <- true  // streaming
            bodyDict.["temperature"]      <- (req.Temperature |> Option.defaultValue 0.7) :> obj
            bodyDict.["top_p"]            <- (req.TopP        |> Option.defaultValue 0.8) :> obj
            bodyDict.["top_k"]            <- 20 :> obj
            bodyDict.["presence_penalty"] <- 0.0 :> obj

            match req.MaxTokens with
            | Some n -> bodyDict.["max_tokens"] <- n :> obj
            | None   -> ()

            // 4. Merge UnknownFields last — client-supplied non-OpenAI fields flow through verbatim (API-04).
            for kv in req.UnknownFields do
                bodyDict.[kv.Key] <- kv.Value :> obj

            let bodyJson = JsonSerializer.Serialize(bodyDict, jsonOptions)

            Log.Debug("StreamAsync POST {Url}/v1/chat/completions (stream=true)", upstreamUrl)

            // 5. Build the HTTP request message. Do NOT cache the client (PITFALL-15).
            let client = httpFactory.CreateClient(clientName)
            use reqMsg = new HttpRequestMessage(HttpMethod.Post, upstreamUrl + "/v1/chat/completions")
            reqMsg.Content <- new StringContent(bodyJson, Encoding.UTF8, "application/json")

            // 6. Open upstream connection with ResponseHeadersRead — no body buffering (STRM-01 / PITFALL-2).
            //    Direct let! form — NO task { return! ... } wrapper (per plan constraint).
            //    use _ = resp immediately after — disposal scope covers entire read loop (STRM-06 / PITFALL-4).
            let! resp = client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct)
            use _ = resp

            if not resp.IsSuccessStatusCode then
                let! errorBody = resp.Content.ReadAsStringAsync(ct)
                let snippet = if errorBody.Length > 200 then errorBody.Substring(0, 200) else errorBody
                yield Error (ModelUnavailable (target, $"HTTP {int resp.StatusCode}: {snippet}"))
            else
                // 7. Read upstream stream line-by-line.
                //    StreamReader default buffer = 4096 bytes — sufficient for SSE events (50–200 bytes each).
                //    ReadLineAsync(ct) propagates cancellation (CancellationToken overload — .NET 7+).
                use stream = resp.Content.ReadAsStream()
                use reader = new System.IO.StreamReader(stream)

                let mutable isDone = false
                while not isDone && not ct.IsCancellationRequested do
                    let! line = reader.ReadLineAsync(ct)
                    if isNull line then
                        isDone <- true   // EOF — upstream stream complete
                    elif line.Length > 0 then
                        yield Ok line    // e.g. "data: {\"id\":\"...\",\"choices\":[...]}"
                        // Blank separator lines between SSE events are intentionally skipped here;
                        // the endpoint re-appends \n\n when writing each yielded chunk to the client.
        }

    interface IUpstreamClient with
        member this.CompleteAsync req decision ct = this.CompleteAsync req decision ct
        member this.StreamAsync   req decision ct = this.StreamAsync   req decision ct
