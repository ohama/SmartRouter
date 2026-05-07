# Pitfalls Research

**Domain:** F# .NET 10 LLM router/gateway — SSE pass-through, priority queue, SemaphoreSlim(1), mlx_lm.server upstream
**Researched:** 2026-05-07
**Confidence:** HIGH (grounded in blueCode operational history on the same Qwen servers + F# .NET patterns)

---

## Critical Pitfalls

### Pitfall 1: mlx_lm.server HF-id Fallback Trap

**What goes wrong:**
The router sends the HuggingFace repo id (e.g., `"Qwen/Qwen2.5-Coder-32B"`) in the POST body's `model` field instead of the local filesystem path. mlx_lm.server interprets an unknown or HF-style id as an instruction to re-resolve via HuggingFace Hub, fetches the Base Coder tokenizer, and **overwrites the loaded Instruct tokenizer** in the running process. All subsequent responses become FIM-mode continuations: raw `<|fim_prefix|>`, `<|fim_suffix|>` tokens, system-prompt echo, imaginary continuation dialogue.

**Why it happens:**
`GET /v1/models` returns an array with multiple id entries per model — e.g. both `"Qwen/Qwen2.5-Coder-32B"` and `"/Users/ohama/llm-system/models/qwen35b"`. A naive `data[0].id` read picks the HF id because it sorts first alphabetically. The router then echoes that id back in the POST body.

**How to avoid:**
Copy `tryParseModelId` verbatim from `blueCode/src/BlueCode.Cli/Adapters/QwenHttpClient.fs`. It prefers the first id that starts with `"/"` (absolute filesystem path), falling back to `data[0].id` only when no path-like id exists. The local path keeps the server pinned to its already-loaded tokenizer.

```fsharp
// Prefer path-like id (starts with '/') over HF repo id
match ids |> List.tryFind (fun s -> s.StartsWith("/")) with
| Some pathId -> Some pathId
| None -> List.tryHead ids
```

The router must probe `GET /v1/models` on first use per port, cache the result (`Lazy<Task<ModelInfo>>`), and send `info.ModelId` as the `model` field in every POST body.

**Warning signs:**
- Upstream response contains `<|fim_prefix|>`, `<|fim_suffix|>`, or `<|im_start|>` raw special tokens
- Response echoes the full system prompt text back
- `finish_reason: length` repeated on short prompts
- Symptoms appear only after a `launchctl kickstart` or server restart (clean boot starts with correct tokenizer; first bad POST poisons it)
- Log `POST body: {"model": "Qwen/..."` (HF id) is the smoking gun

**Phase to address:** Phase 1 (HTTP client adapter bootstrap) — must be solved before any upstream call is made.

---

### Pitfall 2: HttpClient.SendAsync Without ResponseHeadersRead Buffers Entire Body

**What goes wrong:**
When forwarding a streaming response, calling `httpClient.SendAsync(req, ct)` (the default overload) buffers the entire upstream response body before returning. The router sees a stall lasting the full generation time (~30–240s for 122B), then floods all chunks to the downstream client at once. The client experiences a timeout followed by a data burst — not streaming.

**Why it happens:**
The default `HttpCompletionOption` is `HttpCompletionOption.ResponseContentRead`, which instructs the HttpClient to fully buffer the response body into memory before completing the task.

**How to avoid:**
Always pass `HttpCompletionOption.ResponseHeadersRead` for SSE/streaming endpoints:

```fsharp
use! resp = httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
let stream = resp.Content.ReadAsStream()   // or ReadAsStreamAsync
```

This makes the task complete as soon as response headers arrive, giving you access to the body stream incrementally. Non-streaming (JSON) responses can still use `ResponseContentRead` or just call `ReadAsStringAsync` after `ResponseHeadersRead` — both are correct.

**Warning signs:**
- Curl shows no output for 60+ seconds, then the entire response appears simultaneously
- Streaming latency matches non-streaming latency to within noise
- Client-side "first byte" timing equals "last byte" timing

**Phase to address:** Phase 2 (SSE pass-through implementation) — must be the *first* thing verified in streaming integration tests.

---

### Pitfall 3: Forgetting to Flush HttpContext.Response.Body Between SSE Chunks

**What goes wrong:**
The router reads a chunk from the upstream stream and writes it to `HttpContext.Response.Body`, but the bytes sit in ASP.NET Core's response buffer. The downstream client sees nothing until the buffer fills or the connection closes — effectively destroying the streaming experience.

**Why it happens:**
`Stream.Write` or `Stream.WriteAsync` does not guarantee a flush to the network layer. ASP.NET Core's `PipeWriter`-backed response stream has internal buffering. Without an explicit flush, chunks accumulate in memory.

**How to avoid:**
After each chunk write, call `FlushAsync`:

```fsharp
do! context.Response.Body.WriteAsync(buffer, 0, bytesRead, ct)
do! context.Response.Body.FlushAsync(ct)
```

Alternatively, use `HttpContext.Response.BodyWriter.FlushAsync()` if writing via the `PipeWriter` path. Kestrel will flush when the buffer crosses a threshold, but for low-frequency LLM tokens (1–5 tokens/s for 122B) the buffer threshold may never be reached during a long generation.

**Warning signs:**
- Client sees chunks in large bursts rather than token-by-token
- Chunk timing has irregular long gaps followed by multiple tokens at once
- `time curl ... -N` shows long pauses between token groups

**Phase to address:** Phase 2 (SSE pass-through) — write a flush-verification test that asserts first-byte latency < 2s.

---

### Pitfall 4: HttpResponseMessage Disposal Racing With the Response Stream

**What goes wrong:**
The router disposes the `HttpResponseMessage` (via `use` binding or GC) while still reading from its `Content` stream downstream. The stream is torn out from under the read loop, causing `ObjectDisposedException` or silent truncation — the downstream client receives a partial response with no `[DONE]` sentinel.

**Why it happens:**
In F# `task {}`, `use resp = ...` calls `Dispose()` when the CE scope exits. If the pass-through loop runs inside a helper function that returns before fully draining the stream, or if an exception causes early scope exit, the `HttpResponseMessage` is disposed while the caller is still piping bytes.

**How to avoid:**
Keep the `HttpResponseMessage` alive for the entire duration of the stream pipe. The safe pattern:

```fsharp
// WRONG: resp disposed before stream is fully read
let! chunks = getChunks resp  // resp.Dispose() fires here
for chunk in chunks do write chunk

// CORRECT: resp lives until pipe is done
use resp = ... // scope covers the entire loop below
let stream = resp.Content.ReadAsStreamAsync()
// ... read loop inside this scope
```

Never return the `Content` stream or its consumer from a `using`-equivalent block. The `HttpResponseMessage` must outlive all reads of its content stream.

**Warning signs:**
- `ObjectDisposedException: Cannot access a disposed object` in logs, correlated with streaming requests
- Downstream clients receive partial SSE streams (missing `data: [DONE]\n\n`)
- Truncation happens consistently at a specific byte count (buffer boundary)

**Phase to address:** Phase 2 (SSE pass-through) — disposable lifecycle must be reviewed in code review and covered by a cancellation/truncation test.

---

### Pitfall 5: Cancellation Not Propagated — Upstream Keeps Generating After Client Disconnects

**What goes wrong:**
The downstream client disconnects (browser tab closed, Hermes `Ctrl+C`, Graphify timeout). The router's `HttpContext.RequestAborted` token fires, but the upstream `HttpClient` request was started with a different (or no) `CancellationToken`. The upstream mlx_lm.server continues generating for the full session, holding the `SemaphoreSlim` token on 122B and blocking the next queued request for potentially 240+ seconds.

**Why it happens:**
`HttpContext.RequestAborted` is a separate `CancellationToken`. If the router passes `CancellationToken.None` (or a fixed timeout CT) to `httpClient.SendAsync`, disconnect events do not reach the upstream.

**How to avoid:**
Link `HttpContext.RequestAborted` with any router-level timeout token using `CancellationTokenSource.CreateLinkedTokenSource`:

```fsharp
use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
    ctx.RequestAborted, timeoutCts.Token)
let ct = linkedCts.Token
// pass ct to httpClient.SendAsync AND to SemaphoreSlim.WaitAsync
```

This ensures: (a) client disconnect aborts the upstream call, (b) router timeout aborts the upstream call, (c) whichever fires first wins. The `SemaphoreSlim` must also be released in `finally` so it is not held if the upstream call is cancelled mid-stream.

**Warning signs:**
- 122B queue depth climbs monotonically even after clients disconnect
- `/stats` shows active requests outliving their client connections
- `[METAL] Insufficient Memory` crashes appear even with `SemaphoreSlim(1)` because the "released" semaphore was not actually acquired by a live request — old requests held it past disconnect

**Phase to address:** Phase 3 (concurrency + queueing) — link the tokens in the same pass as SemaphoreSlim integration.

---

### Pitfall 6: Content-Type and X-Accel-Buffering Missing on SSE Responses

**What goes wrong:**
The router forwards SSE chunks but omits `Content-Type: text/event-stream` and/or does not disable proxy-level buffering. Downstream clients (OpenAI SDK, Hermes) may not enter streaming parse mode. Intermediate proxies (nginx, Caddy, even Kestrel's internal response caching) may buffer chunks into a single response.

**Why it happens:**
When proxying, the router typically copies response headers from upstream. If the upstream `Content-Type` is not forwarded verbatim, or if the router constructs a new response, these headers must be set explicitly. Kestrel itself does not buffer by default, but the `X-Accel-Buffering: no` header prevents nginx-style intermediaries from doing so if they sit in the path.

**How to avoid:**
At the start of any SSE response, before writing the first byte:

```fsharp
ctx.Response.Headers["Content-Type"] <- "text/event-stream"
ctx.Response.Headers["Cache-Control"] <- "no-cache"
ctx.Response.Headers["X-Accel-Buffering"] <- "no"
ctx.Response.Headers["Connection"] <- "keep-alive"
```

If forwarding the upstream headers wholesale, verify that `Content-Type` is passed through. Do not let `StringContent` or any default content type override it.

**Warning signs:**
- Client receives all chunks at once (buffering occurred somewhere)
- Hermes `stream=True` calls return a fully-assembled string instead of an iterator
- OpenAI SDK `stream=True` calls block until response complete

**Phase to address:** Phase 2 (SSE pass-through) — add header assertions to integration tests.

---

### Pitfall 7: SSE Chunk Splitting — Partial `data: ...\n\n` Events

**What goes wrong:**
The router tries to "parse" or re-frame SSE events: it reads bytes from upstream, splits on newlines, or applies its own buffering logic. This splits valid SSE events across multiple writes. The downstream client's SSE parser receives a fragment like `data: {"id":"chatcmpl-` and hangs waiting for the closing `\n\n`, which arrives in the next write. Some OpenAI SDK versions silently drop partial events; others raise parse errors.

**Why it happens:**
Developers assume they need to understand the SSE format to proxy it. They don't. mlx_lm.server emits complete `data: <json>\n\n` events. The correct approach is to pass the upstream byte stream through verbatim without any reframing.

**How to avoid:**
Use a fixed-size byte buffer and pipe raw bytes from the upstream `HttpContent` stream to `HttpContext.Response.Body`. Do not attempt to detect or split on `\n\n`. The upstream already emits complete events:

```fsharp
let buf = Array.zeroCreate<byte> 4096
let upstreamStream = resp.Content.ReadAsStream()
let mutable keepReading = true
while keepReading do
    let! n = upstreamStream.ReadAsync(buf, 0, buf.Length, ct)
    if n = 0 then keepReading <- false
    else
        do! ctx.Response.Body.WriteAsync(buf, 0, n, ct)
        do! ctx.Response.Body.FlushAsync(ct)
```

This pattern is correct regardless of where TCP segment boundaries fall. The mlx_lm.server guarantees complete events per write; a 4 KB buffer will never split a single event in practice (events are typically 50–200 bytes each).

**Warning signs:**
- Downstream SSE parser emits `EventSourceMessageException` or similar parse errors
- Streaming works on short responses but fails on long ones (where more events are emitted)
- Log shows byte-count mismatches between what upstream sent and what downstream received

**Phase to address:** Phase 2 (SSE pass-through) — do not add any SSE parsing logic; add a test that streams 100+ chunks and verifies each is received intact.

---

### Pitfall 8: SemaphoreSlim.WaitAsync Leak on Cancellation

**What goes wrong:**
A request acquires the `SemaphoreSlim(1)` token, then is cancelled (client disconnect or timeout). The upstream call throws `OperationCanceledException`. If the `Release()` call is not in a `finally` block, the semaphore count stays at 0 forever and all subsequent 122B requests queue indefinitely — the gateway is effectively dead for 122B traffic.

**Why it happens:**
F# `task {}` exception propagation: an `OperationCanceledException` from `WaitAsync` (when `ct` fires before acquiring the semaphore) does NOT require a `Release()` — `WaitAsync` never returned `true`. But if `WaitAsync` returned (i.e., the semaphore was acquired) and then a subsequent operation throws, `Release()` must run regardless.

**How to avoid:**
Use the `try/finally` pattern unconditionally. The F# equivalent:

```fsharp
do! semaphore.WaitAsync(ct)  // may throw if ct fires before acquire
try
    return! doUpstreamCall ct
finally
    semaphore.Release() |> ignore
```

Note: if `WaitAsync` itself throws (ct fires before acquire), we must NOT call `Release()` because we never held the slot. The `try/finally` wrapping must go *after* the `WaitAsync` line to be correct:

```fsharp
let! acquired = task {
    do! semaphore.WaitAsync(ct)  // if this throws, no Release needed
    return true
}
// only reaches here if WaitAsync succeeded
try
    return! doUpstreamCall ct
finally
    semaphore.Release() |> ignore
```

**Warning signs:**
- `/stats` shows 122B queue depth growing but active count staying at 0
- All 122B requests return 503/timeout after the first cancellation event
- `SemaphoreSlim.CurrentCount` = 0 while no upstream calls are in flight (detectable via `/stats`)

**Phase to address:** Phase 3 (concurrency) — test this explicitly: start a request, cancel it mid-flight, verify semaphore CurrentCount returns to 1.

---

### Pitfall 9: Raw SemaphoreSlim.WaitAsync Is FIFO, Not Priority-Ordered

**What goes wrong:**
The priority queue is implemented as a `PriorityQueue<T, int>` but requests still call `SemaphoreSlim.WaitAsync(ct)` directly. `SemaphoreSlim` uses an internal FIFO queue for waiting continuations. High-priority requests that arrive while the semaphore is held join the same FIFO as low-priority requests — priority is ignored.

**Why it happens:**
`SemaphoreSlim` has no concept of priority. It was designed for fair queuing. Developers add a `PriorityQueue` alongside it without realizing that `WaitAsync` itself bypasses the priority structure.

**How to avoid:**
Use the dispatcher loop pattern: a single loop drains the `PriorityQueue`, picks the highest-priority item, and *then* calls `SemaphoreSlim.WaitAsync`. Waiting requests park on a per-request `TaskCompletionSource<unit>`, not on `SemaphoreSlim.WaitAsync` directly.

```fsharp
// Each incoming 122B request:
let tcs = TaskCompletionSource<unit>()
priorityQueue.Enqueue({ Tcs = tcs; Priority = priority; ... })
signal.Release()          // wake the dispatcher
do! tcs.Task              // park here until dispatcher grants slot

// Dispatcher loop (background Task):
while true do
    do! signal.WaitAsync()
    match priorityQueue.TryDequeue() with
    | true, item ->
        do! semaphore.WaitAsync()      // blocks until 122B slot free
        item.Tcs.SetResult()           // unblock the waiting request
    | _ -> ()
```

The dispatcher serializes decisions; individual requests park on their own `TCS.Task`. This gives exact priority semantics.

**Warning signs:**
- Load tests show `graph_indexing` (high priority) waiting behind `reasoning` (low priority) requests that arrived earlier
- Priority ordering only works in unit tests (sequential), fails under concurrent load

**Phase to address:** Phase 3 (concurrency) — write a concurrent test: enqueue 3 low-priority + 1 high-priority request while semaphore is held, verify high-priority runs first when slot opens.

---

### Pitfall 10: Low-Priority Starvation Under Constant High-Priority Load

**What goes wrong:**
Graphify's `graph_indexing` and `compiler_debug` tasks are marked high priority. If a batch job or runaway loop submits a constant stream of high-priority requests, the `dependency_analysis` and `reasoning` tasks (low priority) queue indefinitely. `/stats` shows queue depth growing without bound; low-priority callers timeout.

**Why it happens:**
Pure priority scheduling is work-conserving for high-priority items and starvation-prone for low-priority items under sustained high-priority load. This is correct behavior for a priority queue but incorrect for a production system that must eventually serve all callers.

**How to avoid:**
Implement aging or a max-wait cap. Simplest correct approach: add a `EnqueuedAt` timestamp to each queue entry. In the dispatcher loop, before selecting the highest-priority item, promote any entry that has waited longer than `MaxWaitMs` (e.g., 30 seconds) to the highest priority. This bounds worst-case latency for any request.

```fsharp
let promote (entry: QueueEntry) =
    let age = DateTime.UtcNow - entry.EnqueuedAt
    if age.TotalMilliseconds > maxWaitMs then
        { entry with Priority = HighPriority }
    else entry
```

Alternatively, enforce a maximum high-priority slot budget per second to rate-limit high-priority intake at the source.

**Warning signs:**
- `/stats` shows items with `waitMs > 30000` in the low-priority queue
- Graphify `dependency_analysis` tasks timeout during heavy `graph_indexing` batch runs
- Low-priority item count grows monotonically without being served

**Phase to address:** Phase 3 (concurrency) — add aging strategy in the dispatcher; add a starvation test (constant high-priority load for 10 items, verify low-priority item is served within max-wait threshold).

---

### Pitfall 11: Upstream Hang Without Timeout — Semaphore Never Releases

**What goes wrong:**
mlx_lm.server deadlocks internally (observed during `[METAL] Insufficient Memory` near-miss events: server accepts the connection but never writes response bytes). The router's upstream `HttpClient` call hangs indefinitely. The `SemaphoreSlim` is held. All queued 122B requests wait forever. The health endpoint still returns 200 (the server is up, just stuck on that request).

**Why it happens:**
`HttpClient.Timeout` is set on the `HttpClient` instance but only fires if no bytes have been received. For a streaming response that starts (headers arrive, first chunk arrives) and then stalls mid-stream, the per-instance timeout does not fire — it only covers the initial connection, not streaming progress. A stalled generation after 50 tokens would hang indefinitely.

**How to avoid:**
Pass a per-request `CancellationToken` derived from a `CancellationTokenSource` with a configurable deadline (300s matching blueCode):

```fsharp
use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(300.0))
use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
    ctx.RequestAborted, timeoutCts.Token)
let ct = linkedCts.Token
```

Also set `HttpClient.Timeout = TimeSpan.FromSeconds(330.0)` as a backstop (slightly longer than the per-request CT so the CT fires first and the error is attributable). The per-request CT covers the full streaming duration; `HttpClient.Timeout` covers the initial connection phase.

**Warning signs:**
- `/stats` active count = 1 for longer than `maxTokens / expectedTokensPerSecond` (e.g., 4096 tokens / ~3 tok/s 122B = ~22 min maximum; anything beyond that is a hang)
- Queue depth grows while active count stays at 1
- `122b.err` log shows no new lines for the stuck request

**Phase to address:** Phase 3 (concurrency) — pair timeout CT with semaphore acquisition in the same code path.

---

### Pitfall 12: HttpClient.Timeout Default 100s — Too Short for 122B

**What goes wrong:**
`HttpClient.Timeout` defaults to 100 seconds. The 122B model cold-start after `launchctl kickstart` takes up to 240 seconds. Any request sent during cold-start returns `TaskCanceledException` (timeout disguised as cancellation), mapped to a 503. Callers see failures during what should be a normal operational startup window.

**Why it happens:**
The default is set for typical web service interactions; local LLM inference is orders of magnitude slower.

**How to avoid:**
Set `HttpClient.Timeout` to at least 300 seconds, matching blueCode Phase 20-01:

```fsharp
httpClient.Timeout <- TimeSpan.FromSeconds(300.0)
```

Distinguish timeout vs. user cancellation in error handling: `TaskCanceledException` with a token that is NOT the user's `CancellationToken` is a timeout (blueCode's pattern in `postAsync`).

**Warning signs:**
- Requests to 122B fail with 503 within exactly 100 seconds
- Error logs show `TaskCanceledException` with no user cancellation event
- Only cold-start requests fail; warm requests succeed

**Phase to address:** Phase 1 (HTTP client bootstrap) — set at creation time, never override downward.

---

### Pitfall 13: `async {}` vs `task {}` — Cancellation Semantics Differ

**What goes wrong:**
An F# `async {}` computation is used somewhere in the request path (perhaps copied from a snippet). When a `CancellationToken` is cancelled, F# `async {}` raises `OperationCanceledException` at the *next bind point*, which may be delayed. More critically, `async {}` and `task {}` do not compose cleanly: `Async.AwaitTask` wraps a `Task` in an `Async` but loses the calling `CancellationToken` unless explicitly threaded. A cancellation in the outer `task {}` does not propagate into an `async {}` sub-computation.

**Why it happens:**
Developers mix idioms from examples. blueCode's CI grep (`check-no-async.sh`) catches this in Core but the smart-router needs its own equivalent enforcement.

**How to avoid:**
Use `task {}` exclusively in all router code. Mirror blueCode's CI check:

```bash
# scripts/check-no-async.sh
if grep -rn "async {" src/SmartRouter.Core/; then
  echo "ERROR: async {} found in Core — use task {} only"; exit 1
fi
```

When bridging to libraries that return `Async<'T>`, use `Async.StartAsTask` with explicit `cancellationToken` parameter, not `|> Async.RunSynchronously`.

**Warning signs:**
- Cancellation events do not abort in-flight upstream calls (client disconnects but router keeps running)
- Test with explicit `CancellationToken` shows upstream call not cancelled
- CI `check-no-async.sh` script absent or not enforced

**Phase to address:** Phase 1 (project scaffold) — add the CI grep as part of the initial build script setup.

---

### Pitfall 14: `let!` on `Task<HttpResponseMessage>` Does Not Preserve `using` Semantics

**What goes wrong:**
In F# `task {}`, writing:

```fsharp
let! resp = httpClient.SendAsync(req, ct)
// ... use resp.Content ...
```

does NOT call `resp.Dispose()` automatically. The `HttpResponseMessage` (and its underlying socket connection) is not released when the scope ends. Under streaming load, each open SSE pass-through holds an `HttpResponseMessage` reference; if these are not disposed, connections accumulate, exhausting the connection pool and eventually the socket table.

**Why it happens:**
F# `let!` in `task {}` binds the value without `IDisposable` tracking. `use!` is required for auto-disposal, but `use!` disposes at scope exit — which for a streaming response is *before* the stream is fully read (see Pitfall 4). There is no magic solution: disposal must be explicit and timed correctly.

**How to avoid:**
Use `use!` for `HttpResponseMessage` only when the scope covers the entire stream read. For SSE pass-through, the correct pattern is to keep the `use!` binding active for the full pipe loop:

```fsharp
use! resp = httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
// entire stream loop runs inside this scope
let stream = resp.Content.ReadAsStream()
// ... read loop ...
// resp disposed here, after loop completes
```

**Warning signs:**
- Connection pool exhaustion errors (`SocketException: Too many open files`, `HttpRequestException: An established connection was aborted`)
- `netstat -an | grep 8001 | wc -l` shows connection count growing over time
- Errors appear only under load or after many streaming requests

**Phase to address:** Phase 2 (SSE pass-through) — review every `SendAsync` call site for `use!` vs `let!` correctness.

---

### Pitfall 15: Named vs Typed HttpClient — Wrong Choice Creates Capture Problems

**What goes wrong:**
Using a typed `HttpClient` wrapper (e.g., `type QwenClient(http: HttpClient)`) registered as a typed client in DI creates a new `HttpClient` per DI scope by default — potentially hundreds of instances for a streaming-heavy server. Using a named client avoids this but requires calling `_factory.CreateClient("upstream122b")` on every request, which is the correct pattern.

**Why it happens:**
Typed clients are convenient for simple cases. The DI docs show typed clients as the "modern" approach without always emphasizing the scope lifetime implications.

**How to avoid:**
Use named clients for the upstream Qwen connections. Register once:

```fsharp
services.AddHttpClient("upstream35b", fun c ->
    c.BaseAddress <- Uri("http://127.0.0.1:8000")
    c.Timeout <- TimeSpan.FromSeconds(300.0)) |> ignore
services.AddHttpClient("upstream122b", fun c ->
    c.BaseAddress <- Uri("http://127.0.0.1:8001")
    c.Timeout <- TimeSpan.FromSeconds(300.0)) |> ignore
```

In the handler: `let client = factory.CreateClient("upstream122b")`. The `IHttpClientFactory` manages the connection pool and DNS refresh internally; named clients get the correct pooling behavior.

**Warning signs:**
- `HttpClient` instances are created per-request (observable via metrics or memory profiler)
- DNS changes to upstream are never picked up (client was created once and cached too aggressively)

**Phase to address:** Phase 1 (HTTP client bootstrap).

---

### Pitfall 16: OpenAI Response Field Contract — Missing Fields Break Downstream Clients

**What goes wrong:**
Hermes (OpenAI Python SDK) and Graphify assert the presence of specific fields in non-streaming responses. A router that proxies the upstream response verbatim is safe, but any response *constructed* by the router (error responses, fallback responses, health-check responses) that omits `id`, `object`, `created`, `model`, `choices[0].finish_reason`, or `usage` will cause the OpenAI SDK to raise `AttributeError` or silently return `None` for expected fields.

Required fields for OpenAI-compat non-streaming response:
- `id` (string, e.g., `"chatcmpl-<uuid>"`)
- `object` (string, must be `"chat.completion"`)
- `created` (Unix timestamp integer)
- `model` (string — see Pitfall 17)
- `choices[0].message.role` (`"assistant"`)
- `choices[0].message.content` (string)
- `choices[0].finish_reason` (`"stop"` | `"length"` | `"content_filter"`)
- `usage` (object — see Pitfall 18)

**How to avoid:**
For any router-generated response (fallback, error-wrapped-as-200, etc.), construct a full OpenAI-compat envelope. Never return a bare `{"error": "..."}` to a path that the OpenAI SDK calls — use the proper `{"error": {"message": "...", "type": "...", "code": ...}}` shape for non-200 responses.

**Warning signs:**
- Hermes Python traceback: `AttributeError: 'NoneType' object has no attribute 'message'`
- `choices[0].finish_reason` is `None` in the SDK response object
- Graphify fails JSON deserialization on router-generated responses but passes on upstream-proxied responses

**Phase to address:** Phase 4 (OpenAI-compat wire format) — add a contract test that POSTs to the router and asserts all required fields are present in the response envelope.

---

### Pitfall 17: `model` Echo-Back Policy — Clients Assert `response.model == request.model`

**What goes wrong:**
The router receives `"model": "gpt-4"` (or an alias), routes to `qwen122b`, and proxies the upstream response verbatim. The upstream response contains `"model": "/Users/ohama/llm-system/models/qwen122b"`. The OpenAI Python SDK's `response.model` field returns the upstream value. Hermes or Graphify code that asserts `response.model == request_model` will fail.

**Why it happens:**
mlx_lm.server echoes whatever model id it was given (the local filesystem path). The router's clients sent their own model name/alias. The two values differ.

**How to avoid:**
Choose a policy and document it. Options:
1. **Echo request model** — rewrite `response.model` to match what the client sent. Simple, breaks nothing downstream.
2. **Echo canonical alias** — normalize to `"qwen35b"` or `"qwen122b"` regardless of what the client sent or upstream returned. Consistent; useful for logging.
3. **Pass upstream value through** — most transparent but breaks clients that assert equality.

**Recommendation:** Option 2 (canonical alias). The router owns the model identity layer; clients know they're talking to the router, not directly to the model. Log both the client-requested model and the upstream model id for debugging.

**Warning signs:**
- Python assertion `assert response.model == "gpt-4"` fails in Hermes tests
- Graphify task routing validation fails because it reads `response.model` to confirm routing
- `response.model` in logs shows filesystem paths (`/Users/ohama/...`) instead of clean aliases

**Phase to address:** Phase 4 (OpenAI-compat wire format).

---

### Pitfall 18: Missing or Malformed `usage` Field

**What goes wrong:**
mlx_lm.server may not include a `usage` field in responses, or may include it with `null` values for token counts. The OpenAI Python SDK treats `response.usage` as nullable, but Graphify's logging/cost-tracking code may assume it is always present and non-null. `NoneType` access errors appear in Graphify's post-processing.

**Why it happens:**
mlx_lm.server's `usage` field presence depends on server version and configuration. It is not guaranteed to match the OpenAI spec exactly.

**How to avoid:**
If proxying verbatim, document the absence policy. If constructing router-level responses, always include a `usage` stub:

```json
"usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0}
```

For proxied responses, if `usage` is absent from upstream, inject the stub before forwarding to the client. This prevents downstream `NoneType` errors while acknowledging the count is unknown.

**Warning signs:**
- `AttributeError: 'NoneType' object has no attribute 'prompt_tokens'` in Graphify
- `/stats` shows token count always 0 (expected if upstream omits `usage`)

**Phase to address:** Phase 4 (OpenAI-compat wire format).

---

### Pitfall 19: Missing `[DONE]` Sentinel in Streaming Responses

**What goes wrong:**
The OpenAI streaming protocol requires the final SSE event to be `data: [DONE]\n\n`. Some mlx_lm.server versions or configurations omit it. The OpenAI Python SDK's streaming iterator runs until `[DONE]` is received; if `[DONE]` never arrives, the iterator blocks until the connection drops. Hermes hangs waiting for more tokens even after generation is complete.

**Why it happens:**
The server closes the connection instead of sending `[DONE]`. The SDK treats connection-close without `[DONE]` as an incomplete stream.

**How to avoid:**
In the SSE pass-through loop, monitor the upstream stream for the `[DONE]` sentinel. If the upstream stream closes (EOF) without a `[DONE]` event having been forwarded, inject it:

```fsharp
// After stream loop exits:
if not sentDone then
    let doneEvent = "data: [DONE]\n\n"B
    do! ctx.Response.Body.WriteAsync(doneEvent, 0, doneEvent.Length, ct)
    do! ctx.Response.Body.FlushAsync(ct)
```

This requires tracking whether `[DONE]` was seen in the pass-through loop — a simple boolean flag suffices.

**Warning signs:**
- Hermes hangs after a streaming completion, only releases on connection timeout
- SSE stream ends without `data: [DONE]` (observable via `curl -N`)
- OpenAI SDK `stream` iterator never raises `StopIteration`

**Phase to address:** Phase 2 (SSE pass-through) — add test: stream a short completion, verify the last event received is `data: [DONE]\n\n`.

---

### Pitfall 20: Cold-Start RSS Climb — ~17 GB / ~45 GB — Requests During Load Window

**What goes wrong:**
After `launchctl kickstart` for 122B, the model takes up to 240 seconds to fully load weights into resident memory. During this window, mlx_lm.server accepts connections but returns garbage, times out, or crashes with `[METAL] Insufficient Memory` as the Metal GPU memory allocator fails to satisfy competing demands from partial-load state and incoming inference requests.

**Why it happens:**
mlx_lm.server starts accepting HTTP connections before model weights are fully mapped into memory. Inference requests during the load window race against the loader.

**How to avoid:**
The router's `/health` endpoint should probe upstream liveness (GET `/v1/models` returns 200) before reporting healthy. The health probe should be called before the router enters service:

```fsharp
// Poll until upstream is ready, with timeout
let! ready = probeUntilReady "http://127.0.0.1:8001" (TimeSpan.FromSeconds(300.0)) ct
```

Additionally, the router's retry policy should treat 5xx responses during cold-start as transient and retry with backoff rather than failing immediately. The first successful response confirms the server is past the load window.

**Warning signs:**
- `~/llm-system/services/logs/122b.err` shows `[METAL] Insufficient Memory`
- `ps aux | grep mlx | grep qwen122b` shows RSS < 10 GB (still loading)
- Requests fail with connection reset (not timeout) in the first 60 seconds after kickstart
- `curl -s http://127.0.0.1:8001/v1/models` returns 200 but subsequent POST returns 500

**Phase to address:** Phase 5 (health probing + retry) — the health check must be load-aware, not just connectivity-aware.

---

### Pitfall 21: Port Rebind Race After `launchctl kickstart`

**What goes wrong:**
Issuing `launchctl kickstart -k com.ohama.qwen122b` rapidly (or mid-load) starts the new process before the old process fully releases its socket on port 8001. The new process fails with `Address already in use` and exits immediately. launchd's `KeepAlive` + `ThrottleInterval 30` respawns it after 30 seconds, but the router sees 30+ seconds of complete unavailability with confusing log messages (`Connection refused` rather than a restart message).

**Why it happens:**
TCP socket `TIME_WAIT` state keeps the port occupied briefly after process exit. launchd's `kickstart -k` sends SIGKILL, which skips graceful socket close and extends `TIME_WAIT` duration.

**How to avoid:**
Prefer `launchctl unload + load -w` for clean restarts instead of `kickstart -k`. Document this in the router's operational runbook. The router's health probe should detect this 30-second gap and route traffic to 35B fallback (where task policy permits) rather than queuing indefinitely.

**Warning signs:**
- `122b.err` log shows `OSError: [Errno 48] Address already in use` immediately after kickstart
- Port 8001 shows `TIME_WAIT` in `netstat -an | grep 8001`
- Router health endpoint reports 122B unavailable for exactly 30 seconds after kickstart

**Phase to address:** Phase 5 (health probing) — document the restart procedure; health probe must distinguish "temporarily restarting" from "permanently down."

---

### Pitfall 22: `[METAL] Insufficient Memory` Under Concurrent Generation

**What goes wrong:**
Two simultaneous inference requests reach mlx_lm.server for 122B (from concurrent router instances, during a semaphore-release race, or before the semaphore is implemented). mlx_lm.server crashes or produces garbage — the Metal GPU memory allocator cannot satisfy two concurrent forward-pass allocations at 45 GB RSS. The crash is not recoverable without a full restart.

**Why it happens:**
mlx_lm.server is not designed for concurrent inference. Its architecture assumes a single request at a time. The application-layer `SemaphoreSlim(1)` is the only guard; if it leaks (see Pitfall 8), concurrent requests reach the upstream.

**How to avoid:**
SemaphoreSlim(1) is the primary guard. Additionally:
- Test the semaphore hold under cancellation explicitly (Pitfall 8)
- The router should run as a single process (`SemaphoreSlim` is process-local, which is sufficient for this deployment)
- Do not run multiple router instances on the same host without a shared coordination mechanism

**Warning signs:**
- `~/llm-system/services/logs/122b.err` contains `[METAL] Insufficient Memory`
- RSS for the mlx process spikes then drops suddenly (crash+restart)
- All in-flight 122B requests receive `Connection reset by peer` simultaneously

**Phase to address:** Phase 3 (concurrency) — SemaphoreSlim correctness is the primary mitigation; cancellation safety is the second.

---

### Pitfall 23: launchd `dotnet` Not on PATH — Service Silently Fails to Load

**What goes wrong:**
The smart-router launchd plist uses `dotnet run` or a relative path to the binary. launchd's environment does not inherit the user's shell `PATH`. `dotnet` is not found; the service label loads successfully (launchctl shows it as loaded) but the process immediately exits. `launchctl list | grep smart-router` shows the last exit code as 1 or 127. No error in stderr because the process never started.

**Why it happens:**
launchd agents inherit a minimal `PATH` (`/usr/bin:/bin:/usr/sbin:/sbin`) that does not include `/usr/local/bin`, `~/.dotnet/`, or homebrew paths where `dotnet` lives.

**How to avoid:**
Use the absolute path to the built binary in the plist `ProgramArguments`, not `dotnet run`. Build the binary first (`dotnet publish -c Release`), then reference the output:

```xml
<key>ProgramArguments</key>
<array>
    <string>/Users/ohama/projs/smart-router/publish/SmartRouter</string>
</array>
```

If `dotnet` must be referenced, provide an explicit `PATH` in the plist `EnvironmentVariables`, mirroring the pattern in `com.ohama.qwen122b.plist`:

```xml
<key>EnvironmentVariables</key>
<dict>
    <key>PATH</key>
    <string>/usr/local/share/dotnet:/usr/local/bin:/usr/bin:/bin</string>
</dict>
```

**Warning signs:**
- `launchctl list com.ohama.smart-router` shows `"LastExitStatus" = 32512` (command not found) or `"LastExitStatus" = 1`
- No process visible in `ps aux | grep SmartRouter`
- No log output in the plist-configured stdout/stderr log files

**Phase to address:** Phase 6 (launchd deployment) — test by running `launchctl load -w <plist>` and immediately checking `launchctl list` for a running PID.

---

### Pitfall 24: `localhost` vs `127.0.0.1` vs `0.0.0.0` — Mac Firewall Behaviors

**What goes wrong:**
Binding to `localhost` on macOS resolves to `::1` (IPv6 loopback) in some .NET versions, not `127.0.0.1`. The router starts on `[::1]:4000`, but Hermes connects to `http://127.0.0.1:4000` — connection refused. Or the router binds to `0.0.0.0:4000` and macOS Application Firewall prompts for an incoming connection accept each session.

**Why it happens:**
macOS dual-stack behavior: `localhost` resolves to IPv6 first in `/etc/hosts` on recent macOS versions. .NET's Kestrel defaults to binding on both IPv4 and IPv6 when `localhost` is specified, but if the client explicitly uses `127.0.0.1`, the IPv6 socket does not match.

**How to avoid:**
Bind explicitly to `127.0.0.1` (IPv4 loopback only), matching the upstream mlx_lm.server convention (`--host 127.0.0.1`). In `appsettings.json`:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://127.0.0.1:4000"
      }
    }
  }
}
```

This avoids IPv6 ambiguity and does not trigger macOS Application Firewall prompts (loopback traffic is exempt). `0.0.0.0` is unnecessary and expands the attack surface.

**Warning signs:**
- Hermes connection refused despite router process running
- `netstat -an | grep 4000` shows `:::4000` (IPv6) but not `127.0.0.1:4000` (IPv4)
- macOS firewall dialog appears on router launch
- `curl http://localhost:4000/health` succeeds but `curl http://127.0.0.1:4000/health` fails (or vice versa)

