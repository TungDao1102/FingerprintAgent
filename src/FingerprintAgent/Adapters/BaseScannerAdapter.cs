using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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

        public CaptureResult Scan()
        {
            byte[] raw;
            try
            {
                raw = CaptureRawImage();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return CaptureResult.Fail("CAPTURE_ERROR", ex.Message);
            }

            if (raw == null || raw.Length == 0)
            {
                _lastError = "CAPTURE_RETURNED_EMPTY";
                return CaptureResult.Fail("CAPTURE_ERROR", "Capture returned no image data");
            }

            byte[] png = ToPngGrayscale(raw, ImageWidth, ImageHeight);

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
                Width = ImageWidth,
                Height = ImageHeight
            };
        }

        protected abstract int ImageWidth { get; }
        protected abstract int ImageHeight { get; }

        protected byte[] ToPngGrayscale(byte[] raw, int width, int height)
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
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(raw, y * width, bitmapData.Scan0 + y * stride, width);
                }
                bitmap.UnlockBits(bitmapData);

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }
}