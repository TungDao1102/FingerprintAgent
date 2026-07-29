namespace FingerprintAgent.Adapters
{
    public class CaptureResult
    {
        public bool IsSuccess { get; init; }
        public byte[] ImageBytes { get; init; }
        public string MimeType { get; init; }
        public string CapturedAt { get; init; }
        public string DeviceId { get; init; }
        public string VerificationData { get; init; }
        public string ErrorMessage { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
    }
}