**Phase to address:** Phase 1 (project scaffold + Kestrel configuration) — lock this in the initial `appsettings.json`.

---

### Pitfall 25: `enable_thinking=false` — Missing Flag Breaks JSON Parsing

**What goes wrong:**
The upstream mlx_lm.server is launched without `--chat-template-args '{"enable_thinking": false}'`. Qwen 3.5 emits `<think>...</think>` tokens before the response content. Any code that attempts to parse the response body as JSON (or as an OpenAI completion envelope) fails because the body starts with `<think>` XML, not `{`. For the router, this affects response forwarding if the router does any response body inspection (e.g., logging the model field for echo-back). For clients downstream (Graphify strict-JSON parsing, Hermes tool-use parsing), all responses break.

**Why it happens:**
Qwen 3.5's default mode has thinking enabled. The flag must be passed explicitly at server launch.

**How to avoid:**
The router itself does not control mlx_lm.server's launch flags. Document the requirement in the router's README and health-check logic: if a response body starts with `<think>`, log a `[METAL]`-level warning: "upstream returned thinking tokens — restart mlx_lm.server with --chat-template-args '{\"enable_thinking\": false}'". Optionally strip the `<think>...</think>` block as a defensive measure (though this should not be needed if the servers are configured correctly per their plists).

**Warning signs:**
- Response `content` field starts with `<think>` or contains `</think>`
- Downstream JSON parse failures on otherwise well-formed requests
- `curl http://localhost:8001/v1/chat/completions` returns content with `<think>...</think>` wrapper

