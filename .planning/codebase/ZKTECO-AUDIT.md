# ZKTeco P/Invoke — Fix Plan

**Date**: 2026-08-24
**Scope**: 6 actionable items from comparative audit against SDK headers, vendor C++/C# demos, and upstream wrapper `rainxh11/ZkTecoFingerPrint@v1.2.1`
**Validation summary**: All 6 items below are independently confirmed by ≥2 of the 3 comparator sources. Wrapper `bf7d3ca` migration commit dropped wrapper bugs B1–B7 — none of those regressed.

---

## Execution Order

Apply in this order to minimize build churn and surface missed call sites early:

| Step | Item | Touches | Risk |
|------|------|---------|------|
| 1 | #6 — named parameter-code constants | `ZkNativeHost.cs` | None (additive) |
| 2 | #3 — `LockBits`/`UnlockBits` try/finally | `BaseScannerAdapter.cs` | None |
| 3 | #5 — extract `PngEncoder` | new file + 2 call sites | Low (mechanical refactor) |
| 4 | #7 — `ref int` → `ref uint` | signatures + 3 call sites | Medium (build surfaces misses) |
| 5 | #4 — defensive `AllocHGlobal` | `ZKTecoAdapter.cs:AcquireOnce` | None |
| 6 | #2 — missing error codes | `ZkNativeHost.cs` + `ZKTecoAdapter.cs` | None (additive) |

After each step:
```powershell
dotnet build FingerprintAgent.sln -c Release   # must be 0 warn / 0 err
dotnet test  FingerprintAgent.sln               # must pass (hardware tests skip)
```

---

## Issue #2 — Missing error codes in `_errorStrings` + `ErrorCodeToUserMessage` switch

