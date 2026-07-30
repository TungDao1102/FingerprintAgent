---
phase: "03-resilience-runtime-reconfiguration"
reviewed: 2026-07-30T23:30:00Z
depth: deep
files_reviewed: 13
files_reviewed_list:
  - src/FingerprintAgent/Adapters/CaptureResult.cs
  - src/FingerprintAgent/Adapters/ScannerManager.cs
  - src/FingerprintAgent/Api/CaptureHandler.cs
  - src/FingerprintAgent/Api/CorsMiddleware.cs
  - src/FingerprintAgent/Api/HealthHandler.cs
  - src/FingerprintAgent/Api/HttpServer.cs
  - src/FingerprintAgent/Configuration/ConfigFileWatcher.cs
  - src/FingerprintAgent/Models/CaptureResponse.cs
  - src/FingerprintAgent/Service/FingerprintAgentService.cs
  - tests/FingerprintAgent.Tests/ErrorHandlingTests.cs
  - tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj
  - tests/FingerprintAgent.Tests/MockScannerAdapterTestDoubles.cs
  - tests/FingerprintAgent.Tests/ScannerManagerTests.ExponentialBackoff.cs
findings:
  critical: 1
  warning: 3
  info: 3
  total: 7
status: issues_found
---

# Phase 03: Code Review Report (Post-Fix Re-Assessment)

**Reviewed:** 2026-07-30
**Depth:** deep (cross-file analysis including import graphs and call chains)
**Files Reviewed:** 13
**Status:** issues_found

## Summary

Phase 03 fixes were applied across 6 commits. Two fixes were inadvertently reverted during later fix commits, and one fix was accidentally removed when another fix was applied to the same file. The net result: **CR-01 has regressed** (critical bug reintroduced), WR-01 (lock ordering doc) was removed along with it, and WR-03/WR-05 (bare catch and connected heartbeat log) were reverted. WR-02 and WR-04 fixes remain correctly applied.

**Fix quality concern:** When applying multiple independent fixes to the same file in separate commits, later commits modified lines that had been set by earlier fix commits. A combined/merged fix approach would have prevented these regressions.

---

## Previous Findings Resolution

| ID | Description | Status | Evidence |
|----|-------------|--------|----------|
| CR-01 | `_activeAdapter` null in two-arg constructor | **NOT FIXED — REGRESSED** | Fix was applied at commit `50150f4` (+1 line), then removed at commit `50ac9a5` (same line deleted as part of WR-01 class doc change). Current two-arg constructor at `ScannerManager.cs:124-130` does NOT initialize `_activeAdapter`. |
| WR-01 | Lock ordering policy undocumented | **NOT FIXED — REGRESSED** | Lock ordering policy was added at commit `50ac9a5` lines 20-24, but removed at commit `21b9804` (entire class doc comment was replaced). No lock ordering policy exists in current HEAD. |
| WR-02 | OnConfigReloaded silently skips CORS when `_httpServer` null | **FIXED** | Commit `e014eb3` correctly applies the `if (_httpServer != null) ... else _logger?.Warn(...)` pattern at `FingerprintAgentService.cs:230-233`. |
| WR-03 | HealthCheckCallback only logs when disconnected | **NOT FIXED — REVERTED** | Fix was applied at commit `315bbe6` (added `else { _logger?.Debug(...) }` block), then reverted at commit `e014eb3`. Current code at `FingerprintAgentService.cs:206-211` only logs when `!connected`. |
| WR-04 | UpdatePriority() leaks old adapters | **FIXED** | Commit `21b9804` added the trade-off note to the `UpdatePriority()` doc comment at `ScannerManager.cs:138-141`. Correctly documents that old adapters are intentionally NOT disposed because `_activeAdapter` might reference one. |
| WR-05 | OnStop bare `catch {}` anti-pattern | **NOT FIXED — REVERTED** | Fix was applied at commit `bbd5a43` (`catch (Exception ex) { _logger?.Debug(...) }`), then reverted at commit `315bbe6`. Current code at `FingerprintAgentService.cs:111` is back to bare `catch { }`. |
| IN-01 | `_configLock` redundant | **NOT APPLICABLE** | INFO item. `_configLock` at `FingerprintAgentService.cs:25` remains locked but unused for meaningful synchronization. Still valid observation. |
| IN-02 | ScannerManager test constructor bypasses production init | **NOT APPLICABLE** | INFO item. Two-arg constructor still bypasses production initialization. Still valid observation. |
| IN-03 | HealthHandler upcast to ScannerManager implicit | **NOT APPLICABLE** | INFO item. `as` cast at `HealthHandler.cs:25-26` still implicit. Still valid observation. |

---

## Critical Issues

