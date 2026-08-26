using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace FingerprintAgent.Models
{
    /// <summary>
    /// Generic capture request contract. Domain-neutral on purpose so any
    /// fingerprint-consuming application (HIS, attendance, KYC, ...) can
    /// reuse this agent without inheriting foreign field semantics.
    ///
    /// Fields:
    ///   - requestId (required, ≤100 chars): caller-supplied ID echoed back
    ///     in CaptureResponse and logged alongside the internal correlation
    ///     ID so the caller can correlate request↔response and request↔log.
    ///   - purpose (optional, ≤100 chars): free-text hint of what the
    ///     capture is for (enrollment, verification, signature, attendance,
    ///     ...). Logged at INFO for debug visibility; not consumed.
    ///   - metadata (optional, ≤20 keys, ≤100 chars per key/value):
    ///     pass-through bag for app-specific context (formCode, subjectId,
    ///     featureFlag, ...). Logged at DEBUG; not consumed.
    /// </summary>
    public class CaptureRequest
    {
        [JsonProperty("requestId")]
        [StringLength(100)]
        public string RequestId { get; set; }

        [JsonProperty("purpose")]
        [StringLength(100)]
        public string Purpose { get; set; }

        [JsonProperty("metadata")]
        public Dictionary<string, string> Metadata { get; set; }
    }
}
