using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintAgent.Adapters
{
    public class MockScannerAdapter : IScannerAdapter
    {
        public bool IsConnected => true;
        public string DeviceId => "mock-scanner-001";
        public string Model => "Mock Scanner v1.0";
        public string MimeType => "image/png";

        public bool Initialize() => true;

        public bool ProbeConnection() => IsConnected;

        public string VendorErrorCode { get { return "MOCK"; } }

        public Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            const int width = 320;
            const int height = 240;

            byte[] imageBytes = GenerateMockPng(width, height);

            string verificationData;
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(imageBytes);
                verificationData = Convert.ToBase64String(hash);
            }

            return Task.FromResult(new CaptureResult
            {
                IsSuccess = true,
                ImageBytes = imageBytes,
                MimeType = "image/png",
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = DeviceId,
                VerificationData = verificationData,
                ErrorMessage = null,
                Width = width,
                Height = height
            });
        }

        private static byte[] GenerateMockPng(int width, int height)
        {
            // Graphics doesn't support indexed pixel formats, so draw on a 32-bit ARGB temp
            byte[] grayPixels;
            using (var temp = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(temp))
            {
                graphics.Clear(Color.LightGray);

                using (var fillBrush = new SolidBrush(Color.FromArgb(50, 100, 150)))
                {
                    graphics.FillEllipse(fillBrush, 10, 10, width - 20, height - 20);
                }

                using (var borderPen = new Pen(Color.DarkGray, 2))
                {
                    graphics.DrawRectangle(borderPen, 1, 1, width - 2, height - 2);
                }

                using (var labelFont = new Font("Consolas", 10))
                using (var labelBrush = new SolidBrush(Color.Black))
                {
                    graphics.DrawString("MOCK SCANNER", labelFont, labelBrush, 10, 10);
                }

                grayPixels = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Color c = temp.GetPixel(x, y);
                        byte gray = (byte)((c.R * 0.299) + (c.G * 0.587) + (c.B * 0.114));
                        grayPixels[y * width + x] = gray;
                    }
                }
            }

            using (var grayscale = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
            {
                var palette = grayscale.Palette;
                for (int i = 0; i < 256; i++)
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                grayscale.Palette = palette;

                var data = grayscale.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format8bppIndexed);

                int stride = data.Stride;
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(grayPixels, y * width, data.Scan0 + y * stride, width);
                }
                grayscale.UnlockBits(data);

                using (var ms = new MemoryStream())
                {
                    grayscale.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }
}
