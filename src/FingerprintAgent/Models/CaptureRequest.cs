using System.Collections.Generic;
using Newtonsoft.Json;

namespace FingerprintAgent.Models
{
    public class CaptureRequest
    {
        [JsonProperty("thamChieuId")]
        public string ThamChieuId { get; set; }

        [JsonProperty("maPhieu")]
        public string MaPhieu { get; set; }

        [JsonProperty("loaiPhieu")]
        public string LoaiPhieu { get; set; }

        [JsonProperty("vaiKyId")]
        public string VaiKyId { get; set; }

        [JsonProperty("nhanLucId")]
        public string NhanLucId { get; set; }

        [JsonProperty("metadata")]
        public Dictionary<string, string> Metadata { get; set; }
    }
}
