using System.Collections.Generic;
using Newtonsoft.Json;

namespace FingerprintAgent.Update
{
    /// <summary>
    /// Lifecycle states of UpdateCheckService. Public so tests and observers can introspect.
    /// </summary>
    public enum UpdateState
    {
        Stopped = 0,
        Running = 1,
        Checking = 2,
        Downloading = 3,
        Installing = 4
    }

    /// <summary>
    /// Subset of the GitHub Releases API response we actually consume.
    /// Newtonsoft.Json ignores unknown fields, so the full payload is tolerated.
    /// </summary>
    public class GitHubReleaseInfo
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("prerelease")]
        public bool Prerelease { get; set; }

        [JsonProperty("draft")]
        public bool Draft { get; set; }

        [JsonProperty("assets")]
        public List<GitHubAsset> Assets { get; set; } = new List<GitHubAsset>();
    }

    public class GitHubAsset
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }
    }
}
