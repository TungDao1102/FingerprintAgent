# Device Compatibility Matrix

**Project:** KCB Fingerprint Capture Agent  
**Last Updated:** July 2026

---

## 1. Scanner Comparison Table

| Brand / Model | SDK Name | .NET Support | Windows 7 32-bit | Image Format | License Cost | Vietnam Distributor |
|---------------|----------|-------------|:---------------:|--------------|-------------|---------------------|
| **SecuGen** Hamster Plus | FDx SDK Pro | Native .NET assembly | ✅ Yes | SG400 (proprietary), ANSI 378, ISO 19794-2 | Free | SmartID (smartid.com.vn) |
| **SecuGen** Hamster Pro 20 | FDx SDK Pro | Native .NET assembly | ✅ Yes | SG400, ANSI 378, ISO 19794-2 | Free | SmartID (smartid.com.vn) |
| **SecuGen** Hamster Pro 30 | FDx SDK Pro | Native .NET assembly | ✅ Yes | SG400, ANSI 378, ISO 19794-2 | Free | SmartID (smartid.com.vn) |
| **Digital Persona** U.are.U 4500 | U.are.U SDK (DPFP) | Native .NET via DPFP.Capture, DPFP.Processing | ✅ Yes (with legacy driver) | ANSI 378, ISO 19794-2 | Contact HID Global (royalty-free) | VTTech |
| **Digital Persona** U.are.U 3200 | U.are.U SDK (DPFP) | Native .NET via DPFP.Capture, DPFP.Processing | ✅ Yes (with legacy driver) | ANSI 378, ISO 19794-2 | Contact HID Global (royalty-free) | VTTech |
| **Futronic** FS80 | Futronic Standard SDK | P/Invoke only (no native .NET) | ✅ Yes | Proprietary only (free SDK) / ANSI 378, ISO 19794-2 ($999 SDK) | Free Standard / $999 ANSI+ | SmartID, VTTech |
| **Futronic** FS90 | Futronic Standard SDK | P/Invoke only (no native .NET) | ✅ Yes | Proprietary only (free SDK) / ANSI 378, ISO 19794-2 ($999 SDK) | Free Standard / $999 ANSI+ | SmartID, VTTech |

### Recommended Scanner: **SecuGen Hamster Plus**

- **Reason:** Free SDK with native .NET support, no P/Invoke required, excellent Windows 7 32-bit support, widest image format support, competitive pricing with local Vietnam distributor.

---

## 2. SecuGen — FDx SDK Pro

### 2.1 SDK Overview

| Item | Detail |
|------|--------|
| DLL | `SecuGen.FDxSDKPro.Windows.dll` |
| Platform | x86 (32-bit) |
| .NET Version | .NET 1.1+ and .NET 4.0+ |
| Runtime Dependency | VC++ Redistributable 2015+ x86 |
| Vietnam Distributor | SmartID — smartid.com.vn |
| Device ID String | `"SecuGen Fingerprint Scanner"` |

### 2.2 Project References

Add a reference to `SecuGen.FDxSDKPro.Windows.dll` in the Visual Studio project. The DLL must be deployed to the same directory as the agent's executable.

### 2.3 P/Invoke / Native Declarations

The SecuGen SDK exposes a COM-like interface. Most functions are accessible via the managed `SecuGen.FDxSDKPro.Windows.SGFingerPrintManager` class:

```csharp
// Managed C# wrapper class (provided by SecuGen SDK)
// No P/Invoke required — use the managed API directly

using SecuGen.FDxSDKPro.Windows;

// Initialization
var manager = new SGFingerPrintManager();
var error = manager.Init(SGFDxDeviceType.SG_DEV_AUTO, SGFDxSecurityLevel.SL_NORMAL);
error = manager.OpenDevice(0); // 0 = first available device
```

### 2.4 Core API Surface

| Method | Description |
|--------|-------------|
| `Init(deviceType, securityLevel)` | Initialize the SDK |
| `OpenDevice(deviceId)` | Open the USB device (deviceId = 0 for auto-detect) |
| `GetImage(out byte[] imageBuffer)` | Capture raw fingerprint image |
| `GetImageQuality(imageWidth, imageHeight, imageData, out quality)` | Check capture quality (0–100) |
| `CreateTemplate(imageData, templateBuffer)` | Create ANSI 378 template |
| `CloseDevice()` | Close the device |
| `GetDeviceInfo(out SgDeviceInfo info)` | Get device model and serial |

### 2.5 Image Output

- **Native format:** SG400 (SecuGen proprietary, 2-byte/pixel greyscale)
- **Convert to PNG:** Use `Bitmap` class: create Bitmap from raw buffer (width=256, height=364 typically), save as PNG via `Bitmap.Save(stream, ImageFormat.Png)`
- **Recommended resolution:** 500 DPI

