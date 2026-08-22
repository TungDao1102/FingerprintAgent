# PLAN: ZKTeco NuGet → P/Invoke Migration — Execution Detail

> **Source plan:** `.planning/plans/zkteco-pinvoke-migration-plan.md`
> **Commit convention:** `<type>(05): <description>` per repo convention (`feat|fix|test|docs|refactor|chore`)
> **Baseline:** 203/212 tests pass, build Release 0/0 (HEAD `72540bb`, 2026-08-22)
> **Target:** 212/212 tests, 0/0 build, 0 reference to `ZkTecoFingerPrint` trong `src/`

---

## Scope

Migrate ZKTeco adapter từ `ZkTecoFingerPrint 1.2.1` NuGet wrapper sang raw P/Invoke trên `libzkfp.dll`. Fix đồng thời 3 vấn đề critical:

- **W1**: SourceAFIS extraction + GDI+ BMP build chạy mỗi capture thành công (+100–300ms CPU vô ích)
- **W2**: leak `AllocHGlobal` khi delegate ném exception (wrapper không có try/finally)
- **C3/W3**: race song-song capture → native use-after-free → AccessViolation giết process

Giữ nguyên behavior quan sát được từ ngoài: HTTP contract, error codes/messages, rolling-capture ~15s, PNG grayscale không inversion, correlation ID logging.

---

## T0 — Pre-flight Baseline ✅ DONE

Đã verify tại HEAD `72540bb` (2026-08-22):

- Build Release: **0 warning / 0 error**
- Tests: **203/212 PASS**, 9 FAILED (pre-existing)

Nguyên nhân 9 fail: `ZKTecoDeviceIntegrationTests` gọi `adapter.Initialize()` → P/Invoke `ZKFPM_Init()` ném `DllNotFoundException` trên máy không có `libzkfp.dll`. Cơ chế "skip gracefully" chỉ hoạt động khi Initialize **trả false** — không xử lý case SDK vắng hoàn toàn → throw.

**Quyết định:** baseline = 203/212 với 9 known-failures. Trong T3, `InitializeInternal` mới sẽ catch `DllNotFoundException`/`BadImageFormatException` → trả `false` + `_vendorErrorCode="DLL_NOT_FOUND"` → 9 test này chuyển thành pass-by-design. Mục tiêu sau migrate: **212/212**.

---

## T1 — Serialize Concurrent Capture (C3 Fix)

### Files

| Action | File |
|---|---|
| MODIFY | `src/FingerprintAgent/Adapters/ScannerManager.cs` |
| CREATE | `tests/FingerprintAgent.Tests/Scanner/ScannerManagerConcurrencyTests.cs` |

### ScannerManager.cs — 4 thay đổi

**Thay đổi 1 — Thêm field** (sau dòng 35, sau `_adapterLock`):

```csharp
private readonly SemaphoreSlim _scanGate = new SemaphoreSlim(1, 1);
```

**Thay đổi 2 — Wrap `ScanAsync`** (dòng 287): body hiện tại GIỮ NGUYÊN, chỉ bọc ngoài bằng gate:

```csharp
public async Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
{
    string cid = AgentLogger.GenerateCorrelationId();

    await _scanGate.WaitAsync(cancellationToken);
    try
    {
        // === TOÀN BỘ BODY HIỆN TẠI Ở ĐÂY — KHÔNG SỬA GÌ ===
        // mock path, SCAN-06 backoff retry, 20s budget, foreach adapter...
    }
    finally
    {
        _scanGate.Release();
    }
}
```

**Thay đổi 3 — `TryProbe` non-blocking** (dòng 99): thêm block NGAY SAU khai báo variables, TRƯỚC logic probe hiện tại:

```csharp
// === THÊM MỚI: non-blocking gate check ===
if (!_scanGate.Wait(0))
{
    // Capture in progress — return cached state, don't block /health
    IScannerAdapter cachedActive;
    lock (_adapterLock) { cachedActive = _activeAdapter; }
    if (cachedActive != null)
    {
        deviceId = cachedActive.DeviceId;
        model = cachedActive.Model;
        vendorErrorCode = cachedActive.VendorErrorCode;
        return cachedActive.IsConnected;
    }
    return false;
}
try
{
    // === TOÀN BỘ LOGIC PROBE HIỆN TẠI Ở ĐÂY ===
    // fast path cached.ProbeConnection(), foreach adapter.Initialize()...
}
finally
{
    _scanGate.Release();
}
```

**Thay đổi 4 — `Dispose`** (dòng 387): thêm 1 dòng sau `_cts?.Dispose();`:

```csharp
_scanGate?.Dispose();
```

