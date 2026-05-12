# Phase 21: HSP + CFP Extraction Primitives — Research

**Researched:** 2026-05-12
**Domain:** F# BCL pure functions — System.Text.RegularExpressions + System.Security.Cryptography.SHA256
**Confidence:** HIGH

---

## Summary

Phase 21 ships two new files in `src/SmartRouter.Cli/Adapters/` — `HermesSessionExtract.fs` and
`ContentFingerprint.fs` — and two new test files in `tests/SmartRouter.Tests/`. Both adapters are
pure BCL-only functions; no async, no DI, no HTTP, no I/O. The design doc
(`~/projs/smart-router-distillation/idea/hermes-session-without-modification.md`) already contains
working F# code for both (§2.2 for HSP, §3.2/3.6 for CFP) that must be adapted to project style.

The EXACT Hermes `Session ID:` emission is confirmed from `run_agent.py:5764-5766`: the line is
always `\nSession ID: {self.session_id}` appended to `timestamp_line`, which itself begins with
`Conversation started: ...`. The colon is always followed by one space before the id. The regex
`^Session ID:\s*(\S+)` with `RegexOptions.Multiline` matches this correctly regardless of the
line's position in the system prompt.

Both adapters closely mirror the SHA-256 pattern already in `CorrelationMiddleware.fs` (lines 69-73)
and `SelfRouter.fs` (lines 168-172). The `use sha = SHA256.Create()` pattern is the only correct
approach — `SHA256.Create()` is NOT thread-safe; sharing an instance is a correctness bug.
For `ContentFingerprint`, the hash object can be a local `use` inside the `compute` function body.

**Primary recommendation:** Implement adapters as standalone F# modules with a module-level
`let private` compiled regex (HSP) and a simple `let compute` that creates/disposes SHA256 inline
(CFP). Follow HardRulesTests.fs as the test template — no `testSequenced` needed (pure functions).

---

## Standard Stack

The established primitives for this domain are all BCL; no NuGet additions.

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Text.RegularExpressions.Regex` | .NET 10 BCL | HSP regex parse | Only correct way for pre-compiled pattern |
| `System.Security.Cryptography.SHA256` | .NET 10 BCL | CFP hash | BCL crypto; already used in CorrelationMiddleware.fs + SelfRouter.fs |
| `System.Text.Encoding.UTF8` | .NET 10 BCL | CFP byte conversion | Required for correct Korean+English hashing |
| `SmartRouter.Core.Domain` | local | `RouterRequest`, `Message`, `MessageRole` | Core domain types — adapters import these |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `RegexOptions.Multiline` | `RegexOptions.Multiline \|\|\| RegexOptions.Compiled` | `Compiled` uses Reflection.Emit at startup; module-level `let private` binding already amortizes one allocation across all calls; Compiled adds JIT overhead at module init — marginal benefit for a pattern matched per-request |
| `Array.map (sprintf "%02x") \|> String.concat ""` | `Convert.ToHexString(bytes).ToLower()` | `Convert.ToHexString` returns UPPERCASE — project convention (from FP-3 test comment line 76) explicitly calls out "do not use Convert.ToHexString" because it produces uppercase hex; use `sprintf "%02x"` pattern exactly as CorrelationMiddleware.fs:72 |
| F# `s.[..3999]` slice | `s.Substring(0, 4000)` | Both are valid F#; REQUIREMENTS.md §CFP-01 specifies `s.[..3999]` literally; planner should use that form |

**Installation:** No new packages. All BCL.

---

## Architecture Patterns

### File Placement (ARCH-01 invariant)

Both new adapters go in `SmartRouter.Cli.Adapters` — NOT in `SmartRouter.Core`. The ROADMAP.md
§Architectural Invariants is explicit: "Both new files live in `SmartRouter.Cli.Adapters` — NOT in
`SmartRouter.Core`. Core receives no new files in v2.1."

The design doc (§2.2) shows the module header as `SmartRouter.Core.Routing.HermesSessionExtract` —
this is WRONG for the project. The correct header for v2.1 is:
- `module SmartRouter.Cli.Adapters.HermesSessionExtract`
- `module SmartRouter.Cli.Adapters.ContentFingerprint`

### Recommended Project Structure

New files:
```
src/SmartRouter.Cli/Adapters/
├── HermesSessionExtract.fs   ← NEW (Phase 21, Plan 21-01)
├── ContentFingerprint.fs     ← NEW (Phase 21, Plan 21-02)
└── [existing files unchanged]

