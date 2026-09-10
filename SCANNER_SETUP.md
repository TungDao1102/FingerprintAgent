# Scanner Setup Guide

This document covers setup instructions for each fingerprint scanner vendor supported by FingerprintAgent.

---

## ZKTeco (ZkTecoFingerPrint NuGet + libzkfpcsharp.dll)

### Compatible Models
ZK4500, ZK6500, ZK7500, ZK8500, ZK8500R, ZK9500, SLK20R, SLK20M

### Prerequisites
1. Install ZKFinger SDK for Windows from zkteco.com (requires Silver+ membership)
   - The SDK installer copies `libzkfpcsharp.dll` and `libzkfp.dll` to `C:\Windows\System32`
   - On a 64-bit OS running a 32-bit process, the DLLs are loaded from SysWOW64
2. .NET Framework 4.8 with x86 build target (already configured in FingerprintAgent.csproj)

### NuGet Package
The `ZkTecoFingerPrint` NuGet (MIT, v1.2.1, ~13-star GitHub project) wraps the native DLLs and provides a sane async C# API.

**Implementation note (updated):** The `ZkTecoFingerPrint` NuGet wrapper was **removed** — the adapter now talks to `libzkfp.dll` directly via `Adapters/ZkNativeHost.cs` (raw P/Invoke, prior art: `tests/.../ZkSdkProbe.cs` + commit `4c7c358`). Capture sizes its own width×height buffer, skipping the parameter-106 query that fails on ZK9500 firmware. Transitive deps SourceAFIS/System.Reactive/Dahomey.Cbor are gone. If ZkNativeHost ever needs replacing, the historical wrapper remains documented in `.planning/phases/02-multi-vendor-scanner-adapters/02-RESEARCH.md` §5 Option B and the old review `.planning/reviews/release-crash-review.md` Phụ lục A.