### Test mới — ScannerManagerConcurrencyTests.cs

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    public class ScannerManagerConcurrencyTests
    {
        /// <summary>
        /// Slow adapter simulating a 200ms capture — enough time to detect overlap.
        /// Tracks max observed concurrency via Interlocked CAS loop.
        /// </summary>
        private class SlowAdapter : IScannerAdapter, IDisposable
        {
            private int _concurrentCount;
            private int _maxConcurrent;

            public bool IsConnected => true;
            public string DeviceId => "slow-device";
            public string Model => "Slow Scanner";
            public string MimeType => "image/png";
            public string VendorErrorCode => "NONE";
            public int MaxObservedConcurrency => _maxConcurrent;

            public bool Initialize() => true;
            public bool ProbeConnection() => true;

            public async Task<CaptureResult> ScanAsync(CancellationToken ct = default)
            {
                int count = Interlocked.Increment(ref _concurrentCount);
                int prev;
                do { prev = _maxConcurrent; }
                while (count > prev &&
                       Interlocked.CompareExchange(ref _maxConcurrent, count, prev) != prev);

                await Task.Delay(200, ct);

                Interlocked.Decrement(ref _concurrentCount);
                return CaptureResult.Ok(new byte[] { 1, 2, 3 });
            }

            public void Dispose() { }
        }

        [Fact]
        public async Task ScanAsync_SerializesConcurrentCalls_NoOverlap()
        {
            var slow = new SlowAdapter();
            var manager = new ScannerManager(new IScannerAdapter[] { slow }, null);

            var t1 = manager.ScanAsync();
            var t2 = manager.ScanAsync();
            var t3 = manager.ScanAsync();

            await Task.WhenAll(t1, t2, t3);

            // Max concurrent must be 1 — proves SemaphoreSlim serializes captures
            Assert.Equal(1, slow.MaxObservedConcurrency);
        }

        [Fact]
        public async Task ScanAsync_AllConcurrentCallsSucceed()
        {
            var slow = new SlowAdapter();
            var manager = new ScannerManager(new IScannerAdapter[] { slow }, null);

            var results = await Task.WhenAll(
                manager.ScanAsync(), manager.ScanAsync(), manager.ScanAsync());

            foreach (var r in results)
                Assert.True(r.IsSuccess);
        }
    }
}
```

### Accept T1

- `dotnet build FingerprintAgent.sln -c Release` → 0 warning / 0 error
- `dotnet test FingerprintAgent.sln -c Release` → ≥ 203/212 (no regressions), concurrency tests pass
- `ScanAsync_SerializesConcurrentCalls_NoOverlap` asserts `MaxObservedConcurrency == 1`

### Commit T1

```
fix(05): serialize concurrent ScanAsync via SemaphoreSlim — close C3 use-after-free window
```

---

## T2 — Create `ZkNativeHost.cs`

### File

| Action | File |
|---|---|
| CREATE | `src/FingerprintAgent/Adapters/ZkNativeHost.cs` (~150 dòng) |

### Structure

`internal static class ZkNativeHost`, namespace `FingerprintAgent.Adapters`.

### P/Invoke Declarations (7 DllImports)

```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Raw P/Invoke layer for libzkfp.dll (ZKTeco fingerprint SDK).
    /// Replaces ZkTecoFingerPrint NuGet wrapper (v1.2.1) — eliminates transitive
    /// deps (SourceAFIS, System.Reactive, Dahomey.Cbor) and fixes W1/W2/W5.
    ///
    /// Prior art:
    /// - tests/.../Scanner/ZkSdkProbe.cs: 6/7 DllImport verified on real ZK9500
    /// - Commit 4c7c358: ZKFPM_AcquireFingerprint signature (StdCall, template 2048)
    /// - Wrapper source rainxh11/ZkTecoFingerPrint@31e5941: GetCaptureParamsEx, param 1102/1103
    /// </summary>
    internal static class ZkNativeHost
    {
        // CallingConvention: default Winapi = StdCall on x86. NEVER set Cdecl (F2).

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_Init();

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_Terminate();

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_GetDeviceCount();

        /// <summary>
        /// Returns raw int handle. Positive = valid; zero OR NEGATIVE = fail.
        /// CRITICAL GUARD: check &gt; 0, not just != 0 — ZkSdkProbe.cs:64-75 observed
        /// negative handles when device held by another process. A negative value
        /// passed to AcquireFingerprint = undefined behavior.
        /// </summary>
        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_OpenDevice(int index);

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_CloseDevice(IntPtr handle);

        /// <summary>Blocking acquire (~1s on ZK9500). Caller wraps in Task.Run + try/finally FreeHGlobal.</summary>
        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_AcquireFingerprint(
            IntPtr hDevice, IntPtr fpImage, uint cbFPImage,
            IntPtr fpTemplate, ref int cbTemplate);

        /// <summary>
        /// Read parameter by code. Codes verified on ZK9500:
        /// 1=width, 2=height, 3=dpi, 1102=product_name, 1103=serial_number.
        /// </summary>
        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_GetParameters(
            IntPtr hDev, int nParamCode, IntPtr paramValue, ref int cbParamValue);
```

### Error Constants

```csharp
        internal const int ZKFP_OK                = 0;
        internal const int ZKFP_ALREADY_INIT      = 1;   // F5: host usable
        internal const int ZKFP_ERR_INITLIB       = -1;
        internal const int ZKFP_ERR_INIT          = -2;
        internal const int ZKFP_ERR_NO_DEVICE     = -3;
        internal const int ZKFP_ERR_NOT_SUPPORT   = -4;
        internal const int ZKFP_ERR_INVALID_PARAM = -5;
        internal const int ZKFP_ERR_OPEN          = -6;
        internal const int ZKFP_ERR_INVALID_HANDLE= -7;
        internal const int ZKFP_ERR_CAPTURE       = -8;
        internal const int ZKFP_ERR_EXTRACT_FP    = -9;
        internal const int ZKFP_ERR_ABORT         = -10;
        internal const int ZKFP_ERR_MEMORY        = -11;
        internal const int ZKFP_ERR_BUSY          = -12;
```

### Managed Helpers (passthrough)

```csharp
        internal static int Initialize() => ZKFPM_Init();
        internal static int Close() => ZKFPM_Terminate();      // double-call benign
        internal static int GetDeviceCount() => ZKFPM_GetDeviceCount();
        internal static int CloseDevice(IntPtr handle) => ZKFPM_CloseDevice(handle);

        internal static int AcquireFingerprint(
            IntPtr hDevice, IntPtr imagePtr, uint cbImage,
            IntPtr templatePtr, ref int cbTemplate)
            => ZKFPM_AcquireFingerprint(hDevice, imagePtr, cbImage, templatePtr, ref cbTemplate);
```

### `TryOpenDevice` — method quan trọng nhất (W5-leak fix)

```csharp
        /// <summary>
        /// Opens device at index, reads dims/dpi/serial/product.
        /// Returns false if any critical step fails.
        ///
        /// GUARD: rawHandle &gt; 0 (negative handles observed on ZK9500).
        /// W5-leak fix: CloseDevice on ANY intermediate failure before return false.
        /// Fail-open for serial/product: keep defaults if param 1103/1102 read fails.
        /// </summary>
        internal static bool TryOpenDevice(
            int index,
            out IntPtr handle,
            out int width,
            out int height,
            out int dpi,
            out string serialNumber,
            out string productName)
        {
            handle = IntPtr.Zero;
            width = 0; height = 0; dpi = 0;
            serialNumber = "ZKTeco-unknown";
            productName = "ZKTeco Device";

            // Step 1: Open — guard against zero AND negative handles
            int rawHandle = ZKFPM_OpenDevice(index);
            if (rawHandle <= 0)
                return false;
            handle = new IntPtr(rawHandle);

            try
            {
                // Step 2: Read width/height/dpi via GetParameters codes 1/2/3
                // (verified on ZK9500 via ZkSdkProbe.cs:98-110)
                byte[] buf4 = new byte[4];

                int sz = buf4.Length;
                if (ZKFPM_GetParameters(handle, 1, buf4, ref sz) == 0 && sz >= 4)
                    width = BitConverter.ToInt32(buf4, 0);

                sz = buf4.Length;
                if (ZKFPM_GetParameters(handle, 2, buf4, ref sz) == 0 && sz >= 4)
                    height = BitConverter.ToInt32(buf4, 0);

                sz = buf4.Length;
                if (ZKFPM_GetParameters(handle, 3, buf4, ref sz) == 0 && sz >= 4)
                    dpi = BitConverter.ToInt32(buf4, 0);

                if (width <= 0 || height <= 0)
                {
                    CloseDevice(handle);
                    handle = IntPtr.Zero;
                    return false;
                }

                // Step 3: Serial (1103) + product name (1102) — fail-open
                byte[] buf64 = new byte[64];
                sz = buf64.Length;
                if (ZKFPM_GetParameters(handle, 1103, buf64, ref sz) == 0 && sz > 0)
                    serialNumber = Encoding.UTF8.GetString(buf64, 0, sz)
                        .TrimEnd('\0').Replace("\0", "");

                sz = buf64.Length;
                if (ZKFPM_GetParameters(handle, 1102, buf64, ref sz) == 0 && sz > 0)
                    productName = Encoding.UTF8.GetString(buf64, 0, sz)
                        .TrimEnd('\0').Replace("\0", "");

                return true;
            }
            catch
            {
                // Any unexpected managed exception during param reads:
                // close handle to avoid leak, report failure
                CloseDevice(handle);
                handle = IntPtr.Zero;
                return false;
            }
        }
    }
}
```

**Lưu ý thiết kế:**
- KHÔNG tái tạo event `OnClosing` của wrapper — lifecycle điều khiển bằng `_hostLock` + `_captureInProgress` của adapter
- KHÔNG dùng SourceAFIS/GDI+ trong host class — PNG encode là `ToPngGrayscale` riêng của adapter
- Dims đọc qua `GetParameters` codes 1/2/3 (đã verify trên ZK9500), KHÔNG dùng `GetCaptureParamsEx` (tránh rủi ro marshal chưa verify). Nếu sau này cần switch, chỉ đổi trong TryOpenDevice.

### Accept T2

- `dotnet build FingerprintAgent.sln -c Release` → 0/0
- Không reference package nào mới
- XML doc trên mỗi member ghi rõ nguồn prior-art từng signature

### Commit T2

```
feat(05): add ZkNativeHost — raw libzkfp.dll P/Invoke layer
```

---

## T3 — Rewrite `ZKTecoAdapter.cs`

### File

| Action | File |
|---|---|
| MODIFY | `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs` (444 dòng hiện tại, ~60% rewrite) |

### Changes — theo thứ tự trong file

**1. Remove wrapper import** (line 11):

```csharp
// DELETE: using ZkTecoFingerPrint;
```

**2. Replace device field** (line 47):

```csharp
// BEFORE:
private ZkFingerPrintDevice? _device;

