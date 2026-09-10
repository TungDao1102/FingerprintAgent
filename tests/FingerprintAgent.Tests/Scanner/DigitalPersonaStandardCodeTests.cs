using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    /// <summary>
    /// Per-adapter map step: DigitalPersona vendor strings → the capture handler's
    /// standard errorCode vocabulary. Pure/static — no SDK or hardware needed.
    /// Regression guard for the P/Invoke port (DPUruNet wrapper removed 2026-09):
    /// the mapping must keep the DLL_NOT_FOUND → DRIVER_NOT_INSTALLED contract that
    /// the capture handler and ScanAsync paths rely on.
    /// </summary>
    public class DigitalPersonaStandardCodeTests
    {
        [Theory]
        [InlineData("DLL_NOT_FOUND")]
        public void ToStandardErrorCode_DriverLoadFailure_ReturnsDriverNotInstalled(string vendorCode)
        {
            Assert.Equal("DRIVER_NOT_INSTALLED", DigitalPersonaAdapter.ToStandardErrorCode(vendorCode, "CAPTURE_ERROR"));
        }

        [Theory]
        [InlineData("DEVICE_NOT_FOUND")]
        [InlineData("DEVICE_NOT_CAPTURING")]
        public void ToStandardErrorCode_ConnectionClassVendorCode_ReturnsScannerNotConnected(string vendorCode)
        {
            Assert.Equal("SCANNER_NOT_CONNECTED", DigitalPersonaAdapter.ToStandardErrorCode(vendorCode, "CAPTURE_ERROR"));
        }

        [Theory]
        [InlineData("DEVICE_IN_USE")]
        [InlineData("NO_FINGER")]
        [InlineData("QUALITY_NOT_GOOD")]
        [InlineData("FAIL")]
        [InlineData("NOT_INITIALIZED")]
        [InlineData(null)]
        public void ToStandardErrorCode_UnmappedVendorCode_PassesThroughCoarse(string vendorCode)
        {
            Assert.Equal("CAPTURE_ERROR", DigitalPersonaAdapter.ToStandardErrorCode(vendorCode, "CAPTURE_ERROR"));
        }

        [Fact]
        public void ToStandardErrorCode_TimeoutCoarseCode_PassesThrough()
        {
            // ScanAsync emits timeout directly as the coarse code (not a vendor string),
            // so the mapper must leave it untouched.
            Assert.Equal("CAPTURE_TIMEOUT", DigitalPersonaAdapter.ToStandardErrorCode("CAPTURE_TIMEOUT", "CAPTURE_TIMEOUT"));
        }
    }
}
