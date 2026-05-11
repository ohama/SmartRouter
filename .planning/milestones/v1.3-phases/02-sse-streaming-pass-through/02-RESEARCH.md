# Phase 2: SSE Streaming Pass-Through — Research

**Researched:** 2026-05-07
**Domain:** F# .NET 10 SSE streaming pass-through, HttpClient streaming, IAsyncEnumerable, Kestrel response
**Confidence:** HIGH — all patterns grounded in PITFALLS.md, ARCHITECTURE.md, STACK.md, and current codebase

---

## Summary

Phase 2 fills in the `StreamAsync` stub in `QwenUpstreamClient` and replaces the HTTP 501 branch in `ChatCompletions.fs` with a working SSE forward loop. All five atomic pitfalls — `ResponseHeadersRead`, per-chunk `FlushAsync`, `use!` disposable scope, SSE headers, and `[DONE]` sentinel — must ship together in this phase. Splitting any of them to a later phase leaves the streaming path silently broken in production.

The primary recommendation is to implement `StreamAsync` using a `taskSeq {}` block from `FSharp.Control.TaskSeq` (already at version 1.1.1 in the project, highest-confidence choice). The endpoint adapter iterates the `IAsyncEnumerable<Result<string, RouterError>>` with a manual `GetAsyncEnumerator` loop (required for `try/finally` disposal discipline in F# `task {}`). `[DONE]` injection uses Strategy D (state-machine: track whether the last forwarded chunk contained `[DONE]`; inject only if it did not), which is the minimum correct approach.

The tests use a real Kestrel-on-random-port fake upstream (not `TestServer`) because `TestServer`'s in-process pipeline has known quirks with SSE backpressure and `FlushAsync` timing. The `SmartRouter.Tests.fsproj` already has `Microsoft.AspNetCore.Mvc.Testing 10.0.7` which is sufficient for the fake upstream pattern.

**Primary recommendation:** `taskSeq {}` for `StreamAsync`; manual `GetAsyncEnumerator` loop in the endpoint; Strategy D for `[DONE]`; 4 KB read buffer; real Kestrel fake upstream in tests.

---

## Standard Stack

### Core (already in project — do not add packages)

| Library | Version | Purpose | Status |
|---------|---------|---------|--------|
| `FSharp.Control.TaskSeq` | 1.1.1 | `taskSeq {}` CE for `IAsyncEnumerable` | Already in `SmartRouter.Cli.fsproj` |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.7 | Fake upstream Kestrel + WebApplicationFactory | Already in `SmartRouter.Tests.fsproj` |
| `System.Net.Http` (BCL) | .NET 10 | `HttpClient`, `HttpCompletionOption`, `HttpResponseMessage` | BCL |
| ASP.NET Core `HttpContext.Response` | .NET 10 | `Body.WriteAsync`, `Body.FlushAsync`, `ContentType`, response headers | BCL |
| `System.IO.StreamReader` (BCL) | .NET 10 | `ReadLineAsync(ct)` for line-by-line SSE iteration | BCL |

No new packages are needed for Phase 2. All dependencies are already present.

---

## Architecture Patterns

### Pattern 1: `StreamAsync` — `taskSeq {}` implementation

**What:** Implement `QwenUpstreamClient.StreamAsync` by replacing the stub with a `taskSeq {}` block that calls `httpClient.SendAsync` with `HttpCompletionOption.ResponseHeadersRead`, opens the response stream, and iterates line-by-line with `StreamReader.ReadLineAsync`.

**Critical: `use!` scope must cover the entire stream read.** The `HttpResponseMessage` must stay alive until all lines have been yielded. In `taskSeq {}`, `use!` inside the sequence expression disposes when the sequence is disposed by the consumer. This is correct behavior.

**Why `taskSeq {}` over manual `IAsyncEnumerable`:** `FSharp.Control.TaskSeq` 1.1.1 is already in the project. `taskSeq {}` is a computation expression that compiles to a correct `IAsyncEnumerable<'T>` state machine with proper cancellation propagation and `use!` semantics. Writing a manual `IAsyncEnumerable` implementation in F# (as the current stub does) is error-prone and was already marked as a Phase-1 placeholder. The `taskSeq` CE handles disposal, cancellation, and `MoveNextAsync` correctly without boilerplate.

**Why not `Stream.CopyToAsync` directly:** `CopyToAsync` is simpler but makes `[DONE]` injection impossible without a second stream wrapper. The `IAsyncEnumerable<Result<string, RouterError>>` port contract (from `Ports.fs`) already mandates line-level yielding. `CopyToAsync` would require returning a `Stream` through the port boundary, which violates `ARCH-05` (no `HttpResponseMessage` or raw `Stream` through the port). The architecture decision is final.

**Buffer size: 4 KB.** mlx_lm.server emits complete `data: <json>\n\n` events. A typical token chunk is 50–200 bytes. A 4 KB buffer will not split a single SSE event in practice. 8 KB is acceptable. Do not use `ReadLineAsync` with a very small buffer — use the default `StreamReader` which buffers 4096 bytes internally. This avoids per-character allocations.

```fsharp
// QwenUpstreamClient.fs — replace the Phase-1 stub with this
member _.StreamAsync (req: RouterRequest) (target: ModelId) (ct: CancellationToken) : IAsyncEnumerable<Result<string, RouterError>> =
    // taskSeq {} is a computation expression from FSharp.Control.TaskSeq 1.1.1
    // that produces IAsyncEnumerable<'T>. use! inside taskSeq disposes when
    // the consumer calls DisposeAsync on the enumerator (PITFALL-4 solved).
    FSharp.Control.TaskSeq.taskSeq {
        let probe, clientName, upstreamUrl = resolveProbe target

        // 1. Resolve upstream model id (lazy probe — fires at most once per upstream)
        let! probeResult = probe.Value
        match probeResult with
        | Error e ->
            yield Error e
            // Yield the error and stop — consumer sees one Error element
        | Ok modelId ->

        // 2. Build wire body — same logic as CompleteAsync but stream=true
        let msgs =
            req.Messages
            |> List.map (fun m -> {| role = roleString m.Role; content = m.Content |})
            |> List.toArray

        let bodyDict = System.Collections.Generic.Dictionary<string, obj>()
        bodyDict.["model"]            <- modelId
        bodyDict.["messages"]         <- msgs
        bodyDict.["stream"]           <- true   // <— streaming
        bodyDict.["temperature"]      <- (req.Temperature |> Option.defaultValue 0.7) :> obj
        bodyDict.["top_p"]            <- (req.TopP        |> Option.defaultValue 0.8) :> obj
        bodyDict.["top_k"]            <- 20 :> obj
        bodyDict.["presence_penalty"] <- 0.0 :> obj
        match req.MaxTokens with
        | Some n -> bodyDict.["max_tokens"] <- n :> obj
        | None   -> ()
        for kv in req.UnknownFields do
            bodyDict.[kv.Key] <- kv.Value :> obj

        let bodyJson = System.Text.Json.JsonSerializer.Serialize(bodyDict, jsonOptions)
        Log.Debug("StreamAsync POST {Url}/v1/chat/completions body (stream=true)", upstreamUrl)

        // 3. Open upstream HTTP connection — ResponseHeadersRead = no body buffering (PITFALL-2)
        let client = httpFactory.CreateClient(clientName)
        use reqMsg = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Post,
                        upstreamUrl + "/v1/chat/completions")
        reqMsg.Content <- new System.Net.Http.StringContent(
                            bodyJson, System.Text.Encoding.UTF8, "application/json")

        // use! keeps HttpResponseMessage alive for the full sequence (PITFALL-4)
        let! resp =
            task { return! client.SendAsync(reqMsg, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct) }

        use _ = resp   // ensures disposal when taskSeq is disposed

        if not resp.IsSuccessStatusCode then
            let! errorBody = resp.Content.ReadAsStringAsync(ct)
            let snippet = if errorBody.Length > 200 then errorBody[..199] else errorBody
            yield Error (ModelUnavailable (target, $"HTTP {int resp.StatusCode}: {snippet}"))
        else
            // 4. Read line-by-line; yield non-empty lines as Ok chunks
            // StreamReader default buffer = 4096 bytes — sufficient for SSE events
            use stream = resp.Content.ReadAsStream()
            use reader = new System.IO.StreamReader(stream)

            let mutable isDone = false
            while not isDone && not ct.IsCancellationRequested do
                let! line = reader.ReadLineAsync(ct)
                if isNull line then
                    isDone <- true  // EOF — stream complete
                elif line.Length > 0 then
                    yield Ok line   // e.g. "data: {\"id\":\"...\",\"choices\":[...]}"
                    // blank lines between SSE events are intentionally skipped
    }
```

**Important note on `use!` inside `taskSeq {}`:** In `FSharp.Control.TaskSeq` 1.1.1, `use!` inside a `taskSeq {}` block binds a `Task<IDisposable>` and disposes when the taskSeq is disposed (when the consumer calls `DisposeAsync`). However, `resp` from `client.SendAsync(...)` is `Task<HttpResponseMessage>`, not `Task<IDisposable>`. The correct pattern is to bind it with `let!` and then use a `use _ = resp` binding for disposal. Alternatively, bind as:

```fsharp
// Safer pattern for HttpResponseMessage in taskSeq:
let! resp = client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct)
use __ = resp  // <-- disposes resp when taskSeq is GC'd or DisposeAsync is called
```

This is the `use` (not `use!`) form, which in `taskSeq {}` is equivalent to wrapping the rest of the sequence body in a try/finally that calls `resp.Dispose()`. The `HttpResponseMessage` is alive until the consumer disposes the enumerator.

**Error yielding:** On HTTP failure, yield a single `Error` element and let the sequence terminate. Do not throw an exception — exceptions inside `taskSeq {}` terminate the sequence with an `OperationCanceledException` or propagate as `AggregateException` depending on where they occur. Yielding `Error` is the idiomatic `Result`-based approach and matches the port contract.

---

### Pattern 2: Endpoint adapter — consume `IAsyncEnumerable` and write to `HttpContext.Response`

**Replace the Phase-1 501 branch in `ChatCompletions.fs`** with this streaming forward loop.

**Order of operations (critical):** Headers must be set BEFORE the first `WriteAsync` call. Once any byte is written to the response body, headers are sent and cannot be modified. The routing decision (and any 400 errors) must be returned BEFORE entering the streaming branch.

**Cancellation:** Pass `ctx.RequestAborted` directly to `StreamAsync` and as the `ct` for all writes. Do NOT create a linked `CancellationTokenSource` at this layer — that is Phase 3's responsibility (linked with per-request timeout CTS). In Phase 2, `ctx.RequestAborted` is the only cancellation source.

**Manual enumerator loop** is required (instead of `foreach` or `TaskSeq.iter`) because `foreach` in F# `task {}` does not support `try/finally` around the full loop. The manual pattern gives precise control over `DisposeAsync`:

```fsharp
// ChatCompletions.fs — replace the "if req.Stream then 501" branch with:
if req.Stream then
    // 4a. Set SSE headers BEFORE writing any bytes (PITFALL-6)
    ctx.Response.ContentType <- "text/event-stream"
    ctx.Response.Headers["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache"
    ctx.Response.Headers["X-Accel-Buffering"] <- Microsoft.Extensions.Primitives.StringValues "no"
    ctx.Response.Headers["Connection"] <- Microsoft.Extensions.Primitives.StringValues "keep-alive"
    // Note: Transfer-Encoding: chunked is set automatically by Kestrel when
    // Content-Length is absent (which it is for streaming responses). Do NOT set
    // Content-Length manually. Do NOT set Transfer-Encoding manually.

    let ct = ctx.RequestAborted
    let chunks = upstream.StreamAsync req decision.Target ct

    // Manual enumerator loop for try/finally disposal discipline (PITFALL-4)
    let enumerator = chunks.GetAsyncEnumerator(ct)
    let mutable sentDone = false
    try
        let mutable go = true
        while go do
            let! hasNext = enumerator.MoveNextAsync()
            if not hasNext then
                go <- false
            else
                match enumerator.Current with
                | Error e ->
                    // Stream errored — emit an SSE error event and stop.
                    // Do NOT return HTTP 502 here (headers already sent).
                    // Emit a best-effort SSE error event in OpenAI error shape.
                    let errMsg = $"data: {{\"error\":{{\"message\":\"{string e}\",\"type\":\"upstream_error\"}}}}\n\n"
                    let errBytes = System.Text.Encoding.UTF8.GetBytes(errMsg)
                    do! ctx.Response.Body.WriteAsync(errBytes, 0, errBytes.Length, ct)
                    do! ctx.Response.Body.FlushAsync(ct)
                    go <- false
                | Ok chunk ->
                    // Strategy D: track whether this chunk contains [DONE]
                    if chunk.Contains("[DONE]") then sentDone <- true
                    // Write the raw line + \n\n (upstream sent the full SSE event line)
                    // upstream emits: "data: {...}\n\n" or "data: [DONE]\n\n"
                    // StreamReader.ReadLineAsync strips the trailing \n from each line,
                    // so we re-append \n\n to restore complete SSE event framing
                    let eventLine = chunk + "\n\n"
                    let bytes = System.Text.Encoding.UTF8.GetBytes(eventLine)
                    do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ct)
                    do! ctx.Response.Body.FlushAsync(ct)  // PITFALL-3: flush after EVERY chunk

        // Strategy D: inject [DONE] if upstream didn't send it
        if not sentDone then
            let doneBytes = "data: [DONE]\n\n"B
            do! ctx.Response.Body.WriteAsync(doneBytes, 0, doneBytes.Length, ct)
            do! ctx.Response.Body.FlushAsync(ct)

    with
    | :? System.OperationCanceledException ->
        // Client disconnected mid-stream (ctx.RequestAborted fired).
        // Log and exit — no more writes possible.
        Log.Information("StreamAsync: client disconnected mid-stream for {Target}", decision.Target)
    | ex ->
        Log.Error(ex, "StreamAsync: unexpected error writing to response for {Target}", decision.Target)
    finally
        do! enumerator.DisposeAsync()
        // DisposeAsync on the taskSeq enumerator triggers disposal of HttpResponseMessage
        // inside StreamAsync (the `use _ = resp` binding). This ensures the upstream
        // HTTP connection is closed when the client disconnects. (PITFALL-4)
else
    // Non-streaming path (unchanged from Phase 1)
    ...
```

**SSE line format note:** `StreamReader.ReadLineAsync` strips the trailing `\n` from each line. mlx_lm.server sends `data: {...}\n\n` — which `ReadLineAsync` returns as two reads: `"data: {...}"` and `""` (blank line). The blank line is currently skipped in the `StreamAsync` yield loop (`elif line.Length > 0`). This means the endpoint must append `\n\n` to each yielded chunk to restore valid SSE event framing.

**Alternative:** Read from the upstream stream directly with a byte buffer and write bytes verbatim. This avoids the `ReadLineAsync` + re-append pattern entirely. The tradeoff: you lose the ability to detect `[DONE]` without parsing bytes. Given that `[DONE]` detection is required (Strategy D), the `ReadLineAsync` approach is cleaner for Phase 2.

---

### Pattern 3: `[DONE]` sentinel — Strategy D

**Decision: Strategy D** — small boolean flag `sentDone`; set to `true` when a forwarded chunk contains `"[DONE]"`; inject `data: [DONE]\n\n` after the loop if `sentDone` is still `false`.

**Why not the other strategies:**
- Strategy A (trust upstream, no injection): Risk. mlx_lm.server does emit `[DONE]` in current versions, but the architecture decision is defensive. A network hiccup or version change could cause Hermes to hang indefinitely. Ruled out.
- Strategy B (scan every chunk for `[DONE]`): Essentially what Strategy D is, but Strategy D also handles the inject-if-missing. Strategy B as stated only scans without injecting.
- Strategy C (always append `[DONE]` after stream end): Risk of double `[DONE]`. The OpenAI Python SDK handles double `[DONE]` gracefully in practice (stops on first), but it's sloppy and produces protocol-incorrect output. Ruled out.
- Strategy D: Correct, minimal. One boolean flag. Detects via string `Contains("[DONE]")` — sufficient because `[DONE]` only appears inside the data field of the final SSE event. No false positives (no message content contains literal `[DONE]` in valid chat completions).

**mlx_lm.server behavior (HIGH confidence):** mlx_lm.server does emit `data: [DONE]\n\n` as the final SSE event. Confirmed by blueCode operational history and OpenAI-compat conformance docs. The injection logic is defensive overhead for non-mlx_lm upstreams in the future. Implement it for correctness; it adds near-zero cost.

---

### Pattern 4: SSE chunk integrity — "never split data: events"

**Analysis:** The `ReadLineAsync` approach reads one line at a time. mlx_lm.server emits `data: {...}\n\n` as a complete event. `ReadLineAsync` returns `"data: {...}"` and then `""` (blank). The endpoint yields the non-blank line and skips the blank. Then the endpoint appends `"\n\n"` before writing to the client. This reconstructs the complete `data: {...}\n\n` event as a single `WriteAsync` call. The downstream client's SSE parser receives a complete event boundary in every write.

**This is better than a raw byte buffer** for SSE integrity: a 4 KB byte buffer could (in theory) contain partial lines if the upstream writes very large events. `ReadLineAsync` ensures each `yield` is always a complete SSE field line. The downstream never receives a split `data:` prefix.

**Confirm for the planner:** Do not use `Stream.CopyToAsync` for Phase 2. The `IAsyncEnumerable` contract requires line-level yielding for `[DONE]` detection and for `Result<string, RouterError>` error propagation. `CopyToAsync` is simpler but incompatible with the port architecture.

---

### Pattern 5: Cancellation propagation chain

**Phase 2 chain (no linked CTS yet — that's Phase 3):**

```
ctx.RequestAborted  →  StreamAsync(req, target, ct)
                    →  client.SendAsync(reqMsg, ResponseHeadersRead, ct)
                    →  reader.ReadLineAsync(ct)
                    →  IAsyncEnumerable terminates via OperationCanceledException
                    →  enumerator.DisposeAsync() fires in finally block
                    →  resp.Dispose() fires (use _ = resp inside taskSeq)
                    →  upstream HTTP connection closed
```

When `ctx.RequestAborted` fires (client killed curl), the `ct` token passed to `ReadLineAsync(ct)` throws `OperationCanceledException`. This propagates out of the `taskSeq {}` state machine, terminating the async enumerable. The endpoint's `while go` loop exits via the `OperationCanceledException` catch block. The `finally` block calls `enumerator.DisposeAsync()`. This triggers the `use _ = resp` disposal inside `StreamAsync`, which disposes the `HttpResponseMessage` and closes the upstream socket.

**Verify (post-implementation):** After killing `curl -N`, run:
```bash
lsof -p $(pgrep SmartRouter) | grep CLOSE_WAIT
```
No `CLOSE_WAIT` connections to port 8000/8001 should remain after one chunk interval.

**Phase 3 will add:** `CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)`. Phase 2 does not need this because Phase 3 hasn't shipped the per-request timeout CTS yet.

---

### Pattern 6: Content-Type and Kestrel response buffering

**Required headers (set before first write):**

```fsharp
ctx.Response.ContentType <- "text/event-stream"
ctx.Response.Headers["Cache-Control"] <- StringValues "no-cache"
ctx.Response.Headers["X-Accel-Buffering"] <- StringValues "no"
ctx.Response.Headers["Connection"] <- StringValues "keep-alive"
```

**Notes:**
- `Transfer-Encoding: chunked` is set automatically by Kestrel when `Content-Length` is not set. Do NOT set `Content-Length` and do NOT set `Transfer-Encoding` manually. Kestrel handles this correctly for streaming responses.
- `X-Accel-Buffering: no` is defensive — this router runs directly on Kestrel (no nginx). The header is harmless and prevents issues if nginx is ever added as a reverse proxy without updating config.
- There is no response compression middleware in the current `Program.fs`. Response compression would buffer SSE — if it were ever added, it would need to exclude `text/event-stream`. Not an issue for Phase 2.
- Kestrel does NOT buffer by default. The `FlushAsync` call after each chunk is still required because ASP.NET Core's `PipeWriter`-backed response stream has an internal buffer that may not flush until full (especially for low-frequency 122B tokens at 1–3 tok/s).
- Verify with: `curl -i -N -X POST http://127.0.0.1:4000/v1/chat/completions -H 'Content-Type: application/json' -d '{"messages":[...],"stream":true}'`
  Expected headers in response: `Content-Type: text/event-stream`, `Cache-Control: no-cache`, `Transfer-Encoding: chunked` (Kestrel auto), no `Content-Length`.

---

### Pattern 7: StreamingTests.fs — fake upstream architecture

**Use real Kestrel on a random port** (not `TestServer` for the fake upstream). `TestServer` uses an in-process pipeline that does not expose a real socket — the SSE backpressure and `FlushAsync` behavior differs from a real TCP connection. TTFB timing tests require real TCP latency simulation.

**Why the test project already has the right package:** `Microsoft.AspNetCore.Mvc.Testing 10.0.7` is already in `SmartRouter.Tests.fsproj`. `WebApplication.CreateBuilder()` is available in tests without additional packages.

**Fake upstream skeleton:**

```fsharp
// Inside StreamingTests.fs test setup
let startFakeUpstream (chunkCount: int) (delayMs: int) (emitDone: bool) : Task<WebApplication * int> =
    task {
        let fakeBuilder = WebApplication.CreateBuilder()
        fakeBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore  // OS assigns port
        fakeBuilder.Services.AddRouting() |> ignore
        let fakeApp = fakeBuilder.Build()

        fakeApp.MapPost("/v1/chat/completions", Func<HttpContext, Task>(fun ctx ->
            task {
                ctx.Response.ContentType <- "text/event-stream"
                ctx.Response.Headers["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache"

                for i in 0 .. chunkCount - 1 do
                    if delayMs > 0 then
                        do! Task.Delay(delayMs, ctx.RequestAborted)
                    // Emit a data chunk: SSE event with chunk index
                    let chunk = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{i}\"}}}}]}}\n\n"
                    let bytes = System.Text.Encoding.UTF8.GetBytes(chunk)
                    do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted)
                    do! ctx.Response.Body.FlushAsync(ctx.RequestAborted)

                if emitDone then
                    let doneBytes = "data: [DONE]\n\n"B
                    do! ctx.Response.Body.WriteAsync(doneBytes, 0, doneBytes.Length, ctx.RequestAborted)
                    do! ctx.Response.Body.FlushAsync(ctx.RequestAborted)
            })) |> ignore

        // Also map /v1/models for the probe
        fakeApp.MapGet("/v1/models", Func<IResult>(fun () ->
            Results.Json({| data = [| {| id = "/fake/model" |} |] |}))) |> ignore

        do! fakeApp.StartAsync()

        let addresses = fakeApp.Services
                            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                            .Features
                            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
                            .Addresses
        let port = addresses |> Seq.head |> fun a -> a.Split(':') |> Array.last |> int

        return fakeApp, port
    }
```

**Wiring the router under test to point at fake upstream:**

The router reads upstream URLs from `IConfiguration` (bound into `UpstreamOptions`). Use in-memory configuration override in the test factory:

```fsharp
// Create smart router WebApplication pointing at fake upstream
let buildTestRouter (fakePort35b: int) (fakePort122b: int) : WebApplication =
    let testBuilder = WebApplication.CreateBuilder()
    testBuilder.Configuration.AddInMemoryCollection([
        KeyValuePair("Upstreams:Model35B",  $"http://127.0.0.1:{fakePort35b}")
        KeyValuePair("Upstreams:Model122B", $"http://127.0.0.1:{fakePort122b}")
    ]) |> ignore
    // ... configure services same as production ...
    testBuilder.Build()
```

**How to read SSE from the test client side:**

```fsharp
// Use HttpClient.GetStreamAsync + StreamReader.ReadLineAsync
let readSseChunks (response: HttpResponseMessage) (ct: CancellationToken) : Task<string list> =
    task {
        use stream = response.Content.ReadAsStream()
        use reader = new System.IO.StreamReader(stream)
        let chunks = System.Collections.Generic.List<string>()
        let mutable isDone = false
        while not isDone do
            let! line = reader.ReadLineAsync(ct)
            if isNull line then isDone <- true
            elif line.StartsWith("data: ") then
                chunks.Add(line)
                if line = "data: [DONE]" then isDone <- true
        return List.ofSeq chunks
    }
```

**Test list (maps to requirements STRM-01 through STRM-07, TEST-03):**

1. **TTFB timing** (STRM-01): Fake upstream delays 100ms between chunks; assert first byte arrives within 500ms (well under 2s TTFB threshold). Verify by timing `ReadLineAsync` returning the first chunk.

2. **Chunk ordering** (STRM-02): 10 chunks `"data: 0"` through `"data: 9"`; assert received in order with no reordering.

3. **100-chunk integrity** (STRM-03): 100 chunks; assert all received, no duplicates, no missing, byte-equal to upstream emissions.

4. **Mid-stream cancellation** (STRM-05): Client cancels after 5 chunks via `CancellationTokenSource`. Assert: (a) no `ObjectDisposedException` in logs; (b) fake upstream's `ctx.RequestAborted` fires within one chunk interval (500ms after cancel). Hook `ctx.RequestAborted.Register` in fake upstream to set a flag; assert flag is set.

5. **`[DONE]` sentinel forwarded** (STRM-06): Fake upstream emits `[DONE]`; assert client receives exactly one `data: [DONE]` and it is the final event.

6. **`[DONE]` sentinel injected** (STRM-07): Fake upstream does NOT emit `[DONE]`; assert client still receives exactly one `data: [DONE]` as the final event.

7. **Header assertions** (STRM-04): Assert response `Content-Type: text/event-stream`, `Cache-Control: no-cache`, no `Content-Length` header.

8. **stream=false + stream=true routing** (TEST-03): The 400 error for unknown task fires correctly even when `stream=true`; streaming branch only taken after routing succeeds.

**`testSequenced` wrapper:** Wrap the entire `StreamingTests` test list in `testSequenced`. The tests spin up Kestrel servers on random ports — they must not run in parallel due to port contention and potential resource exhaustion under concurrent load.

---

## Five Atomic Pitfalls — Prevention Code

These must ALL be addressed in Phase 2. Shipping any one without the others leaves streaming broken.

### Pitfall A: Missing `HttpCompletionOption.ResponseHeadersRead`
**Prevention:** The `SendAsync` call in `StreamAsync` MUST use the two-argument overload:
```fsharp
client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct)
```
Never use the single-argument overload `client.SendAsync(reqMsg, ct)` for streaming — it defaults to `ResponseContentRead` which buffers the entire body.

### Pitfall B: Missing `FlushAsync` after each chunk
**Prevention:** After every `ctx.Response.Body.WriteAsync(...)` call in the endpoint loop:
```fsharp
do! ctx.Response.Body.FlushAsync(ct)
```
Without this, Kestrel's `PipeWriter` may buffer multiple chunks before flushing to the TCP socket. For 122B at 1–3 tok/s, the buffer threshold may never be reached naturally.

### Pitfall C: `HttpResponseMessage` early disposal (PITFALL-4)
**Prevention:** The `use _ = resp` binding inside `taskSeq {}` in `StreamAsync` keeps `resp` alive until `enumerator.DisposeAsync()` is called. The endpoint's `finally` block calls `enumerator.DisposeAsync()`. This guarantees `resp` outlives all reads of `resp.Content.ReadAsStream()`.
**Verification:** The mid-stream cancellation test must pass without `ObjectDisposedException` in logs.

### Pitfall D: SSE headers not set before first write (PITFALL-6)
**Prevention:** The four header assignments (`ContentType`, `Cache-Control`, `X-Accel-Buffering`, `Connection`) must appear BEFORE `enumerator.MoveNextAsync()` is first called. In the code pattern above, they are set immediately upon entering the `if req.Stream then` branch, before the enumerator is created. This is correct.

### Pitfall E: `[DONE]` not forwarded / injected (PITFALL-19)
**Prevention:** Strategy D as described above. The `sentDone` boolean is checked and injection fired after the `while go` loop exits normally (not on `OperationCanceledException`). If the client cancels mid-stream, `[DONE]` is NOT injected (the connection is already gone; there is nobody to write to).

---

## `let!` vs `use!` — F# Disposal Discipline (PITFALL-14)

**The issue:** In F# `task {}`, `let! resp = httpClient.SendAsync(...)` binds `resp` without `IDisposable` tracking. `resp.Dispose()` is NEVER called automatically. In `taskSeq {}`, the semantics differ: `use!` inside a computation expression disposes when the CE exits.

**For `taskSeq {}` (StreamAsync):** Use `let! resp = ...` followed immediately by `use _ = resp`. This is the correct two-step pattern for `IDisposable` values in `taskSeq {}` that are also `Task<_>` returns.

**For `task {}` (endpoint handler):** The endpoint does NOT hold `HttpResponseMessage` directly — it holds the `IAsyncEnumerable` enumerator. The `finally` block calls `enumerator.DisposeAsync()`, which chains through to `resp.Dispose()` inside `taskSeq {}`. The endpoint never directly references `HttpResponseMessage`. This is the correct hexagonal isolation.

---

## Per-Chunk Log Noise — Logging Discipline

**Rule (OBS-04):** No log calls inside the streaming chunk loop. Log lines interleaved between SSE data events on stderr are acceptable (Serilog goes to stderr, SSE goes to HTTP response body — they are separate streams). However, per-chunk logging creates ~100+ log lines per request which pollutes structured logs and obscures the routing decision lines.

**Correct placement:**
- Log the routing decision BEFORE entering the streaming branch (already done in Phase 1 handler).
- Log ONCE after streaming completes (or on error) — e.g., `Log.Information("StreamAsync complete: {ChunkCount} chunks, sentDone={SentDone}", chunkCount, sentDone)`.
- No logging inside `while go` loop.

---

## stream=true Error Path — Order of Operations

**Critical:** The 400 error for unknown task or invalid request fires BEFORE the streaming branch is taken. The endpoint handler flow is:

1. Parse wire body → `RouterRequest`
2. `routeRequest routingConfig req` → if `Error`, return HTTP 400/503 immediately (no streaming)
3. `if req.Stream then` → only reached if routing succeeded with `Ok decision`

This means: a `stream=true` request with `task=foobar` returns HTTP 400 (JSON error body, no SSE). This is correct. The HTTP 400 is returned normally because no streaming headers have been set yet.

Once the streaming headers are set and the first byte is written, the response status is committed (cannot be changed). Any upstream error after that point is communicated via an SSE error event, not via HTTP status code.

---

## Open Questions

### Q1: `ReadLineAsync` vs byte buffer for upstream stream

**What we know:** `ReadLineAsync` simplifies `[DONE]` detection and yields complete SSE field lines. A raw byte buffer is simpler and has lower allocation overhead.

**What's unclear:** Whether `ReadLineAsync` on `resp.Content.ReadAsStream()` handles very large SSE events correctly (>4096 bytes per event). mlx_lm.server events are 50–200 bytes each; this is not a practical concern.

**Recommendation:** Use `ReadLineAsync` for Phase 2. Migrate to byte buffer if profiling shows allocation pressure (unlikely).

### Q2: `ReadLineAsync(CancellationToken)` availability

**What we know:** `TextReader.ReadLineAsync(CancellationToken)` was added in .NET 7. The project targets `net10.0`. It is available.

**No action required.** Confirmed safe to use.

### Q3: mlx_lm.server `[DONE]` behavior on partial streaming

**What we know:** mlx_lm.server emits `data: [DONE]\n\n` at the end of OpenAI-compat streaming. Confirmed by blueCode operational history and the `qwen35-122b-openai-compat.md` eval doc.

**What's unclear:** Whether mlx_lm.server emits `[DONE]` when the upstream call is cancelled mid-stream (by router CT firing). In that case, `ReadLineAsync` throws `OperationCanceledException` and the loop exits without seeing `[DONE]`. The endpoint correctly skips `[DONE]` injection in the `OperationCanceledException` catch block (client is gone; nothing to write to). **No action required.**

### Q4: `testSequenced` scope in StreamingTests

**Recommendation:** Wrap the entire `testList "streaming"` in `testSequenced`. Individual test isolation (spin up / tear down fake Kestrel) is more important than parallel execution speed for streaming tests. This also prevents port assignment races between parallel test runs.

---

## Sources

### Primary (HIGH confidence — codebase)

- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — current stub + probe + `CompleteAsync` pattern
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — 501 branch to replace; existing handler structure
- `/Users/ohama/projs/smart-router/src/SmartRouter.Core/Ports.fs` — `IUpstreamClient.StreamAsync` signature
- `/Users/ohama/projs/smart-router/.planning/research/PITFALLS.md` — all 27 pitfalls; Phase 2 cluster: PITFALL-2, 3, 4, 6, 7, 14, 19
- `/Users/ohama/projs/smart-router/.planning/research/ARCHITECTURE.md` — SSE seam decision; `IAsyncEnumerable` port contract; endpoint adapter skeleton
- `/Users/ohama/projs/smart-router/.planning/research/STACK.md` — `FSharp.Control.TaskSeq 1.1.1` confirmed current; `Microsoft.AspNetCore.Mvc.Testing 10.0.7` confirmed

### Primary (HIGH confidence — live package state)

- `dotnet package search FSharp.Control.TaskSeq --take 1` → confirmed 1.1.1 is current version
- `SmartRouter.Cli.fsproj` — `FSharp.Control.TaskSeq 1.1.1` already installed
- `SmartRouter.Tests.fsproj` — `Microsoft.AspNetCore.Mvc.Testing 10.0.7` already installed

### Secondary (MEDIUM confidence — documentation)

- `FSharp.Control.TaskSeq` README / source — `taskSeq {}` CE, `use!` and `use` disposal semantics
- ASP.NET Core documentation — `HttpContext.Response.Body.FlushAsync`, Kestrel chunked transfer behavior
- .NET `TextReader.ReadLineAsync(CancellationToken)` — .NET 7+ overload availability

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all packages already in project; versions verified live
- Architecture: HIGH — port contract is locked; `StreamAsync` signature from `Ports.fs` is authoritative
- Pitfalls: HIGH — grounded in PITFALLS.md (operational history) and current codebase state
- Test pattern: HIGH — `Microsoft.AspNetCore.Mvc.Testing` already in project; WebApplication fake upstream is standard .NET pattern

**Research date:** 2026-05-07
**Valid until:** 2026-06-07 (stable stack; no fast-moving dependencies)
