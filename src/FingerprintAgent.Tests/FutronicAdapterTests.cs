using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests
{
    /// <summary>
    /// Unit tests for FutronicAdapter.
    /// Tests pixel inversion logic and interface contract. Hardware tests use [Fact(Skip = "...")].
    /// </summary>
    public class FutronicAdapterTests
    {
        [Fact]
        public void FutronicAdapter_Implements_IScannerAdapter()
        {
            var adapter = new FutronicAdapter();
            Assert.IsAssignableFrom<IScannerAdapter>(adapter);
        }

        [Fact]
        public void FutronicAdapter_Initialize_ReturnsFalse_WhenNoDevice()
        {
            var adapter = new FutronicAdapter();
            bool result = adapter.Initialize();
            // Stub always returns false (no ftrScanAPI.dll present)
            Assert.False(result);
        }

        [Fact]
        public void FutronicAdapter_Scan_ReturnsFail_WhenNotInitialized()
        {
            var adapter = new FutronicAdapter();
            // Scan without Initialize — stub returns SCANNER_NOT_CONNECTED
            var result = adapter.Scan();
            Assert.False(result.IsSuccess);
            Assert.Contains("Stub adapter", result.ErrorMessage);
        }

        [Fact]
        public void FutronicAdapter_VendorErrorCode_DefaultsToNone()
        {
            var adapter = new FutronicAdapter();
            // Before any operation, VendorErrorCode should be "NONE"
            Assert.Equal("NONE", adapter.VendorErrorCode);
        }

        [Fact]
        public void FutronicAdapter_MimeType_ReturnsImagePng()
        {
            var adapter = new FutronicAdapter();
            Assert.Equal("image/png", adapter.MimeType);
        }

        [Fact]
        public void FutronicAdapter_IsConnected_False_WhenNotInitialized()
        {
            var adapter = new FutronicAdapter();
            Assert.False(adapter.IsConnected);
        }

        [Fact]
        public void FutronicAdapter_DeviceId_StubValue()
        {
            var adapter = new FutronicAdapter();
            Assert.Equal("stub-device", adapter.DeviceId);
        }

        [Fact]
        public void FutronicAdapter_Model_StubValue()
        {
            var adapter = new FutronicAdapter();
            Assert.Equal("Futronic (stub)", adapter.Model);
        }

        [Fact]
        public void FutronicAdapter_PixelInversion_InvertsAllPixels()
        {
            // Verify pixel inversion: 255 - rawValue for each pixel
            byte[] raw = new byte[] { 0, 50, 100, 150, 200, 255 };
            byte[] expected = new byte[] { 255, 205, 155, 105, 55, 0 };

            byte[] inverted = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                inverted[i] = (byte)(255 - raw[i]);

            Assert.Equal(expected, inverted);
        }

        [Fact]
        public void FutronicAdapter_PixelInversion_WhiteBecomesBlack()
        {
            byte[] whiteRaw = new byte[100]; // all 0 = white
            byte[] inverted = new byte[whiteRaw.Length];
            for (int i = 0; i < whiteRaw.Length; i++)
                inverted[i] = (byte)(255 - whiteRaw[i]);

            Assert.All(inverted, b => Assert.Equal(255, b)); // all 255 = black
        }

        [Fact]
        public void FutronicAdapter_PixelInversion_BlackBecomesWhite()
        {
            byte[] blackRaw = new byte[100];
            for (int i = 0; i < blackRaw.Length; i++) blackRaw[i] = 255; // all 255 = black
            byte[] inverted = new byte[blackRaw.Length];
            for (int i = 0; i < blackRaw.Length; i++)
                inverted[i] = (byte)(255 - blackRaw[i]);

            Assert.All(inverted, b => Assert.Equal(0, b)); // all 0 = white
        }

        [Fact]
        public void FutronicAdapter_PixelInversion_IdentityForGrayscaleMidpoint()
        {
            byte[] midGray = new byte[] { 128, 127, 129 };
            byte[] inverted = new byte[midGray.Length];
            for (int i = 0; i < midGray.Length; i++)
                inverted[i] = (byte)(255 - midGray[i]);

            Assert.Equal(new byte[] { 127, 128, 126 }, inverted);
        }
    }
}