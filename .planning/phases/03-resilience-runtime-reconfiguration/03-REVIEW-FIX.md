---
phase: 03-resilience-runtime-reconfiguration
fixed_at: 2026-07-30T21:30:00Z
review_path: .planning/phases/03-resilience-runtime-reconfiguration/03-REVIEW.md
iteration: 1
findings_in_scope: 6
fixed: 6
skipped: 0
status: all_fixed
---

# Phase 3: Resilience & Runtime Reconfiguration — Code Review Fix Report

**Fixed at:** 2026-07-30T21:30:00Z
**Source review:** `.planning/phases/03-resilience-runtime-reconfiguration/03-REVIEW.md`
**Iteration:** 1
**Fix scope:** Critical + Warning

**Summary:**
- Findings in scope: 6
- Fixed: 6
- Skipped: 0

## Fixed Issues

### CR-01 (BL-01): Active adapter disposed twice in ScannerManager.Dispose()

**Files modified:** `src/FingerprintAgent/Adapters/ScannerManager.cs`
**Commit:** `9606ba0`
**Applied fix:** Modified `Dispose()` to skip the active adapter in the `_adapters` foreach loop (using `ReferenceEquals` check), then dispose it exactly once after the loop. This prevents double-dispose of native SDK wrappers.

### CR-02 (BL-02): Memory leak — UpdatePriority() abandons old adapter instances without disposal

**Files modified:** `src/FingerprintAgent/Adapters/ScannerManager.cs`
**Commit:** `097ce8e`
**Applied fix:** Capture the old `_adapters` array before replacement under `_adapterLock`, then dispose all non-active adapters from the old array after exiting the lock. Active adapter is preserved per D-09 design decision.

### WR-01: Race condition — health check callback may access disposed scanner during shutdown

**Files modified:** `src/FingerprintAgent/Service/FingerprintAgentService.cs`
**Commit:** `ef23aee`
**Applied fix:** Moved `_healthCheckTimer?.Dispose()` from before `_scanner` disposal to after it, ensuring any thread-pool-queued health check callback that runs concurrently will find the scanner still alive.

### WR-02: Dead code — ConfigFileWatcher reads file into unused `json` variable

**Files modified:** `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs`
**Commit:** `45c1ca1`
**Applied fix:** Removed the dead `FileStream` + `StreamReader` read block from `OnDebounceElapsed`. The `json` variable was never used after the `using` block. Config is loaded via `ConfigLoader.LoadFromDirectory()` on the next line.

### WR-03: `_adapters` read in Scan() without lock, inconsistent with documented lock ordering policy

**Files modified:** `src/FingerprintAgent/Adapters/ScannerManager.cs`
**Commit:** `250a843`
**Applied fix:** Added a lock-copied local before the foreach loop in `Scan()` to ensure `_adapters` is read consistently under `_adapterLock`, matching the documented lock discipline.

### WR-04: Test `BackoffStep_ResetsOnSuccessfulCapture` does not actually test backoff reset

**Files modified:** `tests/FingerprintAgent.Tests/ScannerManagerTests.ExponentialBackoff.cs`
**Commit:** `a808f51`
**Applied fix:** Renamed test method from `BackoffStep_ResetsOnSuccessfulCapture` to `BackoffStep_NotAffected_WhenCapturesAlwaysSucceed` to accurately reflect what the test exercises.

## Skipped Issues

None — all 6 findings in scope were successfully fixed.

---

_Fixed: 2026-07-30T21:30:00Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
