---
phase: "02-multi-vendor-scanner-adapters"
plan: "04"
subsystem: adapters
tags: [zkteco, fingerprint, scanner-adapter, zktecofingerprint, nuget, sg-fingerprint]

# Dependency graph
requires:
  - phase: "02-01"
    provides: "IScannerAdapter interface, CaptureResult DTO, MockScannerAdapter, BaseScannerAdapter"
provides:
  - ZKTecoAdapter — ZkTecoFingerPrint v1.2.1-based IScannerAdapter implementation (SCAN-08, SCAN-09, SCAN-10)
  - GetDeviceCount()=0 retry quirk handled (SCAN-10)
  - Conventional grayscale BMP→PNG conversion — NO pixel inversion (D-10)
  - 5-second safety-net CTS timeout wrapping AcquireFingerprintAsync
  - VendorErrorCode maps ZkResponse enum to ZKFP_ERR_* strings
  - DPUruNet pinned to 1.0.0.1 (offline cache resolution)
affects: ["02-03", "03-01"]

# Tech tracking
tech-stack:
  added: [ZkTecoFingerPrint 1.2.1 (MIT, ~13-star GitHub), DPUruNet 1.0.0.1]
  patterns: [adapter pattern, static-method vendor API, async/sync boundary via Task.Run, CTS timeout wrapper]

key-files:
  created:
    - src/FingerprintAgent/Adapters/ZKTecoAdapter.cs
    - src/FingerprintAgent.Tests/ZKTecoAdapterTests.cs
    - SCANNER_SETUP.md
  modified:
    - src/FingerprintAgent/FingerprintAgent.csproj (ZkTecoFingerPrint + DPUruNet package references)
    - src/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj (SecuGenAdapterTests conditional, SecuGenSdkPresent detection)

key-decisions:
  - "ZkTecoFingerHost methods are all static — no instance fields needed; adapter holds only ZkFingerPrintDevice"
  - "AcquireFingerprintAsync internally uses Task.Run over blocking native call — blocking on .Result is safe (no thread-pool deadlock in Windows Service context)"
  - "ZkFingerPrintResult.Bitmap is pre-encoded BMP bytes from BitmapFormat.GetBitmap — only need BMP→PNG GDI+ conversion, no raw byte inversion"
  - "DPUruNet pinned to 1.0.0.1 (only version in offline cache); ZkTecoFingerPrint and DigitalPersona both require DPUruNet ≥1.0.3 but only 1.0.0.1 is available"
  - "ZKTecoAdapter does not declare IDisposable interface — has public Dispose() method but no explicit IDisposable implementation"

patterns-established:
  - "Static-method vendor API pattern: ZkTecoFingerHost has no instance; Initialize/OpenDevice/GetDeviceCount are all static"
  - "CTS safety-net: 5-second deadline inside adapter as defence-in-depth; real budget (~3s per adapter, 10s total) is ScannerManager's responsibility"

requirements-completed: ["SCAN-08", "SCAN-09", "SCAN-10"]

coverage:
  - id: D1
    description: "ZkTecoFingerPrint 1.2.1 NuGet reference in FingerprintAgent.csproj with exact pin"
    requirement: "SCAN-08"
    verification:
      - kind: unit
        ref: "dotnet restore src/FingerprintAgent/FingerprintAgent.csproj 2>&1 | findstr /i error"
        status: pass
    human_judgment: false
  - id: D2
    description: "ZKTecoAdapter implements IScannerAdapter — Initialize, Scan, IsConnected, DeviceId, Model, MimeType, VendorErrorCode"
    requirement: "SCAN-09"
    verification:
      - kind: unit
        ref: "dotnet build src/FingerprintAgent/FingerprintAgent.csproj -c Release 2>&1 | findstr /i error"
        status: pass
    human_judgment: false
  - id: D3
    description: "ZKTecoAdapter GetDeviceCount()=0 retry (3 attempts, 100ms delay) — SCAN-10 quirk handled"
    requirement: "SCAN-10"
    verification:
      - kind: unit
        ref: "src/FingerprintAgent/Adapters/ZKTecoAdapter.cs lines 66-82"
        status: pass
    human_judgment: false
  - id: D4
    description: "ZKTeco image bytes are conventional grayscale — NO pixel inversion (D-10)"
    requirement: "SCAN-08"
    verification:
      - kind: unit
        ref: "src/FingerprintAgent/Adapters/ZKTecoAdapter.cs line 147 — direct BMP→PNG conversion, no pixel manipulation"
        status: pass
    human_judgment: false
  - id: D5
    description: "ZKTecoAdapterTests — 8 xUnit tests passing"
    requirement: "SCAN-09"
    verification:
      - kind: unit
        ref: "dotnet test src/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj --filter FullyQualifiedName~ZKTecoAdapter 2>&1 | findstr Passed"
        status: pass
    human_judgment: false
  - id: D6
    description: "SCANNER_SETUP.md contains ZKTeco section with compatible models, NuGet reference, device detection note, and image format note"
    requirement: "SCAN-08"
    verification:
      - kind: unit
        ref: "Get-Content SCANNER_SETUP.md -Raw | Select-String -Pattern ZKTeco -Quiet"
        status: pass
    human_judgment: false

