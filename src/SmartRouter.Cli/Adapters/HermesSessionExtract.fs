module SmartRouter.Cli.Adapters.HermesSessionExtract

open System.Text.RegularExpressions
open SmartRouter.Core.Domain

// ── Module-level pre-compiled regex (HSP-03) ─────────────────────────────
//
// The pattern matches Hermes Agent's emission from `--pass-session-id` (see
// ~/projs/smart-router-distillation/hermes-agent/run_agent.py:5764-5766):
//
//     timestamp_line = f"Conversation started: {ts.isoformat()}"
//     if self.session_id:
//         timestamp_line += f"\nSession ID: {self.session_id}"
//
// RegexOptions.Multiline (HSP-04 case d): ^ must match at the start of any
// line in the System message content, not just at the start of the string.
// The `Session ID:` line may appear on line 2 or later.
//
// Case-sensitive (HSP-03): no RegexOptions.IgnoreCase — `session id:` must NOT
// match. Hermes always emits the exact capitalization `Session ID: `.
//
// `\S+` requires at least one non-whitespace character after the colon, so a
// malformed line like `Session ID:   ` (HSP-04 case e) yields Success=false.
//
// Bound at module level via `let private` so the Regex is allocated once at
// module init and reused across all calls — no per-request allocation
// (HSP-03). RegexOptions.Compiled is intentionally NOT set; the JIT-emit cost
// is not justified for this pattern at our request rate (21-RESEARCH.md).
let private sessionIdRx =
    Regex(@"^Session ID:\s*(\S+)", RegexOptions.Multiline)

// ── extractFromSystemPrompt (HSP-01 / HSP-02) ────────────────────────────
//
// Pure synchronous function. Applies only to the FIRST message with
// Role = System (HSP-02); returns None when no such message exists or when
// the System content contains no `Session ID:` line.
//
// Uses Option.bind (not Option.map) — see 21-RESEARCH.md Pitfall 6. The inner
// lambda returns `string option`; with `map` the result would be
// `string option option`, which the type signature rejects.
//
// The `if m'.Success` guard prevents returning `Some ""` for a failed match
// (Regex.Groups returns an empty Group, not an exception). 21-RESEARCH.md
// Pitfall 8.
/// Extracts the Hermes `--pass-session-id` value from the first System message.
/// Returns `Some captured-id` on a match, `None` when no System message exists
/// or when no `Session ID: ` line is present. Pure — no I/O, no mutation.
let extractFromSystemPrompt (req: RouterRequest) : string option =
    req.Messages
    |> List.tryFind (fun m -> m.Role = System)
    |> Option.bind (fun m ->
        let m' = sessionIdRx.Match(m.Content)
        if m'.Success then Some m'.Groups.[1].Value else None)
