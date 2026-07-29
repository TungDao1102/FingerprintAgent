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

**Supply-chain note:** This is a small open-source project. Pin to exact version `1.2.1` in `FingerprintAgent.csproj`. If the package is abandoned or compromised, fall back to the raw `zkfp2` P/Invoke documented in `.planning/phases/02-multi-vendor-scanner-adapters/02-RESEARCH.md` §5 Option A.

### Setup Steps
1. Ensure ZKFinger SDK is installed and the ZKTeco device USB driver is loaded
2. Verify `libzkfpcsharp.dll` exists in `C:\Windows\SysWOW64\` (for 32-bit process on 64-bit OS) or `C:\Windows\System32\` (32-bit OS)
3. Alternatively, copy `libzkfpcsharp.dll` and `libzkfp.dll` to the FingerprintAgent install directory next to `FingerprintAgent.exe` (per D-08)
4. Run `dotnet restore` to fetch the ZkTecoFingerPrint NuGet package

### Device Detection Note
On some driver versions, `GetDeviceCount()` may return 0 immediately after `Init()`. The ZKTecoAdapter retries up to 3 times with 100ms delays before declaring no device found. If devices still not detected, try unplugging and replugging the USB cable or restarting the service.

### Image Format
ZKTeco returns 8-bit conventional grayscale (0=white, 255=dark ridges). NO pixel inversion needed — this is different from Futronic which requires inversion.

### Timeout Behavior
`AcquireFingerprintAsync` has no built-in timeout. The ScannerManager enforces a 10-second total budget (D-06) and ~3 seconds per adapter (D-11). The ZKTecoAdapter carries an internal 5-second safety-net deadline as a defence-in-depth measure. If no finger is placed before the timeout, ZKTecoAdapter returns `CAPTURE_TIMEOUT`.

### ZKTeco Fallback (if NuGet is unavailable)
If `ZkTecoFingerPrint` NuGet cannot be used, implement the raw `zkfp2` P/Invoke as documented in `02-RESEARCH.md` §5 Option A. Replace the `ZkTecoFingerPrint` NuGet call in `ZKTecoAdapter.cs` with direct DllImport declarations for `ZKFPM_Init`, `ZKFPM_GetDeviceCount`, `ZKFPM_OpenDevice`, and `ZKFPM_AcquireFingerprint`.