### 2.6 Error Codes

| Constant | Value | Description |
|----------|-------|-------------|
| `SGFDxErrorCode.NONE` | 0 | Success |
| `SGFDxErrorCode.DEVICE_NOT_FOUND` | 1001 | No SecuGen device detected |
| `SGFDxErrorCode.DEVICE_OPEN_FAILED` | 1002 | Failed to open device |
| `SGFDxErrorCode.CAPTURE_FAILED` | 2001 | Capture error |

---

## 3. Digital Persona (HID) — U.are.U SDK

### 3.1 SDK Overview

| Item | Detail |
|------|--------|
| Namespace | `DPFP.Capture`, `DPFP.Processing`, `DPFP.Verification` |
| .NET Support | Native .NET via DPFP assemblies |
| Platform | x86 (32-bit) |
| Runtime Dependency | Legacy non-WBF driver on Windows 10+ |
| Vietnam Distributor | VTTech |
| Device ID | Uses device index (0, 1, ...) |

### 3.2 Critical Warning: WBF Driver Conflict

> **⚠️ Windows 10/11 WBF Driver Conflict:** On Windows 10 and later, the default Windows Biometric Framework (WBF) driver may conflict with the Digital Persona scanner. **Use the legacy non-WBF driver** (from HID Global) on Windows 10+ to avoid the scanner being claimed by WBF and becoming inaccessible to the SDK.

Install the legacy driver by:
1. Download legacy driver from HID Global support portal
2. Disable Windows Hello fingerprint enrollment
3. In Device Manager, update the driver to the legacy DP driver (not the Microsoft WBF driver)

### 3.3 Project References

Add references to the DPFP assemblies provided with the U.are.U SDK:
- `DPFPCapture.dll`
- `DPFPShr.dll`
- `DPFPVer.dll`

```csharp
using DPFP.Capture;
using DPFP.Processing;

public class DigitalPersonaAdapter : IScannerAdapter
{
    private Capture _capture;
    private SampleConverter _sampleConverter;

    public void Initialize()
    {
        _capture = new Capture(SampleFormat.ANSI381);
        _sampleConverter = new SampleToImage();
        _capture.StartCapture();
    }
}
```

### 3.4 Core API Surface

| Function | Description |
|----------|-------------|
| `dpfpdd_init()` | Initialize the driver DLL |
| `dpfpdd_open(deviceIndex)` | Open device by index |
| `dpfpdd_capture(handle, timeoutMs)` | Capture fingerprint |
| `dpfpdd_close(handle)` | Close device |

### 3.5 Image Output

- **Native format:** ANSI 378 or internal DP format
- **Convert to PNG:** The SDK provides `SampleToImage` converter. Convert the `Sample` object to a `Bitmap`, then save as PNG.

---

## 4. Futronic — Standard SDK

### 4.1 SDK Overview

| Item | Detail |
|------|--------|
| Interface | P/Invoke only (no native .NET) |
| Platform | **x86 only** (32-bit) |
| Free SDK Image Format | Proprietary (Futronic format only) |
| ANSI/ISO SDK Cost | $999 (provides ANSI 378, ISO 19794-2) |
| Vietnam Distributor | SmartID, VTTech |
| Device ID | Uses device index or `FTRSCAN_FIRST` constant |

### 4.2 P/Invoke Signatures

The Futronic SDK does **not** provide a native .NET assembly. All calls are via `DllImport`.

```csharp
public static class FutronicNative
{
    private const string FutronicSdk = "ftrScanAPI.dll";

    [DllImport(FutronicSdk, EntryPoint = "FtrScanOpenDevice",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern bool FtrScanOpenDevice(IntPtr device, int deviceIndex);

    [DllImport(FutronicSdk, EntryPoint = "FtrScanCloseDevice",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern bool FtrScanCloseDevice(IntPtr device);

    [DllImport(FutronicSdk, EntryPoint = "FtrScanCaptureImage",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern bool FtrScanCaptureImage(
        IntPtr device,
        IntPtr imageBuffer,
        int imageBufferSize,
        int timeout,
        out int qualityScore);

    [DllImport(FutronicSdk, EntryPoint = "FtrScanGetImageSize",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern bool FtrScanGetImageSize(
        IntPtr device,
        out int width,
        out int height);

    [DllImport(FutronicSdk, EntryPoint = "FtrScanIsDeviceOpened",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern bool FtrScanIsDeviceOpened(IntPtr device);
}
```

### 4.3 Key Implementation Notes

1. **Platform Target = x86:** The Futronic SDK is 32-bit only. Set `<PlatformTarget>x86</PlatformTarget>` in the project file and ensure the build output is 32-bit.

