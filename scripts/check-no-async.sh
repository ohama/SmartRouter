#!/usr/bin/env bash
# scripts/check-no-async.sh
# Enforces no `async {}` in SmartRouter.Core — use task {} CE only.
# Mirrors /Users/ohama/projs/blueCode/scripts/check-no-async.sh.
# Exit 0 if clean; exit 1 on any match; exit 2 if Core dir missing.

set -euo pipefail

CORE_DIR="src/SmartRouter.Core"

if [ ! -d "$CORE_DIR" ]; then
    echo "ERROR: $CORE_DIR does not exist (run from repository root)" >&2
    exit 2
fi

if grep -rn --include='*.fs' 'async {' "$CORE_DIR" ; then
    echo "" >&2
    echo "ERROR: async {} found in $CORE_DIR — use task {} CE instead." >&2
    exit 1
fi

echo "OK: no async {} expressions in $CORE_DIR"
exit 0
