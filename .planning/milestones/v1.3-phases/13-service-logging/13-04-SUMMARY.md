---
phase: 13-service-logging
plan: 04
subsystem: cli-arg-parsing
tags: [program.fs, log-level, cli, serilog, levelswitch]
completed: 2026-05-09
duration: ~4 min

dependency-graph:
  requires: ["13-01"]
  provides: ["--log-level CLI flag with enum validation and --trace migration guard"]
  affects: ["13-05", "13-06"]

tech-stack:
  added: []
  patterns: ["module-level private helper shared across two execution branches"]

key-files:
  created: []
  modified:
    - src/SmartRouter.Cli/Program.fs

decisions:
  - "parseLogLevel lives at module-level (not inside main) so applyLogLevelFromArgs can be a private module-level helper callable from both --retrain and main branches"
  - "applyLogLevelFromArgs called AFTER Logging.configure in each branch — setLevel only meaningful after levelSwitch is initialized"
  - "'trace' alias added to parseLogLevel Verbose arm for forward-compat (someone typing --log-level=trace gets Verbose, distinct from --trace which fails with migration error)"
  - "Log.Information emitted when --log-level applied so the operator can see the level was accepted (appears in rolling file and stderr)"

metrics:
  tasks-completed: 1
  tasks-total: 1
  test-baseline: "62 passed + 16 ignored + 0 failed (unchanged)"
---

# Phase 13 Plan 04: --log-level CLI Flag Summary

**One-liner:** Replaced boolean `--trace` with typed `--log-level=enum` in Program.fs via `parseLogLevel` + shared `applyLogLevelFromArgs` helper covering both `--retrain` and main Kestrel branches.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Add parseLogLevel + applyLogLevelFromArgs + arg parsing in Program.fs | e7f904f | src/SmartRouter.Cli/Program.fs |

## What Was Built

`src/SmartRouter.Cli/Program.fs` gains two module-level private functions:

**`parseLogLevel (raw: string) : LogEventLevel`**
- Maps to Serilog `LogEventLevel` via case-insensitive match
- Long forms: `verbose`, `debug`, `information`, `warning`, `error`, `fatal`
- Short aliases: `vrb`, `dbg`, `info`/`inf`, `warn`/`wrn`, `err`, `ftl`
- `trace` alias maps to Verbose (convenience; distinct from `--trace` flag)
- Invalid value: `failwithf` with full valid-values list

**`applyLogLevelFromArgs (args: string array) : unit`**
- Checks for `--trace` first → fails with migration message
- Finds `--log-level=VALUE` (equals form) or `--log-level VALUE` (space form)
- Empty value or next-arg-is-flag → fails with clear error
- When level found: calls `Logging.setLevel level` + logs confirmation
- When absent: no-op (levelSwitch default Information from Logging.fs init)

Called in both branches:
- `--retrain` branch: after `Logging.configure(retrainBuilder.Configuration)`
- Main Kestrel branch: after `Logging.configure(builder.Configuration)`

## Verification Results

```
grep -c "parseLogLevel|applyLogLevelFromArgs|--log-level|setLevel": 18 (>= 4 required)
grep -c "trace flag was removed|--log-level=": 6 (>= 2 required)
dotnet build: 0 warnings, 0 errors
dotnet test: 62 passed + 16 ignored + 0 failed (baseline preserved)
```

## Deviations from Plan

### Auto-fixed Issues

None — plan executed exactly as written.

**Minor extension noted:** `"trace"` alias added to the `Verbose` arm of `parseLogLevel`. This is not a deviation — the plan's match table shows `"verbose" | "vrb" | "trace"` for Verbose in the Task 1 code snippet. Implemented exactly as specified.

## Next Phase Readiness

- 13-05 (startup banner + LogRetentionService): unblocked; `applyLogLevelFromArgs` is in place
- 13-06 (tests): test items 12-14 from Q10 (`cli_log_level_debug_passes_debug_emissions`, `cli_log_level_warn_blocks_information`, `--trace_flag_errors_with_migration_message`) can now be implemented against the live code