tests/SmartRouter.Tests/
├── HermesSessionExtractTests.fs   ← NEW (Plan 21-01)
├── ContentFingerprintTests.fs     ← NEW (Plan 21-02)
└── [existing files unchanged]
```

### .fsproj Compile Entry Order

**SmartRouter.Cli.fsproj**: Both new adapters import `SmartRouter.Core.Domain` (for `RouterRequest`,
`Message`, `MessageRole`). Domain.fs is in a separate project (`SmartRouter.Core`) which Cli already
references as a ProjectReference, so there is no within-project ordering concern. The safest insertion
point is immediately after `Adapters/SessionStore.fs` (line 27) and before `Adapters/DecisionLogger.fs`
(line 28), mirroring the pattern for Session-adjacent functionality:

```xml
<Compile Include="Adapters/SessionStore.fs" />
<Compile Include="Adapters/HermesSessionExtract.fs" />   <!-- Phase 21 HSP -->
<Compile Include="Adapters/ContentFingerprint.fs" />      <!-- Phase 21 CFP -->
<Compile Include="Adapters/DecisionLogger.fs" />
```

Alternatively, both can be inserted anywhere after the `<ProjectReference>` to `SmartRouter.Core`
is declared and before `CompositionRoot.fs`. They have NO dependencies on other Cli adapters —
they only import `SmartRouter.Core.Domain`.

**SmartRouter.Tests.fsproj**: New test files must be added BEFORE `RouterTests.fs` (the entrypoint).
Append after `HermesFingerprintTests.fs` (line 49):

```xml
<Compile Include="HermesFingerprintTests.fs" />
<Compile Include="HermesSessionExtractTests.fs" />   <!-- Phase 21 HSP tests -->
<Compile Include="ContentFingerprintTests.fs" />      <!-- Phase 21 CFP tests -->
<Compile Include="RouterTests.fs" />
```

**RouterTests.fs rootTests list**: Append two entries at the bottom of the list (before the closing
`]`), mirroring Phase 20's `HermesFingerprintTests.tests` entry:

```fsharp
SmartRouter.Tests.HermesFingerprintTests.tests          // Phase 20 (Plan 20-01)
SmartRouter.Tests.HermesSessionExtractTests.tests       // Phase 21 (Plan 21-01)
SmartRouter.Tests.ContentFingerprintTests.tests         // Phase 21 (Plan 21-02)
```

### Pattern 1: HermesSessionExtract — Module-Level Pre-Compiled Regex (HSP-03)

**What:** Module-level `let private` binding for the compiled regex. F# module-level bindings are
executed once at module initialization. `new Regex(...)` at module level amortizes the pattern
compilation cost. Per HSP-03, this is the required approach.

**When to use:** Any time the requirement says "pre-compiled at module init; no per-request allocation."

**Example (adapted from design doc §2.2, corrected for project style):**

```fsharp
// Source: .planning/REQUIREMENTS.md HSP-01, HSP-03
// Verified against run_agent.py:5764-5766 for exact "Session ID: " format
module SmartRouter.Cli.Adapters.HermesSessionExtract

open System.Text.RegularExpressions
open SmartRouter.Core.Domain

// Module-level pre-compiled regex (HSP-03): one Regex allocation at module init,
// reused across all calls. RegexOptions.Multiline makes ^ match line-start
// anywhere in the string, not just string-start.
// Case-sensitive (HSP-03): no RegexOptions.IgnoreCase — "session id:" must NOT match.
let private sessionIdRx = Regex(@"^Session ID:\s*(\S+)", RegexOptions.Multiline)

/// Returns the captured session_id from the first System message's content,
/// or None when no System message exists or no "Session ID: " line is found.
/// Pure function — no I/O, no state mutation. (HSP-01, HSP-02)
let extractFromSystemPrompt (req: RouterRequest) : string option =
    req.Messages
    |> List.tryFind (fun m -> m.Role = System)
    |> Option.bind (fun m ->
        let m' = sessionIdRx.Match(m.Content)
        if m'.Success then Some m'.Groups.[1].Value else None)
