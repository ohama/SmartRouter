---
phase: 11-deployment-documentation
plan: 02
type: execute
wave: 2
depends_on: [11-01]
files_modified:
  - deploy/com.ohama.smart-router.plist
  - scripts/deploy.sh
  - scripts/install-launchd.sh
autonomous: true

must_haves:
  truths:
    - "deploy/com.ohama.smart-router.plist exists and matches the operator's qwen36-35b/qwen122b plist convention exactly (4-space XML, KeepAlive plain bool, ThrottleInterval=30, RunAtLoad=true)"
    - "The plist's ProgramArguments uses absolute paths only — /opt/homebrew/bin/dotnet and /Users/ohama/llm-system/services/smart-router/SmartRouter.dll (PATH is not inherited by launchd)"
    - "The plist's WorkingDirectory is /Users/ohama/llm-system/services/smart-router so relative paths in appsettings.json (models/, datasets/, logs/, prompts/) resolve correctly"
    - "The plist's StandardOutPath and StandardErrorPath point to /Users/ohama/llm-system/services/logs/smart-router.{log,err} (matches qwen plist log convention)"
    - "scripts/deploy.sh runs dotnet publish framework-dependent (-c Release, NO --self-contained, NO PublishTrimmed) and copies appsettings.json + prompts + embed models into the install dir"
    - "scripts/deploy.sh creates the WorkingDirectory (~/llm-system/services/smart-router) and the logs directory (~/llm-system/services/logs/) with mkdir -p before publish"
    - "scripts/install-launchd.sh copies the plist into ~/Library/LaunchAgents/ — it does NOT run launchctl load itself (manual UAT step per L21)"
    - "The plist passes plutil -lint validation (no XML errors)"
    - "Both shell scripts pass shellcheck (or equivalent) basic correctness checks: set -euo pipefail at the top; no unquoted $HOME; no relative paths"
  artifacts:
    - path: "deploy/com.ohama.smart-router.plist"
      provides: "launchd LaunchAgent definition for the smart-router service"
      min_lines: 28
      contains: ["com.ohama.smart-router", "/opt/homebrew/bin/dotnet", "/Users/ohama/llm-system/services/smart-router/SmartRouter.dll", "<key>KeepAlive</key>", "<true/>", "<key>ThrottleInterval</key>", "<integer>30</integer>", "<key>RunAtLoad</key>", "<key>WorkingDirectory</key>"]
    - path: "scripts/deploy.sh"
      provides: "Operator-runnable deploy script: publish + install binaries"
      min_lines: 25
      contains: ["set -euo pipefail", "dotnet publish", "-c Release", "/Users/ohama/llm-system/services/smart-router", "mkdir -p"]
    - path: "scripts/install-launchd.sh"
      provides: "Operator-runnable plist installer: copy into LaunchAgents (does NOT run launchctl load)"
      min_lines: 15
      contains: ["set -euo pipefail", "deploy/com.ohama.smart-router.plist", "~/Library/LaunchAgents/", "launchctl load -w"]
  key_links:
    - from: "deploy/com.ohama.smart-router.plist"
      to: "/opt/homebrew/bin/dotnet"
      via: "ProgramArguments[0] absolute path (PATH not inherited)"
      pattern: "<string>/opt/homebrew/bin/dotnet</string>"
    - from: "deploy/com.ohama.smart-router.plist"
      to: "appsettings.json relative paths (models/, datasets/, logs/)"
      via: "WorkingDirectory key resolves CWD before process startup"
      pattern: "<string>/Users/ohama/llm-system/services/smart-router</string>"
    - from: "scripts/deploy.sh"
      to: "Phase 11-01 published binary (SmartRouter.dll)"
      via: "dotnet publish -c Release -o ~/llm-system/services/smart-router/"
      pattern: "dotnet publish.*-c Release.*-o.*smart-router"
    - from: "scripts/install-launchd.sh"
      to: "deploy/com.ohama.smart-router.plist"
      via: "cp into ~/Library/LaunchAgents/"
      pattern: "cp.*deploy/com\\.ohama\\.smart-router\\.plist.*LaunchAgents"
---