// AFTER:
private IntPtr _handle = IntPtr.Zero;
private const int TemplateBufferSize = 2048;   // F2: vendor demo + wrapper đều dùng 2048
```

**3. Update `IsConnected`** (line 88):

```csharp
public bool IsConnected => _isConnected && _handle != IntPtr.Zero;
```

**4. Rewrite `ProbeConnection`** (lines 110-122) — chỉ đổi 1 dòng:

```csharp
try { ZkNativeHost.Close(); } catch { /* best effort */ }   // thay ZkTecoFingerHost.Close()
```

Giữ nguyên: `_captureInProgress > 0` guard + `_vendorErrorCode = "PROBE_DEFERRED_CAPTURE_IN_FLIGHT"`.

**5. Rewrite `InitializeInternal`** (lines 132-202):

```csharp
private bool InitializeInternal()
{
    // Dispose prior device — SDK sensor state corrupts sau mỗi capture
    if (_handle != IntPtr.Zero)
    {
        try { ZkNativeHost.CloseDevice(_handle); } catch { }
        _handle = IntPtr.Zero;
        _isConnected = false;
    }

    // Init host với recovery cho abandoned session.
    // Catch DllNotFoundException/BadImageFormatException → DLL_NOT_FOUND
    // (khử 9 known-failures của baseline T0)
    int initResult;
    try
    {
        initResult = EnsureHostInitialized();
    }
    catch (DllNotFoundException)
    {
        _vendorErrorCode = "DLL_NOT_FOUND";
        return false;
    }
    catch (BadImageFormatException)   // x86/x64 mismatch
    {
        _vendorErrorCode = "DLL_NOT_FOUND";
        return false;
    }

    bool hostReady = initResult == ZkNativeHost.ZKFP_OK
                  || initResult == ZkNativeHost.ZKFP_ALREADY_INIT;   // F5
    if (!hostReady)
    {
        _vendorErrorCode = ErrorCodeToString(initResult);
        return false;
    }

    // SCAN-10 quirk: GetDeviceCount() có thể trả 0 ngay sau Init → retry 3×100ms
    int deviceCount = 0;
    try
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            deviceCount = ZkNativeHost.GetDeviceCount();
            if (deviceCount > 0) break;
            if (attempt < 2) Thread.Sleep(100);
        }
    }
    catch (DllNotFoundException)
    {
        _vendorErrorCode = "DLL_NOT_FOUND";
        return false;
    }
    catch (Exception ex)
    {
        _vendorErrorCode = $"{ex.GetType().Name}: {ex.Message}";
        return false;
    }

    if (deviceCount == 0)
    {
        _vendorErrorCode = ErrorCodeToString(ZkNativeHost.ZKFP_ERR_NO_DEVICE);
        return false;
    }

    // Open device — TryOpenDevice tự quản leak-safe (W5 fix)
    if (!ZkNativeHost.TryOpenDevice(0, out _handle, out _width, out _height,
            out _, out string serial, out string product))
    {
        _vendorErrorCode = ErrorCodeToString(ZkNativeHost.ZKFP_ERR_OPEN);
        return false;
    }

    // Lock-on-first-init identity — ZK SDK mutates Name sau AcquireFingerprint
    if (_deviceId == "ZKTeco-unknown" && !string.IsNullOrEmpty(serial))
        _deviceId = serial;
    if (_model == "ZKTeco Device" && !string.IsNullOrEmpty(product))
        _model = product;

    _isConnected = true;
    return true;
}
```

**6. Rewrite `EnsureHostInitialized`** (lines 395-422) — đổi return type từ `ZkResult<int>` sang `int`:

```csharp
private static int EnsureHostInitialized()
{
    lock (_hostLock)
    {
        int result = ZkNativeHost.Initialize();

        // AlreadyInit (=1): host đã usable — coi như success (F5)
        if (result == ZkNativeHost.ZKFP_OK || result == ZkNativeHost.ZKFP_ALREADY_INIT)
            return result;

        // InitLibrary (-1) / Init (-2): previous session abandoned,
        // native state corrupted. Close() rồi retry một lần.
        if (result == ZkNativeHost.ZKFP_ERR_INITLIB || result == ZkNativeHost.ZKFP_ERR_INIT)
        {
            try { ZkNativeHost.Close(); } catch { /* best effort */ }
            return ZkNativeHost.Initialize();
        }

        return result;
    }
}
```

**7. Add `AcquireOnce` method** (W2 fix — thay `device.AcquireFingerprintAsync`):

```csharp
/// <summary>
/// Single blocking acquire wrapped in Task.Run với try/finally đúng cho
/// AllocHGlobal cleanup (W2 fix — wrapper không free khi exception).
///
/// Marshal.Copy chạy BÊN TRONG try (trước FreeHGlobal) nên image data
/// được copy khi native pointer còn valid.
///
/// ct chỉ ngăn START (giống wrapper) — native block ~1s không abort được;
/// cancel thật xảy ra tại checkpoint retry kế tiếp (behavior giữ nguyên).
/// </summary>
private async Task<int> AcquireOnce(IntPtr handle, byte[] imageBuffer, CancellationToken ct)
{
    IntPtr imagePtr = Marshal.AllocHGlobal(imageBuffer.Length);
    IntPtr templatePtr = Marshal.AllocHGlobal(TemplateBufferSize);
    try
    {
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
    finally   // W2 fix: FreeHGlobal LUÔN chạy kể cả khi Task.Run ném
    {
        Marshal.FreeHGlobal(templatePtr);
        Marshal.FreeHGlobal(imagePtr);
    }
}
```

**8. Rewrite `ScanAsync`** (lines 204-306) — structure giữ nguyên, chỉ đổi các điểm sau:

```csharp
// Snapshot dưới lock — cùng pattern, IntPtr thay cho ZkFingerPrintDevice:
IntPtr handle;
int width, height;
lock (_hostLock)
{
    if (_handle == IntPtr.Zero || !_isConnected)   // ← đổi từ `_device == null`
    {
        _vendorErrorCode = "SCANNER_NOT_CONNECTED";
        return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "ZKTeco: scanner not initialized");
    }
    handle = _handle;
    width = _width;
    height = _height;
    _captureInProgress++;
}