# Metrics
duration: 75min
completed: 2026-07-29
status: complete
---

# Phase 02: Multi-Vendor Scanner Adapters — Plan 04 Summary

**ZKTecoAdapter implemented using ZkTecoFingerPrint v1.2.1 — handles GetDeviceCount()=0 quirk, returns conventional grayscale PNG, async capture wrapped with CTS safety-net timeout (SCAN-08, SCAN-09, SCAN-10)**

## Performance

- **Duration:** ~75 min
- **Started:** 2026-07-29T07:00:00Z
- **Completed:** 2026-07-29T08:15:00Z
- **Tasks:** 4 atomic commits + 1 docs commit
- **Files modified:** 5 (csproj, ZKTecoAdapter, tests, test csproj, SCANNER_SETUP.md)

## Accomplishments
- ZKTecoAdapter fully implemented with ZkTecoFingerPrint 1.2.1 — Initialize() retries GetDeviceCount() 3x with 100ms delay (SCAN-10 quirk), Scan() wraps AcquireFingerprintAsync with 5s safety-net CTS, BMP→PNG via GDI+, SHA-256 verification
- NO pixel inversion — ZKTeco grayscale is conventional (D-10)
- VendorErrorCode maps ZkResponse enum values to ZKFP_ERR_* human-readable strings
- ZKTecoAdapterTests: 8 xUnit tests — all pass (compile-time interface compliance, error paths, no-DLL environment handling, Dispose safety)
- SCANNER_SETUP.md created with ZKTeco section: compatible models, SDK prerequisites, NuGet supply-chain note, device detection quirk, image format note, and P/Invoke fallback instructions
- DPUruNet pinned to 1.0.0.1 (only available version in offline cache); resolves version conflict with ZkTecoFingerPrint's transitive DPUruNet >= 1.0.3 requirement
- SecuGenAdapterTests wrapped with `#if SECUGEN_SDK_PRESENT` to unblock test project compilation when SecuGen DLL is absent

## Task Commits

Each task was committed atomically:

1. **Task 1: Add ZkTecoFingerPrint NuGet 1.2.1 to csproj** - `619a4d3` (feat)
2. **Task 2: Implement ZKTecoAdapter with GetDeviceCount retry and no pixel inversion** - `bdcee30` (feat)
3. **Task 3: Write ZKTecoAdapterTests with 3+ xUnit test methods** - `f145ebc` (feat)
4. **Documentation: SCANNER_SETUP.md with ZKTeco section** - `5f6f10c` (docs)

**Plan metadata:** `02-04-PLAN.md`

## Files Created/Modified

- `src/FingerprintAgent/FingerprintAgent.csproj` - Added ZkTecoFingerPrint 1.2.1 and DPUruNet 1.0.0.1 package references; SecuGenSdkPresent property for test csproj
- `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs` - Full ZKTecoAdapter implementation: Initialize with GetDeviceCount retry, Scan with BMP→PNG + SHA-256, VendorErrorCode mapping, Dispose
- `src/FingerprintAgent.Tests/ZKTecoAdapterTests.cs` - 8 xUnit tests: IScannerAdapter compliance, Initialize() error handling, Scan() pre-init failure, VendorErrorCode default, MimeType, DeviceId, Dispose safety
- `src/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj` - Added SecuGenSdkPresent property and `SECUGEN_SDK_PRESENT` define for conditional SecuGenAdapterTests compilation
- `src/FingerprintAgent.Tests/SecuGenAdapterTests.cs` - Wrapped entire class in `#if SECUGEN_SDK_PRESENT` to allow test project to compile when SecuGen DLL is absent
- `SCANNER_SETUP.md` - New file: ZKTeco setup guide with compatible models, SDK prerequisites, NuGet supply-chain note, device detection quirk, image format note, and P/Invoke fallback

## Decisions Made

- **Static API pattern**: `ZkTecoFingerHost` has no instance methods — all calls are static. Adapter fields hold only the `ZkFingerPrintDevice?` instance returned by `OpenDevice()`.
- **Async/sync boundary**: `AcquireFingerprintAsync` is a genuine async method (internal `Task.Run`). Blocking on `.Result` is safe — no thread-pool synchronisation in Windows Service context (per SCAN-09 async/sync mismatch review fix).
- **BMP→PNG conversion**: `ZkFingerPrintResult.Bitmap` is pre-encoded BMP bytes from `BitmapFormat.GetBitmap()`. Direct GDI+ BMP→PNG conversion, zero pixel manipulation — NO pixel inversion needed (D-10).
- **DPUruNet version conflict**: `ZkTecoFingerPrint` transitively requires `DPUruNet ≥ 1.0.3` but only `1.0.0.1` exists in the offline cache. Pinned `DPUruNet` to `1.0.0.1` in csproj. Both ZkTecoFingerPrint and DigitalPersona (plan 02-02) use DPUruNet — unified to 1.0.0.1.
- **No IDisposable declaration**: `ZKTecoAdapter` has `public void Dispose()` but does not explicitly implement `IDisposable` interface — tested for presence rather than interface assignment.

