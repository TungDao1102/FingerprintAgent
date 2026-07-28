using Xunit;
using FingerprintAgent.Adapters;

namespace FingerprintAgent.Tests
{
    public class MockScannerAdapterTests
    {
        private readonly MockScannerAdapter _adapter;

        public MockScannerAdapterTests()
        {
            _adapter = new MockScannerAdapter();
        }

        [Fact]
        public void Scan_ReturnsNonNullResult()
        {
            CaptureResult result = _adapter.Scan();
            Assert.NotNull(result);
        }

        [Fact]
        public void Scan_ReturnsValidPngHeader()
        {
            CaptureResult result = _adapter.Scan();
            byte[] pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            byte[] actualHeader = new byte[4];
            System.Array.Copy(result.ImageBytes, actualHeader, 4);
            Assert.Equal(pngHeader, actualHeader);
        }

        [Fact]
        public void Scan_VerificationDataIsBase64Sha256()
        {
            CaptureResult result = _adapter.Scan();
            // SHA-256 = 32 bytes → 44 characters in base64
            Assert.Equal(44, result.VerificationData.Length);
            // Verify it's valid base64
            byte[] decoded = System.Convert.FromBase64String(result.VerificationData);
            Assert.Equal(32, decoded.Length);
        }

        [Fact]
        public void Scan_DeviceIdIsMockScanner001()
        {
            CaptureResult result = _adapter.Scan();
            Assert.Equal("mock-scanner-001", result.DeviceId);
        }

        [Fact]
        public void Scan_IsConnectedIsTrue()
        {
            Assert.True(_adapter.IsConnected);
        }

        [Fact]
        public void Scan_MimeTypeIsImagePng()
        {
            CaptureResult result = _adapter.Scan();
            Assert.Equal("image/png", result.MimeType);
        }

        [Fact]
        public void Scan_IsDeterministic()
        {
            CaptureResult result1 = _adapter.Scan();
            CaptureResult result2 = _adapter.Scan();
            Assert.Equal(result1.VerificationData, result2.VerificationData);
        }

        [Fact]
        public void Scan_ImageDimensionsAre320x240()
        {
            CaptureResult result = _adapter.Scan();
            Assert.Equal(320, result.Width);
            Assert.Equal(240, result.Height);
        }
    }
}
