module SmartRouter.Tests.StreamingTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open Expecto

// ── Helpers ──────────────────────────────────────────────────────────────────

/// Spin up a fake upstream Kestrel server on http://127.0.0.1:0 (OS-assigned port).
/// Maps:
///   GET /v1/models  → { data: [{ id: "/fake/model" }] }
///   POST /v1/chat/completions  → emits chunkCount SSE chunks with optional inter-chunk delay;
///                                optionally appends data: [DONE]
///
/// Returns (WebApplication, port).
/// The optional abortTcs is set when ctx.RequestAborted fires (for cancellation test).
let startFakeUpstream
    (chunkCount : int)
    (delayMs    : int)
    (emitDone   : bool)
    (abortTcs   : TaskCompletionSource<bool> option)
    : Task<WebApplication * int> =
    task {
        let fakeBuilder = WebApplication.CreateBuilder()
        fakeBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        fakeBuilder.Services.AddRouting() |> ignore
        let fakeApp = fakeBuilder.Build()

        fakeApp.MapGet("/v1/models", Func<IResult>(fun () ->
            Results.Json({| data = [| {| id = "/fake/model" |} |] |})))
        |> ignore

        fakeApp.MapPost("/v1/chat/completions", Func<HttpContext, Task>(fun ctx ->
            task {
                // Register cancellation hook for mid-stream cancellation test
                match abortTcs with
                | Some tcs ->
                    ctx.RequestAborted.Register(fun () ->
                        tcs.TrySetResult(true) |> ignore)
                    |> ignore
                | None -> ()

                ctx.Response.ContentType <- "text/event-stream"
                ctx.Response.Headers["Cache-Control"] <- StringValues "no-cache"

                for i in 0 .. chunkCount - 1 do
                    if delayMs > 0 then
                        try
                            do! Task.Delay(delayMs, ctx.RequestAborted)
                        with :? OperationCanceledException -> ()
                    if not ctx.RequestAborted.IsCancellationRequested then
                        let chunk =
                            sprintf "data: {\"choices\":[{\"delta\":{\"content\":\"%d\"}}]}" i
                        let line = chunk + "\n\n"
                        let bytes = Encoding.UTF8.GetBytes(line)
                        try
                            do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted)
                            do! ctx.Response.Body.FlushAsync(ctx.RequestAborted)
                        with :? OperationCanceledException -> ()

                if emitDone && not ctx.RequestAborted.IsCancellationRequested then
                    let doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n")
                    try
                        do! ctx.Response.Body.WriteAsync(doneBytes, 0, doneBytes.Length, ctx.RequestAborted)
                        do! ctx.Response.Body.FlushAsync(ctx.RequestAborted)
                    with :? OperationCanceledException -> ()
            }))
        |> ignore

        do! fakeApp.StartAsync()

        let addresses =
            fakeApp.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
        let port =
            addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int

        return fakeApp, port
    }

