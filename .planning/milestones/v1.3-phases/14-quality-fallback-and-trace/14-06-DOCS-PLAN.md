---
phase: 14-quality-fallback-and-trace
plan: 06
type: execute
wave: 6
depends_on: ["14-04", "14-05"]
files_modified:
  - README.md
  - .planning/docs/cold-start-request-flow.md
  - .planning/docs/distillation-fallback-design-references.md
autonomous: true

must_haves:
  truths:
    - "README.md has new §5.5 'Quality fallback (35B → 122B)' explaining the path, configuration keys, and streaming exemption"
    - "README.md §7 Configuration Reference has new `Routing.QualityFallback` subsection table (Enabled/MinResponseLength/BadKeywords)"
    - "README.md §9 Debugging has new §9.10 'Trace logging' subsection explaining --trace-responses + logs/trace/ + prompt_uid grep workflow"
    - "README.md §12 Operations has `--cold-start` flag entry alongside other CLI flags (--retrain, --log-level)"
    - "README.md §9.1 DecisionLog field reference's routing_reason row mentions the new \"fallback_to_122b\" value"
    - ".planning/docs/cold-start-request-flow.md updated to remove 'this retry path doesn't exist' caveat — quality fallback is now real (Phase 14); add cross-reference to new §"
    - ".planning/docs/distillation-fallback-design-references.md gap-table updated: distillation 의도 vs smart-router 구현이 이제 일치 (Phase 14 implementation)"
    - "Per CLAUDE.md README sync rule: areas 5 (routing pipeline), 7 (DecisionLog schema), 8 (operational logging), 11 (operator workflows) all touched and corresponding README sections updated"
---

<objective>
Update README + planning docs to reflect Phase 14 reality. Quality fallback is now implemented; cold-start CLI is live; trace logging is documented for operators.

Per CLAUDE.md sync rule, this plan satisfies the 12-area gate by editing README sections covering: routing pipeline (§5.5 new), Configuration Reference (§7 QualityFallback subsection), Debugging (§9.10 trace logging), Operations (§12 --cold-start), DecisionLog schema (§9.1 routing_reason new value).
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/14-quality-fallback-and-trace/14-CONTEXT.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: README.md updates (5 sections)</name>
  <files>README.md</files>
  <action>
Edit `README.md` with 5 targeted Edit operations:

**Edit 1 — §5.5 Quality fallback (NEW subsection)**: After §5.4 "Tuning ML routing", add:

```markdown
### 5.5 Quality fallback (35B → 122B retry)

When a non-streaming chat-completion request is routed to 35B and the response fails a configured quality heuristic, smart-router automatically retries the same request on 122B and forwards 122B's response to the client. This is the distillation design's "Failure = Gold Data" pattern (Phase 14).

**Trigger conditions** (all must hold):
- Stage 3 ML classifier routes to 35B (initial decision)
- 35B returns HTTP 200 with a body
- `Routing.QualityFallback.Enabled = true`
- The 35B response body fails `isBadResponse` check:
  - Length < `MinResponseLength` (default 30 chars), OR
  - Contains any of `BadKeywords` (default `["TODO", "I think"]`)
- 122B is reachable per HealthService

**When fallback fires**:
- Final response = 122B's response (35B's bad response is discarded)
- DecisionLog: `target = "Qwen122B"`, `routing_reason = "fallback_to_122b"`, `fallback_used = true`
- TraceLog (if enabled): captures both 35B and 122B response excerpts joined by `prompt_uid`

**When fallback doesn't fire**:
- Streaming requests (`stream=true`) — chunks already shipped; cannot retract
- 122B unreachable — graceful degradation; 35B response forwarded as-is
- 122B retry also fails — graceful degradation; 35B response forwarded as-is

**Tuning** (`appsettings.json:Routing.QualityFallback`):

```json
"Routing": {
  ...,
  "QualityFallback": {
    "Enabled": true,
    "MinResponseLength": 30,
    "BadKeywords": [ "TODO", "I think", "I don't know", "cannot help" ]
  }
}
```

Add domain-specific keywords your team observes in low-quality responses. Set `Enabled: false` to disable the path entirely (kill switch).

**Cost note**: When fallback fires, total latency = 35B latency + 122B latency. For frequent fallbacks, the 122B usage savings (the original ML routing benefit) is partially eroded. Monitor fallback rate via `/stats` (future enhancement) or grep:

```bash
grep '"routing_reason":"fallback_to_122b"' logs/decisions/$(date +%F).jsonl | wc -l
```
```

**Edit 2 — §7 Configuration Reference, add QualityFallback table**: After the existing `Routing.ML` table:

