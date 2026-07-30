using Newtonsoft.Json;

namespace FingerprintAgent.Models
{
    public class CaptureResponse
    {
        [JsonProperty("isSuccess")]
        public bool IsSuccess { get; set; }

        [JsonProperty("imageBytes")]
        public string ImageBytes { get; set; }

        [JsonProperty("mimeType")]
        public string MimeType { get; set; }

        [JsonProperty("capturedAt")]
        public string CapturedAt { get; set; }

        [JsonProperty("deviceId")]
        public string DeviceId { get; set; }

        [JsonProperty("verificationData")]
        public string VerificationData { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonProperty("errorCode")]
        public string ErrorCode { get; set; }

        [JsonProperty("vendorErrorCode")]
        public string VendorErrorCode { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }
    }
}