/// Build SmartRouter.Cli WebApplication in-process pointing at the fake upstream.
///
/// CRITICAL ordering:
///   Step 1: CreateBuilder + UseUrls
///   Step 2: AddInMemoryCollection — MUST happen before configureServices — overrides bind here
///   Step 3: configureServices — reads config; if Step 2 is swapped after Step 3,
///           UpstreamOptions binds to production defaults (localhost:8000/8001) and tests fail
///   Step 4: Build + mapEndpoints (without mapEndpoints ALL 8 tests silently 404)
///   Step 5: StartAsync
let startTestRouter (fakePort: int) : Task<WebApplication * int> =
    task {
        let testBuilder = WebApplication.CreateBuilder()
        testBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore

        // MUST happen before configureServices — overrides bind here
        // Cast to IConfigurationBuilder so AddInMemoryCollection extension method resolves
        (testBuilder.Configuration :> IConfigurationBuilder)
            .AddInMemoryCollection([
                KeyValuePair("Upstreams:Model35B",  sprintf "http://127.0.0.1:%d" fakePort)
                KeyValuePair("Upstreams:Model122B", sprintf "http://127.0.0.1:%d" fakePort)
                // Routing section — required for buildRoutingConfig + validateConfig to succeed
                KeyValuePair("Routing:Algorithm",            "heuristic")
                KeyValuePair("Routing:ComplexityThreshold", "3")
                KeyValuePair("Routing:TimeoutSeconds",       "300")
                KeyValuePair("Routing:Keywords:0",           "recursive")
                KeyValuePair("Routing:TaskTable:graph_indexing:Model",           "122b")
                KeyValuePair("Routing:TaskTable:graph_indexing:Priority",        "high")
                KeyValuePair("Routing:TaskTable:compiler_debug:Model",           "122b")
                KeyValuePair("Routing:TaskTable:compiler_debug:Priority",        "high")
                KeyValuePair("Routing:TaskTable:architecture_analysis:Model",    "122b")
                KeyValuePair("Routing:TaskTable:architecture_analysis:Priority", "high")
                KeyValuePair("Routing:TaskTable:dependency_analysis:Model",      "122b")
                KeyValuePair("Routing:TaskTable:dependency_analysis:Priority",   "low")
                KeyValuePair("Routing:TaskTable:reasoning:Model",                "122b")
                KeyValuePair("Routing:TaskTable:reasoning:Priority",             "low")
                KeyValuePair("Routing:TaskTable:retrieval:Model",                "35b")
                KeyValuePair("Routing:TaskTable:retrieval:Priority",             "low")
                KeyValuePair("Routing:TaskTable:summary:Model",                  "35b")
                KeyValuePair("Routing:TaskTable:summary:Priority",               "low")
                KeyValuePair("Routing:ModelAliases:35b",       "Qwen35B")
                KeyValuePair("Routing:ModelAliases:122b",      "Qwen122B")
                KeyValuePair("Routing:ModelAliases:qwen-35b",  "Qwen35B")
                KeyValuePair("Routing:ModelAliases:qwen35b",   "Qwen35B")
                KeyValuePair("Routing:ModelAliases:qwen-122b", "Qwen122B")
                KeyValuePair("Routing:ModelAliases:qwen122b",  "Qwen122B")
                // Queue section — required for QueueDispatcherOptions (MaxConcurrent122B must be 1)
                KeyValuePair("Queue:FairnessK",                "10")
                KeyValuePair("Queue:MaxConcurrent122B",        "1")
                KeyValuePair("Queue:PerRequestTimeoutSeconds", "300")
            ])
        |> ignore

        SmartRouter.Cli.CompositionRoot.configureServices
            testBuilder.Services
            testBuilder.Configuration
        |> ignore

        let app = testBuilder.Build()

        // Without mapEndpoints the router has zero routes — all 8 tests silently 404
        SmartRouter.Cli.Endpoints.ChatCompletions.mapEndpoints app

        do! app.StartAsync()

        let addresses =
            app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
        let routerPort =
            addresses
            |> Seq.head
            |> fun a -> a.Split(':') |> Array.last |> int

        return app, routerPort
    }

/// Read SSE lines from an HttpResponseMessage stream until EOF or data: [DONE].
/// Returns all "data: ..." lines (including "data: [DONE]" as the last element if present).
let readSseChunks (response: HttpResponseMessage) (ct: CancellationToken) : Task<string list> =
    task {
        use stream = response.Content.ReadAsStream()
        use reader = new StreamReader(stream)
        let chunks = List<string>()
        let mutable isDone = false
        while not isDone do
            let! line = reader.ReadLineAsync(ct)
            if isNull line then
                isDone <- true
            elif line.StartsWith("data: ") then
                chunks.Add(line)
                if line = "data: [DONE]" then isDone <- true
        return List.ofSeq chunks
    }

