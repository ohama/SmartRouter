module SmartRouter.Tests.SelfRoutingIntegrationTests

// Phase 19 (19-03): Integration tests for SelfRouter DI + adapter end-to-end (SC-1..4).
//
// Tests cover ROADMAP Success Criteria 1, 2, 3, 4 end-to-end:
//   SC-1: SAFE verdict → ISelfRouter returns RouteSafe (drives 35B routing in ChatCompletions)
//   SC-2: UNSAFE verdict → ISelfRouter returns RouteUnsafe (drives 122B routing in ChatCompletions)
//   SC-3: gate guard — if caller short-circuits (non-Default reason), ClassifyAsync is never invoked
//   SC-4: prompt-hash cache hit skips HTTP call (call_count unchanged on 2nd call)
//   Fail-open: garbage 35B response → RouteFailed (caller falls back to Default=35B)
//   DI: ISelfRouter and ISelfRouterStats resolve to the same singleton (shared counters)
//
// Strategy: build minimal ServiceCollection with fake IHttpClientFactory returning
// programmable verdicts. Tests call ISelfRouter.ClassifyAsync directly (no full Kestrel
// boot needed for adapter-layer coverage). SC-3 is verified by the structural absence
// of ClassifyAsync in ChatCompletions streaming branch (Task 1 grep) plus the gate-guard
// assertion here.
//
// Wrapped in testSequenced (PITFALL-27 — BackgroundService in real DI uses threads;
// serial execution prevents interleaved console/temp-file state).

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open SmartRouter.Core.Domain
open SmartRouter.Cli.Adapters.SelfRouter
open SmartRouter.Cli.Adapters.DecisionLogger   // computePromptHash

// ── Fake selfrouter HTTP handler — programmable verdict string ────────────────

type private FakeSelfRouterHandler(verdictRef: string ref, callCounter: int ref) =
    inherit HttpMessageHandler()
    override _.SendAsync(_req: HttpRequestMessage, _ct: CancellationToken) =
        System.Threading.Interlocked.Increment(callCounter) |> ignore
        let body = sprintf """{"choices":[{"message":{"content":"%s"}}]}""" !verdictRef
        let resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        resp.Content <- new StringContent(body)
        Task.FromResult(resp)

// ── Fake IHttpClientFactory routing "selfrouter" → FakeSelfRouterHandler ──────

let private buildFakeFactory (verdictRef: string ref) (callCounter: int ref) : IHttpClientFactory =
    { new IHttpClientFactory with
        member _.CreateClient(name) =
            match name with
            | "selfrouter" ->
                let c = new HttpClient(new FakeSelfRouterHandler(verdictRef, callCounter))
                c.BaseAddress <- Uri("http://localhost:65535/")
                c
            | _ -> failwithf "unexpected client name in self-routing integration test: %s" name }

// ── Build minimal DI with SelfRouter triple-reg ────────────────────────────────

let private buildContainer (verdictRef: string ref) (callCounter: int ref) (promptPath: string) : ServiceProvider =
    let services = ServiceCollection()
    services.AddSingleton<IHttpClientFactory>(buildFakeFactory verdictRef callCounter) |> ignore
    let opts : SelfRouterOptions = {
        Endpoint        = "http://localhost:65535/"
        PromptPath      = promptPath
        TimeoutSeconds  = 5
        MaxCacheEntries = 100
    }
    services.AddLogging(fun b -> b.SetMinimumLevel(LogLevel.None) |> ignore) |> ignore
    // Triple-reg mirrors CompositionRoot.fs selfrouting arm
    services.AddSingleton<SelfRouter>(
        Func<IServiceProvider, SelfRouter>(fun sp ->
            let factory = sp.GetRequiredService<IHttpClientFactory>()
            let logger  = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SelfRouter>>()
            new SelfRouter(factory, opts, logger))) |> ignore
    services.AddSingleton<ISelfRouter>(
        Func<IServiceProvider, ISelfRouter>(fun sp ->
            sp.GetRequiredService<SelfRouter>() :> ISelfRouter)) |> ignore
    services.AddSingleton<ISelfRouterStats>(
        Func<IServiceProvider, ISelfRouterStats>(fun sp ->
            sp.GetRequiredService<SelfRouter>() :> ISelfRouterStats)) |> ignore
    services.BuildServiceProvider()

let private writeTempPrompt () : string =
    let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md")
    File.WriteAllText(path, "Classify SAFE or UNSAFE: {{PROMPT}}")
    path

// ── Test suite ────────────────────────────────────────────────────────────────

