#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// ZKTeco fingerprint scanner adapter using raw P/Invoke via <see cref="ZkNativeHost"/>
    /// (libzkfp.dll) — replaces the ZkTecoFingerPrint NuGet wrapper (v1.2.1).
    /// Handles GetDeviceCount()=0 quirk with retry/delay pattern (SCAN-10 / D-11).
    /// Returns conventional grayscale PNG bytes — NO pixel inversion (D-10).
    ///
    /// Capture calls <c>ZkNativeHost.AcquireFingerprint</c> directly with caller-supplied,
    /// caller-sized buffers (width×height image + fixed 2048-byte template). The old
    /// wrapper's parameterless overload queried ZK parameter 106 to size the image buffer;
    /// parameter 106 is unimplemented on ZK9500 (ZK SDK 5.3 / ZK10.0 firmware) and returns
    /// ZKFP_ERR_CAPTURE (-8) immediately, surfacing as a capture failure without ever
    /// reaching the blocking capture. Sizing our own buffers skips that query entirely.
    ///
        /// Rolling-capture: the native blocking call has an internal timeout (~1s on ZK9500).
        /// We retry on capture errors only while elapsed time is below the 22-second adapter
        /// budget (under ScannerManager's total 25s budget, D-06). The user needs time to click
        /// button → reach for scanner → place finger.
        /// </summary>
    public sealed class ZKTecoAdapter : IScannerAdapter, IDisposable
    {
        // Guards concurrent calls to EnsureHostInitialized(). The native ZKTeco host
        // is a process-wide singleton — repeated Initialize() calls after a failed or
        // abandoned session leave the native state inconsistent and return ZKFP_ERR_INITLIB.
        // We must Close() before re-Initialize() to recover.
        private static readonly object _hostLock = new object();

        // Counts active capture invocations. ProbeConnection refuses to Close+ReInit
        // the process-wide native host while a capture is in flight — otherwise a
        // parallel /health probe from another request would tear down the native
        // context the in-flight AcquireFingerprint still holds a handle to.
        // Read and written only under _hostLock, so no Interlocked is needed.
        private static int _captureInProgress = 0;

        // Lets service shutdown skip native-host teardown while a capture survived the drain —
        // Terminate() under a live AcquireFingerprint risks an access violation.
        internal static bool CaptureInFlight => Volatile.Read(ref _captureInProgress) > 0;

        // F2: vendor demo + old wrapper both use a 2048-byte template buffer.
        private const int TemplateBufferSize = 2048;

        private IntPtr _handle = IntPtr.Zero;
        private int _width;
        private int _height;
        private string _deviceId = "ZKTeco-unknown";
        private string _model = "ZKTeco Device";
        private string _vendorErrorCode = "NONE";
        private bool _isConnected;

        // Maps raw libzkfp error codes (ZkNativeHost.ZKFP_*) to human-readable strings.
        // Dictionary lookup handles codes the SDK may return that we don't enumerate.
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
            [ZkNativeHost.ZKFP_ERR_ADD_FINGER]     = "ERROR_ADD_FINGER",
            [ZkNativeHost.ZKFP_ERR_DELETE_FINGER]  = "ERROR_DELETE_FINGER",
            [ZkNativeHost.ZKFP_ERR_FAIL]           = "ERROR_FAIL",
            [ZkNativeHost.ZKFP_ERR_CANCEL]         = "ERROR_CANCEL",
            [ZkNativeHost.ZKFP_ERR_NOT_OPENED]     = "ERROR_NOT_OPENED",
            [ZkNativeHost.ZKFP_ERR_NOT_INIT]       = "ERROR_NOT_INIT",
            [ZkNativeHost.ZKFP_ERR_TIMEOUT]        = "ERROR_TIMEOUT",
            [ZkNativeHost.ZKFP_ERR_VERIFY]         = "ERROR_VERIFY",
            [ZkNativeHost.ZKFP_ERR_MERGE]          = "ERROR_MERGE",
            [ZkNativeHost.ZKFP_ERR_ALREADY_OPENED] = "ERROR_ALREADY_OPENED",
            [ZkNativeHost.ZKFP_ERR_LOAD_IMAGE]     = "ERROR_LOAD_IMAGE",
            [ZkNativeHost.ZKFP_ERR_ANALYZE_IMAGE]  = "ERROR_ANALYZE_IMAGE"
        };

        public bool IsConnected => _isConnected && _handle != IntPtr.Zero;

        public string DeviceId => _deviceId;

        public string Model => _model;

        public string MimeType => "image/png";

        public string VendorErrorCode => _vendorErrorCode ?? "NONE";

        /// <summary>
        /// Real-time connection check. The native host caches device info after unplug,
        /// so a lightweight GetDeviceCount() check is unreliable for detecting device
        /// removal. Forces Close + re-Initialize to re-enumerate USB devices (~50-200ms).
        /// Locked via _hostLock to prevent race with concurrent Scan().
        ///
        /// Guarded by _captureInProgress: if a capture is in flight, the native host is
        /// already held by the in-flight thread. We refuse to Close+ReInit here — that
        /// would terminate the native context the in-flight AcquireFingerprint is
        /// blocked on, corrupting its handle. Instead we return the cached _isConnected
        /// flag (the device WAS connected when the capture started).
        /// </summary>
        public bool ProbeConnection()
        {
            lock (_hostLock)
            {
                if (_captureInProgress > 0)
                {
                    _vendorErrorCode = "PROBE_DEFERRED_CAPTURE_IN_FLIGHT";
                    return _isConnected;
                }
                try { ZkNativeHost.Close(); } catch { /* best effort */ }
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
            // subsequent AcquireFingerprint returns ZKFP_ERR_CAPTURE in ~70ms instead of 2s.
            if (_handle != IntPtr.Zero)
            {
                try { ZkNativeHost.CloseDevice(_handle); } catch { }
                _handle = IntPtr.Zero;
                _isConnected = false;
            }

            // Init host with recovery for abandoned session.
            // Catch DllNotFoundException/BadImageFormatException → DLL_NOT_FOUND
            // (libzkfp.dll missing or x86/x64 mismatch — degrade gracefully instead of crashing).
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

            // AlreadyInit (=1) means the host is already usable — it is not "success"
            // (only Ok=0 is), but the host state is what we want. Treat as OK (F5).
            bool hostReady = initResult == ZkNativeHost.ZKFP_OK
                          || initResult == ZkNativeHost.ZKFP_ALREADY_INIT;
            if (!hostReady)
            {
                _vendorErrorCode = ErrorCodeToString(initResult);
                return false;
            }

            // SCAN-10 quirk: GetDeviceCount() may return 0 immediately after Init()
            // on some driver versions — retry up to 3 times with 100ms delay.
            int deviceCount = 0;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    deviceCount = ZkNativeHost.GetDeviceCount();
                    if (deviceCount > 0)
                        break;
                    if (attempt < 2)
                        Thread.Sleep(100);
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

            // Open device — TryOpenDevice manages leak-safe close on intermediate failure (W5 fix)
            if (!ZkNativeHost.TryOpenDevice(0, out _handle, out _width, out _height,
                    out _, out string serial, out string product))
            {
                _vendorErrorCode = ErrorCodeToString(ZkNativeHost.ZKFP_ERR_OPEN);
                return false;
            }

            // Lock device identity on first Initialize — ZK SDK mutates Name after AcquireFingerprint
            if (_deviceId == "ZKTeco-unknown" && !string.IsNullOrEmpty(serial))
                _deviceId = serial;
            if (_model == "ZKTeco Device" && !string.IsNullOrEmpty(product))
                _model = product;

            _isConnected = true;
            return true;
        }

        public async Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            // Snapshot device handle under lock so we read a consistent _handle/_width/_height
            // triple. After the lock we publish _captureInProgress++ so a parallel ProbeConnection
            // refuses to Close+ReInit the native host while we hold this handle. The counter is
            // decremented in the finally below. Long AcquireFingerprint runs OUTSIDE the
            // lock so a parallel /health probe doesn't block 15s on it (the probe is now
            // rejected by the _captureInProgress guard, so the lock is uncontended in practice).
            IntPtr handle;
            int width, height;
            lock (_hostLock)
            {
                if (_handle == IntPtr.Zero || !_isConnected)
                {
                    _vendorErrorCode = "SCANNER_NOT_CONNECTED";
                    return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "ZKTeco: scanner not initialized");
                }
                handle = _handle;
                width = _width;
                height = _height;
                _captureInProgress++;
            }

            // CT check MUST stay inside this try: a pre-try return would skip the
            // finally-decrement and permanently leak _captureInProgress (H8).
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _vendorErrorCode = "CANCELLED";
                    return CaptureResult.Fail("CAPTURE_TIMEOUT", "ZKTeco: capture cancelled before start");
                }

                if (width <= 0 || height <= 0)
                {
                    _vendorErrorCode = "INVALID_DIMENSIONS";
                    return CaptureResult.Fail("CAPTURE_FAILED",
                        $"ZKTeco: invalid sensor dimensions {width}x{height}");
                }

                byte[] imageBuffer = new byte[width * height];

                const int captureBudgetMs = 22000;
                const int retryDelayMs = 100;
                var stopwatch = Stopwatch.StartNew();
                int lastResult = ZkNativeHost.ZKFP_ERR_CAPTURE;

                do
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _vendorErrorCode = "CANCELLED";
                        return CaptureResult.Fail("CAPTURE_TIMEOUT", "ZKTeco: capture cancelled by timeout");
                    }

                    lastResult = await AcquireOnce(handle, imageBuffer, cancellationToken);

                    if (lastResult == ZkNativeHost.ZKFP_OK)
                        break;

                    await Task.Delay(retryDelayMs, cancellationToken);
                } while (stopwatch.ElapsedMilliseconds < captureBudgetMs);

                if (lastResult != ZkNativeHost.ZKFP_OK)
                {
                    int elapsedSec = (int)(stopwatch.ElapsedMilliseconds / 1000);
                    _vendorErrorCode = ErrorCodeToString(lastResult);

                    bool isTimeout = stopwatch.ElapsedMilliseconds >= captureBudgetMs
                                  || cancellationToken.IsCancellationRequested
                                  || lastResult == ZkNativeHost.ZKFP_ERR_TIMEOUT;
                    string code = isTimeout ? "CAPTURE_TIMEOUT" : "CAPTURE_FAILED";
                    return CaptureResult.Fail(code, ErrorCodeToUserMessage(lastResult, elapsedSec));
                }

                // imageBuffer has been populated by AcquireOnce (Marshal.Copy inside try, before FreeHGlobal)
                byte[] pngBytes = PngEncoder.ToPngGrayscale(imageBuffer, width, height);

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
            finally
            {
                lock (_hostLock)
                {
                    _captureInProgress--;
                }
            }
        }

        /// <summary>
        /// Single blocking acquire wrapped in Task.Run with correct try/finally around
        /// AllocHGlobal cleanup (W2 fix — the old wrapper did not free on exception).
        ///
        /// Marshal.Copy runs INSIDE the try (before FreeHGlobal) so image data is
        /// copied while the native pointer is still valid.
        ///
        /// ct only prevents START (same as the old wrapper) — the ~1s native block
        /// cannot be aborted; real cancellation happens at the next retry checkpoint
        /// (behavior preserved).
        /// </summary>
        private async Task<int> AcquireOnce(IntPtr handle, byte[] imageBuffer, CancellationToken ct)
        {
            IntPtr imagePtr = Marshal.AllocHGlobal(imageBuffer.Length);
            IntPtr templatePtr = IntPtr.Zero;
            try
            {
                templatePtr = Marshal.AllocHGlobal(TemplateBufferSize);
                uint cbTemplate = (uint)TemplateBufferSize;
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
            finally   // W2 fix: FreeHGlobal ALWAYS runs even if Task.Run throws
            {
                if (templatePtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(templatePtr);
                if (imagePtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(imagePtr);
            }
        }

        /// <summary>
        /// Converts a raw libzkfp error code to a human-readable string for VendorErrorCode.
        /// </summary>
        private static string ErrorCodeToString(int errorCode)
        {
            return _errorStrings.TryGetValue(errorCode, out string value)
                ? value
                : $"ERROR_UNKNOWN_{errorCode}";
        }

        /// <summary>
        /// Maps a raw libzkfp error code to a user-actionable error message for
        /// CaptureResult.ErrorMessage. Vendor-specific error string (ErrorCodeToString)
        /// stays in VendorErrorCode.
        /// Note: the old wrapper had distinct Timeout/Cancel codes; the raw SDK does not
        /// return them separately, so those cases fold into the default message.
        /// </summary>
        private static string ErrorCodeToUserMessage(int errorCode, int elapsedSec)
        {
            switch (errorCode)
            {
                case ZkNativeHost.ZKFP_ERR_CAPTURE:
                    return $"ZKTeco: no finger detected within {elapsedSec}s — please place finger on sensor and try again";
                case ZkNativeHost.ZKFP_ERR_BUSY:
                    return "ZKTeco: scanner is busy with another operation — please retry in a moment";
                case ZkNativeHost.ZKFP_ERR_ADD_FINGER:
                    return "ZKTeco: SDK failed to add fingerprint template — please retry";
                case ZkNativeHost.ZKFP_ERR_DELETE_FINGER:
                    return "ZKTeco: SDK failed to delete fingerprint template — please retry";
                case ZkNativeHost.ZKFP_ERR_ABORT:
                    return "ZKTeco: capture aborted by sensor or driver";
                case ZkNativeHost.ZKFP_ERR_INVALID_HANDLE:
                    return "ZKTeco: device handle invalidated — please retry, scanner will reinitialize";
                case ZkNativeHost.ZKFP_ERR_NO_DEVICE:
                    return "ZKTeco: no scanner detected — check USB connection";
                case ZkNativeHost.ZKFP_ERR_OPEN:
                    return "ZKTeco: scanner not opened — reinitializing, please retry";
                case ZkNativeHost.ZKFP_ERR_INVALID_PARAM:
                    return "ZKTeco: invalid parameter passed to SDK — please report to IT support";
                case ZkNativeHost.ZKFP_ERR_TIMEOUT:
                    return "ZKTeco: SDK timed out waiting for finger — please place finger on sensor and try again";
                case ZkNativeHost.ZKFP_ERR_CANCEL:
                    return "ZKTeco: capture cancelled by sensor — please retry";
                case ZkNativeHost.ZKFP_ERR_NOT_OPENED:
                    return "ZKTeco: device not opened — reinitializing, please retry";
                case ZkNativeHost.ZKFP_ERR_NOT_INIT:
                    return "ZKTeco: SDK not initialized — service will reinitialize, please retry";
                case ZkNativeHost.ZKFP_ERR_FAIL:
                    return "ZKTeco: capture failed (generic SDK error) — please retry";
                case ZkNativeHost.ZKFP_ERR_VERIFY:
                    return "ZKTeco: SDK fingerprint verification failed — please retry";
                case ZkNativeHost.ZKFP_ERR_MERGE:
                    return "ZKTeco: SDK template merge failed — please retry";
                case ZkNativeHost.ZKFP_ERR_ALREADY_OPENED:
                    return "ZKTeco: device already opened — reinitializing, please retry";
                case ZkNativeHost.ZKFP_ERR_LOAD_IMAGE:
                    return "ZKTeco: SDK failed to load fingerprint image — please retry";
                case ZkNativeHost.ZKFP_ERR_ANALYZE_IMAGE:
                    return "ZKTeco: SDK failed to analyze fingerprint image — please retry";
                default:
                    return $"ZKTeco: capture failed ({ErrorCodeToString(errorCode)})";
            }
        }

        /// <summary>
        /// Calls <see cref="ZkNativeHost.Initialize"/> with recovery for the well-known case
        /// where a previous session was abandoned mid-flight (capture failed before
        /// device was closed), leaving the native state corrupted.
        ///
        /// The ZKTeco SDK has a confusing response model:
        ///   - First call: returns ZKFP_OK (0) on success
        ///   - Second call when host is already initialized: returns ZKFP_ALREADY_INIT (1)
        ///     — not "success" but the host IS initialized, no action needed
        ///   - After a failed/abandoned session: returns ZKFP_ERR_INITLIB (-1) or
        ///     ZKFP_ERR_INIT (-2) — requires Close() + retry to recover
        ///
        /// We treat AlreadyInit as a success because the host is in the state we want.
        /// </summary>
        private static int EnsureHostInitialized()
        {
            lock (_hostLock)
            {
                int result = ZkNativeHost.Initialize();

                // AlreadyInit (=1): host already usable — treat as success (F5)
                if (result == ZkNativeHost.ZKFP_OK || result == ZkNativeHost.ZKFP_ALREADY_INIT)
                    return result;

                // InitLibrary (-1) / Init (-2): previous session abandoned,
                // native state corrupted. Close() then retry once.
                if (result == ZkNativeHost.ZKFP_ERR_INITLIB || result == ZkNativeHost.ZKFP_ERR_INIT)
                {
                    try { ZkNativeHost.Close(); } catch { /* best effort */ }
                    return ZkNativeHost.Initialize();
                }

                return result;
            }
        }

        public void Dispose()
        {
            Exception? disposalEx = null;
            if (_handle != IntPtr.Zero)
            {
                try { ZkNativeHost.CloseDevice(_handle); }
                catch (Exception ex) { disposalEx = ex; }
                _handle = IntPtr.Zero;
            }
            _isConnected = false;
            // NOTE: ZkNativeHost.Close() is deliberately NOT called here — it is a static
            // teardown that terminates the native context for ALL instances. Calling it from
            // an individual adapter's Dispose() would break the multi-instance pattern when
            // ScannerManager iterates through adapters. The host should be closed at service/
            // application shutdown only (see FingerprintAgentService / Program.cs cleanup).

            if (disposalEx != null)
                System.Diagnostics.Debug.WriteLine($"[ZKTecoAdapter] Disposal error: {disposalEx.Message}");
        }
    }
}