```markdown
### Routing.QualityFallback

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Routing.QualityFallback.Enabled` | bool | `true` | Master switch; false disables quality fallback entirely |
| `Routing.QualityFallback.MinResponseLength` | int | `30` | Responses shorter than this trigger fallback |
| `Routing.QualityFallback.BadKeywords` | string[] | `["TODO", "I think"]` | Substrings that mark a response as bad (case-sensitive) |
```

**Edit 3 — §9.1 DecisionLog field reference**: Update the `routing_reason` row to mention `fallback_to_122b`:

Find the line:
```
| `routing_reason` | string | `explicit_model:{alias}`, `explicit_task:{TaskType}`, `default`, `ml`, `fallback_to_35b`, or a compound like `ml;upstream_error`, `ml;cancelled`, `ml;stream_error` |
```

Replace with:
```
| `routing_reason` | string | `explicit_model:{alias}`, `explicit_task:{TaskType}`, `default`, `ml`, `fallback_to_35b` (Phase 10 — 122B unreachable), `fallback_to_122b` (Phase 14 — 35B response failed quality check), or a compound like `ml;upstream_error`, `ml;cancelled`, `ml;stream_error` |
```

**Edit 4 — §9 add §9.10 Trace logging (NEW)**: After §9.9 "Log parameters reference":

```markdown
### 9.10 Trace logging — `--trace-responses` flag (operator debugging)

For end-to-end debugging of routing decisions and fallback behavior, the router has an optional trace log that captures intermediate request state. Operator opts in via CLI flag:

```bash
dotnet run --project src/SmartRouter.Cli -- --trace-responses
# or in production deployment:
dotnet SmartRouter.Cli.dll --trace-responses
```

When enabled, each non-streaming chat-completion request appends one row to `logs/trace/YYYY-MM-DD.jsonl`. Schema (12 fields):

| Field | Description |
|-------|-------------|
| `schema_version` | Currently 1 |
| `correlation_id` | UUID per request (matches DecisionLog) |
| `prompt_uid` | First 12 hex of `prompt_hash` (stable per prompt content) |
| `prompt_hash` | Full SHA-256 (matches DecisionLog) |
| `prompt_excerpt` | First 200 chars of concatenated prompt messages |
| `initial_target` | First routing decision (`Qwen35B` or `Qwen122B`) |
| `initial_response_excerpt` | First 500 chars of the initial-target response (null if no fallback) |
| `fallback_kind` | `"quality"` (Phase 14), `"availability"` (Phase 10), or null |
| `final_target` | Model that actually produced the response sent to client |
| `final_response_excerpt` | First 500 chars of the final response |
| `total_latency_ms` | End-to-end time from request start to last byte written |
| `timestamp` | ISO 8601 UTC |

**Operator workflow — grep by prompt UID**:

```bash
# Compute UID from your prompt text
PROMPT="explain recursion in Python"
UID=$(echo -n "$PROMPT" | sha256sum | cut -c1-12)

# Find the trace row(s) for that prompt
jq "select(.prompt_uid == \"$UID\")" logs/trace/$(date +%F).jsonl

# What did 35B say? What did 122B say? Did fallback fire?
jq "select(.prompt_uid == \"$UID\") | {initial_target, fallback_kind, initial_response_excerpt, final_target, final_response_excerpt}" logs/trace/$(date +%F).jsonl
```

**Privacy / size note**: prompts and response excerpts are stored in plaintext. Default state is **off** (file not created). Enable only for diagnostics, not for production-default. The 200/500 char truncation limits storage but doesn't fully sanitize PII — operator's responsibility to manage retention.

**Retention**: Phase 14 ships without auto-cleanup of `logs/trace/`. Operator manually prunes or relies on Phase 13 LogRetentionService extension (future).
```

**Edit 5 — §12 Operations, add `--cold-start` entry**: Within the existing CLI flags table or sub-section:

```markdown
### 12.X CLI flags

| Flag | Effect |
|------|--------|
| `--retrain` | Run offline retrain pipeline (Phase 7); exits after completion |
| `--log-level=LEVEL` | Set Serilog minimum level (verbose/debug/information/warning/error/fatal) |
| `--trace-responses` | Enable trace logging to `logs/trace/<date>.jsonl` (Phase 14) |
| `--cold-start` | Backup existing models/router.zip + datasets/ with timestamp suffix; ensureDummyModel generates fresh model on this same startup (Phase 14) |

**`--cold-start` example**:

```bash
dotnet run --project src/SmartRouter.Cli -- --cold-start
# Logs:
#   [INF] Cold-start: backed up 2 file(s) with timestamp=20260510-153422; files=[...]
#   [WRN] No ML classifier model found at models/router.zip. Generating random dummy 1024-dim model.
#   [INF] Dummy classifier model written to models/router.zip
#   [INF] SmartRouter starting ...

# To recover:
mv models/router.zip.cold-start-backup-20260510-153422 models/router.zip
# Then restart router.
```
```

(Adjust `12.X` to whatever subsection number is appropriate — likely insert as §12.5 or §12.6 depending on existing structure.)
  </action>
  <verify>
```bash
grep -c "QualityFallback\|fallback_to_122b\|--trace-responses\|--cold-start\|prompt_uid" README.md
# expected: >= 8 (each new term appears multiple times)
grep -c "Routing\.QualityFallback" README.md
# expected: >= 2 (subsection heading + table)
grep -c "logs/trace/" README.md
# expected: >= 2
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Update .planning/docs/ — remove 'doesn't exist' caveats</name>
  <files>
    - .planning/docs/cold-start-request-flow.md
    - .planning/docs/distillation-fallback-design-references.md
  </files>
  <action>
