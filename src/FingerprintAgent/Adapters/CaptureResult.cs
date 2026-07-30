using System;

namespace FingerprintAgent.Adapters
{
    public class CaptureResult
    {
        public bool IsSuccess { get; set; }
        public byte[] ImageBytes { get; set; }
        public string MimeType { get; set; }
        public string CapturedAt { get; set; }
        public string DeviceId { get; set; }
        public string VerificationData { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorCode { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public static CaptureResult Fail(string errorCode, string message)
        {
            return new CaptureResult
            {
                IsSuccess = false,
                ImageBytes = null,
                MimeType = null,
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = null,
                VerificationData = null,
                ErrorMessage = message,
                ErrorCode = errorCode,
                Width = 0,
                Height = 0
            };
        }
    }
}
