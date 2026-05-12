module SmartRouter.Cli.Adapters.ContentFingerprint

open System.Security.Cryptography
open System.Text
open SmartRouter.Core.Domain

// ── Truncate helper (CFP-01) ─────────────────────────────────────────────
//
// Spec-exact: REQUIREMENTS.md CFP-01 says
//   truncate(s) = if s.Length > 4000 then s.[..3999] else s
//
// F# slice `s.[..3999]` is INCLUSIVE on both ends — indices 0..3999 yield
// exactly 4000 characters. A 4000-char string passes through unchanged
// (the condition is strict `> 4000`). A 4001-char string is truncated to
// 4000 chars. .NET `String.Length` is char count (UTF-16 code units), not
// byte count — truncation happens on the character boundary BEFORE UTF-8
// encoding (21-RESEARCH.md Pitfall 3).
let private truncate (s: string) : string =
    if s.Length > 4000 then s.[..3999] else s

// ── compute (CFP-01 / CFP-02 / CFP-03) ───────────────────────────────────
//
// Returns a 16-character lowercase hex prefix of the SHA-256 digest of
//   key = truncate(system) + "|||" + truncate(firstUser)
//
// Per CFP-02: always returns a valid 16-hex string — no `option` wrapper.
// When both system and firstUser are absent, key = "|||" and SHA-256 still
// produces a stable, deterministic value (CFP-04 case d). DO NOT special-case
// the empty-message branch (21-RESEARCH.md Pitfall 4).
//
// SHA256 lifecycle (21-RESEARCH.md Pitfall 2):
//   - `use sha = SHA256.Create()` — per-call instance, disposed at scope exit.
//   - SHA256 is NOT thread-safe; a module-level shared instance would corrupt
//     concurrent hash computations silently. Matches the project pattern in
//     CorrelationMiddleware.fs:69 and SelfRouter.fs:168.
//
// Hex encoding (21-RESEARCH.md §Alternatives Considered + Phase 20 pitfall):
//   - `Array.map (sprintf "%02x") |> String.concat ""` produces LOWERCASE.
//   - DO NOT use `Convert.ToHexString` — it produces UPPERCASE, which fails
//     CFP-04 case (f). Matches the project pattern in CorrelationMiddleware.fs:72
//     and SelfRouter.fs:171.
/// Compute a 16-character lowercase hex content fingerprint for the request's
/// conversation prefix. Pure: no I/O, no DI, no mutation of req. Deterministic
/// for any given (system, firstUser) pair. Tier 3 of the v2.1 session cascade.
let compute (req: RouterRequest) : string =
    let system =
        req.Messages
        |> List.tryFind (fun m -> m.Role = System)
        |> Option.map (fun m -> m.Content)
        |> Option.defaultValue ""
    let firstUser =
        req.Messages
        |> List.tryFind (fun m -> m.Role = User)
        |> Option.map (fun m -> m.Content)
        |> Option.defaultValue ""
    let key = truncate system + "|||" + truncate firstUser
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key))
    let hex = bytes |> Array.map (sprintf "%02x") |> String.concat ""
    hex.Substring(0, 16)