### CR-01: `_activeAdapter` is NULL after ScannerManager two-argument construction — NullReferenceException risk on Scan() [REGRESSION]

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:124-130`
**Status:** not_fixed (regression — fix was applied at `50150f4` then removed at `50ac9a5`)
**Severity:** CRITICAL

**Issue:** The two-argument constructor `ScannerManager(IScannerAdapter[] adapters, AgentLogger logger)` at lines 124-130 does NOT initialize `_activeAdapter`. The field defaults to `null` after construction.

This means:
1. When tests call `Scan()` on a `ScannerManager` constructed with the two-arg constructor, `Scan()` enters the non-MockMode path (line 195: `_mockMode = false`).
2. At line 204: `lock (_adapterLock) { current = _activeAdapter; }` — `current` will be `null`.
3. The null-check at line 205 (`if (current != null && !current.IsConnected)`) skips the SCAN-06 block.
4. The adapter loop at lines 233-275 runs and can succeed, setting `ActiveAdapter = adapter` on success (line 255).
5. **BUT** — if a single adapter is provided and `Initialize()` returns `false` (as in `ScannerManagerExponentialBackoffTests.BackoffStep_StartsAtZero` at line 12-22: `InitializeResult = false`), all adapters fail, and the code falls through to `ApplyBackoff` and returns `SCANNER_NOT_CONNECTED`. The `IsConnected` property at line 43 returns `false` (via `ActiveAdapter?.IsConnected ?? false` — null-conditional is safe for reading). This path doesn't crash.

**The crash path:** If `_mockMode` were ever set to `true` in the two-arg constructor (or if a test changed it), line 197 would execute `ActiveAdapter.Scan()` with `ActiveAdapter == null` → `NullReferenceException`.

**More critically:** The root defect — `_activeAdapter` is provably `null` after construction — means the SCAN-06 backoff path (`current != null && !current.IsConnected`) is **never exercised** for the test-constructed `ScannerManager`. The SCAN-06 path requires `_activeAdapter` to be non-null but disconnected; since `_activeAdapter` is always null in the test path, the entire SCAN-06 retry logic is **unexercised** by unit tests.

**The regression chain:**
- Commit `50150f4` added `_activeAdapter = adapters.Length > 0 ? adapters[0] : null;` (CR-01 fix)
- Commit `50ac9a5` removed that same line when adding the lock ordering policy documentation to the class doc comment
- Commit `21b9804` further modified the class doc comment, permanently removing the lock ordering policy and leaving `_activeAdapter` uninitialized

**Fix — add back the single line:**
```csharp
// In two-arg constructor, after line 129:
_activeAdapter = adapters.Length > 0 ? adapters[0] : null;
```

---

## Warnings

### WR-01: Lock ordering policy documentation was removed [REGRESSION]

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:7-21`
**Status:** not_fixed (regression — fix added at `50ac9a5`, removed at `21b9804`)
**Severity:** WARNING

**Issue:** The lock ordering policy doc comment was correctly added at commit `50ac9a5` (lines 20-24):
```
/// Lock ordering policy (DO-01):
/// 1. Always acquire _adapterLock before _backoffLock
/// 2. Never acquire _backoffLock without also holding _adapterLock
/// 3. UpdatePriority() must NOT be modified to take _backoffLock
```

This was part of the class doc comment at `ScannerManager.cs:20-24`. Commit `21b9804` replaced the entire class doc comment with the UpdatePriority disposal note, removing the lock ordering policy entirely. The current class doc comment (`ScannerManager.cs:7-20`) contains the composite adapter description, SCAN-06 backoff description, and fail-fast note — but NOT the lock ordering policy.

**Impact:** Without the documented policy, future developers could modify `UpdatePriority()` to also acquire `_backoffLock` (e.g., to reset backoff state on priority change), creating a deadlock risk since `Scan()` acquires `_adapterLock` first then `_backoffLock`.

**Fix — restore the lock ordering policy in the class doc comment:**
```csharp
/// Lock ordering policy (DO-01):
/// 1. Always acquire _adapterLock before _backoffLock
/// 2. Never acquire _backoffLock without also holding _adapterLock
/// 3. UpdatePriority() must NOT be modified to take _backoffLock
```

---

### WR-03: HealthCheckCallback only logs when disconnected — no periodic heartbeat log [REVERTED]

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:202-217`
**Status:** not_fixed (fix was applied at `315bbe6`, reverted at `e014eb3`)
**Severity:** WARNING

**Issue:** The 30-second health check timer only logs when `_scanner.IsConnected` is false (lines 207-210). When the scanner is connected, no log is emitted. This makes it impossible to distinguish "health check is running normally" from "health check timer fired but didn't execute the callback" or "the timer thread is dead."

**Fix — add connected heartbeat log** (as originally intended in commit `315bbe6`):
```csharp
private void HealthCheckCallback(object state)
{
    try
    {
        bool connected = _scanner.IsConnected;
        if (!connected)
        {
            var backoffStep = (_scanner as ScannerManager)?.BackoffStep ?? 0;
            _logger?.Warn(null, $"HealthCheck: scanner not connected (backoff step={backoffStep})");
        }
        else
        {
            _logger?.Debug(null, "HealthCheck: scanner connected");
        }
    }
    catch (Exception ex)
    {
        _logger?.Error(null, $"HealthCheck: callback threw {ex.GetType().Name}: {ex.Message}");
    }
}
```

---

### WR-05: FingerprintAgentService.OnStop() bare `catch {}` anti-pattern still present [REVERTED]

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:111`
**Status:** not_fixed (fix was applied at `bbd5a43`, reverted at `315bbe6`)
**Severity:** WARNING

