---
phase: 02-multi-vendor-scanner-adapters
reviewed: 2026-07-29T12:00:00Z
depth: standard
files_reviewed: 20
files_reviewed_list:
  - src/FingerprintAgent/Adapters/BaseScannerAdapter.cs
  - src/FingerprintAgent/Adapters/SecuGenAdapter.cs
  - src/FingerprintAgent/Adapters/IScannerAdapter.cs
  - src/FingerprintAgent/Adapters/MockScannerAdapter.cs
  - src/FingerprintAgent/Adapters/CaptureResult.cs
  - src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs
  - src/FingerprintAgent/Adapters/FutronicAdapter.cs
  - src/FingerprintAgent/Adapters/ScannerManager.cs
  - src/FingerprintAgent/Adapters/ZKTecoAdapter.cs
  - src/FingerprintAgent/FingerprintAgent.csproj
  - src/FingerprintAgent/Service/FingerprintAgentService.cs
  - src/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj
  - src/FingerprintAgent.Tests/SecuGenAdapterTests.cs
  - src/FingerprintAgent.Tests/DigitalPersonaAdapterTests.cs
  - src/FingerprintAgent.Tests/FutronicAdapterTests.cs
  - src/FingerprintAgent.Tests/ScannerManagerTests.cs
  - src/FingerprintAgent.Tests/ZKTecoAdapterTests.cs
  - src/FingerprintAgent.Host/FingerprintAgent.Host.csproj
  - src/FingerprintAgent.Host/Program.cs
  - SCANNER_SETUP.md
findings:
  critical: 3
  warning: 6
  info: 1
  total: 10
status: issues_found
---

# Phase 02: Code Review Report (Re-review after fix commits)

**Reviewed:** 2026-07-29T12:00:00Z
**Depth:** standard
**Files Reviewed:** 20
**Status:** issues_found — 3 critical, 6 warning, 1 info

---

## Fix Verification Status

All 12 previously resolved findings from commits 4c0a7b6–f586dd8 are **verified correct**:

| Finding | Commit | Status |
|---------|--------|--------|
| CR-02: ZKTecoAdapter IDisposable | 4c0a7b6 | ✓ Verified — line 20 |
| CR-03: DIGITALPERSONA_SDK_PRESENT in csproj | d5398d5 | ✓ Verified — lines 24-26 |
| CR-04: #if gate removed from SecuGenAdapterTests | 23d7244 | ✓ Verified — no `#if` blocks in test file |
| WR-01: IDisposable + cleanup in DigitalPersonaAdapter | 3061430 | ✓ Verified — lines 19, 221-227 |
| WR-02: Actual Width/Height from bitmap | 2c62e4a | ✓ Verified — lines 127-128 |
| WR-03: Stride-aligned row copy in ToPngGrayscale | 0691abe | ✓ Verified — line 93 uses stride |
| WR-05: Remove Close() from ZKTecoAdapter.Dispose() | 43d8b29 | ✓ Verified — lines 197-214 |
| WR-06: Lock on _activeAdapter | 5f01bca | ✓ Verified — lines 32-36 |
| WR-07: Non-mock ScannerManager fallback tests | 810988c | ✓ Verified — tests present (lines 159-276) |
| WR-08: 8-bit grayscale in MockScannerAdapter | 3a55e69 | ✓ Verified — Format8bppIndexed used |
| WR-09: GetAwaiter().GetResult() over .Result | 968bdf2 | ✓ Verified — line 122 |
| WR-10: Forward-compatible test assertions | f586dd8 | ✓ Verified — try/catch handles missing native DLLs |

---

## Critical Issues

### CR-NEW-01: BaseScannerAdapter.Scan() double-initializes device — SecuGenAdapter fails every scan through ScannerManager

**File:** `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs:28`
**Also affects:** `src/FingerprintAgent/Adapters/SecuGenAdapter.cs:71`

