---
phase: 13-service-logging
verified: 2026-05-09T22:15:00Z
status: passed
score: 10/10 must-haves verified
re_verification: false
---

# Phase 13: Service Logging Verification Report

**Phase Goal:** smart-router 가 launchd service 로 영구 동작할 때 운영자가 의지할 수 있는 logging 인프라 구축. 두 stream 분리 + 듀얼 sink + correlation_id 출력 + appsettings.json 활성화 + ILogger<T> 마이그레이션 + hot-path 감축 + banners + LogRetentionService + --log-level CLI.
**Verified:** 2026-05-09T22:15:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Dual sink: Console stderr + rolling file both active | VERIFIED | Logging.fs L37-50: WriteTo.Console(standardErrorFromLevel=Verbose) + WriteTo.File(path=filePath, rollingInterval=Day) |
| 2 | correlation_id renders as request value or [-] default | VERIFIED | Logging.fs L36: Enrich.WithProperty("correlation_id", "-"); CorrelationMiddleware.fs L23: LogContext.PushProperty("correlation_id", cid) |
| 3 | Output template uses text (not JSON), includes [{correlation_id}] | VERIFIED | Logging.fs L16: outputTemplate includes [{correlation_id}]; no CompactJsonFormatter anywhere |
| 4 | appsettings.json has Logging:Directory, RetentionDays, DecisionLog:RetentionDays | VERIFIED | appsettings.json L126-128: "Directory":"logs/operational", "RetentionDays":30; L51-53: DecisionLog.RetentionDays:90 |
| 5 | ILogger<T> on all 12 type-based adapters; module adapters use ILogger param | VERIFIED | 12 adapter types confirmed; Validator/Retrainer/DatasetMerger/ModelBootstrapper use (logger: ILogger) param |
| 6 | LogRetentionService prunes 3 categories; registered in configureRequestPipeline only | VERIFIED | LogRetentionService.fs L72-74: prunes operational + decisions + datasets; CompositionRoot.fs L715 in configureRequestPipeline (before L719 closing `services`), absent from configureWithoutMl |
| 7 | --log-level=enum replaces --trace; --trace triggers migration error | VERIFIED | Program.fs L23-34: parseLogLevel with 6 levels + short aliases; L40-41: --trace failwith migration message |
| 8 | 50MB file cap; 30-day retained file count | VERIFIED | Logging.fs L44: fileSizeLimitBytes=50_000_000L; L46: retainedFileCountLimit=30 |
| 9 | Startup + shutdown banners emitted | VERIFIED | Program.fs L219-255: startup banner via ILoggerFactory.CreateLogger("Startup"); shutdown banner via ApplicationStopping callback |
| 10 | README §9.6+ documents Phase 13 reality; no "v1: ignored" or "Phase 13 (planned)" stubs | VERIFIED | grep count=0; §9.6 L690 documents quarterly truncate of smart-router.err |

