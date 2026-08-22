using System;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Scanner
{
    public class ScannerManagerConcurrencyTests
    {
        /// <summary>
        /// Slow adapter simulating a 200ms capture — enough time to detect overlap.
        /// Tracks max observed concurrency via Interlocked CAS loop.
        /// </summary>
        private class SlowAdapter : IScannerAdapter, IDisposable
        {
            private int _concurrentCount;
            private int _maxConcurrent;

            public bool IsConnected => true;
            public string DeviceId => "slow-device";
            public string Model => "Slow Scanner";
            public string MimeType => "image/png";
            public string VendorErrorCode => "NONE";
            public int MaxObservedConcurrency => _maxConcurrent;

            public bool Initialize() => true;
            public bool ProbeConnection() => true;

            public async Task<CaptureResult> ScanAsync(CancellationToken ct = default)
            {
                int count = Interlocked.Increment(ref _concurrentCount);
                int prev;
                do { prev = _maxConcurrent; }
                while (count > prev &&
                       Interlocked.CompareExchange(ref _maxConcurrent, count, prev) != prev);

                await Task.Delay(200, ct);

                Interlocked.Decrement(ref _concurrentCount);
                return CaptureResult.Ok(new byte[] { 1, 2, 3 });
            }

            public void Dispose() { }
        }

        [Fact]
        public async Task ScanAsync_SerializesConcurrentCalls_NoOverlap()
        {
            var slow = new SlowAdapter();
            var manager = new ScannerManager(new IScannerAdapter[] { slow }, null);

            var t1 = manager.ScanAsync();
            var t2 = manager.ScanAsync();
            var t3 = manager.ScanAsync();

            await Task.WhenAll(t1, t2, t3);

            // Max concurrent must be 1 — proves SemaphoreSlim serializes captures
            Assert.Equal(1, slow.MaxObservedConcurrency);
        }

        [Fact]
        public async Task ScanAsync_AllConcurrentCallsSucceed()
        {
            var slow = new SlowAdapter();
            var manager = new ScannerManager(new IScannerAdapter[] { slow }, null);

            var results = await Task.WhenAll(
                manager.ScanAsync(), manager.ScanAsync(), manager.ScanAsync());

            foreach (var r in results)
                Assert.True(r.IsSuccess);
        }
    }
}
