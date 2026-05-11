module SmartRouter.Tests.SelfRouterTests

// Phase 19 (19-03): Unit tests for SelfRouter adapter (SR-03/SR-04/SR-07).
//
// Tests cover:
//   Parser safety bias: UNSAFE wins over SAFE on collision (PITFALL #1 — "SAFE" ⊂ "UNSAFE")
//   Parser variants: SAFE response, UNSAFE response, garbage response → RouteFailed
//   Cache mechanics: hit skips HTTP, miss counts HTTP, RouteFailed NOT cached
//   Template missing: RouteSkipped + skipped counter incremented
//   PromptVersion: SHA-256 hex8 prefix from file; "selfrouting-v1" fallback when missing
//
// Uses fake IHttpClientFactory returning controllable responses.
// Wrapped in testSequenced (PITFALL-27 — not strictly required for these pure tests
// but good hygiene since we write temp files; serialization prevents file collisions).

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Logging.Abstractions
open SmartRouter.Cli.Adapters.SelfRouter

// ── Fake IHttpClientFactory → controllable stub handler ──────────────────────

type private StubHandler(responder: unit -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(_req: HttpRequestMessage, _ct: CancellationToken) = responder ()

let private makeFactory (responder: unit -> Task<HttpResponseMessage>) : IHttpClientFactory =
    { new IHttpClientFactory with
        member _.CreateClient(_name) =
            let client = new HttpClient(new StubHandler(responder))
            client.BaseAddress <- Uri("http://localhost:0/")
            client }

let private makeCountingFactory (responder: unit -> Task<HttpResponseMessage>) (counter: int ref) : IHttpClientFactory =
    { new IHttpClientFactory with
        member _.CreateClient(_name) =
            let client =
                new HttpClient(new StubHandler(fun () ->
                    System.Threading.Interlocked.Increment(counter) |> ignore
                    responder ()))
            client.BaseAddress <- Uri("http://localhost:0/")
            client }

let private makeOpts (promptPath: string) : SelfRouterOptions =
    { Endpoint        = "http://localhost:0"
      PromptPath      = promptPath
      TimeoutSeconds  = 5
      MaxCacheEntries = 100 }

let private mkResponse (body: string) : Task<HttpResponseMessage> =
    let r = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    r.Content <- new StringContent(body)
    Task.FromResult(r)

let private safeBody    = """{"choices":[{"message":{"content":"SAFE"}}]}"""
let private unsafeBody  = """{"choices":[{"message":{"content":"UNSAFE"}}]}"""
let private garbageBody = """{"choices":[{"message":{"content":"i think probably yes"}}]}"""

// ── Prompt template fixture helper ───────────────────────────────────────────

let private writeTempPrompt (content: string) : string =
    let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md")
    File.WriteAllText(path, content)
    path

let private validTemplate = "Classify: SAFE or UNSAFE. {{PROMPT}}"

// ── Test suite ────────────────────────────────────────────────────────────────

let selfRouterTests : Test =
    testSequenced <| testList "SelfRouter — unit" [

        testCase "parser: SAFE response → RouteSafe" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let factory = makeFactory (fun () -> mkResponse safeBody)
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let verdict = (sr :> ISelfRouter).ClassifyAsync("hash1", "anything", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal verdict RouteSafe "SAFE response → RouteSafe"
            finally File.Delete(path)

        testCase "parser: UNSAFE response → RouteUnsafe" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let factory = makeFactory (fun () -> mkResponse unsafeBody)
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let verdict = (sr :> ISelfRouter).ClassifyAsync("hash2", "anything", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal verdict RouteUnsafe "UNSAFE response → RouteUnsafe"
            finally File.Delete(path)

        // PITFALL #1 — SAFE is a substring of UNSAFE; UNSAFE must win (safety bias)
        testCase "parser: response containing both SAFE and UNSAFE → RouteUnsafe (safety bias)" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let body = """{"choices":[{"message":{"content":"UNSAFE (definitely not SAFE)"}}]}"""
                let factory = makeFactory (fun () -> mkResponse body)
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let verdict = (sr :> ISelfRouter).ClassifyAsync("hash3", "x", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal verdict RouteUnsafe "SAFE-in-UNSAFE collision → UNSAFE wins"
            finally File.Delete(path)

        testCase "parser: garbage response → RouteFailed" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let factory = makeFactory (fun () -> mkResponse garbageBody)
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let verdict = (sr :> ISelfRouter).ClassifyAsync("hash4", "x", CancellationToken.None).GetAwaiter().GetResult()
                match verdict with
                | RouteFailed _ -> ()
                | other -> failtestf "expected RouteFailed; got %A" other
            finally File.Delete(path)

        testCase "cache: second call with same hash skips HTTP (call_count stays at 1)" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let hits = ref 0
                let factory = makeCountingFactory (fun () -> mkResponse safeBody) hits
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let v1 = (sr :> ISelfRouter).ClassifyAsync("hashCACHE", "x", CancellationToken.None).GetAwaiter().GetResult()
                let v2 = (sr :> ISelfRouter).ClassifyAsync("hashCACHE", "x", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v1 RouteSafe "first call decided"
                Expect.equal v2 RouteSafe "second call cached"
                Expect.equal !hits 1 "HTTP called exactly once"
                let struct (cacheHits, cacheMisses, callCount, _) = (sr :> ISelfRouterStats).GetSelfRouterStats()
                Expect.equal cacheMisses 1L "cache miss on first call"
                Expect.equal cacheHits   1L "cache hit on second call"
                Expect.equal callCount   1L "HTTP call counted once"
            finally File.Delete(path)

        testCase "cache: RouteFailed NOT cached (subsequent call re-hits HTTP)" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let hits = ref 0
                let factory = makeCountingFactory (fun () -> mkResponse garbageBody) hits
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let _ = (sr :> ISelfRouter).ClassifyAsync("hashFAIL", "x", CancellationToken.None).GetAwaiter().GetResult()
                let _ = (sr :> ISelfRouter).ClassifyAsync("hashFAIL", "x", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal !hits 2 "RouteFailed not cached → both calls hit HTTP"
            finally File.Delete(path)

        testCase "template missing: returns RouteSkipped + increments skipped counter" <| fun () ->
            let nonexistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md")
            let factory = makeFactory (fun () -> mkResponse safeBody)
            let sr = SelfRouter(factory, makeOpts nonexistent, NullLogger<SelfRouter>())
            let verdict = (sr :> ISelfRouter).ClassifyAsync("hashNT", "x", CancellationToken.None).GetAwaiter().GetResult()
            match verdict with
            | RouteSkipped _ -> ()
            | other -> failtestf "expected RouteSkipped; got %A" other
            let struct (_, _, _, skipped) = (sr :> ISelfRouterStats).GetSelfRouterStats()
            Expect.equal skipped 1L "skipped counter incremented"

        testCase "PromptVersion: present prompt file → selfrouting-{hex8}" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let factory = makeFactory (fun () -> mkResponse safeBody)
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let version = (sr :> ISelfRouter).PromptVersion
                Expect.isTrue (version.StartsWith("selfrouting-")) "version prefix"
                Expect.equal (version.Length) ("selfrouting-".Length + 8) "8-char hex suffix"
            finally File.Delete(path)

        testCase "PromptVersion: missing prompt file → selfrouting-v1 fallback" <| fun () ->
            let nonexistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md")
            let factory = makeFactory (fun () -> mkResponse safeBody)
            let sr = SelfRouter(factory, makeOpts nonexistent, NullLogger<SelfRouter>())
            let version = (sr :> ISelfRouter).PromptVersion
            Expect.equal version "selfrouting-v1" "fallback version"

        testCase "RouteUnsafe is cached: second call for UNSAFE does not re-hit HTTP" <| fun () ->
            let path = writeTempPrompt validTemplate
            try
                let hits = ref 0
                let factory = makeCountingFactory (fun () -> mkResponse unsafeBody) hits
                let sr = SelfRouter(factory, makeOpts path, NullLogger<SelfRouter>())
                let v1 = (sr :> ISelfRouter).ClassifyAsync("hashUNSAFE", "dangerous", CancellationToken.None).GetAwaiter().GetResult()
                let v2 = (sr :> ISelfRouter).ClassifyAsync("hashUNSAFE", "dangerous", CancellationToken.None).GetAwaiter().GetResult()
                Expect.equal v1 RouteUnsafe "first call → RouteUnsafe"
                Expect.equal v2 RouteUnsafe "second call → RouteUnsafe (cached)"
                Expect.equal !hits 1 "HTTP called exactly once for cached RouteUnsafe"
            finally File.Delete(path)
    ]