**Issue:** `BaseScannerAdapter.Scan()` calls `InitializeDevice()` on line 28 as a guard. However, `ScannerManager.Scan()` already calls `adapter.Initialize()` (= `InitializeDevice()`) immediately before calling `adapter.Scan()` (at `ScannerManager.cs:192`). This means `InitializeDevice()` is called **twice** per scan cycle.

For `SecuGenAdapter` in production with the real SDK:
1. `ScannerManager.Scan()` calls `adapter.Initialize()` → `SecuGenAdapter.InitializeDevice()` → opens USB device successfully.
2. `ScannerManager.Scan()` calls `adapter.Scan()` → `BaseScannerAdapter.Scan()` → calls `InitializeDevice()` AGAIN → creates new `SGFingerPrintManager`, calls `OpenDevice()` on already-open USB → SDK returns error code 59 (`ERROR_DEV_ALREADY_OPEN`) → returns `false`.
3. `Scan()` returns `CaptureResult.Fail("SCANNER_NOT_CONNECTED", ...)`.

**Impact:** `SecuGenAdapter` can **NEVER** successfully scan when used through `ScannerManager`. This is a production blocker for any deployment using SecuGen. In stub/test mode the issue is masked because the stub `Init()` always returns error code 55 (DEVICE_NOT_FOUND), so the double-call never reaches `OpenDevice`.

**Fix:** Remove the `InitializeDevice()` guard from `BaseScannerAdapter.Scan()`. The `ScannerManager` already handles initialization before `Scan()`. Subclasses' `CaptureRawImage()` already have null-guards (e.g., `SecuGenAdapter.CaptureRawImage()` checks `_fpm == null`), and `Scan()` already handles null/empty results from `CaptureRawImage()`:

```csharp
public CaptureResult Scan()
{
    // DELETE lines 28-31 — the if (!InitializeDevice()) guard.
    // ScannerManager calls Initialize() before Scan().

    byte[] raw;
    try
    {
        raw = CaptureRawImage();
    }
    // ...
}
```

---

### CR-NEW-02: `FUTRONIC_SDK_PRESENT` never defined — FutronicAdapter real implementation is dead code

**File:** `src/FingerprintAgent/FingerprintAgent.csproj`
**Also affects:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs:1`

**Issue:** `FutronicAdapter.cs` real implementation (lines 1–255, P/Invoke + all scanner logic) is guarded by `#if FUTRONIC_SDK_PRESENT`. The stub implementation (lines 256–280) is the `#else` branch. However, **`FUTRONIC_SDK_PRESENT` is never defined** in `FingerprintAgent.csproj`. All three other vendors have conditional property definitions that auto-detect SDK DLL presence and define the corresponding constant; Futronic is missing its entry:

| Vendor | Property | Constant | Status |
|--------|----------|----------|--------|
| SecuGen | `SecuGenSdkPresent` (line 11) | `SECUGEN_SDK_PRESENT` (line 21) | ✓ |
| ZKTeco | `ZKTecoSdkPresent` (line 12) | `ZKTECO_SDK_PRESENT` (line 17) | ✓ |
| DigitalPersona | `DigitalPersonaSdkPresent` (line 13) | `DIGITALPERSONA_SDK_PRESENT` (line 25) | ✓ |
| **Futronic** | **MISSING** | **`FUTRONIC_SDK_PRESENT` never defined** | ✗ |

**Impact:** The real FutronicAdapter can never be compiled or used. `Initialize()` always returns `false` (the stub returns false unconditionally). Any system relying on Futronic scanning will silently fall through to other adapters or fail.

**Fix:** Add a conditional property group for Futronic SDK presence, matching the other vendors' pattern:

```xml
<PropertyGroup>
  <FutronicSdkPresent Condition="Exists('$(MSBuildProjectDirectory)\..\..\lib\Futronic\ftrScanAPI.dll')">true</FutronicSdkPresent>
</PropertyGroup>

<PropertyGroup Condition="'$(FutronicSdkPresent)' == 'true'">
  <DefineConstants>$(DefineConstants);FUTRONIC_SDK_PRESENT</DefineConstants>
</PropertyGroup>
```

