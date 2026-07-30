---
phase: 03-resilience-runtime-reconfiguration
reviewed: 2026-07-30T21:00:00Z
depth: deep
files_reviewed: 12
files_reviewed_list:
  - src/FingerprintAgent/Adapters/ScannerManager.cs
  - src/FingerprintAgent/Service/FingerprintAgentService.cs
  - src/FingerprintAgent/Api/HealthHandler.cs
  - src/FingerprintAgent/Configuration/ConfigFileWatcher.cs
  - src/FingerprintAgent/Api/CorsMiddleware.cs
  - src/FingerprintAgent/Api/HttpServer.cs
  - src/FingerprintAgent/Models/CaptureResponse.cs
  - src/FingerprintAgent/Api/CaptureHandler.cs
  - src/FingerprintAgent/Adapters/CaptureResult.cs
  - tests/FingerprintAgent.Tests/ErrorHandlingTests.cs
  - tests/FingerprintAgent.Tests/MockScannerAdapterTestDoubles.cs
  - tests/FingerprintAgent.Tests/ScannerManagerTests.ExponentialBackoff.cs
findings:
  critical: 2
  warning: 4
  info: 7
  total: 13
status: issues_found
---

# Phase 3: Resilience & Runtime Reconfiguration — Code Review Report

**Reviewed:** 2026-07-30T21:00:00Z
**Depth:** deep (cross-file import graph, call-chain tracing, thread-safety analysis, resource lifecycle audit)
**Files Reviewed:** 12
**Status:** issues_found — 2 critical, 4 warning, 7 info

## Summary

Deep review of Phase 3 (Resilience & Runtime Reconfiguration) across 12 files covering the backoff state machine, health-check loop, config hot-reload, CORS hot-reload, error-code mapping, and integration tests. The code is structurally sound but has two potentially crash-inducing issues: a double-dispose of the active scanner adapter in `ScannerManager.Dispose()`, and a silent accumulation of undisposed adapter instances from `UpdatePriority()` calls. Several thread-safety gaps exist around timer/event lifecycles during shutdown, and one test does not actually exercise the behavior implied by its name (backoff reset). Additionally, the `[StringLength]` data annotations on `CaptureRequest` are decorative — JSON.NET does not enforce them, providing no input-length protection.

## Critical Issues

### CR-01 (BL-01): Active adapter disposed twice in ScannerManager.Dispose()

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:300-311`
**Issue:** `ScannerManager.Dispose()` iterates all adapters in `_adapters` and disposes each, then separately accesses `ActiveAdapter` (which is one of those same adapters) and disposes it again. Double-disposing `IDisposable` objects that wrap native SDK handles (SecuGen FDxSDKPro, Futronic ftrScanAPI P/Invoke, ZKTeco native host, DigitalPersona DPUruNet) can lead to undefined behavior: crashes, native heap corruption, or access violations.

**Root cause:** `Dispose()` at line 310 calls `(ActiveAdapter as IDisposable)?.Dispose()` without checking whether that adapter was already disposed in the `_adapters` loop (lines 305-308).

```csharp
// Lines 305-308: disposes every adapter including _activeAdapter
if (_adapters != null)
{
    foreach (var adapter in _adapters)
        (adapter as IDisposable)?.Dispose();
}
// Line 310: disposes _activeAdapter again — same object reference
(ActiveAdapter as IDisposable)?.Dispose();
```

**Fix:** Either skip the active adapter in the foreach loop, or null-check after the foreach:

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _cts?.Dispose();
    if (_adapters != null)
    {
        IScannerAdapter active;
        lock (_adapterLock) { active = _activeAdapter; }
        foreach (var adapter in _adapters)
        {
            // Skip active adapter — dispose it separately below to ensure
            // it is cleaned up even if UpdatePriority moved it out of _adapters
            if (ReferenceEquals(adapter, active))
                continue;
            (adapter as IDisposable)?.Dispose();
        }
    }
    // Dispose active adapter exactly once
    (ActiveAdapter as IDisposable)?.Dispose();
}
```

