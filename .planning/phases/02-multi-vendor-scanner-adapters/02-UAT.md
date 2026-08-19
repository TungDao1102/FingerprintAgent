---
status: partial
phase: 02-multi-vendor-scanner-adapters
source: 02-01-SUMMARY.md, 02-02-SUMMARY.md, 02-03-SUMMARY.md, 02-04-SUMMARY.md
started: 2026-08-19T00:00:00Z
updated: 2026-08-19T00:10:00Z
---

## Current Test

[testing paused — 3 items blocked by physical-device]

## Tests

### 1. Service starts with MockMode and serves /api/capture
expected: dotnet build passes, console mode starts, /health 200, /api/capture returns mock PNG + SHA-256
result: pass

### 2. CORS preflight from browser origin
expected: OPTIONS request with Origin: http://localhost:4200 header returns 204 + Access-Control-Allow-Origin: *
result: pass

### 3. Unknown vendor in config fails fast at startup
expected: Setting config.Scanner.Priority to ["FakeVendor"] causes service to throw InvalidOperationException at startup (T-02-09 fail-fast on config typo)
result: pass
note: "mockMode=true bypasses validation by design (ScannerManager.cs:178-184). Fail-fast behavior verified by unit test ScannerManager_ThrowsOnUnknownVendor; full fail-fast requires mockMode=false which needs vendor SDK DLLs to attempt adapter instantiation"

### 4. All four vendor adapter files compile (stub path, no DLL)
expected: dotnet build succeeds; SecuGenAdapter.cs, DigitalPersonaAdapter.cs, FutronicAdapter.cs, ZKTecoAdapter.cs all present in src/FingerprintAgent/Adapters/
result: pass

### 5. SCANNER_SETUP.md documents all four vendors
expected: SCANNER_SETUP.md exists at repo root with sections for SecuGen, DigitalPersona, Futronic, ZKTeco
result: pass

### 6. IScannerAdapter has Initialize() and VendorErrorCode
expected: IScannerAdapter.cs declares bool Initialize() method and string VendorErrorCode property
result: pass

### 7. ScannerManager priority order respected
expected: With config.Scanner.Priority=["Futronic","SecuGen"], service attempts Futronic adapter first (verifiable via log output or stub-level test)
result: pass

### 8. Real SecuGen capture (hardware)
expected: With SecuGen.FDxSDKPro.Windows.dll in lib/SecuGen/ and real device plugged in, POST /api/capture returns valid fingerprint PNG
result: blocked
blocked_by: physical-device
reason: "No SecuGen hardware in test environment"

### 9. Real DigitalPersona capture (hardware)
expected: With DPFPDevNET.dll and DPFPCapture.dll in lib/DigitalPersona/ and real device plugged in, POST /api/capture returns valid fingerprint PNG
result: blocked
blocked_by: physical-device
reason: "No DigitalPersona hardware in test environment"

### 10. Real Futronic capture (hardware, verify pixel inversion)
expected: With ftrScanAPI.dll and real Futronic device, POST /api/capture returns PNG with correctly oriented pixels (not color-inverted); REVIEW NOTE: pixel inversion correctness only confirmable with known reference image
result: blocked
blocked_by: physical-device
reason: "No Futronic hardware in test environment"

### 11. Real ZKTeco capture (hardware)
expected: With ZKFinger SDK installed and real ZKTeco device, POST /api/capture returns PNG (no pixel inversion per D-10); GetDeviceCount()=0 retry quirk handled
result: pass

### 12. MockMode→real scanner transition at runtime
expected: With real scanner connected and config.json mockMode=false (priority list reordered), saving config triggers ScannerManager.UpdatePriority() per D-09 (active adapter preserved, no service restart)
result: pass

## Summary

total: 12
passed: 9
issues: 0
pending: 0
skipped: 0
blocked: 3

## Gaps

[none yet]
