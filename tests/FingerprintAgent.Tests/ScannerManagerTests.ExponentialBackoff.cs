using System;
using System.Threading.Tasks;
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
        public async Task BackoffStep_IncrementsAfterAllAdapterFailure()
        {
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var manager = new ScannerManager(new[] { failing }, null);

            await manager.ScanAsync();
            Assert.Equal(1, manager.BackoffStep);

            await manager.ScanAsync();
            Assert.Equal(2, manager.BackoffStep);
        }

        [Fact]
        public async Task BackoffStep_CapsAtThree()
        {
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var manager = new ScannerManager(new[] { failing }, null);

            for (int i = 0; i < 10; i++)
                await manager.ScanAsync();

            Assert.Equal(3, manager.BackoffStep);
        }

        [Fact]
        public async Task BackoffStep_NotAffected_WhenCapturesAlwaysSucceed()
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

            await manager.ScanAsync();
            await manager.ScanAsync();
            await manager.ScanAsync();
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
        public async Task InFlight_FailsImmediately_WhenScannerDisconnects()
        {
            var disconnecting = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var manager = new ScannerManager(new[] { disconnecting }, null);

            var result1 = await manager.ScanAsync();
            Assert.True(result1.IsSuccess);
            Assert.Equal(0, manager.BackoffStep);

            disconnecting.IsConnectedValue = false;
            disconnecting.InitializeResult = false;
            disconnecting.ScanResult = CaptureResult.Fail("SCANNER_NOT_CONNECTED", "disconnected");

            var result2 = await manager.ScanAsync();
            Assert.False(result2.IsSuccess);
            Assert.Equal("SCANNER_NOT_CONNECTED", result2.ErrorCode);
            Assert.Equal(1, manager.BackoffStep);
        }
    }
}