```

### Pattern 2: ContentFingerprint — SHA-256 with `use` and hex encoding (CFP-01)

**What:** Inline `use sha = SHA256.Create()` inside the function body. SHA256 implements IDisposable;
`use` ensures proper disposal. Pattern exactly mirrors `CorrelationMiddleware.fs:69-72` and
`SelfRouter.fs:168-172`.

**When to use:** Every SHA256 computation. Do NOT share a SHA256 instance across calls — not thread-safe.

**Example (adapted from design doc §3.6, corrected for project style):**

```fsharp
// Source: .planning/REQUIREMENTS.md CFP-01, CFP-02
// Source: src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs:69-73 (SHA256 pattern)
module SmartRouter.Cli.Adapters.ContentFingerprint

open System.Security.Cryptography
open System.Text
open SmartRouter.Core.Domain

// truncate: spec-exact (CFP-01): s.[..3999] is inclusive in F# (chars 0..3999 = 4000 chars).
// "if s.Length > 4000 then s.[..3999] else s" matches REQUIREMENTS.md CFP-01 literal.
let private truncate (s: string) = if s.Length > 4000 then s.[..3999] else s

/// Compute a 16-character lowercase hex prefix of SHA-256(truncate(system) + "|||" + truncate(firstUser)).
/// Always returns a valid 16-char hex string — no option wrapper (CFP-02).
/// Deterministic, pure, no I/O (CFP-03).
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
```

### Anti-Patterns to Avoid

- **Module-level `SHA256` binding**: Do NOT write `let private sha = SHA256.Create()` at module level.
  SHA256 is stateful and not thread-safe. Concurrent calls would corrupt each other's hash state.
  Use `use sha = SHA256.Create()` inside the function body (matches `CorrelationMiddleware.fs:69`).
- **Omitting `RegexOptions.Multiline`**: Without Multiline, `^` only matches the start of the entire
  string. Hermes' system prompt has `Session ID:` on the second or third line — without Multiline
  the regex will fail to match, breaking HSP in all production cases.
- **Using `RegexOptions.IgnoreCase`**: HSP-03 explicitly requires case-sensitive matching.
  `"session id: ..."` (lowercase) must NOT match — the requirement says "matches Hermes' exact
  emission `Session ID: `".
- **Using `Convert.ToHexString()`**: Produces uppercase hex. FP-3 test (HermesFingerprintTests.fs:76)
  explicitly documents this pitfall: "do not use Convert.ToHexString". The `sprintf "%02x"` pattern
  used in CorrelationMiddleware.fs:72 produces lowercase.
- **Truncating by byte count instead of char count**: CFP-01 says `truncate(s) = if s.Length > 4000 then s.[..3999] else s`.
  `String.Length` in .NET is char count (UTF-16 code units), not byte count. The spec truncates
  on character boundary. UTF-8 encoding happens AFTER truncation (`Encoding.UTF8.GetBytes(key)`).
  This is correct — do not try to truncate bytes.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Case-sensitive multiline regex | Custom line-splitting + string comparison | `Regex(@"...", RegexOptions.Multiline)` | Handles CR+LF vs LF portably; `^` semantics under Multiline are well-specified |
| Lowercase hex from byte array | Bit-shifting loop | `Array.map (sprintf "%02x") \|> String.concat ""` | Project-established pattern (CorrelationMiddleware.fs:72, SelfRouter.fs:171); avoids uppercase pitfall |
| Regex option parsing | manual parsing | `Regex.Match().Groups.[1].Value` | `Groups.[1]` is the first capture group — always correct for `(\S+)` pattern |

**Key insight:** Both adapters have zero external dependencies beyond BCL. The entire implementation
fits in ~15 lines each. The risk is not complexity — it is subtle correctness issues in options flags,
casing, and disposal patterns. Research confirms the exact patterns to use.

---

## Common Pitfalls

### Pitfall 1: `RegexOptions.Multiline` scope — `^` also changes `$`

**What goes wrong:** `RegexOptions.Multiline` makes BOTH `^` and `$` match at line boundaries, not
just string boundaries. This is intentional for `^` (we need it to match mid-string line starts), but
be aware `$` also changes meaning. Since the pattern `^Session ID:\s*(\S+)` has no `$`, this is not
a problem for HSP. Note for future patterns.

**Why it happens:** .NET Multiline flag semantics per MSDN.

**How to avoid:** For HSP, the pattern is `^Session ID:\s*(\S+)` — no `$` — so no issue arises.

**Warning signs:** Only becomes a problem if future patterns add `$`.

### Pitfall 2: SHA256 thread-safety — NEVER share instances

**What goes wrong:** `SHA256.Create()` returns a `SHA256` managed object that is NOT thread-safe.
If a module-level `let private sha = SHA256.Create()` is used, concurrent requests corrupt each
other's hash computation, producing wrong hashes silently.

**Why it happens:** .NET crypto primitives maintain internal state between `ComputeHash` calls.

**How to avoid:** Always `use sha = SHA256.Create()` inside the function body. See
`CorrelationMiddleware.fs:69` — the comment on line 59 says "Pitfall 1: use SHA256 per-request
(not thread-safe to share instances)." For `ContentFingerprint.compute`, the SHA256 is created,
used once, and disposed within a single synchronous call — no sharing possible.

**Warning signs:** Wrong/changing hashes for the same input in concurrent load test scenarios.

### Pitfall 3: F# string slice `s.[..3999]` is INCLUSIVE on both ends

**What goes wrong:** `s.[..3999]` in F# produces characters at indices 0 through 3999 inclusive —
that is 4000 characters. This is correct per CFP-01 spec: "if s.Length > 4000 then s.[..3999]".
The condition `s.Length > 4000` means a string of exactly 4000 characters is NOT truncated
(it is returned as-is).

**Why it happens:** F# slice notation `a.[lo..hi]` is inclusive on hi, unlike Python's exclusive upper bound.

**How to avoid:** The condition `if s.Length > 4000 then s.[..3999] else s` is spec-exact. A test
case with exactly 4001 chars should produce a 4000-char truncation; 4000 chars should pass through.

**Warning signs:** Off-by-one in truncation test — check boundary cases (3999, 4000, 4001 chars).

### Pitfall 4: CFP empty-input edge case — "|||" is a valid hash input

**What goes wrong:** When both system and firstUser are absent, `key = "" + "|||" + "" = "|||"`.
CFP-02 requires this returns `Some valid-16-hex` — it does, because SHA-256("|||") is deterministic.
The concern is if someone writes `if key = "|||" then ... special case`. Do NOT special-case.

**Why it happens:** Developers sometimes want to handle "nothing to hash" specially.

**How to avoid:** Per CFP-02: "the function always returns a valid 16-hex string (no `option` wrapper)".
Let SHA-256 run on the `|||` separator — it produces a stable value. Tests must include the empty-message
case to verify this.

**Warning signs:** Any `if key.Trim() = "|||" then "0000000000000000"` pattern — wrong.

### Pitfall 5: HSP test string with Windows CR+LF vs Unix LF in raw string literals

**What goes wrong:** In test code, if a multi-line system prompt is constructed with `\n` in
F# strings, it works correctly. If someone accidentally uses Windows line endings (`\r\n`) in the
literal content, the `^` multiline match still works (RegexOptions.Multiline treats both `\n`
and `\r\n` as line boundaries in .NET). But `\r` at end of group capture for `(\S+)` would
fail because `\S` (non-whitespace) matches `\r`. This is relevant ONLY if test data uses `\r\n`.

**Why it happens:** On Windows, strings copied from editors may include `\r\n`. On Mac, less likely.

**How to avoid:** Use `\n` in F# test string literals. The `\S` in the regex specifically excludes `\r`
and `\n`. The actual Hermes emission (run_agent.py:5766) uses Python f-string `\n` — Unix LF.

**Warning signs:** Test passes on Mac, fails on Windows (irrelevant for this Mac-only project, but
worth knowing).

### Pitfall 6: `Option.bind` vs `Option.map` in HSP implementation

**What goes wrong:** Using `Option.map` when `Option.bind` is needed. `Option.map` wraps the result
in another `Some`, producing `Some (Some "id")` instead of `Some "id"`.

**Why it happens:** `List.tryFind` returns `string option`; the lambda that applies regex returns
`string option`; `Option.map` would produce `string option option`.

**How to avoid:** The design doc §2.2 shows `Option.bind` — this is correct. The F# compiler will
catch the type mismatch if `map` is used instead of `bind`, since the function signature is
`RouterRequest -> string option` not `RouterRequest -> string option option`.

### Pitfall 7: Regex `Groups.[1]` indexing — group 0 is the full match

**What goes wrong:** Accessing `m'.Groups.[0].Value` returns the entire match (`Session ID: abc123`),
not the captured group. `m'.Groups.[1].Value` is the first parenthesized group (`abc123`).

