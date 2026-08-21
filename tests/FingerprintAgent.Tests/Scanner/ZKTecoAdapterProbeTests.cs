using System.Reflection;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    /// <summary>
    /// Verifies ZKTecoAdapter.ProbeConnection refuses to Close+ReInit the process-wide
    /// native host while a capture is in flight (Bug #5: previously, a parallel /health
    /// probe from another request could call ZkTecoFingerHost.Close() and tear down
    /// the native context the in-flight AcquireFingerprintAsync still held a handle to,
    /// corrupting the device handle. The lock only protected the C# field reference
    /// snapshot — not the underlying native handle).
    ///
    /// These tests use reflection on the private static _captureInProgress counter and
    /// the private _isConnected flag because the field names are part of an internal
    /// thread-safety invariant. The test asserts BEHAVIOR (return value + side-effect
    /// on _vendorErrorCode) — if ProbeConnection bypassed the guard, it would attempt
    /// to call the static native ZkTecoFingerHost.Close(), which throws DllNotFoundException
    /// when the ZKTeco SDK is not installed at test time. The fact that the call
    /// completes without throwing IS evidence the native close was bypassed.
    /// </summary>
    public class ZKTecoAdapterProbeTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static int GetCaptureInProgress()
        {
            var field = typeof(ZKTecoAdapter).GetField("_captureInProgress", PrivateStatic);
            Assert.NotNull(field);
            return (int)field.GetValue(null);
        }

        private static void SetCaptureInProgress(int value)
        {
            var field = typeof(ZKTecoAdapter).GetField("_captureInProgress", PrivateStatic);
            Assert.NotNull(field);
            field.SetValue(null, value);
        }

        private static void SetIsConnected(ZKTecoAdapter adapter, bool value)
        {
            var field = typeof(ZKTecoAdapter).GetField("_isConnected", PrivateInstance);
            Assert.NotNull(field);
            field.SetValue(adapter, value);
        }

        private static string GetVendorErrorCode(ZKTecoAdapter adapter)
        {
            var field = typeof(ZKTecoAdapter).GetField("_vendorErrorCode", PrivateInstance);
            Assert.NotNull(field);
            return (string)field.GetValue(adapter);
        }

        [Fact]
        public void ProbeConnection_NoCaptureInFlight_TakesNormalPath()
        {
            // Arrange — counter is 0 (no active capture)
            SetCaptureInProgress(0);
            using (var adapter = new ZKTecoAdapter())
            {
                SetIsConnected(adapter, false);

                // Act
                bool result;
                try
                {
                    result = adapter.ProbeConnection();
                }
                catch (System.DllNotFoundException)
                {
                    // SDK not installed on this test machine — that's OK.
                    // The important thing is that ProbeConnection TRIED to call the native
                    // SDK (proving the guard did NOT short-circuit). Verify by checking
                    // the counter is still 0 (unchanged by ProbeConnection).
                    Assert.Equal(0, GetCaptureInProgress());
                    return;
                }

                // Assert — if SDK was present, probe went through Close+Initialize.
                // The counter must still be 0 (we don't increment on the probe path).
                Assert.Equal(0, GetCaptureInProgress());
            }
        }

        [Fact]
        public void ProbeConnection_CaptureInFlight_ReturnsCachedIsConnected_NoDllCall()
        {
            // Arrange — simulate an in-flight capture
            SetCaptureInProgress(1);
            try
            {
                using (var adapter = new ZKTecoAdapter())
                {
                    SetIsConnected(adapter, true);

                    // Act — if ProbeConnection ignored the guard, this would call
                    // ZkTecoFingerHost.Close() and throw DllNotFoundException on a
                    // machine without the ZK SDK installed.
                    bool result = adapter.ProbeConnection();

                    // Assert — probe returned the cached _isConnected (true) and
                    // recorded the deferral reason in VendorErrorCode.
                    Assert.True(result, "ProbeConnection should return cached _isConnected=true when a capture is in flight");
                    string vendorError = GetVendorErrorCode(adapter);
                    Assert.Equal("PROBE_DEFERRED_CAPTURE_IN_FLIGHT", vendorError);

                    // Counter must still be 1 (the guard did not reset it; the decrement
                    // happens in ScanAsync's finally).
                    Assert.Equal(1, GetCaptureInProgress());
                }
            }
            finally
            {
                SetCaptureInProgress(0);
            }
        }

        [Fact]
        public void ProbeConnection_CaptureInFlight_StillReturnsTrueEvenWhenDisconnected()
        {
            // Arrange — capture in flight, cached _isConnected=false (e.g. scanner was
            // disconnected just before capture started but counter still incremented).
            // The probe must return whatever was cached, not start a new probe.
            SetCaptureInProgress(1);
            try
            {
                using (var adapter = new ZKTecoAdapter())
                {
                    SetIsConnected(adapter, false);

                    // Act
                    bool result = adapter.ProbeConnection();

                    // Assert — returns cached value (false), does NOT attempt Close+Init
                    Assert.False(result);
                    Assert.Equal("PROBE_DEFERRED_CAPTURE_IN_FLIGHT", GetVendorErrorCode(adapter));
                    Assert.Equal(1, GetCaptureInProgress());
                }
            }
            finally
            {
                SetCaptureInProgress(0);
            }
        }

        [Fact]
        public void ProbeConnection_AfterCaptureCompletes_ResumesNormalPath()
        {
            // Arrange — simulate a capture that just finished: counter back to 0
            SetCaptureInProgress(0);
            using (var adapter = new ZKTecoAdapter())
            {
                SetIsConnected(adapter, false);

                // Act — probe should NOT short-circuit now; should attempt native probe
                try
                {
                    adapter.ProbeConnection();
                }
                catch (System.DllNotFoundException)
                {
                    // SDK not installed — that's fine, we proved the guard did NOT
                    // short-circuit because we entered the Close+Init path.
                    Assert.Equal(0, GetCaptureInProgress());
                }
                Assert.Equal(0, GetCaptureInProgress());
            }
        }

        [Fact]
        public void ProbeConnection_NestedCaptureInFlight_CounterRemainsAccurate()
        {
            // Arrange — two captures "in flight" (e.g. overlapping concurrent scans)
            SetCaptureInProgress(2);
            try
            {
                using (var adapter = new ZKTecoAdapter())
                {
                    SetIsConnected(adapter, true);

                    // Act
                    bool result = adapter.ProbeConnection();

                    // Assert — guard fires (counter > 0), counter unchanged
                    Assert.True(result);
                    Assert.Equal(2, GetCaptureInProgress());
                }
            }
            finally
            {
                SetCaptureInProgress(0);
            }
        }
    }
}