<objective>
Ship the launchd LaunchAgent definition and the two deploy scripts the
operator needs to install, run, auto-restart, and supervise smart-router on
the operator's macOS host. After this plan, the operator can run two
commands (`./scripts/deploy.sh` then `./scripts/install-launchd.sh`) and the
manual UAT step (`launchctl load -w ...`) to satisfy ROADMAP success
criteria #1 and #2.

Purpose: ROADMAP SC#1 (router auto-starts under launchd) and SC#2
(kill -9 → auto-restart within 5 seconds — actually 30s due to
ThrottleInterval, which is the locked operator convention from research §1)
become reachable. This is the LAST piece of plumbing before smart-router can
run permanently in the background alongside the qwen36-35b and qwen122b
services.

Output:
- `deploy/com.ohama.smart-router.plist` — verbatim from research §1, matches
  qwen plist convention.
- `scripts/deploy.sh` — `dotnet publish` + asset copy.
- `scripts/install-launchd.sh` — copy plist to `~/Library/LaunchAgents/`,
  print the manual UAT step (does NOT run `launchctl load` itself per L21).

NO new tests. NO source-code changes. NO Core changes.

This plan does NOT itself execute `launchctl load -w` against the
operator's host — that would mutate the operator's running services
without explicit user opt-in. The script `install-launchd.sh` ends by
PRINTING the launchctl command and the kill -9 UAT command for the
operator to run manually after they've reviewed everything.
</objective>

<execution_context>
@./.claude/get-shit-done/workflows/execute-plan.md
@./.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/phases/11-deployment-documentation/11-CONTEXT.md
@.planning/phases/11-deployment-documentation/11-RESEARCH.md

# Operator's existing LaunchAgent — convention reference (4-space XML, KeepAlive plain bool, ThrottleInterval=30)
# Read for shape only; do NOT copy values that are qwen-specific (Label, ProgramArguments, log paths).
# Note: 36-35b is the CURRENT 35B plist filename (NOT com.ohama.qwen35b.plist).
# These files live OUTSIDE the repo at /Users/ohama/Library/LaunchAgents/ — read them via the Read tool
# at runtime if needed, e.g. Read /Users/ohama/Library/LaunchAgents/com.ohama.qwen36-35b.plist
</context>

<tasks>

<task type="auto">
  <name>Task 1: Author deploy/com.ohama.smart-router.plist (verbatim from research §1, matches qwen convention)</name>
  <files>deploy/com.ohama.smart-router.plist</files>
  <action>
**Step 1.1 — Create the directory** if it does not yet exist:

```bash
mkdir -p /Users/ohama/projs/smart-router/deploy
```

**Step 1.2 — Write the plist** using the EXACT XML below. This is taken
verbatim from `11-RESEARCH.md` §1 ("Complete plist template (locked)") and
mirrors the operator's `~/Library/LaunchAgents/com.ohama.qwen36-35b.plist`
shape (4-space indentation, natural-order keys, plain `<true/>` bool for
`KeepAlive`, `ThrottleInterval=30`).

Path: `/Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.ohama.smart-router</string>
    <key>ProgramArguments</key>
    <array>
        <string>/opt/homebrew/bin/dotnet</string>
        <string>/Users/ohama/llm-system/services/smart-router/SmartRouter.dll</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ThrottleInterval</key>
    <integer>30</integer>
    <key>StandardOutPath</key>
    <string>/Users/ohama/llm-system/services/logs/smart-router.log</string>
    <key>StandardErrorPath</key>
    <string>/Users/ohama/llm-system/services/logs/smart-router.err</string>
    <key>WorkingDirectory</key>
    <string>/Users/ohama/llm-system/services/smart-router</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>ASPNETCORE_ENVIRONMENT</key>
        <string>Production</string>
        <key>HOME</key>
        <string>/Users/ohama</string>
        <key>PATH</key>
        <string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin</string>
    </dict>
</dict>
</plist>
```