### Setup Steps
1. Ensure ZKFinger SDK is installed and the ZKTeco device USB driver is loaded
2. Verify `libzkfpcsharp.dll` exists in `C:\Windows\SysWOW64\` (for 32-bit process on 64-bit OS) or `C:\Windows\System32\` (32-bit OS)
3. Alternatively, copy `libzkfpcsharp.dll` and `libzkfp.dll` to the FingerprintAgent install directory next to `FingerprintAgent.exe` (per D-08)
4. Run `dotnet restore` to fetch the ZkTecoFingerPrint NuGet package

### Device Detection Note
On some driver versions, `GetDeviceCount()` may return 0 immediately after `Init()`. The ZKTecoAdapter retries up to 3 times with 100ms delays before declaring no device found. If devices still not detected, try unplugging and replugging the USB cable or restarting the service.

### Image Format
ZKTeco returns 8-bit conventional grayscale (0=white, 255=dark ridges). NO pixel inversion needed — this is different from Futronic which requires inversion.

The `ZKTecoAdapter.Scan()` method encodes PNG directly from the raw 8-bit grayscale pixel buffer (which `AcquireFingerprintAsync(byte[], ...)` writes into the caller's buffer). It bypasses the wrapper's `BitmapFormat.GetBitmap()` step (which constructs a BMP file) and the `System.Drawing.Bitmap` decode/re-encode — saving 2 allocations and 1 BMP parse per capture.

### Timeout Behavior
The underlying `ZKFPM_AcquireFingerprint` call has an internal ~1s timeout per attempt. ZKTecoAdapter retries on `ERROR_CAPTURE` while elapsed time is below a 15-second adapter budget. Total user-visible wait window is ~15 seconds — enough time for "click button → reach for scanner → place finger".

### Cancellation
`Scan()` honors `CancellationToken` at the next retry checkpoint. The token is propagated by `ScannerManager`'s per-adapter 3s budget (D-06), so a hung blocking call will be cancelled at the next retry.

### Error Mapping
`ZkResponse` enum values (29 total) map to user-actionable messages in `CaptureResult.ErrorMessage`:
- `Capture` → "no finger detected within Xs — please place finger on sensor"
- `Busy` → "scanner is busy with another operation"
- `Timeout` → "capture timed out"
- `InvalidHandle` → "device handle invalidated — please retry, scanner will reinitialize"
- `NoDevice` → "no scanner detected — check USB connection"
- (etc.)

The raw `ZkResponse` string (e.g., `ERROR_CAPTURE`) is preserved in `VendorErrorCode` for IT debugging.

### ZKTeco Implementation (raw P/Invoke — current)
If `ZkTecoFingerPrint` NuGet cannot be used, implement the raw `zkfp2` P/Invoke as documented in `02-RESEARCH.md` §5 Option A. Replace the `ZkTecoFingerPrint` NuGet calls in `ZKTecoAdapter.cs` with direct DllImport declarations for `ZKFPM_Init`, `ZKFPM_GetDeviceCount`, `ZKFPM_OpenDevice`, `ZKFPM_CloseDevice`, and `ZKFPM_AcquireFingerprint`. This path was previously used in commit `4c7c358` based on a misdiagnosis that the wrapper had a bug; the actual issue was calling the parameterless overload of `AcquireFingerprintAsync` (which queries parameter 106, unimplemented on ZK9500).

---

## SecuGen (SecuGen.FDxSDKPro.Windows + sgfplib.dll)

### Compatible Models
Hamster Pro 20, Hamster IV (FDU04), Hamster III (FDU03), Hamster Plus, Hamster II

### Prerequisites
1. Download SecuGen FDx SDK Pro from [secugen.com/download](https://www.secugen.com/download) — free SDK registration required
2. Copy native DLLs from SDK `Bin\i386\` to the FingerprintAgent install directory:
   - `sgfplib.dll` — main driver module
   - `sgfpamx.dll` — algorithm module
3. The managed wrapper `SecuGen.FDxSDKPro.Windows.dll` is added via HintPath in `FingerprintAgent.csproj` pointing to `lib\SecuGen\`

### Download Links
- SecuGen FDx SDK Pro: https://www.secugen.com/download (free SDK — evaluation/development license only; verify distribution rights before production)

### Setup Steps
1. Register at secugen.com and download FDx SDK Pro
2. Copy `sgfplib.dll` and `sgfpamx.dll` from SDK `Bin\i386\` to `lib\SecuGen\` in the project
3. Copy `SecuGen.FDxSDKPro.Windows.dll` to `lib\SecuGen\`
4. The csproj conditional `SecuGenSdkPresent` property auto-detects the DLL and defines `SECUGEN_SDK_PRESENT`
5. Build with `SECUGEN_SDK_PRESENT` defined to use the real adapter; without it, stub types allow compilation

### Build Requirement
`<PlatformTarget>x86</PlatformTarget>` is mandatory — all three vendor SDKs are 32-bit. AnyCPU build will crash with `BadImageFormatException` on 64-bit Windows (D-05).

### Image Format
SecuGen raw buffer is 256-level grayscale. Convert to PNG using the `ToPngGrayscale` helper in `BaseScannerAdapter` (no pixel inversion needed — conventional grayscale).

### Distribution
All DLLs must go into the same folder as `FingerprintAgent.exe` per D-08.

**License note:** The free SecuGen SDK may be evaluation-only. Verify commercial distribution rights before shipping.

---

## Digital Persona (dpfpdd.dll — 32-bit P/Invoke)

### Compatible Models
Digital Persona U.are.U 4500, U.are.U 4500B, U.are.U 5160, U.are.U 5300

### Prerequisites
1. Install the U.are.U driver on the workstation — the driver provides `dpfpdd.dll` (in `System32`/`SysWOW64`)
2. No NuGet / managed wrapper needed — `DigitalPersonaAdapter` talks to `dpfpdd.dll` directly via `Adapters/DpNativeHost.cs` (raw P/Invoke, same pattern as `ZkNativeHost`). The former `DPUruNet 1.0.0.1` NuGet package and the conditional `DPFPDevNET.dll` managed reference were removed — the wrapper assembly was never referenced by compiled code.

### Setup Steps
1. The project builds with or without `lib\` — the adapter code compiles unconditionally (no `#if` gate)
2. Optional for local device testing: install the U.are.U SDK/driver, or copy the 32-bit `dpfpdd.dll` into `lib\DigitalPersona\` and add that folder to the DLL search path when running
3. Missing driver at runtime → `DRIVER_NOT_INSTALLED` / `SCANNER_NOT_CONNECTED` — never a crash

### Build Requirement
`<PlatformTarget>x86</PlatformTarget>` — the native DLL is 32-bit.

### Image Format
`DPFPDD_IMG_FMT_PIXEL_BUFFER` — raw 8bpp grayscale (width × height bytes), PNG-encoded via the shared `PngEncoder.ToPngGrayscale`. No pixel inversion needed.

### Distribution
No vendor DLL ships in the MSI (D-32a). The U.are.U driver installed on the user's workstation provides `dpfpdd.dll`; the loader finds it via the standard search path (app dir → SysWOW64/System32 → PATH).

---

## Futronic (ftrScanAPI.dll — 32-bit P/Invoke)

### Compatible Models
Futronic FS80, FS90, FS60, FM200u

### Prerequisites
1. Download Futronic Standard SDK v4.2 from [futronic-tech.com/download.html](http://www.futronic-tech.com/download.html) — registration required
2. Copy `ftrScanAPI.dll` to `lib\Futronic\` or directly alongside `FingerprintAgent.exe`

### Download Links
- Futronic Standard SDK v4.2: http://www.futronic-tech.com/download.html (registration required)

### Setup Steps
1. Register and download the Futronic Standard SDK
2. Copy `ftrScanAPI.dll` from the SDK to `lib\Futronic\` or the install directory
3. No NuGet package needed — P/Invoke loads the DLL by name at runtime

### Build Requirement
`<PlatformTarget>x86</PlatformTarget>` is **mandatory and non-negotiable** — `ftrScanAPI.dll` is 32-bit only. AnyCPU process on 64-bit Windows will crash with `DllNotFoundException` or `BadImageFormatException`.

### Image Format — CRITICAL: Pixel Inversion Required
Futronic raw buffer uses inverted grayscale: 0=dark (ridges), 255=white (background). The `FutronicAdapter` applies `255 - value` per pixel before PNG encoding. If images appear inverted on screen, the inversion is wrong — verify against a known test fingerprint image.

**REVIEW NOTE:** Pixel inversion in `FutronicAdapter` is based on research sources, not official documentation. Post-integrate verification against a known-good test image is required.

### Distribution
Copy `ftrScanAPI.dll` to the same folder as `FingerprintAgent.exe` per D-08.

**License note:** Futronic Standard SDK may be watermarked or evaluation-only. Verify distribution rights for production.

---

## ScannerManager — Build & Runtime Summary

### All Vendors
- **PlatformTarget:** `x86` (non-negotiable — all vendor SDKs are 32-bit)
- **Distribution:** All vendor DLLs must be in the same folder as `FingerprintAgent.exe`
- **NuGet packages used:** none for scanner SDKs — ZKTeco (`ZkNativeHost` → `libzkfp.dll`) and DigitalPersona (`DpNativeHost` → `dpfpdd.dll`) are pure P/Invoke; Futronic is direct P/Invoke; SecuGen uses a HintPath to `lib\SecuGen\`

### Priority Order (default)
`config.json` → `Scanner.Priority: ["ZKTeco"]` (code default in `AgentConfig` is also `["ZKTeco"]`)

ScannerManager tries adapters in this order on each `/api/capture` call, with the first successful scan winning. If all fail, returns `SCANNER_NOT_CONNECTED`.

### Timeout Strategy
- **Total budget**: 20 seconds (D-06, extended from 10s to accommodate ZK9500's full rolling-capture window).
- **Per-adapter budget**: NOT enforced by ScannerManager. Each adapter manages its own internal timeout (e.g., ZKTecoAdapter uses 15s rolling-capture). Per D-13, timeout enforcement is centralized — ScannerManager enforces the total budget via `CancellationTokenSource.CancelAfter(20s)`; individual adapters decide how to use the passed token at their own checkpoints.

### MockMode
Set `config.Scanner.MockMode: true` to bypass all real scanners and use `MockScannerAdapter` for development/testing without hardware.