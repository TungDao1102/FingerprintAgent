---
phase: "03-resilience-runtime-reconfiguration"
fixed_at: "2026-07-30T06:50:00Z"
review_path: ".planning/phases/03-resilience-runtime-reconfiguration/03-REVIEW.md"
iteration: 1
findings_in_scope: 6
fixed: 6
skipped: 0
status: all_fixed
---

# Phase 03: Code Review Fix Report

**Fixed at:** 2026-07-30T06:50:00Z
**Source review:** `.planning/phases/03-resilience-runtime-reconfiguration/03-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 6 (CR-01, WR-01, WR-02, WR-03, WR-04, WR-05)
- Fixed: 6
- Skipped: 0

## Fixed Issues

### CR-01: ScannerManager two-argument constructor leaves `_activeAdapter` null

**Files modified:** `src/FingerprintAgent/Adapters/ScannerManager.cs`
**Commit:** `50150f4` — `fixup(03): CR-01 initialize _activeAdapter in ScannerManager two-arg constructor`
**Applied fix:** Added `_activeAdapter = adapters.Length > 0 ? adapters[0] : null;` at the end of the two-argument `ScannerManager(IScannerAdapter[], AgentLogger)` constructor body. This ensures `_activeAdapter` is never left in a provably-null state after construction, preventing latent `NullReferenceException` bugs if `Scan()` is called before the first successful adapter scan.

---

### WR-01: Lock ordering violates "acquire in same order" convention

**Files modified:** `src/FingerprintAgent/Adapters/ScannerManager.cs`
**Commit:** `50ac9a5` — `fixup(03): WR-01 document lock ordering policy in ScannerManager class doc`
**Applied fix:** Added a lock ordering policy comment block to the `ScannerManager` class doc comment:
```csharp
/// Lock ordering policy (DO-01):
/// 1. Always acquire _adapterLock before _backoffLock
/// 2. Never acquire _backoffLock without also holding _adapterLock
/// 3. UpdatePriority() must NOT be modified to take _backoffLock
```
This documents the lock ordering contract to prevent future code changes from introducing deadlock. The pattern `Scan()` acquiring `_adapterLock` then `_backoffLock` is safe only as long as `UpdatePriority()` never takes `_backoffLock`.

---

### WR-02: OnConfigReloaded silently skips when `_httpServer` is null

**Files modified:** `src/FingerprintAgent/Service/FingerprintAgentService.cs`
**Commit:** `e014eb3` — `fixup(03): WR-02 add warning log when _httpServer is null in OnConfigReloaded`
**Applied fix:** Replaced the silent null-conditional `_httpServer?.UpdateCorsConfig(...)` with an explicit null-check and warning log:
```csharp
if (_httpServer != null)
    _httpServer?.UpdateCorsConfig(newConfig.Cors);
else
    _logger?.Warn(cid, "OnConfigReloaded: _httpServer is null, skipping CORS update");
```

---

### WR-03: HealthCheckCallback only logs when disconnected

**Files modified:** `src/FingerprintAgent/Service/FingerprintAgentService.cs`
**Commit:** `315bbe6` — `fixup(03): WR-03 add connected heartbeat log in HealthCheckCallback`
**Applied fix:** Added an `else` branch in `HealthCheckCallback` to emit a debug-level heartbeat log when the scanner is connected:
```csharp
else
{
    _logger?.Debug(null, "HealthCheck: scanner connected");
}
```
This enables operational distinction between "scanner connected" and "callback not firing."

---

### WR-04: UpdatePriority() creates new adapters but never disposes old ones

**Files modified:** `src/FingerprintAgent/Adapters/ScannerManager.cs`
**Commit:** `21b9804` — `fixup(03): WR-04 document adapter disposal trade-off in UpdatePriority`
**Applied fix:** Added a note to the `UpdatePriority()` doc comment:
```csharp
/// Note: old adapters are NOT disposed here because _activeAdapter might reference
/// one of them. This is an intentional trade-off (D-09). Dispose is called only
/// when ScannerManager.Dispose() is called at service shutdown.
```
This documents the resource-leak trade-off as an intentional design decision rather than an oversight, making it clear to future maintainers that disposal is deferred to service shutdown.

---

### WR-05: OnStop() bare `catch {}` — pre-existing anti-pattern

**Files modified:** `src/FingerprintAgent/Service/FingerprintAgentService.cs`
**Commit:** `bbd5a43` — `fixup(03): WR-05 replace bare catch in OnStop with debug log`
**Applied fix:** Replaced the bare `catch { }` with a debug log to surface disposal exceptions:
```csharp
catch (Exception ex) { _logger?.Debug(null, $"healthCheckTimer disposal threw: {ex.Message}"); }
```

---

## Build & Test Results

- **Build:** `dotnet build FingerprintAgent.sln -c Release` — 0 errors, 0 new warnings (2 pre-existing xUnit1031 warnings in test code)
- **Tests:** `dotnet test FingerprintAgent.sln` — 49 passed, 0 failed

---

_Fixed: 2026-07-30_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_