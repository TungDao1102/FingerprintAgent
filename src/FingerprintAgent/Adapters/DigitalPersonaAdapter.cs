#if DIGITALPERSONA_SDK_PRESENT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DPFP;
using DPFP.Capture;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Digital Persona U.are.U scanner adapter using DPUruNet wrapper.
    /// Wraps the event-driven capture API with TaskCompletionSource for asynchronous ScanAsync().
    /// </summary>
    public class DigitalPersonaAdapter : IScannerAdapter, CaptureEventHandler, IDisposable
    {
        private Reader _reader;
        private Capture _capture;
        private TaskCompletionSource<bool> _captureTcs;
        private Sample _capturedSample;
        private string _deviceId;
        private string _model;
        private string _vendorErrorCode;
        private bool _isConnected;

        private static readonly Dictionary<ReturnCode, string> ErrorStrings = new Dictionary<ReturnCode, string>
        {
            { ReturnCode.SUCCESS, "SUCCESS" },
            { ReturnCode.FAIL, "FAIL" },
            { ReturnCode.DEVICE_IN_USE, "DEVICE_IN_USE" },
            { ReturnCode.DEVICE_NOT_FOUND, "DEVICE_NOT_FOUND" },
            { ReturnCode.DEVICE_NOT_CAPTURING, "DEVICE_NOT_CAPTURING" }
        };

        public bool IsConnected => _isConnected;

        public string DeviceId => _deviceId ?? "no-device";

        public string Model => _model ?? "no-device";

        public string MimeType => "image/png";

        public string VendorErrorCode => _vendorErrorCode ?? "NONE";

        public bool ProbeConnection() => IsConnected;

        public bool Initialize()
        {
            try
            {
                _capturedSample = null;
                _vendorErrorCode = "NONE";

                ReaderCollection readers = ReaderCollection.GetReaders();
                if (readers.Count == 0)
                {
                    _vendorErrorCode = "DEVICE_NOT_FOUND";
                    _isConnected = false;
                    return false;
                }

                _reader = readers[0];
                _capture = new Capture();
                _capture.EventHandler = this;
                _deviceId = _reader.SerialNumber;
                _model = _reader.Description;
                _isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                _vendorErrorCode = MapException(ex);
                _isConnected = false;
                return false;
            }
        }

        public async Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            if (_reader == null)
            {
                _vendorErrorCode = "NOT_INITIALIZED";
                return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "DigitalPersona scanner not initialized. Call Initialize() first.");
            }

            // Use a LOCAL TaskCompletionSource per call to prevent the callback from
            // racing with a subsequent ScanAsync() call's reassignment of _captureTcs.
            // The callback (OnComplete) signals _captureTcs which, at the moment it
            // fires, holds the correct local TCS for this call.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _captureTcs = tcs;
            _capturedSample = null;
            _vendorErrorCode = "NONE";

            try
            {
                _capture.StartCapture();
            }
            catch (Exception ex)
            {
                _vendorErrorCode = MapException(ex);
                return CaptureResult.Fail("CAPTURE_ERROR", $"DigitalPersona:{_vendorErrorCode}");
            }

            bool signaled;
            try
            {
                // Link caller cancellation with a 3s timeout. When either fires, the
                // TCS is cancelled and the await re-throws OperationCanceledException.
                // RunContinuationsAsynchronously (set on TCS construction) keeps the
                // OnComplete callback — which may run on a native SDK thread — from
                // blocking on our async continuation.
                using (var timeoutCts = new CancellationTokenSource(3000))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
                {
                    try
                    {
                        await tcs.Task;
                        signaled = true;
                    }
                    catch (OperationCanceledException)
                    {
                        signaled = false;
                    }
                }
            }
            finally
            {
                // Always stop capture — even if timeout occurred or exception was thrown.
                // DPUruNet's StartCapture/StopCapture must be paired per call.
                _capture.StopCapture();
            }

            if (cancellationToken.IsCancellationRequested && !signaled)
            {
                _vendorErrorCode = "CANCELLED";
                return CaptureResult.Fail("CAPTURE_TIMEOUT", "DigitalPersona: capture cancelled by timeout");
            }

            if (!signaled || _capturedSample == null)
            {
                _vendorErrorCode = _vendorErrorCode == "NONE" ? "CAPTURE_TIMEOUT" : _vendorErrorCode;
                return CaptureResult.Fail("CAPTURE_TIMEOUT", $"DigitalPersona:{_vendorErrorCode}");
            }

            // Convert sample to PNG
            SampleConversion conv = new SampleConversion();
            IntPtr ptr = IntPtr.Zero;
            Bitmap bmp = null;
            byte[] png;
            int bmpWidth = 0;
            int bmpHeight = 0;
            try
            {
                conv.ConvertToPicture(_capturedSample, ref ptr);
                bmp = Bitmap.FromHbitmap(ptr);
                bmpWidth = bmp.Width;
                bmpHeight = bmp.Height;
                png = BitmapToPng(bmp);
            }
            catch (Exception ex)
            {
                _vendorErrorCode = MapException(ex);
                return CaptureResult.Fail("CONVERSION_ERROR", $"DigitalPersona:{ex.Message}");
            }
            finally
            {
                // Bitmap.FromHbitmap(ptr) creates a Bitmap that owns the HANDLE.
                // Calling bmp.Dispose() internally calls GDI DeleteObject(ptr).
                // Do NOT call DestroyHbitmap separately — that would be a double-delete.
                bmp?.Dispose();
            }

            string verificationData;
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(png);
                verificationData = Convert.ToBase64String(hash);
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
                Width = bmpWidth,
                Height = bmpHeight
            };
        }

        #region CaptureEventHandler Members

        public void OnComplete(object Capture, string ReaderSerialNumber, Sample Sample)
        {
            _capturedSample = Sample;
            if (_captureTcs != null)
                _captureTcs.TrySetResult(true);
        }

        public void OnFingerGone(object Capture, string ReaderSerialNumber) { }

        public void OnFingerTouch(object Capture, string ReaderSerialNumber) { }

        public void OnReaderConnect(object Capture, string ReaderSerialNumber) { }

        public void OnReaderDisconnect(object Capture, string ReaderSerialNumber) { }

        public void OnSampleQuality(object Capture, string ReaderSerialNumber, CaptureFeedback CaptureFeedback)
        {
            if (CaptureFeedback != CaptureFeedback.Good)
            {
                _vendorErrorCode = "QUALITY_NOT_GOOD";
            }
        }

        #endregion

        private static byte[] BitmapToPng(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        private static string MapException(Exception ex)
        {
            if (ex is DPFP.Error.Exception dpEx)
            {
                if (ErrorStrings.TryGetValue(dpEx.ReturnCode, out string mapped))
                    return mapped;
                return dpEx.ReturnCode.ToString();
            }
            return ex.GetType().Name;
        }

        public void Dispose()
        {
            _capture?.Dispose();
            _capture = null;
            _reader?.Dispose();
            _reader = null;
        }
    }
}
#else
// Stub implementation when DIGITALPERSONA_SDK_PRESENT is not defined.
// Allows compilation and unit testing without the vendor SDK DLL present.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintAgent.Adapters
{
    public class DigitalPersonaAdapter : IScannerAdapter, IDisposable
    {
        public bool IsConnected => false;
        public string DeviceId => "stub-device";
        public string Model => "Digital Persona (stub)";
        public string MimeType => "image/png";
        public string VendorErrorCode => "NONE";

        public bool Initialize()
        {
            return false;
        }

        public bool ProbeConnection() => IsConnected;

        public Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CaptureResult.Fail("SCANNER_NOT_CONNECTED", "DigitalPersona: Stub adapter — SDK not present"));
        }

        public void Dispose()
        {
        }
    }
}
#endif