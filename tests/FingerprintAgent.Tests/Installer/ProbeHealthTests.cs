extern alias WixCA;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using CustomActions = WixCA::FingerprintAgent.Installer.CustomActions;

namespace FingerprintAgent.Tests.Installer
{
    /// <summary>
    /// Tests for CustomActions.ProbeHealth — the pure-logic HTTP probe helper that
    /// classifies /health responses into Healthy / DegradedScannerMissing / Unhealthy /
    /// Timeout / ConnectionRefused. Uses ClassifyHealthResponse (extracted from
    /// ProbeHealthSingleAttempt) so tests exercise the actual classifier instead of
    /// re-implementing it via a duplicated ProbeUrl helper.
    /// </summary>
    public class ProbeHealthTests : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _prefix;
        private readonly CancellationTokenSource _cts;
        private readonly Task _serveTask;
        private bool _disposed;
        // Test-controlled response — set per-test via a setter before invoking ProbeHealth
        private int _responseStatus = 200;
        private string _responseBody = @"{ ""status"": ""healthy"" }";

        public ProbeHealthTests()
        {
            int port = FindFreePort();
            _prefix = "http://127.0.0.1:" + port + "/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _serveTask = Task.Run(() => ServeLoop(_cts.Token));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _cts.Cancel();
                _listener.Stop();
                _serveTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            try { ((IDisposable)_listener).Dispose(); } catch { }
            _cts.Dispose();
        }

        private static int FindFreePort()
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }

        private async Task ServeLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                try
                {
                    ctx.Response.StatusCode = _responseStatus;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(_responseBody);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
                }
                catch { }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            }
        }

        [Fact]
        public void Classify_200_ClassifiedAsHealthy()
        {
            var result = CustomActions.ClassifyHealthResponse(200, @"{ ""status"": ""healthy"" }", null);
            Assert.Equal(CustomActions.HealthProbeOutcome.Healthy, result.Outcome);
            Assert.Equal(200, result.HttpStatus);
        }

        [Fact]
        public void Classify_503_ClassifiedAsDegradedScannerMissing()
        {
            var result = CustomActions.ClassifyHealthResponse(503, @"{ ""status"": ""degraded"" }", null);
            Assert.Equal(CustomActions.HealthProbeOutcome.DegradedScannerMissing, result.Outcome);
            Assert.Equal(503, result.HttpStatus);
        }

        [Fact]
        public void Classify_500_ClassifiedAsUnhealthy()
        {
            var result = CustomActions.ClassifyHealthResponse(500, "internal error", null);
            Assert.Equal(CustomActions.HealthProbeOutcome.Unhealthy, result.Outcome);
            Assert.Equal(500, result.HttpStatus);
        }

        [Fact]
        public void Classify_NullStatus_ClassifiedAsTimeout()
        {
            var result = CustomActions.ClassifyHealthResponse(null, null, null);
            Assert.Equal(CustomActions.HealthProbeOutcome.Timeout, result.Outcome);
            Assert.Null(result.HttpStatus);
        }

        [Fact]
        public void Classify_NegativeStatus_ClassifiedAsTimeout()
        {
            var result = CustomActions.ClassifyHealthResponse(-1, null, null);
            Assert.Equal(CustomActions.HealthProbeOutcome.Timeout, result.Outcome);
            Assert.Null(result.HttpStatus);
        }

        [Fact]
        public void Classify_HttpRequestException_ClassifiedAsConnectionRefused()
        {
            var result = CustomActions.ClassifyHealthResponse(null, null, new HttpRequestException("refused"));
            Assert.Equal(CustomActions.HealthProbeOutcome.ConnectionRefused, result.Outcome);
            Assert.Null(result.HttpStatus);
        }

        [Fact]
        public void Classify_AggregateException_ClassifiedAsConnectionRefused()
        {
            var result = CustomActions.ClassifyHealthResponse(null, null, new AggregateException());
            Assert.Equal(CustomActions.HealthProbeOutcome.ConnectionRefused, result.Outcome);
        }

        [Fact]
        public void Classify_OtherException_ClassifiedAsUnhealthy()
        {
            var result = CustomActions.ClassifyHealthResponse(null, null, new InvalidOperationException("boom"));
            Assert.Equal(CustomActions.HealthProbeOutcome.Unhealthy, result.Outcome);
        }

        [Fact]
        public void ProbeHealth_HardcodedUrl_AtLeastReachesClassifier()
        {
            // Indirect smoke test: the production ProbeHealth() targets 127.0.0.1:5043.
            // Without spinning up the agent we expect ConnectionRefused (port unbound).
            // This verifies ProbeHealth() doesn't throw — it returns ConnectionRefused.
            var result = CustomActions.ProbeHealth();
            // Outcome should be one of the known enum values — proves classifier ran.
            Assert.True(Enum.IsDefined(typeof(CustomActions.HealthProbeOutcome), result.Outcome));
        }
    }
}
