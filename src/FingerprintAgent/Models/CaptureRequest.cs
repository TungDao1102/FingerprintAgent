using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace FingerprintAgent.Models
{
    public class CaptureRequest
    {
        [JsonProperty("thamChieuId")]
        [StringLength(50)]
        public string ThamChieuId { get; set; }

        [JsonProperty("maPhieu")]
        [StringLength(50)]
        public string MaPhieu { get; set; }

        [JsonProperty("loaiPhieu")]
        [StringLength(50)]
        public string LoaiPhieu { get; set; }

        [JsonProperty("vaiKyId")]
        [StringLength(50)]
        public string VaiKyId { get; set; }

        [JsonProperty("nhanLucId")]
        [StringLength(50)]
        public string NhanLucId { get; set; }

        [JsonProperty("metadata")]
        public Dictionary<string, string> Metadata { get; set; }
    }
}
