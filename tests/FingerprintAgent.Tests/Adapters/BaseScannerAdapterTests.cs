using System;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Adapters
{
    public class BaseScannerAdapterTests
    {
        // Minimal concrete subclass for exercising BaseScannerAdapter behavior.
        // 10x10 keeps PNG encoding fast while exercising the full pipeline.
        private class TestableAdapter : BaseScannerAdapter
        {
            public bool IsConnectedValue { get; set; } = true;
            public string DeviceIdValue { get; set; } = "test-device-001";
            public string ModelValue { get; set; } = "Test Model";
            public bool InitializeDeviceResult { get; set; } = true;
            public byte[] RawImageToReturn { get; set; }
            public Exception CaptureException { get; set; }

            public override bool IsConnected => IsConnectedValue;
            public override string DeviceId => DeviceIdValue;
            public override string Model => ModelValue;

            protected override int ImageWidth => 10;
            protected override int ImageHeight => 10;

            public override bool InitializeDevice() => InitializeDeviceResult;

            public override byte[] CaptureRawImage()
            {
                if (CaptureException != null)
                    throw CaptureException;
                return RawImageToReturn;
            }
        }

        [Fact]
        public void ProbeConnection_DefaultsToCachedIsConnected_ReturnsTrueWhenConnected()
        {
            // Arrange
            var adapter = new TestableAdapter { IsConnectedValue = true };

            // Act
            bool result = adapter.ProbeConnection();

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void ProbeConnection_DefaultsToCachedIsConnected_ReturnsFalseWhenDisconnected()
        {
            // Arrange
            var adapter = new TestableAdapter { IsConnectedValue = false };

            // Act
            bool result = adapter.ProbeConnection();

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void Initialize_DelegatesToInitializeDevice_ReturnsTrueWhenUnderlyingReturnsTrue()
        {
            // Arrange
            var adapter = new TestableAdapter { InitializeDeviceResult = true };

            // Act
            bool result = adapter.Initialize();

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void Initialize_DelegatesToInitializeDevice_ReturnsFalseWhenUnderlyingReturnsFalse()
        {
            // Arrange
            var adapter = new TestableAdapter { InitializeDeviceResult = false };

            // Act
            bool result = adapter.Initialize();

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void VendorErrorCode_DefaultsToNONE()
        {
            // Arrange
            var adapter = new TestableAdapter();

            // Act
            string errorCode = adapter.VendorErrorCode;

            // Assert
            Assert.Equal("NONE", errorCode);
        }

        [Fact]
        public async Task VendorErrorCode_AfterException_IsSet()
        {
            // Arrange
            var adapter = new TestableAdapter
            {
                CaptureException = new InvalidOperationException("device wedged")
            };

            // Act
            CaptureResult result = await adapter.ScanAsync();
            string errorCode = adapter.VendorErrorCode;

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal("device wedged", errorCode);
        }

        [Fact]
        public async Task ScanAsync_CancelledBeforeStart_ReturnsTimeoutFail()
        {
            // Arrange
            var adapter = new TestableAdapter();
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                // Act
                CaptureResult result = await adapter.ScanAsync(cts.Token);

                // Assert
                Assert.False(result.IsSuccess);
                Assert.Equal("CAPTURE_TIMEOUT", result.ErrorCode);
                Assert.Equal("CANCELLED", adapter.VendorErrorCode);
            }
        }

        [Fact]
        public async Task ScanAsync_RawImageNull_ReturnsCaptureError()
        {
            // Arrange
            var adapter = new TestableAdapter { RawImageToReturn = null };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal("CAPTURE_ERROR", result.ErrorCode);
            Assert.Equal("CAPTURE_RETURNED_EMPTY", adapter.VendorErrorCode);
        }

        [Fact]
        public async Task ScanAsync_RawImageEmpty_ReturnsCaptureError()
        {
            // Arrange
            var adapter = new TestableAdapter { RawImageToReturn = new byte[0] };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal("CAPTURE_ERROR", result.ErrorCode);
            Assert.Equal("CAPTURE_RETURNED_EMPTY", adapter.VendorErrorCode);
        }

        [Fact]
        public async Task ScanAsync_CaptureRawThrows_ReturnsCaptureErrorWithVendorMessage()
        {
            // Arrange
            var adapter = new TestableAdapter
            {
                CaptureException = new InvalidOperationException("sensor timeout")
            };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal("CAPTURE_ERROR", result.ErrorCode);
            Assert.Equal("sensor timeout", result.ErrorMessage);
            Assert.Equal("sensor timeout", adapter.VendorErrorCode);
        }

        [Fact]
        public async Task ScanAsync_ValidRawImage_ReturnsSuccessWithPng()
        {
            // Arrange
            byte[] raw = new byte[10 * 10];
            for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 256);
            byte[] expectedPngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            var adapter = new TestableAdapter { RawImageToReturn = raw };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.ImageBytes);
            Assert.True(result.ImageBytes.Length >= expectedPngSignature.Length);
            for (int i = 0; i < expectedPngSignature.Length; i++)
            {
                Assert.Equal(expectedPngSignature[i], result.ImageBytes[i]);
            }
        }

        [Fact]
        public async Task ScanAsync_ValidRawImage_PopulatesVerificationData()
        {
            // Arrange
            byte[] raw = new byte[10 * 10];
            for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 256);
            var adapter = new TestableAdapter { RawImageToReturn = raw };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert
            Assert.NotNull(result.VerificationData);
            Assert.Equal(44, result.VerificationData.Length);
            byte[] decoded = Convert.FromBase64String(result.VerificationData);
            Assert.Equal(32, decoded.Length);
        }

        [Fact]
        public async Task ScanAsync_ValidRawImage_SetsWidthAndHeightFromAbstractProperties()
        {
            // Arrange
            byte[] raw = new byte[10 * 10];
            var adapter = new TestableAdapter { RawImageToReturn = raw };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert
            Assert.Equal(10, result.Width);
            Assert.Equal(10, result.Height);
        }

        [Fact]
        public async Task ScanAsync_ValidRawImage_DeviceIdFromAbstractProperty()
        {
            // Arrange
            byte[] raw = new byte[10 * 10];
            var adapter = new TestableAdapter { RawImageToReturn = raw, DeviceIdValue = "ABC-XYZ-789" };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert
            Assert.Equal("ABC-XYZ-789", result.DeviceId);
        }

        [Fact]
        public async Task ScanAsync_PngIsGrayscale8bpp_ProducesReasonableBytesFor10x10()
        {
            // Arrange
            byte[] raw = new byte[10 * 10];
            var adapter = new TestableAdapter { RawImageToReturn = raw };

            // Act
            CaptureResult result = await adapter.ScanAsync();

            // Assert - a 10x10 grayscale PNG: PNG signature (8) + IHDR (25) +
            // IDAT (zlib-compressed scanlines) + IEND (12). Conservative lower bound.
            Assert.NotNull(result.ImageBytes);
            Assert.True(result.ImageBytes.Length > 50,
                $"Expected PNG > 50 bytes for 10x10 grayscale; got {result.ImageBytes.Length}");
            Assert.Equal("image/png", result.MimeType);
        }
    }
}
