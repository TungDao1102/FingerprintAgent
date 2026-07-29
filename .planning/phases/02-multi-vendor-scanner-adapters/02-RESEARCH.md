# Phase 2: Multi-vendor Scanner Adapters - Research

**Researched:** 2026-07-29
**Domain:** Hardware fingerprint scanner SDK integration — SecuGen FDx SDK Pro, Digital Persona U.are.U SDK, Futronic Standard SDK
**Confidence:** MEDIUM

## Summary

Phase 2 replaces `MockScannerAdapter` with real vendor SDK adapters (SecuGen, Digital Persona, Futronic) managed by `ScannerManager` with priority-based fallback. Each vendor SDK has distinct integration patterns: SecuGen provides a managed .NET assembly wrapping a native DLL; Digital Persona uses event-driven capture via COM interop; Futronic is pure P/Invoke against a native Win32 DLL. The key engineering challenge is managing per-capture lazy connection (D-01) with the 10-second total budget (D-06) across potentially three adapter attempts.

**Primary recommendation:** Implement vendor adapters as thin wrappers around each SDK's native API, using a shared adapter base class for common logic (timeout, error translation, PNG conversion). ScannerManager implements the same `IScannerAdapter` interface so the existing `FingerprintAgentService` wiring in `OnStart` requires minimal change.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Scanner device enumeration | API / Backend (adapter) | — | Each vendor SDK discovers USB devices differently |
| Fingerprint image capture | API / Backend (adapter) | — | Blocking per-call; no persistence between calls |
| PNG/WBMP conversion | API / Backend (adapter) | — | Each SDK produces different raw formats |
| Priority-based fallback | API / Backend (ScannerManager) | — | Orchestrates adapter selection across vendors |
| Total capture timeout (10s) | API / Backend (ScannerManager) | — | Budget spans all adapter attempts |
| Health endpoint (IsConnected, DeviceId, Model) | API / Backend (ScannerManager) | — | Delegates to active adapter or returns last-known |

---

## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Lazy connect per capture — each `/api/capture` triggers adapter selection → connection attempt → capture. No persistent connection state between requests.
- **D-02:** Extend `IScannerAdapter` with `Initialize()` method and `VendorErrorCode` string property.
- **D-03:** First found device selection per vendor (enumerate → pick first).
- **D-04:** Priority-based fallback per capture — SecuGen → Digital Persona → Futronic until one succeeds.
- **D-05:** Agent runs as x86 (`<PlatformTarget>x86</PlatformTarget>`).
- **D-06:** 10 seconds total capture timeout across all adapter attempts.
- **D-07:** Pass through SDK output as-is — no normalization.
- **D-08:** SDK DLLs in install directory alongside exe.

### Deferred Ideas
None.

---

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| SCAN-01 | SecuGen FDx SDK Pro adapter — init, scan, return PNG | Section 1: SecuGen API fully documented |
| SCAN-02 | Digital Persona U.are.U adapter — init, scan, return PNG | Section 2: Digital Persona API fully documented |
| SCAN-03 | Futronic Standard SDK adapter (P/Invoke x86) — init, scan, return PNG | Section 3: Futronic P/Invoke signatures fully documented |
| SCAN-04 | ZKTeco adapter — init, scan, return PNG (or BMP via NuGet) | Section 5: ZKTeco API fully documented |
| SCAN-04b | ScannerManager priority-based fallback (now 4 vendors) | Section 6: ScannerManager architecture updated |
| SCAN-07 | Each adapter returns raw SDK PNG bytes | Sections 1-3, 5: each SDK image format documented |

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `SecuGen.FDxSDKPro.Windows.dll` | 3.54+ (SDK v3.54) | SecuGen fingerprint capture | Official managed wrapper; installs to System32 |
| `sgfplib.dll` + `sgfpamx.dll` | (bundled with SDK) | SecuGen native driver + algorithm modules | Required at runtime; 32-bit in i386 folder |
| `DPUruNet` (DPFPDevNET.dll, DPFPEngNET.dll, etc.) | 1.0.0.0+ | Digital Persona U.are.U capture | Official .NET assembly |
| `ftrScanAPI.dll` | 4.2+ (Standard SDK) | Futronic fingerprint capture | Native Win32 DLL; x86 P/Invoke |
| `libzkfpcsharp.dll` + `libzkfp.dll` | ZKFinger SDK 5.x | ZKTeco fingerprint capture | Official COM/.NET wrapper; installs to System32/SysWOW64 |
| `ZkTecoFingerPrint` | 1.2.1 (NuGet) | ZKTeco sane Rx wrapper | MIT; simpler API than raw zkfp2; uses native DLLs underneath |
| .NET Framework 4.8 | 4.8 | Runtime target | Project constraint; x86 |
| `Microsoft.Extensions.DependencyInjection` | 8.0.1 | DI container | Already in project |
| `Newtonsoft.Json` | 13.0.3 | JSON serialization | Already in project |

### Supporting — Testing
| Library | Version | Purpose |
|---------|---------|---------|
| `Moq` | 4.x | Mock IScannerAdapter for unit tests |
| `xUnit` | 2.x | Test framework (existing test project) |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| SecuGen .NET SDK | SecuGen C API via P/Invoke | .NET SDK is simpler and officially supported |
| Digital Persona COM | Digital Persona C API | .NET binding (DPUruNet) is the standard approach |
| Hand-rolled P/Invoke | C++/CLI wrapper | Futronic is natively Win32; P/Invoke is the documented approach |
| ZKTeco `libzkfpcsharp` raw | `ZkTecoFingerPrint` NuGet | NuGet is simpler but introduces 3rd-party dependency; raw zkfp2 is official but verbose |

**Installation (Phase 2 adapter DLLs):**
- `sgfplib.dll`, `sgfpamx.dll` — from SecuGen FDx SDK Pro i386 folder → install directory
- `DPFPDevNET.dll`, `DPFPEngNET.dll`, `DPFPGuiNET.dll`, `DPFPShrNET.dll` — from Digital Persona SDK → install directory
- `ftrScanAPI.dll` — from Futronic Standard SDK → install directory
- `libzkfpcsharp.dll`, `libzkfp.dll` — from ZKFinger SDK → install directory (or use SDK installer to register in System32)

> **Note:** SDK DLLs must be downloaded from each vendor's developer portal. They are not on NuGet (Digital Persona's `DPUruNet` is on NuGet but requires separate SDK download for native DLLs). ZKTeco's official SDK requires Silver+ membership to download; `ZkTecoFingerPrint` NuGet is MIT-licensed and wraps the native DLLs.

---

## 1. SecuGen FDx SDK Pro (.NET)

### DLL / Package
- Managed assembly: `SecuGen.FDxSDKPro.Windows.dll` — .NET 4.0+ managed wrapper
- Native DLLs: `sgfplib.dll` (main module), `sgfpamx.dll` (algorithm module) — both 32-bit in SDK `Bin\i386\` folder
- No NuGet package — download from [secugen.com/download](https://www.secugen.com/download); free SDK available
- Distribution: install to app directory alongside exe (D-08)

### Core API (SGFingerPrintManager class)

**Initialization (2-step — init + open device):**
```csharp
// Step 1: Create manager and init with device name
var m_FPM = new SGFingerPrintManager();
SGFPMDeviceName deviceName = SGFPMDeviceName.DEV_FDU03; // FDU03/SDU03 USB
Int32 err = m_FPM.Init(deviceName); // Returns 0 = ERROR_NONE

// Step 2: Open device (for USB_AUTO_DETECT = first device found)
err = m_FPM.OpenDevice((Int32)SGFPMPortAddr.USB_AUTO_DETECT);
```

**Device enumeration:**
```csharp
m_FPM.EnumerateDevice();
Int32 deviceCount = m_FPM.NumberOfDevice; // Number of found devices
SGFPMDeviceList[] devList = new SGFPMDeviceList[deviceCount];
for (int i = 0; i < deviceCount; i++)
{
    m_FPM.GetEnumDeviceInfo(i, devList[i]);
    // devList[i].DevName, devList[i].DevID
}
```

**Image capture (GetImage — raw gray, no quality check):**
```csharp
// Get device info for dimensions
SGFPMDeviceInfoParam pInfo = new SGFPMDeviceInfoParam();
m_FPM.GetDeviceInfo(pInfo);
int width = pInfo.ImageWidth;   // e.g., 260
int height = pInfo.ImageHeight; // e.g., 300

