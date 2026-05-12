module SmartRouter.Tests.HermesFingerprintTests

// Integration tests for Phase 20 fingerprint fallback (HMRS-02 goal-backward verification).
//
// Strategy: exercise correlationMiddleware directly with DefaultHttpContext (no Kestrel).
// The middleware is synchronous-enough to test via Async.RunSynchronously after
// Async.AwaitTask — same pattern as other in-process adapter unit tests.
//
// Wrapped with testSequenced per PITFALL-27 (project convention for all test modules
// that could touch shared state; mirrors StickyEscalationTests.fs / SessionStoreTests.fs).

open System.Net
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open SmartRouter.Cli.Adapters.CorrelationMiddleware

// ── Helpers ──────────────────────────────────────────────────────────────────

/// Build a DefaultHttpContext with the given remote IP, User-Agent, and optional X-Session-Id.
/// Pass "" for remoteIp to simulate a null RemoteIpAddress (Pitfall 2 / FP-8 scenario).
let private makeCtx (remoteIp: string) (ua: string) (sessionHeader: string option) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Connection.RemoteIpAddress <-
        if remoteIp = "" then null
        else IPAddress.Parse(remoteIp)
    if ua <> "" then
        ctx.Request.Headers.["User-Agent"] <- StringValues(ua)
    match sessionHeader with
    | Some hv -> ctx.Request.Headers.["X-Session-Id"] <- StringValues(hv)
    | None    -> ()
    ctx

/// Run the middleware to completion. RequestDelegate is a no-op (just returns CompletedTask).
let private runMiddleware (fingerprintEnabled: bool) (ctx: DefaultHttpContext) : unit =
    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    correlationMiddleware fingerprintEnabled ctx next
    |> Async.AwaitTask
    |> Async.RunSynchronously

/// Read the session ID that correlationMiddleware wrote into HttpContext.Items.
let private getSessionId (ctx: DefaultHttpContext) : string =
    match ctx.Items.TryGetValue(SessionIdKey) with
    | true, (:? string as s) -> s
    | _                      -> ""

// ── Tests ─────────────────────────────────────────────────────────────────────

let tests : Test =
    testSequenced <| testList "HermesFingerprintTests" [

        // FP-1: Fingerprint disabled + no X-Session-Id → sessionId is empty string.
        // Verifies v1.x stateless behavior is preserved when opt-in flag is false.
        testCase "FP-1: fingerprint disabled + no header → sessionId is empty" <| fun _ ->
            let ctx = makeCtx "127.0.0.1" "python/3.11" None
            runMiddleware false ctx
            Expect.equal (getSessionId ctx) "" "expected empty sessionId when fingerprint disabled and no header"

        // FP-2: Fingerprint disabled + explicit X-Session-Id → header value is returned.
        // Verifies Phase 18 header-passthrough still works with fingerprintEnabled=false.
        testCase "FP-2: fingerprint disabled + header present → sessionId is the header value" <| fun _ ->
            let ctx = makeCtx "127.0.0.1" "python/3.11" (Some "sess-abc")
            runMiddleware false ctx
            Expect.equal (getSessionId ctx) "sess-abc" "expected explicit header value when fingerprint disabled"

        // FP-3: Fingerprint enabled + no X-Session-Id → 16 lowercase-hex chars derived from SHA-256.
        // HMRS-02 core assertion: the fingerprint is the first 16 hex chars of SHA-256(IP|UA).
        testCase "FP-3: fingerprint enabled + no header → sessionId is 16-char lowercase hex" <| fun _ ->
            let ctx = makeCtx "127.0.0.1" "hermes/2.0" None
            runMiddleware true ctx
            let sid = getSessionId ctx
            Expect.equal sid.Length 16 "expected exactly 16 hex chars"
            Expect.isTrue
                (sid |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                "expected only lowercase hex chars [0-9a-f] (not uppercase — PITFALL: do not use Convert.ToHexString)"

        // FP-4: Fingerprint enabled + explicit X-Session-Id → header wins over derived fingerprint.
        // Verifies that Hermes Agent's explicit header is always honoured (HMRS-01 / HMRS-02 priority).
        testCase "FP-4: fingerprint enabled + header present → header wins over fingerprint" <| fun _ ->
            let ctx = makeCtx "127.0.0.1" "hermes/2.0" (Some "explicit-id")
            runMiddleware true ctx
            Expect.equal (getSessionId ctx) "explicit-id" "expected explicit header to win over fingerprint"

        // FP-5: Fingerprint is deterministic — same (IP, UA) pair always produces the same prefix.
        // Verifies the SHA-256 hash is stable for the loopback single-client developer scenario.
        testCase "FP-5: fingerprint enabled + same IP/UA → deterministic fingerprint" <| fun _ ->
            let ctxA = makeCtx "127.0.0.1" "hermes/2.0" None
            let ctxB = makeCtx "127.0.0.1" "hermes/2.0" None
            runMiddleware true ctxA
            runMiddleware true ctxB
            Expect.equal (getSessionId ctxA) (getSessionId ctxB)
                "expected deterministic fingerprint for identical IP+UA pair"

        // FP-6: Different User-Agent → different fingerprint.
        // Verifies that UA is included in the hash input (not just IP), so different clients
        // on the same loopback address get distinct sticky buckets.
        testCase "FP-6: fingerprint enabled + different UA → different fingerprint" <| fun _ ->
            let ctxA = makeCtx "127.0.0.1" "hermes/2.0" None
            let ctxB = makeCtx "127.0.0.1" "curl/7.88"  None
            runMiddleware true ctxA
            runMiddleware true ctxB
            Expect.notEqual (getSessionId ctxA) (getSessionId ctxB)
                "expected different fingerprints for different User-Agent values"

        // FP-7: Fingerprint enabled + whitespace-only X-Session-Id → falls back to fingerprint.
        // Verifies the String.IsNullOrWhiteSpace guard correctly ignores "   " as if absent.
        testCase "FP-7: fingerprint enabled + whitespace-only header → falls back to fingerprint" <| fun _ ->
            let ctx = makeCtx "127.0.0.1" "hermes/2.0" (Some "   ")
            runMiddleware true ctx
            let sid = getSessionId ctx
            Expect.equal sid.Length 16 "expected fingerprint (16 hex) when header is whitespace-only"
            Expect.isTrue
                (sid |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                "expected lowercase hex fingerprint for whitespace-only header"

        // FP-8: Null RemoteIpAddress → "unknown" sentinel path; still produces 16-char fingerprint.
        // Covers RESEARCH Pitfall 2: when the server is behind a reverse proxy or in a test context,
        // RemoteIpAddress may be null. The middleware must NOT crash; it uses "unknown" as the IP
        // component of the hash input, producing a fixed-but-non-empty fingerprint.
        testCase "FP-8: fingerprint enabled + null RemoteIpAddress → still produces 16-char fingerprint (unknown|ua)" <| fun _ ->
            let ctx = makeCtx "" "hermes/2.0" None   // empty string → null IPAddress (see makeCtx helper)
            runMiddleware true ctx
            let sid = getSessionId ctx
            Expect.equal sid.Length 16 "expected 16-char fingerprint even when RemoteIpAddress is null"
            Expect.isTrue
                (sid |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                "expected lowercase hex fingerprint when IP is null (uses 'unknown' sentinel)"

    ]
