---
phase: 02-sse-streaming-pass-through
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
  - src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
autonomous: true

must_haves:
  truths:
    - "A `curl -N` streaming request to `POST /v1/chat/completions` with `\"stream\": true` delivers the first byte (response headers + first SSE event) within ~2s of upstream's first chunk emission."
    - "Each upstream SSE event arrives at the downstream client as a complete `data: ...\\n\\n` event in a single write — no `data:` prefix is split across two write boundaries."
    - "The response carries `Content-Type: text/event-stream`, `Cache-Control: no-cache`, `X-Accel-Buffering: no`, `Connection: keep-alive`; Kestrel auto-applies `Transfer-Encoding: chunked` (no `Content-Length` header)."
    - "When upstream emits `data: [DONE]\\n\\n`, the downstream sees exactly that as the final event; when upstream omits `[DONE]`, the router injects `data: [DONE]\\n\\n` after the final upstream chunk."
    - "When the downstream client disconnects mid-stream, `ctx.RequestAborted` fires, the taskSeq enumerator's `DisposeAsync` runs in the endpoint's `finally`, the `HttpResponseMessage` inside `StreamAsync` is disposed via `use _ = resp`, and the upstream HTTP socket closes within one chunk interval."
    - "`stream=true` requests with unknown task or invalid body still return HTTP 400 (JSON error) BEFORE any SSE headers or body bytes are written — the streaming branch is only entered after a successful routing `Ok decision`."
    - "No log calls fire inside the per-chunk write loop; routing decision logs once before the loop, completion/cancellation logs once after."
  artifacts:
    - path: "src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs"
      provides: "Real StreamAsync implementation using FSharp.Control.TaskSeq.taskSeq, HttpCompletionOption.ResponseHeadersRead, use _ = resp for disposal scope, line-level ReadLineAsync yielding."
      contains: "HttpCompletionOption.ResponseHeadersRead"
    - path: "src/SmartRouter.Cli/Endpoints/ChatCompletions.fs"
      provides: "stream=true branch that sets SSE headers before first write, drives the taskSeq enumerator with a try/finally DisposeAsync loop, calls FlushAsync after every WriteAsync, tracks sentDone for [DONE] injection (Strategy D)."
      contains: "text/event-stream"
  key_links:
    - from: "ChatCompletions.handler (stream=true branch)"
      to: "IUpstreamClient.StreamAsync"
      via: "upstream.StreamAsync req decision.Target ctx.RequestAborted"
      pattern: "upstream\\.StreamAsync"
    - from: "QwenUpstreamClient.StreamAsync"
      to: "Qwen upstream over HTTP with ResponseHeadersRead"
      via: "client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct)"
      pattern: "HttpCompletionOption\\.ResponseHeadersRead"
    - from: "ChatCompletions endpoint chunk write"
      to: "downstream client TCP socket"
      via: "ctx.Response.Body.WriteAsync followed by ctx.Response.Body.FlushAsync after EVERY chunk"
      pattern: "FlushAsync"
    - from: "ChatCompletions endpoint finally block"
      to: "Disposal of HttpResponseMessage held by taskSeq"
      via: "enumerator.DisposeAsync() chains through to `use _ = resp` in StreamAsync"
      pattern: "DisposeAsync"
---

<objective>
Implement the SSE streaming pass-through path end-to-end as a single atomic correctness unit.

Purpose: Hermes defaults to `stream=true` on every chat completion call and cannot be used against the router until streaming works. All five SSE pitfalls (ResponseHeadersRead, FlushAsync per chunk, HttpResponseMessage disposal scope, SSE headers before first write, [DONE] sentinel) must ship together — splitting them leaves a silently broken streaming path in production.

Output:
- `QwenUpstreamClient.StreamAsync` becomes a real `taskSeq {}` implementation that opens a streaming HTTP connection (ResponseHeadersRead), holds the response message alive via `use _ = resp`, and yields one SSE event line at a time as `Ok line` (or a single `Error _` element on failure).
- `ChatCompletions.fs` replaces the HTTP 501 stub for `stream=true` with: routing first → SSE headers BEFORE first byte → manual `GetAsyncEnumerator` loop with `try/finally enumerator.DisposeAsync()` → per-chunk `WriteAsync` + `FlushAsync` → Strategy D `[DONE]` injection → graceful handling of `OperationCanceledException` on client disconnect.
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
@.planning/research/PITFALLS.md
@.planning/research/ARCHITECTURE.md
@.planning/phases/02-sse-streaming-pass-through/02-RESEARCH.md
@.planning/phases/01-foundation/01-VERIFICATION.md

