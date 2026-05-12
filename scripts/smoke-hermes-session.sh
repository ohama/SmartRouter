#!/usr/bin/env bash
# scripts/smoke-hermes-session.sh — verify X-Session-Id sticky escalation end-to-end (Tier 1).
#
# Assumes smart-router is ALREADY RUNNING on http://127.0.0.1:4000.
# Does NOT start or stop the router.
# Re-runnable: uses a timestamp-based session ID per run to avoid TTL interference.
#
# Usage:
#   ./scripts/smoke-hermes-session.sh
#
# Exit 0 = PASS (sticky escalation observed on follow-up request).
# Exit 1 = FAIL (DecisionLog did not record routing_reason="sticky_to_122b").
#
# Assertion:
#   Request 1: prompt contains "LLVM" → Hard Rule fires → routes to 122B
#               → SessionStore writes the X-Session-Id key → Qwen122B.
#   Request 2: neutral prompt, same X-Session-Id → cascade detects sticky bucket
#               → DecisionLog row has routing_reason="sticky_to_122b".
#
# Notes:
# - Must run from the router's working directory (relative path to logs/decisions/).
# - Brief sleep after each request lets the channel-buffered DecisionLogWriter flush.
# - Does NOT require Hermes Agent — curl drives both requests directly.
# - Tier 1 (X-Session-Id header) is exercised here. Tier 2 (Hermes Session ID line
#   in the system prompt; --pass-session-id) and Tier 3 (content fingerprint) are
#   covered by unit tests in tests/SmartRouter.Tests/SessionKeyCascadeTests.fs
#   (TC-1..TC-6, Phase 22 Plan 22-03). The smoke script proves end-to-end sticky
#   continuity at the operator level for Tier 1; Tier 2/3 are deterministic
#   transformations covered at the test layer.

set -euo pipefail

PORT=4000
BASE_URL="http://127.0.0.1:${PORT}"
DECISION_LOG="logs/decisions/$(date +%Y-%m-%d).jsonl"
SESSION_ID="smoke-hermes-$(date +%s)"
TRIGGER_PROMPT="diagnose the LLVM compiler segfault in the optimizer"
FOLLOWUP_PROMPT="what was the last thing you said"

echo "[smoke] Session ID: ${SESSION_ID}"
echo "[smoke] Decision log: ${DECISION_LOG}"

if [[ ! -f "${DECISION_LOG}" ]]; then
  echo "[smoke] WARN: DecisionLog file ${DECISION_LOG} does not exist yet — will be created on first request."
fi

# --- Request 1: Hard Rule keyword triggers 122B; SessionStore writes the key → Qwen122B. ---
CID1=$(curl -sf -X POST "${BASE_URL}/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-Session-Id: ${SESSION_ID}" \
  -d "{\"messages\":[{\"role\":\"user\",\"content\":\"${TRIGGER_PROMPT}\"}],\"stream\":false}" \
  -D - -o /dev/null 2>&1 | grep -i "x-correlation-id" | awk '{print $2}' | tr -d '\r')

echo "[smoke] Request 1 correlation_id: ${CID1}"

# Brief wait for DecisionLog async write to flush (channel-buffered; typically <100ms).
sleep 1

# --- Request 2: neutral prompt, same session → sticky_to_122b expected. ---
CID2=$(curl -sf -X POST "${BASE_URL}/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-Session-Id: ${SESSION_ID}" \
  -d "{\"messages\":[{\"role\":\"user\",\"content\":\"${FOLLOWUP_PROMPT}\"}],\"stream\":false}" \
  -D - -o /dev/null 2>&1 | grep -i "x-correlation-id" | awk '{print $2}' | tr -d '\r')

echo "[smoke] Request 2 correlation_id: ${CID2}"

sleep 1

# --- Assert: DecisionLog for CID2 shows routing_reason="sticky_to_122b". ---
if grep -q "\"correlation_id\":\"${CID2}\"" "${DECISION_LOG}" && \
   grep "\"correlation_id\":\"${CID2}\"" "${DECISION_LOG}" | grep -q '"routing_reason":"sticky_to_122b"'; then
  echo "[smoke] PASS: Request 2 routed sticky_to_122b (correlation_id=${CID2})"
  exit 0
else
  echo "[smoke] FAIL: Did not find routing_reason=sticky_to_122b for correlation_id=${CID2}" >&2
  echo "[smoke] DecisionLog tail (last 5 entries):" >&2
  tail -5 "${DECISION_LOG}" >&2 || true
  exit 1
fi
