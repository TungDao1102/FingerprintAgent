using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    /// <summary>
    /// Real-device integration tests for ScannerManager.TryProbe() and HealthHandler.
    /// Uses an actual ZKTecoAdapter (NOT mock) connected to a physical ZK9500 over USB.
    ///
    /// Skip behavior: each test early-returns when no device is detected, mirroring the
    /// pattern in ZKTecoDeviceIntegrationTests.cs. This keeps `dotnet test` green on
    /// machines without hardware (CI, dev laptops). The actual probe assertions only
    /// run when the ZK9500 is plugged in and the SDK driver is present.
    ///
    /// Tests run SEQUENTIALLY (no parallel) because ZkTecoFingerHost is a process-wide
    /// SDK singleton — parallel test classes would conflict on ZKFPM_Init()/Terminate().
    /// Collection definition: ProbeIntegration (defined elsewhere if needed) or use the
    /// assembly-level xunit.runner.json with parallelizeAssembly=false.
    ///
    /// Run a single test:
    ///   dotnet test --filter "FullyQualifiedName~ScannerManagerProbeIntegrationTests.TryProbe_ReturnsTrue_WithRealDeviceId"
    /// Run the whole suite:
    ///   dotnet test --filter "FullyQualifiedName~ScannerManagerProbeIntegrationTests"
    ///
    /// Prerequisites when a real device IS attached (verified at test start):
    ///   - ZK9500 plugged into USB
    ///   - ZKFinger SDK 5.3+ driver installed (libzkfp.dll in C:\Windows\SysWOW64)
    ///   - FingerprintAgent Windows service NOT running (holds device exclusively)
    /// </summary>
    [Collection("ProbeIntegration")]
    public class ScannerManagerProbeIntegrationTests : IDisposable
    {
        private readonly ZKTecoAdapter _adapter;
        private readonly ScannerManager _scanner;
        private readonly bool _deviceAvailable;

        public ScannerManagerProbeIntegrationTests()
        {
            _adapter = new ZKTecoAdapter();
            _deviceAvailable = _adapter.Initialize();
            _scanner = new ScannerManager(new IScannerAdapter[] { _adapter }, logger: null);
        }

        public void Dispose()
        {
            _scanner?.Dispose();
            _adapter?.Dispose();
        }

        private bool ShouldSkipForNoDevice(string testName)
        {
            if (_deviceAvailable) return false;
            Console.WriteLine($"[Probe] SKIPPED [{testName}] — no ZK9500 detected " +
                              $"(VendorErrorCode={_adapter.VendorErrorCode}). " +
                              "Test requires real hardware; on this machine it passes silently. " +
                              "To run for real: (1) plug in ZK9500, (2) install ZKFinger SDK 5.3+ driver, " +
                              "(3) stop the FingerprintAgent service if it is running.");
            return true;
        }

        [Fact]
        public void TryProbe_ReturnsTrue_WithRealDeviceId()
        {
            if (ShouldSkipForNoDevice(nameof(TryProbe_ReturnsTrue_WithRealDeviceId))) return;

            string deviceId, model, vendorErrorCode;
            bool result = _scanner.TryProbe(out deviceId, out model, out vendorErrorCode);

            Assert.True(result, $"TryProbe returned false. vendorErrorCode={vendorErrorCode}");
            Assert.NotEqual("no-device", deviceId);
            Assert.NotEqual("stub-device", deviceId);
            Assert.False(string.IsNullOrEmpty(deviceId), "deviceId must not be empty");
            Assert.False(string.IsNullOrEmpty(model), "model must not be empty");
            Assert.Equal("NONE", vendorErrorCode);
        }

        [Fact]
        public void TryProbe_PromotesToActiveAdapter_OnFirstSuccess()
        {
            if (ShouldSkipForNoDevice(nameof(TryProbe_PromotesToActiveAdapter_OnFirstSuccess))) return;

            Assert.False(_scanner.IsConnected, "precondition: no ActiveAdapter cached yet");

            string deviceId, model, vendorErrorCode;
            bool result = _scanner.TryProbe(out deviceId, out model, out vendorErrorCode);

            Assert.True(result);
            Assert.True(_scanner.IsConnected, "after successful probe, IsConnected must reflect cached adapter");
            Assert.Equal(deviceId, _scanner.DeviceId);
        }

        [Fact]
        public void TryProbe_WarmAndColdPath_ReturnSameDeviceId_ForCachedAdapter()
        {
            if (ShouldSkipForNoDevice(nameof(TryProbe_WarmAndColdPath_ReturnSameDeviceId_ForCachedAdapter))) return;

            string d1, m1, v1;
            _scanner.TryProbe(out d1, out m1, out v1);

            string d2, m2, v2;
            _scanner.TryProbe(out d2, out m2, out v2);

            Assert.Equal(d1, d2);
            Assert.Equal(m1, m2);
        }

        [Fact]
        public void TryProbe_DoesNotEscalateBackoff()
        {
            if (ShouldSkipForNoDevice(nameof(TryProbe_DoesNotEscalateBackoff))) return;

            int initialBackoffStep = _scanner.BackoffStep;
            bool initialInBackoff = _scanner.InBackoff;

            for (int i = 0; i < 5; i++)
            {
                string d, m, v;
                _scanner.TryProbe(out d, out m, out v);
            }

            Assert.Equal(initialBackoffStep, _scanner.BackoffStep);
            Assert.Equal(initialInBackoff, _scanner.InBackoff);
        }

        [Fact]
        public async Task HealthHandler_ReportsHealthy_WithRealDevice_OnProbe()
        {
            if (ShouldSkipForNoDevice(nameof(HealthHandler_ReportsHealthy_WithRealDevice_OnProbe))) return;

            int port = 5047;
            var config = new AgentConfig
            {
                Http = new HttpConfig { Host = "127.0.0.1", Port = port },
                Cors = new CorsConfig { Mode = "wildcard", AllowedOrigins = new[] { "*" } }
            };

            using (var server = new HttpServer(config, _scanner))
            {
                server.Start();
                using (var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) })
                {
                    var response = await client.GetAsync("/health");
                    var body = await response.Content.ReadAsStringAsync();

                    Assert.Equal(200, (int)response.StatusCode);

                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(body);
                    Assert.Equal("healthy", (string)parsed["status"]);
                    Assert.NotEqual("no-device", (string)parsed["deviceId"]);
                    Assert.NotEqual("stub-device", (string)parsed["deviceId"]);
                    Assert.False(string.IsNullOrEmpty((string)parsed["model"]));
                }
            }
        }
    }
}