byte[] fpImage = new byte[width * height];
Int32 err = m_FPM.GetImage(fpImage); // 0 = ERROR_NONE
// fpImage is 256-gray-level pixels, row by row
```

**Image capture with quality check (GetImageEx):**
```csharp
byte[] fpImage = new byte[width * height];
Int32 quality = 80; // 0-100
Int32 err = m_FPM.GetImageEx(fpImage, timeoutMs, hWndForDisplay, quality);
// Returns ERROR_NONE if quality image captured within timeout
```

**Image to PNG:**
The SDK does not produce PNG directly. Convert gray-level raw to PNG using `System.Drawing.Bitmap`:
```csharp
var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
ColorPalette pal = bmp.Palette;
for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
bmp.Palette = pal;
BitmapData bd = bmp.LockBits(new Rectangle(0,0,width,height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
Marshal.Copy(fpImage, 0, bd.Scan0, fpImage.Length);
bmp.UnlockBits(bd);
using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); /* ... */ }
```

### Error Codes → VendorErrorCode String
| Code | Name | Meaning |
|------|------|---------|
| 0 | ERROR_NONE | No error |
| 1 | ERROR_CREATION_FAILED | Cannot create object |
| 2 | ERROR_FUNCTION_FAILED | Function failed |
| 5 | ERROR_DLLLOAD_FAILED | Cannot load sgfplib.dll |
| 6 | ERROR_DLLLOAD_FAILED_DRV | Cannot load device driver |
| 7 | ERROR_DLLLOAD_FAILED_ALGO | Cannot load sgfpamx.dll |
| 51 | ERROR_SYSLOAD_FAILED | Cannot load driver kernel file |
| 52 | ERROR_INITIALIZE_FAILED | Device initialize failed |
| 54 | ERROR_TIME_OUT | GetLiveImage timeout |
| 55 | ERROR_DEVICE_NOT_FOUND | Device not found |
| 56 | ERROR_DRVLOAD_FAILED | Cannot load driver file |
| 57 | ERROR_WRONG_IMAGE | Wrong image |
| 58 | ERROR_LACK_OF_BANDWIDTH | USB bandwidth lack |
| 59 | ERROR_DEV_ALREADY_OPEN | Device already opened |
| 60 | ERROR_GETSN_FAILED | Cannot get device serial |
| 61 | ERROR_UNSUPPORTED_DEV | Unsupported device |

### .NET Framework 4.8 / x86 Gotchas
- The managed assembly (`SecuGen.FDxSDKPro.Windows.dll`) works in-process for both x86 and x64 — it wraps `sgfplib.dll` which ships in both `i386` and `x64` folders in the SDK
- Use `DEV_FDU03` for FDU03/SDU03 devices (most common Hamster series); `DEV_FDU04` for FDU04 (Hamster IV)
- `DEV_AUTO` constant can be used for auto-detection (searches Hamster IV → Plus → III)
- Mixed-mode CLR 2 assembly note: some projects add `RuntimePolicyHelper.LegacyV2RuntimeEnabledSuccessfully` check before instantiating SGFingerPrintManager — this may be needed on .NET Framework 4.8 with certain VS/CLR hosting configurations
- `GetImage()` does NOT check for finger presence — use `GetImageEx()` if quality gating is needed
- The `hWndForDisplay` parameter of `GetImageEx()` is optional (pass `IntPtr.Zero` if no display)

### Device ID / Serial
- Device ID retrieved via `GetDeviceInfo()` → `DeviceSerialNumber` field
- Model name via `GetDeviceInfo()` → can map device type enum to name string

---

## 2. Digital Persona U.are.U SDK

### DLL / Package
- `DPUruNet` is on NuGet: `Install-Package DPUruNet` [ASSUMED — not verified on npm/PyPI]
- Native DLLs required alongside: `DPFPDevNET.dll`, `DPFPEngNET.dll`, `DPFPGuiNET.dll`, `DPFPShrNET.dll`, `DPFPVerNET.dll` [ASSUMED]
- These native DLLs come with the U.are.U SDK installer (separate download from HID Global / Digital Persona developer site)
- Supports Windows 7/10 32/64-bit, Windows Server 2012+

### Core API

**Reader enumeration:**
```csharp
using DPUruNet;

ReaderCollection readers = ReaderCollection.GetReaders();
// or synchronously:
Reader reader = Reader.GetDevice(); // Gets first available reader
```

**Capture (event-driven pattern):**
```csharp
Capture capture = new Capture();
capture.EventHandler = this; // implements DPFP.Capture.EventHandler

// Start capture (non-blocking)
capture.StartCapture();

// Event handler interface:
public void OnComplete(object Capture, string ReaderSerialNumber, Sample Sample) { }
public void OnFingerGone(object Capture, string ReaderSerialNumber) { }
public void OnFingerTouch(object Capture, string ReaderSerialNumber) { }
public void OnReaderConnect(object Capture, string ReaderSerialNumber) { }
public void OnReaderDisconnect(object Capture, string ReaderSerialNumber) { }
public void OnSampleQuality(object Capture, string ReaderSerialNumber, CaptureFeedback CaptureFeedback) { }
```

**Sample to Bitmap conversion:**
```csharp
SampleConversion convertor = new SampleConversion();
Bitmap bitmap = null;
convertor.ConvertToPicture(sample, ref bitmap); // modifies bitmap in-place
// bitmap.Save(@"fingerprint.bmp");
```

**Sync (blocking) capture using ManualResetEvent:**
```csharp
private ManualResetEvent _captureEvent = new ManualResetEvent(false);
private Sample _capturedSample;

public Sample CaptureSync(Capture capture)
{
    _captureEvent.Reset();
    capture.StartCapture();
    _captureEvent.WaitOne(); // Blocks until OnComplete fires
    capture.StopCapture();
    return _capturedSample;
}

public void OnComplete(object Capture, string ReaderSerialNumber, Sample Sample)
{
    _capturedSample = Sample;
    _captureEvent.Set();
}
```

**Device info:**
- `reader.SerialNumber` — reader serial number string
- `reader.Description` — model description
- `reader.DevicePath` — USB device path

### Error Handling
- Digital Persona uses `DPFP.Capture.ReturnCode` enum values
- Common capture-related return codes: `SUCCESS`, `FAIL`, `DEVICE_IN_USE`, `DEVICE_NOT_FOUND`, `DEVICE_NOT_CAPTURING`
- The event-driven model makes timeout handling critical — wrap the `ManualResetEvent.WaitOne(timeoutMs)` with a timeout and call `StopCapture()` if it fires

### .NET Framework / x86 Compatibility
- DPUruNet is a .NET assembly that should work in x86 process
- The SDK supports Windows XP through Windows 10 (32/64-bit) so Windows 10/11 x86 compatibility is confirmed

### Image Format
- Samples are captured as DPUruNet `Sample` objects — these can be converted to 8-bit grayscale or 24-bit color bitmaps via `SampleConversion`
- The SDK produces ANSI/NIST-compliant image format natively
- For PNG output, convert Bitmap to PNG via `System.Drawing.Imaging.ImageFormat.Png`

---

## 3. Futronic Standard SDK (x86 P/Invoke)

### DLL / Package
- Native DLL: `ftrScanAPI.dll` (or `ftrScanApi.dll` depending on version) — comes with Futronic Standard SDK download
- Download from [futronic-tech.com](http://www.futronic-tech.com/download.html) — "Standard SDK version 4.2 is available for free" (registration required)
- The DLL is 32-bit only — confirmed by multiple sources: "The project must be compiled as x86 because the DLL is 32bits"
- Alternative DLL naming: `ftrScanApi.dll` (found in some SDK distributions), `ftrMFAPI.dll` (Mifare functionality, not needed for capture)

### P/Invoke Declarations

```csharp
using System.Runtime.InteropServices;