**Why it happens:** .NET Regex groups are 1-indexed for named/explicit groups; group 0 is always
the full match.

**How to avoid:** Use `m'.Groups.[1].Value` as shown in §2.2 of the design doc.

### Pitfall 8: `m'.Success` check before accessing `Groups`

**What goes wrong:** If `sessionIdRx.Match(m.Content).Success` is false and the code accesses
`Groups.[1].Value` anyway, it returns `""` (empty string), not an exception. But `if m'.Success then
Some m'.Groups.[1].Value else None` is the correct pattern — it prevents returning `Some ""` for
a failed match.

**Why it happens:** .NET Match.Groups returns empty Group on failed match, not an exception.

**How to avoid:** Always guard with `if m'.Success` before accessing groups, then return `None`
for non-matching case. Design doc §2.2 shows the correct guard.

### Pitfall 9: `malformed-line-no-value` test case — `Session ID:` with no trailing non-whitespace

**What goes wrong:** The HSP-04 test case (e) "malformed Session ID: line with no value returns None"
tests `"Session ID:"` or `"Session ID:   "` (all whitespace after colon). The pattern `\s*(\S+)`
requires at least ONE non-whitespace character after optional whitespace. A line of
`"Session ID:"` or `"Session ID:   "` will NOT match `\S+`, so `Match.Success = false`.

