using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;

namespace FingerprintAgent.Adapters
{
    public class MockScannerAdapter : IScannerAdapter
    {
        public bool IsConnected => true;
        public string DeviceId => "mock-scanner-001";
        public string Model => "Mock Scanner v1.0";
        public string MimeType => "image/png";

        public bool Initialize() => true;

        public string VendorErrorCode { get { return "MOCK"; } }

        public CaptureResult Scan()
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

            return new CaptureResult
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
            };
        }

        private static byte[] GenerateMockPng(int width, int height)
        {
            using (var bitmap = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(bitmap))
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

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }
}
