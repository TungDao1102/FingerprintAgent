using System;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    /// <summary>
    /// Unit tests for ScannerManager.UpdateScannerConfig — hot-reload of the
    /// MockMode toggle (D-06 reload scope / E-2 workflow flip).
    ///
    /// Uses the config constructor with an empty priority list so no real vendor
    /// adapter is ever initialized — deterministic on machines without hardware.
    /// </summary>
    public class ScannerManagerMockReloadTests : IDisposable
    {
        private readonly ScannerManager _scanner;

        public ScannerManagerMockReloadTests()
        {
            _scanner = CreateScanner(mockMode: false);
        }

        public void Dispose() => _scanner?.Dispose();

        private static ScannerManager CreateScanner(bool mockMode)
        {
            var config = new AgentConfig
            {
                Scanner = new ScannerConfig { MockMode = mockMode, Priority = new string[0] }
            };
            return new ScannerManager(config, logger: null);
        }

        private static ScannerConfig ReloadConfig(bool mockMode) =>
            new ScannerConfig { MockMode = mockMode, Priority = new string[0] };

        [Fact]
        public async Task UpdateScannerConfig_MockModeFlipsFalseToTrue_ScanAsyncReturnsMockCapture()
        {
            _scanner.UpdateScannerConfig(ReloadConfig(mockMode: true));

            CaptureResult result = await _scanner.ScanAsync();

            Assert.True(result.IsSuccess, $"capture failed: {result.ErrorMessage}");
            Assert.Equal("mock-scanner-001", result.DeviceId);
            Assert.Equal("image/png", result.MimeType);
        }

        [Fact]
        public void UpdateScannerConfig_MockModeFlipsFalseToTrue_TryProbeReportsMockConnected()
        {
            _scanner.UpdateScannerConfig(ReloadConfig(mockMode: true));

            string deviceId, model, vendorErrorCode;
            bool connected = _scanner.TryProbe(out deviceId, out model, out vendorErrorCode);

            Assert.True(connected, $"TryProbe returned false. vendorErrorCode={vendorErrorCode}");
            Assert.Equal("mock-scanner-001", deviceId);
            Assert.Equal("MOCK", vendorErrorCode);
        }

        [Fact]
        public void UpdateScannerConfig_MockModeFlipsTrueToFalse_MockAdapterNoLongerActive()
        {
            using (var scanner = CreateScanner(mockMode: true))
            {
                Assert.Equal("mock-scanner-001", scanner.DeviceId);

                scanner.UpdateScannerConfig(ReloadConfig(mockMode: false));

                Assert.Equal("no-device", scanner.DeviceId);
                Assert.Equal("NO_ADAPTER", scanner.VendorErrorCode);
            }
        }

        [Fact]
        public void UpdateScannerConfig_NullConfig_KeepsPreviousState()
        {
            Assert.Equal("no-device", _scanner.DeviceId);

            _scanner.UpdateScannerConfig(null);

            Assert.Equal("no-device", _scanner.DeviceId);
        }
    }
}