**Why it happens:** `\S+` requires 1+ non-whitespace chars. `\s*` before it only matches 0+ whitespace.
If content after colon is only whitespace (or nothing), `\S+` fails — `Success = false`.

**How to avoid:** Test case (e) should use content like `"Session ID:\n"` or `"Session ID:   \n"`
and assert `None` is returned. This is correct per spec.

### Pitfall 10: `Expecto.Expect` naming for `string option` results

**What goes wrong:** `Expect.isSome` and `Expect.isNone` are correct for `option` types. For
string equality, use `Expect.equal (result |> Option.get) "expected" "msg"` after asserting
`isSome`. Do not use `Expect.equal (Some "x") result` — argument order in Expecto is
`(actual, expected, message)`.

**Why it happens:** Expecto's Expect functions take `(actual, expected, message)`, not `(expected, actual, message)`.

**How to avoid:** Pattern from HardRulesTests.fs:38: `Expect.isSome result "msg"`, then
`let d = result.Value` to unwrap. For HSP, use the same approach:
```fsharp
let result = extractFromSystemPrompt req
Expect.isSome result "should have found session id"
Expect.equal result.Value "20260512T1530_a1b2c3" "captured group value"
```

---

## Code Examples

Verified patterns from existing project files:

### SHA256 + lowercase hex (established project pattern)

```fsharp
// Source: src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs:69-73
use sha = System.Security.Cryptography.SHA256.Create()
let bytes = System.Text.Encoding.UTF8.GetBytes(input)
let hash  = sha.ComputeHash(bytes)
let hex   = hash |> Array.map (sprintf "%02x") |> String.concat ""
hex.Substring(0, 16)
```

### Regex pre-compiled at module level (pattern for HSP-03)

```fsharp
// Established pattern for module-level compiled regex in F#
// (no direct prior example in project; standard F# idiom)
let private myRx = Regex(@"^pattern", RegexOptions.Multiline)
// Called per-request — no allocation; Regex object is stateless for Match
```

### RouterRequest construction in tests (from HardRulesTests.fs:11-21)

```fsharp
// Source: tests/SmartRouter.Tests/HardRulesTests.fs:11-21
let private mkReq content =
    { Messages       = [ { Role = User; Content = content } ]
      ModelOverride  = None
      Task           = None
      Stream         = false
      Temperature    = None
      TopP           = None
      MaxTokens      = None
      CorrelationId  = ""
      SessionId      = ""
      UnknownFields  = Map.empty }
```

For HSP tests, helpers need both System and User messages. The pattern extends as:

