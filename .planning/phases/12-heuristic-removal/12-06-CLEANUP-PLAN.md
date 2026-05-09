---
phase: 12-heuristic-removal
plan: 06
type: execute
wave: 4
depends_on: ["12-03", "12-04", "12-05"]
files_modified:
  - scripts/check-routing-isolation.sh (DELETED)
  - src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl (DELETED — stray)
  - src/SmartRouter.Cli/Adapters/CanaryGate.fs
  - src/SmartRouter.Cli/Adapters/CanaryMetrics.fs
  - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
  - src/SmartRouter.Core/CanaryPorts.fs
  - .gitignore
autonomous: true

must_haves:
  truths:
    - "scripts/check-routing-isolation.sh does not exist (Q6)"
    - "src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl does not exist (stray dev artifact)"
    - "Four files (CanaryGate.fs, CanaryMetrics.fs, QwenUpstreamClient.fs, CanaryPorts.fs) have no remaining historical 'heuristic' mentions in comments — wording updated"
    - ".gitignore contains src/**/logs/ pattern (or src/SmartRouter.Cli/logs/ specifically) to prevent future stray log files from being committed"
    - "Final phase-level verification greps return zero hits (per 12-CONTEXT.md §Verification grep checklist)"
    - "dotnet build clean; dotnet test passes; new baseline test count ~61-66 (without embeddings) — recorded in plan summary"
---

<objective>
Final cleanup pass for Phase 12: delete the now-meaningless `scripts/check-routing-isolation.sh` (Q6); delete the stray dev-artifact JSONL under `src/SmartRouter.Cli/logs/decisions/`; update four source files with leftover historical "heuristic" comments; add `.gitignore` rule to prevent future stray log files from `src/**/logs/`; run the full phase-level verification grep checklist.

After this plan: Phase 12 complete. ARCH-01 invariant preserved (Heuristic.fs was BCL-only; no Core deps changed). All routing-decision logic flows through ML algorithm. archive/heuristic-baseline branch and tag untouched (Q7). README and top-level docs (graphify_smart_router_prompt.md, smart-router.md, qwen35-122b-openai-compat-router.md) NOT modified — out of scope per CONTEXT.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/phases/12-heuristic-removal/12-CONTEXT.md
@.planning/phases/12-heuristic-removal/12-heuristic-removal-research.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Delete check-routing-isolation.sh + stray JSONL + .gitignore update</name>
  <files>
    - scripts/check-routing-isolation.sh (DELETED)
    - src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl (DELETED)
    - .gitignore
  </files>
  <action>
**Step 1.** Delete the routing-isolation script:

```bash
git rm scripts/check-routing-isolation.sh
```

**Step 2.** Delete the stray dev JSONL:

```bash
git rm src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl
# also remove the empty parent directory if no other files remain
[ -d src/SmartRouter.Cli/logs/decisions ] && rmdir src/SmartRouter.Cli/logs/decisions
[ -d src/SmartRouter.Cli/logs ] && rmdir src/SmartRouter.Cli/logs
```

(The `rmdir` calls fail silently if directories aren't empty — that's fine.)

**Step 3.** Verify `.gitignore` contains a pattern that prevents `src/**/logs/`. Read current `.gitignore` first:

```bash
grep -n "logs" .gitignore
```

If `logs/` is present at root (`logs/`), it does NOT cover `src/SmartRouter.Cli/logs/`. Add a more specific rule. Append (or insert in alphabetical order if the file is sorted):

```
# Prevent stray local-run log files inside source trees
src/**/logs/
```

If the existing rule already covers it (e.g. `**/logs/`), no change needed.

**Step 4.** Confirm no operator workflow references the deleted script. Quick sanity check:

```bash
grep -rn "check-routing-isolation" . --exclude-dir=.git --exclude-dir=node_modules 2>/dev/null
# expected: 0 hits  (script self-reference and any CI workflow gone)
```
  </action>
  <verify>
```bash
test ! -f scripts/check-routing-isolation.sh && echo "script gone" || echo "STILL PRESENT"
test ! -f src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl && echo "stray gone" || echo "STILL PRESENT"
grep -c "src/\*\*/logs/\|^logs/\|\*\*/logs/" .gitignore
# expected: >= 1
```
  </verify>
</task>

<task type="auto">
  <name>Task 2: Comment cleanup in 4 files</name>
  <files>
    - src/SmartRouter.Cli/Adapters/CanaryGate.fs
    - src/SmartRouter.Cli/Adapters/CanaryMetrics.fs
    - src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs
    - src/SmartRouter.Core/CanaryPorts.fs
  </files>
  <action>
Each of these files has 1 historical comment mentioning heuristic. Update or delete each. Use grep to locate the exact line:

```bash
for f in src/SmartRouter.Cli/Adapters/CanaryGate.fs \
         src/SmartRouter.Cli/Adapters/CanaryMetrics.fs \
         src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs \
         src/SmartRouter.Core/CanaryPorts.fs; do
  echo "=== $f ==="
  grep -n "heuristic\|Heuristic" "$f"
done
```

For each hit, the executor reads the surrounding context and rewrites the comment to remove the heuristic reference while preserving the surrounding meaning. Examples (actual wording may differ):

- `// Heuristic mode: this gate is a no-op` → `// Without canary feature: this gate is a no-op`
- `// matches heuristic baseline routing` → `// matches default routing`
- `// during heuristic-only deployments` → delete the line entirely if it's no longer accurate

