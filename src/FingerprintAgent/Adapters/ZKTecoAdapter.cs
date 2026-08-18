#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
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

        // Maps ZkResponse enum values to human-readable strings matching ZKFP_ERR_* constants
        private static readonly string[] _zkResponseStrings = new string[]
        {
            "ERROR_NONE",              //  0  Ok
            "ERROR_INITLIB",           // -1  InitLibrary
            "ERROR_INIT",              // -2  Init
            "ERROR_NO_DEVICE",         // -3  NoDevice
            "ERROR_NOT_SUPPORT",       // -4  NotSupported
            "ERROR_INVALID_PARAM",     // -5  InvalidParameter
            "ERROR_OPEN",              // -6  Open
            "ERROR_INVALID_HANDLE",    // -7  InvalidHandle
            "ERROR_CAPTURE",           // -8  Capture
            "ERROR_EXTRACT_FP",        // -9  ExtractFingerPrint
            "ERROR_ABORT",             // -10 Abort
            "ERROR_MEMORY_NOT_ENOUGH", // -11 NotEnoughMemory
            "ERROR_BUSY"               // -12 Busy
            // Note: enum values beyond -12 exist (AddFinger=-13, DeleteFinger=-14, etc.)
            // but are not covered by the classic ZKFP_ERR_* 0/-1 to -12 range.
        };

        public bool IsConnected => _isConnected && _device != null;

        public string DeviceId => _deviceId;

        public string Model => _model;

        public string MimeType => "image/png";

        public string VendorErrorCode => _vendorErrorCode ?? "NONE";

        /// <summary>
        /// Lightweight real-time connection check. Re-queries ZkTecoFingerHost.GetDeviceCount()
        /// to verify the device is still attached without full re-initialization. Takes ~1ms
        /// (no device open/close). Updates _isConnected to false if the device was unplugged
        /// since the last Initialize(). Without this, /health would report stale "healthy"
        /// state until the next /api/capture triggered a full re-init.
        /// </summary>
        public bool ProbeConnection()
        {
            if (_device == null)
                return false;

            int count = ZkTecoFingerHost.GetDeviceCount();
            if (count <= 0)
            {
                _isConnected = false;
                _vendorErrorCode = ZkResponseToString(ZkResponse.NoDevice);
                return false;
            }
            return _isConnected;
        }

        public bool Initialize()
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
            // on some driver versions — retry up to 3 times with 100ms delay
            int deviceCount = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                deviceCount = ZkTecoFingerHost.GetDeviceCount();
                if (deviceCount > 0)
                    break;
                if (attempt < 2)
                    Thread.Sleep(100);
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

        public CaptureResult Scan()
        {
            if (_device == null || !_isConnected)
            {
                _vendorErrorCode = "SCANNER_NOT_CONNECTED";
                return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "ZKTeco: scanner not initialized");
            }

            try
            {
                int width = _device.Width;
                int height = _device.Height;
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
                    lastResult = _device.AcquireFingerprintAsync(imageBuffer, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (lastResult.IsSuccess)
                        break;
                    Thread.Sleep(retryDelayMs);
                } while (stopwatch.ElapsedMilliseconds < captureBudgetMs);

                if (lastResult == null || !lastResult.IsSuccess)
                {
                    int elapsedSec = (int)(stopwatch.ElapsedMilliseconds / 1000);
                    _vendorErrorCode = ZkResponseToString(lastResult?.Response ?? ZkResponse.Capture);
                    return CaptureResult.Fail("CAPTURE_FAILED",
                        $"ZKTeco: no finger detected within {elapsedSec}s");
                }

                var captureResult = lastResult.Value!;

                byte[] pngBytes;
                using (var ms = new MemoryStream(captureResult.Bitmap))
                using (var bmp = new Bitmap(ms))
                using (var pngStream = new MemoryStream())
                {
                    bmp.Save(pngStream, ImageFormat.Png);
                    pngBytes = pngStream.ToArray();
                }

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
                _vendorErrorCode = ex.Message;
                return CaptureResult.Fail("CAPTURE_FAILED", $"ZKTeco: {ex.Message}");
            }
        }

        /// <summary>
        /// Converts a ZkResponse enum value to a human-readable string.
        /// ZkResponse values are negative integers matching ZKFP_ERR_* from zkfp2.h.
        /// Value 0 (Ok) maps to "ERROR_NONE" for consistency with other adapters.
        /// </summary>
        private static string ZkResponseToString(ZkResponse response)
        {
            int index = (int)response;
            if (index == 0)
                return "ERROR_NONE"; // Ok
            int arrayIndex = Math.Abs(index);
            if (arrayIndex < _zkResponseStrings.Length)
                return _zkResponseStrings[arrayIndex];
            // For enum values beyond our coverage, return raw enum name
            return response.ToString();
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