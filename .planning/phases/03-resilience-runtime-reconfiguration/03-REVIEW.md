---
phase: "03-resilience-runtime-reconfiguration"
reviewed: 2026-07-30T00:00:00Z
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
  warning: 5
  info: 3
  total: 9
status: issues_found
---

# Phase 03: Code Review Report

**Reviewed:** 2026-07-30
**Depth:** deep (cross-file analysis including import graphs and call chains)
**Files Reviewed:** 13
**Status:** issues_found

## Summary

Phase 03 implemented resilience and runtime reconfiguration: exponential backoff, config hot-reload, and HTTP status mapping. The implementation is mostly sound but has **one critical bug** (null `_activeAdapter` in the test-path constructor) and several warnings around lock ordering, resource lifecycle, and silent failure paths. Error code → HTTP status mapping is consistent across callers. Backoff state machine is correct. The two pre-existing bare `catch {}` anti-patterns remain.

---

## Critical Issues

### CR-01: `ScannerManager` two-argument constructor leaves `_activeAdapter` null — causes NullReferenceException on Scan()

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:124-130`
**Severity:** CRITICAL

**Issue:** The internal test constructor `ScannerManager(IScannerAdapter[] adapters, AgentLogger logger)` at line 124 never initializes `_activeAdapter`. The field defaults to `null`. When `Scan()` is called, the SCAN-06 retry path (lines 199-221) does:

```csharp
IScannerAdapter current;
lock (_adapterLock) { current = _activeAdapter; }  // null!
if (current != null && !current.IsConnected)       // null-check exists, skips block
```

The null-check at line 201 does prevent a crash in the SCAN-06 path. However, after falling through to the adapter loop, the code iterates `_adapters` and sets `ActiveAdapter = adapter` on success (line 251) — this path works.

**BUT:** In MockMode, the path at lines 191-194 reads:
```csharp
if (_mockMode)
{
    var result = ActiveAdapter.Scan();  // throws NullReferenceException if ActiveAdapter is null
    return result;
}
```

`_mockMode` is set to `false` in the two-argument constructor (line 128), so this specific path is safe for tests. However, the root issue remains: **`_activeAdapter` is provably null after construction with the two-argument constructor**. This is a latent bug — if anyone calls `Scan()` before any successful scan completes, `_activeAdapter` is null.

The `IsConnected` property at line 43 also accesses `ActiveAdapter?.IsConnected` — the null-conditional makes this safe for reading, but it returns `false` when `_activeAdapter` is null.

**Impact:** Production code path (`FingerprintAgentService`) always uses the `ScannerManager(AgentConfig, AgentLogger)` constructor which sets `ActiveAdapter` properly, so the production path is not affected. However, this means **the test constructor bypasses production initialization logic** and the test path exercises a different code path than production.

**Recommendation:** Initialize `_activeAdapter` in the two-argument constructor, or assert that `Scan()` is never called before the first adapter succeeds:
```csharp
// Add after line 129 in the two-argument constructor:
_activeAdapter = null;
```

Or better: set `_activeAdapter = adapters.Length > 0 ? adapters[0] : null` and document that `Scan()` must be called at least once before reading `ActiveAdapter`.

---

## Warnings

### WR-01: Lock ordering violates the "acquire in order" convention — potential deadlock

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:225-270` (Scan), `src/FingerprintAgent/Adapters/ScannerManager.cs:144-153` (UpdatePriority)
**Severity:** WARNING

**Issue:** `ScannerManager.Scan()` acquires locks in this order:
1. `_adapterLock` (line 200: `lock (_adapterLock) { current = _activeAdapter; }`)
2. `_backoffLock` (line 252: `lock (_backoffLock) { _backoffStep = 0; ... }`)

But `UpdatePriority()` at line 144 only acquires `_adapterLock`:
```csharp
lock (_adapterLock)
{
    var vendorList = new List<IScannerAdapter>();
    ...
}
```

If one thread holds `_adapterLock` (in `UpdatePriority`) and another thread holds both `_adapterLock` and `_backoffLock` (in `Scan`), and the first thread then tries to acquire `_backoffLock` (e.g., in a future code change), a deadlock could occur. Currently `UpdatePriority()` does not use `_backoffLock`, so no deadlock exists today. However, this is a fragile lock ordering that violates the "always acquire locks in the same order" convention.

**Recommendation:** Document the lock ordering contract, or restructure so only one lock is needed. For example, `_backoffLock` could be consolidated into `_adapterLock` since backoff state is only modified in `Scan()` which already holds `_adapterLock`.

---