/// POST /v1/chat/completions with stream=true. Uses ResponseHeadersRead so the test
/// client also streams — required for TTFB measurement.
let postStreamRequest
    (client  : HttpClient)
    (port    : int)
    (taskOpt : string option)
    (ct      : CancellationToken)
    : Task<HttpResponseMessage> =
    task {
        let body =
            match taskOpt with
            | None ->
                """{"messages":[{"role":"user","content":"hi"}],"stream":true}"""
            | Some t ->
                sprintf """{"messages":[{"role":"user","content":"hi"}],"stream":true,"task":"%s"}""" t
        let url = sprintf "http://127.0.0.1:%d/v1/chat/completions" port
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        return! client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
    }

/// Stop and dispose both apps. Called from finally blocks (synchronous — task CE does not
/// allow do! inside finally). Uses .GetAwaiter().GetResult() for the async stop.
let private teardown (routerApp: WebApplication) (fakeApp: WebApplication) =
    try routerApp.StopAsync().GetAwaiter().GetResult() with _ -> ()
    try fakeApp.StopAsync().GetAwaiter().GetResult()   with _ -> ()
    try (routerApp :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()
    try (fakeApp   :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() with _ -> ()

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced (testList "streaming" [

        // Test 1: TTFB under 2s (STRM-01, STRM-03)
        // Proves ResponseHeadersRead is working and FlushAsync fires per chunk.
        testCase "TTFB under 2 s" <| fun () ->
            (task {
                let! fakeApp, fakePort = startFakeUpstream 5 100 true None
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! response =
                        postStreamRequest client routerPort None cts.Token
                    Expect.equal (int response.StatusCode) 200 "router should return 200"
                    // Read first data line and measure TTFB
                    let sw = Stopwatch.StartNew()
                    use stream = response.Content.ReadAsStream()
                    use reader = new StreamReader(stream)
                    let mutable firstLine : string = null
                    let mutable isDone = false
                    while not isDone do
                        let! line = reader.ReadLineAsync(cts.Token)
                        if isNull line then isDone <- true
                        elif line.StartsWith("data: ") then
                            firstLine <- line
                            isDone <- true
                    sw.Stop()
                    Expect.isNotNull firstLine "should have received at least one data line"
                    Expect.isLessThan sw.ElapsedMilliseconds 2000L "TTFB must be under 2 s (STRM-01)"
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // Test 2: Chunk ordering (STRM-02)
        // Proves chunks arrive in the correct 0..9 order with no reordering.
        testCase "chunks arrive in order" <| fun () ->
            (task {
                let! fakeApp, fakePort = startFakeUpstream 10 0 true None
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! response =
                        postStreamRequest client routerPort None cts.Token
                    Expect.equal (int response.StatusCode) 200 "router should return 200"
                    let! chunks = readSseChunks response cts.Token
                    // Filter out [DONE]; remaining should be data chunks
                    let dataChunks = chunks |> List.filter (fun c -> c <> "data: [DONE]")
                    Expect.equal dataChunks.Length 10 "should receive 10 data chunks"
                    for i in 0 .. 9 do
                        let expected = sprintf "%d" i
                        let line = dataChunks.[i]
                        Expect.isTrue
                            (line.Contains(sprintf "\"content\":\"%s\"" expected))
                            (sprintf "chunk %d should contain content '%s' but got: %s" i expected line)
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // Test 3: 100-chunk integrity (STRM-02 reliability)
        // Proves no duplication, no loss, no split events over 100 chunks.
        testCase "100-chunk integrity" <| fun () ->
            (task {
                let! fakeApp, fakePort = startFakeUpstream 100 0 true None
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! response =
                        postStreamRequest client routerPort None cts.Token
                    Expect.equal (int response.StatusCode) 200 "router should return 200"
                    let! chunks = readSseChunks response cts.Token
                    let dataChunks = chunks |> List.filter (fun c -> c <> "data: [DONE]")
                    Expect.equal dataChunks.Length 100 "should receive exactly 100 data chunks"
                    for i in 0 .. 99 do
                        let expected = sprintf "%d" i
                        let line = dataChunks.[i]
                        Expect.isTrue
                            (line.Contains(sprintf "\"content\":\"%s\"" expected))
                            (sprintf "chunk %d content mismatch: %s" i line)
                    // No duplicates: all 100 indices appear exactly once
                    let distinctCount = dataChunks |> List.distinct |> List.length
                    Expect.equal distinctCount 100 "no duplicate chunks"
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // Test 4: Mid-stream cancellation aborts upstream (STRM-05, STRM-06, PITFALL-4)
        // Proves: closing the client TCP connection fires fake upstream's RequestAborted within
        // 5s (20x headroom over 5x50ms = 250ms emission window); no ObjectDisposedException
        // escapes (proving use _ = resp in StreamAsync keeps response alive during read).
        //
        // Mechanism: disposing the response closes the TCP socket to the router. Kestrel
        // detects the socket close and fires ctx.RequestAborted in the router handler. The
        // router's ct (= ctx.RequestAborted) then propagates into StreamAsync → ReadLineAsync
        // throws OperationCanceledException → enumerator.DisposeAsync() fires → upstream socket
        // closes → fake upstream's ctx.RequestAborted fires.
        testCase "mid-stream cancellation aborts upstream" <| fun () ->
            (task {
                let abortTcs = TaskCompletionSource<bool>()
                let! fakeApp, fakePort = startFakeUpstream 100 50 true (Some abortTcs)
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    // Send request — the response variable is NOT bound with use! here because
                    // we need to dispose it manually after 5 chunks to close the TCP connection.
                    let! response = postStreamRequest client routerPort None cts.Token
                    Expect.equal (int response.StatusCode) 200 "router should return 200"
                    let stream = response.Content.ReadAsStream()
                    let reader = new StreamReader(stream)
                    // Read 5 chunks then dispose response to close TCP connection to router
                    let mutable received = 0
                    let mutable isDone = false
                    while not isDone do
                        let! line = reader.ReadLineAsync(cts.Token)
                        if isNull line then
                            isDone <- true
                        elif line.StartsWith("data: ") && line <> "data: [DONE]" then
                            received <- received + 1
                            if received >= 5 then
                                isDone <- true
                    // Dispose the response to close the TCP socket to the router.
                    // Kestrel detects the socket close → ctx.RequestAborted fires in router
                    // → OperationCanceledException propagates through ReadLineAsync in StreamAsync
                    // → enumerator.DisposeAsync() fires → upstream socket closes
                    // → fake upstream's ctx.RequestAborted fires (the abortTcs flag)
                    reader.Dispose()
                    stream.Dispose()
                    response.Dispose()
                    // Fake upstream's RequestAborted should fire within 5s (20x headroom)
                    use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
                    let! abortFired =
                        task {
                            try
                                return! abortTcs.Task.WaitAsync(timeoutCts.Token)
                            with :? OperationCanceledException ->
                                return false
                        }
                    Expect.isTrue abortFired
                        "fake upstream RequestAborted should fire within 5s of client disconnect (STRM-05)"
                    // Test passes: no ObjectDisposedException escaped (PITFALL-4)
                    // If use _ = resp were missing in StreamAsync, disposing the response here
                    // while the read loop is still running would cause ObjectDisposedException.
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // Test 5: [DONE] forwarded when upstream emits it (STRM-07 forward path)
        // Proves Strategy D forward path: exactly one [DONE], it is the final element.
        testCase "[DONE] forwarded when upstream emits it" <| fun () ->
            (task {
                let! fakeApp, fakePort = startFakeUpstream 5 0 true None
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! response =
                        postStreamRequest client routerPort None cts.Token
                    Expect.equal (int response.StatusCode) 200 "router should return 200"
                    let! chunks = readSseChunks response cts.Token
                    Expect.isTrue (chunks.Length > 0) "should receive at least one chunk"
                    Expect.equal (List.last chunks) "data: [DONE]"
                        "[DONE] should be the final chunk (STRM-07 forward path)"
                    let doneCount = chunks |> List.filter (fun c -> c = "data: [DONE]") |> List.length
                    Expect.equal doneCount 1 "exactly one [DONE] — no double injection"
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // Test 6: [DONE] injected when upstream omits it (STRM-07 inject path / Strategy D)
        // Proves router synthesizes [DONE] even when upstream omits it.
        testCase "[DONE] injected when upstream omits it" <| fun () ->
            (task {
                // emitDone=false — upstream does NOT send [DONE]
                let! fakeApp, fakePort = startFakeUpstream 5 0 false None
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! response =
                        postStreamRequest client routerPort None cts.Token
                    Expect.equal (int response.StatusCode) 200 "router should return 200"
                    let! chunks = readSseChunks response cts.Token
                    Expect.isTrue (chunks.Length > 0) "should receive at least one chunk"
                    Expect.equal (List.last chunks) "data: [DONE]"
                        "router must inject [DONE] when upstream omits it (STRM-07 inject path)"
                    let doneCount = chunks |> List.filter (fun c -> c = "data: [DONE]") |> List.length
                    Expect.equal doneCount 1 "exactly one [DONE] — not duplicated"
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // Test 7: SSE response headers (STRM-04, PITFALL-6)
        // Proves Content-Type: text/event-stream, Cache-Control: no-cache, no Content-Length.
        testCase "response headers are SSE" <| fun () ->
            (task {
                let! fakeApp, fakePort = startFakeUpstream 1 0 true None
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! response =
                        postStreamRequest client routerPort None cts.Token
                    Expect.equal (int response.StatusCode) 200 "router should return 200"
                    // Content-Type must be text/event-stream (STRM-04)
                    let contentType = response.Content.Headers.ContentType
                    Expect.isNotNull contentType "Content-Type header must be set"
                    Expect.equal contentType.MediaType "text/event-stream"
                        "Content-Type must be text/event-stream (STRM-04)"
                    // Cache-Control must contain no-cache
                    let cacheControl =
                        response.Headers.GetValues("Cache-Control")
                        |> Seq.tryFind (fun v -> v.Contains("no-cache"))
                    Expect.isSome cacheControl "Cache-Control must contain no-cache (STRM-04)"
                    // Content-Length must NOT be set — Kestrel uses chunked transfer encoding
                    let contentLength = response.Content.Headers.ContentLength
                    Expect.isTrue
                        (not contentLength.HasValue)
                        "Content-Length must be absent for streaming (chunked transfer encoding)"
                    // Consume stream to avoid leaked connections
                    let! _ = readSseChunks response cts.Token
                    ()
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

        // Test 8: Routing error returns HTTP 400 not SSE (TEST-03 / order-of-operations)
        // Proves routing (and 400 error) runs BEFORE any SSE headers are set.
        // stream=true + task=foobar → HTTP 400 JSON, NOT text/event-stream.
        testCase "routing error returns HTTP 400 not SSE" <| fun () ->
            (task {
                let! fakeApp, fakePort = startFakeUpstream 1 0 false None
                let! routerApp, routerPort = startTestRouter fakePort
                try
                    use client = new HttpClient()
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    use! response =
                        postStreamRequest client routerPort (Some "foobar") cts.Token
                    Expect.equal (int response.StatusCode) 400
                        "unknown task with stream=true must return HTTP 400 (not SSE)"
                    let contentType = response.Content.Headers.ContentType
                    Expect.isNotNull contentType "Content-Type header must be set"
                    Expect.equal contentType.MediaType "application/json"
                        "error response must be application/json (not text/event-stream)"
                    let! body = response.Content.ReadAsStringAsync(cts.Token)
                    Expect.isTrue
                        (body.Contains("foobar") || body.Contains("unknown task"))
                        (sprintf "error body should mention 'foobar' or 'unknown task' but got: %s" body)
                finally
                    teardown routerApp fakeApp
            } |> Async.AwaitTask |> Async.RunSynchronously)

    ])