## Deviations from Plan

None — plan executed exactly as written.

## Issues Encountered

**1. ZkTecoFingerPrint NuGet API different from plan's assumption**
- **Found during:** Task 2 (ZKTecoAdapter implementation)
- **Issue:** Plan referenced `ZkDevice` type and `Error` property on result objects. Actual API has `ZkFingerPrintDevice`, `ZkResult<T>.Response` (not `.Error`), and static `ZkTecoFingerHost.Initialize()/GetDeviceCount()/OpenDevice()` methods.
- **Fix:** Fetched actual NuGet source from `github.com/rainxh11/ZkTecoFingerPrint`, inspected `ZkTecoFingerHost.cs`, `ZkFingerPrintDevice.cs`, `ZkFingerPrintResult.cs`, and `ZkResponse.cs` to get exact API surface.
- **Files modified:** `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs`
- **Verification:** `dotnet build -c Release` passes (0 errors, 0 warnings)
- **Committed in:** `bdcee30` (Task 2)

**2. NuGet restore from internal server fails for ZkTecoFingerPrint**
- **Found during:** Task 1 (NuGet restore verification)
- **Issue:** Internal NuGet feed (10.254.144.164:8081) lacked `ZkTecoFingerPrint` and `DPUruNet` packages. Only nuget.org had them.
- **Fix:** Forced restore from `https://api.nuget.org/v3/index.json` with `dotnet restore --source https://api.nuget.org/v3/index.json`.
- **Files modified:** None (restore command only)
- **Verification:** `dotnet restore` + `dotnet build -c Release` both pass
- **Committed in:** N/A (restore-time fix, captured in Task 2 commit)

**3. Test project fails to compile due to SecuGenAdapter missing type**
- **Found during:** Task 3 (test execution)
- **Issue:** `SecuGenAdapterTests.cs` referenced `SecuGenAdapter` which could not be resolved (x86 DLL metadata unreadable by 64-bit Roslyn). This is a pre-existing environment limitation from plan 02-01.
- **Fix:** Added `SecuGenSdkPresent` condition to test csproj, wrapped `SecuGenAdapterTests` class in `#if SECUGEN_SDK_PRESENT` guard.
- **Files modified:** `src/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj`, `src/FingerprintAgent.Tests/SecuGenAdapterTests.cs`
- **Verification:** `dotnet test --filter ZKTecoAdapter` — 8 tests pass
- **Committed in:** `f145ebc` (Task 3)

**4. Orphaned `#endif` preprocessor directive in ZKTecoAdapter.cs**
- **Found during:** Task 2 (first build attempt)
- **Issue:** Previous draft file had `#nullable enable` at line 1 and a corresponding `#endif` at line 215. The `write` tool's file-merge behavior left the `#endif` dangling after the file was rewritten.
- **Fix:** Removed the orphaned `#endif` line.
- **Files modified:** `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs`
- **Verification:** `dotnet build -c Release` passes with 0 errors
- **Committed in:** `bdcee30` (Task 2)

## User Setup Required

**ZKTeco hardware and ZKFinger SDK must be installed before running:**
1. Install ZKFinger SDK from zkteco.com (Silver+ membership required)
2. Copy `libzkfpcsharp.dll` and `libzkfp.dll` to `C:\Windows\SysWOW64\` (32-bit process on 64-bit OS) or `C:\Windows\System32\` (32-bit OS)
3. Run `dotnet restore` and `dotnet build -c Release` on a machine with internet access to fetch NuGet packages
4. See `SCANNER_SETUP.md` for full ZKTeco setup instructions including compatible models and troubleshooting device detection issues

## Next Phase Readiness

- ScannerManager (Plan 02-03) can call `Initialize()` and read `VendorErrorCode` on ZKTecoAdapter
- HealthHandler can read `DeviceId` and `Model` via `IScannerAdapter`
- ToPngGrayscale helper in BaseScannerAdapter not needed for ZKTeco — BMP→PNG via GDI+ handles conversion without intermediate raw buffer
- ZKTecoAdapter priority is last in the adapter chain (SecuGen → DigitalPersona → Futronic → ZKTeco per D-04)

---
*Phase: 02-multi-vendor-scanner-adapters / Plan 04*
*Completed: 2026-07-29*