# Source files this plan modifies (read first to see current shape)
@src/SmartRouter.Core/Ports.fs
@src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
@src/SmartRouter.Cli/Endpoints/ChatCompletions.fs
@src/SmartRouter.Cli/SmartRouter.Cli.fsproj
</context>

<tasks>

<task type="auto">
  <name>Task 1: Implement QwenUpstreamClient.StreamAsync with taskSeq, ResponseHeadersRead, and disposal scope</name>
  <files>src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs</files>
  <action>
Replace the Phase-1 `StreamAsync` stub (currently returns an empty IAsyncEnumerable) with a real implementation built on `FSharp.Control.TaskSeq.taskSeq {}` (package already pinned at 1.1.1 in `SmartRouter.Cli.fsproj`).

Required structure (mirror `CompleteAsync`'s probe-then-build-then-POST shape):

1. `open FSharp.Control` at the top of the file (alongside existing opens) so `taskSeq {}` is in scope.

2. Replace the `member _.StreamAsync (_req: RouterRequest) (_target: ModelId) (_ct: CancellationToken) : IAsyncEnumerable<Result<string, RouterError>>` body with:
   - Resolve `probe, clientName, upstreamUrl` via `resolveProbe target` (same helper `CompleteAsync` uses).
   - Return `taskSeq { ... }`. Inside the taskSeq:
     a. `let! probeResult = probe.Value`. On `Error e`: `yield Error e` and let the sequence terminate (do NOT throw).
     b. On `Ok modelId`: build the wire body dictionary identically to `CompleteAsync` BUT set `bodyDict.["stream"] <- true`.
     c. Merge `req.UnknownFields` last (verbatim, matching `CompleteAsync`).
     d. Serialize with `JsonSerializer.Serialize(bodyDict, jsonOptions)`.
     e. Log once at Debug level with `Log.Debug("StreamAsync POST {Url}/v1/chat/completions (stream=true)", upstreamUrl)`. NO logs inside the read loop.
     f. Construct `HttpRequestMessage` (POST to `upstreamUrl + "/v1/chat/completions"`) with `StringContent(bodyJson, Encoding.UTF8, "application/json")`.
     g. **PITFALL-2 / STRM-01**: Use the direct `let!` form inside `taskSeq {}` — NO outer `task { return! ... }` wrapper:
        ```fsharp
        let! resp = client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct)
        use _ = resp
        ```
        The `HttpCompletionOption.ResponseHeadersRead` argument is mandatory — the default `ResponseContentRead` buffers the entire body and breaks streaming. The bare two-argument overload `client.SendAsync(reqMsg, ct)` is FORBIDDEN here. Also FORBIDDEN: wrapping the call in `task { return! client.SendAsync(...) }` — `taskSeq {}` lets `let!` bind a `Task<HttpResponseMessage>` directly; the outer `task {}` adds noise and risks invented variants. (02-RESEARCH.md Pattern 1 shows the wrapper, but its own "Important note" block immediately below recommends the direct form — follow the direct form.)
     h. **PITFALL-4 / STRM-06**: The `use _ = resp` binding (shown above) MUST appear immediately after the `let! resp = ...` line. This binds the `HttpResponseMessage` for disposal when the consumer disposes the enumerator (which happens via the endpoint's `finally enumerator.DisposeAsync()`). Without this, the response can be GC'd while the read loop is still iterating, causing `ObjectDisposedException` mid-stream.
     i. If `not resp.IsSuccessStatusCode`: read up to ~200 bytes of error body via `resp.Content.ReadAsStringAsync(ct)`, `yield Error (ModelUnavailable (target, $"HTTP {int resp.StatusCode}: {snippet}"))`, end the sequence.
     j. Otherwise read line-by-line:
        - `use stream = resp.Content.ReadAsStream()`
        - `use reader = new System.IO.StreamReader(stream)` (default 4096-byte buffer is fine; mlx_lm SSE events are 50–200 bytes).
        - Loop with `let mutable isDone = false in while not isDone && not ct.IsCancellationRequested do let! line = reader.ReadLineAsync(ct)`. If `isNull line` set `isDone <- true` (EOF). If `line.Length > 0`, `yield Ok line`. Skip blank separator lines (these reappear as `\n\n` in the endpoint's re-frame step).

Reference Pattern 1 in `02-RESEARCH.md` for the F# code skeleton. Match it with one explicit deviation: at the `SendAsync` call, use the direct `let! resp = client.SendAsync(...)` form (per Pattern 1's own "Important note on `use!` inside `taskSeq {}`" sub-section), NOT the `task { return! ... }` wrapper shown in the leading code block. Do not invent any other variations.

DO NOT:
- Use `Stream.CopyToAsync` (violates `IAsyncEnumerable<Result<string, RouterError>>` port contract; precludes [DONE] detection — see Pattern 4 of 02-RESEARCH.md).
- Use `client.SendAsync(reqMsg, ct)` (defaults to `ResponseContentRead` — buffers full body).
- `throw` exceptions inside the taskSeq body. Yield `Error _` on failure paths instead — the port contract is `Result<string, RouterError>` (consistent with `CompleteAsync`'s contract).
- Add log calls inside the `while` loop. Per-chunk logging pollutes structured logs and obscures routing decisions (OBS-04).
- Cache `HttpClient` in a field — call `httpFactory.CreateClient(clientName)` per request (PITFALL-15, same as `CompleteAsync`).

Update the `interface IUpstreamClient with` block at the bottom: it already calls `this.StreamAsync` — leave it untouched. Confirm the member's signature matches `Ports.fs`: `req -> target -> ct -> IAsyncEnumerable<Result<string, RouterError>>`.
  </action>
  <verify>
Build clean: `dotnet build /Users/ohama/projs/smart-router/SmartRouter.slnx` returns exit 0 with zero warnings (project has `TreatWarningsAsErrors=true`).

Grep verifications (run from repo root):
1. `grep -n "HttpCompletionOption.ResponseHeadersRead" src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — must return at least one match (STRM-01 / PITFALL-2 prevention).
2. `grep -n "use _ = resp" src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — must return at least one match (STRM-06 / PITFALL-4 prevention).
3. `grep -n "taskSeq" src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — must return at least one match (the new implementation uses the CE).
4. `grep -nE 'client\.SendAsync\(reqMsg, *ct\)' src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` — MUST return zero matches inside the StreamAsync body. The CompleteAsync call uses the two-arg overload; that is fine. (Visual review the StreamAsync block to confirm.)
5. `bash scripts/check-no-async.sh` — exit 0 (Core purity preserved; this plan only changes Cli).

Run existing tests still pass: `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — the 22 Phase-1 tests should still pass (no test changes in this task).
  </verify>
  <done>
- `StreamAsync` body uses `FSharp.Control.TaskSeq.taskSeq { ... }` (no more empty-IAsyncEnumerable stub).
- The `SendAsync` call inside StreamAsync passes `HttpCompletionOption.ResponseHeadersRead` and the `ct` parameter.
- A `use _ = resp` line follows the `let! resp = ...` binding; `resp` is disposed when the taskSeq enumerator's `DisposeAsync` is called.
- The implementation yields `Error` on probe failure / non-2xx upstream / etc., and `Ok line` for each non-blank SSE event line read via `ReadLineAsync(ct)`.
- Build is clean (zero warnings under `TreatWarningsAsErrors=true`); all 22 Phase-1 tests still pass.
  </done>
</task>

<task type="auto">
  <name>Task 2: Wire SSE forward loop in ChatCompletions endpoint (headers, FlushAsync, [DONE] injection, cancellation)</name>
  <files>src/SmartRouter.Cli/Endpoints/ChatCompletions.fs</files>
  <action>
Replace the Phase-1 HTTP 501 branch (currently lines ~111-117 — the `if req.Stream then ctx.Response.StatusCode <- 501 ...` block) with a real SSE forward loop. The new flow:

**Order of operations (CRITICAL — get this wrong and you violate STRM-04):**

1. Parse wire body → `RouterRequest` (unchanged).
2. Run routing FIRST. If `routeRequest routingConfig req` returns `Error` (UnsupportedTask, etc.), return HTTP 400 normally — no SSE headers, normal JSON error body. This stays exactly as it is today.
3. ONLY when routing returns `Ok decision` AND `req.Stream` is true, enter the streaming branch (move the `if req.Stream then` check INTO or AFTER the `Ok decision` arm, so the 400 path stays normal HTTP). The cleanest layout: keep the `match routeRequest ... with` block; in the `Ok decision` arm, branch on `req.Stream`.

**Streaming branch (`Ok decision` AND `req.Stream`):**

a. **PITFALL-6 / STRM-04** — Set headers BEFORE any body byte is written. Once any `WriteAsync` runs, headers are committed and cannot be changed:
   ```
   ctx.Response.ContentType <- "text/event-stream"
   ctx.Response.Headers["Cache-Control"]    <- Microsoft.Extensions.Primitives.StringValues "no-cache"
   ctx.Response.Headers["X-Accel-Buffering"] <- Microsoft.Extensions.Primitives.StringValues "no"
   ctx.Response.Headers["Connection"]       <- Microsoft.Extensions.Primitives.StringValues "keep-alive"
   ```
   Do NOT set `Content-Length`. Do NOT set `Transfer-Encoding` — Kestrel applies `chunked` automatically when `Content-Length` is absent.

b. Log routing decision ONCE at this point (`Log.Information("Routing target=... reason=... priority=... stream=true", ...)`). The Phase-1 handler already logs routing for non-streaming; mirror it but include the streaming flag. NO logs inside the chunk loop (OBS-04).

c. Bind `let ct = ctx.RequestAborted` (Phase 2 cancellation chain — Phase 3 will add a linked timeout CTS; do not pre-empt that here).

d. Call `let chunks = upstream.StreamAsync req decision.Target ct` to obtain the `IAsyncEnumerable<Result<string, RouterError>>`.

e. **PITFALL-4 manual enumerator loop** (required — F# `task {}` does not support `for ... in asyncEnumerable do` with `try/finally` semantics around the full loop):
   ```
   let enumerator = chunks.GetAsyncEnumerator(ct)
   let mutable sentDone = false
   try
       let mutable go = true
       while go do
           let! hasNext = enumerator.MoveNextAsync()
           if not hasNext then go <- false
           else
               match enumerator.Current with
               | Error e ->
                   // SSE error event — headers already sent so we cannot return HTTP 502 anymore.
                   let errMsg = sprintf "data: {\"error\":{\"message\":\"%s\",\"type\":\"upstream_error\"}}\n\n" (string e)
                   let errBytes = System.Text.Encoding.UTF8.GetBytes(errMsg)
                   do! ctx.Response.Body.WriteAsync(errBytes, 0, errBytes.Length, ct)
                   do! ctx.Response.Body.FlushAsync(ct)
                   go <- false
               | Ok line ->
                   if line.Contains("[DONE]") then sentDone <- true   // Strategy D
                   let eventLine = line + "\n\n"   // re-append SSE event terminator stripped by ReadLineAsync
                   let bytes = System.Text.Encoding.UTF8.GetBytes(eventLine)
                   do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ct)
                   do! ctx.Response.Body.FlushAsync(ct)   // PITFALL-3 / STRM-03 — flush after EVERY chunk
       // Strategy D injection: if upstream did not emit [DONE], emit it now (only if loop exited normally — see catch below).
       if not sentDone then
           let doneBytes = System.Text.Encoding.UTF8.GetBytes("data: [DONE]\n\n")
           do! ctx.Response.Body.WriteAsync(doneBytes, 0, doneBytes.Length, ct)
           do! ctx.Response.Body.FlushAsync(ct)
   with
   | :? System.OperationCanceledException ->
       // Client disconnected mid-stream (ctx.RequestAborted fired). No more writes possible.
       Log.Information("StreamAsync: client disconnected mid-stream for {Target}", decision.Target)
   | ex ->
       Log.Error(ex, "StreamAsync: unexpected error for {Target}", decision.Target)
   // finally — ensure enumerator (and through it, HttpResponseMessage) is disposed
   ```

f. The `finally`-style cleanup must run regardless of how the `try` exited — including the cancellation case. Use `try ... with ... finally` (F# task {} supports this). The finally body must call `do! enumerator.DisposeAsync()`. This is what triggers the `use _ = resp` disposal back in `StreamAsync`, which closes the upstream HTTP socket (PITFALL-4 / STRM-05).

g. Note on log placement: the success-path completion log can go AFTER the try/with/finally if you want a single completion line, OR omit it (the Phase-1 routing-decision log already records the decision). Do NOT log per-chunk.

**Non-streaming branch** (`Ok decision` AND `not req.Stream`): unchanged from Phase 1 — calls `upstream.CompleteAsync` and writes the JSON body.

**Routing-error branches (Error _)**: unchanged from Phase 1 — HTTP 400/etc. with JSON error body.

**Other invariants:**
- `HttpResponseMessage` must NOT cross the port boundary into Core or into `ChatCompletions.fs`. The endpoint only sees `IAsyncEnumerable<Result<string, RouterError>>`. (ARCH-05 — preserved.)
- Do not introduce a linked `CancellationTokenSource` here. Phase 3 owns the per-request timeout linkage; Phase 2 uses `ctx.RequestAborted` directly.
- Match the exact `chunk + "\n\n"` re-frame: `ReadLineAsync` strips the trailing `\n` from each line; mlx_lm sends `data: {...}\n\n`, which `ReadLineAsync` returns as `"data: {...}"` then `""`. The endpoint skips the empty separator (because StreamAsync's `elif line.Length > 0` filter drops it) and re-appends `\n\n` to restore complete event framing.

DO NOT:
- Set `Content-Length` or `Transfer-Encoding` manually.
- Call `WriteAsync` before headers are set.
- Use `for ... in chunks do` (F# task {} foreach over async sequence does not give `try/finally/DisposeAsync` precision).
- Log inside the `while go` loop.
- Inject `[DONE]` after the `OperationCanceledException` catch (the connection is gone — there is nobody to write to). Inject only on normal loop exit.
  </action>
  <verify>
Build clean: `dotnet build /Users/ohama/projs/smart-router/SmartRouter.slnx` exits 0, zero warnings.

Grep verifications:
1. `grep -n "text/event-stream" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — must return at least one match (STRM-04).
2. `grep -n "X-Accel-Buffering" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — must return at least one match (defensive header).
3. `grep -n "FlushAsync" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — must return at least 2 matches (one per Ok-chunk write, one for [DONE] injection or error event) — STRM-03 / PITFALL-3.
4. `grep -n "GetAsyncEnumerator" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — must return at least one match (manual enumerator loop, not `for ... in`).
5. `grep -n "DisposeAsync" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — must return at least one match (finally cleanup chain — PITFALL-4 / STRM-05).
6. `grep -n "\[DONE\]" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — must return at least one match (Strategy D injection — STRM-07).
7. `grep -n "501" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — must return zero matches (the Phase-1 stub is gone). If a 501 reference remains for a different purpose document it; otherwise scrub.
8. `grep -n "Log\\." src/SmartRouter.Cli/Endpoints/ChatCompletions.fs` — manual review: confirm no `Log.` call appears between `let mutable go = true` and the closing `with` (no per-chunk logs — OBS-04).

Manual smoke test (live upstream optional — Phase-2 tests in Plan 02-02 will exercise this without a live upstream):
- Start router: `cd src/SmartRouter.Cli && dotnet run`.
- Send a `stream=false` request (Phase-1 path) — confirm it still works (regression check).
- Send a `stream=true` request with an unknown task (`{"task":"foobar","stream":true}`). Confirm: HTTP 400, JSON error body, `Content-Type: application/json` (NOT `text/event-stream`). This proves routing happens before SSE headers (STRM-04 ordering).

Phase-1 tests still pass: `dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — 22/22 (no test additions in this plan; Plan 02-02 adds StreamingTests).
  </verify>
  <done>
- The `if req.Stream then` HTTP 501 stub is gone, replaced by a real SSE forward loop within the `Ok decision` routing arm.
- All four required SSE headers (`Content-Type: text/event-stream`, `Cache-Control: no-cache`, `X-Accel-Buffering: no`, `Connection: keep-alive`) are set BEFORE the first `WriteAsync` call.
- The chunk loop calls `ctx.Response.Body.FlushAsync(ct)` after every `WriteAsync(...)` (one for `Ok` chunks, one for `[DONE]` injection, one for the SSE error event).
- A manual `GetAsyncEnumerator` loop is used (no `for ... in` over the IAsyncEnumerable); `try/with` catches `OperationCanceledException` for client disconnect; the `finally`-equivalent path calls `enumerator.DisposeAsync()` — disposal chains through to `use _ = resp` in `StreamAsync`.
- Strategy D `[DONE]` injection: tracks `sentDone` boolean during the loop; injects `data: [DONE]\n\n` after the loop ONLY if `sentDone` is false AND the loop exited normally (not on cancellation).
- Routing errors (`UnsupportedTask`, etc.) still return HTTP 400 with normal JSON body; SSE headers are NOT set in those branches.
- No log call fires inside the chunk loop body.
- Build is clean (zero warnings); 22 Phase-1 tests still pass.
  </done>
</task>

</tasks>

<verification>
**Build + Phase-1 regression:**
```
dotnet build /Users/ohama/projs/smart-router/SmartRouter.slnx
# expect: 0 warnings, 0 errors
dotnet test /Users/ohama/projs/smart-router/tests/SmartRouter.Tests/SmartRouter.Tests.fsproj
# expect: 22 tests passed (Phase-1 RoutingTests still green)
bash /Users/ohama/projs/smart-router/scripts/check-no-async.sh
# expect: exit 0 — Core purity preserved
```

**Pitfall mitigation grep map (each must have ≥1 match):**
- PITFALL-2 / STRM-01 (ResponseHeadersRead): `grep "HttpCompletionOption.ResponseHeadersRead" src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs`
- PITFALL-3 / STRM-03 (FlushAsync per chunk): `grep "FlushAsync" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`
- PITFALL-4 / STRM-06 (HttpResponseMessage disposal scope via taskSeq): `grep "use _ = resp" src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs` AND `grep "DisposeAsync" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`
- PITFALL-6 / STRM-04 (SSE headers before first write): `grep "text/event-stream" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`
- PITFALL-19 / STRM-07 ([DONE] sentinel forwarded/injected): `grep "\[DONE\]" src/SmartRouter.Cli/Endpoints/ChatCompletions.fs`
- STRM-05 (client disconnect aborts upstream): verified via the cancellation chain `ctx.RequestAborted → StreamAsync ct → SendAsync ct → ReadLineAsync ct → finally enumerator.DisposeAsync() → resp.Dispose() → upstream socket close`. Plan 02-02 adds the test that proves this end-to-end.

**Manual smoke (optional — without live upstream):**
- `curl -i -X POST http://127.0.0.1:4000/v1/chat/completions -H 'Content-Type: application/json' -d '{"task":"foobar","stream":true,"messages":[{"role":"user","content":"hi"}]}'`
  Expected: HTTP 400, `Content-Type: application/json`, OpenAI-shape error body. Routing failed before SSE headers were set.
</verification>

<success_criteria>
- Both files build clean under `TreatWarningsAsErrors=true`.
- All 6 grep checks above return their expected match counts.
- Phase-1 tests remain 22/22 green (no regression).
- The 5 SSE pitfalls are all mitigated atomically in this single plan; any one of them missing leaves streaming silently broken.
- The streaming code path is in place but NOT YET TESTED end-to-end — Plan 02-02 owns the StreamingTests that exercise TTFB, ordering, 100-chunk integrity, mid-stream cancellation, [DONE] sentinel, and header assertions.
</success_criteria>

<output>
After completion, create `.planning/phases/02-sse-streaming-pass-through/02-01-SUMMARY.md` documenting:
- Final shape of `QwenUpstreamClient.StreamAsync` (taskSeq + ResponseHeadersRead + use _ = resp + ReadLineAsync loop).
- Final shape of `ChatCompletions` streaming branch (header order, manual enumerator loop, Strategy D for [DONE], cancellation handling).
- Any deviations from `02-RESEARCH.md` Pattern 1 / Pattern 2 with rationale.
- Note that StreamingTests are intentionally deferred to Plan 02-02 (depends_on this plan).
</output>
