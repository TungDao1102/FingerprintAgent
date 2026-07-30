using System;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests
{
    public class ScannerManagerExponentialBackoffTests
    {
        [Fact]
        public void BackoffStep_StartsAtZero()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false,
                ScanResult = CaptureResult.Fail("SCANNER_NOT_CONNECTED", "not connected")
            };
            var manager = new ScannerManager(new[] { mock }, null);

            Assert.Equal(0, manager.BackoffStep);
            Assert.False(manager.InBackoff);
        }

        [Fact]
        public void BackoffStep_IncrementsAfterAllAdapterFailure()
        {
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var manager = new ScannerManager(new[] { failing }, null);

            manager.Scan();
            Assert.Equal(1, manager.BackoffStep);

            manager.Scan();
            Assert.Equal(2, manager.BackoffStep);
        }

        [Fact]
        public void BackoffStep_CapsAtThree()
        {
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var manager = new ScannerManager(new[] { failing }, null);

            for (int i = 0; i < 10; i++)
                manager.Scan();

            Assert.Equal(3, manager.BackoffStep);
        }

        [Fact]
        public void BackoffStep_ResetsOnSuccessfulCapture()
        {
            var alwaysFailing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var eventuallySucceeding = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var manager = new ScannerManager(new[] { alwaysFailing, eventuallySucceeding }, null);

            manager.Scan();
            manager.Scan();
            manager.Scan();
            Assert.Equal(0, manager.BackoffStep);
        }

        [Fact]
        public void InBackoff_IsFalseWhenStepIsZero()
        {
            var succeeding = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var manager = new ScannerManager(new[] { succeeding }, null);

            Assert.Equal(0, manager.BackoffStep);
            Assert.False(manager.InBackoff);
        }

        [Fact]
        public void InFlight_FailsImmediately_WhenScannerDisconnects()
        {
            var disconnecting = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var manager = new ScannerManager(new[] { disconnecting }, null);

            var result1 = manager.Scan();
            Assert.True(result1.IsSuccess);
            Assert.Equal(0, manager.BackoffStep);

            disconnecting.IsConnectedValue = false;
            disconnecting.InitializeResult = false;
            disconnecting.ScanResult = CaptureResult.Fail("SCANNER_NOT_CONNECTED", "disconnected");

            var result2 = manager.Scan();
            Assert.False(result2.IsSuccess);
            Assert.Equal("SCANNER_NOT_CONNECTED", result2.ErrorCode);
            Assert.Equal(1, manager.BackoffStep);
        }
    }
}