2. **Proprietary Format Lock-in:** The free Standard SDK outputs only Futronic's proprietary format. This image format is **not** ANSI 378 or ISO 19794-2 compliant. If standards compliance is required, purchase the $999 ANSI/ISO SDK.

3. **Device Handle:** Use `IntPtr` for the device handle. Initialize with `FtrScanOpenDevice` and pass the handle to subsequent calls.

4. **Image Buffer Allocation:** Call `FtrScanGetImageSize` first to get dimensions, then allocate a byte array of `width × height` bytes (8-bit greyscale).

5. **Error Handling:** Check return values (`false` = error). Call `FtrScanGetLastError()` to get the error code.

---

## 5. Adapter Pattern — Interface Definition

All three scanner adapters implement the common `IScannerAdapter` interface:

```csharp
public interface IScannerAdapter : IDisposable
{
    /// <summary>Human-readable device name</summary>
    string DeviceName { get; }

    /// <summary>Unique device identifier (e.g., "secugen-hamster-001")</summary>
    string DeviceId { get; }

    /// <summary>Whether this adapter is currently connected and ready</summary>
    bool IsConnected { get; }

    /// <summary>Initialize the scanner (open handle, load calibration)</summary>
    Task<bool> InitializeAsync(CancellationToken ct = default);

    /// <summary>Poll hardware to check connection status</summary>
    Task<bool> IsConnectedAsync(CancellationToken ct = default);

    /// <summary>Capture fingerprint and return raw PNG bytes</summary>
    Task<byte[]> CaptureAsync(int timeoutMs = 30000, CancellationToken ct = default);

    /// <summary>Release hardware resources</summary>
    void Disconnect();
}
```

The `ScannerManager` uses this interface to route all operations, enabling runtime scanner type switching via `config.json`.

---

## 6. Multi-Scanner Auto-Detection

When `scannerType` is set to `"auto"` in `config.json`, the agent should attempt to open scanners in this order:

1. **SecuGen** — Most reliable W7 32-bit support, native .NET, free SDK
2. **Digital Persona** — Good support with legacy driver workaround
3. **Futronic** — P/Invoke only, proprietary format on free SDK

```csharp
public IScannerAdapter DetectScanner()
{
    var secuGen = new SecuGenAdapter();
    if (secuGen.Initialize()) return secuGen;

    var digitalPersona = new DigitalPersonaAdapter();
    if (digitalPersona.Initialize()) return digitalPersona;

    var futronic = new FutronicAdapter();
    if (futronic.Initialize()) return futronic;

    throw new ScannerException("No supported fingerprint scanner detected");
}
```

---

## 7. USB Device Enumeration Notes

### SecuGen
- Uses device index (0, 1, 2, ...) — not USB serial numbers
- Multiple SecuGen devices can be connected; specify `deviceId` in config

### Digital Persona
- Uses device index — `dpfpdd_open(0)` opens first device
- Device index assignment may change after USB re-enumeration

### Futronic
- `FtrScanOpenDevice(NULL, 0)` opens first device (`NULL` for auto-detect)
- `FtrScanOpenDevice(devicePtr, index)` opens specific device

---

## 8. Driver Installation Summary

| Scanner | Windows 7 Driver | Windows 10+ Driver | Installation Source |
|---------|-----------------|-------------------|---------------------|
| SecuGen | Built-in SecuGen USB driver | Same (WBF not required) | SecuGen support site |
| Digital Persona | Legacy DP driver | **Legacy DP driver** (NOT WBF) | HID Global support portal |
| Futronic | Built-in Windows USB driver | Same | Futronic SDK installer |

### Digital Persona Legacy Driver Installation Steps

1. Download legacy driver from HID Global (do NOT use Windows Update driver)
2. Disable WBF in Windows Registry: `HKLM\SOFTWARE\WOW6432Node\DigitalPersona\Plugins\Fingerprint\EnableWBF = 0`
3. In Device Manager, right-click the fingerprint device → Update Driver → Browse → Let me pick → Have Disk → point to legacy driver `.inf`
4. Verify device is NOT listed under "Biometric devices" (WBF) but under "Universal Serial Bus controllers" or "HID"

---

## 9. SDK Download Links (Reference)

| SDK | Download | Notes |
|-----|----------|-------|
| SecuGen FDx SDK Pro | smartid.com.vn or secugen.com | Free registration required |
| HID U.are.U SDK | hidglobal.com | Requires HID developer account |
| Futronic Standard SDK | futronic.com.tw | Free download; $999 for ANSI/ISO SDK |

> **Note:** URLs may change. Verify distributor websites for latest SDK versions and download instructions.