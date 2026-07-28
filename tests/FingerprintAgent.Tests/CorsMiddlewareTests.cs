using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using Xunit;

namespace FingerprintAgent.Tests
{
    /// <summary>
    /// Tests CorsMiddleware behavior through a real HttpServer with HttpClient.
    /// HttpListenerRequest/Response cannot be constructed directly in unit tests,
    /// so we verify CORS behavior via actual HTTP requests.
    /// </summary>
    public class CorsMiddlewareTests
    {
        /// <summary>
        /// Tests with wildcard CORS mode.
        /// </summary>
        public class WildcardMode : IDisposable
        {
            private readonly HttpServer _server;
            private readonly MockScannerAdapter _scanner;
            private readonly HttpClient _client;
            private bool _disposed;

            public WildcardMode()
            {
                _scanner = new MockScannerAdapter();
                var config = new Configuration.AgentConfig();
                config.Cors.Mode = "wildcard";
                config.Http.Port = 5045;
                _server = new HttpServer(config, _scanner);
                _server.Start();

                _client = new HttpClient();
                _client.BaseAddress = new Uri("http://127.0.0.1:5045");
                _client.Timeout = TimeSpan.FromSeconds(5);
            }

            [Fact]
            public async Task Preflight_WithOrigin_Returns204()
            {
                var request = new HttpRequestMessage(HttpMethod.Options, "/api/capture");
                request.Headers.Add("Origin", "http://example.com");

                var response = await _client.SendAsync(request);

                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }

            [Fact]
            public async Task Preflight_WithoutOrigin_ReturnsNotFound()
            {
                var request = new HttpRequestMessage(HttpMethod.Options, "/api/capture");

                var response = await _client.SendAsync(request);

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            [Fact]
            public async Task ActualRequest_SetsAsterisk()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/health");
                request.Headers.Add("Origin", "http://example.com");

                var response = await _client.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _client?.Dispose();
                    _server?.Stop();
                    _server?.Dispose();
                }
            }
        }

        /// <summary>
        /// Tests with allowlist CORS mode.
        /// </summary>
        public class AllowlistMode : IDisposable
        {
            private readonly HttpServer _server;
            private readonly MockScannerAdapter _scanner;
            private readonly HttpClient _client;
            private bool _disposed;

            public AllowlistMode()
            {
                _scanner = new MockScannerAdapter();
                var config = new Configuration.AgentConfig();
                config.Cors.Mode = "allowlist";
                config.Cors.AllowedOrigins = new[] { "http://trusted.com" };
                config.Http.Port = 5046;
                _server = new HttpServer(config, _scanner);
                _server.Start();

                _client = new HttpClient();
                _client.BaseAddress = new Uri("http://127.0.0.1:5046");
                _client.Timeout = TimeSpan.FromSeconds(5);
            }

            [Fact]
            public async Task Preflight_AllowedOrigin_Returns204()
            {
                var request = new HttpRequestMessage(HttpMethod.Options, "/api/capture");
                request.Headers.Add("Origin", "http://trusted.com");

                var response = await _client.SendAsync(request);

                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal("http://trusted.com",
                    response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }

            [Fact]
            public async Task Preflight_DeniedOrigin_Returns403()
            {
                var request = new HttpRequestMessage(HttpMethod.Options, "/api/capture");
                request.Headers.Add("Origin", "http://evil.com");

                var response = await _client.SendAsync(request);

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            [Fact]
            public async Task ActualRequest_AllowedOrigin_SetsOriginHeader()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/health");
                request.Headers.Add("Origin", "http://trusted.com");

                var response = await _client.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("http://trusted.com",
                    response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _client?.Dispose();
                    _server?.Stop();
                    _server?.Dispose();
                }
            }
        }
    }
}
