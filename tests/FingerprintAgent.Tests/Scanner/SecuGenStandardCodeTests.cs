using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    /// <summary>
    /// Per-adapter map step: SecuGen SDK vendor strings → the capture handler's
    /// standard errorCode vocabulary. Pure/static — no SDK or hardware needed.
    /// </summary>
    public class SecuGenStandardCodeTests
    {
        [Theory]
        [InlineData("DLL_NOT_FOUND")]
        [InlineData("ERROR_DLLLOAD_FAILED")]
        [InlineData("ERROR_DLLLOAD_FAILED_DRV")]
        [InlineData("ERROR_DLLLOAD_FAILED_ALGO")]
        [InlineData("ERROR_DRVLOAD_FAILED")]
        public void ToStandardErrorCode_DriverLoadFailure_ReturnsDriverNotInstalled(string vendorCode)
        {
            Assert.Equal("DRIVER_NOT_INSTALLED", SecuGenAdapter.ToStandardErrorCode(vendorCode, "CAPTURE_ERROR"));
        }

        [Theory]
        [InlineData("ERROR_DEVICE_NOT_FOUND", "SCANNER_NOT_CONNECTED")]
        [InlineData("ERROR_TIME_OUT", "CAPTURE_TIMEOUT")]
        [InlineData("ERROR_DEV_ALREADY_OPEN", "CAPTURE_ERROR")]
        [InlineData("ERROR_WRONG_IMAGE", "CAPTURE_ERROR")]
        [InlineData(null, "CAPTURE_ERROR")]
        public void ToStandardErrorCode_MapsOrPassesThroughCoarse(string vendorCode, string expected)
        {
            Assert.Equal(expected, SecuGenAdapter.ToStandardErrorCode(vendorCode, "CAPTURE_ERROR"));
        }
    }
}
