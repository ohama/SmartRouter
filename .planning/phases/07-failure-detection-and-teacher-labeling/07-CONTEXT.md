# Phase 7: Failure Detection + Teacher Labeling - Context

**Gathered:** 2026-05-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Build the offline+in-process data pipeline that converts production routing decisions into labeled training samples for Phase 8's retraining loop. Three injectable Cli adapters: `FailureDetector` reads JSONL decision logs and returns hard-case correlation_ids; `TeacherLabeler` calls 122B with a configurable prompt template, parses the response into a `RoutingLabel` (35B / 122B), enforces 30s timeout + 3x retry + persistent daily cost cap; `HardCaseDatasetWriter` appends labeled samples to `datasets/hard-cases.jsonl` via a Channel + single-writer BackgroundService (mirror of Phase 5 DecisionLogWriter pattern). A CLI command `dotnet run -- --retrain` exercises the pipeline manually; Phase 8's BackgroundService composes the same components on a `PeriodicTimer`. No request-path behavior change — this is a cold-path data pipeline.

</domain>

<decisions>
## Implementation Decisions

### Data-availability path: A+F (build as-spec'd + synthetic seed script)
- `FailureDetector` reads `logs/decisions/*.jsonl` and filters `fallback_used=true` records — returns hard-case correlation_ids ONLY (no prompt text; LOG-01 schema only stores `prompt_hash` for privacy)
- In current state (before Phase 10 lands), `fallback_used` is always false → FailureDetector returns empty sets. **This is correct, not a bug.** Phase 7 ships the components; Phase 10 makes them flow real data.
- `scripts/seed-hard-cases.fsx` (NEW): generates synthetic hard-case dataset entries with known prompt → label pairs (e.g., "explain F# compiler error" → 122B; "what is 2+2" → 35B). Lets Phase 8 test the retraining loop end-to-end before Phase 10 ships real fallback data.
- Operator runs the seed script once after `scripts/download-models.sh`; Phase 8's first retrain reads the seeded dataset.
- When Phase 10 lands, real fallback events naturally append to `datasets/hard-cases.jsonl` and Phase 8 uses both seeded + real samples.

### Where prompt text comes from for TeacherLabeler
- LOG-01 stores only `prompt_hash`, not raw prompt text (Phase 5 locked decision; privacy)
- `TeacherLabeler` needs prompt text to call 122B
- Two paths:
  - **Phase 8's BackgroundService (in-process)**: captures prompt text at request time (it's in `RouterRequest.Messages`); feeds directly to TeacherLabeler. The natural production path.
  - **CLI `--retrain` (offline)**: reads correlation_ids from FailureDetector; cannot recover prompts; either skips with logged warning OR reads from a separate `logs/prompts/{correlation_id}.txt` file (not built in Phase 7; deferred)
- For Phase 7: TeacherLabeler signature is `LabelAsync : prompt: string * correlation_id: string * ct: CancellationToken -> Task<LabelResult>` — prompt text is an INPUT, not something the labeler retrieves. Both BackgroundService (Phase 8) and seed script provide it directly.

### Component placement (Cli adapters)
All three components live in `src/SmartRouter.Cli/Adapters/`:
- `Adapters/FailureDetector.fs` — reads JSONL, filters by predicate
- `Adapters/TeacherLabeler.fs` — HTTP client to 122B, parses response, retries, cost-cap
- `Adapters/HardCaseDatasetWriter.fs` — Channel + single-writer BackgroundService (mirrors `DecisionLogWriter.fs` exactly)

Pure-Core invariant: a small `Core/RetrainingPorts.fs` (NEW) defines the port interfaces (`IFailureDetector`, `ITeacherLabeler`, `IHardCaseDatasetWriter`, `LabelResult`, `RoutingLabel` DU) — pure F# / BCL types. Concrete adapters in Cli.

`.fsproj` order in Core: Domain.fs → Heuristic.fs → MLPorts.fs → ML.fs → Routing.fs → RetrainingPorts.fs → Ports.fs (or whichever spot keeps types Core-resolvable; planner verifies). Ports.fs typically goes last; the new file slots before or after Ports.fs is fine because RetrainingPorts.fs doesn't depend on Ports.fs.

### Teacher prompt: configurable file path
- Config key: `TeacherLabeler:PromptPath` in `appsettings.json`; default `"prompts/teacher-prompt.md"`
- `TeacherLabeler` reads the file at startup (or first call) and caches; logs warning if file missing (skip teacher pass; still functional for Phase 7 testing)
- Phase 7 ships `prompts/teacher-prompt.md` in the repo (copy from `~/projs/smart-router-distillation/prompts/teacher_prompt.md` — committed as the initial default)
- Operator can edit the file without recompiling; restart router to pick up changes (Phase 7 doesn't need watchForChanges; Phase 8 may add it)
- `prompts/` directory is committed (small text files, not gitignored)

### Trigger mechanism: both components + CLI command
- Components are injectable via DI (`IFailureDetector`, `ITeacherLabeler`, `IHardCaseDatasetWriter`)
- Phase 8's BackgroundService composes them on a `PeriodicTimer`
- Phase 7 ships a CLI command: `dotnet run --project src/SmartRouter.Cli -- --retrain` runs once and exits — extracts hard cases, calls teacher, writes dataset, prints summary. Useful for manual operator-driven retraining and CI-friendly testing.
- The CLI command path doesn't have `RouterRequest` context (offline mode), so it operates on whatever is already in `datasets/hard-cases.jsonl` PLUS any new entries the seed script added. It does NOT recover prompt text from logs (that requires Phase 8's in-process path or a Phase 10+ schema amendment).