```fsharp
let private mkReqWithSystem systemContent userContent =
    { Messages = [ { Role = System; Content = systemContent }
                   { Role = User;   Content = userContent } ]
      ModelOverride = None; Task = None; Stream = false
      Temperature = None; TopP = None; MaxTokens = None
      CorrelationId = ""; SessionId = ""; UnknownFields = Map.empty }
```

### Expecto test list structure (no testSequenced needed for pure functions)

```fsharp
// Source: tests/SmartRouter.Tests/HardRulesTests.fs:31-124 (pattern)
// Pure functions → no Console.SetOut or temp files → no testSequenced wrapper needed
let tests : Test =
    testList "HermesSessionExtractTests" [
        testCase "extracts session id when present" <| fun () ->
            // ...
        testCase "returns None when no system message" <| fun () ->
            // ...
    ]
```

Compare with `HermesFingerprintTests.fs:51` which uses `testSequenced <| testList ...` because it
invokes middleware with `DefaultHttpContext`. Phase 21 tests are pure — no `testSequenced` required.

---

## Test Design

### HermesSessionExtractTests.fs — 5 cases (HSP-04)

| Case | Label | Test input | Expected output |
|------|-------|------------|-----------------|
| (a) | match-when-present | System message contains `"Conversation started: ...\nSession ID: 20260512T1530_a1b2c3\nModel: qwen-35b\nProvider: custom"` | `Some "20260512T1530_a1b2c3"` |
| (b) | no-match-when-absent | System message has no `Session ID:` line | `None` |
| (c) | no-match-when-no-system-message | `Messages = [ { Role = User; Content = "..." } ]` (only User) | `None` |
| (d) | multiline-still-matches | Session ID on line 3 of system (not line 1) | `Some "..."` |
| (e) | malformed-line-no-value | System message contains `"Session ID:   \n"` (whitespace only after colon) | `None` |

**Case-sensitivity guard (HSP-03)**: Add a 6th implicit check within case (a) or a separate case:
`"session id: abc"` (all lowercase) should return `None`. The requirements say "case-sensitive",
but no explicit HSP-04 test letter is assigned — the planner should include this.

### ContentFingerprintTests.fs — 6 cases (CFP-04)

| Case | Label | Test input | Expected output |
|------|-------|------------|-----------------|
| (a) | determinism | Same `RouterRequest` twice | `result1 = result2` |
| (b) | uniqueness | Two requests differing by one char in system or firstUser | `result1 <> result2` |
| (c) | truncation | System message of 4001 chars | No exception; returns 16-char hex |
| (d) | empty-message | `Messages = []` (no system, no user) | Valid 16-char lowercase hex (hash of `"|||"`) |
| (e) | Korean+English mixed UTF-8 | System = `"안녕하세요 hello"`, firstUser = `"테스트 test"` | Valid 16-char lowercase hex |
| (f) | 16-char lowercase | Any call | `result.Length = 16` and all chars in `[0-9a-f]` |

For (d), the expected output is the first 16 chars of SHA-256("|||") encoded as lowercase hex.
Pre-compute and hardcode in test: `"8ca4d4e3c19a1c14"` (if correct) or compute at test time and
assert length+charset rather than hardcoding the exact value (simpler, still correct for spec).

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Per-request `new Regex(...)` | Module-level `let private` binding | Always best practice in .NET | Zero per-call allocation for pattern compilation |
| `Convert.ToHexString()` | `Array.map (sprintf "%02x") \|> String.concat ""` | Project established in Phase 20 (FP-3 pitfall note) | Correct lowercase hex — uppercase from `Convert.ToHexString` would break spec |
| SHA256 shared instance | `use sha = SHA256.Create()` per call | Phase 20 (FP-1 pitfall note in CorrelationMiddleware) | Thread safety; no corruption on concurrent requests |

**Deprecated/outdated:**
- `Regex.IsMatch()` then separate `Regex.Match()` — two passes. Use a single `Match()` call and check `.Success`.
- `String.IsNullOrEmpty` guard on `req.Messages` — not needed; `List.tryFind` on `[]` returns `None` safely.

---

## Open Questions

Things that the planner should resolve or be aware of:

1. **Test module naming convention**
   - What we know: existing tests use `XxxTests.fs` (HardRulesTests, HermesFingerprintTests).
   - Recommendation: `HermesSessionExtractTests.fs` and `ContentFingerprintTests.fs`.
   - Planner decision: confirm these names in plans 21-01 and 21-02.

