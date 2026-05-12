module SmartRouter.Tests.ContentFingerprintTests

open Expecto
open SmartRouter.Core.Domain
open SmartRouter.Cli.Adapters.ContentFingerprint

// ── Test helpers ──────────────────────────────────────────────────────────

/// Build a RouterRequest with a System message + first User message.
let private mkReq (systemContent: string) (userContent: string) : RouterRequest =
    { Messages       = [ { Role = System; Content = systemContent }
                         { Role = User;   Content = userContent } ]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = ""
      UnknownFields  = Map.empty }

/// Build a RouterRequest with no messages at all (Messages = []).
/// Both system and firstUser resolve to "" — key = "|||".
let private mkReqEmpty () : RouterRequest =
    { Messages       = []
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = ""
      UnknownFields  = Map.empty }

/// Hex char predicate — true iff c is a lowercase hex digit [0-9a-f].
let private isLowerHex (c: char) : bool =
    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

/// Assert: output is exactly 16 chars and every char is lowercase hex.
let private assertHexFormat (result: string) =
    Expect.equal result.Length 16
        "fingerprint must be exactly 16 characters"
    Expect.isTrue (result |> Seq.forall isLowerHex)
        (sprintf "all chars must be in [0-9a-f]; got %A" result)

// ── Tests (CFP-04 cases a-f) ─────────────────────────────────────────────

let tests : Test =
    testList "ContentFingerprintTests" [

        // (a) determinism — same input twice → identical output
        testCase "(a) determinism: identical input produces identical output" <| fun () ->
            let req = mkReq "You are a router." "hello world"
            let r1 = compute req
            let r2 = compute req
            Expect.equal r1 r2 "two calls with same input must produce same fingerprint"
            assertHexFormat r1

        // (b) uniqueness — single-char change → different output
        testCase "(b) uniqueness: single-char change in system produces different output" <| fun () ->
            let r1 = compute (mkReq "You are a router." "same user prompt")
            let r2 = compute (mkReq "You are a router!" "same user prompt")
            Expect.notEqual r1 r2 "single char in system should produce different output"

        testCase "(b) uniqueness: single-char change in firstUser produces different output" <| fun () ->
            let r1 = compute (mkReq "common system" "hello world")
            let r2 = compute (mkReq "common system" "hello worle")
            Expect.notEqual r1 r2 "single char in firstUser should produce different output"

        // (c) truncation — >4000-char input handled without exception, hash uses first 4000 chars
        testCase "(c) truncation: 4001-char system handled without exception; truncation collapses tail diffs" <| fun () ->
            let baseStr = String.replicate 4000 "a"   // exactly 4000 'a' chars
            let req4000 = mkReq baseStr "user"
            let req4001A = mkReq (baseStr + "X") "user"   // 4001 chars; X is truncated away
            let req4001B = mkReq (baseStr + "Y") "user"   // 4001 chars; Y is truncated away
            // Should not throw. All three produce valid 16-hex outputs.
            let r4000  = compute req4000
            let r4001A = compute req4001A
            let r4001B = compute req4001B
            assertHexFormat r4000
            assertHexFormat r4001A
            assertHexFormat r4001B
            // Since both 4001-char inputs truncate to the same 4000-char prefix, their
            // outputs MUST equal each other AND equal the 4000-char baseline.
            Expect.equal r4001A r4001B
                "4001-char inputs that differ only in the truncated tail must produce the same fingerprint"
            Expect.equal r4001A r4000
                "truncation must collapse to the same fingerprint as the 4000-char prefix alone"

        // (d) empty-message — Messages = [] → key = "|||" → valid 16-hex
        // Per 21-RESEARCH.md Open Question 2: assert determinism + format,
        // NOT a hardcoded SHA-256("|||") magic constant.
        testCase "(d) empty-message: no messages produces deterministic valid 16-hex" <| fun () ->
            let r1 = compute (mkReqEmpty ())
            let r2 = compute (mkReqEmpty ())
            Expect.equal r1 r2
                "empty Messages → deterministic output (SHA-256(\"|||\")); no special-case branch"
            assertHexFormat r1

        // (e) Korean+English mixed UTF-8 — exercises Encoding.UTF8.GetBytes
        testCase "(e) Korean+English mixed UTF-8 produces valid 16-hex" <| fun () ->
            let req = mkReq "안녕하세요 hello" "테스트 test"
            let r = compute req
            assertHexFormat r
            // Determinism with multi-byte UTF-8 too
            let r' = compute req
            Expect.equal r r' "Korean+English determinism"
            // Sanity: differ from ASCII-only baseline
            let rAscii = compute (mkReq "hello world" "test")
            Expect.notEqual r rAscii
                "Korean content should produce different fingerprint than ASCII"

        // (f) 16-char lowercase format — explicit format assertion
        testCase "(f) output is exactly 16 chars of lowercase hex" <| fun () ->
            // Multiple varied inputs — every output must satisfy the format.
            let inputs =
                [ mkReq "" ""
                  mkReq "a" "b"
                  mkReq "long system prompt with spaces and punctuation!" "what is 2+2?"
                  mkReqEmpty () ]
            for req in inputs do
                let r = compute req
                assertHexFormat r
    ]