These are mechanical edits with no functional impact. The Edit tool is appropriate.
  </action>
  <verify>
```bash
grep -rn "heuristic\|Heuristic" src/SmartRouter.Cli/Adapters/CanaryGate.fs src/SmartRouter.Cli/Adapters/CanaryMetrics.fs src/SmartRouter.Cli/Adapters/QwenUpstreamClient.fs src/SmartRouter.Core/CanaryPorts.fs
# expected: 0 hits across all 4 files
```
  </verify>
</task>

<task type="auto">
  <name>Task 3: Phase-level verification grep checklist</name>
  <files>(read-only)</files>
  <action>
Run the full grep checklist from `12-CONTEXT.md` § Verification grep checklist. Each grep should return 0 hits OR the explicitly allowed count. Failure of any grep means a regression in the cleanup work — investigate and fix in Task 2 or earlier plans.

```bash
echo "=== Routing decision heuristic ==="
grep -rn "applyHeuristic\|scoreComplexity\|canonicalKeywords" src/ tests/ 2>/dev/null
# expected: 0

echo "=== Heuristic DU case (with trailing space to avoid 'Heuristic' module-name false positives) ==="
grep -rn "| Heuristic " src/ tests/ 2>/dev/null
# expected: 0

echo "=== heuristic string literal in source ==="
grep -rn "\"heuristic\"" src/ tests/ 2>/dev/null
# expected: 0  (the only allowed survivors would be in top-level *.md docs which are out of scope)

echo "=== --routing-algorithm flag ==="
grep -rn "routing-algorithm\|--routing-algorithm" src/ tests/ scripts/ 2>/dev/null
# expected: 0

echo "=== Routing.Algorithm config key ==="
grep -rn "Routing\.Algorithm\|Routing:Algorithm" src/SmartRouter.Cli/ tests/SmartRouter.Tests/ 2>/dev/null
# expected: 0

echo "=== Files that should be absent ==="
test ! -f src/SmartRouter.Core/Heuristic.fs && echo "Heuristic.fs absent OK"
test ! -f tests/SmartRouter.Tests/RoutingTests.fs && echo "RoutingTests.fs absent OK"
test ! -f scripts/check-routing-isolation.sh && echo "check-routing-isolation.sh absent OK"

echo "=== Build + test ==="
dotnet build 2>&1 | tail -3
# expected: Build succeeded.

dotnet test tests/SmartRouter.Tests/SmartRouter.Tests.fsproj --no-restore 2>&1 | tail -3
# expected: Passed!  XX passed (XX is the new baseline)
```

If any grep returns non-zero hits, identify the file and either delete the offending content (mechanical) or escalate to the user if it's intentional retention (e.g. a comment that says "this used to be heuristic but isn't anymore" — those should still be deleted; if any meaningful retention surfaces, document in the plan summary).

Record the new baseline test count in the plan SUMMARY: "Phase 12 complete; new baseline = N passed + M ignored (was 86+17 / 93+10 with embeddings)".
  </action>
  <verify>
```bash
echo "Final regression check:"
[ "$(grep -rn 'applyHeuristic\|scoreComplexity\|canonicalKeywords' src/ tests/ 2>/dev/null | wc -l)" -eq 0 ] && echo "OK no routing-heuristic refs" || echo "FAIL"
[ ! -f src/SmartRouter.Core/Heuristic.fs ] && echo "OK Heuristic.fs gone" || echo "FAIL"
[ ! -f tests/SmartRouter.Tests/RoutingTests.fs ] && echo "OK RoutingTests gone" || echo "FAIL"
[ ! -f scripts/check-routing-isolation.sh ] && echo "OK isolation script gone" || echo "FAIL"
```
  </verify>
</task>

</tasks>

<verification>
- [x] check-routing-isolation.sh deleted
- [x] Stray src/SmartRouter.Cli/logs/decisions/2026-05-08.jsonl deleted
- [x] .gitignore covers src/**/logs/
- [x] 4 files (CanaryGate, CanaryMetrics, QwenUpstreamClient, CanaryPorts) have zero heuristic refs in comments
- [x] Phase-level grep checklist all-pass
- [x] dotnet build clean; dotnet test green; new baseline test count recorded in summary
</verification>

---

## Phase 12 deliverables (post-execution)

After all 6 plans execute successfully:

- `Heuristic.fs` deleted; `RoutingReason.Heuristic` DU case + `RoutingConfig.Keywords/ComplexityThreshold` fields removed
- `Routing.Algorithm` config key + `--routing-algorithm` CLI flag removed
- `configureServices` split into `configureRequestPipeline` (full ML) and `configureWithoutMl` (offline + tests)
- `RoutingTests.fs` deleted; 3 MLRoutingTests heuristic-related tests pruned; 3 fixture modules (StreamingTests, LoggingTests, HealthFallbackTests) migrated to test-stub `RoutingAlgorithmRegistration` direct injection
- `scripts/check-routing-isolation.sh` + stray JSONL deleted
- `archive/heuristic-baseline` branch + tag untouched (history preserved per Q7)
- New baseline test count recorded; build green; ARCH-01 preserved

Top-level `*.md` docs (graphify_smart_router_prompt.md, smart-router.md, qwen35-122b-openai-compat-router.md) intentionally NOT modified — out of Phase 12 scope per CONTEXT decision.
