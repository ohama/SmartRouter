#!/usr/bin/env bash
# scripts/deploy.sh — publish smart-router into ~/llm-system/services/smart-router/
# (See deploy/com.ohama.smart-router.plist for the launchd entry that runs the published DLL.)
#
# Usage:   ./scripts/deploy.sh
# Effect:  publishes SmartRouter.Cli framework-dependent into the install dir
#          and copies the operator-editable assets (appsettings, prompts).
#
# Idempotent: re-running this script overwrites the published binaries
# in-place. It does NOT run launchctl load — see scripts/install-launchd.sh.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
INSTALL_DIR="$HOME/llm-system/services/smart-router"
LOGS_DIR="$HOME/llm-system/services/logs"
DOTNET="/opt/homebrew/bin/dotnet"

echo "[deploy] repo:        $REPO_ROOT"
echo "[deploy] install dir: $INSTALL_DIR"
echo "[deploy] logs dir:    $LOGS_DIR"

# Pre-flight: launchd's WorkingDirectory + StandardOutPath dirs MUST exist
# before launchctl load; launchd will silently exit with status 78 otherwise
# (research §1 pitfall #3 + #4).
mkdir -p "$INSTALL_DIR" \
         "$INSTALL_DIR/models/embed" \
         "$INSTALL_DIR/datasets" \
         "$INSTALL_DIR/prompts" \
         "$INSTALL_DIR/logs/decisions" \
         "$LOGS_DIR"

# Verify dotnet is at the expected absolute path. launchd ProgramArguments
# uses this exact path; if it has moved, the plist is stale.
if [[ ! -x "$DOTNET" ]]; then
    echo "[deploy] ERROR: dotnet not found at $DOTNET" >&2
    echo "[deploy]        Update deploy/com.ohama.smart-router.plist if dotnet has moved." >&2
    exit 1
fi
echo "[deploy] dotnet:      $($DOTNET --version)"

# Framework-dependent publish (CONTEXT L4 — NOT trimmed, runs against the installed .NET runtime).
# ML.NET reflection breaks under trimming; do NOT enable trimming or single-file
# bundling unless you have audited every reflection site.
"$DOTNET" publish "$REPO_ROOT/src/SmartRouter.Cli/SmartRouter.Cli.fsproj" \
    -c Release \
    -o "$INSTALL_DIR"

# Copy operator-editable assets that dotnet publish does not fold in.
# (appsettings.json IS folded in by the .fsproj <None Update> entry, but
# we re-copy here so the operator can edit it in-place at the install
# location without it being overwritten on next publish.)
cp "$REPO_ROOT/src/SmartRouter.Cli/appsettings.json" "$INSTALL_DIR/appsettings.json"

if [[ -f "$REPO_ROOT/prompts/teacher-prompt.md" ]]; then
    cp "$REPO_ROOT/prompts/teacher-prompt.md" "$INSTALL_DIR/prompts/teacher-prompt.md"
fi

# If embed models were already downloaded into the repo, mirror them across.
if [[ -d "$REPO_ROOT/models/embed" ]]; then
    cp -R "$REPO_ROOT/models/embed/." "$INSTALL_DIR/models/embed/" 2>/dev/null || true
fi

echo "[deploy] published to $INSTALL_DIR"
echo "[deploy] next step:   ./scripts/install-launchd.sh"