**Phase to address:** Phase 1 (upstream health check) — add a smoke-test assertion: send a trivial request to each upstream, assert `choices[0].message.content` does not contain `<think>`.

---

### Pitfall 26: Expecto Test Discovery — `rootTests` Must Be Explicit

**What goes wrong:**
New test modules are added to the `.fsproj` and decorated with `[<Tests>]`. The test runner compiles and runs, but the new tests never execute. `dotnet test` reports 0 failures and the previous test count — silently skipping all new tests. This has burned multiple executors in blueCode (four separate instances across v1.0 + v1.1).

**Why it happens:**
Expecto's `[<Tests>]` auto-discovery relies on reflection over the assembly. In practice, it is unreliable in the blueCode/smart-router configuration. The project uses an explicit `rootTests` list as the authoritative test registration.

**How to avoid:**
Mirror blueCode's pattern exactly: maintain an explicit `rootTests` list in the test entry point. Every new test module must be added to BOTH:
1. The `.fsproj` `<Compile Include="...">` list, BEFORE the entry-point file
2. The `rootTests` list in the entry-point module

```fsharp
// RouterTests.fs (entry point)
let rootTests = [
    SmartRouter.Tests.RoutingTests.tests
    SmartRouter.Tests.SseTests.tests
    SmartRouter.Tests.ConcurrencyTests.tests
    // <- ADD NEW MODULES HERE
]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv (testList "all" rootTests)
```