// Trong rolling-capture loop — đổi kiểu lastResult và lời gọi:
var stopwatch = Stopwatch.StartNew();
int lastResult = ZkNativeHost.ZKFP_ERR_CAPTURE;

do
{
    if (cancellationToken.IsCancellationRequested) { /* giữ nguyên */ }

    lastResult = await AcquireOnce(handle, imageBuffer, cancellationToken);   // ← THAY đổi

    if (lastResult == ZkNativeHost.ZKFP_OK)
        break;

    await Task.Delay(retryDelayMs, cancellationToken);
} while (stopwatch.ElapsedMilliseconds < captureBudgetMs);

if (lastResult != ZkNativeHost.ZKFP_OK)
{
    int elapsedSec = (int)(stopwatch.ElapsedMilliseconds / 1000);
    _vendorErrorCode = ErrorCodeToString(lastResult);
    return CaptureResult.Fail("CAPTURE_FAILED", ErrorCodeToUserMessage(lastResult, elapsedSec));
}

// imageBuffer ĐÃ được populate bởi AcquireOnce (Marshal.Copy bên trong)
byte[] pngBytes = ToPngGrayscale(imageBuffer, width, height);

// ... SHA256 verificationData + return CaptureResult GIỮ NGUYÊN ...
```

Giữ nguyên: `_captureInProgress++/--` logic, budget 15s×100ms, catch-all Exception, finally decrement.

**9. Rewrite `Dispose`** (lines 424-442):

```csharp
public void Dispose()
{
    Exception disposalEx = null;
    if (_handle != IntPtr.Zero)
    {
        try { ZkNativeHost.CloseDevice(_handle); }
        catch (Exception ex) { disposalEx = ex; }
        _handle = IntPtr.Zero;
    }
    _isConnected = false;
    // NOTE: ZkNativeHost.Close() deliberately NOT called here — static teardown
    // terminates native context cho ALL instances. Chỉ gọi ở service shutdown.

    if (disposalEx != null)
        System.Diagnostics.Debug.WriteLine($"[ZKTecoAdapter] Disposal error: {disposalEx.Message}");
}
```

**10. Replace error dictionary** (lines 57-86) — keys từ `(int)ZkResponse.*` sang `ZkNativeHost.ZKFP_*`, values GIỮ NGUYÊN string:

```csharp
private static readonly System.Collections.Generic.Dictionary<int, string> _errorStrings =
    new System.Collections.Generic.Dictionary<int, string>
{
    [ZkNativeHost.ZKFP_OK]                 = "ERROR_NONE",
    [ZkNativeHost.ZKFP_ALREADY_INIT]       = "ERROR_ALREADY_INIT",
    [ZkNativeHost.ZKFP_ERR_INITLIB]        = "ERROR_INITLIB",
    [ZkNativeHost.ZKFP_ERR_INIT]           = "ERROR_INIT",
    [ZkNativeHost.ZKFP_ERR_NO_DEVICE]      = "ERROR_NO_DEVICE",
    [ZkNativeHost.ZKFP_ERR_NOT_SUPPORT]    = "ERROR_NOT_SUPPORT",
    [ZkNativeHost.ZKFP_ERR_INVALID_PARAM]  = "ERROR_INVALID_PARAM",
    [ZkNativeHost.ZKFP_ERR_OPEN]           = "ERROR_OPEN",
    [ZkNativeHost.ZKFP_ERR_INVALID_HANDLE] = "ERROR_INVALID_HANDLE",
    [ZkNativeHost.ZKFP_ERR_CAPTURE]        = "ERROR_CAPTURE",
    [ZkNativeHost.ZKFP_ERR_EXTRACT_FP]     = "ERROR_EXTRACT_FP",
    [ZkNativeHost.ZKFP_ERR_ABORT]          = "ERROR_ABORT",
    [ZkNativeHost.ZKFP_ERR_MEMORY]         = "ERROR_MEMORY_NOT_ENOUGH",
    [ZkNativeHost.ZKFP_ERR_BUSY]           = "ERROR_BUSY",
};
```

**11. Replace `ZkResponseToString`** (lines 340-346):

```csharp
private static string ErrorCodeToString(int errorCode)
{
    return _errorStrings.TryGetValue(errorCode, out string value)
        ? value
        : $"ERROR_UNKNOWN_{errorCode}";
}
```

**12. Replace `ZkResponseToUserMessage`** (lines 352-378):

```csharp
private static string ErrorCodeToUserMessage(int errorCode, int elapsedSec)
{
    switch (errorCode)
    {
        case ZkNativeHost.ZKFP_ERR_CAPTURE:
            return $"ZKTeco: no finger detected within {elapsedSec}s — please place finger on sensor and try again";
        case ZkNativeHost.ZKFP_ERR_BUSY:
            return "ZKTeco: scanner is busy with another operation — please retry in a moment";
        case ZkNativeHost.ZKFP_ERR_ABORT:
            return "ZKTeco: capture aborted by sensor or driver";
        case ZkNativeHost.ZKFP_ERR_TIMEOUT:      // nếu thêm constant -13 hoặc map từ Abort
            return $"ZKTeco: capture timed out after {elapsedSec}s — please retry";
        case ZkNativeHost.ZKFP_ERR_INVALID_HANDLE:
            return "ZKTeco: device handle invalidated — please retry, scanner will reinitialize";
        case ZkNativeHost.ZKFP_ERR_NO_DEVICE:
            return "ZKTeco: no scanner detected — check USB connection";
        case ZkNativeHost.ZKFP_ERR_OPEN:
            return "ZKTeco: scanner not opened — reinitializing, please retry";
        case ZkNativeHost.ZKFP_ERR_INVALID_PARAM:
            return "ZKTeco: invalid parameter passed to SDK — please report to IT support";
        case ZkNativeHost.ZKFP_ERR_ABORT_CANCEL: // Cancel — nếu không có constant riêng thì bỏ case này
            return "ZKTeco: capture cancelled";
        default:
            return $"ZKTeco: capture failed ({ErrorCodeToString(errorCode)})";
    }
}
```

Lưu ý: wrapper có `Timeout`/`Cancel` codes mà raw SDK có thể không trả riêng — giữ mapping gần nhất, message user-facing không đổi đáng kể.

**13. Grep verification:**

```powershell
Select-String -Path src\**\*.cs -Pattern "ZkTecoFingerPrint|ZkTecoFingerHost|ZkResponse|ZkResult|ZkDeviceResult|ZkFingerPrintDevice"
# Expected: 0 hits trong src/
```

### Accept T3

- Grep trên → **0 hits** trong `src/`
- `dotnet build FingerprintAgent.sln -c Release` → 0/0
- `dotnet test FingerprintAgent.sln -c Release` → **212/212** (9 DllNotFoundException failures giờ caught → pass-by-design)

### Commit T3

```
refactor(05): ZKTecoAdapter onto ZkNativeHost — drop wrapper (kills W1/W2)
```

---

## T4 — Call-sites + csproj Cleanup

### Files

| Action | File |
|---|---|
| MODIFY | `src/FingerprintAgent/Service/FingerprintAgentService.cs` |
| MODIFY | `src/FingerprintAgent.Host/Program.cs` |
| MODIFY | `src/FingerprintAgent/FingerprintAgent.csproj` |

### FingerprintAgentService.cs — 2 thay đổi

**Thay đổi 1** (line 12):

```csharp
// DELETE: using ZkTecoFingerPrint;
```

**Thay đổi 2** (line 193):

```csharp
// BEFORE:
try { ZkTecoFingerHost.Close(); } catch { /* best-effort */ }

