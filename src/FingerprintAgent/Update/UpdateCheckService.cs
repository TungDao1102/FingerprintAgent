using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;
using Newtonsoft.Json;

namespace FingerprintAgent.Update
{
    /// <summary>
    /// In-process auto-update Timer (D-13). Polls GitHub Releases, compares versions,
    /// downloads MSI, invokes msiexec. Default DISABLED via config.json (D-14).
    /// Auto-backoff: 6h → 12h → 24h after 3 no-update checks (D-15).
    /// On update failure: disable update.enabled in ProgramData config.json (D-43).
    /// </summary>
    public class UpdateCheckService : IDisposable
    {
        // ===== Constants (D-15) =====
        private static readonly int[] BackoffHours = { 6, 12, 24 };
        private const int BaseIntervalHours = 6;
        private const string ReleasesLatestUrlFormat = "https://api.github.com/repos/{0}/{1}/releases/latest";
        private const string MsiAssetName = "FingerprintAgent-Setup.msi";
        private const string GitHubOwnerEnvVar = "FA_GITHUB_OWNER";
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan PreInstallDelay = TimeSpan.FromSeconds(10);

        // ===== Testability seams =====
        private readonly HttpClient _httpClient;
        private readonly AgentConfig _config;
        private readonly AgentLogger _logger;
        private readonly object _lock = new object();

        // Mutable state (guarded by _lock)
        private Timer _timer;
        private int _noUpdateCount;
        private UpdateState _state = UpdateState.Stopped;
        private TimeSpan _nextCheckInterval = TimeSpan.FromHours(BaseIntervalHours);

        // Test seam: when set, replaces the real msiexec invocation. Tests inject a
        // record-and-skip callback. Default null = real msiexec behavior.
        // Internal so tests can set/clear it; production never sets it.
        internal Action<string, string> InstallInstallerOverride;

        // Test seam: file path to ProgramData config.json (overrides ConfigLoader.ProgramDataConfigPath).
        // Production uses ConfigLoader.ProgramDataConfigPath. Tests inject a temp path.
        private string _programDataConfigPathOverride;

        // Test seam: prevents Environment.Exit from killing the test runner.
        private bool _skipEnvironmentExit;

        // ===== Public surface =====

        /// <summary>
        /// Current lifecycle state.
        /// </summary>
        public UpdateState State
        {
            get { lock (_lock) { return _state; } }
        }

        /// <summary>
        /// Auto-backoff counter (D-15). Resets on release detected; increments on no-update / HTTP error.
        /// </summary>
        public int NoUpdateCount
        {
            get { lock (_lock) { return _noUpdateCount; } }
        }

        /// <summary>
        /// Next scheduled check interval. Updated after each check.
        /// </summary>
        public TimeSpan NextCheckInterval
        {
            get { lock (_lock) { return _nextCheckInterval; } }
        }

        /// <summary>
        /// For tests: number of times the install path was invoked.
        /// </summary>
        internal int InstallCallCount { get; private set; }

        public UpdateCheckService(AgentConfig config, AgentLogger logger)
            : this(config, logger, null)
        {
        }

        internal UpdateCheckService(AgentConfig config, AgentLogger logger, HttpMessageHandler handler)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger;

            if (handler != null)
            {
                _httpClient = new HttpClient(handler) { Timeout = HttpTimeout };
            }
            else
            {
                _httpClient = new HttpClient { Timeout = HttpTimeout };
            }
        }