### CR-02 (BL-02): Memory leak — UpdatePriority() abandons old adapter instances without disposal

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:147-164`
**Issue:** `UpdatePriority()` replaces `_adapters` with a new array of newly-created adapter instances. The old array (including all old adapter instances except `_activeAdapter`) loses all references without being disposed. Over multiple config reloads, this accumulates orphaned adapter objects. Each `CreateAdapter` call may allocate SDK-native resources (device handles, allocated buffers, native thread contexts).

**Root cause:** The `_adapters = vendorList.ToArray()` assignment (line 160) drops the previous array. The comment at lines 143-145 acknowledges this as "an intentional trade-off (D-09)" because `_activeAdapter` might reference one of the old adapters. But the non-active adapters in the old array are leaked.

```csharp
public void UpdatePriority(string[] newPriority)
{
    // ...
    lock (_adapterLock)
    {
        var vendorList = new List<IScannerAdapter>();
        foreach (var vendorName in newPriority)
        {
            IScannerAdapter adapter = CreateAdapter(vendorName);
            vendorList.Add(adapter);
        }
        _adapters = vendorList.ToArray();  // old array dropped — leaked
    }
}
```

**Fix:** Capture and dispose the old array (excluding the active adapter) under `_adapterLock`:

```csharp
public void UpdatePriority(string[] newPriority)
{
    if (newPriority == null || newPriority.Length == 0)
        return;

    IScannerAdapter[] oldAdapters;
    lock (_adapterLock)
    {
        oldAdapters = _adapters;
        var vendorList = new List<IScannerAdapter>();
        foreach (var vendorName in newPriority)
        {
            IScannerAdapter adapter = CreateAdapter(vendorName);
            vendorList.Add(adapter);
        }
        _adapters = vendorList.ToArray();
    }

    // Dispose old adapters that are NOT the active adapter
    // (active adapter stays alive; disposed only at ScannerManager shutdown)
    if (oldAdapters != null)
    {
        IScannerAdapter active;
        lock (_adapterLock) { active = _activeAdapter; }
        foreach (var adapter in oldAdapters)
        {
            if (!ReferenceEquals(adapter, active))
                (adapter as IDisposable)?.Dispose();
        }
    }

    _logger?.Info(null, $"ScannerManager: priority updated, new order=[{string.Join(", ", newPriority)}]");
}
```

## Warnings

### WR-01: Race condition — health check callback may access disposed scanner during shutdown

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:205-223`
**Issue:** `OnStop()` disposes `_healthCheckTimer` before `_scanner`. `System.Threading.Timer.Dispose()` guarantees no *new* callbacks are scheduled after it returns, but a callback already dispatched to the thread pool may still execute concurrently with subsequent disposal code. If the health check callback (line 209, `_scanner.IsConnected`) executes after `_scanner` has been disposed (line 147), it accesses freed native resources or throws `ObjectDisposedException`.

**Sequence of events:**
1. `OnStop` line 109: `_healthCheckTimer?.Dispose()` — prevents new callbacks
2. Thread pool: a health check callback was already queued before step 1
3. `OnStop` line 147: `(_scanner as IDisposable)?.Dispose()` — scanner disposed
4. Thread pool: queued callback runs → `_scanner.IsConnected` → accesses disposed scanner

**Fix:** Either move `_healthCheckTimer` disposal after `_scanner` disposal, or add a `_stopping` guard:

```csharp
// Option A: dispose timer after scanner
protected override void OnStop()
{
    // ... cancel CTS, stop HTTP server
    (_scanner as IDisposable)?.Dispose();
    _healthCheckTimer?.Dispose();
    // ... rest
}

// Option B: add guard flag
private volatile bool _stopping;

private void HealthCheckCallback(object state)
{
    if (_stopping) return;
    // ... rest
}
```

### WR-02: ConfigFileWatcher — dead code reads file contents into unused `json` variable