---

### CR-NEW-03: `ftrScanGetLastError()` P/Invoke call missing required argument — latent compile error

**File:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs:92, 195`

**Issue:** The P/Invoke declaration on line 195 requires an `IntPtr device` parameter:
```csharp
public static extern uint ftrScanGetLastError(IntPtr device);
```

But the call site on line 92 passes no arguments:
```csharp
uint err = FutronicSDK.ftrScanGetLastError();
```

In C#, calling a method with a required (non-optional) parameter without providing the argument is a **compile-time error** (CS7036). The entire `#if FUTRONIC_SDK_PRESENT` block fails to compile when the constant is defined.

**Impact:** This is a latent bug, currently masked by CR-NEW-02 (the constant is never defined). But as soon as someone adds the Futronic csproj condition to fix CR-NEW-02, the build breaks with:
```
error CS7036: There is no argument given that corresponds to the required formal parameter 'device'
```

**Fix:** Pass `_device` to the call. The Futronic SDK's `ftrScanGetLastError` takes a device handle:
```csharp
uint err = FutronicSDK.ftrScanGetLastError(_device);
```

---

## Warnings

### WR-NEW-01: FutronicAdapter leaks device handle on repeated Initialize/Scan cycles

**File:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs:41`

**Issue:** `Initialize()` calls `ftrScanOpenDevice()` (line 41) which allocates a native device handle. This handle is only freed on error paths — never on the success path. When `ScannerManager.Scan()` iterates through adapters on the next request, it calls `Initialize()` again, which calls `ftrScanOpenDevice()` again and overwrites the `_device` field without closing the previous handle.

For a long-running Windows service, repeated handle leaks across scan requests will exhaust system resources.

**Fix:** Close existing handle before opening a new one:
```csharp
public bool Initialize()
{
    _vendorErrorCode = "NONE";
    if (_device != IntPtr.Zero)
        FutronicSDK.ftrScanCloseDevice(_device);
    _device = FutronicSDK.ftrScanOpenDevice();
    if (_device == IntPtr.Zero) { /* existing error handling */ }
    // ... rest of existing method
}
```

---

### WR-NEW-02: SecuGenAdapter leaks `_fpm` object on repeated Initialize/Scan cycles

**File:** `src/FingerprintAgent/Adapters/SecuGenAdapter.cs:71`

**Issue:** Each call to `InitializeDevice()` creates a new `SGFingerPrintManager()` (line 71) without releasing the previous one. Since `ScannerManager.Scan()` calls `Initialize()` on every scan request, the old `_fpm` reference is overwritten and its SDK resources are leaked. The stub `SGFingerPrintManager` doesn't hold native resources, but the real SDK's `SGFingerPrintManager` does.

**Fix:** Dispose the previous `_fpm` before allocating a new one. If the real SDK's object implements `IDisposable`, call `_fpm?.Dispose()`:

```csharp
public override bool InitializeDevice()
{
    if (_fpm != null)
    {
        // Dispose/release previous instance if SDK supports it
    }
    _fpm = new SGFingerPrintManager();
    // ... rest of method
}
```

---

### WR-NEW-03: Dead code — `DestroyHbitmap` and `DeleteObject` never called

**File:** `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs:201-208`

**Issue:** `DestroyHbitmap(IntPtr)` (line 204) and its P/Invoke dependency `DeleteObject(IntPtr)` (line 201) are defined but **never called**. The comments on lines 138-141 explicitly explain why they should NOT be called (the Bitmap dispose handles it, and calling `DestroyHbitmap` separately would be a double-delete). The dead methods remain as a maintenance burden.

**Fix:** Remove the dead code:
```csharp
// Delete lines 201-208 entirely.
```

---

### WR-NEW-04: FutronicAdapter missing `IDisposable` implementation

**File:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs:18`

