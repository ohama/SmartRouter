#!/usr/bin/env bash
# scripts/install-launchd.sh — install the smart-router LaunchAgent plist.
#
# Usage:   ./scripts/install-launchd.sh
# Effect:  copies deploy/com.ohama.smart-router.plist into ~/Library/LaunchAgents/.
#          DOES NOT run launchctl load — you must run that manually after
#          reviewing the installed plist.
#
# Why manual launchctl load?
#   The smart-router service runs on the operator's host and integrates
#   with two other long-running services (qwen36-35b, qwen122b). The
#   operator should review the installed plist and decide WHEN to bring
#   the service up. After this script prints the next-steps block, run
#   the load command to start the service.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PLIST_SRC="$REPO_ROOT/deploy/com.ohama.smart-router.plist"
PLIST_DST="$HOME/Library/LaunchAgents/com.ohama.smart-router.plist"

if [[ ! -f "$PLIST_SRC" ]]; then
    echo "[install] ERROR: plist source not found at $PLIST_SRC" >&2
    exit 1
fi

# Sanity-check the plist before installing.
if command -v plutil >/dev/null 2>&1; then
    plutil -lint "$PLIST_SRC"
fi

mkdir -p "$HOME/Library/LaunchAgents"
cp "$PLIST_SRC" "$PLIST_DST"
echo "[install] copied: $PLIST_DST"

cat <<'EOF'

[install] NEXT STEPS — run these manually:

  1) Bring the service up:

       $ launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist

  2) Verify it's reachable (ROADMAP SC#1):

       $ curl -fsS http://127.0.0.1:4000/health

  3) Verify auto-restart on crash (ROADMAP SC#2):

       $ kill -9 "$(pgrep -f SmartRouter.dll)"
       $ sleep 35   # ThrottleInterval=30s + ~5s startup
       $ curl -fsS http://127.0.0.1:4000/health

  4) View logs:

       $ tail -f ~/llm-system/services/logs/smart-router.log
       $ tail -f ~/llm-system/services/logs/smart-router.err

  5) Take it down:

       $ launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist

EOF