**File:** `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:60-66`
**Issue:** The file `config.json` is opened and read into the local variable `json`, but `json` is never referenced after the `using` block. The config is then reloaded via `ConfigLoader.LoadFromDirectory()` which opens the file a second time. The first read is waste (I/O, allocation, GC pressure) and indicates dead code that confuses maintainers.

```csharp
// Lines 60-66 — dead read: json is never used
string json;
using (var fs = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var reader = new StreamReader(fs))
{
    json = reader.ReadToEnd();
}
// Line 70: config loaded again via ConfigLoader
var newConfig = ConfigLoader.LoadFromDirectory(directory);
```

**Fix:** Remove the dead read:

```csharp
private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
{
    try
    {
        _logger?.Info(null, "ConfigFileWatcher: config changed, reloading");
        var directory = Path.GetDirectoryName(_configPath);
        var newConfig = ConfigLoader.LoadFromDirectory(directory);
        // ... rest of validation
```

### WR-03: `_adapters` read in Scan() without lock, inconsistent with documented lock ordering policy

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:237`
**Issue:** `Scan()` iterates `_adapters` at line 237 without acquiring `_adapterLock`, while `UpdatePriority()` (line 152) writes `_adapters` under the same lock. The documented lock ordering policy (lines 21-22) states `_adapterLock` is the primary lock for adapter state changes, but `Scan()` doesn't follow this when reading the fallback adapter array. Although the current code is safe in practice (reference assignment atomicity + foreach captures the array locally), the inconsistency violates the documented lock discipline and makes future refactoring fragile.

```csharp
// Scan(), line 237: reads _adapters WITHOUT _adapterLock
foreach (var adapter in _adapters)

// UpdatePriority(), line 152: writes _adapters WITH _adapterLock
lock (_adapterLock) { _adapters = vendorList.ToArray(); }
```

**Fix:** Wrap the foreach with a lock-copied local:

```csharp
IScannerAdapter[] currentAdapters;
lock (_adapterLock) { currentAdapters = _adapters; }
foreach (var adapter in currentAdapters)
{
    // ...
}
```

### WR-04: Test `BackoffStep_ResetsOnSuccessfulCapture` does not actually test backoff reset

**File:** `tests/FingerprintAgent.Tests/ScannerManagerTests.ExponentialBackoff.cs:57-77`
**Issue:** The test creates a ScannerManager with one always-failing adapter and one always-succeeding adapter, then calls `Scan()` three times. All three calls succeed (the first adapter initialises false, the second succeeds). Backoff is never incremented (stays at 0). The assertion `Assert.Equal(0, sm.BackoffStep)` confirms that backoff was never applied — not that it *reset* from a non-zero state. The test name is misleading.

**Fix:** Restructure the test to cover the actual reset scenario (backoff increments, then a successful capture resets to 0). As the current API does not support injecting adapters mid-test, rename the test to match what it actually tests:

```csharp
[Fact]
public void BackoffStep_NotAffected_WhenCapturesAlwaysSucceed()
{
    // ... existing code
}
```

## Info

### IN-01: MockScannerAdapterWithSettableProperties.MimeTypeValue property is unused

**File:** `tests/FingerprintAgent.Tests/MockScannerAdapterTestDoubles.cs:21`
**Issue:** The `MimeTypeValue` property is set on test instances but never read by any test. The default `ScanResult` has its own MIME type baked into the pre-created `CaptureResult.Ok()` call; `MimeTypeValue` is never wired into the scan result.

**Fix:** Either remove the property or wire it into the default `ScanResult` factory if tests may need to vary MIME type.

### IN-02: ConfigFileWatcher.OnDebounceElapsed recomputes directory already known in constructor

**File:** `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:69`
**Issue:** `Path.GetDirectoryName(_configPath)` is computed in the constructor (line 30) but not stored. It is re-computed on every config reload.

**Fix:** Store it as `_configDirectory` in the constructor.

### IN-03: HttpServer.Stop() silently swallows all AggregateException from worker task

**File:** `src/FingerprintAgent/Api/HttpServer.cs:88-90`
**Issue:** The empty `catch (AggregateException) { }` discards all exception details from the worker task drain, making post-cancellation bugs impossible to diagnose.

**Fix:** Log the flattened exception:
```csharp
catch (AggregateException aex)
{
    _logger?.Debug(null, $"HttpServer: worker task drain: {aex.Flatten().Message}");
}
```

### IN-04: Test `HealthHandler_Returns200_WhenScannerIsConnected` bypasses error-handling helper

**File:** `tests/FingerprintAgent.Tests/ErrorHandlingTests.cs:316-317`
**Issue:** This test calls `responseTask.GetAwaiter().GetResult()` directly instead of using the `GetResponse` helper used by all other tests. Inconsistent pattern.

**Fix:** Use `GetResponse(responseTask)` consistently.

### IN-05: ScannerManager.IsConnected has identical code in both branches of _mockMode ternary

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:46-48`
**Issue:** Both branches: `ActiveAdapter?.IsConnected ?? false`. The `_mockMode` check is dead for this property. (DeviceId and Model correctly have different fallback values and are not affected.)