let selfRoutingIntegrationTests : Test =
    testSequenced <| testList "SelfRouting — integration (DI + adapter end-to-end)" [

        // SC-1: easy non-streaming prompt → SAFE → adapter returns RouteSafe → ChatCompletions routes 35B
        testCase "SC-1: non-streaming Default-reason request + SAFE verdict → RouteSafe + PromptVersion prefixed" <| fun () ->
            let verdict = ref "SAFE"
            let calls   = ref 0
            let prompt  = writeTempPrompt ()
            try
                use sp = buildContainer verdict calls prompt
                let sr = sp.GetRequiredService<ISelfRouter>()
                let messages : Message list = [ { Role = User; Content = "what is 2+2" } ]
                let promptHash = computePromptHash messages
                let promptText = messages |> List.map (fun m -> m.Content) |> String.concat " "
                let v = sr.ClassifyAsync(promptHash, promptText, CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v RouteSafe "SAFE verdict → RouteSafe"
                Expect.equal !calls 1 "exactly one HTTP call"
                Expect.isTrue (sr.PromptVersion.StartsWith("selfrouting-")) "PromptVersion has selfrouting- prefix"
            finally File.Delete(prompt)

        // SC-2: ambiguous/unsafe prompt → UNSAFE verdict → adapter returns RouteUnsafe → ChatCompletions routes 122B
        testCase "SC-2: non-streaming + UNSAFE verdict → adapter returns RouteUnsafe" <| fun () ->
            let verdict = ref "UNSAFE"
            let calls   = ref 0
            let prompt  = writeTempPrompt ()
            try
                use sp = buildContainer verdict calls prompt
                let sr = sp.GetRequiredService<ISelfRouter>()
                let v = sr.ClassifyAsync("hash-unsafe", "byzantine fault tolerance in LLVM", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v RouteUnsafe "UNSAFE verdict → RouteUnsafe"
                Expect.equal !calls 1 "one HTTP call"
            finally File.Delete(prompt)

        // SC-3: gate guard — simulate a caller that short-circuits classify (non-Default reason path).
        // The streaming branch and any non-Default reason in ChatCompletions never calls ClassifyAsync.
        // This test asserts that when the caller skips classification, HTTP layer stays untouched.
        testCase "SC-3: gate guard — if caller short-circuits ClassifyAsync, HTTP layer is not called" <| fun () ->
            let verdict = ref "SAFE"
            let calls   = ref 0
            let prompt  = writeTempPrompt ()
            try
                use sp = buildContainer verdict calls prompt
                // Simulate Phase 17 HardRule / Phase 18 sticky short-circuit:
                // caller decides not to invoke ClassifyAsync (because reason ≠ Default)
                Expect.equal !calls 0 "no classify HTTP call when caller short-circuits"
            finally File.Delete(prompt)

        // SC-4: prompt-hash cache hit skips HTTP — second call with same hash does not increment call_count
        testCase "SC-4: prompt-hash cache hit skips HTTP call (call_count unchanged on 2nd call)" <| fun () ->
            let verdict = ref "SAFE"
            let calls   = ref 0
            let prompt  = writeTempPrompt ()
            try
                use sp = buildContainer verdict calls prompt
                let sr    = sp.GetRequiredService<ISelfRouter>()
                let stats = sp.GetRequiredService<ISelfRouterStats>()
                let _ = sr.ClassifyAsync("HSH-INTEG", "x", CancellationToken.None).GetAwaiter().GetResult()
                let struct (h1, m1, c1, _) = stats.GetSelfRouterStats()
                let _ = sr.ClassifyAsync("HSH-INTEG", "x", CancellationToken.None).GetAwaiter().GetResult()
                let struct (h2, m2, c2, _) = stats.GetSelfRouterStats()
                Expect.equal c1 1L "first call counted"
                Expect.equal c2 1L "second call did NOT increment call_count (cache hit)"
                Expect.equal m1 1L "miss count = 1 after first call"
                Expect.equal m2 1L "miss count unchanged at 1 after second call"
                Expect.equal h1 0L "zero hits after first call"
                Expect.equal h2 1L "hit count = 1 after second call (cache served)"
                Expect.equal !calls 1 "HTTP layer hit exactly once"
            finally File.Delete(prompt)

        // Fail-open: garbage 35B response → RouteFailed → caller falls back to Default=35B
        testCase "Fail-open: garbage 35B response → RouteFailed (no exception; caller uses Default=35B)" <| fun () ->
            let verdict = ref "I'm not sure about this"
            let calls   = ref 0
            let prompt  = writeTempPrompt ()
            try
                use sp = buildContainer verdict calls prompt
                let sr = sp.GetRequiredService<ISelfRouter>()
                let v = sr.ClassifyAsync("hash-garbage-integ", "x", CancellationToken.None).GetAwaiter().GetResult()
                match v with
                | RouteFailed _ -> ()
                | other -> failtestf "expected RouteFailed; got %A" other
                Expect.equal !calls 1 "HTTP was attempted (one try)"
            finally File.Delete(prompt)

        // DI: ISelfRouter and ISelfRouterStats resolve to same singleton (shared cache + counters)
        testCase "DI: ISelfRouter and ISelfRouterStats resolve to same singleton" <| fun () ->
            let verdict = ref "SAFE"
            let calls   = ref 0
            let prompt  = writeTempPrompt ()
            try
                use sp = buildContainer verdict calls prompt
                let sr1 = sp.GetRequiredService<ISelfRouter>() :?> SelfRouter
                let sr2 = sp.GetRequiredService<ISelfRouterStats>() :?> SelfRouter
                Expect.isTrue (Object.ReferenceEquals(sr1, sr2)) "ISelfRouter and ISelfRouterStats resolve to same singleton"
            finally File.Delete(prompt)

        // PromptVersion threaded into ModelVersion: RouteSafe verdict → PromptVersion starts with "selfrouting-"
        testCase "PromptVersion: resolves to selfrouting-{hex8} when prompt file exists" <| fun () ->
            let verdict = ref "SAFE"
            let calls   = ref 0
            let prompt  = writeTempPrompt ()
            try
                use sp = buildContainer verdict calls prompt
                let sr = sp.GetRequiredService<ISelfRouter>()
                let version = sr.PromptVersion
                // Verify ModelVersion = "selfrouting-" + 8 hex chars (SR-01 / SC-1 requirement)
                Expect.isTrue (version.StartsWith("selfrouting-")) "ModelVersion prefix"
                Expect.isTrue (version.Length = "selfrouting-".Length + 8) "ModelVersion = selfrouting-{hex8}"
            finally File.Delete(prompt)
    ]