// AFTER:
try { ZkNativeHost.Close(); } catch { /* best-effort — double-Close benign */ }
```

### Program.cs — 2 thay đổi

**Thay đổi 1** (line 8):

```csharp
// DELETE: using ZkTecoFingerPrint;
```

**Thay đổi 2** (line 43) — WRAP trong try/catch để fix luôn H3:

```csharp
// BEFORE:
ZkTecoFingerHost.Close();

// AFTER:
try { ZkNativeHost.Close(); } catch { /* best-effort — double-Close benign */ }
```

### FingerprintAgent.csproj — xóa PackageReference

Xóa dòng 74-75:

```xml
<!-- DELETE cả 2 dòng dưới đây -->
<PackageReference Include="ZkTecoFingerPrint" Version="1.2.1" />
<!-- REVIEW FIX (fallback): If ZkTecoFingerPrint NuGet is abandoned or compromised... -->
```

Giữ nguyên define-gating `ZKTECO_SDK_PRESENT` (native DLL vẫn cần lúc runtime).

### Verify bin output

Sau build Release, kiểm tra `src/FingerprintAgent.Host/bin/Release/net48/`:

```powershell
# Các file này KHÔNG ĐƯỢC tồn tại:
Test-Path src\FingerprintAgent.Host\bin\Release\net48\ZkTecoFingerPrint.dll   # False
Test-Path src\FingerprintAgent.Host\bin\Release\net48\SourceAFIS.dll          # False
Test-Path src\FingerprintAgent.Host\bin\Release\net48\System.Reactive.dll     # False
Test-Path src\FingerprintAgent.Host\bin\Release\net48\Dahomey.Cbor.dll        # False
```

### Grep toàn repo

```powershell
Select-String -Path (Get-ChildItem -Recurse -Include *.cs,*.csproj).FullName -Pattern "ZkTecoFingerPrint"
# Expected: chỉ còn trong docs (SCANNER_SETUP.md), plans, git history — KHÔNG còn trong code/csproj
```

### Accept T4

- `dotnet build FingerprintAgent.sln -c Release` → 0/0
- 4 DLLs trên không còn trong bin output (≈1.6MB nhẹ đi)
- Grep → chỉ còn docs/plans/history

### Commit T4

```
chore(05): remove ZkTecoFingerPrint package + rename host Close call sites
```

---

## T5 — Device Smoke Test (Manual, ZK9500 required)

### Checklist

| # | Test | Expected | Cách verify |
|---|---|---|---|
| 1 | `--console` start → `/health` | HTTP 200 healthy, `deviceId` = serial thật, `model` = product name thật. **Verify thêm: raw `ZKFPM_Init()` lần 2 trả AlreadyInit(=1)** qua log | `curl http://127.0.0.1:5043/health` |
| 2 | Capture 10 lần liên tiếp | Mỗi lần 200/PNG hợp lệ; latency GIẢM so với trước migrate (bỏ SourceAFIS ~100–300ms/capture); log `cbTemplate` thực nhận được lần đầu (confirm ≤2048) | `scripts\Test-Capture.ps1` ×10, so sánh timestamp request↔response trong log |
| 3 | Unplug USB giữa 2 capture | `SCANNER_NOT_CONNECTED` sạch (không crash), replug → tự phục hồi ≤30s | Rút USB khi service đang chạy, đợi, cắm lại |
| 4 | Shutdown khi capture đang treo (không đặt tay) | Service stop sạch ≤30s, không crash EventLog | Bắt đầu capture rồi `scripts\Service.ps1 stop` |
| 5 | 2 curl song song cùng lúc | 1 success + 1 xếp hàng (serialized bởi T1), không crash — chứng minh C3 đã fix | 2 PowerShell job chạy `Test-Capture.ps1` đồng thời |
| 6 | Ctrl+C console mode | Thoát sạch, không exception trên signal thread (H3 đã fix bởi try/catch ở Program.cs:43) | Ctrl+C trong console window |