        /// <summary>
        /// Starts the update Timer if config.Update.Enabled is true.
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    // Already running — no-op
                    return;
                }

                if (!_config.Update.Enabled)
                {
                    _state = UpdateState.Stopped;
                    return;
                }

                var owner = ResolveGitHubOwner();
                if (string.IsNullOrEmpty(owner))
                {
                    _logger?.Warn(null, "UpdateCheckService: githubOwner not set and FA_GITHUB_OWNER env var missing — update disabled");
                    _state = UpdateState.Stopped;
                    return;
                }

                _state = UpdateState.Running;
                var initialInterval = TimeSpan.FromHours(BaseIntervalHours);
                _nextCheckInterval = initialInterval;
                _timer = new Timer(TimerCallback, null, initialInterval, initialInterval);
                _logger?.Info(null, $"UpdateCheckService: started (interval={initialInterval.TotalHours}h, owner={owner}, repo={_config.Update.GitHubRepo})");
            }
        }

        /// <summary>
        /// Stops the update Timer. Idempotent.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    _timer.Dispose();
                    _timer = null;
                }

                _state = UpdateState.Stopped;
            }
        }

        /// <summary>
        /// Resets the Timer to fire immediately on the next available thread-pool slot.
        /// Useful for the Programs and Features "Update" verb.
        /// </summary>
        public void TriggerImmediateCheck()
        {
            lock (_lock)
            {
                if (_timer == null || _state == UpdateState.Stopped)
                {
                    return;
                }

                _timer.Change(TimeSpan.Zero, _nextCheckInterval);
            }
        }

        /// <summary>
        /// Applies new config. Starts/stops the Timer based on update.enabled toggle.
        ///
        /// CR-06: If a download or install is in flight when the config is reloaded
        /// (operator edits config.json → ConfigFileWatcher → ApplyConfig), the operator's
        /// "disable" intent would otherwise be ignored — the in-flight msiexec continues.
        /// In that case we defer the apply: the timer is left running, but new config
        /// values are stashed so a subsequent ApplyConfig (or next CheckForUpdateAsync
        /// boundary) takes effect without interrupting the in-flight operation.
        /// </summary>
        public void ApplyConfig(AgentConfig newConfig)
        {
            if (newConfig == null) return;

            bool inFlight;
            bool wasEnabled;
            bool isEnabled;

            lock (_lock)
            {
                inFlight = _state == UpdateState.Downloading
                    || _state == UpdateState.Installing;

                wasEnabled = _config.Update.Enabled;
                isEnabled = newConfig.Update.Enabled;
                // Mutate _config in place so callers don't have to re-thread the reference.
                _config.Update.Enabled = newConfig.Update.Enabled;
                _config.Update.GitHubOwner = newConfig.Update.GitHubOwner;
                _config.Update.GitHubRepo = newConfig.Update.GitHubRepo;
                _config.Update.CheckIntervalHours = newConfig.Update.CheckIntervalHours;
            }

            if (inFlight)
            {
                // Don't start/stop the timer mid-download or mid-install. Config values
                // (owner/repo/interval) are already updated above; the next CheckForUpdateAsync
                // will pick them up. The operator's effective disable intent will apply on the
                // next cycle.
                _logger?.Warn(null, "UpdateCheck: config reload during in-flight update — apply deferred until next cycle");
                return;
            }

            if (!wasEnabled && isEnabled)
            {
                Start();
            }
            else if (wasEnabled && !isEnabled)
            {
                Stop();
            }
        }

        /// <summary>
        /// Releases the Timer and HttpClient.
        /// </summary>
        public void Dispose()
        {
            Stop();
            try { _httpClient?.Dispose(); } catch { }
        }

        // ===== Internal test seams =====

        /// <summary>
        /// Test-only: returns the parsed Version from a tag name, or null on failure.
        /// Public via internal-visibility so tests can call it directly.
        /// </summary>
        internal static Version TryParseTagVersionPublic(string tagName)
        {
            return TryParseTagVersion(tagName);
        }

        /// <summary>
        /// Test-only: invokes CheckForUpdateAsync synchronously (awaitable).
        /// </summary>
        internal Task CheckForUpdateAsyncPublic()
        {
            return CheckForUpdateAsync(CancellationToken.None);
        }

        /// <summary>
        /// Test-only: invokes DownloadAndInstallAsync with a constructed release.
        /// </summary>
        internal void DownloadAndInstallForTest(GitHubReleaseInfo release)
        {
            DownloadAndInstallAsync(release).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Test-only: sets a path override for the ProgramData config.json location.
        /// Default uses ConfigLoader.ProgramDataConfigPath.
        /// </summary>
        internal void SetProgramDataConfigPathForTest(string path)
        {
            _programDataConfigPathOverride = path;
            _skipEnvironmentExit = true;
        }

        // ===== Internals =====

        private void TimerCallback(object state)
        {
            // CR-03: skip if a check is already in flight. TriggerImmediateCheck + Timer
            // could otherwise fire two concurrent CheckForUpdateAsync invocations —
            // wasted API quota, double msiexec, racing config.json writes.
            bool inFlight;
            lock (_lock)
            {
                inFlight = _state == UpdateState.Checking
                    || _state == UpdateState.Downloading
                    || _state == UpdateState.Installing;
            }
            if (inFlight)
            {
                _logger?.Debug(null, "UpdateCheckService: TimerCallback skipped — check already in flight");
                return;
            }

            // Fire-and-forget — exceptions inside CheckForUpdateAsync are caught internally.
            try
            {
                CheckForUpdateAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.Error(null, $"UpdateCheckService: TimerCallback threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task CheckForUpdateAsync(CancellationToken ct)
        {
            UpdateState prevState;
            lock (_lock) { prevState = _state; _state = UpdateState.Checking; }

            try
            {
                var owner = ResolveGitHubOwner();
                var repo = _config.Update.GitHubRepo;
                if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
                {
                    HandleNoUpdate("githubOwner or githubRepo not configured");
                    return;
                }

                var url = string.Format(ReleasesLatestUrlFormat, owner, repo);
                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;

                HttpResponseMessage response;
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.Add("Accept", "application/vnd.github+json");
                        req.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                        req.Headers.Add("User-Agent", $"FingerprintAgent/{currentVersion}");

                        response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    HandleNoUpdate($"HTTP error: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        HandleNoUpdate($"HTTP {(int)response.StatusCode}");
                        return;
                    }

                    GitHubReleaseInfo release;
                    try
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        release = JsonConvert.DeserializeObject<GitHubReleaseInfo>(body);
                    }
                    catch (Exception ex)
                    {
                        HandleNoUpdate($"JSON parse error: {ex.Message}");
                        return;
                    }

                    if (release == null)
                    {
                        HandleNoUpdate("empty release payload");
                        return;
                    }

                    // Defense in depth: even though /releases/latest excludes prereleases,
                    // explicitly skip prereleases and drafts.
                    if (release.Prerelease || release.Draft)
                    {
                        HandleNoUpdate($"prerelease or draft ({release.TagName})");
                        return;
                    }

                    var latestVersion = TryParseTagVersion(release.TagName);
                    if (latestVersion == null)
                    {
                        HandleNoUpdate($"unparseable tag '{release.TagName}'");
                        return;
                    }

                    if (latestVersion <= currentVersion)
                    {
                        HandleNoUpdate($"latest {latestVersion} <= current {currentVersion}");
                        return;
                    }

                    // Newer release detected — reset backoff and install
                    lock (_lock)
                    {
                        _noUpdateCount = 0;
                        var interval = TimeSpan.FromHours(BaseIntervalHours);
                        _nextCheckInterval = interval;
                        if (_timer != null) _timer.Change(interval, interval);
                    }
                    _logger?.Info(null, $"UpdateCheck: new release detected ({release.TagName}), backoff reset to {BaseIntervalHours}h");

                    await DownloadAndInstallAsync(release).ConfigureAwait(false);
                }
            }
            finally
            {
                // CR-05: preserve Installing/Downloading state set by DownloadAndInstallAsync.
                // If a sub-operation (Downloading, Installing) is in progress when this finally
                // runs, leave _state as-is — the inner method's own lifecycle owns it.
                // Otherwise restore to the saved prevState.
                lock (_lock)
                {
                    if (_state == UpdateState.Installing || _state == UpdateState.Downloading)
                    {
                        // Sub-operation owns state — do not overwrite.
                    }
                    else
                    {
                        _state = prevState == UpdateState.Stopped ? UpdateState.Stopped : UpdateState.Running;
                    }
                }
            }
        }

        private void HandleNoUpdate(string reason)
        {
            int newCount;
            TimeSpan nextInterval;

            lock (_lock)
            {
                _noUpdateCount++;
                newCount = _noUpdateCount;

                // 1 → 6h, 2 → 12h, 3+ → 24h (capped)
                int idx = Math.Min(newCount - 1, BackoffHours.Length - 1);
                if (idx < 0) idx = 0;
                int hours = BackoffHours[idx];
                nextInterval = TimeSpan.FromHours(hours);
                _nextCheckInterval = nextInterval;

                if (_timer != null)
                {
                    _timer.Change(nextInterval, nextInterval);
                }
            }

            _logger?.Info(null, $"UpdateCheck: no newer version (reason: {reason}), backoff step={newCount}, next interval={nextInterval.TotalHours}h");
        }

        private async Task DownloadAndInstallAsync(GitHubReleaseInfo release)
        {
            var cid = AgentLogger.GenerateCorrelationId();

            var asset = release.Assets?.Find(a => a.Name == MsiAssetName);
            if (asset == null || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
            {
                _logger?.Error(cid, $"UpdateCheck: release {release.TagName} missing {MsiAssetName} asset — cannot update");
                return;
            }

            lock (_lock) { _state = UpdateState.Downloading; }

            var tempPath = Path.Combine(Path.GetTempPath(), MsiAssetName);

            try
            {
                _logger?.Info(cid, $"UpdateCheck: downloading {asset.BrowserDownloadUrl} → {tempPath}");
                using (var stream = await _httpClient.GetStreamAsync(asset.BrowserDownloadUrl).ConfigureAwait(false))
                using (var file = File.Create(tempPath))
                {
                    await stream.CopyToAsync(file).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(cid, $"UpdateCheck: download failed: {ex.GetType().Name}: {ex.Message}");
                await HandleInstallFailureAsync(cid, $"download exception: {ex.Message}").ConfigureAwait(false);
                return;
            }

            _logger?.Info(cid, $"UpdateCheck: download complete — beginning pre-install delay of {PreInstallDelay.TotalSeconds}s");
            TryWriteEventLog($"FingerprintAgent update starting: {release.TagName}", EventLogEntryType.Information);
            await Task.Delay(PreInstallDelay).ConfigureAwait(false);

            lock (_lock) { _state = UpdateState.Installing; }

            int exitCode;
            try
            {
                if (InstallInstallerOverride != null)
                {
                    // Test seam — invoke override; treat exceptions as failure.
                    InstallInstallerOverride(asset.BrowserDownloadUrl, tempPath);
                    InstallCallCount++;
                    exitCode = 0;
                }
                else
                {
                    exitCode = RunMsiexec(tempPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(cid, $"UpdateCheck: install invocation failed: {ex.GetType().Name}: {ex.Message}");
                await HandleInstallFailureAsync(cid, $"install exception: {ex.Message}").ConfigureAwait(false);
                return;
            }

            if (exitCode != 0)
            {
                await HandleInstallFailureAsync(cid, $"msiexec exit code {exitCode}").ConfigureAwait(false);
                return;
            }

            _logger?.Info(cid, "UpdateCheck: msiexec returned 0 — restarting service");
            TryWriteEventLog($"FingerprintAgent update installed: {release.TagName}", EventLogEntryType.Information);

            // Test seam: don't kill the test runner when running under a mock install override
            if (InstallInstallerOverride != null || _skipEnvironmentExit)
            {
                _logger?.Info(cid, "UpdateCheck: test mode — would normally call Environment.Exit(0)");
                return;
            }

            // SCM recovery restarts us with new binaries
            Environment.Exit(0);
        }

        private int RunMsiexec(string tempPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/qn /i \"{tempPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                if (p == null)
                {
                    throw new InvalidOperationException("msiexec process did not start");
                }
                if (!p.WaitForExit((int)InstallTimeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("msiexec timed out after 15 minutes");
                }
                return p.ExitCode;
            }
        }

        private async Task HandleInstallFailureAsync(string cid, string reason)
        {
            _logger?.Error(cid, $"UpdateCheck: update failed: {reason} — disabling update.enabled");
            TryWriteEventLog($"FingerprintAgent update failed: {reason}", EventLogEntryType.Error);

            try
            {
                await Task.Run(() => DisableUpdateEnabledInConfig()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(cid, $"UpdateCheck: failed to write update.enabled=false to config: {ex.Message}");
            }

            Stop();
        }

        private void DisableUpdateEnabledInConfig()
        {
            var path = _programDataConfigPathOverride ?? ConfigLoader.ProgramDataConfigPath;
            if (!File.Exists(path))
            {
                _logger?.Warn(null, $"UpdateCheck: ProgramData config not found at {path} — cannot disable update.enabled");
                return;
            }

            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                var update = json["update"] as Newtonsoft.Json.Linq.JObject;
                if (update == null)
                {
                    update = new Newtonsoft.Json.Linq.JObject();
                    json["update"] = update;
                }
                update["enabled"] = false;
                // CR-02: atomic write — a process kill or power loss mid-WriteAllText would
                // otherwise leave a partial config.json that bricks the service on next boot.
                AtomicFileWriter.WriteAllText(path, json.ToString(Newtonsoft.Json.Formatting.Indented));
                _logger?.Info(null, $"UpdateCheck: wrote update.enabled=false to {path}");
            }
            catch (Exception ex)
            {
                _logger?.Error(null, $"UpdateCheck: config write failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        private string ResolveGitHubOwner()
        {
            if (!string.IsNullOrEmpty(_config.Update.GitHubOwner))
            {
                return _config.Update.GitHubOwner;
            }

            var envOwner = Environment.GetEnvironmentVariable(GitHubOwnerEnvVar);
            return string.IsNullOrEmpty(envOwner) ? null : envOwner;
        }

        private static Version TryParseTagVersion(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return null;

            var s = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName.Substring(1) : tagName;

            int dashIdx = s.IndexOf('-');
            if (dashIdx >= 0) s = s.Substring(0, dashIdx);

            return Version.TryParse(s, out var v) ? v : null;
        }

        private static void TryWriteEventLog(string message, EventLogEntryType type)
        {
            try
            {
                EventLog.WriteEntry("FingerprintAgent", message, type);
            }
            catch (Exception)
            {
                // Best-effort
            }
        }
    }
}