**Severity**: 🔴 Correctness
**Files**: `src/FingerprintAgent/Adapters/ZkNativeHost.cs`, `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs`
**Comparator confirmation**: SDK header `libzkfperrdef.h` defines 26 codes (wrapper's `ZkResponse.cs` mirrors all 26). FingerprintAgent covers 13.

**Problem**: SDK can return `-17 FAIL`, `-18 CANCEL`, `-23 NOT_OPENED`, `-24 NOT_INIT`, `-28 TIMEOUT` during capture. No case in the switch → user sees generic `"ZKTeco: capture failed (ERROR_UNKNOWN_*)"`.

### Fix A — Add constants in `ZkNativeHost.cs` (after line 69)

```csharp
internal const int ZKFP_ERR_FAIL       = -17;
internal const int ZKFP_ERR_CANCEL     = -18;
internal const int ZKFP_ERR_NOT_OPENED = -23;
internal const int ZKFP_ERR_NOT_INIT   = -24;
internal const int ZKFP_ERR_TIMEOUT    = -28;
```

### Fix B — Extend `_errorStrings` in `ZKTecoAdapter.cs` (after line 80)

```csharp
[ZkNativeHost.ZKFP_ERR_FAIL]          = "ERROR_FAIL",
[ZkNativeHost.ZKFP_ERR_CANCEL]        = "ERROR_CANCEL",
[ZkNativeHost.ZKFP_ERR_NOT_OPENED]    = "ERROR_NOT_OPENED",
[ZkNativeHost.ZKFP_ERR_NOT_INIT]      = "ERROR_NOT_INIT",
[ZkNativeHost.ZKFP_ERR_TIMEOUT]       = "ERROR_TIMEOUT",
```

### Fix C — Add cases in `ErrorCodeToUserMessage` switch in `ZKTecoAdapter.cs` (after line 415, before `default`)

```csharp
case ZkNativeHost.ZKFP_ERR_TIMEOUT:
    return $"ZKTeco: SDK timed out waiting for finger — please place finger on sensor and try again";
case ZkNativeHost.ZKFP_ERR_CANCEL:
    return "ZKTeco: capture cancelled by sensor — please retry";
case ZkNativeHost.ZKFP_ERR_NOT_OPENED:
    return "ZKTeco: device not opened — reinitializing, please retry";
case ZkNativeHost.ZKFP_ERR_NOT_INIT:
    return "ZKTeco: SDK not initialized — service will reinitialize, please retry";
case ZkNativeHost.ZKFP_ERR_FAIL:
    return "ZKTeco: capture failed (generic SDK error) — please retry";
```

### Verification
- `dotnet build -c Release` — 0 warnings
- `dotnet test --filter "FullyQualifiedName~ZKTeco"` — pass
- `grep "ZKFP_ERR_" ZkNativeHost.cs` — 18 constants total (was 13)

---

## Issue #3 — `bitmap.LockBits`/`UnlockBits` not in try/finally

**Severity**: 🟡 Robustness
**File**: `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs` (lines 86-113; the duplicate in `ZKTecoAdapter.cs` will be removed by Issue #5)

**Problem**: `Marshal.Copy` between `LockBits` and `UnlockBits` can throw → bitmap memory stays locked. `BitmapData` is a struct, cannot be `using`-disposed.

### Fix — Replace entire `ToPngGrayscale` body in `BaseScannerAdapter.cs:86-113`

```csharp
protected byte[] ToPngGrayscale(byte[] raw, int width, int height)
{
    using (var bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
    {
        ColorPalette palette = bitmap.Palette;
        for (int i = 0; i < 256; i++)
            palette.Entries[i] = Color.FromArgb(i, i, i);
        bitmap.Palette = palette;

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format8bppIndexed);
        try
        {
            int stride = bitmapData.Stride;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(raw, y * width, bitmapData.Scan0 + y * stride, width);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
```

### Verification
- `dotnet build -c Release` clean
- `dotnet test --filter "FullyQualifiedName~Mock"` + `HttpServerIntegrationTests` — pass

---

## Issue #4 — `Marshal.AllocHGlobal` partial-fail leak in `AcquireOnce`

**Severity**: 🟡 Robustness
**File**: `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs` (lines 335-359)
**Comparator confirmation**: Wrapper `ZkFingerPrintDevice.cs:51-58` had the same bug — confirmed real-world concern.

**Problem**: Two `AllocHGlobal` calls before `try`. If the second throws OOM, the first pointer leaks.

### Fix — Replace `AcquireOnce` body (lines 337-358)

```csharp
private async Task<int> AcquireOnce(IntPtr handle, byte[] imageBuffer, CancellationToken ct)
{
    IntPtr imagePtr = Marshal.AllocHGlobal(imageBuffer.Length);
    IntPtr templatePtr = IntPtr.Zero;
    try
    {
        templatePtr = Marshal.AllocHGlobal(TemplateBufferSize);
        int cbTemplate = TemplateBufferSize;
        int result = await Task.Run(() =>
            ZkNativeHost.AcquireFingerprint(
                handle, imagePtr, (uint)imageBuffer.Length,
                templatePtr, ref cbTemplate), ct)
            .ConfigureAwait(false);

        if (result == ZkNativeHost.ZKFP_OK)
        {
            Marshal.Copy(imagePtr, imageBuffer, 0, imageBuffer.Length);
        }
        return result;
    }
    finally
    {
        if (templatePtr != IntPtr.Zero)
            Marshal.FreeHGlobal(templatePtr);
        if (imagePtr != IntPtr.Zero)
            Marshal.FreeHGlobal(imagePtr);
    }
}
```

### Verification
- `dotnet build -c Release` clean
- `dotnet test` — `ZKTecoDeviceIntegrationTests` skip when no hardware

---

## Issue #5 — Duplicate `ToPngGrayscale` → extract `PngEncoder`

**Severity**: 🟡 Maintainability
**Files**: 
- `BaseScannerAdapter.cs:86-113` — `protected` instance method
- `ZKTecoAdapter.cs:361-388` — `private` static method (verbatim copy)

### Fix A — Create new file `src/FingerprintAgent/Adapters/PngEncoder.cs`

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Shared 8bpp grayscale raw-pixel → PNG encoder. Single source of truth — keeps
    /// BaseScannerAdapter and ZKTecoAdapter (which does not extend BaseScannerAdapter)
    /// producing identical PNG output. Wraps LockBits/UnlockBits in try/finally.
    /// </summary>
    internal static class PngEncoder
    {
        public static byte[] ToPngGrayscale(byte[] rawPixels, int width, int height)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
            {
                ColorPalette palette = bitmap.Palette;
                for (int i = 0; i < 256; i++)
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                bitmap.Palette = palette;

                var bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format8bppIndexed);
                try
                {
                    int stride = bitmapData.Stride;
                    for (int row = 0; row < height; row++)
                    {
                        Marshal.Copy(rawPixels, row * width, bitmapData.Scan0 + row * stride, width);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }
}
```

### Fix B — `BaseScannerAdapter.cs`, replace `ToPngGrayscale` body (lines 86-113) with delegation

```csharp
protected byte[] ToPngGrayscale(byte[] raw, int width, int height)
    => PngEncoder.ToPngGrayscale(raw, width, height);
```

### Fix C — `ZKTecoAdapter.cs`, change call site at line 287

```csharp
byte[] pngBytes = PngEncoder.ToPngGrayscale(imageBuffer, width, height);
```

### Fix D — `ZKTecoAdapter.cs`, **delete** private `ToPngGrayscale` method (lines 361-388)

### Verification
- `dotnet build -c Release` clean
- `dotnet test` — full suite passes
- Binary-identical PNG output: spot-check `VerificationData` (SHA-256 of PNG) unchanged before/after refactor by capturing once and comparing hex digest

---

## Issue #6 — Named constants for SDK parameter codes

**Severity**: 🟢 Readability
**File**: `src/FingerprintAgent/Adapters/ZkNativeHost.cs` (lines 116-145)
**Comparator confirmation**: SDK header has no constants for width/height/dpi/product/serial — these are empirically verified codes. `ZkSdkProbe.cs:97-110` documents them.

### Fix A — Add constants block in `ZkNativeHost.cs` (after line 69, near other constants)

```csharp
// SDK parameter codes (empirically verified on ZK9500 / ZK SDK 5.3; see tests/.../ZkSdkProbe.cs)
internal const int PARAM_WIDTH         = 1;     // Image width in pixels
internal const int PARAM_HEIGHT        = 2;     // Image height in pixels
internal const int PARAM_DPI           = 3;     // Sensor DPI
internal const int PARAM_PRODUCT_NAME  = 1102;  // Product name (UTF-8 string)
internal const int PARAM_SERIAL_NUMBER = 1103;  // Serial number (UTF-8 string)
```

### Fix B — Replace magic numbers in `TryOpenDevice` (lines 116, 120, 124, 137, 142)

| Line | Before | After |
|------|--------|-------|
| 116 | `ZKFPM_GetParameters(handle, 1, buf4, ref sz)` | `ZKFPM_GetParameters(handle, PARAM_WIDTH, buf4, ref sz)` |
| 120 | `ZKFPM_GetParameters(handle, 2, buf4, ref sz)` | `ZKFPM_GetParameters(handle, PARAM_HEIGHT, buf4, ref sz)` |
| 124 | `ZKFPM_GetParameters(handle, 3, buf4, ref sz)` | `ZKFPM_GetParameters(handle, PARAM_DPI, buf4, ref sz)` |
| 137 | `ZKFPM_GetParameters(handle, 1103, buf64, ref sz)` | `ZKFPM_GetParameters(handle, PARAM_SERIAL_NUMBER, buf64, ref sz)` |
| 142 | `ZKFPM_GetParameters(handle, 1102, buf64, ref sz)` | `ZKFPM_GetParameters(handle, PARAM_PRODUCT_NAME, buf64, ref sz)` |

### Verification
- `dotnet build -c Release` clean
- `dotnet test` — pass
- `grep -nE "\\b(1102|1103)\\b" src/FingerprintAgent/Adapters/ZkNativeHost.cs` — no matches (only the named constants should remain)

---

## Issue #7 — `ref int` → `ref uint` for SDK size params

**Severity**: 🟢 Type-correctness
**Files**: `src/FingerprintAgent/Adapters/ZkNativeHost.cs` (3 sites)
**Comparator confirmation**: SDK header uses `unsigned int*`. Functionally identical on Windows x86/x64 (4 bytes, runtime treats both identically) — but type-correct.

### Fix A — `ZKFPM_GetParameters` P/Invoke (line 53-54)

```csharp
[DllImport("libzkfp.dll")]
private static extern int ZKFPM_GetParameters(
    IntPtr hDev, int nParamCode, byte[] paramValue, ref uint cbParamValue);
```

### Fix B — `ZKFPM_AcquireFingerprint` P/Invoke (line 44-46)

```csharp
[DllImport("libzkfp.dll")]
private static extern int ZKFPM_AcquireFingerprint(
    IntPtr hDevice, IntPtr fpImage, uint cbFPImage,
    IntPtr fpTemplate, ref uint cbTemplate);
```

### Fix C — Wrapper method (line 76-79)

```csharp
internal static int AcquireFingerprint(
    IntPtr hDevice, IntPtr imagePtr, uint cbImage,
    IntPtr templatePtr, ref uint cbTemplate)
    => ZKFPM_AcquireFingerprint(hDevice, imagePtr, cbImage, templatePtr, ref cbTemplate);
```

### Fix D — Call sites in `TryOpenDevice` (lines 115, 119, 123, 136, 141)

Replace `int sz = buf4.Length;` with `uint sz = (uint)buf4.Length;` (and same for `buf64.Length`). The ref-updated `sz` is still used as array length for `GetString(buf64, 0, sz)` — works fine, `int` auto-converts.

### Fix E — `AcquireOnce` in `ZKTecoAdapter.cs:341`

```csharp
uint cbTemplate = (uint)TemplateBufferSize;
int result = await Task.Run(() =>
    ZkNativeHost.AcquireFingerprint(
        handle, imagePtr, (uint)imageBuffer.Length,
        templatePtr, ref cbTemplate), ct)
    .ConfigureAwait(false);
```

### Verification
- `dotnet build -c Release` clean (no signedness warnings — runtime marshals both as 4-byte int)
- `dotnet test` — full suite passes
- `grep -nE "ref int (cb|sz)" src/FingerprintAgent/Adapters/ZkNativeHost.cs` — no matches

---

## Out of Scope (documented but NOT fixed)

The following items appeared during audit but were **explicitly excluded** from this fix batch. Rationale preserved for future reference.

| Item | Source | Why excluded |
|------|--------|--------------|
| Wrapper's `AcquireFingerprintAsync()` parameterless overload (queries param 106) | Wrapper `ZkFingerPrintDevice.cs:20-26` | Param 106 returns `-8` on ZK9500 — never used by FingerprintAgent (buffer overload only) |
| Wrapper's `OpenDevice` `== IntPtr.Zero` check | Wrapper `ZkTecoFingerHost.cs:69-71` | Negative handles missed — FingerprintAgent uses `<= 0` correctly |
| Wrapper's `AllocHGlobal` without try/finally | Wrapper `ZkFingerPrintDevice.cs:51-58` | Already addressed by Issue #4 |
| Wrapper's no width/height validation after `GetCaptureParamsEx` | Wrapper `ZkTecoFingerHost.cs:83-86` | Already correctly validated in `ZkNativeHost.cs:127-132` |
| Wrapper's no retry on `GetDeviceCount()=0` | Wrapper `ZkTecoFingerHost.cs:74` | Already addressed (SCAN-10 quirk handled, 3 retries × 100ms) |
| Wrapper's no recovery on `INITLIB`/`INIT` | Wrapper `ZkTecoFingerHost.cs:61-63` | Already addressed by `EnsureHostInitialized` |
| Wrapper's `GetCaptureParamsEx` consolidation | Wrapper `ZkTecoFingerHost.cs:83` | Not adopted — may invoke param 106 internally, risk of breaking ZK9500 (defensive) |
| Wrapper's `TrimNonAscii` for serial/product | Wrapper `Extensions.cs:42-45` | Cosmetic — current `TrimEnd('\0')` works for normal ZK9500 output |
| `BitmapFormat` vertical flip (`RotatePic`) | Wrapper + C# demo both | NOT applied — per AGENTS.md D-10 "NO pixel inversion" |
| `OnClosing` static event pattern | Wrapper `ZkTecoFingerHost.cs:9` | Global mutable state — explicit cleanup chain preferred |
| SourceAFIS + System.Reactive deps | Wrapper csproj | Out of scope — explicit migration goal to remove them |
| Bitmap as BMP bytes | Wrapper `ZkFingerPrintResult.cs:9` | HIS expects PNG per README contract |
| Error code names: `Abort` vs `ABSORT` | SDK header typo vs `ZkFP_ERR_ABORT` constant | **Kept as-is** (`ABORT` is the conventional name; SDK header's `ABSORT` is the typo) |
| Missing `ZKFPM_GetCaptureParamsEx` use | Wrapper uses | Not adopted — see above |
| Bitmap rotation testing | Wrapper + demo both rotate | NOT applied — see above |
| Wrapper's `OnClosing` event | Wrapper pattern | Not adopted — see above |
| SDK codes `-13`, `-14`, `-20`, `-22`, `-26`, `-27` | Matching/extract-only | Out of scope per AGENTS.md "Matching is NOT done here" |

---

## References

- SDK headers (authoritative): `C:/Users/admin/Music/ZKFingerSDK 5.3_ZK10.0/.../CPP/libs/include/libzkfp.h`, `libzkfperrdef.h`, `libzkfptype.h`
- Native demo: `.../CPP/MFC-Demo/libzkfpDemoDlg.cpp` (lines 250-291 capture, 392-450 init, 452-486 close)
- C# demo (wrapper-based): `.../C#/Demo/Form1.cs` + `BitmapFormat.cs`
- Upstream wrapper: `https://github.com/rainxh11/ZkTecoFingerPrint@v1.2.1` (master)
- Hardware-verified probe: `tests/FingerprintAgent.Tests/Scanner/ZkSdkProbe.cs`
- Integration tests: `tests/FingerprintAgent.Tests/Scanner/ZKTecoDeviceIntegrationTests.cs`
- Migration commit: `bf7d3ca refactor(05): ZKTeco adapter onto raw P/Invoke — drop ZkTecoFingerPrint wrapper` (+478, −180)
- Project knowledge: `AGENTS.md` (critical gotchas F2/F5/W1/W2/W5, anti-patterns)