### WR-02: `OnConfigReloaded` silently skips CORS update when `_httpServer` is null

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:219-237`
**Severity:** WARNING

**Issue:** At line 230:
```csharp
_httpServer?.UpdateCorsConfig(newConfig.Cors);
```

The null-conditional means if `_httpServer` is null, the call is silently skipped — no log, no exception. While in practice `_httpServer` is initialized before `_configWatcher` in `OnStart()` (line 56 before line 64), this is a silent failure path. If `_httpServer` were somehow null, CORS config would not be updated and there would be no indication in logs.

**Recommendation:** Add a warning log when skipping:
```csharp
if (_httpServer != null)
    _httpServer?.UpdateCorsConfig(newConfig.Cors);
else
    _logger?.Warn(cid, "OnConfigReloaded: _httpServer is null, skipping CORS update");
```

---

### WR-03: `HealthCheckCallback` only logs when disconnected — no periodic heartbeat log

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:202-217`
**Severity:** WARNING

**Issue:** The 30-second health check timer only logs when `_scanner.IsConnected` is false:
```csharp
bool connected = _scanner.IsConnected;
if (!connected)
{
    var backoffStep = (_scanner as ScannerManager)?.BackoffStep ?? 0;
    _logger?.Warn(null, $"HealthCheck: scanner not connected (backoff step={backoffStep})");
}
```

When the scanner IS connected, no log is emitted. This makes it impossible to distinguish "health check is running normally" from "health check timer fired but didn't execute the callback." In production, absence of health check logs might be misinterpreted.

**Recommendation:** Add an info-level log in the connected case:
```csharp
_logger?.Debug(null, "HealthCheck: scanner connected");
```

---

### WR-04: `UpdatePriority()` creates new adapters but never disposes the old ones

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:139-156`
**Severity:** WARNING

**Issue:** When `UpdatePriority(string[] newPriority)` is called, it creates new adapter instances via `CreateAdapter()` and replaces `_adapters`. The old adapter instances are simply overwritten without being disposed. If any of the old adapters held unmanaged resources (e.g., open handles, native SDK connections), these would leak.

The comment at line 133-136 says "D-09: active adapter is NOT touched — stays as-is across priority changes" — this is intentional. But the adapters being replaced (those no longer in the priority list) are leaked.

**Note:** In the current implementation, vendor adapters (SecuGen, DigitalPersona, Futronic, ZKTeco) are P/Invoke or NuGet wrappers — some may hold native resources. The `Dispose()` method at line 292 properly disposes all `_adapters`, but `UpdatePriority()` bypasses this.

**Recommendation:** Dispose old adapters before replacing the array:
```csharp
var oldAdapters = _adapters;
lock (_adapterLock) { _adapters = vendorList.ToArray(); }
// Note: can't dispose old adapters here because _activeAdapter might reference one of them
// This is a design trade-off — document it.
```

---

### WR-05: `FingerprintAgentService.OnStop()` bare `catch {}` — pre-existing anti-pattern still present

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:111`
**Severity:** WARNING (pre-existing, documented in AGENTS.md)

**Issue:** At line 111:
```csharp
try { _healthCheckTimer?.Dispose(); } catch { }
```

This bare `catch {}` silently swallows any exception from timer disposal. While timer disposal exceptions are unlikely and the pattern is "best-effort cleanup," bare catches are an anti-pattern per project convention. This was already documented in AGENTS.md as a known issue.

**Recommendation:** Add at minimum a debug log:
```csharp
try { _healthCheckTimer?.Dispose(); }
catch (Exception ex) { _logger?.Debug(null, $"healthCheckTimer disposal threw: {ex.Message}"); }
```

---

## Info

### IN-01: `_configLock` in `FingerprintAgentService` is locked but never used for synchronization

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:25` (field), `src/FingerprintAgent/Service/FingerprintAgentService.cs:224-227` (usage)
**Severity:** INFO

**Issue:** `_configLock` is declared at line 25:
```csharp
private readonly object _configLock = new object();
```

It is used at line 224:
```csharp
lock (_configLock)
{
    _config = newConfig;
}
```

Only `_config` is written inside the lock. `_config` is only ever read by `OnStop()` to write to the event log (line 83: `_logger?.Info(stopCid, "Service stopping")` — doesn't actually read `_config`). The lock is redundant; `_config` is an `AgentConfig` reference that is atomically assignable. If `_config` were ever read in `OnStart()` concurrently with `OnConfigReloaded` writing it, a stale reference read is harmless.

**Recommendation:** Remove `_configLock` or document why it exists.

---

### IN-02: `ScannerManager` test constructor bypasses production initialization path

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:124-130`
**Severity:** INFO

