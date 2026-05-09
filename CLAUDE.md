# Claude project instructions — smart-router

This file is read by Claude Code at the start of every session in this repository. It encodes project-wide rules that any code change must respect.

## README.md sync rule (mandatory)

**Whenever code is modified in a way that changes any of the following observable behaviors, `README.md` MUST be updated in the same change set:**

1. The application's stated purpose or scope (§ 1 What This Is)
2. The architecture or data flow (§ 2 Architecture; § 6 ML Feedback Loop; § 6.3 Canary)
3. The public surface (Quickstart commands, CLI flags, environment variables)
4. The compile / build / deploy procedure (§ 4 Quickstart; § 12 Operations)
5. The endpoints (paths, methods, request/response shapes; § 8 Endpoints)
6. The routing pipeline behavior (stages, task table, ML threshold; § 5 Routing Pipeline)
7. The DecisionLog JSONL schema (§ 9.1 — schema_version, field names, field meanings)
8. The operational log behavior (sinks, paths, rotation, retention, levels, output template; § 9.6–9.9 Log files / Reading the operational log / Log levels and filtering / Log parameters reference)
9. Configuration keys in `appsettings.json` (§ 7 Configuration Reference)
10. Hermes / Graphify integration assumptions (§ 10 Hermes Integration; § 11 Graphify Integration)
11. Operator workflows — launchd setup, canary promote/rollback, manual retrain, threshold tuning (§ 12 Operations)
12. Troubleshooting recipes (§ 13 Troubleshooting)

**Why:** smart-router runs as an unattended launchd service. The README is the single document an operator (or a future contributor, or a fresh Claude session) reads to understand what's running. README drift from code is the most expensive mistake we can ship — a wrong recipe in § 13 wastes hours; a stale endpoint table in § 8 silently misroutes requests in tooling.

**How to apply:**