### Kết quả ghi vào

`.planning/plans/zkteco-pinvoke-migration-plan.md` section §8:
- Ngày test, model firmware
- Kết quả từng mục pass/fail
- Độ trễ trung bình trước/sau migrate (từ log timestamps)

---

## Risk Mitigation Quick Reference

| Risk | Check point | Fallback |
|---|---|---|
| `ZKFPM_Init()` lần 2 ≠ AlreadyInit(=1) | T5-bước 1 | Coi `ret != ERR_INITLIB && != ERR_INIT` là usable (sửa 1 dòng trong EnsureHostInitialized) |
| `GetParameters` marshal sai | T5-bước 2 | Codes 1/2/3 đã verify qua ZkSdkProbe — nếu vẫn fail, switch sang `GetCaptureParamsEx` |
| StdCall/Cdecl sai → stack corrupt | Đã loại trừ | F2: mặc định Winapi, TUYỆT ĐỐI không đặt Cdecl |
| Template >2048 bytes | T5-bước 2 log cbTemplate | Tăng `TemplateBufferSize` lên 4096 |
| Negative handle chảy vào Acquire | T2 TryOpenDevice guard `> 0` | Đã xử lý sẵn |
| DllNotFoundException trong tests | T3 catch → DLL_NOT_FOUND | Tests chuyển pass-by-design → 212/212 |

