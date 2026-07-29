using System;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests
{
    /// <summary>
    /// Unit tests for ZKTecoAdapter.
    /// Note: Full integration testing requires either a physical ZKTeco device
    /// or the native libzkfpcsharp.dll. These tests verify compile-time interface
    /// compliance, property defaults, and error-handling paths without a device.
    /// </summary>
    public class ZKTecoAdapterTests
    {
        [Fact]
        public void ZKTecoAdapter_Implements_IScannerAdapter()
        {
            // Verify ZKTecoAdapter implements IScannerAdapter fully
            var adapter = new ZKTecoAdapter();
            Assert.IsAssignableFrom<IScannerAdapter>(adapter);
        }

        [Fact]
        public void ZKTecoAdapter_Initialize_SetsIsConnectedFalse_WhenNoDevice()
        {
            // When no ZKTeco device is connected (or native DLL unavailable),
            // Initialize() should return false and IsConnected should be false
            var adapter = new ZKTecoAdapter();
            try
            {
                bool result = adapter.Initialize();
                // Either: device found (unusual in test env) or device not found
                if (!result)
                {
                    Assert.False(adapter.IsConnected);
                    Assert.NotEqual("NONE", adapter.VendorErrorCode);
                }
            }
            catch (DllNotFoundException)
            {
                // Native DLL not present in test environment — Initialize throws DllNotFoundException.
                // This is expected behavior; adapter was never connected.
                Assert.False(adapter.IsConnected);
            }
        }

        [Fact]
        public void ZKTecoAdapter_Scan_ReturnsFail_WhenNotInitialized()
        {
            // Calling Scan() before Initialize() should return a failure CaptureResult
            var adapter = new ZKTecoAdapter();
            CaptureResult result = adapter.Scan();

            Assert.False(result.IsSuccess, "Scan() should return failure when adapter is not initialized");
            // ErrorMessage is the second arg to CaptureResult.Fail: "ZKTeco: not initialized"
            Assert.Equal("ZKTeco: not initialized", result.ErrorMessage);
        }

        [Fact]
        public void ZKTecoAdapter_VendorErrorCode_IsNone_BeforeInitialize()
        {
            // Before Initialize() is called, VendorErrorCode should be "NONE"
            var adapter = new ZKTecoAdapter();
            Assert.Equal("NONE", adapter.VendorErrorCode);
        }

        [Fact]
        public void ZKTecoAdapter_MimeType_IsImagePng()
        {
            var adapter = new ZKTecoAdapter();
            Assert.Equal("image/png", adapter.MimeType);
        }

        [Fact]
        public void ZKTecoAdapter_DeviceId_IsNonEmptyString()
        {
            var adapter = new ZKTecoAdapter();
            Assert.NotNull(adapter.DeviceId);
            Assert.NotEmpty(adapter.DeviceId);
        }

        [Fact]
        public void ZKTecoAdapter_Dispose_DoesNotThrow()
        {
            // Dispose should be safe to call regardless of initialization state
            var adapter = new ZKTecoAdapter();
            try { adapter.Initialize(); } catch { /* ignore — may fail without native DLL */ }

            var exception = Record.Exception(() => adapter.Dispose());
            Assert.Null(exception);

            // Second dispose should also be safe
            exception = Record.Exception(() => adapter.Dispose());
            Assert.Null(exception);
        }

        [Fact]
        public void ZKTecoAdapter_DeviceId_Property_IsAccessible()
        {
            // Verify the DeviceId property is readable without throwing
            var adapter = new ZKTecoAdapter();
            var deviceId = adapter.DeviceId;
            Assert.NotNull(deviceId);
        }
    }
}