2. **Pre-computed SHA-256("|||") for test case (d)**
   - What we know: CFP-04 (d) tests empty system + empty user → hash of `"|||"`.
   - What's unclear: should the test hardcode the expected 16-char prefix, or compute it
     dynamically and assert only length+charset?
   - Recommendation: compute it dynamically in the test itself (call `compute` twice, assert
     equality) rather than hardcoding a magic string. Keeps test correct if implementation
     changes encoding accidentally.

3. **`testSequenced` wrapper needed?**
   - What we know: HardRulesTests.fs (pure functions) has no `testSequenced`. HermesFingerprintTests.fs
     (DefaultHttpContext) wraps with `testSequenced`.
   - What's clear: Phase 21 adapters are pure functions, no Console/IO/DI. No `testSequenced` needed.
   - Planner decision: confirm this — executor should NOT add `testSequenced`.

4. **Additional case-sensitivity test for HSP**
   - What we know: HSP-03 requires case-sensitivity. HSP-04 lists 5 cases (a)-(e), none explicitly
     names the case-insensitive non-match case.
   - Recommendation: add a 6th test case `"case-insensitive-does-not-match"` to fully verify
     HSP-03. This is a natural extension of (a) and takes 3 lines.

5. **`Regex.Match` vs `Regex.IsMatch` + separate `Match`**
   - Design doc §2.2 correctly uses `sessionIdRx.Match(m.Content)` and checks `.Success`.
   - Planner decision: single `Match()` call is the correct pattern (two-pass is wasteful). This
     is already encoded in the recommended implementation above.

---

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/projs/smart-router/.planning/REQUIREMENTS.md` — HSP-01..04, CFP-01..04 specs
- `/Users/ohama/projs/smart-router/.planning/ROADMAP.md` — Phase 21 goal, plans, success criteria, architectural invariants
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/CorrelationMiddleware.fs:69-73` — SHA256 + hex pattern (project-established)
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/Adapters/SelfRouter.fs:168-172` — SHA256 `use` disposal pattern
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/HardRulesTests.fs` — test pattern (pure functions, no testSequenced, RouterRequest construction)
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/RouterTests.fs` — rootTests list + compile order requirements
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/SmartRouter.Tests.fsproj` — .fsproj compile entry order
- `/Users/ohama/projs/smart-router/src/SmartRouter.Cli/SmartRouter.Cli.fsproj:20-69` — Cli adapter compile order
- `/Users/ohama/projs/smart-router/src/SmartRouter.Core/Domain.fs` — RouterRequest, Message, MessageRole types
- `/Users/ohama/projs/smart-router-distillation/idea/hermes-session-without-modification.md §2.2, §3.2, §3.6` — reference implementation code
- `/Users/ohama/projs/smart-router-distillation/hermes-agent/run_agent.py:5764-5766` — EXACT Hermes `Session ID:` emission format (ground truth)

### Secondary (MEDIUM confidence)
- `/Users/ohama/projs/smart-router/tests/SmartRouter.Tests/HermesFingerprintTests.fs` — SHA256 hex output format pitfall (FP-3 comment line 76: "do not use Convert.ToHexString")
- `.planning/STATE.md`, `.planning/PROJECT.md` — ARCH-01 (Cli adapter placement), ARCH-02 (no async), Expecto conventions

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all BCL; patterns confirmed in 3 existing project files
- Architecture: HIGH — ARCH-01 placement locked by ROADMAP.md + STATE.md; .fsproj order determined by reading fsproj file
- Pitfalls: HIGH — SHA256 thread-safety pitfall documented in CorrelationMiddleware.fs comment line 59; hex format pitfall documented in FP-3 comment; Multiline ^ semantics verified against .NET docs; slice inclusive/exclusive confirmed from F# spec behavior
- Test design: HIGH — pattern confirmed from HardRulesTests.fs; testSequenced need confirmed by comparison with HermesFingerprintTests.fs
- Hermes emission format: HIGH — directly read from run_agent.py:5764-5766 (ground truth source code)

**Research date:** 2026-05-12
**Valid until:** 2026-06-12 (stable BCL; Hermes run_agent.py format would need re-check if Hermes upgrades)