---

## Rollback

Toàn bộ change nằm trong chuỗi commit `(05)` tuần tự trên 1 nhánh:

```powershell
git revert <T4-sha> <T3-sha> <T2-sha> <T1-sha>
# Hoặc đơn giản:
git checkout <commit-before-T1> -- src/ tests/
```

NuGet restore trả về trạng thái cũ tức thì. Không có data/schema migration nào.

---

## DONE Conditions

- [ ] T0 baseline đã ghi (203/212, 9 known-failures DllNotFoundException)
- [ ] T1 committed: SemaphoreSlim trong ScannerManager, concurrency tests pass, ≥203/212
- [ ] T2 committed: ZkNativeHost.cs tạo mới, build 0/0, XML doc đầy đủ prior-art
- [ ] T3 committed: ZKTecoAdapter rewritten, grep wrapper symbols trong src/ → 0 hits, tests **212/212**
- [ ] T4 committed: PackageReference removed, bin output sạch (4 DLL biến mất)
- [ ] T5: checklist 6/6 pass trên ZK9500 thật
- [ ] §8 của source plan được điền kết quả device test
- [ ] `dotnet build FingerprintAgent.sln -c Release` → 0 warning / 0 error
- [ ] `dotnet test FingerprintAgent.sln -c Release` → 212/212

---

## Thứ tự thực hiện & dependency

```
T0 (done) ──► T1 ──► T2 ──► T3 ──► T4 ──► T5
              │      │      │      │
              │      │      │      └─ phụ thuộc T3 (ZkNativeHost phải tồn tại)
              │      │      └─ phụ thuộc T2 (ZkNativeHost)
              │      └─ độc lập về logic nhưng commit tuần tự giữ rollback chain sạch
              └─ độc lập hoàn toàn với migrate — giá trị riêng dù migrate hoãn
```

**Lưu ý:** T1–T4 làm SEQUENTIAL theo thứ tự (mỗi task một commit, không nhảy cóc). T5 là manual test cuối cùng, chỉ chạy sau khi T4 build xanh.

---

*Plan generated 2026-08-22 từ review của `.planning/reviews/release-crash-review.md` Phụ lục A/C + source plan `.planning/plans/zkteco-pinvoke-migration-plan.md`.*
