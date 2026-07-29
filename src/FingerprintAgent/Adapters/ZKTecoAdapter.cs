#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
    /// AcquireFingerprintAsync is wrapped with CancellationToken for a safety-net timeout;
    /// the real budget (~3s per adapter, 10s total) is enforced by ScannerManager (D-06).
    /// </summary>
    public sealed class ZKTecoAdapter : IScannerAdapter, IDisposable
    {
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

        public bool Initialize()
        {
            // ZkTecoFingerHost.Initialize() is static — returns ZkResult<int>
            var initResult = ZkTecoFingerHost.Initialize();
            if (!initResult.IsSuccess)
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
            _deviceId = !string.IsNullOrEmpty(_device!.SerialNumber) ? _device.SerialNumber : _deviceId;
            _model = !string.IsNullOrEmpty(_device!.Name) ? _device.Name : _model;
            _isConnected = true;
            return true;
        }

        public CaptureResult Scan()
        {
            if (_device == null || !_isConnected)
            {
                _vendorErrorCode = "SCANNER_NOT_CONNECTED";
                return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "ZKTeco: not initialized");
            }

            // Safety-net 5s timeout inside the adapter — real budget (10s total, ~3s per
            // adapter) is enforced by ScannerManager per D-06 and D-11.
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    // AcquireFingerprintAsync wraps the blocking native call in Task.Run.
                    // Blocking on .Result here is safe — no thread-pool deadlock risk in the
                    // Windows Service hosting context (per SCAN-09 async/sync mismatch review fix).
                    var captureResult = _device.AcquireFingerprintAsync(cts.Token).Result;

                    if (!captureResult.IsSuccess)
                    {
                        _vendorErrorCode = ZkResponseToString(captureResult.Response);
                        return CaptureResult.Fail("CAPTURE_FAILED",
                            $"ZKTeco: capture failed ({ZkResponseToString(captureResult.Response)})");
                    }

                    // ZkFingerPrintResult.Bitmap is already BMP bytes from BitmapFormat.GetBitmap().
                    // Convert BMP -> PNG via GDI+. NO pixel inversion — ZKTeco grayscale is
                    // conventional (0=white, 255=dark ridges) per D-10.
                    byte[] pngBytes;
                    byte[] bmpBytes = captureResult.Value!.Bitmap;
                    using (var ms = new MemoryStream(bmpBytes))
                    using (var bmp = new Bitmap(ms))
                    using (var outMs = new MemoryStream())
                    {
                        bmp.Save(outMs, ImageFormat.Png);
                        pngBytes = outMs.ToArray();
                    }

                    // SHA-256 verification data
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
                        Width = _width,
                        Height = _height
                    };
                }
                catch (AggregateException ae)
                {
                    ae.Handle(ex =>
                    {
                        if (ex is OperationCanceledException || ex is TaskCanceledException)
                        {
                            _vendorErrorCode = "CAPTURE_TIMEOUT";
                            return true;
                        }
                        return false;
                    });
                    return CaptureResult.Fail("CAPTURE_TIMEOUT",
                        "ZKTeco: capture timeout — no finger detected within safety-net deadline");
                }
                catch (Exception ex)
                {
                    _vendorErrorCode = ex.Message;
                    return CaptureResult.Fail("CAPTURE_FAILED", $"ZKTeco: {ex.Message}");
                }
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
            // ZkTecoFingerHost.Close() is a static teardown — call at service shutdown.
            // The native library is reference-counted so it is safe to call multiple times.
            try { ZkTecoFingerHost.Close(); }
            catch (Exception ex) { disposalEx ??= ex; }

            if (disposalEx != null)
                System.Diagnostics.Debug.WriteLine($"[ZKTecoAdapter] Disposal error: {disposalEx.Message}");
        }
    }
}