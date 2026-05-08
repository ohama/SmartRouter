#!/usr/bin/env bash
# scripts/check-routing-isolation.sh
# Enforces zero cross-imports between Heuristic.fs and ML.fs (ML-04).
# Mirrors scripts/check-no-async.sh shape.
# Exit 0 if clean; exit 1 on any violation; exit 2 if Core dir missing.

set -euo pipefail

CORE_DIR="src/SmartRouter.Core"
HEURISTIC_FS="${CORE_DIR}/Heuristic.fs"
ML_FS="${CORE_DIR}/ML.fs"

if [ ! -d "$CORE_DIR" ]; then
    echo "ERROR: $CORE_DIR does not exist (run from repository root)" >&2
    exit 2
fi

FAIL=0

if [ -f "$HEURISTIC_FS" ]; then
    if grep -nE 'open SmartRouter\.Core\.ML|SmartRouter\.Core\.ML\.' "$HEURISTIC_FS" ; then
        echo "" >&2
        echo "ERROR: $HEURISTIC_FS references ML module — zero cross-imports required (ML-04)" >&2
        FAIL=1
    fi
fi

if [ -f "$ML_FS" ]; then
    if grep -nE 'open SmartRouter\.Core\.Heuristic|SmartRouter\.Core\.Heuristic\.' "$ML_FS" ; then
        echo "" >&2
        echo "ERROR: $ML_FS references Heuristic module — zero cross-imports required (ML-04)" >&2
        FAIL=1
    fi
fi

if [ "$FAIL" -eq 1 ]; then
    exit 1
fi

echo "OK: routing modules isolated (Heuristic.fs and ML.fs have zero cross-imports)"
exit 0