### TeacherLabeler implementation details
- HTTP client: named `HttpClient("teacher")` registered via `IHttpClientFactory` — same pattern as `QwenUpstreamClient`'s `Qwen35B`/`Qwen122B` named clients
- **Does NOT route through `QueueDispatcher`** — teacher calls would starve real inference requests for the 122B semaphore. Instead, teacher calls go DIRECTLY to the 122B endpoint, bypassing the queue.
- Endpoint: `appsettings.json` `TeacherLabeler:Endpoint` (default = same as Qwen 122B endpoint = `localhost:8001/v1`); operator can swap to a Claude API or different teacher
- Request body: chat/completions with system message = teacher prompt content + user message = the routed prompt
- Response parsing: regex extract `ROUTE_35B` or `ROUTE_122B` token; on malformed/no-match → retry once, then skip with logged warning (don't crash the loop)
- Concurrency: serial per-call (single-threaded teacher loop) within one `--retrain` run; the CLI processes hard cases sequentially. Phase 8's BackgroundService may parallelize later if needed.

### 30s timeout + 3x retry (FAIL-02)
- Use `Microsoft.Extensions.Http.Resilience` (already pinned in Cli .fsproj from STACK research) — `AddResilienceHandler` with retry policy: 3 attempts, exponential backoff 1s/2s/4s, only retry on transient errors (HttpRequestException, 5xx, timeout)
- Do NOT retry on 4xx (logic error; keeps retrying won't help)
- 30s `HttpClient.Timeout` per attempt = 90s total worst case for retries; acceptable for offline pipeline

### Daily cost cap (FAIL-03)
- Persistent file-backed counter at `datasets/teacher-cap-YYYY-MM-DD.json` (UTC date)
- Schema: `{ date: "YYYY-MM-DD", count: 42, max: 1000 }`
- TeacherLabeler increments count before each call; if `count >= max` skip with logged warning + return early
- File rotates at midnight UTC (lazy: check date per call; if changed, write new file with count=0)
- Configurable max in `appsettings.json` `TeacherLabeler:DailyCallCap` (default 1000)
- Survives router restarts (file-backed)
- Concurrency: per-process counter atomicity via `Interlocked.Increment` on in-memory cache; periodic flush to file (every 10 calls or every 60s)

### HardCaseDatasetWriter (FAIL-04)
- File: `datasets/hard-cases.jsonl` (operator-curated dataset; gitignored along with `models/` and `logs/`)
- Channel + single-writer BackgroundService — direct mirror of `DecisionLogWriter.fs` pattern
- `BoundedChannelOptions(1000)` with `BoundedChannelFullMode.Wait` (NOT DropWrite) — losing training data is unacceptable; back-pressure is fine because writes are infrequent (one per labeled hard case)
- Per-line append; `FileShare.None` open exclusively
- Idempotency: dedupe by `(correlation_id, prompt_hash)` — append-time check against an in-memory set seeded from existing file at startup. Prevents double-labeling on rerun.
- Graceful drain on `StopAsync` (mirror Phase 5 LOG-03 pattern)

### Hard-case dataset schema (`datasets/hard-cases.jsonl`)
```jsonc
{
  "schema_version": 1,
  "correlation_id": "abc123...",     // links back to original decision (or "seed-N" for synthetic)
  "prompt": "...",                    // raw prompt text (NOT in production logs; sourced from in-process or seed)
  "prompt_hash": "sha256...",         // matches LOG-01 prompt_hash
  "label": "Qwen35B" | "Qwen122B",    // teacher decision
  "label_source": "teacher" | "seed" | "operator", // where the label came from
  "teacher_response_excerpt": "...",  // first 200 chars of teacher response (for debugging mismatches)
  "labeled_at": "2026-05-08T...Z"
}
```

### Test seam — split into 3 test files
- `tests/SmartRouter.Tests/FailureDetectorTests.fs` — fixture-based JSONL filtering (no HTTP, no fixtures); always runs
- `tests/SmartRouter.Tests/TeacherLabelerTests.fs` — fake-Kestrel teacher (mirrors StreamingTests pattern); cost cap simulation; retry simulation; malformed response handling
- `tests/SmartRouter.Tests/HardCaseDatasetTests.fs` — concurrency safety (10 parallel writes), idempotency (rerun → no dupes), graceful shutdown drain
- All wrapped in `testSequenced` (Console.SetOut races + temp dir hygiene)

### `prompts/teacher-prompt.md` initial content
- Copy from `~/projs/smart-router-distillation/prompts/teacher_prompt.md` to `prompts/teacher-prompt.md` in the repo
- This is committed (small text file, not gitignored)
- Operator can edit; restart router picks up new content (no hot reload in Phase 7)

### `appsettings.json` additions
```json
"TeacherLabeler": {
  "Endpoint": "http://localhost:8001/v1",
  "PromptPath": "prompts/teacher-prompt.md",
  "DailyCallCap": 1000,
  "TimeoutSeconds": 30
},
"HardCaseDataset": {
  "Path": "datasets/hard-cases.jsonl",
  "ChannelCapacity": 1000
}
```

### Synthetic seeding script: `scripts/seed-hard-cases.fsx` (NEW)
F# script (NOT a CLI command — a one-off ops utility). Generates ~30 synthetic hard-case entries with known labels. Run once after Phase 7 ships; Phase 8 reads the resulting dataset.

```fsharp
// scripts/seed-hard-cases.fsx
// Run: dotnet fsi scripts/seed-hard-cases.fsx
let hardCases = [
    "explain F# compiler error type inference", "Qwen122B"
    "what is 2+2", "Qwen35B"
    // ... ~30 pairs covering en/ko/code/short/long/technical/casual
]
// Write JSONL to datasets/hard-cases.jsonl
```

### Constraint inheritance
- Heuristic stays soft-paused; this phase doesn't touch Heuristic.fs
- Pure-Core invariant: `Core/RetrainingPorts.fs` is BCL-only; concrete adapters in Cli
- `task {}` only in Core (not relevant here — RetrainingPorts is interfaces only)
- Per-task atomic commits; `git add <file>` not `-A`
- TreatWarningsAsErrors stays on
- testSequenced wrapper for all new test modules (Console.SetOut + file I/O races)

### Phase 8 forward-link
Phase 8's `RetrainingService : BackgroundService` will:
1. Inject `IFailureDetector`, `ITeacherLabeler`, `IHardCaseDatasetWriter` (Phase 7 ports)
2. On `PeriodicTimer` tick: scan logs OR receive in-process hard cases; call labeler; append to dataset
3. Trigger retraining when dataset count ≥ 500 (Phase 8 constant)

So Phase 7 ships injectable components + CLI utility; Phase 8 wires them into the loop.

### Claude's Discretion
- Exact F# module file split (e.g., put `LabelResult` DU in RetrainingPorts.fs vs a separate file)
- Internal regex pattern for parsing `ROUTE_35B`/`ROUTE_122B` from teacher response (case-insensitive; trim whitespace; allow surrounding text)
- Exponential backoff timing (1s/2s/4s vs 0.5s/1s/2s) — Polly defaults are fine
- Synthetic seeding script content (~30 prompts; mix Korean+English; mix domains)
- Error message wording for cost-cap-reached and malformed-response

</decisions>

<specifics>
## Specific Ideas

- Mirror Phase 5's `DecisionLogWriter` exactly for `HardCaseDatasetWriter` — same Channel + single-writer + graceful drain pattern; just different file + different schema
- Mirror Phase 5's StreamingTests fake-Kestrel pattern for `TeacherLabelerTests` — startTestRouter equivalent for the teacher endpoint
- Use `Microsoft.Extensions.Http.Resilience` `AddResilienceHandler` (Polly-wrapped) for retry policy; this package is already pinned in Cli .fsproj
- Cost-cap counter file: keep schema simple (3 fields); rotation by date is the only logic — no overflow handling needed at v1 scale
- TeacherLabeler must NOT use `QueueDispatcher` — would starve real requests; use named HttpClient bypassing the gate

</specifics>

<deferred>
## Deferred Ideas

- Hot-reload of teacher prompt — Phase 7 just reads at startup; operator restarts to pick up changes
- Multi-teacher consensus (call multiple teachers, majority vote) — out of scope; single 122B teacher for v1
- Active learning (only label uncertain cases — confidence-based selection) — explicit v2 (ML2-01)
- Online labeling (label in real-time on the request path) — explicit v2 (ML2-02)
- Schema versioning for hard-case dataset — `schema_version: 1` field included from start; future evolution handled via Phase 8 reader branching
- Recovering prompt text from logs — would require Phase 5 LOG-01 schema amendment (privacy decision); out of scope
- Operator CLI `add-hard-case` for manually flagging individual decisions — Phase 8 may add; Phase 7 has the synthetic seed script which covers the same gap
- Persistent cost-cap counter resilience to clock skew / system reboot — current design assumes monotonic UTC; acceptable for v1

</deferred>

---

*Phase: 07-failure-detection-and-teacher-labeling*
*Context gathered: 2026-05-08*