**Issue:** The two-argument constructor `ScannerManager(IScannerAdapter[] adapters, AgentLogger logger)` (line 124) sets `_mockMode = false` and `_cts = new CancellationTokenSource()` but does NOT call `Initialize()` on any adapter, does NOT set `_activeAdapter`, and does NOT go through the priority list build process. This means the test path exercises a meaningfully different initialization than the production constructor (line 93).

Specifically:
- `_activeAdapter` is `null` after the test constructor
- `_mockMode` is hardcoded to `false`
- The test path creates `IScannerAdapter[]` directly rather than going through `CreateAdapter()` / `UpdatePriority()`

This is intentional (enables unit testing of specific states) but worth documenting.

---

### IN-03: `HealthHandler` upcast to `ScannerManager` is safe but implicit

**File:** `src/FingerprintAgent/Api/HealthHandler.cs:25-26`
**Severity:** INFO

**Issue:** At lines 25-26:
```csharp
var inBackoff = (scanner as ScannerManager)?.InBackoff ?? false;
var backoffStep = (scanner as ScannerManager)?.BackoffStep ?? 0;
```

The code uses `as` casting to access `ScannerManager`-specific properties. This is a safe pattern but relies on the concrete implementation being `ScannerManager`. If the scanner implementation changes, these properties would silently return `false`/`0`. This is acceptable for the current design but worth noting.

---

## Structural Findings (fallow)

No structural pre-pass findings were provided.

---

## Cross-Cut Analysis

### Error Code → HTTP Status Mapping (consistency check)
All callers use `CaptureHandler.MapErrorCode()` (line 120). Verified:
- `SCANNER_NOT_CONNECTED` → 503 ✓
- `CAPTURE_TIMEOUT` → 504 ✓
- `INVALID_REQUEST` → 400 ✓
- `CAPTURE_FAILED` → 500 ✓
- `CONFIG_ERROR` → 500 ✓
- `null/unknown` → 500 with `CAPTURE_FAILED` ✓

`HealthHandler` uses a separate 200/503 logic based on `backoffStep < 3`, which is independent of `CaptureHandler.MapErrorCode()`. No inconsistency.

### Exponential Backoff State Machine (correctness check)
- `_backoffStep` starts at 0 ✓
- `ApplyBackoff()` increments with `Math.Min(_backoffStep + 1, 3)` — correctly caps at index 3 ✓
- `InBackoff` returns `step > 0 && now < _backoffUntil` — correct ✓
- Reset happens on successful scan at line 252 ✓
- BackoffDelays array: `{10, 30, 60, 120}` — index matches step number ✓
- `BackoffStep` property returns the raw int (not the delay seconds) — test at line 333 confirms this ✓

### ConfigFileWatcher → OnConfigReloaded → UpdateCorsConfig/UpdatePriority (race condition check)
- `OnDebounceElapsed` runs on a Timer thread ✓
- `ConfigReloaded?.Invoke(newConfig)` is at line 79 — fires event synchronously
- `OnConfigReloaded` is registered as the handler at line 65 in `OnStart`
- The chain is: Timer thread → `OnDebounceElapsed` → fires event → `OnConfigReloaded` on Timer thread
- `_httpServer?.UpdateCorsConfig()` and `scannerManager?.UpdatePriority()` are called on the Timer thread
- No locks are held across this chain, so no deadlock risk ✓
- `_configLock` is held only for `_config = newConfig` assignment — brief, no contention ✓

### Fire-and-Forget in HttpServer (deadlock/race check)
- `ProcessRequestLoop` at line 96 fires `HandleRequest` as fire-and-forget via `Task.Run()` (line 104) ✓
- `Stop()` waits `_workerTask?.Wait(TimeSpan.FromSeconds(30))` (line 86) to drain ✓
- `catch (AggregateException)` at line 88 silently swallows exceptions from `Wait()` ✓
- CS4014 pragma at line 103 suppresses the compiler warning about un-awaited task ✓
- This is the known anti-pattern documented in AGENTS.md — fragile but intentional ✓

### ZKTecoAdapter Static Singleton Teardown (safety check)
- `ZkTecoFingerHost.Close()` called once at line 155 in `OnStop()`, after all adapters are disposed ✓
- `ZKTecoAdapter.Dispose()` does NOT call `ZkTecoFingerHost.Close()` (per design, individual adapters must not close shared host) ✓
- Order: `_scanner` (ScannerManager) disposed first (disposes all adapters), then `ZkTecoFingerHost.Close()` ✓
- This is consistent with the documented anti-pattern — safe if exactly one `ZkTecoAdapter` instance is used per process lifetime ✓

---

_Reviewed: 2026-07-30_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: deep_