**Fix:** Simplify to: `public bool IsConnected => ActiveAdapter?.IsConnected ?? false;`

### IN-06: CaptureHandler.WriteErrorResponse receives hardcoded `isSuccess = false` — dead parameter

**File:** `src/FingerprintAgent/Api/CaptureHandler.cs:139-158`
**Issue:** The `isSuccess` parameter is always passed as `false` from all call sites. The parameter adds confusion with no flexibility.

**Fix:** Remove the `isSuccess` parameter; hardcode `IsSuccess = false` in the response.

### IN-07: FingerprintAgentService.OnStop does not unsubscribe ConfigReloaded event

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:138`
**Issue:** Before disposing `_configWatcher`, the `OnConfigReloaded` event handler is not unsubscribed. While the disposal of `_configWatcher` prevents most races, leaving the handler subscribed keeps the disposed watcher reachable from the service instance.

**Fix:** Unsubscribe before disposal:
```csharp
if (_configWatcher != null)
{
    _configWatcher.ConfigReloaded -= OnConfigReloaded;
    _configWatcher.Dispose();
}
```

## Additional Notes

### Testing coverage gaps identified

| Gap | Missing test |
|---|---|
| `CONFIG_ERROR` → HTTP 500 | No test exercises the CONFIG_ERROR path (empty adapter array, MockMode=false) |
| Empty `_adapters` array | No test calls `Scan()` with `ScannerManager(new IScannerAdapter[0], logger)` |
| ScannerManager.Dispose() idempotency | No test verifies double-Dispose is safe or doesn't crash |
| Concurrent Scan() calls | No thread-safety test for parallel capture requests |
| ConfigFileWatcher | No unit tests exist for debounce, file change, or error handling at all |
| CorsMiddleware | No unit tests for UpdateConfig thread safety or preflight path |
| Backoff expiry | No test for `BackoffStep == 3` but `_backoffUntil` already passed |
| CaptureRequest string-length | No test for excessively long `thamChieuId` or `maPhieu` values |

### Known anti-patterns (not re-flagged)

The following are documented in AGENTS.md and confirmed present but already tracked:
- Bare `catch { }` in `ConfigFileWatcher.cs:95-96,102` (timer stop/dispose) — acceptable for best-effort disposal
- `HttpServer.cs:103-109` fire-and-forget with `Wait()` for graceful drain
- Nullable not enforced project-wide
- Test constructor bypasses locked `ActiveAdapter` property setter (`_activeAdapter = null`)

---

_Reviewed: 2026-07-30T21:00:00Z_
_Reviewer: gsd-code-reviewer (deep mode)_
_Depth: deep — cross-file import graph, call-chain tracing, thread-safety audit, resource lifecycle analysis_