DO NOT alter:
- Indentation (must be 4-space, NOT tab — qwen-style, NOT hermes-style)
- `KeepAlive` form (plain `<true/>`, NOT a `<dict>`)
- `ThrottleInterval` value (30s — matches qwen plists)
- The DTD header (Apple's standard PLIST 1.0 DTD)
- Absolute paths anywhere — `/opt/homebrew/bin/dotnet`,
  `/Users/ohama/...` — launchd does NOT expand `~` and does NOT inherit
  the user's PATH (L3, L6, L7 from CONTEXT.md).

**Step 1.3 — Validate the plist with plutil**:

```bash
plutil -lint /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
```

Expected output: `/Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist: OK`

If plutil reports an error, the most likely causes are:
- Mismatched `<key>...</key>` and value tag count (each key needs exactly
  one following value).
- Stray whitespace inside `<integer>` (must be `<integer>30</integer>`,
  no spaces).
- Missing closing `</dict>` or `</plist>`.

Fix any errors and re-run plutil until it reports OK.

**Step 1.4 — Compare against the operator's qwen36-35b convention**
(sanity check only; expect identical structural shape):

```bash
diff <(grep -E '<key>|<true/>|<integer>' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist | sort) \
     <(grep -E '<key>|<true/>|<integer>' /Users/ohama/Library/LaunchAgents/com.ohama.qwen36-35b.plist 2>/dev/null | sort) || true
```

The smart-router plist will have ONE additional `<key>HOME</key>` entry
that the qwen plist lacks (per research §1 — .NET runtime needs HOME for
NuGet/dotnet telemetry paths, but Python doesn't). All other structural
elements should match.
  </action>
  <verify>
```bash
# File exists with correct path
test -f /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist

# Lints clean
plutil -lint /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist | grep -q ': OK'

# Locked elements present (CONTEXT.md L1, L3, L4-L7, L8-L10)
grep -q 'com.ohama.smart-router' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '/opt/homebrew/bin/dotnet' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '/Users/ohama/llm-system/services/smart-router/SmartRouter.dll' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '<key>KeepAlive</key>' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '<key>ThrottleInterval</key>' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '<integer>30</integer>' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '<key>RunAtLoad</key>' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '<key>WorkingDirectory</key>' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q '<string>/Users/ohama/llm-system/services/smart-router</string>' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q 'smart-router.log' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
grep -q 'smart-router.err' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist

# KeepAlive is plain bool, NOT dict (anti-pattern guard from research §8)
awk '/<key>KeepAlive<\/key>/{getline; print}' /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist | grep -q '<true/>'
```
  </verify>
  <done>
- `deploy/com.ohama.smart-router.plist` exists and `plutil -lint` reports OK.
- Plist contains all 10 locked elements (Label, ProgramArguments[0],
  ProgramArguments[1], RunAtLoad, KeepAlive plain bool, ThrottleInterval=30,
  StandardOutPath, StandardErrorPath, WorkingDirectory, EnvironmentVariables
  with HOME+PATH+ASPNETCORE_ENVIRONMENT).
- Indentation is 4-space (qwen convention), NOT tabs (hermes convention).
  </done>
</task>

<task type="auto">
  <name>Task 2: Author scripts/deploy.sh (publish + install assets) and scripts/install-launchd.sh (copy plist; print manual UAT)</name>
  <files>
    scripts/deploy.sh
    scripts/install-launchd.sh
  </files>
  <action>
**Step 2.1 — Author `scripts/deploy.sh`**. This script runs on the operator
host and:
1. Creates the install directory tree (`~/llm-system/services/smart-router/`
   and the logs dir `~/llm-system/services/logs/`).
2. Runs `dotnet publish` framework-dependent (`-c Release`, NO
   `--self-contained`, NO trimming) and outputs to the install dir.
3. Copies appsettings.json, prompts/teacher-prompt.md, and any embed
   models the operator may have downloaded.

Path: `/Users/ohama/projs/smart-router/scripts/deploy.sh`

```bash
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

# Framework-dependent publish (CONTEXT L4 — NOT self-contained, NOT trimmed).
# ML.NET reflection breaks with PublishTrimmed=true; do NOT add --self-contained
# unless you have audited every reflection site.
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
```

**Step 2.2 — Author `scripts/install-launchd.sh`**. This script copies the
plist into `~/Library/LaunchAgents/` and PRINTS the launchctl command +
the kill-9 UAT command. Per L21, it does NOT run `launchctl load -w` itself
— that is a manual operator step so the operator can review the plist
contents first and choose when to introduce a new service into their
system.

Path: `/Users/ohama/projs/smart-router/scripts/install-launchd.sh`

```bash
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

       launchctl load -w ~/Library/LaunchAgents/com.ohama.smart-router.plist

  2) Verify it's reachable (ROADMAP SC#1):

       curl -fsS http://127.0.0.1:4000/health

  3) Verify auto-restart on crash (ROADMAP SC#2):

       kill -9 "$(pgrep -f SmartRouter.dll)"
       sleep 35   # ThrottleInterval=30s + ~5s startup
       curl -fsS http://127.0.0.1:4000/health

  4) View logs:

       tail -f ~/llm-system/services/logs/smart-router.log
       tail -f ~/llm-system/services/logs/smart-router.err

  5) Take it down:

       launchctl unload ~/Library/LaunchAgents/com.ohama.smart-router.plist

EOF
```

**Step 2.3 — Make both scripts executable**:

```bash
chmod +x /Users/ohama/projs/smart-router/scripts/deploy.sh
chmod +x /Users/ohama/projs/smart-router/scripts/install-launchd.sh
```

**Step 2.4 — Lint the scripts** with shellcheck if available; otherwise do
a basic syntactic check via bash -n:

```bash
if command -v shellcheck >/dev/null 2>&1; then
    shellcheck /Users/ohama/projs/smart-router/scripts/deploy.sh
    shellcheck /Users/ohama/projs/smart-router/scripts/install-launchd.sh
else
    bash -n /Users/ohama/projs/smart-router/scripts/deploy.sh
    bash -n /Users/ohama/projs/smart-router/scripts/install-launchd.sh
fi
```

Expected: no output (success). If shellcheck flags `SC2086` (unquoted
variables) or `SC2068` (unquoted "$@"), fix the script before proceeding.

**Step 2.5 — Smoke-test scripts/deploy.sh** ONLY IF the operator wants to
verify the publish runs end-to-end. This is OPTIONAL because it touches
the operator's host filesystem. If running:

```bash
cd /Users/ohama/projs/smart-router && ./scripts/deploy.sh
ls -la ~/llm-system/services/smart-router/SmartRouter.dll   # should exist after publish
```

If you skip Step 2.5, that's fine — Plan 11-02 ships the script; the
operator runs it during UAT. Do NOT run install-launchd.sh from this plan;
that mutates the operator's `~/Library/LaunchAgents/` and is per L21
explicitly a manual operator step.
  </action>
  <verify>
```bash
# Both scripts exist, are executable, and have shebangs
test -x /Users/ohama/projs/smart-router/scripts/deploy.sh
test -x /Users/ohama/projs/smart-router/scripts/install-launchd.sh
head -1 /Users/ohama/projs/smart-router/scripts/deploy.sh           | grep -q '^#!/usr/bin/env bash'
head -1 /Users/ohama/projs/smart-router/scripts/install-launchd.sh  | grep -q '^#!/usr/bin/env bash'

# set -euo pipefail at the top (defensive scripting)
grep -q 'set -euo pipefail' /Users/ohama/projs/smart-router/scripts/deploy.sh
grep -q 'set -euo pipefail' /Users/ohama/projs/smart-router/scripts/install-launchd.sh

# deploy.sh runs framework-dependent publish, NOT self-contained
grep -q 'dotnet publish'                   /Users/ohama/projs/smart-router/scripts/deploy.sh
grep -q -- '-c Release'                    /Users/ohama/projs/smart-router/scripts/deploy.sh
! grep -q -- '--self-contained'            /Users/ohama/projs/smart-router/scripts/deploy.sh   # MUST be absent
! grep -q 'PublishTrimmed=true'            /Users/ohama/projs/smart-router/scripts/deploy.sh   # MUST be absent

# deploy.sh creates required dirs (research §1 pitfall #3 + #4)
grep -q 'mkdir -p'                         /Users/ohama/projs/smart-router/scripts/deploy.sh
grep -q 'llm-system/services/smart-router' /Users/ohama/projs/smart-router/scripts/deploy.sh
grep -q 'llm-system/services/logs'         /Users/ohama/projs/smart-router/scripts/deploy.sh

# install-launchd.sh copies plist; does NOT run launchctl load itself (L21)
grep -q 'cp.*com\.ohama\.smart-router\.plist' /Users/ohama/projs/smart-router/scripts/install-launchd.sh
grep -q 'launchctl load -w'                   /Users/ohama/projs/smart-router/scripts/install-launchd.sh   # printed in next-steps block
# Anti-pattern: NO line that EXECUTES launchctl load (it should appear only inside the heredoc)
! grep -E '^[[:space:]]*launchctl load' /Users/ohama/projs/smart-router/scripts/install-launchd.sh

# bash -n syntactic OK
bash -n /Users/ohama/projs/smart-router/scripts/deploy.sh
bash -n /Users/ohama/projs/smart-router/scripts/install-launchd.sh
```
  </verify>
  <done>
- `scripts/deploy.sh` runs framework-dependent `dotnet publish` to
  `~/llm-system/services/smart-router/`, creates required dirs, copies
  appsettings + prompts.
- `scripts/install-launchd.sh` copies the plist into `~/Library/LaunchAgents/`
  and prints the manual UAT commands; it does NOT execute `launchctl load`.
- Both scripts are executable, have `#!/usr/bin/env bash`, and start with
  `set -euo pipefail`.
- Both scripts pass `bash -n` syntactic check (shellcheck if available).
  </done>
</task>

</tasks>

<verification>
Phase 11-02 verification — combined check:

```bash
# Plist artifact
test -f /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
plutil -lint /Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist | grep -q ': OK'

# All locked plist elements
P=/Users/ohama/projs/smart-router/deploy/com.ohama.smart-router.plist
for s in 'com.ohama.smart-router' '/opt/homebrew/bin/dotnet' \
         '/Users/ohama/llm-system/services/smart-router/SmartRouter.dll' \
         '<key>KeepAlive</key>' '<key>ThrottleInterval</key>' '<integer>30</integer>' \
         '<key>RunAtLoad</key>' '<key>WorkingDirectory</key>' \
         'smart-router.log' 'smart-router.err'; do
    grep -q "$s" "$P" || { echo "MISSING: $s"; exit 1; }
done

# Both scripts executable + linted
test -x /Users/ohama/projs/smart-router/scripts/deploy.sh
test -x /Users/ohama/projs/smart-router/scripts/install-launchd.sh
bash -n /Users/ohama/projs/smart-router/scripts/deploy.sh
bash -n /Users/ohama/projs/smart-router/scripts/install-launchd.sh

# install-launchd.sh does NOT execute launchctl load (per L21)
! grep -E '^[[:space:]]*launchctl load' /Users/ohama/projs/smart-router/scripts/install-launchd.sh

# No source code changes (this is purely an ops plan)
git diff --name-only -- 'src/' 'tests/' | wc -l | tr -d ' '   # → 0

# No new tests added (per L19)
git diff --name-only -- 'tests/SmartRouter.Tests/' | wc -l | tr -d ' '   # → 0
```
</verification>

<success_criteria>
- [ ] `deploy/com.ohama.smart-router.plist` exists, plutil-clean, and
      mirrors the operator's qwen plist convention exactly.
- [ ] `scripts/deploy.sh` is executable, runs framework-dependent publish
      to `~/llm-system/services/smart-router/`, creates all required dirs.
- [ ] `scripts/install-launchd.sh` is executable, copies plist into
      `~/Library/LaunchAgents/`, prints next-steps without itself running
      `launchctl load`.
- [ ] Both scripts pass `bash -n` (shellcheck if available).
- [ ] Zero source-code changes (no edits under `src/` or `tests/`).
- [ ] Pure-Core invariant preserved.
</success_criteria>

<output>
After completion, create `.planning/phases/11-deployment-documentation/11-02-LAUNCHD-OPS-SUMMARY.md`
covering:
- Files created (plist + 2 scripts) with line counts.
- `plutil -lint` output (showing OK).
- Confirmation the plist matches the qwen36-35b convention (4-space, plain
  bool KeepAlive, ThrottleInterval=30) — show a `diff` excerpt of the key
  list against the operator's qwen plist.
- The exact manual UAT commands the operator will run after Phase 11
  completes (load + curl /health + kill -9 + curl /health + unload).
- Confirmation that NO source-code or test files were modified by this
  plan (`git diff --name-only -- 'src/' 'tests/'` → empty).
</output>
