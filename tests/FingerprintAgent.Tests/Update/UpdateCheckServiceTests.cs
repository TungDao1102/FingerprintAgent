using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;
using FingerprintAgent.Update;
using Newtonsoft.Json;
using Xunit;

namespace FingerprintAgent.Tests.Update
{
    public class UpdateCheckServiceTests : IDisposable
    {
        private readonly string _logDir;
        private readonly string _logFile;
        private readonly string _tempDir;
        private readonly AgentLogger _logger;

        public UpdateCheckServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"UpdateCheckService-Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            _logDir = Path.Combine(_tempDir, "logs");
            _logFile = Path.Combine(_logDir, "agent.log");
            Directory.CreateDirectory(_logDir);

            _logger = new AgentLogger(new LoggingConfig
            {
                Level = "DEBUG",
                File = _logFile,
                MaxSizeMb = 10,
                MaxFiles = 5
            });
        }

        public void Dispose()
        {
            try { _logger?.Dispose(); } catch { }
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        private AgentConfig CreateConfig(bool enabled = true, string owner = "testowner", string repo = "FingerprintAgent")
        {
            return new AgentConfig
            {
                Update = new UpdateConfig
                {
                    Enabled = enabled,
                    GitHubOwner = owner,
                    GitHubRepo = repo,
                    CheckIntervalHours = 6
                }
            };
        }

        private UpdateCheckService CreateService(AgentConfig config, MockHttpMessageHandler handler)
        {
            return new UpdateCheckService(config, _logger, handler);
        }

        private static string MakeReleaseJson(string tag, bool prerelease = false, string assetName = "FingerprintAgent-Setup.msi", string assetUrl = "https://mock.local/setup.msi")
        {
            var release = new
            {
                tag_name = tag,
                name = tag,
                prerelease,
                draft = false,
                assets = new[]
                {
                    new { name = assetName, browser_download_url = assetUrl, size = 1024L }
                }
            };
            return JsonConvert.SerializeObject(release);
        }

        // ===== Tests =====

        [Fact]
        public void Start_WhenUpdateDisabled_DoesNothing()
        {
            var config = CreateConfig(enabled: false);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.Host == "api.github.com",
                HttpStatusCode.OK,
                MakeReleaseJson("v0.0.1"));

            using (var service = CreateService(config, handler))
            {
                service.Start();

                Assert.Equal(UpdateState.Stopped, service.State);
                Assert.Equal(0, handler.CallCount);
            }
        }