**Score:** 10/10 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/SmartRouter.Cli/Adapters/Logging.fs` | Dual sink + outputTemplate + correlation_id enricher + levelSwitch | VERIFIED | 60 lines; all 6 grep targets confirmed (ReadFrom.Configuration, WriteTo.File, WriteTo.Console, fileSizeLimitBytes, retainedFileCountLimit, Enrich.*correlation_id) |
| `src/SmartRouter.Cli/Adapters/LogRetentionService.fs` | BackgroundService pruning 3 categories | VERIFIED | 88 lines; PeriodicTimer, pruneFiles called for operational/decisions/datasets |
| `src/SmartRouter.Cli/appsettings.json` | Logging:Directory + RetentionDays; DecisionLog:RetentionDays | VERIFIED | 5 matching keys (Override + Directory + RetentionDays x2) |
| `src/SmartRouter.Cli/Program.fs` | parseLogLevel + applyLogLevelFromArgs + startup/shutdown banners | VERIFIED | 267 lines; banners at L219-255; parseLogLevel L23-34; applyLogLevelFromArgs L38-62 |
| `src/SmartRouter.Cli/CompositionRoot.fs` | LogRetentionService registered; ILogger<T> passed to all type adapters | VERIFIED | 2 references to LogRetentionService (open + AddHostedService); 15 ILogger<T> resolutions for type adapters |
| `tests/SmartRouter.Tests/LogRotationTests.fs` | 14 testCase entries; all testSequenced; unique temp dirs | VERIFIED | grep count=14 testCase entries; testSequenced at L91; withTempDir uses Guid.NewGuid() L45 |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Logging.fs | appsettings.json Serilog section | ReadFrom.Configuration(config) | VERIFIED | L33: .ReadFrom.Configuration(config) |
| Logging.fs | appsettings.json Logging:Directory | config.["Logging:Directory"] | VERIFIED | L24: let raw = config.["Logging:Directory"] |
| Program.fs | Logging.fs levelSwitch | Logging.setLevel(level) | VERIFIED | L60: Logging.setLevel level inside applyLogLevelFromArgs |
| CompositionRoot.fs | LogRetentionService | services.Configure<LogRetentionOptions> + AddHostedService | VERIFIED | L704-715: reads Logging + DecisionLog sections; registers as BackgroundService |
| CorrelationMiddleware.fs | Serilog LogContext | LogContext.PushProperty("correlation_id", cid) | VERIFIED | L23: use _ = LogContext.PushProperty("correlation_id", cid) |
| ChatCompletions.fs | ILoggerFactory | ILoggerFactory.CreateLogger("ChatCompletions") | VERIFIED | Endpoints/ChatCompletions.fs L415 |
| Program.fs startup banner | ILoggerFactory | ILoggerFactory.CreateLogger("Startup") | VERIFIED | Program.fs L239 |

---

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| Q1=C Logging:Directory default logs/operational | SATISFIED | appsettings.json + Logging.fs defensive fallback both confirmed |
| Q2 50MB cap | SATISFIED | fileSizeLimitBytes=50_000_000L exact match |
| Q3 30+90 retention | SATISFIED | Logging:RetentionDays=30; DecisionLog:RetentionDays=90 |
| Q4 LogRetentionService active | SATISFIED | File present; registered in configureRequestPipeline; 3 categories pruned |
| Q5 text format both sinks | SATISFIED | outputTemplate variable used for both Console + File sinks; no CompactJsonFormatter |
| Q6 [{correlation_id}] + [-] default | SATISFIED | Enrich.WithProperty("-") + template confirmed; LogContext.PushProperty wires request value |
| Q7 ILogger<T> 전면 마이그레이션 | SATISFIED | 12 type adapters with ILogger<T> ctor; 4 module adapters with ILogger param; ChatCompletions uses ILoggerFactory; Program.fs + CompositionRoot.fs retain static Log only for bootstrap |
| Q8 --log-level=enum | SATISFIED | parseLogLevel + applyLogLevelFromArgs; --trace migration error confirmed |
| Q9 smart-router.err document only | SATISFIED | README §9.6 L690 documents quarterly truncate; no newsyslog config in repo |
| Q10 13+ tests | SATISFIED | 14 testCase entries in LogRotationTests.fs (exceeds 13 minimum) |

---

### Anti-Patterns Found

None detected. Scan of all Phase 13 modified files:
- No TODO/FIXME/HACK in Logging.fs, LogRetentionService.fs, Program.fs (Phase 13 sections), LogRotationTests.fs
- No empty handlers or placeholder returns in key paths
- Static `Log.Warning` at CompositionRoot.fs L134 (validateConfig) and `Log.Fatal` at Program.fs L263 (outer catch) are both bootstrap-only / pre-host contexts — permitted per Q7 exception

---

### Build and Test Results

| Check | Result |
|-------|--------|
| `dotnet build` | Clean: 0 errors, 0 warnings |
| `dotnet run --project tests/SmartRouter.Tests` | 76 passed, 16 ignored, 0 failed |
| Test count expectation (62 baseline + 14 new) | VERIFIED: 76 pass = 62 + 14 |
| ARCH-01: zero Core changes | VERIFIED: no ILogger/Serilog references in SmartRouter.Core/ |

---

### Human Verification Required

None. All Phase 13 behavioral contracts are verifiable structurally:
- File emission behavior is covered by LogRotationTests (Tests 6-8)
- Retention pruning is covered by LogRotationTests (Tests 9-11)
- Level switching is covered by LogRotationTests (Tests 12-13)
- Migration error is covered by LogRotationTests (Test 14)

Items that are runtime-only (startup banner appearing in rolling file on actual launchd boot, smart-router.err quarterly truncate procedure) are operator operational knowledge documented in README §9.6 — not blockers for code verification.

---

## Summary

Phase 13 goal fully achieved. All 10 locked decisions (Q1-Q10) are implemented in the actual codebase and confirmed against source, not SUMMARY claims:

- **Dual sink** (Logging.fs) with 50MB cap, 30-file retention, text outputTemplate
- **correlation_id** enriched as "-" default; CorrelationMiddleware pushes request value via LogContext
- **appsettings.json** has all three keys: Logging:Directory, Logging:RetentionDays, DecisionLog:RetentionDays
- **LogRetentionService** is a real BackgroundService pruning 3 categories on PeriodicTimer; registered only in configureRequestPipeline
- **ILogger<T>** migration complete: 12 type adapters via ctor, 4 modules via param, ChatCompletions via ILoggerFactory; static Log retained only at bootstrap (validateConfig warn + outer catch fatal)
- **--log-level enum** replaces --trace; --trace raises migration failwith with explicit error message
- **Banners** emitted via ILoggerFactory.CreateLogger("Startup"/"Shutdown") — not static Log
- **14 testCase entries** in LogRotationTests.fs, all testSequenced, unique temp dirs
- **README** updated: 0 occurrences of "v1: ignored" or "Phase 13 (planned)"
- **ARCH-01 preserved**: SmartRouter.Core contains zero logging infrastructure references

---

_Verified: 2026-05-09T22:15:00Z_
_Verifier: Claude (gsd-verifier)_