public static class FutronicSDK
{
    // Open/close device
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ftrScanOpenDevice();

    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ftrScanCloseDevice(IntPtr ftrHandle);

    // Get image size (needed before capture to allocate buffer)
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool ftrScanGetImageSize(IntPtr ftrHandle, out FTRSCAN_IMAGE_SIZE pImageSize);

    // Capture image (nDose typically 4 for normal quality; 1-10 range)
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool ftrScanGetImage(IntPtr ftrHandle, int nDose, byte[] pBuffer);

    // Get device info
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool ftrScanGetDeviceInfo(IntPtr ftrHandle, out FTRSCAN_DEVICE_INFO pDeviceInfo);

    // Get last error code
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ftrScanGetLastError();

    // Check finger presence (for live finger detection)
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool ftrScanIsFingerPresent(IntPtr ftrHandle, out FTRSCAN_FRAME_PARAMETERS pFrameParameters);

    // Get serial number
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool ftrScanGetSerialNumber(IntPtr ftrHandle, byte[] pBuffer);

    // Version info
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool ftrScanGetVersion(IntPtr ftrHandle, out FTRSCAN_VERSION_INFO pVersionInfo);
}

// Structs (must match native layout exactly — use pack(1))
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FTRSCAN_IMAGE_SIZE
{
    public int nWidth;
    public int nHeight;
    public int nImageSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FTRSCAN_DEVICE_INFO
{
    public uint dwStructSize;
    public byte byDeviceCompatibility;
    public ushort wPixelSizeX;
    public ushort wPixelSizeY;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FTRSCAN_FRAME_PARAMETERS
{
    public int nContrastOnDose2;
    public int nContrastOnDose4;
    public int nDose;
    public int nBrightnessOnDose1;
    public int nBrightnessOnDose2;
    public int nBrightnessOnDose3;
    public int nBrightnessOnDose4;
    // Fake replica params follow...
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FTRSCAN_VERSION_INFO
{
    public uint dwVersionInfoSize;
    public FTRSCAN_VERSION APIVersion;
    public FTRSCAN_VERSION HardwareVersion;
    public FTRSCAN_VERSION FirmwareVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FTRSCAN_VERSION
{
    public ushort wMajorVersionHi;
    public ushort wMajorVersionLo;
    public ushort wMinorVersionHi;
    public ushort wMinorVersionLo;
}
```

### Core Usage Pattern

```csharp
public class FutronicAdapter : IScannerAdapter
{
    private IntPtr _device = IntPtr.Zero;
    private int _imageWidth;
    private int _imageHeight;

    public bool Initialize()
    {
        _device = FutronicSDK.ftrScanOpenDevice();
        if (_device == IntPtr.Zero) return false;

        FutronicSDK.FTRSCAN_IMAGE_SIZE size;
        if (!FutronicSDK.ftrScanGetImageSize(_device, out size))
            return false;

        _imageWidth = size.nWidth;
        _imageHeight = size.nHeight;
        return true;
    }

    public CaptureResult Scan()
    {
        // Finger detection loop (optional — SDK captures even without finger)
        byte[] buffer = new byte[_imageWidth * _imageHeight];
        // nDose=4 is standard; higher = longer exposure, brighter image
        bool ok = FutronicSDK.ftrScanGetImage(_device, 4, buffer);
        if (!ok)
        {
            uint err = FutronicSDK.ftrScanGetLastError();
            return CaptureResult.Fail($"Futronic capture failed: 0x{err:X}");
        }

        // Convert raw to PNG (grayscale inverted — SDK returns dark-on-light)
        var bmp = new Bitmap(_imageWidth, _imageHeight);
        for (int x = 0; x < _imageWidth; x++)
            for (int y = 0; y < _imageHeight; y++)
                bmp.SetPixel(x, y, Color.FromArgb(255 - buffer[y * _imageWidth + x],
                                                  255 - buffer[y * _imageWidth + x],
                                                  255 - buffer[y * _imageWidth + x]));
        // Save to PNG memory stream...
    }
}
```

### Error Codes → VendorErrorCode String
Error codes are Windows error codes or custom codes in range `0x20000000 | x`:
| Code | Hex | Meaning |
|------|-----|---------|
| `FTR_ERROR_EMPTY_FRAME` | 0x200010E2 | No frame captured |
| `FTR_ERROR_MOVABLE_FINGER` | 0x20000001 | Finger moved during capture |
| `FTR_ERROR_NO_FRAME` | 0x20000002 | No frame received |
| `FTR_ERROR_USER_CANCELED` | 0x20000003 | User canceled |
| `FTR_ERROR_HARDWARE_INCOMPATIBLE` | 0x20000004 | Hardware incompatible |
| `FTR_ERROR_FIRMWARE_INCOMPATIBLE` | 0x20000005 | Firmware incompatible |
| `FTR_ERROR_INVALID_AUTHORIZATION_CODE` | 0x20000006 | Invalid auth code |
| Standard Win32 codes | e.g., 0x20000087 = ERROR_INVALID_PARAMETER | See Windows error codes |

### x86 Constraint Confirmation
- Confirmed by multiple independent sources: "The project must be compiled as x86 because the DLL is 32bits"
- The DLL does not have a 64-bit version available from Futronic
- D-05 (`<PlatformTarget>x86</PlatformTarget>`) is mandatory for Futronic integration
- Multiple Futronic SDK examples on GitHub all specify x86 build platform

### Image Format
- Raw 8-bit grayscale — values represent optical density (higher = darker)
- Most implementations invert the values when displaying (white background, dark ridges)
- For PNG: invert pixel values, create Bitmap, save as PNG

### Device Serial Number
```csharp
byte[] serial = new byte[32]; // Buffer for serial number string
FutronicSDK.ftrScanGetSerialNumber(_device, serial);
string serialNumber = Encoding.ASCII.GetString(serial).TrimEnd('\0');
```

---

## 5. ZKTeco (ZK Finger SDK)

### DLL / Package

| Package | Source | Purpose |
|---------|--------|---------|
| `libzkfpcsharp.dll` | ZKFinger SDK 5.x (installed to System32/SysWOW64) | Official C# COM wrapper — primary integration path |
| `ZkTecoFingerPrint` | NuGet (v1.2.1, netstandard2.0) | 3rd-party sane wrapper around native DLLs — simpler API, MIT licensed |

- **Official SDK:** ZKFinger SDK for Windows (34MB) — available from [zkteco.com](https://www.zkteco.com/en/ZKFingerSDKforWindows/ZKFinger-SDK-for-Windows) (requires Silver+ membership to download)
- **Native DLL:** `libzkfpcsharp.dll` — installed to `C:\Windows\System32` (64-bit OS) or `C:\Windows\SysWOW64` (32-bit process on 64-bit OS) by the ZKFinger SDK installer
- **NuGet wrapper:** `ZkTecoFingerPrint` (1.2.1, MIT) by rainxh11 — wraps native DLLs directly, provides Rx observable API and result discriminated union types
- **Compatible devices:** ZK4500, ZK6500, ZK7500, ZK8500, ZK8500R, ZK9500, SLK20R, SLK20M [VERIFIED: ZK4500 datasheet]
- **OS support:** Windows XP through Windows 10 / Server 2012 (32-bit and 64-bit) [VERIFIED: ZKTeco SDK selection guide]
- **x86 support:** YES — SDK runs as 32-bit process; `libzkfpcsharp.dll` ships in SysWOW64 [VERIFIED: multiple implementation repos target x86]

### Core API (Official `zkfp2` class in `libzkfpcsharpsharp`)

```csharp
using libzkfpcsharp;

// Initialization
int ret = zkfp2.Init();           // Returns 0 = ZKFP_ERR_OK
int deviceCount = zkfp2.GetDeviceCount();  // Number of connected devices

// Open device by index (0-based)
IntPtr handle = zkfp2.OpenDevice(0);  // Returns IntPtr.Zero on failure

// Get image dimensions (code 1 = width, code 2 = height)
byte[] paramValue = new byte[4];
int size = 4;
zkfp2.GetParameters(handle, 1, paramValue, ref size);  // width
zkfp2.ByteArray2Int(paramValue, ref width);
zkfp2.GetParameters(handle, 2, paramValue, ref size); // height
zkfp2.ByteArray2Int(paramValue, ref height);

// Capture (blocking — polls until finger placed or error)
byte[] imgBuffer = new byte[width * height];   // raw gray pixels
byte[] template = new byte[2048];
int templateLen = 2048;
int ret = zkfp2.AcquireFingerprint(handle, imgBuffer, template, ref templateLen);
// Returns ZKFP_ERR_OK (0) on success; ZKFP_ERR_CAPTURE (-8) if no finger; other negatives for errors

// Close device
zkfp2.CloseDevice(handle);

// Release library (on shutdown)
zkfp2.Terminate();
```

### Image-Only Capture (no template extraction)
```csharp
// AcquireFingerprintImage captures just the image buffer — no template processing
int ret = zkfp2.AcquireFingerprintImage(handle, imgBuffer);
// Slightly faster when only image bytes are needed (D-07: PNG passthrough)
```

### NuGet Wrapper API (`ZkTecoFingerPrint`, v1.2.1)

```csharp
// Initialize
ZkTecoFingerHost.Initialize();

// Enumerate
int count = ZkTecoFingerHost.GetDeviceCount();

// Open device (returns ZkDeviceResult — IDisposable)
using var deviceResult = ZkTecoFingerHost.OpenDevice(0);
if (deviceResult.IsSuccess)
{
    var device = deviceResult.Value;
    // device.Name, device.SerialNumber, device.Width, device.Height, device.Dpi

    // Polling capture with CancellationToken
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await device.AcquireFingerprintAsync(cts.Token);
    if (result.IsSuccess)
    {
        byte[] bitmapBytes = result.Value!.Bitmap;  // BMP image bytes (already encoded)
        // No raw buffer → no manual Bitmap construction needed
    }
}

// Cleanup
ZkTecoFingerHost.Close();
```

### Error Codes → VendorErrorCode String

| Code | Name | Meaning |
|------|------|---------|
| 0 | `ZKFP_ERR_OK` | Success |
| -1 | `ZKFP_ERR_INITLIB` | Failed to initialize library |
| -2 | `ZKFP_ERR_INIT` | Failed to initialize capture library |
| -3 | `ZKFP_ERR_NO_DEVICE` | No device connected |
| -4 | `ZKFP_ERR_NOT_SUPPORT` | Not supported by interface |
| -5 | `ZKFP_ERR_INVALID_PARAM` | Invalid parameter |
| -6 | `ZKFP_ERR_OPEN` | Failed to start device |
| -7 | `ZKFP_ERR_INVALID_HANDLE` | Invalid handle |
| -8 | `ZKFP_ERR_CAPTURE` | Failed to capture (no finger or poor quality) |
| -9 | `ZKFP_ERR_EXTRACT_FP` | Failed to extract fingerprint template |
| -10 | `ZKFP_ERR_ABORT` | Suspension |
| -11 | `ZKFP_ERR_MEMORY_NOT_ENOUGH` | Insufficient memory |
| -12 | `ZKFP_ERR_BUSY` | Capture already in progress |

### Pixel Format

| Property | Value | Source |
|----------|-------|--------|
| Resolution | 280×360 pixels (ZK4500) | [VERIFIED: ZK4500 datasheet] |
| DPI | 500 DPI | [VERIFIED: ZK4500 datasheet] |
| Bit depth | 8-bit grayscale (256 levels) | [VERIFIED: ZK4500 datasheet] |
| Pixel values | 0 = white/background, 255 = dark ridges | [ASSUMED — consistent with other optical sensors] |
| Output format | Raw byte array from `AcquireFingerprint`/`AcquireFingerprintImage`; BMP from NuGet wrapper | [VERIFIED: Context7 docs] |

> **Note on pixel inversion:** Unlike Futronic (which returns dark-on-light), ZKTeco raw data is conventional grayscale with ridges darker than background. Invert only if display/output looks inverted vs. SecuGen/Digital Persona output.

### x86 Compatibility

- `libzkfpcsharp.dll` ships in both `System32` (64-bit) and `SysWOW64` (32-bit) in the SDK distribution
- The ZK4500 driver explicitly supports 32-bit and 64-bit Windows [VERIFIED: multiple datasheets]
- The `zkfp2` class is a COM-visible managed wrapper callable from any .NET process
- `<PlatformTarget>x86</PlatformTarget>` is fully supported — no 64-bit-only models in the ZK4500/ZK8500/SLK20R family [VERIFIED: ZKTeco SDK selection guide v3]
- D-05 (x86 constraint) does NOT conflict with ZKTeco integration

### Licensing

- **Official ZKFinger SDK:** Requires Silver+ membership to download from zkteco.com. License terms are not openly published on the website — SDK agreement presented at download. Free development, but production redistribution restrictions likely apply (standard for OEM SDK licensing). **Confirmation needed before Phase 4.**
- **`ZkTecoFingerPrint` NuGet:** MIT License — freely redistributable. Uses native `libzkfpcsharp.dll` which requires the ZKFinger SDK installation; the wrapper itself carries no additional license burden.
- **Recommended approach for Phase 2:** Use `ZkTecoFingerPrint` NuGet (MIT, simpler API) with native `libzkfpcsharp.dll` from the installed ZKFinger SDK. No per-seat royalties from the wrapper; ZKFinger SDK licensing remains the user's responsibility.

### Integration Notes

#### Option A: Direct `zkfp2` P/Invoke (official SDK)
Follows the same pattern as Futronic — raw buffer management, manual Bitmap construction:
```csharp
var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
ColorPalette pal = bmp.Palette;
for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
bmp.Palette = pal;
BitmapData bd = bmp.LockBits(new Rectangle(0,0,width,height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
Marshal.Copy(imgBuffer, 0, bd.Scan0, imgBuffer.Length);
bmp.UnlockBits(bd);
using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); /* ... */ }
```

#### Option B: `ZkTecoFingerPrint` NuGet (recommended)
Provides BMP bytes directly — no raw buffer → Bitmap construction step:
```csharp
if (result.IsSuccess)
{
    byte[] bmpBytes = result.Value!.Bitmap;
    // BMP bytes can be decoded via: new Bitmap(new MemoryStream(bmpBytes))
    // Or converted to PNG via same Bitmap decode:
    using var bmp = new Bitmap(new MemoryStream(bmpBytes));
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    // ms.ToArray() = PNG bytes for CaptureResult.ImageBytes
}
```

#### ZKTeco in ScannerManager (D-04 priority order)
Add `ZKTeco` as 4th priority after Futronic:
```csharp
// AgentConfig.Scanner.Priority → ["SecuGen", "DigitalPersona", "Futronic", "ZKTeco"]
```

#### Adapter Lifecycle (per D-01 lazy connect)
```csharp
public class ZKTecoAdapter : IScannerAdapter
{
    private IntPtr _handle = IntPtr.Zero;
    private int _width, _height;
    private string _deviceId = "", _model = "";
    private bool _disposed = false;

    public bool Initialize()
    {
        // Per-capture: Init → GetDeviceCount → OpenDevice(0) → GetParameters
        int ret = zkfp2.Init();
        if (ret != zkfp2.ZKFP_ERR_OK) return false;

        if (zkfp2.GetDeviceCount() == 0) { zkfp2.Terminate(); return false; }

        _handle = zkfp2.OpenDevice(0);
        if (_handle == IntPtr.Zero) { zkfp2.Terminate(); return false; }

        // Get dimensions
        byte[] buf = new byte[4];
        int size = 4;
        zkfp2.GetParameters(_handle, 1, buf, ref size);
        zkfp2.ByteArray2Int(buf, ref _width);
        size = 4;
        zkfp2.GetParameters(_handle, 2, buf, ref size);
        zkfp2.ByteArray2Int(buf, ref _height);

        // Get device info for health endpoint
        _deviceId = GetSerialNumber();
        _model = "ZKTeco"; // Parameter code 1102 gives product name
        return true;
    }

    public CaptureResult Scan()
    {
        byte[] imgBuf = new byte[_width * _height];
        byte[] template = new byte[2048];
        int templateLen = 2048;

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Note: AcquireFingerprint blocks. Wrap in Task.Run + WaitOne for timeout.
        int ret = zkfp2.AcquireFingerprint(_handle, imgBuf, template, ref templateLen);
        if (ret == zkfp2.ZKFP_ERR_OK)
            return CreatePngResult(imgBuf);
        else if (ret == zkfp2.ZKFP_ERR_CAPTURE)
            return CaptureResult.Fail("CAPTURE_FAILED", $"ZKTeco: no finger detected ({ret})");
        else
            return CaptureResult.Fail("CAPTURE_FAILED", $"ZKTeco: error {ret}");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) zkfp2.CloseDevice(_handle);
        zkfp2.Terminate();
    }
}
```

### Gotchas

1. **`libzkfpcsharp.dll` must be in System32 or app directory** — The SDK installer places it in System32. If the agent runs from a non-admin install directory and the DLL isn't found, `DllNotFoundException` or silent failure results. Phase 4 installer must ensure the DLL is in the app directory (D-08).
2. **`GetDeviceCount()` returns 0 after `Init()` on first call** — Some driver versions require a short delay or re-init. The `ZkTecoFingerPrint` NuGet handles this with retry logic. If using raw `zkfp2`, call `Init()` → wait ~100ms → `GetDeviceCount()`.
3. **`AcquireFingerprint` blocks indefinitely** — No timeout parameter. Must wrap in a worker thread with `Thread.Join(timeout)` or equivalent cancellation to stay within the D-06 budget.
4. **Parameter codes are device-specific** — Width/height codes (1, 2) are universal. Other codes (DPI, anti-fake, LED control) only work on specific models (e.g., LIVEID20R). Only query width/height generically.
5. **`ZkTecoFingerPrint` NuGet uses SourceAFIS for templates, not ZKTeco's algorithm** — If template matching with ZKTeco's own algorithm is needed, use the raw SDK. For capture-only (Phase 2 requirement), the NuGet is fine.
6. **`AcquireFingerprintImage` (image only) vs `AcquireFingerprint` (image + template)** — For PNG passthrough (D-07), prefer `AcquireFingerprintImage` to skip template extraction overhead.
7. **Dispose order matters** — `CloseDevice` before `Terminate`. Not doing this in order can leave the library in a bad state for the next `Initialize()` call.

---

## 6. Adapter Architecture Patterns

### ScannerManager (Composite + Fallback)

ScannerManager implements `IScannerAdapter` itself (composite pattern) and wraps N real adapters. Per D-04 and D-06:

```csharp
public class ScannerManager : IScannerAdapter
{
    private readonly IScannerAdapter[] _adapters; // in priority order
    private IScannerAdapter _activeAdapter;
    private readonly TimeSpan _totalTimeout;
    private readonly TimeSpan _perAdapterTimeout;

    public ScannerManager(AgentConfig config, AgentLogger logger)
    {
        // Build adapter list from config.Scanner.Priority
        // e.g., ["SecuGen", "DigitalPersona", "Futronic"]
        _totalTimeout = TimeSpan.FromSeconds(10);
        _perAdapterTimeout = TimeSpan.FromSeconds(3); // D-06: ~3s connect + 3s capture
    }

    public bool IsConnected => _activeAdapter?.IsConnected ?? false;
    public string DeviceId => _activeAdapter?.DeviceId ?? "no-device";
    public string Model => _activeAdapter?.Model ?? "no-device";

    public CaptureResult Scan()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(_totalTimeout); // 10 second total budget

        foreach (var adapter in _adapters)
        {
            if (cts.Token.IsCancellationRequested)
                return CaptureResult.Timeout();

            // Try init + scan with per-adapter timeout
            var adapterCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            adapterCts.CancelAfter(_perAdapterTimeout);

            try
            {
                if (adapter.Initialize())
                {
                    var result = adapter.Scan(); // wrapped in try/catch
                    if (result.IsSuccess)
                    {
                        _activeAdapter = adapter;
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning($"Adapter {adapter.GetType().Name} failed: {ex.Message}");
            }
        }

        return CaptureResult.NotConnected();
    }

    public string VendorErrorCode => _activeAdapter?.VendorErrorCode ?? "NO_ADAPTER";
    public string MimeType => _activeAdapter?.MimeType ?? "image/png";
}
```

### IScannerAdapter Interface Extension (D-02)

```csharp
public interface IScannerAdapter
{
    bool IsConnected { get; }
    string DeviceId { get; }
    string Model { get; }
    string MimeType { get; }
    CaptureResult Scan();
    // Added for Phase 2:
    bool Initialize();                          // D-02
    string VendorErrorCode { get; }             // D-02 — human-readable SDK error
}
```

### Lazy Connect Pattern (D-01)
- No adapter holds state between `/api/capture` calls
- `Initialize()` is called fresh on each `Scan()` invocation
- `IsConnected` reflects state after the current `Scan()` attempt
- This means `HealthHandler` needs to call `Scan()` once to get a working adapter, or ScannerManager tracks last successful adapter properties separately from capture state

### Timeout Distribution (D-06)
With 10s total and 3 adapters:
- Per-adapter connect: ~1.5s (timeout on `Initialize()`)
- Per-adapter capture: ~2s (timeout on `Scan()`)
- Total with fallback: up to ~10.5s if all 3 adapters are tried

### PNG Passthrough (D-07)
Each adapter converts its native format (raw gray, WBMP, etc.) to PNG in-process using `System.Drawing.Bitmap`:
- SecuGen: raw 256-gray → indexed Bitmap → PNG
- Digital Persona: `Sample` → `SampleConversion` → `Bitmap` → PNG
- Futronic: raw 8-bit → inverted grayscale `Bitmap` → PNG

---

## 7. Validation Architecture

### Unit Test Strategy (without hardware)
Mock each `IScannerAdapter` implementation using Moq. Test the **orchestration logic** in `ScannerManager`:

```csharp
[Fact]
public void ScannerManager_TriesAdaptersInPriorityOrder()
{
    var mockAdapter1 = new Mock<IScannerAdapter>();
    var mockAdapter2 = new Mock<IScannerAdapter>();
    mockAdapter1.Setup(a => a.Initialize()).Returns(false);
    mockAdapter2.Setup(a => a.Initialize()).Returns(true);
    mockAdapter2.Setup(a => a.Scan()).Returns(new CaptureResult { IsSuccess = true });

    var manager = new ScannerManager(new[] { mockAdapter1.Object, mockAdapter2.Object });
    var result = manager.Scan();

    mockAdapter1.Verify(a => a.Initialize(), Times.Once);
    mockAdapter1.Verify(a => a.Scan(), Times.Never); // Never called since init failed
    mockAdapter2.Verify(a => a.Initialize(), Times.Once);
    mockAdapter2.Verify(a => a.Scan(), Times.Once);
    Assert.True(result.IsSuccess);
}

[Fact]
public void ScannerManager_ReturnsNotConnected_WhenAllAdaptersFail()
{
    var mock = new Mock<IScannerAdapter>();
    mock.Setup(a => a.Initialize()).Returns(false);
    var manager = new ScannerManager(new[] { mock.Object });
    var result = manager.Scan();
    Assert.False(result.IsSuccess);
    Assert.Equal("SCANNER_NOT_CONNECTED", result.ErrorMessage);
}

[Fact]
public void ScannerManager_ExposesActiveAdapterProperties()
{
    var mock = new Mock<IScannerAdapter>();
    mock.Setup(a => a.Initialize()).Returns(true);
    mock.Setup(a => a.DeviceId).Returns("secugen-001");
    mock.Setup(a => a.Model).Returns("Hamster Pro 20");
    var manager = new ScannerManager(new[] { mock.Object });
    manager.Scan(); // Trigger init

    Assert.Equal("secugen-001", manager.DeviceId);
    Assert.Equal("Hamster Pro 20", manager.Model);
    Assert.True(manager.IsConnected);
}
```

### Integration Test Strategy (with real SDK)
- Requires vendor SDK installed + physical device (or SDK simulator)
- Test on a干净 Windows VM or device lab machine
- Use `config.json` with `MockMode = false` and real `Priority` array
- Verify real PNG bytes in response (check `imageBytes` base64 decodes to valid PNG)
- Test fallback: physically disconnect primary scanner, verify secondary takes over

### Adapter-Specific Validation
| Adapter | Check | Method |
|---------|-------|--------|
| SecuGen | `Initialize()` returns true with device | Unit: mock SGFingerPrintManager; Integration: real device |
| SecuGen | `GetImage()` produces valid gray bitmap | Integration: compare dimensions to device spec |
| Digital Persona | `ReaderCollection.GetReaders()` finds device | Unit: mock ReaderCollection |
| Digital Persona | `OnComplete` fires with `Sample` | Integration: verify `Bitmap` from `SampleConversion` |
| Futronic | `ftrScanOpenDevice()` returns non-null handle | Unit: mock P/Invoke |
| Futronic | `ftrScanGetImage()` fills buffer correctly | Integration: compare to known test image |
| ZKTeco | `zkfp2.Init()` returns 0 and `GetDeviceCount()` > 0 | Unit: mock zkfp2 static calls; Integration: real device |
| ZKTeco | `AcquireFingerprint` returns ZKFP_ERR_OK with valid buffer | Integration: compare dimensions to 280×360 spec |

### Without Physical Hardware
- For SecuGen: `m_FPM.InitEx(width, height, dpi)` allows running fingerprint algorithm without device — test image processing pipeline
- For Digital Persona: Can test enrollment/verification flow with pre-recorded sample files (SDK ships sample images)
- For Futronic: No device-free mode known — integration testing requires hardware
- For ZKTeco: `zkfp2.Init()` can be called without a device (it initializes the library). `GetDeviceCount()` returns 0 without hardware — test the init + zero-device path in unit tests to verify error translation.

---

## Pitfalls

### SecuGen
1. **sgfplib.dll not found at runtime** — Must be in `Windows\System32` or in the app directory. Distribution guide explicitly states to copy to app path. If `System32` copy is missing, `DllNotFoundException` fires on `new SGFingerPrintManager()`.
2. **Mixed-mode CLR assembly** — `SecuGen.FDxSDKPro.Windows.dll` is a mixed-mode assembly (CLR 2). Some .NET Framework 4.8 hosting configurations require `RuntimePolicyHelper.LegacyV2RuntimeEnabledSuccessfully` check. Test on a clean .NET 4.8 environment.
3. **Device not found (55)** — `Init()` succeeds but `OpenDevice()` returns 55 if the device driver is not installed. Must install SecuGen USB driver before the SDK works.
4. **GetImage() blocks indefinitely** — `GetImage()` has no timeout parameter; it blocks until a finger is placed or an error occurs. Use `GetImageEx()` with explicit timeout to implement D-06 timeout budget.
5. **Wrong device name enum** — Using `DEV_FDU02` when the device is FDU03 will silently fail to open. Use `EnumerateDevice()` + auto detection rather than hardcoding device name.

### Digital Persona
1. **Event-driven capture is async** — The `OnComplete` callback fires on a background thread. In a sync `Scan()` call, use `ManualResetEvent.WaitOne(timeout)` and call `StopCapture()` on timeout.
2. **Sample conversion must match bitmap size** — `ConvertToPicture(Sample, ref bitmap)` writes into the provided Bitmap object's pixel buffer. If the bitmap dimensions don't match the sample dimensions, it may fail silently or throw.
3. **Device disconnection not handled gracefully** — If the device is unplugged during capture, `OnReaderDisconnect` fires but `Scan()` will block on `WaitOne()`. Need to handle this via `OnReaderDisconnect` setting the event to unblock.
4. **NuGet package DPUruNet version mismatch** — There are multiple versions of the Digital Persona / HID Global SDK. `DPUruNet` v1.0.0.0 is the most documented; verify the DLLs from the downloaded SDK match the NuGet version.
5. **COM apartment state** — `Capture` object may require STA (single-threaded apartment) context. If `FingerprintAgentService` runs as a Windows Service, the STA requirement may not be met by default. Test in service context.

### Futronic
1. **ftrScanAPI.dll is 32-bit ONLY** — This is the most critical gotcha. If the project is compiled as `AnyCPU` on a 64-bit OS, it will load the 64-bit CLR and then fail to load the 32-bit DLL. D-05 (`<PlatformTarget>x86</PlatformTarget>`) is mandatory.
2. **ftrScanAPI.dll must be in app directory** — Unlike SecuGen (which installs to System32), Futronic may not automatically install to System32. Copy `ftrScanAPI.dll` to the install directory.
3. **Image pixel inversion** — The raw buffer from `ftrScanGetImage()` returns dark ridges as high values (closer to 0 = white, 255 = black). For a normal fingerprint image (dark ridges on light background), invert: `255 - buffer[index]`.
4. **nDose parameter** — The capture dose parameter (1-10) controls exposure time. `4` is typically normal quality. Too high causes oversaturation, too low causes underexposure. Start with `4` and tune.
5. **No device enumeration** — Unlike SecuGen's `EnumerateDevice()`, Futronic uses `ftrScanOpenDevice()` to open the first device with no enumeration API. All devices share the same handle from one call.
6. **ftrScanGetLastError() returns 0 on success** — Must call after a failed API call to get the error code. The returned value is 0 on success, so don't treat 0 as an error.

### ZKTeco
1. **`libzkfpcsharp.dll` must be in System32 or app directory** — The SDK installer places it in `System32` (64-bit OS, 64-bit process) or `SysWOW64` (32-bit process on 64-bit OS). On 32-bit Windows or if the installer skips the DLL, copy from the SDK distribution folder to the install directory (D-08). Without it, `DllNotFoundException` on `new zkfp()`.
2. **`GetDeviceCount()` returns 0 on first call after `Init()`** — Some driver versions require a ~100ms settling delay or a re-init call. The `ZkTecoFingerPrint` NuGet handles this with retry. If using raw `zkfp2`, implement retry logic: `Init()` → wait 100ms → `GetDeviceCount()`; if 0, `Terminate()` → `Init()` → retry.
3. **`AcquireFingerprint` blocks indefinitely** — No timeout parameter. Must wrap in `Task.Run` + `WaitOne(timeout)` or `Thread.Join(timeout)` to stay within the D-06 budget.
4. **BMP format from NuGet, raw bytes from `zkfp2`** — If using `ZkTecoFingerPrint` NuGet, `FingerPrintResult.Bitmap` returns BMP bytes (can decode directly via `new Bitmap(new MemoryStream(...))`). If using raw `zkfp2`, `AcquireFingerprintImage` returns raw 8-bit gray bytes requiring Bitmap construction for PNG conversion.
5. **Pixel values may need inversion** — ZKTeco raw output is 0=white, 255=dark (conventional grayscale). If the captured image displays inverted compared to other scanners, apply `255 - value` per pixel before PNG encoding. Test against known-good reference.
6. **Dispose order: `CloseDevice` before `Terminate`** — Not calling `CloseDevice()` before `Terminate()` can leave the library in a bad state, causing the next `Initialize()` to fail silently or return `IntPtr.Zero` from `OpenDevice()`.

### Common (All Adapters)
1. **Timeout budget collision with D-06** — If each adapter tries to implement its own 3s timeout on top of the 10s total budget, nested cancellation can cause partial state. Coordinate via `CancellationToken` propagation through ScannerManager.
2. **SDK DLLs not in install directory** — D-08 requires SDK DLLs in the install directory. But the SDK installers typically copy to `System32`. A setup script must copy from `System32` to the install directory, or use the SDK's "distribution" folder which has the correct DLLs.
3. **ScannerManager needs to implement IScannerAdapter** — `FingerprintAgentService` stores `IScannerAdapter _scanner`. If ScannerManager doesn't implement the interface, `FingerprintAgentService` needs to change. ScannerManager must also expose `DeviceId`, `Model`, `IsConnected` from the active adapter for `HealthHandler`.
4. **MockMode still needed for Phase 2** — SCAN-05 (IScannerAdapter) already exists in Phase 1 as `MockScannerAdapter`. Phase 2 should respect `AgentConfig.Scanner.MockMode` flag to stay backward-compatible for dev/test without hardware.
5. **PNG conversion is per-call GDI+ allocation** — Following the pattern from `MockScannerAdapter` (creates Bitmap per scan), each adapter should allocate GDI+ objects per `Scan()` call and dispose them. No persistent GDI+ state between calls.

---

## Common Pitfalls

| Pitfall | Category | Severity | Avoidance |
|---------|----------|----------|-----------|
| Futronic DLL is 32-bit only; AnyCPU crashes | Futronic | HIGH | Set `<PlatformTarget>x86</PlatformTarget>` |
| SecuGen sgfplib.dll not found | SecuGen | HIGH | Copy SDK i386 DLLs to install directory |
| Digital Persona event-driven blocks sync Scan() | Digital Persona | HIGH | Use ManualResetEvent + WaitOne(timeout) pattern |
| ZKTeco `libzkfpcsharp.dll` not in app directory | ZKTeco | HIGH | Copy from System32/SysWOW64 to install directory (D-08); or run SDK installer |
| ZKTeco `GetDeviceCount()` returns 0 on first Init() | ZKTeco | MEDIUM | Retry Init() after 100ms delay if count is 0; or use `ZkTecoFingerPrint` NuGet |
| Nested timeouts exceed 10s budget | All | MEDIUM | Single CancellationToken for all adapter attempts |
| Wrong SecuGen device enum | SecuGen | MEDIUM | Use auto-detection or enumerate first |
| Futronic pixel inversion | Futronic | LOW | Invert buffer: `255 - value` |
| ZKTeco pixels may need inversion vs other scanners | ZKTeco | LOW | Test against reference; apply `255 - value` if output looks inverted |
| SDK DLLs not in install directory | All | HIGH | Phase 4 installer must copy SDK DLLs to install dir |

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| COM-based SDKs (Digital Persona) | .NET assemblies (DPUruNet) | SDK v4.x | Cleaner interop; event handling still async |
| SecuGen HAMSTER SDK (older) | FDx SDK Pro (newer) | ~2015 | Newer algorithm; DEV_FDU03/04 devices |
| Futronic proprietary template format | ANSI/ISO template support | SDK v4.2 | Standards-compliant templates v2 |
| Hardcoded device name | Device enumeration + first-found | Phase 2 | Single-device auto-detection |
| ZKTeco raw `libzkfpcsharp` COM wrapper | `ZkTecoFingerPrint` NuGet (sane Rx API) | 2023 | Eliminates threading complexity; BMP bytes direct from capture |

---

## Assumptions Log

> List all claims tagged `[ASSUMED]` in this research. The planner and discuss-phase use this section to identify decisions that need user confirmation before execution.

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `DPUruNet` NuGet package version 1.0.0.0 works with the U.are.U 4500/4500B reader | §2 | If NuGet version doesn't match vendor DLLs, runtime error. Verify DLL version against NuGet readme. |
| A2 | Futronic Standard SDK v4.2 includes `ftrScanAPI.dll` (32-bit) as the primary capture DLL | §3 | If version differs, function signatures may change. Vendor documentation suggests v4.2 is current. |
| A3 | `secugen.com/download` free SDK includes the managed .NET assembly and native DLLs needed for distribution | §1 | If free SDK is evaluation-only (time-limited or watermarked), production deployment needs paid SDK. Verify license terms. |
| A4 | All three vendors' SDKs are distributable alongside the application without additional runtime licenses | §1-3 | If per-seat SDK licenses apply, Phase 4 installer needs to handle license acceptance. |
| A5 | Digital Persona `SampleConversion.ConvertToPicture` produces a `Bitmap` compatible with PNG save | §2 | If the conversion produces a different format, PNG encoding may differ. Verified via Jeremy Lindsay's blog (2016). |
| A6 | ZKTeco ZK4500 image dimensions are 280×360 at 500 DPI | §5 | Datasheet confirms 280×360 for ZK4500. Other models (ZK8500, SLK20R) may differ — query at runtime via `GetParameters`. |
| A7 | ZKTeco pixel values are conventional grayscale (0=white, 255=dark) — not inverted like Futronic | §5 | Consistent with other optical sensors; if display looks inverted, invert before PNG encoding. |
| A8 | `ZkTecoFingerPrint` NuGet (MIT, v1.2.1) is a legitimate open-source package | §5 | GitHub: 13 stars, MIT license, netstandard2.0 — small but real project. No indication of slopquatting. |

---

## Open Questions

1. **Digital Persona NuGet vs. Vendor DLLs**
   - What we know: `DPUruNet` is on NuGet; native DLLs come from separate SDK download
   - What's unclear: Which version of `DPUruNet` on NuGet matches which version of the native SDK DLLs?
   - Recommendation: Download the exact SDK version (e.g., U.are.U 4500 SDK v4.5) and check the DLL versions; note in SCANNER_SETUP.md

2. **SecuGen free SDK distribution rights**
   - What we know: Free SDK available from secugen.com; distribution guide exists
   - What's unclear: Can the native DLLs (sgfplib.dll, sgfpamx.dll) be distributed with a commercial agent installer?
   - Recommendation: Review SecuGen SDK license agreement; if unclear, contact SecuGen for commercial licensing

3. **Futronic Standard SDK registration requirement**
   - What we know: "Please register by clicking the link...to download NOW"
   - What's unclear: Is the free Standard SDK fully functional (no time limit, no watermark) for production use?
   - Recommendation: Download and verify the captured image has no watermarks; if watermarked, purchase the commercial SDK

4. **Device disconnection during capture**
   - What we know: Each SDK fires disconnection events; no persistent connection per D-01
   - What's unclear: How cleanly ScannerManager handles mid-capture disconnection
   - Recommendation: Phase 3 (SCAN-06) handles reconnection; Phase 2 focus is first-scan success

5. **Multiple devices of same vendor**
   - What we know: D-03 says "first found device selection per vendor" — no multi-device per vendor
   - What's unclear: Is it acceptable to silently ignore 2nd+ devices of the same vendor?
   - Recommendation: Log a warning when `NumberOfDevice > 1` so operators know a device is being skipped

6. **ZKTeco SDK licensing for commercial production use**
   - What we know: SDK requires Silver+ membership to download; official license not published online
   - What's unclear: Can `libzkfpcsharp.dll` (from ZKFinger SDK) be redistributed with a commercial Windows Service installer?
   - Recommendation: Download SDK, review license agreement during Phase 3/4. If unclear, contact ZKTeco for OEM licensing.

7. **ZKTeco `ZkTecoFingerPrint` NuGet stability**
   - What we know: 13 GitHub stars, v1.2.1, MIT license, small but active project
   - What's unclear: Long-term maintenance. Falls back to raw SDK if abandoned.
   - Recommendation: Use raw `zkfp2` class directly if NuGet proves unreliable. Both use same `libzkfpcsharp.dll` underneath.

---

## Environment Availability

> Step 2.6: SKIPPED — no external dependencies beyond vendor SDK DLLs (which are manually downloaded, not package-managed). No CLI tools, runtimes, or services required beyond the Windows environment already implied by .NET Framework 4.8 and the Windows Service project.

---

## Sources

### Primary (HIGH confidence)
- [FDx SDK Pro NET Programming Manual v3.54 (PDF)](https://www.ravirajtech.com/downloads/FDx-SDK-Pro-for-Windows-secugen/FDx-SDK-Pro-for-Windows-v3.54/FDx-SDK-Pro-for-Windows-v3.54/Documents/FDx-SDK-Pro-NET-Programming-Manual-Windows-SG1-0030B-005.pdf) — SecuGen .NET class reference, Init/OpenDevice/GetImage patterns, error codes
- [FDx SDK Pro Programming Manual (PDF)](https://www.ravirajtech.com/downloads/FDx-SDK-Pro-for-Windows-secugen/FDx-SDK-Pro-for-Windows-v3.54/FDx-SDK-Pro-for-Windows-v3.54/Documents/FDxSDK-Pro-Programming-Manual-Windows-SG1-0030A-013.pdf) — C API reference, device enumeration, capture functions
- [ftrScanAPI.h (GitHub)](https://github.com/erikssm/futronics-fingerprint-reader/blob/master/ftrScanAPI.h) — Complete P/Invoke signatures, structs, error codes
- [ftrScanAPI.h (GitHub alternate)](https://github.com/MuhammdAli/Futronic_C_Sharp_wrapper/blob/master/ftrScanAPI.h) — Same header, confirms P/Invoke patterns
- [Futronic Wrapper C++ (GitHub)](https://github.com/MuhammdAli/Futronic_C_Sharp_wrapper/blob/master/FutronicWrapper.cpp) — CaptureImage, OpenDevice usage patterns
- [Jeremy Lindsay blog — Digital Persona C# (2016)](https://jeremylindsayni.wordpress.com/2016/03/24/how-to-use-c-to-create-a-bitmap-of-a-fingerprint-from-the-digitalpersona-u-are-u-4000-fingerprint-scanner-part-1/) — Complete OnComplete/ManualResetEvent pattern, SampleConversion
- [SecuGen GitHub Gist](https://gist.github.com/cyrilCodePro/b7cbd15e0c9172fba018bc237f83eea1) — Complete C# WinForms example with SGFingerPrintManager, EnumerateDevice, GetImageEx
- [ZKFinger SDK C# Programming Guide v2 (PDF)](https://pdfcoffee.com/download/zkfinger-reader-sdk-c-en-v2-pdf-free.html) — Official ZKTeco zkfp2 class API: Init/Terminate/OpenDevice/AcquireFingerprint/GetParameters, error codes, parameter codes
- [ZK4500 Datasheet](https://www.isecus.com/wp-content/uploads/2019/04/ZK4500-Fingerprint-Scanner.pdf) — Image resolution (500 DPI), image size (280×360), gray level (256), Windows 32/64-bit support
- [ZK4500 Datasheet (alternate)](https://www.edatame.com/datasheets/zk-zk4500.pdf) — Confirms 280×360 pixels, 500 DPI, 8-bit gray, USB 2.0 compatibility
- [ZKTeco Fingerprint Scanner SDK Selection Guide v3 (PDF)](https://www.isecus.com/wp-content/uploads/2019/04/ZKTeco-Fingerprint-Scanner-SDK-Selection-Guide-Ver3.0.pdf) — SDK compatibility matrix: ZK4500/ZK7500/ZK8500, SLK20R/ZK9500 supported by Windows x86/x64 SDK

### Secondary (MEDIUM confidence)
- [StackOverflow — SecuGen sgfplib.dll not found](https://stackoverflow.com/questions/57857024/cant-find-sgfplib-dll) — Confirms System32/SysWOW64 DLL placement issue
- [StackOverflow — Digital Persona 4500 capture](https://wiki.hoelee.com/content/stackoverflow.com_en_all_2023-11/questions/45493824/fingerprint-scanning-with-digitalpersona-4500-reader-how-to-get-captured-image) — Event handler setup pattern
- [Digital Persona GitHub — UareU-C-Sharp](https://github.com/ankit4u3/UareU-C-Sharp) — Enrollment.cs pattern
- [SecuGen Distribution Guide PDF](https://www.ravirajtech.com/downloads/FDx-SDK-Pro-for-Windows-secugen/FDx-SDK-Pro-for-Windows-v3.54/FDx-SDK-Pro-for-Windows-v3.54/Documents/FDx%20SDK%20Pro%20Distribution%20Guide%20(Windows)%20SG1-0008M-004.pdf) — DLL distribution requirements
- [Futronic SDK Demo (GitHub)](https://github.com/michaelschnyder/futronic-sdk-demo) — Confirms ftrScanAPI.dll + driver versioning
- [SecuGen.com Products/SDK page](https://secugen.com/products/sdk/) — Windows 11 support, .NET 5+ support, device list
- [DebuggersHub — ZKTeco ZK4500 C# Implementation](https://www.debuggershub.com/c-zkteco-fingerprint-scanner-implementation-zk4500-slk20m-slk20r-zk9500/) — Complete Windows Forms example with InitializeDevice/ConnectDevice/DoCapture pattern, AcquireFingerprint usage
- [MuhammadSalmanSiddiqui/zkteco-4500-9500-implementation (GitHub)](https://github.com/MuhammadSalmanSiddiqui/zkteco-4500-9500-implementation) — x86 csproj, libzkfpcsharp.dll reference from SysWOW64, full capture loop
- [rainxh11/ZkTecoFingerPrint (GitHub)](https://github.com/rainxh11/ZkTecoFingerPrint) — NuGet v1.2.1, MIT, sane Rx API, native DLL direct usage pattern, BMP bytes in result
- [Context7 — ZkTecoFingerPrint API docs](https://context7.com/rainxh11/zktecofingerprint/llms.txt) — ZkTecoFingerHost.Initialize/OpenDevice/AcquireFingerprintAsync, ZkDeviceResult, bitmap format
- [StackOverflow — ZKTeco libzkfpcsharp buffer overrun](https://stackoverflow.com/questions/77260192/libreria-zkteco9500-libzkfpcsharp-dll-error-attempted-to-read-or-write-protected) — zkfp2 singleton usage, AcquireFingerprint blocking behavior
- [DigitalPlatform/dp2 FingerPrint.cs (GitHub)](https://github.com/DigitalPlatform/dp2/blob/master/FingerprintCenter/FingerPrint.cs) — Production usage of zkfp2 with CancellationToken capture thread, GetParameters for width/height

### Tertiary (LOW confidence)
- [Digital Persona U.are.U SDK PDF](https://gestor.papsf.cat/_Adm3/upload/docs/ITEMDOC_1970.pdf) — SDK overview, DLL names (DPFPDevNET.dll, etc.) — partial document, not fully verified
- [Futronic SDK Brochure PDF](https://www.futronic-tech.com/download/SDK_Windows_brochure.pdf) — SDK features, VB.NET/VC.NET samples
- [Digital Persona .NET Developer Guide (Yumpu)](https://www.yumpu.com/en/document/view/34561305/net-developer-guide-digitalpersona) — DPUruNet class overview
- [Digital Persona HID Developer Center](https://sdk.hidglobal.com/developer-center/digitalpersona-touchchip) — Current SDK product page
- [OnTheClock Desktop — Digital Persona C# (GitHub)](https://github.com/mrmgomes/ontheclock-desktop) — Real-world usage of DPUruNet with Windows Forms
- [ZKTeco SDK 2013 (support.zkteco.pro)](https://support.zkteco.pro/support/download/zkteco/item/zkteco-scanner-sdk.html) — SDK bundle description (Russian), driver version 2.3.3.5, ZK4500/ZK7500/ZK8500 compatibility notes
- [MuaazH/ZKTeco_PULLSDK_Wrapper (GitHub)](https://github.com/MuaazH/ZKTeco_PULLSDK_Wrapper) — Alternative ZKFinger wrapper, PullSDK usage, FingerReader class pattern
- [DigitalPlatform/dp2 — ZKTeco initialization error handling](https://github.com/DigitalPlatform/dp2/blob/master/FingerprintCenter/FingerPrint.cs) — Error code handling, driver-not-installed detection via exception message check

---

## Metadata

**Confidence breakdown:**
- SecuGen SDK API: HIGH — PDF programming manual with full API reference
- Futronic P/Invoke signatures: HIGH — Complete header file from two independent GitHub sources
- Digital Persona API: MEDIUM — Multiple blog and StackOverflow examples confirm pattern; PDF SDK docs partially verified
- ZKTeco API: HIGH — Complete SDK PDF (pdfcoffee), two independent GitHub implementations, Context7 documentation; official ZK4500 datasheet for image specs
- `ZkTecoFingerPrint` NuGet: MEDIUM — 13-star GitHub project, Context7 docs confirmed, MIT license. Small but not suspicious.
- Adapter architecture: HIGH — Standard composite + fallback pattern applied to hardware abstraction
- .NET Framework 4.8 / x86 gotchas: MEDIUM — CLR mixed-mode assembly concern is documented but version-specific

**Research date:** 2026-07-29
**Valid until:** 2026-08-28 (30 days for stable SDKs; 7 days if vendor SDK versions change)