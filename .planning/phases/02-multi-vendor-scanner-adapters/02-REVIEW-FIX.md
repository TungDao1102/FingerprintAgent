---
phase: 02-multi-vendor-scanner-adapters
fixed_at: 2026-07-29T12:00:00Z
review_path: .planning/phases/02-multi-vendor-scanner-adapters/02-REVIEW.md
iteration: 1
findings_in_scope: 9
fixed: 9
skipped: 0
status: all_fixed
---

# Phase 02: Code Review Fix Report

**Fixed at:** 2026-07-29T12:00:00Z
**Source review:** `.planning/phases/02-multi-vendor-scanner-adapters/02-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 9 (3 critical, 6 warning)
- Fixed: 9
- Skipped: 0

## Fixed Issues

### CR-NEW-01: BaseScannerAdapter.Scan() double-initializes device

**Files modified:** `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs`
**Commit:** `e41c10e`
**Applied fix:** Removed the `if (!InitializeDevice())` guard from `BaseScannerAdapter.Scan()`. `ScannerManager` already calls `Initialize()` before `Scan()`, so the redundant `InitializeDevice()` call caused `SecuGenAdapter` to fail every scan through `ScannerManager` (error 59: `ERROR_DEV_ALREADY_OPEN`). Subclasses' `CaptureRawImage()` already have null-guards, and `Scan()` handles null/empty results from `CaptureRawImage()`.

---

### CR-NEW-02: `FUTRONIC_SDK_PRESENT` never defined in csproj

**Files modified:** `src/FingerprintAgent/FingerprintAgent.csproj`
**Commit:** `c79477b`
**Applied fix:** Added `FutronicSdkPresent` property that auto-detects `lib/Futronic/ftrScanAPI.dll` using `Exists()` MSBuild condition, and a matching `DefineConstants` block that sets `FUTRONIC_SDK_PRESENT` when the DLL is detected — following the same pattern as `SecuGenSdkPresent`/`ZKTecoSdkPresent`/`DigitalPersonaSdkPresent`.

---

### CR-NEW-03: `ftrScanGetLastError()` P/Invoke call missing required `_device` argument

**Files modified:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs`
**Commit:** `450aba1`
**Applied fix:** Changed line 92 from `FutronicSDK.ftrScanGetLastError()` to `FutronicSDK.ftrScanGetLastError(_device)` — the P/Invoke declaration requires an `IntPtr device` parameter. This was a latent compile error (CS7036) masked by CR-NEW-02, which would break the build as soon as `FUTRONIC_SDK_PRESENT` is defined.

---

### WR-NEW-01: FutronicAdapter leaks device handle on repeated Initialize/Scan cycles

**Files modified:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs`
**Commit:** `af8c03c`
**Applied fix:** Added a guard at the start of `Initialize()` that closes the existing `_device` handle (if non-zero) before opening a new one via `ftrScanOpenDevice()`. This prevents handle leaks across repeated scan cycles in a long-running service.

---

### WR-NEW-02: SecuGenAdapter leaks `_fpm` object on repeated Initialize/Scan cycles

**Files modified:** `src/FingerprintAgent/Adapters/SecuGenAdapter.cs`
**Commit:** `03f2b87`
**Applied fix:** Added a guard at the start of `InitializeDevice()` that disposes the previous `_fpm` instance (via `(_fpm as IDisposable)?.Dispose()`) and sets it to null before creating a new `SGFingerPrintManager()`. This prevents leaking the SDK's native resources across repeated scan cycles.

---

### WR-NEW-03: Dead code — `DestroyHbitmap` and `DeleteObject` never called

**Files modified:** `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs`
**Commit:** `eb30134`
**Applied fix:** Removed the unused `DeleteObject` P/Invoke declaration and `DestroyHbitmap` wrapper method (lines 201-208). The comments already explain that `Bitmap.FromHbitmap` + `bmp.Dispose()` handles GDI cleanup and that calling `DestroyHbitmap` separately would be a double-delete.

---

### WR-NEW-04: FutronicAdapter missing `IDisposable` implementation

**Files modified:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs`
**Commit:** `17377ed`
**Applied fix:** Added `IDisposable` to both the real `#if FUTRONIC_SDK_PRESENT` implementation and the `#else` stub. The real `Dispose()` closes the device handle via `ftrScanCloseDevice()` and sets `_device = IntPtr.Zero`. The stub provides an empty `Dispose()`. This enables `ScannerManager.Dispose()` to clean up Futronic resources.

---

### WR-NEW-05: SecuGenAdapter missing `IDisposable` implementation

**Files modified:** `src/FingerprintAgent/Adapters/SecuGenAdapter.cs`
**Commit:** `3295bb2`
**Applied fix:** Added `IDisposable` to `SecuGenAdapter`. `Dispose()` releases the `_fpm` instance (via `(_fpm as IDisposable)?.Dispose()`) and sets it to null. This enables `ScannerManager.Dispose()` to clean up SecuGen resources on shutdown.

---

### WR-NEW-06: `ScannerManager.Dispose()` only disposes `ActiveAdapter` — non-active initialized adapters leak

**Files modified:** `src/FingerprintAgent/Adapters/ScannerManager.cs`
**Commit:** `345f1a8`
**Applied fix:** Modified `Dispose()` to iterate all adapters in the `_adapters` array and dispose any that implement `IDisposable`, in addition to the existing active adapter disposal. Added a null guard for `_adapters` (which is null in mock mode) to prevent `NullReferenceException`. This ensures that adapters initialized during the priority fallback loop but not selected as active are properly cleaned up.

---

## Summary

| Category | In Scope | Fixed | Skipped |
|----------|----------|-------|---------|
| Critical | 3 | 3 | 0 |
| Warning  | 6 | 6 | 0 |
| **Total** | **9** | **9** | **0** |

All 9 findings in scope were fixed successfully with no skipped issues.

---

_Fixed: 2026-07-29T12:00:00Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