        [Fact]
        public void Start_WhenUpdateEnabled_StartsTimer()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.Host == "api.github.com",
                HttpStatusCode.OK,
                MakeReleaseJson("v0.0.1"));

            using (var service = CreateService(config, handler))
            {
                service.Start();

                Assert.Equal(UpdateState.Running, service.State);
                // No immediate HTTP call — initial due time = interval
                Assert.Equal(0, handler.CallCount);
            }
        }

        [Fact]
        public async Task CheckForUpdateAsync_NewerRelease_TriggersDownload()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();

            // Mock the GitHub API response with newer release
            handler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.OK,
                MakeReleaseJson("v99.99.99"));

            // Mock the MSI download URL — return a tiny fake MSI body
            handler.QueueResponse(
                uri => uri.Host == "mock.local",
                HttpStatusCode.OK,
                "FAKE_MSI_CONTENT",
                "application/octet-stream");

            using (var service = CreateService(config, handler))
            {
                service.InstallInstallerOverride = (url, path) => { /* swallow */ };

                await service.CheckForUpdateAsyncPublic();

                Assert.Equal(1, service.InstallCallCount);
                Assert.True(handler.CallCount >= 2, $"Expected at least 2 HTTP calls (releases/latest + MSI), got {handler.CallCount}");
            }
        }

        [Fact]
        public async Task CheckForUpdateAsync_SameVersion_NoDownload()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            // Use a tag equal to or below the current assembly version
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            var tagVersion = $"{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}";
            handler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.OK,
                MakeReleaseJson("v" + tagVersion));

            using (var service = CreateService(config, handler))
            {
                service.InstallInstallerOverride = (url, path) => { };

                await service.CheckForUpdateAsyncPublic();

                Assert.Equal(0, service.InstallCallCount);
            }
        }

        [Fact]
        public async Task CheckForUpdateAsync_OlderVersion_NoDownload()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.OK,
                MakeReleaseJson("v0.0.1"));

            using (var service = CreateService(config, handler))
            {
                service.InstallInstallerOverride = (url, path) => { };

                await service.CheckForUpdateAsyncPublic();

                Assert.Equal(0, service.InstallCallCount);
            }
        }

        [Fact]
        public async Task CheckForUpdateAsync_Prerelease_Ignored()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.OK,
                MakeReleaseJson("v99.99.99", prerelease: true));

            using (var service = CreateService(config, handler))
            {
                service.InstallInstallerOverride = (url, path) => { };

                await service.CheckForUpdateAsyncPublic();

                Assert.Equal(0, service.InstallCallCount);
            }
        }

        [Fact]
        public async Task CheckForUpdateAsync_HttpError_IncrementsCounter()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.InternalServerError,
                "{}");

            using (var service = CreateService(config, handler))
            {
                await service.CheckForUpdateAsyncPublic();

                Assert.Equal(1, service.NoUpdateCount);
                Assert.Equal(TimeSpan.FromHours(6), service.NextCheckInterval);
            }
        }

        [Fact]
        public async Task AutoBackoff_After3NoUpdates_ResetsTo24h()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.InternalServerError,
                "{}");

            using (var service = CreateService(config, handler))
            {
                await service.CheckForUpdateAsyncPublic(); // count = 1, interval = 6h
                Assert.Equal(TimeSpan.FromHours(6), service.NextCheckInterval);

                await service.CheckForUpdateAsyncPublic(); // count = 2, interval = 12h
                Assert.Equal(TimeSpan.FromHours(12), service.NextCheckInterval);

                await service.CheckForUpdateAsyncPublic(); // count = 3, interval = 24h (capped)
                Assert.Equal(TimeSpan.FromHours(24), service.NextCheckInterval);

                await service.CheckForUpdateAsyncPublic(); // count = 4, still 24h (cap holds)
                Assert.Equal(TimeSpan.FromHours(24), service.NextCheckInterval);
            }
        }

        [Fact]
        public async Task AutoBackoff_OnRelease_ResetsToBase()
        {
            // Phase 1: 3 errors → 24h
            var config = CreateConfig(enabled: true);
            var errorHandler = new MockHttpMessageHandler();
            errorHandler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.InternalServerError,
                "{}");

            using (var service = CreateService(config, errorHandler))
            {
                await service.CheckForUpdateAsyncPublic();
                await service.CheckForUpdateAsyncPublic();
                await service.CheckForUpdateAsyncPublic();
                Assert.Equal(TimeSpan.FromHours(24), service.NextCheckInterval);
                Assert.Equal(3, service.NoUpdateCount);
            }

            // Phase 2: release available → reset
            var successHandler = new MockHttpMessageHandler();
            successHandler.QueueResponse(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                HttpStatusCode.OK,
                MakeReleaseJson("v99.99.99"));
            successHandler.QueueResponse(
                uri => uri.Host == "mock.local",
                HttpStatusCode.OK,
                "FAKE_MSI_CONTENT",
                "application/octet-stream");

            using (var service2 = CreateService(config, successHandler))
            {
                service2.InstallInstallerOverride = (url, path) => { };
                await service2.CheckForUpdateAsyncPublic();

                Assert.Equal(0, service2.NoUpdateCount);
                Assert.Equal(TimeSpan.FromHours(6), service2.NextCheckInterval);
            }
        }

        [Fact]
        public void DownloadAndInstallAsync_InstallFailure_DisablesUpdateEnabled()
        {
            // Configure: enabled=true so download path runs
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();

            // Mock the MSI download URL so it succeeds
            handler.QueueResponse(
                uri => uri.Host == "mock.local",
                HttpStatusCode.OK,
                "FAKE_MSI_CONTENT",
                "application/octet-stream");

            // Create a temp ProgramData-style config file
            var programDataDir = Path.Combine(_tempDir, "ProgramData", "FingerprintAgent");
            Directory.CreateDirectory(programDataDir);
            var configPath = Path.Combine(programDataDir, "config.json");
            File.WriteAllText(configPath, @"{
  ""update"": {
    ""enabled"": true,
    ""githubOwner"": ""testowner"",
    ""githubRepo"": ""FingerprintAgent"",
    ""checkIntervalHours"": 6
  }
}");

            using (var service = CreateService(config, handler))
            {
                // Force install to fail by making override throw
                service.InstallInstallerOverride = (url, path) =>
                {
                    throw new InvalidOperationException("SIMULATED_MSIEXEC_1603");
                };
                service.SetProgramDataConfigPathForTest(configPath);

                var release = new GitHubReleaseInfo
                {
                    TagName = "v99.99.99",
                    Prerelease = false,
                    Assets = new System.Collections.Generic.List<GitHubAsset>
                    {
                        new GitHubAsset { Name = "FingerprintAgent-Setup.msi", BrowserDownloadUrl = "https://mock.local/setup.msi" }
                    }
                };

                service.DownloadAndInstallForTest(release);

                // Verify config.json updated
                Assert.True(File.Exists(configPath));
                var updatedJson = File.ReadAllText(configPath);
                Assert.Contains("\"enabled\": false", updatedJson);
            }
        }

        [Fact]
        public void DownloadAndInstallAsync_DownloadFailure_DisablesUpdateEnabled()
        {
            // Configure: enabled=true so download path runs
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();

            // Make the download URL fail (no mock registered)

            // Create a temp ProgramData-style config file
            var programDataDir = Path.Combine(_tempDir, "ProgramData", "FingerprintAgent");
            Directory.CreateDirectory(programDataDir);
            var configPath = Path.Combine(programDataDir, "config.json");
            File.WriteAllText(configPath, @"{
  ""update"": {
    ""enabled"": true,
    ""githubOwner"": ""testowner"",
    ""githubRepo"": ""FingerprintAgent"",
    ""checkIntervalHours"": 6
  }
}");

            using (var service = CreateService(config, handler))
            {
                service.SetProgramDataConfigPathForTest(configPath);

                var release = new GitHubReleaseInfo
                {
                    TagName = "v99.99.99",
                    Prerelease = false,
                    Assets = new System.Collections.Generic.List<GitHubAsset>
                    {
                        new GitHubAsset { Name = "FingerprintAgent-Setup.msi", BrowserDownloadUrl = "https://unreachable.local/setup.msi" }
                    }
                };

                service.DownloadAndInstallForTest(release);

                // Verify config.json updated to disable update
                var updatedJson = File.ReadAllText(configPath);
                Assert.Contains("\"enabled\": false", updatedJson);
            }
        }

        [Fact]
        public void VersionParsing_StripsPrefixAndSuffix()
        {
            // v1.2.3-rc1 → 1.2.3
            var parsed = UpdateCheckService.TryParseTagVersionPublic("v1.2.3-rc1");
            Assert.NotNull(parsed);
            Assert.Equal(1, parsed.Major);
            Assert.Equal(2, parsed.Minor);
            Assert.Equal(3, parsed.Build);

            // Plain v1.2.3
            var plain = UpdateCheckService.TryParseTagVersionPublic("v1.2.3");
            Assert.NotNull(plain);
            Assert.Equal(new Version(1, 2, 3), plain);

            // No prefix
            var noPrefix = UpdateCheckService.TryParseTagVersionPublic("2.0.0");
            Assert.NotNull(noPrefix);
            Assert.Equal(new Version(2, 0, 0), noPrefix);

            // Garbage
            var garbage = UpdateCheckService.TryParseTagVersionPublic("not-a-version");
            Assert.Null(garbage);
        }

        [Fact]
        public void Dispose_StopsTimer()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.Host == "api.github.com",
                HttpStatusCode.OK,
                MakeReleaseJson("v0.0.1"));

            var service = CreateService(config, handler);
            service.Start();

            Assert.Equal(UpdateState.Running, service.State);

            service.Dispose();

            Assert.Equal(UpdateState.Stopped, service.State);
        }

        // ===== Concurrency guard tests (CR-03 / CR-05 / CR-06) =====

        /// <summary>
        /// CR-03: when the Timer fires while a TriggerImmediateCheck is already in flight,
        /// the second TimerCallback must skip and not issue a second HTTP request. Without
        /// the guard, two CheckForUpdateAsync invocations would race — wasted API quota,
        /// potential double-msiexec, racing config.json writes.
        /// </summary>
        [Fact]
        public async Task TriggerImmediateCheck_WhenAlreadyChecking_SkipsSecondHttpCall()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();

            // Block the first HTTP response until manually released — state stays in Checking
            // while the gate is open, which is the window where overlapping triggers would race.
            var gate = new TaskCompletionSource<HttpResponseMessage>();
            handler.QueueResponseTask(
                uri => uri.AbsolutePath.Contains("/releases/latest"),
                gate.Task);

            using (var service = CreateService(config, handler))
            {
                service.Start();

                // First trigger: Timer fires immediately, state goes Checking, HTTP call in flight
                service.TriggerImmediateCheck();
                await Task.Delay(100);
                Assert.Equal(UpdateState.Checking, service.State);
                Assert.Equal(1, handler.CallCount);

                // Subsequent triggers while first is still in flight — TimerCallback must skip
                // because state == Checking (CR-03 in-flight guard).
                service.TriggerImmediateCheck();
                service.TriggerImmediateCheck();
                service.TriggerImmediateCheck();
                await Task.Delay(300);
                Assert.Equal(1, handler.CallCount);

                // Release the gate — first check completes
                gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(MakeReleaseJson("v0.0.1"))
                });
                await Task.Delay(200);

                // Still only one HTTP call total — no overlap was permitted
                Assert.Equal(1, handler.CallCount);
                Assert.Equal(UpdateState.Running, service.State);
            }
        }

        /// <summary>
        /// CR-05: when an install fails, the service must end in Stopped state (HandleInstallFailureAsync
        /// calls Stop()). A naive `finally` block that unconditionally restored `_state = prevState`
        /// would leave state == Running even after Stop() — defeating the disable intent and producing
        /// a confusing state for the next ApplyConfig cycle.
        /// </summary>
        [Fact]
        public void DownloadAndInstallForTest_WhenInstallFails_FinalStateIsStopped()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();
            handler.QueueResponse(
                uri => uri.Host == "mock.local",
                HttpStatusCode.OK,
                "FAKE_MSI_CONTENT",
                "application/octet-stream");

            // ProgramData-style config so HandleInstallFailureAsync can write update.enabled=false
            var programDataDir = Path.Combine(_tempDir, "ProgramData-FailState", "FingerprintAgent");
            Directory.CreateDirectory(programDataDir);
            var configPath = Path.Combine(programDataDir, "config.json");
            File.WriteAllText(configPath, @"{
  ""update"": {
    ""enabled"": true,
    ""githubOwner"": ""testowner"",
    ""githubRepo"": ""FingerprintAgent"",
    ""checkIntervalHours"": 6
  }
}");

            using (var service = CreateService(config, handler))
            {
                service.SetProgramDataConfigPathForTest(configPath);
                service.InstallInstallerOverride = (url, path) =>
                {
                    throw new InvalidOperationException("SIMULATED_MSIEXEC_1603");
                };

                var release = new GitHubReleaseInfo
                {
                    TagName = "v99.99.99",
                    Prerelease = false,
                    Assets = new System.Collections.Generic.List<GitHubAsset>
                    {
                        new GitHubAsset { Name = "FingerprintAgent-Setup.msi", BrowserDownloadUrl = "https://mock.local/setup.msi" }
                    }
                };

                service.DownloadAndInstallForTest(release);

                // Final state must be Stopped — HandleInstallFailureAsync.Stop() won, and the
                // outer CheckForUpdateAsync finally did not overwrite it back to Running.
                Assert.Equal(UpdateState.Stopped, service.State);

                // Verify config.json was updated to disable updates
                Assert.Contains("\"enabled\": false", File.ReadAllText(configPath));
            }
        }

        /// <summary>
        /// CR-06: ApplyConfig(enabled=false) called while a download/install is in flight must
        /// defer the Stop() call. The operator's disable intent takes effect on the next cycle,
        /// not mid-MSI. This test forces Downloading state directly so we can assert the deferral
        /// without driving the full 10s PreInstallDelay.
        /// </summary>
        [Fact]
        public void ApplyConfig_DuringDownload_DoesNotStopTimer()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();

            using (var service = CreateService(config, handler))
            {
                service.Start();
                Assert.Equal(UpdateState.Running, service.State);

                // Force state into Downloading — simulates mid-download snapshot.
                service.SetStateForTest(UpdateState.Downloading);

                // Operator toggles update.enabled=false mid-flight
                var disabledConfig = CreateConfig(enabled: false);
                service.ApplyConfig(disabledConfig);

                // CR-06 deferral: Stop() was NOT called because state was in-flight.
                // State remains Downloading; config values are mutated in place and take effect
                // on the next CheckForUpdateAsync cycle.
                Assert.Equal(UpdateState.Downloading, service.State);
                Assert.False(config.Update.Enabled);

                // Same assertion for Installing — both in-flight states defer
                service.SetStateForTest(UpdateState.Installing);
                var stillDisabled = CreateConfig(enabled: false);
                service.ApplyConfig(stillDisabled);
                Assert.Equal(UpdateState.Installing, service.State);
            }
        }

        /// <summary>
        /// CR-06 boundary case: ApplyConfig(enabled=false) called while state == Checking
        /// (NOT in-flight per the current definition) MUST call Stop(). This is the inverse
        /// of the in-flight deferral test — the operator's disable intent must apply promptly
        /// when nothing is in progress. A naive "always defer" would leave the timer running
        /// for an entire check cycle.
        /// </summary>
        [Fact]
        public void ApplyConfig_DuringChecking_CallsStop()
        {
            var config = CreateConfig(enabled: true);
            var handler = new MockHttpMessageHandler();

            using (var service = CreateService(config, handler))
            {
                service.Start();
                Assert.Equal(UpdateState.Running, service.State);

                // Force state into Checking — the HTTP call is "in flight" from the user's
                // perspective but the in-flight definition only includes Downloading/Installing.
                service.SetStateForTest(UpdateState.Checking);

                var disabledConfig = CreateConfig(enabled: false);
                service.ApplyConfig(disabledConfig);

                // Stop() IS called because Checking is not in-flight — operator's intent applies
                Assert.Equal(UpdateState.Stopped, service.State);
                Assert.False(config.Update.Enabled);
            }
        }
    }
}
