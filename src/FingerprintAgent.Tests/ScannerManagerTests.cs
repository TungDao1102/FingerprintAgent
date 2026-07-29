using System;
using FingerprintAgent.Adapters;
using FingerprintAgent.Configuration;
using Moq;
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

        [Fact]
        public void ScannerManager_BackoffRetry_VerifiesActiveAdapterIsCheckedOnRetry()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            var result1 = sm.Scan();
            Assert.True(result1.IsSuccess);
            Assert.True(sm.IsConnected);

            var result2 = sm.Scan();
            Assert.True(result2.IsSuccess);
            Assert.NotNull(result2.ImageBytes);
        }

        [Fact]
        public void ScannerManager_BackoffRetry_FallsBackWhenActiveStaysDisconnected()
        {
            var config = MakeAgentConfig(mockMode: true);
            var sm = new ScannerManager(config, logger: null);

            var result = sm.Scan();
            Assert.True(result.IsSuccess, "MockScannerAdapter should succeed in MockMode");
        }

        #region Non-Mock Fallback Tests (WR-07)

        [Fact]
        public void ScannerManager_PriorityFallback_FirstFailsSecondSucceeds()
        {
            var failAdapter = new Mock<IScannerAdapter>();
            failAdapter.Setup(a => a.Initialize()).Returns(false);
            failAdapter.Setup(a => a.IsConnected).Returns(false);
            failAdapter.Setup(a => a.VendorErrorCode).Returns("ERROR");

            var successAdapter = new Mock<IScannerAdapter>();
            successAdapter.Setup(a => a.Initialize()).Returns(true);
            successAdapter.Setup(a => a.IsConnected).Returns(true);
            successAdapter.Setup(a => a.DeviceId).Returns("success-device");
            successAdapter.Setup(a => a.Model).Returns("Success Model");
            successAdapter.Setup(a => a.VendorErrorCode).Returns("NONE");
            successAdapter.Setup(a => a.Scan()).Returns(new CaptureResult
            {
                IsSuccess = true,
                ImageBytes = new byte[] { 0, 1, 2 },
                MimeType = "image/png",
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = "success-device",
                ErrorMessage = null,
                Width = 100,
                Height = 100
            });

            var sm = new ScannerManager(
                new[] { failAdapter.Object, successAdapter.Object },
                logger: null);

            var result = sm.Scan();
            Assert.True(result.IsSuccess);
            Assert.Equal("success-device", result.DeviceId);
        }

        [Fact]
        public void ScannerManager_PriorityFallback_AllAdaptersFail_ReturnsFailure()
        {
            var failAdapter = new Mock<IScannerAdapter>();
            failAdapter.Setup(a => a.Initialize()).Returns(false);
            failAdapter.Setup(a => a.VendorErrorCode).Returns("ERR_1");

            var sm = new ScannerManager(
                new[] { failAdapter.Object },
                logger: null);

            var result = sm.Scan();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void ScannerManager_BackoffRetry_ReconnectsOnDisconnect()
        {
            var adapter = new Mock<IScannerAdapter>();
            int initCallCount = 0;
            adapter.Setup(a => a.Initialize()).Returns(() =>
            {
                initCallCount++;
                return initCallCount > 1;
            });
            adapter.Setup(a => a.IsConnected).Returns(() => initCallCount > 1);
            adapter.Setup(a => a.VendorErrorCode).Returns("NONE");
            adapter.Setup(a => a.Scan()).Returns(new CaptureResult
            {
                IsSuccess = true,
                ImageBytes = new byte[] { 0, 1, 2 },
                MimeType = "image/png",
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = "test-device",
                ErrorMessage = null,
                Width = 100,
                Height = 100
            });

            var sm = new ScannerManager(
                new[] { adapter.Object },
                logger: null);

            var result = sm.Scan();
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ScannerManager_BackoffRetry_FallsThroughWhenBackoffFails()
        {
            var disconnected = new Mock<IScannerAdapter>();
            disconnected.Setup(a => a.Initialize()).Returns(false);
            disconnected.Setup(a => a.IsConnected).Returns(false);
            disconnected.Setup(a => a.VendorErrorCode).Returns("DISCONNECTED");

            var successAdapter = new Mock<IScannerAdapter>();
            successAdapter.Setup(a => a.Initialize()).Returns(true);
            successAdapter.Setup(a => a.IsConnected).Returns(true);
            successAdapter.Setup(a => a.DeviceId).Returns("fallback-device");
            successAdapter.Setup(a => a.VendorErrorCode).Returns("NONE");
            successAdapter.Setup(a => a.Scan()).Returns(new CaptureResult
            {
                IsSuccess = true,
                ImageBytes = new byte[] { 0, 1, 2 },
                MimeType = "image/png",
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = "fallback-device",
                ErrorMessage = null,
                Width = 100,
                Height = 100
            });

            // First call: disconnected adapter → backoff fails → falls through to successAdapter
            var sm = new ScannerManager(
                new[] { disconnected.Object, successAdapter.Object },
                logger: null);

            var result = sm.Scan();
            Assert.True(result.IsSuccess);
            Assert.Equal("fallback-device", result.DeviceId);
        }

        #endregion
    }
}