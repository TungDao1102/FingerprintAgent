using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Digital Persona U.are.U scanner adapter — raw P/Invoke against dpfpdd.dll
    /// via DpNativeHost. Replaces the DPUruNet/DPFPDevNET managed-wrapper path.
    ///
    /// Runtime model = ZKTeco adapter: the code compiles unconditionally and
    /// dpfpdd.dll is resolved at request time from the U.are.U driver installed on
    /// the user's machine — the MSI ships no vendor DLLs. A missing driver surfaces
    /// as DllNotFoundException → DRIVER_NOT_INSTALLED.
    ///
    /// dpfpdd_init()/dpfpdd_exit() are process-wide: init happens once (lazy, guarded)
    /// and exit is never called from here — same policy as ZkNativeHost.Close().
    /// </summary>
    public class DigitalPersonaAdapter : IScannerAdapter, IDisposable
    {
        private static readonly object _initLock = new object();
        private static bool _nativeInitialized;

        private readonly object _lock = new object();
        private IntPtr _handle;
        private string _deviceId;
        private string _model;
        private string _vendorErrorCode;
        private bool _isConnected;

        // Generous single allocation: any U.are.U reader at 500 dpi produces ≈0.25 MB
        // (500×500×1B); 2 MB also covers 1000 dpi modes, so a single dpfpdd_capture
        // call succeeds without needing the E_MORE_DATA size-probe pass.
        private const int CaptureImageBufferBytes = 2 * 1024 * 1024;
        // User reach budget: click button → reach for scanner → place finger
        // (mirrors ZKTecoAdapter's 22s budget; ScannerManager enforces the 25s total).
        private const uint CaptureTimeoutMs = 20000;

        public bool IsConnected
        {
            get { lock (_lock) return _isConnected; }
        }

        public string DeviceId => _deviceId ?? "no-device";

        public string Model => _model ?? "no-device";

        public string MimeType => "image/png";

        public string VendorErrorCode => _vendorErrorCode ?? "NONE";

        public bool ProbeConnection() => IsConnected;

        public bool Initialize()
        {
            lock (_lock)
            {
                _vendorErrorCode = "NONE";
                if (_isConnected && _handle != IntPtr.Zero)
                    return true;

                CloseHandleLocked(); // drop a stale handle from a previous Initialize

                try
                {
                    if (!EnsureNativeInitialized())
                    {
                        _vendorErrorCode = "DEVICE_FAILURE";
                        _isConnected = false;
                        return false;
                    }
                }
                catch (DllNotFoundException)
                {
                    _vendorErrorCode = "DLL_NOT_FOUND";
                    _isConnected = false;
                    return false;
                }
                catch (BadImageFormatException)
                {
                    _vendorErrorCode = "DLL_NOT_FOUND";
                    _isConnected = false;
                    return false;
                }

                IntPtr handle;
                string deviceId;
                string model;
                try
                {
                    if (!DpNativeHost.TryOpenFirstDevice(out handle, out deviceId, out model))
                    {
                        _vendorErrorCode = "DEVICE_NOT_FOUND";
                        _isConnected = false;
                        return false;
                    }
                }
                catch (DllNotFoundException)
                {
                    _vendorErrorCode = "DLL_NOT_FOUND";
                    _isConnected = false;
                    return false;
                }
                catch (BadImageFormatException)
                {
                    _vendorErrorCode = "DLL_NOT_FOUND";
                    _isConnected = false;
                    return false;
                }

                _handle = handle;
                _deviceId = deviceId;
                _model = model;
                _isConnected = true;
                return true;
            }
        }

        public async Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            IntPtr handle;
            lock (_lock)
            {
                if (_handle == IntPtr.Zero || !_isConnected)
                {
                    // The DLL_NOT_FOUND latch means the driver is missing — a different
                    // fix (install driver) than replugging the scanner.
                    if (_vendorErrorCode == "DLL_NOT_FOUND")
                    {
                        return CaptureResult.Fail("DRIVER_NOT_INSTALLED",
                            "DigitalPersona: dpfpdd.dll could not be loaded — install the U.are.U driver");
                    }
                    _vendorErrorCode = "NOT_INITIALIZED";
                    return CaptureResult.Fail("SCANNER_NOT_CONNECTED",
                        "DigitalPersona: scanner not initialized. Call Initialize() first.");
                }
                handle = _handle;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _vendorErrorCode = "CANCELLED";
                return CaptureResult.Fail("CAPTURE_TIMEOUT", "DigitalPersona: capture cancelled before start");
            }

            IntPtr imagePtr = Marshal.AllocHGlobal(CaptureImageBufferBytes);
            try
            {
                DpNativeHost.DpCaptureResult result = DpNativeHost.NewCaptureResult();
                uint imageSize = (uint)CaptureImageBufferBytes;

                int rc;
                using (cancellationToken.Register(() =>
                {
                    try { DpNativeHost.Cancel(handle); }
                    catch (DllNotFoundException) { }
                    catch (Exception) { }
                }))
                {
                    // Native capture blocks up to CaptureTimeoutMs; cancellation is
                    // delivered via dpfpdd_cancel from the registration above.
                    rc = await Task.Run(
                        () => DpNativeHost.Capture(handle, CaptureTimeoutMs, ref result, imagePtr, (uint)CaptureImageBufferBytes),
                        cancellationToken).ConfigureAwait(false);
                }

                if (rc != DpNativeHost.DpfpddSuccess)
                {
                    _vendorErrorCode = MapNativeError(rc);
                    return CaptureResult.Fail(
                        ToStandardErrorCode(_vendorErrorCode, "CAPTURE_FAILED"),
                        $"DigitalPersona:{_vendorErrorCode}");
                }

                if (result.Success == 0)
                {
                    if ((result.Quality & DpNativeHost.QualityTimedOut) != 0)
                    {
                        _vendorErrorCode = "CAPTURE_TIMEOUT";
                        return CaptureResult.Fail("CAPTURE_TIMEOUT",
                            "DigitalPersona: no finger placed within the capture timeout");
                    }
                    if ((result.Quality & DpNativeHost.QualityCanceled) != 0
                        || cancellationToken.IsCancellationRequested)
                    {
                        _vendorErrorCode = "CANCELLED";
                        return CaptureResult.Fail("CAPTURE_TIMEOUT", "DigitalPersona: capture cancelled");
                    }
                    _vendorErrorCode = MapQuality(result.Quality);
                    return CaptureResult.Fail(
                        ToStandardErrorCode(_vendorErrorCode, "CAPTURE_FAILED"),
                        $"DigitalPersona:{_vendorErrorCode}");
                }

                if (result.Info.Bpp != 8 || result.Info.Width == 0 || result.Info.Height == 0)
                {
                    _vendorErrorCode = "UNSUPPORTED_IMAGE";
                    return CaptureResult.Fail("CAPTURE_FAILED",
                        $"DigitalPersona: unexpected image format {result.Info.Width}x{result.Info.Height} @ {result.Info.Bpp}bpp");
                }

                int width = (int)result.Info.Width;
                int height = (int)result.Info.Height;
                int pixelCount = width * height;
                if (imageSize < (uint)pixelCount)
                {
                    _vendorErrorCode = "TRUNCATED_IMAGE";
                    return CaptureResult.Fail("CAPTURE_FAILED",
                        $"DigitalPersona: image truncated ({imageSize} < {pixelCount} bytes)");
                }

                var rawPixels = new byte[pixelCount];
                Marshal.Copy(imagePtr, rawPixels, 0, pixelCount);
                byte[] png = PngEncoder.ToPngGrayscale(rawPixels, width, height);

                string verificationData;
                using (var sha256 = SHA256.Create())
                {
                    verificationData = Convert.ToBase64String(sha256.ComputeHash(png));
                }

                return new CaptureResult
                {
                    IsSuccess = true,
                    ImageBytes = png,
                    MimeType = "image/png",
                    CapturedAt = DateTime.UtcNow.ToString("O"),
                    DeviceId = DeviceId,
                    VerificationData = verificationData,
                    ErrorMessage = null,
                    Width = width,
                    Height = height
                };
            }
            catch (DllNotFoundException)
            {
                _vendorErrorCode = "DLL_NOT_FOUND";
                return CaptureResult.Fail("DRIVER_NOT_INSTALLED",
                    "DigitalPersona: dpfpdd.dll could not be loaded — install the U.are.U driver");
            }
            catch (BadImageFormatException)
            {
                _vendorErrorCode = "DLL_NOT_FOUND";
                return CaptureResult.Fail("DRIVER_NOT_INSTALLED",
                    "DigitalPersona: dpfpdd.dll architecture mismatch (x86/x64) — reinstall the U.are.U driver");
            }
            catch (OperationCanceledException)
            {
                _vendorErrorCode = "CANCELLED";
                return CaptureResult.Fail("CAPTURE_TIMEOUT", "DigitalPersona: capture cancelled");
            }
            catch (Exception ex)
            {
                _vendorErrorCode = $"{ex.GetType().Name}: {ex.Message}";
                return CaptureResult.Fail("CAPTURE_FAILED",
                    $"DigitalPersona: capture failed ({ex.GetType().Name}) — please retry");
            }
            finally
            {
                Marshal.FreeHGlobal(imagePtr);
            }
        }

        /// <summary>
        /// Lazily initializes the process-wide native library (dpfpdd_init must precede
        /// every other dpfpdd call). DllNotFoundException/BadImageFormatException from
        /// the first native call propagate to the caller, which latches DLL_NOT_FOUND.
        /// </summary>
        private static bool EnsureNativeInitialized()
        {
            lock (_initLock)
            {
                if (_nativeInitialized)
                    return true;
                if (DpNativeHost.Init() != DpNativeHost.DpfpddSuccess)
                    return false;
                _nativeInitialized = true;
                return true;
            }
        }

        private void CloseHandleLocked()
        {
            if (_handle == IntPtr.Zero)
                return;

            Exception disposalError = null;
            try
            {
                DpNativeHost.Close(_handle);
            }
            catch (DllNotFoundException)
            {
                // Driver vanished mid-session; nothing further to release.
            }
            catch (Exception ex)
            {
                disposalError = ex;
            }
            if (disposalError != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DigitalPersonaAdapter: close failed — {disposalError.Message}");
            }
            _handle = IntPtr.Zero;
            _isConnected = false;
        }

        private static string MapNativeError(int rc)
        {
            switch (rc)
            {
                case DpNativeHost.DpfpddEMoreData: return "IMAGE_BUFFER_TOO_SMALL";
                case DpNativeHost.DpfpddEInvalidDevice: return "DEVICE_NOT_CAPTURING";
                case DpNativeHost.DpfpddEInvalidParameter: return "INVALID_PARAMETER";
                case DpNativeHost.DpfpddENoData: return "NO_DATA";
                case DpNativeHost.DpfpddEDeviceBusy: return "DEVICE_IN_USE";
                case DpNativeHost.DpfpddEDeviceFailure: return "DEVICE_FAILURE";
                case DpNativeHost.DpfpddENotImplemented: return "NOT_IMPLEMENTED";
                case DpNativeHost.DpfpddEFailure: return "FAIL";
                default: return $"DPFPDD_0x{rc:X8}";
            }
        }

        private static string MapQuality(uint quality)
        {
            if ((quality & DpNativeHost.QualityNoFinger) != 0)
                return "NO_FINGER";
            if (quality == DpNativeHost.QualityGood)
                return "NONE";
            return "QUALITY_NOT_GOOD";
        }

        /// <summary>
        /// Per-adapter map step: DigitalPersona vendor strings → the capture handler's
        /// standard codes. Unmapped codes fall back to the coarse code untouched.
        /// </summary>
        internal static string ToStandardErrorCode(string vendorErrorCode, string coarseCode)
        {
            switch (vendorErrorCode)
            {
                case "DLL_NOT_FOUND":
                    return "DRIVER_NOT_INSTALLED";
                case "DEVICE_NOT_FOUND":
                case "DEVICE_NOT_CAPTURING":
                    return "SCANNER_NOT_CONNECTED";
                default:
                    return coarseCode;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                CloseHandleLocked();
            }
            // dpfpdd_exit() is deliberately NOT called — process-wide teardown, same
            // policy as ZkNativeHost.Close() (see AGENTS.md, ZKTecoAdapter.Dispose).
        }
    }
}
