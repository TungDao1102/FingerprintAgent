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
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
