using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests
{
    /// <summary>
    /// Unit tests for DigitalPersonaAdapter.
    /// Uses the stub implementation when DIGITALPERSONA_SDK_PRESENT is not defined (no vendor DLL).
    /// Tests marked with [Fact] are hardware-independent. Hardware tests use [Fact(Skip = "...")].
    /// </summary>
    public class DigitalPersonaAdapterTests
    {
        [Fact]
        public void DigitalPersonaAdapter_Implements_IScannerAdapter()
        {
            var adapter = new DigitalPersonaAdapter();
            Assert.IsAssignableFrom<IScannerAdapter>(adapter);
        }

        [Fact]
        public void DigitalPersonaAdapter_Initialize_ReturnsFalse_WhenNoDevice()
        {
            var adapter = new DigitalPersonaAdapter();
            bool result = adapter.Initialize();
            // Stub always returns false (no SDK present)
            Assert.False(result);
        }

        [Fact]
        public void DigitalPersonaAdapter_Scan_ReturnsFail_WhenNotInitialized()
        {
            var adapter = new DigitalPersonaAdapter();
            // Scan without Initialize — stub returns SCANNER_NOT_CONNECTED
            var result = adapter.Scan();
            Assert.False(result.IsSuccess);
            Assert.Contains("Stub adapter", result.ErrorMessage);
        }

        [Fact]
        public void DigitalPersonaAdapter_VendorErrorCode_DefaultsToNone()
        {
            var adapter = new DigitalPersonaAdapter();
            // Before any operation, VendorErrorCode should be "NONE"
            Assert.Equal("NONE", adapter.VendorErrorCode);
        }

        [Fact]
        public void DigitalPersonaAdapter_VendorErrorCode_UpdatesOnFailure()
        {
            var adapter = new DigitalPersonaAdapter();
            adapter.Initialize(); // Stub returns false, no device
            // Stub VendorErrorCode remains "NONE" on stub returns false
            Assert.Equal("NONE", adapter.VendorErrorCode);
        }

        [Fact]
        public void DigitalPersonaAdapter_MimeType_ReturnsImagePng()
        {
            var adapter = new DigitalPersonaAdapter();
            Assert.Equal("image/png", adapter.MimeType);
        }

        [Fact]
        public void DigitalPersonaAdapter_IsConnected_False_WhenNotInitialized()
        {
            var adapter = new DigitalPersonaAdapter();
            Assert.False(adapter.IsConnected);
        }

        [Fact]
        public void DigitalPersonaAdapter_DeviceId_StubValue()
        {
            var adapter = new DigitalPersonaAdapter();
            Assert.Equal("stub-device", adapter.DeviceId);
        }

        [Fact]
        public void DigitalPersonaAdapter_Model_StubValue()
        {
            var adapter = new DigitalPersonaAdapter();
            Assert.Equal("Digital Persona (stub)", adapter.Model);
        }
    }
}