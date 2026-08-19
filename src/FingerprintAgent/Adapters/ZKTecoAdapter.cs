#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZkTecoFingerPrint;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// ZKTeco fingerprint scanner adapter using ZkTecoFingerPrint NuGet (v1.2.1).
    /// Handles GetDeviceCount()=0 quirk with retry/delay pattern (SCAN-10 / D-11).
    /// Returns conventional grayscale PNG bytes — NO pixel inversion (D-10).
    ///
    /// Capture uses the buffer-overload of <c>ZkFingerPrintDevice.AcquireFingerprintAsync(byte[], CancellationToken)</c>.
    /// The parameterless overload queries ZK parameter 106 to size the image buffer;
    /// parameter 106 is unimplemented on ZK9500 (ZK SDK 5.3 / ZK10.0 firmware) and
    /// returns ZKFP_ERR_CAPTURE (-8) immediately, which the wrapper surfaces as a
    /// capture failure without ever calling the blocking capture. The buffer-overload
    /// skips that query and writes directly into the caller-supplied buffer.
    ///
    /// Rolling-capture: the wrapper's blocking call has an internal timeout (~1s on
    /// ZK9500). We retry on Capture errors only while elapsed time is below the
    /// 8-second adapter budget (under ScannerManager's 10s total, D-06). The user
    /// needs time to click button → reach for scanner → place finger.
    /// </summary>
    public sealed class ZKTecoAdapter : IScannerAdapter, IDisposable
    {
        // Guards concurrent calls to EnsureHostInitialized(). The native ZKTeco host
        // is a process-wide singleton — repeated Initialize() calls after a failed or
        // abandoned session leave the native state inconsistent and return ERROR_INITLIB.
        // We must Close() before re-Initialize() to recover.
        private static readonly object _hostLock = new object();

        private ZkFingerPrintDevice? _device;
        private int _width;
        private int _height;
        private string _deviceId = "ZKTeco-unknown";
        private string _model = "ZKTeco Device";
        private string _vendorErrorCode = "NONE";
        private bool _isConnected;

        // Maps ZkResponse enum values to human-readable strings matching ZKFP_ERR_* constants.
        // Dictionary lookup handles gaps in the enum (-15, -16, -19, -21 are not defined).
        private static readonly System.Collections.Generic.Dictionary<int, string> _zkResponseStrings =
            new System.Collections.Generic.Dictionary<int, string>
        {
            [(int)ZkResponse.Ok]                = "ERROR_NONE",
            [(int)ZkResponse.AlreadyInit]       = "ERROR_ALREADY_INIT",
            [(int)ZkResponse.InitLibrary]       = "ERROR_INITLIB",
            [(int)ZkResponse.Init]              = "ERROR_INIT",
            [(int)ZkResponse.NoDevice]          = "ERROR_NO_DEVICE",
            [(int)ZkResponse.NotSupported]      = "ERROR_NOT_SUPPORT",
            [(int)ZkResponse.InvalidParameter]  = "ERROR_INVALID_PARAM",
            [(int)ZkResponse.Open]              = "ERROR_OPEN",
            [(int)ZkResponse.InvalidHandle]     = "ERROR_INVALID_HANDLE",
            [(int)ZkResponse.Capture]           = "ERROR_CAPTURE",
            [(int)ZkResponse.ExtractFingerPrint]= "ERROR_EXTRACT_FP",
            [(int)ZkResponse.Abort]             = "ERROR_ABORT",
            [(int)ZkResponse.NotEnoughMemory]   = "ERROR_MEMORY_NOT_ENOUGH",
            [(int)ZkResponse.Busy]              = "ERROR_BUSY",
            [(int)ZkResponse.AddFinger]         = "ERROR_ADD_FINGER",
            [(int)ZkResponse.DeleteFinger]      = "ERROR_DELETE_FINGER",
            [(int)ZkResponse.Fail]              = "ERROR_FAIL",
            [(int)ZkResponse.Cancel]            = "ERROR_CANCEL",
            [(int)ZkResponse.VerifyFingerPrint] = "ERROR_VERIFY_FP",
            [(int)ZkResponse.Merge]             = "ERROR_MERGE",
            [(int)ZkResponse.NotOpened]         = "ERROR_NOT_OPENED",
            [(int)ZkResponse.NotInit]           = "ERROR_NOT_INIT",
            [(int)ZkResponse.AlreadyOpened]     = "ERROR_ALREADY_OPENED",
            [(int)ZkResponse.LoadImage]         = "ERROR_LOAD_IMAGE",
            [(int)ZkResponse.AnalyzeImage]      = "ERROR_ANALYZE_IMAGE",
            [(int)ZkResponse.Timeout]           = "ERROR_TIMEOUT"
        };

        public bool IsConnected => _isConnected && _device != null;

        public string DeviceId => _deviceId;

        public string Model => _model;

        public string MimeType => "image/png";

        public string VendorErrorCode => _vendorErrorCode ?? "NONE";

        /// <summary>
        /// Real-time connection check. ZkTecoFingerHost caches device info after unplug,
        /// so a lightweight GetDeviceCount() check is unreliable for detecting device
        /// removal. Forces Close + re-Initialize to re-enumerate USB devices (~50-200ms).
        /// Locked via _hostLock to prevent race with concurrent Scan().
        /// </summary>
        public bool ProbeConnection()
        {
            lock (_hostLock)
            {
                try { ZkTecoFingerHost.Close(); } catch { /* best effort */ }
                return InitializeInternal();
            }
        }

        public bool Initialize()
        {
            lock (_hostLock)
            {
                return InitializeInternal();
            }
        }

        private bool InitializeInternal()
        {
            // Dispose prior device — SDK sensor state corrupts after each capture,
            // subsequent AcquireFingerprint returns ERROR_CAPTURE in ~70ms instead of 2s.
            if (_device != null)
            {
                try { _device.Dispose(); } catch { }
                _device = null;
                _isConnected = false;
            }

            // ZkTecoFingerHost is a process-wide singleton. Calling Initiaize() when
            // the previous session was abandoned (e.g. capture failed mid-flight) leaves
            // the native context in a bad state and returns ERROR_INITLIB on retry.
            // Recovery: Close() then re-Initialize().
            var initResult = EnsureHostInitialized();

            // AlreadyInit (1) means the host is already usable — IsSuccess is false
            // (only Ok=0 is "success") but the host state is what we want. Treat as OK.
            bool hostReady = initResult.IsSuccess || initResult.Response == ZkResponse.AlreadyInit;
            if (!hostReady)
            {
                _vendorErrorCode = ZkResponseToString(initResult.Response);
                return false;
            }

            // SCAN-10 quirk: GetDeviceCount() may return 0 immediately after Init()
            // on some driver versions — retry up to 3 times with 100ms delay.
            int deviceCount = 0;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    deviceCount = ZkTecoFingerHost.GetDeviceCount();
                    if (deviceCount > 0)
                        break;
                    if (attempt < 2)
                        Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                _vendorErrorCode = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }

            if (deviceCount == 0)
            {
                _vendorErrorCode = ZkResponseToString(ZkResponse.NoDevice);
                return false;
            }

            // OpenDevice(0) is static — returns ZkDeviceResult with IsSuccess + Value
            var deviceResult = ZkTecoFingerHost.OpenDevice(0);
            if (!deviceResult.IsSuccess)
            {
                _vendorErrorCode = ZkResponseToString(deviceResult.Response);
                return false;
            }

            _device = deviceResult.Value;
            _width = _device!.Width;
            _height = _device!.Height;
            // Lock device identity on first Initialize — ZK SDK's _device.Name mutates after AcquireFingerprint
            if (_deviceId == "ZKTeco-unknown" && !string.IsNullOrEmpty(_device!.SerialNumber))
                _deviceId = _device.SerialNumber;
            if (_model == "ZKTeco Device" && !string.IsNullOrEmpty(_device!.Name))
                _model = _device.Name;
            _isConnected = true;
            return true;
        }

        public async Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            // Snapshot device handle under lock so ProbeConnection's Close+Initialize
            // (also under lock) can't dispose the handle mid-capture. Long
            // AcquireFingerprintAsync runs outside lock to avoid blocking /health for
            // up to 15s during a capture.
            ZkFingerPrintDevice device;
            int width, height;
            lock (_hostLock)
            {
                if (_device == null || !_isConnected)
                {
                    _vendorErrorCode = "SCANNER_NOT_CONNECTED";
                    return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "ZKTeco: scanner not initialized");
                }
                device = _device;
                width = _width;
                height = _height;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _vendorErrorCode = "CANCELLED";
                return CaptureResult.Fail("CAPTURE_TIMEOUT", "ZKTeco: capture cancelled before start");
            }

            try
            {
                if (width <= 0 || height <= 0)
                {
                    _vendorErrorCode = "INVALID_DIMENSIONS";
                    return CaptureResult.Fail("CAPTURE_FAILED",
                        $"ZKTeco: invalid sensor dimensions {width}x{height}");
                }

                byte[] imageBuffer = new byte[width * height];

                const int captureBudgetMs = 15000;
                const int retryDelayMs = 100;
                var stopwatch = Stopwatch.StartNew();
                ZkResult<ZkFingerPrintResult?>? lastResult = null;

                do
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _vendorErrorCode = "CANCELLED";
                        return CaptureResult.Fail("CAPTURE_TIMEOUT", "ZKTeco: capture cancelled by timeout");
                    }

                    lastResult = await device.AcquireFingerprintAsync(imageBuffer, cancellationToken);
                    if (lastResult.IsSuccess)
                        break;
                    await Task.Delay(retryDelayMs, cancellationToken);
                } while (stopwatch.ElapsedMilliseconds < captureBudgetMs);

                if (lastResult == null || !lastResult.IsSuccess)
                {
                    int elapsedSec = (int)(stopwatch.ElapsedMilliseconds / 1000);
                    ZkResponse failedResponse = lastResult?.Response ?? ZkResponse.Capture;
                    _vendorErrorCode = ZkResponseToString(failedResponse);
                    return CaptureResult.Fail("CAPTURE_FAILED", ZkResponseToUserMessage(failedResponse, elapsedSec));
                }

                byte[] pngBytes = ToPngGrayscale(imageBuffer, width, height);

                string verificationData;
                using (var sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(pngBytes);
                    verificationData = Convert.ToBase64String(hash);
                }

                return new CaptureResult
                {
                    IsSuccess = true,
                    ImageBytes = pngBytes,
                    MimeType = "image/png",
                    CapturedAt = DateTime.UtcNow.ToString("O"),
                    DeviceId = _deviceId,
                    VerificationData = verificationData,
                    ErrorMessage = null,
                    Width = width,
                    Height = height
                };
            }
            catch (Exception ex)
            {
                _vendorErrorCode = $"{ex.GetType().Name}: {ex.Message}";
                return CaptureResult.Fail("CAPTURE_FAILED",
                    $"ZKTeco: capture failed ({ex.GetType().Name}) — please retry");
            }
        }

        private static byte[] ToPngGrayscale(byte[] rawPixels, int width, int height)
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

                int stride = bitmapData.Stride;
                for (int row = 0; row < height; row++)
                {
                    Marshal.Copy(rawPixels, row * width, bitmapData.Scan0 + row * stride, width);
                }
                bitmap.UnlockBits(bitmapData);

                using (var pngStream = new MemoryStream())
                {
                    bitmap.Save(pngStream, ImageFormat.Png);
                    return pngStream.ToArray();
                }
            }
        }

        /// <summary>
        /// Converts a ZkResponse enum value to a human-readable string for VendorErrorCode.
        /// </summary>
        private static string ZkResponseToString(ZkResponse response)
        {
            int key = (int)response;
            return _zkResponseStrings.TryGetValue(key, out string value)
                ? value
                : $"ERROR_UNKNOWN_{key}";
        }

        /// <summary>
        /// Maps ZkResponse to a user-actionable error message for CaptureResult.ErrorMessage.
        /// Vendor-specific error string (ZkResponseToString) stays in VendorErrorCode.
        /// </summary>
        private static string ZkResponseToUserMessage(ZkResponse response, int elapsedSec)
        {
            switch (response)
            {
                case ZkResponse.Capture:
                    return $"ZKTeco: no finger detected within {elapsedSec}s — please place finger on sensor and try again";
                case ZkResponse.Busy:
                    return "ZKTeco: scanner is busy with another operation — please retry in a moment";
                case ZkResponse.Abort:
                    return "ZKTeco: capture aborted by sensor or driver";
                case ZkResponse.Timeout:
                    return $"ZKTeco: capture timed out after {elapsedSec}s — please retry";
                case ZkResponse.InvalidHandle:
                    return "ZKTeco: device handle invalidated — please retry, scanner will reinitialize";
                case ZkResponse.NoDevice:
                    return "ZKTeco: no scanner detected — check USB connection";
                case ZkResponse.NotOpened:
                    return "ZKTeco: scanner not opened — reinitializing, please retry";
                case ZkResponse.InvalidParameter:
                    return "ZKTeco: invalid parameter passed to SDK — please report to IT support";
                case ZkResponse.Cancel:
                    return "ZKTeco: capture cancelled";
                case ZkResponse.NotEnoughMemory:
                    return "ZKTeco: scanner memory insufficient — please retry";
                default:
                    return $"ZKTeco: capture failed ({ZkResponseToString(response)})";
            }
        }

        /// <summary>
        /// Calls ZkTecoFingerHost.Initialize() with recovery for the well-known case
        /// where a previous session was abandoned mid-flight (capture failed before
        /// device was closed), leaving the native state corrupted.
        ///
        /// The ZKTeco SDK has a confusing response model:
        ///   - First call: returns Ok (0) on success
        ///   - Second call when host is already initialized: returns AlreadyInit (1)
        ///     — IsSuccess=false but the host IS initialized, no action needed
        ///   - After a failed/abandoned session: returns InitLibrary (-1) or Init (-2)
        ///     — requires Close() + retry to recover
        ///
        /// We treat AlreadyInit as a success because the host is in the state we want.
        /// </summary>
        private static ZkResult<int> EnsureHostInitialized()
        {
            lock (_hostLock)
            {
                var result = ZkTecoFingerHost.Initialize();

                // AlreadyInit means the host is already in a usable state.
                if (result.IsSuccess || result.Response == ZkResponse.AlreadyInit)
                {
                    return result;
                }

                // InitLibrary (-1) or Init (-2): previous session was abandoned,
                // native state is corrupted. Close() then retry once.
                if (result.Response == ZkResponse.InitLibrary || result.Response == ZkResponse.Init)
                {
                    try { ZkTecoFingerHost.Close(); } catch { /* best effort */ }
                    var retry = ZkTecoFingerHost.Initialize();
                    if (retry.IsSuccess || retry.Response == ZkResponse.AlreadyInit)
                    {
                        return retry;
                    }
                    return retry;
                }

                return result;
            }
        }

        public void Dispose()
        {
            Exception? disposalEx = null;
            if (_device != null)
            {
                try { _device?.Dispose(); }
                catch (Exception ex) { disposalEx = ex; }
                _device = null;
            }
            _isConnected = false;
            // NOTE: ZkTecoFingerHost.Close() is deliberately NOT called here — it is a static
            // teardown that terminates the native context for ALL instances. Calling it from
            // an individual adapter's Dispose() would break the multi-instance pattern when
            // ScannerManager iterates through adapters. The host should be closed at service/
            // application shutdown only (see ScannerManager.Dispose() or Program.cs cleanup).

            if (disposalEx != null)
                System.Diagnostics.Debug.WriteLine($"[ZKTecoAdapter] Disposal error: {disposalEx.Message}");
        }
    }
}