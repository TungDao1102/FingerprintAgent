using System;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests
{
    public class SecuGenAdapterTests
    {
        [Fact]
        public void SecuGenAdapter_Initialize_ReturnsFalse_WhenNoDevice()
        {
            var adapter = new SecuGenAdapter();
            bool result = adapter.Initialize();
            Assert.False(result);
            Assert.False(adapter.IsConnected);
        }

        [Fact]
        public void SecuGenAdapter_Scan_ReturnsFail_WhenNotInitialized()
        {
            var adapter = new SecuGenAdapter();
            var result = adapter.Scan();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void SecuGenAdapter_VendorErrorCode_MapsErrorCode55()
        {
            var adapter = new SecuGenAdapter();
            adapter.Initialize();
            string errorCode = adapter.VendorErrorCode;
            Assert.Contains("DEVICE_NOT_FOUND", errorCode);
        }

        [Fact]
        public void SecuGenAdapter_DeviceId_AfterInitialize_IsPrefixed()
        {
            var adapter = new SecuGenAdapter();
            adapter.Initialize();
            Assert.StartsWith("SecuGen-", adapter.DeviceId);
        }

        [Fact]
        public void SecuGenAdapter_Model_IsNotEmpty()
        {
            var adapter = new SecuGenAdapter();
            adapter.Initialize();
            Assert.False(string.IsNullOrEmpty(adapter.Model));
        }

        [Fact]
        public void SecuGenAdapter_MimeType_IsImagePng()
        {
            var adapter = new SecuGenAdapter();
            Assert.Equal("image/png", adapter.MimeType);
        }
    }
}