**Issue:** At line 111:
```csharp
try { _healthCheckTimer?.Dispose(); } catch { }
```

This bare `catch {}` silently swallows any exception from timer disposal. While timer disposal exceptions are unlikely, bare catches violate project convention and make debugging difficult. This is the pre-existing anti-pattern documented in AGENTS.md.

**Fix — add debug logging** (as originally intended in commit `bbd5a43`):
```csharp
try { _healthCheckTimer?.Dispose(); }
catch (Exception ex) { _logger?.Debug(null, $"healthCheckTimer disposal threw: {ex.Message}"); }
```

---

## Info

### IN-01: `_configLock` in `FingerprintAgentService` is locked but provides no practical synchronization

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:25` (field), `src/FingerprintAgent/Service/FingerprintAgentService.cs:224-227` (usage)
**Status:** not_applicable (INFO — still valid as observed)

The `_configLock` object is used only to synchronize writes to `_config` (line 224-227). Since `AgentConfig` is a reference type, the assignment `_config = newConfig` is atomic on its own — no lock needed for reference swap. Reads of `_config` in `OnStop()` don't actually consume the config object. The lock is redundant.

---

### IN-02: `ScannerManager` test constructor bypasses production initialization path

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:124-130`
**Status:** not_applicable (INFO — still valid as observed)

The two-argument constructor does NOT call `Initialize()` on any adapter, does NOT set `_activeAdapter`, and does NOT go through the priority list build process. `_activeAdapter` is now null (after CR-01 regression), `_mockMode` is hardcoded to `false`, and no adapter is initialized. This means unit tests exercise a meaningfully different path than production.

---

### IN-03: `HealthHandler` upcast to `ScannerManager` is safe but implicit

**File:** `src/FingerprintAgent/Api/HealthHandler.cs:25-26`
**Status:** not_applicable (INFO — still valid as observed)

The `as` casting pattern at lines 25-26 is acceptable for the current design but relies on the concrete implementation being `ScannerManager`. If the scanner implementation changes, these properties would silently return `false`/`0`.

---

## Structural Findings (fallow)

No structural pre-pass findings were provided.

---

## Cross-Cut Analysis

### Fix-commit chain analysis: how the regressions occurred

The root cause of both regressions is the same pattern: **multiple sequential fixes to the same block of text in separate commits, where later commits replaced text set by earlier commits rather than editing around it.**

**CR-01 regression chain:**
1. `50150f4`: Added `_activeAdapter = adapters.Length > 0 ? adapters[0] : null;` in two-arg constructor ✓
2. `50ac9a5`: Changed class doc comment (added lock ordering policy) — this commit replaced the entire class doc comment string, removing the CR-01 fix line ✗

**WR-01 regression chain:**
1. `50ac9a5`: Added lock ordering policy to class doc comment ✓
2. `21b9804`: Replaced class doc comment with UpdatePriority disposal note, removing lock ordering policy ✗

**WR-03/WR-05 revert chain:**
1. `bbd5a43`: WR-05 fix: bare catch → debug-logging catch ✓
2. `315bbe6`: WR-03 fix: added connected heartbeat log AND reverted WR-05 fix ✗
3. `e014eb3`: WR-02 fix: null-check for _httpServer AND reverted WR-03 fix ✗

**Recommendation for future fix commits:** Apply all fixes to a given file in a single combined commit rather than separate commits that modify overlapping text regions. Alternatively, use `git add -p` to stage hunks individually and commit per-logical-fix to avoid text-conflict regressions.

### Backoff state machine (re-verified)

- `_backoffStep` starts at 0 ✓
- `ApplyBackoff()` increments with `Math.Min(_backoffStep + 1, 3)` — correctly caps at index 3 ✓
- `InBackoff` returns `step > 0 && now < _backoffUntil` — correct ✓
- Reset happens on successful scan at line 255-256 ✓
- BackoffDelays array: `{10, 30, 60, 120}` — index matches step number ✓

### Error code → HTTP status mapping (re-verified)

All callers use `CaptureHandler.MapErrorCode()` (line 120):
- `SCANNER_NOT_CONNECTED` → 503 ✓
- `CAPTURE_TIMEOUT` → 504 ✓
- `INVALID_REQUEST` → 400 ✓
- `CAPTURE_FAILED` → 500 ✓
- `CONFIG_ERROR` → 500 ✓

---

_Reviewed: 2026-07-30_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: deep_