**Edit 1** — `.planning/docs/cold-start-request-flow.md` § "사전 정정". The current text says:

> 이런 **재시도 (escalation) 경로는 smart-router 에 존재하지 않는다.**

Replace with a Phase 14 update:

```markdown
## 사전 정정 — 사용자 멘탈 모델 vs 실제 동작 (Phase 14 업데이트)

질문: "35B 에서 처리 안 되어서 122B 로 다시 처리되는 경우"

**Phase 14 부터 이 경로가 실제로 존재한다.** 이전 (Phase 13까지) 에는 없었고 이 문서가 처음 작성됐을 당시 상황.

이제 (Phase 14):
- non-streaming 요청만 — 35B response 가 quality 기준 미달이면 자동으로 122B 로 retry
- streaming 요청은 여전히 단일 routing 결정만 — 디자인상 chunk retract 불가
- 두 fallback 방향 모두 코드에 있음:
  - 122B unreachable → 35B (Phase 10 REL-01..04, `routing_reason=fallback_to_35b`)
  - 35B quality 미달 → 122B (Phase 14, `routing_reason=fallback_to_122b`)

자세한 설명: README §5.5 "Quality fallback".
```

(쓰여있는 두 시나리오 (A simple → 35B / B complex → 122B) 는 그대로 유효 — 분류기 결정 시점의 outcome. 새 시나리오 C: 35B 가 결정됐지만 응답이 bad → 122B retry 도 추가 가능; 시나리오 부분에 추가하거나 README §5.5 만 reference.)

**Edit 2** — `.planning/docs/distillation-fallback-design-references.md` §2 비교 표 + §3 추측 부분. 현재 "smart-router 의 실제 구현 — 무엇이 다른가" 표 의 "Quality check 의 존재" 행:

```
| Quality check 의 존재 | `isBadResponse` 함수 (TODO / 길이 < 30 / "I think") | **없음** — 35B 응답을 그대로 forward |
```

Replace `**없음** — 35B 응답을 그대로 forward` 를:
```
**Phase 14 부터 implement** — `Adapters/QualityCheck.fs` 의 `isBadResponse` 함수 (configurable via `Routing.QualityFallback.{Enabled, MinResponseLength, BadKeywords}`). non-streaming 만 적용
```

§3 "왜 gap 이 있는가" 의 마지막 문단을 갱신:

> 다시 말해 같은 단어 "fallback" 이 두 시스템에서 다른 개념을 가리킨다:
> - distillation: **품질-기반** quality fallback — "응답 보고 결정"
> - smart-router: **가용성-기반** availability fallback — "헬스 보고 결정"

After (Phase 14):
```markdown
다시 말해 (Phase 14 이전엔) 같은 단어 "fallback" 이 두 시스템에서 다른 개념을 가리켰다:
- distillation 디자인: **품질-기반** quality fallback — "응답 보고 결정"
- smart-router (Phase 13 까지): **가용성-기반** availability fallback — "헬스 보고 결정"

**Phase 14 부터 smart-router 가 distillation 디자인의 quality fallback 도 구현.** 두 fallback 이 공존:
- `routing_reason = "fallback_to_35b"` — 122B unreachable (Phase 10 / availability)
- `routing_reason = "fallback_to_122b"` — 35B response bad (Phase 14 / quality)

DecisionLog `fallback_used=true` 는 둘 다 trigger. routing_reason 으로 종류 구별. Loop B (Phase 7-8) retraining 은 둘 다 hard-case 신호로 사용. 이로써 distillation 의 "Failure = Gold Data" 사상이 실제로 동작.
```

§7 표 의 "implementation 위치" 의 `**Phase 14+ candidate**` 도 `**Phase 14 — DONE**` 으로 교체.
  </action>
  <verify>
```bash
grep -c "Phase 14" .planning/docs/cold-start-request-flow.md
# expected: >= 2
grep -c "Phase 14" .planning/docs/distillation-fallback-design-references.md
# expected: >= 4
grep -c "doesn't exist\|존재하지 않는다" .planning/docs/cold-start-request-flow.md
# expected: 0 — Phase 14 부터 존재하므로
```
  </verify>
</task>

</tasks>

<verification>
- [x] README §5.5 새 quality fallback subsection
- [x] README §7 QualityFallback config table
- [x] README §9.1 routing_reason 에 fallback_to_122b 추가
- [x] README §9.10 trace logging operator guide
- [x] README §12 --cold-start CLI flag entry
- [x] cold-start-flow doc 의 "doesn't exist" 정정
- [x] distillation-fallback-design-references doc 의 gap 표 갱신 (이제 implement 됨)
- [x] CLAUDE.md sync rule 12-area 게이트 충족
</verification>