**Issue:** `FutronicAdapter` allocates a native device handle via `ftrScanOpenDevice()` but does not implement `IDisposable`. Both `DigitalPersonaAdapter` and `ZKTecoAdapter` implement `IDisposable`, enabling `ScannerManager.Dispose()` (line 231) to clean up on shutdown. `FutronicAdapter` is skipped by the `(adapter as IDisposable)?.Dispose()` pattern, leaking the handle until process exit.

**Fix:** Implement `IDisposable`:
```csharp
public class FutronicAdapter : IScannerAdapter, IDisposable
{
    // ... existing members ...

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            FutronicSDK.ftrScanCloseDevice(_device);
            _device = IntPtr.Zero;
        }
        _isConnected = false;
    }
}
```

---

### WR-NEW-05: SecuGenAdapter missing `IDisposable` implementation

**File:** `src/FingerprintAgent/Adapters/SecuGenAdapter.cs:31`

**Issue:** `SecuGenAdapter` allocates an `SGFingerPrintManager` (which holds native SDK state) but does not implement `IDisposable`. This prevents `ScannerManager.Dispose()` from cleaning up its resources on shutdown.

**Fix:** Implement `IDisposable`:
```csharp
public class SecuGenAdapter : BaseScannerAdapter, IDisposable
{
    // ... existing members ...

    public void Dispose()
    {
        _fpm = null; // or _fpm?.Dispose() if the real SDK object implements IDisposable
    }
}
```

---

### WR-NEW-06: `ScannerManager.Dispose()` only disposes `ActiveAdapter` — non-active initialized adapters leak

**File:** `src/FingerprintAgent/Adapters/ScannerManager.cs:226-232`

**Issue:** `ScannerManager.Dispose()` only calls `(ActiveAdapter as IDisposable)?.Dispose()`. During the priority fallback loop in `Scan()`, multiple adapters may have had `Initialize()` called before one succeeds (e.g., SecuGen and Futronic may have opened devices before DigitalPersona succeeds). The failed adapters are never disposed, leaking their resources.

**Fix:** Dispose all adapters, not just the active one:
```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _cts?.Dispose();
    foreach (var adapter in _adapters)
        (adapter as IDisposable)?.Dispose();
    (ActiveAdapter as IDisposable)?.Dispose(); // also covers mock mode
}
```

---

## Info

### IN-NEW-01: `Environment.Exit(1); return;` — unreachable return statement

**File:** `src/FingerprintAgent.Host/Program.cs:35-36`

**Issue:** After `Environment.Exit(1)` on line 35, the `return;` on line 36 is unreachable (process terminates immediately). No behavioral impact, but misleading.

```csharp
Environment.Exit(1);
return;  // ← unreachable
```

**Fix:** Remove the unreachable `return;`.

---

## Summary

| Category | Count |
|----------|-------|
| Critical | 3 |
| Warning  | 6 |
| Info     | 1 |
| **Total** | **10** |

**Critical issues summary:**
1. **CR-NEW-01:** `BaseScannerAdapter.Scan()` double-calls `InitializeDevice()` — SecuGenAdapter fails every scan through ScannerManager (production blocker)
2. **CR-NEW-02:** `FUTRONIC_SDK_PRESENT` never defined in csproj — FutronicAdapter real implementation is dead code
3. **CR-NEW-03:** `ftrScanGetLastError()` P/Invoke call missing required `_device` argument — latent compile error

**Key concerns:**
- The double-initialization in `BaseScannerAdapter.Scan()` is the most impactful production issue — it was present before the fix round but not caught in the initial review. All adapter subclasses that extend `BaseScannerAdapter` (currently SecuGenAdapter) are affected.
- The FutronicAdapter has a cascade of issues: missing csproj constant (CR-NEW-02) → masked compile error (CR-NEW-03) → handle leak (WR-NEW-01) → missing IDisposable (WR-NEW-04). All must be resolved before Futronic can be used.
- Resource lifecycle cleanup across the adapter fallback pattern (WR-NEW-06) should be addressed before adding more vendor implementations.

---

_Reviewed: 2026-07-29T12:00:00Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