**Warning signs:**
- New test count equals old test count after adding a module
- `dotnet test --list-tests` does not show new test names
- Tests compile cleanly but `dotnet test` reports 0 new results

**Phase to address:** Phase 1 (project scaffold) — set up the explicit `rootTests` pattern before writing any tests.

---

### Pitfall 27: Expecto + Console.SetOut Races — Parallel Test Execution

**What goes wrong:**
Integration tests that capture `Console.SetOut` or `Console.SetError` run in parallel (Expecto's default). Two tests simultaneously redirect stdout to different `StringWriter` instances; one test reads the other test's output. Test results are non-deterministic, often appearing flaky (passes sometimes, fails sometimes with garbled output).

**Why it happens:**
`Console.SetOut` is a global side effect. Parallel test execution in Expecto means multiple `testList` items run concurrently on the thread pool by default.

**How to avoid:**
Wrap any `testList` that touches `Console.SetOut`/`Console.SetError` with `testSequenced`:

```fsharp
let tests =
    testSequenced <| testList "SSE integration" [
        test "captures streaming output" { ... }
    ]
```

See `blueCode/documentation/howto/handle-expecto-console-redirection.md` for the full pattern. For smart-router tests that use ASP.NET Core `TestServer`, prefer capturing response bodies via the HTTP response rather than redirecting console globals.

**Warning signs:**
- Test results differ between `dotnet test` runs without code changes
- SSE/streaming tests pass in isolation (`dotnet test --filter`) but fail in full suite
- Output from one test appears in another test's captured buffer

**Phase to address:** Phase 1 (test scaffold) — establish the `testSequenced` convention immediately; document it in the test entrypoint file.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Proxy upstream response headers wholesale without filtering | Zero header logic | Forwards `Transfer-Encoding: chunked` conflicts with Kestrel's own chunking; can cause double-chunked encoding errors | Never — filter hop-by-hop headers |
| Single global `HttpClient` (no factory) | Simple code | Cannot reconfigure per-upstream; DNS pinned forever; no pool size control | Only for single-binary smoke test; replace in Phase 1 |
| Inline `SemaphoreSlim` without dispatcher | Simpler than dispatcher loop | Priority is silently FIFO (Pitfall 9) | Never if priority is a requirement |
| Omit `[DONE]` injection for now | Less code | Hermes hangs on stream end (Pitfall 19) | Never — implement in Phase 2 |
| Echo upstream `model` field verbatim | Zero code | Clients asserting `response.model == request.model` break (Pitfall 17) | Never — normalize in Phase 4 |
| `launchd` service with `dotnet run` | Easier development | PATH resolution fails in launchd context (Pitfall 23) | Development only; change to published binary before any daemon deploy |
| Skip aging in priority queue | Simpler dispatcher | Low-priority starvation under load (Pitfall 10) | Acceptable if sustained high-priority load is operationally impossible; add in Phase 3 |

---

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| mlx_lm.server model id | Send `data[0].id` (HF repo id) in POST body | Probe `/v1/models`, prefer path-like id (starts with `/`) |
| mlx_lm.server streaming | Use default `SendAsync` (buffers body) | Pass `HttpCompletionOption.ResponseHeadersRead` |
| Hermes (OpenAI Python SDK) | Omit required envelope fields in error responses | Always return full OpenAI envelope; use `{"error": {"message": ...}}` for non-200 |
| Graphify task field | Treat `task` as a standard OpenAI field (strip on forward) | Preserve unknown fields when proxying; `task` is non-OpenAI extension but must be passed through |
| launchd PATH | Use `dotnet run` or relative paths | Use absolute path to published binary + explicit `EnvironmentVariables.PATH` |
| Kestrel + SSE | Rely on automatic flushing | Explicitly call `FlushAsync` after each chunk write |

---

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Buffering SSE body before forwarding | Client TTFB equals total generation time | `ResponseHeadersRead` + explicit flush | Every streaming request |
| Creating `HttpClient` per request | Connection pool exhaustion, port starvation | `IHttpClientFactory` named clients | Under any meaningful concurrent load |
| SSE chunk parsing/reframing in router | Parse errors on chunk boundaries; extra allocations | Pass raw bytes verbatim from upstream stream | At higher token rates or larger chunk sizes |
| JSON body logging at DEBUG level without guard | Logging 4KB+ JSON bodies for every request at INFO | Gate body logging behind `LogLevel.Debug` (Serilog `levelSwitch`) | Once request volume > 10/min |
| Priority queue lock contention | Dispatcher loop blocks request threads during high throughput | Use `ConcurrentQueue` per priority level + lock-free dequeue | At >10 concurrent 122B requests (unlikely given SemaphoreSlim(1)) |

---

## "Looks Done But Isn't" Checklist

- [ ] **SSE pass-through:** `ResponseHeadersRead` passed to `SendAsync` — verify with a timing test (TTFB < 2s)
- [ ] **SSE pass-through:** `FlushAsync` called after each chunk — verify chunks arrive incrementally in curl
- [ ] **SSE pass-through:** `data: [DONE]\n\n` present as final event — verify with `curl -N`
- [ ] **SSE pass-through:** `HttpResponseMessage` not disposed before stream is fully read — verify no `ObjectDisposedException` under cancellation
- [ ] **Semaphore:** `Release()` in `finally` block — verify `CurrentCount` returns to 1 after cancellation
- [ ] **Semaphore:** priority ordering under concurrency — not just unit tests; need concurrent load test
- [ ] **Model id:** local path sent in POST body (not HF repo id) — verify with debug log showing `POST body: {"model": "/Users/..."}` 
- [ ] **Timeout:** `HttpClient.Timeout` set to 300s — verify cold-start request does not fail at 100s
- [ ] **Cancellation:** `RequestAborted` linked to upstream `CancellationToken` — verify client disconnect aborts upstream call
- [ ] **Routing:** `tryParseModelId` path-preference heuristic copied from blueCode — verify with a mock `/v1/models` returning both HF id and local path
- [ ] **launchd:** service uses absolute binary path — verify with `launchctl list` showing non-zero PID
- [ ] **Test discovery:** explicit `rootTests` list — verify new test module appears in `dotnet test --list-tests`

---

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| HF-id fallback trap fired (Base tokenizer loaded) | MEDIUM | `launchctl kickstart -k com.ohama.qwen122b` to reload Instruct tokenizer; verify with curl smoke test |
| Semaphore leaked (all 122B requests stuck) | LOW | Restart the smart-router process; root-cause via `[METAL]` in logs |
| `[METAL] Insufficient Memory` crash | HIGH | `launchctl kickstart -k com.ohama.qwen122b`; wait 240s for reload; monitor RSS |
| SSE stream truncated (HttpResponseMessage disposed early) | LOW (code fix) | Wrap upstream call with `use!` covering the full pipe loop; redeploy |
| launchd PATH failure (service not running) | LOW | Switch to absolute binary path in plist; `launchctl unload + load -w` |
| Priority starvation (low-prio requests stuck) | LOW | Add aging to dispatcher; restart router (queue is in-memory, requests re-queue from clients) |

---

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| HF-id fallback trap (1) | Phase 1: HTTP client bootstrap | Mock `/v1/models` returns HF id first; verify POST uses path id |
| `ResponseHeadersRead` missing (2) | Phase 2: SSE pass-through | TTFB timing test < 2s |
| Missing flush (3) | Phase 2: SSE pass-through | Chunk-by-chunk delivery test |
| HttpResponseMessage disposal race (4) | Phase 2: SSE pass-through | Mid-stream cancellation test; no ObjectDisposedException |
| Cancellation not propagated (5) | Phase 3: concurrency | Client disconnect test; verify upstream call aborted |
| Missing SSE headers (6) | Phase 2: SSE pass-through | Header assertion in integration test |
| SSE chunk splitting (7) | Phase 2: SSE pass-through | 100-chunk streaming test; all events intact |
| Semaphore leak on cancellation (8) | Phase 3: concurrency | Cancel mid-flight; verify CurrentCount = 1 after |
| FIFO semaphore bypasses priority (9) | Phase 3: concurrency | Concurrent priority test under held semaphore |
| Low-priority starvation (10) | Phase 3: concurrency | Starvation test with aging |
| Upstream hang / semaphore held forever (11) | Phase 3: concurrency | Stall-simulation test; verify timeout fires |
| HttpClient.Timeout 100s default (12) | Phase 1: HTTP client bootstrap | 300s timeout set at client creation |
| `async {}` in Core (13) | Phase 1: project scaffold | CI grep `check-no-async.sh` added to build |
| `let!` vs `use!` disposal (14) | Phase 2: SSE pass-through | Load test; monitor connection count |
| Named vs typed HttpClient (15) | Phase 1: HTTP client bootstrap | DI registration review |
| Missing OpenAI response fields (16) | Phase 4: wire format | Contract test asserting all required fields |
| `model` echo-back policy (17) | Phase 4: wire format | Assert `response.model` equals canonical alias |
| Missing `usage` field (18) | Phase 4: wire format | Assert `usage` present and non-null in all responses |
| Missing `[DONE]` sentinel (19) | Phase 2: SSE pass-through | Verify last event is `data: [DONE]\n\n` |
| Cold-start request during load window (20) | Phase 5: health probing | Health probe waits for upstream ready before serving |
| Port rebind race after kickstart (21) | Phase 5: health probing | Operational runbook; health probe detects 30s gap |
| `[METAL] Insufficient Memory` (22) | Phase 3: concurrency | Semaphore correctness under cancellation |
| launchd PATH / dotnet not found (23) | Phase 6: deployment | `launchctl list` shows running PID after load |
| `localhost` vs `127.0.0.1` binding (24) | Phase 1: project scaffold | Kestrel bound to `127.0.0.1:4000` explicitly |
| `enable_thinking=false` missing (25) | Phase 1: upstream health check | Smoke test: no `<think>` in response content |
| Expecto auto-discovery failure (26) | Phase 1: test scaffold | Explicit `rootTests` list; `dotnet test --list-tests` verification |
| Expecto Console.SetOut races (27) | Phase 1: test scaffold | `testSequenced` wrapper; CI run shows stable count |

---

## Sources

- blueCode `CLAUDE.md` — operational gotchas for the same Qwen 35B/122B servers on the same machine
- blueCode `documentation/howto/debug-local-llm-server-responses.md` — Layer 1/2/3 mlx_lm.server diagnostic protocol
- blueCode `src/BlueCode.Cli/Adapters/QwenHttpClient.fs` — `tryParseModelId` path-preference heuristic; 300s timeout rationale; HF-id fallback trap defense
- blueCode `~/Library/LaunchAgents/com.ohama.qwen122b.plist` — launchd PATH and absolute-binary patterns
- .NET `HttpClient` documentation — `HttpCompletionOption.ResponseHeadersRead`, disposal semantics, `IHttpClientFactory` patterns
- ASP.NET Core `HttpContext.Response` — `FlushAsync`, response body write semantics
- `SemaphoreSlim` documentation — `WaitAsync` FIFO ordering, correct `try/finally Release` pattern
- OpenAI Chat Completions API spec — required response fields, SSE `[DONE]` sentinel, streaming protocol
- F# `task {}` CE documentation — `let!` vs `use!`, cancellation propagation vs `async {}`

---
*Pitfalls research for: F# .NET 10 LLM router/gateway with SSE pass-through, SemaphoreSlim priority queue, mlx_lm.server upstreams*
*Researched: 2026-05-07*
