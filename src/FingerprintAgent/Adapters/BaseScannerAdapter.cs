using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintAgent.Adapters
{
    public abstract class BaseScannerAdapter : IScannerAdapter
    {
        protected string _lastError;

        public abstract bool IsConnected { get; }
        public abstract string DeviceId { get; }
        public abstract string Model { get; }
        public abstract bool InitializeDevice();
        public abstract byte[] CaptureRawImage();

        public string MimeType => "image/png";

        public string VendorErrorCode => _lastError ?? "NONE";

        public bool Initialize() => InitializeDevice();

        /// <summary>
        /// Default probe: trusts the cached IsConnected flag. Adapters with a native
        /// lightweight "query device count" SDK call (e.g. ZKTeco) should override
        /// this with a real re-verification.
        /// </summary>
        public virtual bool ProbeConnection() => IsConnected;

        public Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _lastError = "CANCELLED";
                return Task.FromResult(CaptureResult.Fail("CAPTURE_TIMEOUT", "Capture cancelled before start"));
            }

            byte[] raw;
            try
            {
                raw = CaptureRawImage();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return Task.FromResult(CaptureResult.Fail("CAPTURE_ERROR", ex.Message));
            }

            if (raw == null || raw.Length == 0)
            {
                _lastError = "CAPTURE_RETURNED_EMPTY";
                return Task.FromResult(CaptureResult.Fail("CAPTURE_ERROR", "Capture returned no image data"));
            }

            byte[] png = ToPngGrayscale(raw, ImageWidth, ImageHeight);

            string verificationData;
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(png);
                verificationData = Convert.ToBase64String(hash);
            }

            return Task.FromResult(new CaptureResult
            {
                IsSuccess = true,
                ImageBytes = png,
                MimeType = "image/png",
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = DeviceId,
                VerificationData = verificationData,
                ErrorMessage = null,
                Width = ImageWidth,
                Height = ImageHeight
            });
        }

        protected abstract int ImageWidth { get; }
        protected abstract int ImageHeight { get; }

        protected byte[] ToPngGrayscale(byte[] raw, int width, int height)
            => PngEncoder.ToPngGrayscale(raw, width, height);
    }
}