using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Tests.Scanner;
using Moq;
using Xunit;

namespace FingerprintAgent.Tests.Api
{
    /// <summary>
    /// Verifies CaptureHandler threads the CancellationToken through to
    /// scanner.ScanAsync so graceful shutdown can abort in-flight captures
    /// (Bug #3: previously CaptureHandler.HandleAsync ignored CT entirely,
    /// so HttpServer.Stop() blocked for the full 30s drain timeout on every
    /// shutdown while a ZKTeco rolling-capture was in flight).
    /// </summary>
    public class CaptureHandlerCancellationTests : IDisposable
    {
        private readonly CaptureHandlerTestFixture _fixture;
        private bool _disposed;

        public CaptureHandlerCancellationTests()
        {
            _fixture = new CaptureHandlerTestFixture();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _fixture?.Dispose();
            }
        }

        private async Task<HttpWebResponse> SendAsync(string body, string path = "/api/capture", string method = "POST")
        {
            var request = WebRequest.CreateHttp(_fixture.BaseUrl.TrimEnd('/') + path);
            request.Method = method;
            if (method != "GET")
            {
                request.ContentType = "application/json";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
                request.ContentLength = bodyBytes.Length;
                using (var rs = await request.GetRequestStreamAsync())
                    await rs.WriteAsync(bodyBytes, 0, bodyBytes.Length);
            }
            try
            {
                return (HttpWebResponse)await request.GetResponseAsync();
            }
            catch (WebException ex)
            {
                return (HttpWebResponse)ex.Response;
            }
        }

        private Task WaitForContextAsync(int timeoutMs = 5000)
        {
            return Task.Run(() => _fixture.WaitForContext(timeoutMs));
        }

        [Fact]
        public async Task HandleAsync_CancellationRequestedBeforeScan_Returns503()
        {
            // Arrange — CT already cancelled
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var handler = new CaptureHandler(null);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                // Act
                var responseTask = SendAsync("{\"thamChieuId\":\"REF-1\",\"maPhieu\":\"P1\"}");
                await WaitForContextAsync(5000);
                await handler.HandleAsync(_fixture.CapturedContext, scanner, "cid", cts.Token);

                var response = await responseTask;

                // Assert — 503 Service Unavailable (the CT guard short-circuits before
                // scanner.ScanAsync is reached). The handler would have returned 200
                // (with the Ok ScanResult) if ScanAsync had been invoked.
                Assert.Equal(503, (int)response.StatusCode);
            }
        }

        [Fact]
        public async Task HandleAsync_PassesCancellationTokenToScanner()
        {
            // Arrange — scanner that records the CT passed in
            CancellationToken capturedToken = default;
            var scanner = new Mock<IScannerAdapter>();
            scanner.Setup(s => s.DeviceId).Returns("dev-1");
            scanner.Setup(s => s.VendorErrorCode).Returns("NONE");
            scanner.Setup(s => s.ScanAsync(It.IsAny<CancellationToken>()))
                .Callback<CancellationToken>(ct => capturedToken = ct)
                .ReturnsAsync(CaptureResult.Ok(new byte[] { 1, 2, 3 }));
            var handler = new CaptureHandler(null);
            using (var cts = new CancellationTokenSource())
            {
                // Act
                var responseTask = SendAsync("{\"thamChieuId\":\"REF-1\",\"maPhieu\":\"P1\"}");
                await WaitForContextAsync(5000);
                await handler.HandleAsync(_fixture.CapturedContext, scanner.Object, "cid", cts.Token);

                var response = await responseTask;

                // Assert — scanner received a CT and it equals the one we passed
                scanner.Verify(s => s.ScanAsync(It.IsAny<CancellationToken>()), Times.Once);
                Assert.Equal(cts.Token, capturedToken);
                Assert.NotEqual(CancellationToken.None, capturedToken);
            }
        }

        [Fact]
        public async Task HandleAsync_DefaultCancellationToken_NoneStillPassedToScanner()
        {
            // Arrange — backward compat: callers that don't supply a CT must still work
            CancellationToken capturedToken = CancellationToken.None;
            var scanner = new Mock<IScannerAdapter>();
            scanner.Setup(s => s.DeviceId).Returns("dev-1");
            scanner.Setup(s => s.VendorErrorCode).Returns("NONE");
            scanner.Setup(s => s.ScanAsync(It.IsAny<CancellationToken>()))
                .Callback<CancellationToken>(ct => capturedToken = ct)
                .ReturnsAsync(CaptureResult.Ok(new byte[] { 1, 2, 3 }));
            var handler = new CaptureHandler(null);

            // Act — no CT supplied (backward-compat overload)
            var responseTask = SendAsync("{\"thamChieuId\":\"REF-1\",\"maPhieu\":\"P1\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner.Object);

            var response = await responseTask;

            // Assert — CancellationToken.None is propagated, not silently dropped
            Assert.Equal(200, (int)response.StatusCode);
            Assert.Equal(CancellationToken.None, capturedToken);
        }

        [Fact]
        public async Task HandleAsync_CancelledDuringScan_ScannerObservesCancellation_AndHandlerCompletesQuickly()
        {
            // Arrange — long-running scanner that observes the CT
            var scanner = new Mock<IScannerAdapter>();
            scanner.Setup(s => s.DeviceId).Returns("dev-1");
            scanner.Setup(s => s.VendorErrorCode).Returns("NONE");
            scanner.Setup(s => s.ScanAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(async ct =>
                {
                    // Simulate a slow SDK call that gets cancelled mid-flight.
                    // Task.Delay throws TaskCanceledException (subclass of OCE) when ct fires.
                    await Task.Delay(5000, ct);
                    return CaptureResult.Ok(new byte[] { 1 });
                });
            var handler = new CaptureHandler(null);
            using (var cts = new CancellationTokenSource())
            {
                // Act — fire the request, cancel after 100ms, and measure how long the handler takes
                var responseTask = SendAsync("{\"thamChieuId\":\"REF-1\",\"maPhieu\":\"P1\"}");
                await WaitForContextAsync(5000);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var handlerTask = handler.HandleAsync(_fixture.CapturedContext, scanner.Object, "cid", cts.Token);
                cts.CancelAfter(100);
                await handlerTask;
                sw.Stop();

                // Assert — handler completed within ~2s (NOT the 5s scanner delay).
                // Pre-fix, this would have been 5s because the CT wasn't threaded into the scanner.
                Assert.True(sw.ElapsedMilliseconds < 2000,
                    $"Handler should complete promptly after CT cancellation; took {sw.ElapsedMilliseconds}ms");

                // Scanner was invoked exactly once
                scanner.Verify(s => s.ScanAsync(It.IsAny<CancellationToken>()), Times.Once);

                // The response is a CAPTURE_FAILED (the handler catches OCE and returns 500)
                var response = await responseTask;
                Assert.Equal(500, (int)response.StatusCode);
                string body = await ReadBody(response);
                Assert.Contains("CAPTURE_FAILED", body);
            }
        }

        private static async Task<string> ReadBody(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }
    }
}
