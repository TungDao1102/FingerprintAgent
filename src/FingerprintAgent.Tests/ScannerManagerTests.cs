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

        [Fact]
        public void ScannerManager_ScanReturnsDeviceScanFailure_DoesNotFallThrough()
        {
            var connectedAdapter = new Mock<IScannerAdapter>();
            connectedAdapter.Setup(a => a.Initialize()).Returns(true);
            connectedAdapter.Setup(a => a.IsConnected).Returns(true);
            connectedAdapter.Setup(a => a.DeviceId).Returns("zk9500");
            connectedAdapter.Setup(a => a.VendorErrorCode).Returns("NONE");
            connectedAdapter.Setup(a => a.Scan()).Returns(CaptureResult.Fail("CAPTURE_FAILED", "no finger detected"));

            var backupAdapter = new Mock<IScannerAdapter>();
            backupAdapter.Setup(a => a.Initialize()).Returns(true);
            backupAdapter.Setup(a => a.IsConnected).Returns(true);
            backupAdapter.Setup(a => a.DeviceId).Returns("backup");
            backupAdapter.Setup(a => a.VendorErrorCode).Returns("NONE");
            backupAdapter.Setup(a => a.Scan()).Returns(new CaptureResult
            {
                IsSuccess = true,
                ImageBytes = new byte[] { 0xFF },
                MimeType = "image/png",
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = "backup",
                ErrorMessage = null,
                Width = 100,
                Height = 100
            });

            var sm = new ScannerManager(
                new[] { connectedAdapter.Object, backupAdapter.Object },
                logger: null);

            var result = sm.Scan();

            Assert.False(result.IsSuccess, "primary adapter's scan failure must be returned, not fall-through success");
            Assert.Equal("CAPTURE_FAILED", result.ErrorCode);
            backupAdapter.Verify(a => a.Initialize(), Times.Never, "must not try backup when primary is connected");
        }

        #endregion

        #region TryProbe Tests (D-13: /health active probe)

        [Fact]
        public void TryProbe_ReturnsTrue_WithFirstSuccessfulAdapter()
        {
            var adapter1 = new Mock<IScannerAdapter>();
            adapter1.Setup(a => a.Initialize()).Returns(false);
            adapter1.Setup(a => a.VendorErrorCode).Returns("ERROR_A");

            var adapter2 = new Mock<IScannerAdapter>();
            adapter2.Setup(a => a.Initialize()).Returns(true);
            adapter2.Setup(a => a.DeviceId).Returns("dev-2");
            adapter2.Setup(a => a.Model).Returns("Model 2");
            adapter2.Setup(a => a.VendorErrorCode).Returns("NONE");

            var sm = new ScannerManager(new[] { adapter1.Object, adapter2.Object }, logger: null);

            string deviceId, model, vendorErrorCode;
            bool result = sm.TryProbe(out deviceId, out model, out vendorErrorCode);

            Assert.True(result);
            Assert.Equal("dev-2", deviceId);
            Assert.Equal("Model 2", model);
            Assert.Equal("NONE", vendorErrorCode);
            adapter1.Verify(a => a.Initialize(), Times.Once);
            adapter2.Verify(a => a.Initialize(), Times.Once);
        }

        [Fact]
        public void TryProbe_ReturnsFalse_WhenAllAdaptersFailToInitialize()
        {
            var adapter1 = new Mock<IScannerAdapter>();
            adapter1.Setup(a => a.Initialize()).Returns(false);
            adapter1.Setup(a => a.VendorErrorCode).Returns("ERROR_A");

            var adapter2 = new Mock<IScannerAdapter>();
            adapter2.Setup(a => a.Initialize()).Returns(false);
            adapter2.Setup(a => a.VendorErrorCode).Returns("ERROR_B");

            var sm = new ScannerManager(new[] { adapter1.Object, adapter2.Object }, logger: null);

            string deviceId, model, vendorErrorCode;
            bool result = sm.TryProbe(out deviceId, out model, out vendorErrorCode);

            Assert.False(result);
            Assert.Equal("no-device", deviceId);
            Assert.Equal("no-device", model);
            Assert.Equal("ERROR_B", vendorErrorCode);
        }

        [Fact]
        public void TryProbe_ReturnsNoDevice_WhenAdaptersArrayEmpty()
        {
            var sm = new ScannerManager(new IScannerAdapter[0], logger: null);

            string deviceId, model, vendorErrorCode;
            bool result = sm.TryProbe(out deviceId, out model, out vendorErrorCode);

            Assert.False(result);
            Assert.Equal("no-device", deviceId);
            Assert.Equal("NONE", vendorErrorCode);
        }

        [Fact]
        public void TryProbe_FastPath_UsesCachedAdapter_OnSecondCall()
        {
            var adapter = new Mock<IScannerAdapter>();
            adapter.Setup(a => a.Initialize()).Returns(true);
            adapter.Setup(a => a.IsConnected).Returns(true);
            adapter.Setup(a => a.DeviceId).Returns("cached-dev");
            adapter.Setup(a => a.Model).Returns("Cached Model");
            adapter.Setup(a => a.VendorErrorCode).Returns("NONE");

            var sm = new ScannerManager(new[] { adapter.Object }, logger: null);

            string d1, m1, v1;
            sm.TryProbe(out d1, out m1, out v1);
            adapter.Verify(a => a.Initialize(), Times.Once);

            string d2, m2, v2;
            sm.TryProbe(out d2, out m2, out v2);
            adapter.Verify(a => a.Initialize(), Times.Once);

            Assert.Equal(d1, d2);
            Assert.Equal(m1, m2);
        }

        [Fact]
        public void TryProbe_FallsBackToColdPath_WhenCachedAdapterDisconnects()
        {
            bool isConnected = true;
            var adapter = new Mock<IScannerAdapter>();
            adapter.Setup(a => a.Initialize()).Returns(true);
            adapter.Setup(a => a.IsConnected).Returns(() => isConnected);
            adapter.Setup(a => a.DeviceId).Returns("dev");
            adapter.Setup(a => a.Model).Returns("M");
            adapter.Setup(a => a.VendorErrorCode).Returns("NONE");

            var sm = new ScannerManager(new[] { adapter.Object }, logger: null);

            string d, m, v;
            sm.TryProbe(out d, out m, out v);
            adapter.Verify(a => a.Initialize(), Times.Once);

            isConnected = false;

            sm.TryProbe(out d, out m, out v);
            adapter.Verify(a => a.Initialize(), Times.Exactly(2));
        }

        [Fact]
        public void TryProbe_PromotesAdapterToActive_OnSuccess()
        {
            var adapter = new Mock<IScannerAdapter>();
            adapter.Setup(a => a.Initialize()).Returns(true);
            adapter.Setup(a => a.IsConnected).Returns(true);
            adapter.Setup(a => a.DeviceId).Returns("promoted");
            adapter.Setup(a => a.VendorErrorCode).Returns("NONE");

            var sm = new ScannerManager(new[] { adapter.Object }, logger: null);

            Assert.False(sm.IsConnected, "precondition: no ActiveAdapter");

            string d, m, v;
            sm.TryProbe(out d, out m, out v);

            Assert.True(sm.IsConnected, "after successful probe, ActiveAdapter should be cached");
            Assert.Equal("promoted", sm.DeviceId);
        }

        [Fact]
        public void TryProbe_DoesNotPromoteAdapter_OnAllFailures()
        {
            var adapter = new Mock<IScannerAdapter>();
            adapter.Setup(a => a.Initialize()).Returns(false);
            adapter.Setup(a => a.IsConnected).Returns(false);
            adapter.Setup(a => a.VendorErrorCode).Returns("ERROR_INIT");

            var sm = new ScannerManager(new[] { adapter.Object }, logger: null);

            string d, m, v;
            sm.TryProbe(out d, out m, out v);

            Assert.False(sm.IsConnected);
            Assert.Equal("no-device", sm.DeviceId);
        }

        [Fact]
        public void TryProbe_ContinuesToNextAdapter_WhenOneThrowsException()
        {
            var faultyAdapter = new Mock<IScannerAdapter>();
            faultyAdapter.Setup(a => a.Initialize()).Throws(new InvalidOperationException("SDK crash"));

            var goodAdapter = new Mock<IScannerAdapter>();
            goodAdapter.Setup(a => a.Initialize()).Returns(true);
            goodAdapter.Setup(a => a.DeviceId).Returns("survivor");
            goodAdapter.Setup(a => a.Model).Returns("Survivor Model");
            goodAdapter.Setup(a => a.VendorErrorCode).Returns("NONE");

            var sm = new ScannerManager(new[] { faultyAdapter.Object, goodAdapter.Object }, logger: null);

            string deviceId, model, vendorErrorCode;
            bool result = sm.TryProbe(out deviceId, out model, out vendorErrorCode);

            Assert.True(result, "must not let one adapter's exception abort the probe");
            Assert.Equal("survivor", deviceId);
        }

        [Fact]
        public void TryProbe_DoesNotIncrementBackoff_OnProbeFailure()
        {
            var adapter = new Mock<IScannerAdapter>();
            adapter.Setup(a => a.Initialize()).Returns(false);
            adapter.Setup(a => a.VendorErrorCode).Returns("ERROR");

            var sm = new ScannerManager(new[] { adapter.Object }, logger: null);

            int initialBackoff = sm.BackoffStep;
            bool initialInBackoff = sm.InBackoff;

            for (int i = 0; i < 5; i++)
            {
                string d, m, v;
                sm.TryProbe(out d, out m, out v);
            }

            Assert.Equal(initialBackoff, sm.BackoffStep);
            Assert.Equal(initialInBackoff, sm.InBackoff);
        }

        [Fact]
        public void TryProbe_DoesNotIncrementBackoff_OnProbeSuccess()
        {
            var adapter = new Mock<IScannerAdapter>();
            adapter.Setup(a => a.Initialize()).Returns(true);
            adapter.Setup(a => a.IsConnected).Returns(true);
            adapter.Setup(a => a.DeviceId).Returns("dev");
            adapter.Setup(a => a.VendorErrorCode).Returns("NONE");

            var sm = new ScannerManager(new[] { adapter.Object }, logger: null);

            int initialBackoff = sm.BackoffStep;

            for (int i = 0; i < 5; i++)
            {
                string d, m, v;
                sm.TryProbe(out d, out m, out v);
            }

            Assert.Equal(initialBackoff, sm.BackoffStep);
            Assert.False(sm.InBackoff);
        }

        #endregion
    }
}