- Before completing any source-code change that touches one of the 12 areas above, search `README.md` for the affected nouns/verbs/keys. Update the relevant section in the same commit (or, if you split into multiple commits per the project's per-task atomic-commit convention, the README update commit must precede the merge/push).
- If the change is purely internal (refactor, test reorganization, dependency bump that doesn't change observable behavior), README does not need updating. The 12-area test is the gate.
- If you are unsure whether a change crosses into one of the 12 areas, it does — update README. False positives are cheap.
- New sections / subsections are fine — keep the table of contents (§ "Table of Contents") in sync.
- Section numbering is meaningful: prefer adding a sub-section (e.g., § 9.10) over renumbering existing sections, so anchor links from external docs and earlier commits stay valid.
- The README is operator-facing. Avoid implementation jargon when the operator-relevant phrasing is clearer; link to internal docs (`documentation/howto/*.md`) for detail-deep technical context. The "Further Reading" section is where howto links live.

**Concrete examples of changes that REQUIRE README updates:**

- Adding/removing/renaming an `appsettings.json` key → § 7 Configuration Reference
- Adding/removing/renaming a CLI flag (e.g., `--retrain`, `--log-level`) → § 4 Quickstart, § 12 Operations
- Adding/removing/renaming an HTTP endpoint or changing its semantics → § 8 Endpoints
- Changing the DecisionLog JSONL schema (adding a field, renaming, changing types) → § 9.1
- Changing the operational log file paths, naming pattern, rotation policy, retention, or output template → § 9.6–9.9
- Changing the routing decision criteria (e.g., adjusting ML.Threshold default, restoring heuristic, adding a new algorithm) → § 5
- Changing the launchd plist contents that affect operator workflow → § 12.1
- Changing canary promote/rollback semantics or auto-rollback gate → § 6.3, § 12.3

**Concrete examples that do NOT require README updates:**

- Refactoring a private function in `src/SmartRouter.Cli/Adapters/`
- Updating a NuGet package version where the public API and observable behavior are unchanged
- Adding internal tests
- Renaming an internal F# module that has no public surface
- Adjusting a comment or doc-string

**On planning artifacts (`.planning/`):**

The README sync rule applies to README.md only. Planning artifacts (`.planning/STATE.md`, `.planning/ROADMAP.md`, `.planning/REQUIREMENTS.md`, `.planning/phases/**/*.md`) follow their own GSD workflow — they are updated by the gsd-planner / gsd-executor / gsd-verifier agents, not by this rule. If a phase delivers a behavior that the README documents, both the planning artifacts AND the README must be updated, but they are independent updates with independent rationales.

## Issue handling rules (mandatory)

When responding to a GitHub issue (or any tracked bug report), commit-number traceability is REQUIRED. Three patterns:

### 1. Issue resolved — reply with commit number(s)

After fixing an issue and pushing the fix, post a comment on the issue that includes the commit hash(es) that resolved it. Use the short SHA (7+ chars). If multiple commits contributed, list all of them.

```
Fixed in commit abc1234 (and follow-up def5678).

Root cause: <one-sentence summary>
Fix: <what changed>

Closing this issue.
```

The commit number is non-negotiable — it gives the reporter a way to verify the fix landed in the branch they're tracking and lets future readers cross-reference the issue with the diff.

### 2. Unable to reproduce — request detailed reproduction info

If the issue cannot be reproduced from the original report, do NOT close it as "works for me" or guess at the cause. Instead, post a comment requesting the specific information needed to reproduce. Be concrete about what's missing.

Reproduction-info checklist (ask for whichever items are missing):

- Exact router version / commit hash the reporter is running (`dotnet SmartRouter.Cli.dll --version` if such a flag exists, or the contents of `~/llm-system/services/smart-router/.git/HEAD`)
- macOS version + arch (`sw_vers` + `uname -m`)
- mlx_lm.server version + which Qwen model files are loaded
- Exact request body that triggers the issue (full JSON, sanitized of any sensitive content)
- Whether the issue reproduces with `--log-level=debug` enabled (and the relevant debug-level log lines)
- The `correlation_id` of a failing request (from `logs/decisions/YYYY-MM-DD.jsonl` or operational log)
- The relevant excerpt from `logs/operational/smart-router-{Date}.log` (or `~/llm-system/services/logs/smart-router.err` for launchd) covering the failing request
- Whether `/health` and `/stats` show anything anomalous at the time of failure
- The `appsettings.json` in use (with any custom keys highlighted)

Phrase the comment as a direct request, not a refusal. The goal is to unblock the reporter, not to push back.

### 3. Issue already resolved — reply with the resolving commit number

If the issue describes a problem that has been fixed by a prior commit (e.g., the reporter was on an older version, or a separate fix coincidentally addressed the same bug), do NOT just close as "stale". Find the commit that resolved it (`git log --grep`, `git blame`, or recall from session context) and post a comment with that commit number plus a short explanation.

```
Already resolved in commit abc1234 (Phase N — <plan name>).

The change <one-sentence summary of what changed>. If you upgrade to that
commit or later, this issue should not reproduce. Please verify and reopen
if you still see the behavior on the current master.

Closing.
```

The commit number is again non-negotiable — without it, the reporter cannot tell whether their version contains the fix.

### Workflow when working an issue

1. Read the issue. Identify the version / commit the reporter is on (if known).
2. Try to reproduce against current master. If reproduces → fix → commit → push → reply with pattern 1.
3. If does NOT reproduce on master:
   - Check git log for an obvious resolving commit (`git log --grep="<keyword>"`, `git log --all -- <relevant-file>`). If found, reply with pattern 3.
   - Otherwise, reply with pattern 2 (request reproduction info).
4. Never close an issue as "works for me" without either a resolving-commit reference or a specific information request.

## Other project conventions

These are documented elsewhere; this section is a quick reference.

- **Architecture** — Hexagonal: `SmartRouter.Core` is BCL-only (no Serilog, no HttpClient, no ASP.NET Core, no Microsoft.ML). Adapters in `SmartRouter.Cli` only. CI grep enforces. (ARCH-01)
- **F# style** — `task {}` only (no `async {}` literals). `scripts/check-no-async.sh` enforces. (ARCH-02)
- **Stream separation** — Serilog → stderr only; stdout reserved for application output. (OBS-04)
- **Tests** — Expecto with explicit `rootTests` list (not auto-discovery; PITFALL-26). All test modules wrapped in `testSequenced` for Console.SetOut + temp dir hygiene.
- **Commits** — Per-task atomic commits with format `{type}({phase}-{plan}): {task-name}`. Never `git add -A` or `git add .` — stage files individually.
- **Build** — `TreatWarningsAsErrors=true` is project-wide. Match exhaustiveness over DUs is enforced by the F# compiler.
- **History** — `archive/heuristic-baseline` branch + `v0.5-heuristic-baseline` tag preserved for the pre-Phase-12 heuristic-routing snapshot. Do not delete or force-push these.
