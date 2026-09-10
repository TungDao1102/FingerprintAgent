using System;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    /// <summary>
    /// Unit tests for the ZKTeco vendor-code → coarse-API-code classification used
    /// by ScanAsync's acquire-failure path. Pure/static — no hardware or SDK needed.
    /// </summary>
    public class ZKTecoVendorCodeClassificationTests
    {
        [Theory]
        [InlineData(ZkNativeHost.ZKFP_ERR_INITLIB)]
        [InlineData(ZkNativeHost.ZKFP_ERR_INIT)]
        [InlineData(ZkNativeHost.ZKFP_ERR_NO_DEVICE)]
        [InlineData(ZkNativeHost.ZKFP_ERR_OPEN)]
        [InlineData(ZkNativeHost.ZKFP_ERR_INVALID_HANDLE)]
        [InlineData(ZkNativeHost.ZKFP_ERR_NOT_OPENED)]
        [InlineData(ZkNativeHost.ZKFP_ERR_NOT_INIT)]
        public void ClassifyAcquireFailure_ConnectionClassVendorCode_ReturnsScannerNotConnected(int vendorResult)
        {
            string code = ZKTecoAdapter.ClassifyAcquireFailure(vendorResult, budgetExhausted: false, cancelRequested: false);

            Assert.Equal("SCANNER_NOT_CONNECTED", code);
        }

        [Theory]
        [InlineData(ZkNativeHost.ZKFP_ERR_TIMEOUT)]
        [InlineData(ZkNativeHost.ZKFP_ERR_CANCEL)]
        public void ClassifyAcquireFailure_TimeoutClassVendorCode_ReturnsCaptureTimeout(int vendorResult)
        {
            string code = ZKTecoAdapter.ClassifyAcquireFailure(vendorResult, budgetExhausted: false, cancelRequested: false);

            Assert.Equal("CAPTURE_TIMEOUT", code);
        }

        [Fact]
        public void ClassifyAcquireFailure_NoFingerBudgetExhausted_ReturnsCaptureTimeout()
        {
            string code = ZKTecoAdapter.ClassifyAcquireFailure(ZkNativeHost.ZKFP_ERR_CAPTURE, budgetExhausted: true, cancelRequested: false);

            Assert.Equal("CAPTURE_TIMEOUT", code);
        }

        [Fact]
        public void ClassifyAcquireFailure_CancelRequested_ReturnsCaptureTimeout()
        {
            string code = ZKTecoAdapter.ClassifyAcquireFailure(ZkNativeHost.ZKFP_ERR_CAPTURE, budgetExhausted: false, cancelRequested: true);

            Assert.Equal("CAPTURE_TIMEOUT", code);
        }

        [Theory]
        [InlineData(ZkNativeHost.ZKFP_ERR_CAPTURE)]
        [InlineData(ZkNativeHost.ZKFP_ERR_BUSY)]
        [InlineData(ZkNativeHost.ZKFP_ERR_FAIL)]
        [InlineData(ZkNativeHost.ZKFP_ERR_MEMORY)]
        [InlineData(ZkNativeHost.ZKFP_ERR_EXTRACT_FP)]
        [InlineData(-99)]
        public void ClassifyAcquireFailure_UnmappedVendorCode_ReturnsCaptureFailed(int vendorResult)
        {
            string code = ZKTecoAdapter.ClassifyAcquireFailure(vendorResult, budgetExhausted: false, cancelRequested: false);

            Assert.Equal("CAPTURE_FAILED", code);
        }

        [Fact]
        public void ClassifyAcquireFailure_ConnectionClassWinsOverBudgetExhaustion()
        {
            string code = ZKTecoAdapter.ClassifyAcquireFailure(ZkNativeHost.ZKFP_ERR_NO_DEVICE, budgetExhausted: true, cancelRequested: false);

            Assert.Equal("SCANNER_NOT_CONNECTED", code);
        }
    }
}
