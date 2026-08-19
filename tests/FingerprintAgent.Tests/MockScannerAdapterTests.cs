using System.Threading.Tasks;
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
        public async Task Scan_ReturnsNonNullResult()
        {
            CaptureResult result = await _adapter.ScanAsync();
            Assert.NotNull(result);
        }

        [Fact]
        public async Task Scan_ReturnsValidPngHeader()
        {
            CaptureResult result = await _adapter.ScanAsync();
            byte[] pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            byte[] actualHeader = new byte[4];
            System.Array.Copy(result.ImageBytes, actualHeader, 4);
            Assert.Equal(pngHeader, actualHeader);
        }

        [Fact]
        public async Task Scan_VerificationDataIsBase64Sha256()
        {
            CaptureResult result = await _adapter.ScanAsync();
            // SHA-256 = 32 bytes → 44 characters in base64
            Assert.Equal(44, result.VerificationData.Length);
            // Verify it's valid base64
            byte[] decoded = System.Convert.FromBase64String(result.VerificationData);
            Assert.Equal(32, decoded.Length);
        }

        [Fact]
        public async Task Scan_DeviceIdIsMockScanner001()
        {
            CaptureResult result = await _adapter.ScanAsync();
            Assert.Equal("mock-scanner-001", result.DeviceId);
        }

        [Fact]
        public void Scan_IsConnectedIsTrue()
        {
            Assert.True(_adapter.IsConnected);
        }

        [Fact]
        public async Task Scan_MimeTypeIsImagePng()
        {
            CaptureResult result = await _adapter.ScanAsync();
            Assert.Equal("image/png", result.MimeType);
        }

        [Fact]
        public async Task Scan_IsDeterministic()
        {
            CaptureResult result1 = await _adapter.ScanAsync();
            CaptureResult result2 = await _adapter.ScanAsync();
            Assert.Equal(result1.VerificationData, result2.VerificationData);
        }

        [Fact]
        public async Task Scan_ImageDimensionsAre320x240()
        {
            CaptureResult result = await _adapter.ScanAsync();
            Assert.Equal(320, result.Width);
            Assert.Equal(240, result.Height);
        }
    }
}
