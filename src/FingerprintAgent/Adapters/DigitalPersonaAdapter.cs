#if DIGITALPERSONA_SDK_PRESENT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using DPFP;
using DPFP.Capture;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Digital Persona U.are.U scanner adapter using DPUruNet wrapper.
    /// Wraps the event-driven capture API with ManualResetEvent for synchronous Scan().
    /// </summary>
    public class DigitalPersonaAdapter : IScannerAdapter, CaptureEventHandler, IDisposable
    {
        private Reader _reader;
        private Capture _capture;
        private ManualResetEvent _captureEvent;
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

        public CaptureResult Scan()
        {
            if (_reader == null)
            {
                _vendorErrorCode = "NOT_INITIALIZED";
                return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "DigitalPersona scanner not initialized. Call Initialize() first.");
            }

            // Use a LOCAL wait handle per call to prevent the callback from racing
            // with a subsequent Scan() call's reassignment of _captureEvent.
            // The callback (OnComplete) signals _captureEvent which, at the moment
            // it fires, holds the correct local handle for this call.
            var waitHandle = new ManualResetEvent(false);
            _captureEvent = waitHandle;
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

            bool signaled = waitHandle.WaitOne(3000);
            _capture.StopCapture();

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
            if (_captureEvent != null)
                _captureEvent.Set();
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

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private static void DestroyHbitmap(IntPtr hBitmap)
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
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

        public CaptureResult Scan()
        {
            return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "DigitalPersona: Stub adapter — SDK not present");
        }

        public void Dispose()
        {
        }
    }
}
#endif