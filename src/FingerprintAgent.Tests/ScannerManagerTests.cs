using System;
using FingerprintAgent.Adapters;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests
{
    public class ScannerManagerTests
    {
        private static ScannerConfig MakeScannerConfig(bool mockMode = false, string[] priority = null)
        {
            return new ScannerConfig
            {
                MockMode = mockMode,
                Priority = priority ?? new[] { "SecuGen", "DigitalPersona", "Futronic", "ZKTeco" }
            };
        }

        private static AgentConfig MakeAgentConfig(bool mockMode = false, string[] priority = null)
        {
            return new AgentConfig
            {
                Scanner = MakeScannerConfig(mockMode, priority)
            };
        }

        [Fact]
        public void ScannerManager_ExposesActiveAdapterProperties_InMockMode()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            Assert.Equal("mock-scanner-001", sm.DeviceId);
            Assert.Equal("Mock Scanner v1.0", sm.Model);
            Assert.True(sm.IsConnected);
            Assert.Equal("MOCK", sm.VendorErrorCode);
        }

        [Fact]
        public void ScannerManager_RespectsMockMode_ReturnsSuccess()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            var result = sm.Scan();

            Assert.True(result.IsSuccess, "MockMode should always return success");
            Assert.NotNull(result.ImageBytes);
            Assert.Equal("image/png", result.MimeType);
            Assert.Equal("mock-scanner-001", result.DeviceId);
        }

        [Fact]
        public void ScannerManager_VendorErrorCode_IsMOCK_InMockMode()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            Assert.Equal("MOCK", sm.VendorErrorCode);
        }

        [Fact]
        public void ScannerManager_MimeType_ReturnsImagePng()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            Assert.Equal("image/png", sm.MimeType);
        }

        [Fact]
        public void ScannerManager_Initialize_ReturnsTrue()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            Assert.True(sm.Initialize());
        }

        [Fact]
        public void ScannerManager_ThrowsOnUnknownVendor()
        {
            var config = MakeAgentConfig();
            config.Scanner.Priority = new[] { "UnknownVendor" };
            config.Scanner.MockMode = false;

            var ex = Assert.Throws<InvalidOperationException>(() => new ScannerManager(config, logger: null));
            Assert.Contains("Unknown scanner vendor", ex.Message);
            Assert.Contains("UnknownVendor", ex.Message);
        }

        [Fact]
        public void ScannerManager_MockMode_DoesNotThrow_OnUnknownVendor()
        {
            var config = MakeAgentConfig(mockMode: true);
            config.Scanner.Priority = new[] { "NonExistent" };

            var sm = new ScannerManager(config, logger: null);
            var result = sm.Scan();

            Assert.True(result.IsSuccess);
            Assert.Equal("MOCK", sm.VendorErrorCode);
        }

        [Fact]
        public void ScannerManager_MockMode_ImageBytes_ArePNG()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            var result = sm.Scan();

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.ImageBytes);
            Assert.True(result.ImageBytes.Length > 0);
            Assert.Null(result.ErrorMessage);
        }

        [Fact]
        public void ScannerManager_MockMode_ScanResult_HasVerificationData()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            var result = sm.Scan();

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.VerificationData);
        }
    }
}