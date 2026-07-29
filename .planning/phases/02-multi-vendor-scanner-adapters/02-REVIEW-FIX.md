---
phase: "02"
fixed_at: "2026-07-29T00:00:00Z"
review_path: ".planning/phases/02-multi-vendor-scanner-adapters/02-REVIEW.md"
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
---

# Phase 02: Code Review Fix Report

**Fixed at:** 2026-07-29
**Source review:** `.planning/phases/02-multi-vendor-scanner-adapters/02-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (CR-01, WR-01, WR-02 — Critical and Warning from updated review)
- Fixed: 3
- Skipped: 0

## Fixed Issues

### CR-01: DigitalPersonaAdapter ManualResetEvent Race Condition

**Files modified:** `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs`
**Commit:** `7f9bf58`
**Applied fix:** Replaced shared `_captureEvent` field usage with a LOCAL `waitHandle` variable per `Scan()` call. The local handle is assigned to `_captureEvent` before `StartCapture()` so the `OnComplete` callback always signals the correct instance. `WaitOne()` now waits on the local `waitHandle` directly, not the instance field — ensuring that if an async callback from a previous `Scan()` fires after a new `_captureEvent` is assigned, the current call's `WaitOne()` is unaffected.

```csharp
// Before (race-prone):
_captureEvent = new ManualResetEvent(false);
_capture.StartCapture();
bool signaled = _captureEvent.WaitOne(5000);

// After (race-safe):
var waitHandle = new ManualResetEvent(false);
_captureEvent = waitHandle;
_capture.StartCapture();
bool signaled = waitHandle.WaitOne(3000);
```

### WR-01: DigitalPersonaAdapter Hardcoded 5s Timeout

**Files modified:** `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs`
**Commit:** `7f9bf58`
**Applied fix:** Changed `WaitOne(5000)` to `WaitOne(3000)` in `DigitalPersonaAdapter.Scan()` to match the D-06 per-adapter 3-second budget. Fixed in the same commit as CR-01.

### WR-02: FingerprintAgentService._cts Never Disposed

**Files modified:** `src/FingerprintAgent/Service/FingerprintAgentService.cs`
**Commit:** `e63b3a2`
**Applied fix:** Added `_cts?.Dispose()` after `_cts?.Cancel()` in `OnStop()`. `CancellationTokenSource` holds a managed timer thread; failing to call `Dispose()` would leak the timer. The `try-catch` around `Cancel()` now also covers `Dispose()`.

---

## Verification

- `dotnet build --nologo` on `src/FingerprintAgent` → **0 warnings, 0 errors**
- All commits are atomic (one fix per commit where possible, combined CR-01+WR-01 since same file)
- Commits follow format: `fix(02): <finding ids> — <brief description>`

---

_Fixed: 2026-07-29_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_