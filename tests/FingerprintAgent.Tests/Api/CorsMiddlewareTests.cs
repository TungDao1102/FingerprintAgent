using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests.Api
{
    /// <summary>
    /// Tests CorsMiddleware behavior through a real HttpServer with HttpClient.
    /// HttpListenerRequest/Response cannot be constructed directly in unit tests,
    /// so we verify CORS behavior via actual HTTP requests.
    /// </summary>
    public class CorsMiddlewareTests
    {
        /// <summary>
        /// Wildcard CORS mode test fixture - shared across all WildcardMode tests.
        /// </summary>
        public class WildcardModeFixture : IDisposable
        {
            public HttpServer Server { get; }
            public MockScannerAdapter Scanner { get; }
            public HttpClient Client { get; }
            private bool _disposed;

            public WildcardModeFixture()
            {
                Scanner = new MockScannerAdapter();
                var config = new AgentConfig();
                config.Cors.Mode = "wildcard";
                config.Http.Port = 5045;
                Server = new HttpServer(config, Scanner);
                Server.Start();

                Client = new HttpClient();
                Client.BaseAddress = new Uri("http://127.0.0.1:5045");
                Client.Timeout = TimeSpan.FromSeconds(5);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    Client?.Dispose();
                    Server?.Stop();
                    Server?.Dispose();
                }
            }
        }

        /// <summary>
        /// Allowlist CORS mode test fixture - shared across all AllowlistMode tests.
        /// </summary>
        public class AllowlistModeFixture : IDisposable
        {
            public HttpServer Server { get; }
            public MockScannerAdapter Scanner { get; }
            public HttpClient Client { get; }
            private bool _disposed;

            public AllowlistModeFixture()
            {
                Scanner = new MockScannerAdapter();
                var config = new AgentConfig();
                config.Cors.Mode = "allowlist";
                config.Cors.AllowedOrigins = new[] { "http://trusted.com" };
                config.Http.Port = 5046;
                Server = new HttpServer(config, Scanner);
                Server.Start();

                Client = new HttpClient();
                Client.BaseAddress = new Uri("http://127.0.0.1:5046");
                Client.Timeout = TimeSpan.FromSeconds(5);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    Client?.Dispose();
                    Server?.Stop();
                    Server?.Dispose();
                }
            }
        }

        /// <summary>
        /// Tests with wildcard CORS mode.
        /// Uses IClassFixture to ensure proper disposal of server/client resources.
        /// </summary>
        [Collection("WildcardMode")]
        public class WildcardMode : IClassFixture<WildcardModeFixture>
        {
            private readonly WildcardModeFixture _fixture;

            public WildcardMode(WildcardModeFixture fixture)
            {
                _fixture = fixture;
            }

            [Fact]
            public async Task Preflight_WithOrigin_Returns204()
            {
                var request = new HttpRequestMessage(new HttpMethod("OPTIONS"), "/api/capture");
                request.Headers.Add("Origin", "http://example.com");

                var response = await _fixture.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }

            [Fact]
            public async Task Preflight_WithoutOrigin_ReturnsNotFound()
            {
                var request = new HttpRequestMessage(new HttpMethod("OPTIONS"), "/api/capture");

                var response = await _fixture.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            [Fact]
            public async Task ActualRequest_SetsAsterisk()
            {
                var request = new HttpRequestMessage(new HttpMethod("GET"), "/health");
                request.Headers.Add("Origin", "http://example.com");

                var response = await _fixture.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }
        }

        /// <summary>
        /// Tests with allowlist CORS mode.
        /// Uses IClassFixture to ensure proper disposal of server/client resources.
        /// </summary>
        [Collection("AllowlistMode")]
        public class AllowlistMode : IClassFixture<AllowlistModeFixture>
        {
            private readonly AllowlistModeFixture _fixture;

            public AllowlistMode(AllowlistModeFixture fixture)
            {
                _fixture = fixture;
            }

            [Fact]
            public async Task Preflight_AllowedOrigin_Returns204()
            {
                var request = new HttpRequestMessage(new HttpMethod("OPTIONS"), "/api/capture");
                request.Headers.Add("Origin", "http://trusted.com");

                var response = await _fixture.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal("http://trusted.com",
                    response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }

            [Fact]
            public async Task Preflight_DeniedOrigin_Returns403()
            {
                var request = new HttpRequestMessage(new HttpMethod("OPTIONS"), "/api/capture");
                request.Headers.Add("Origin", "http://evil.com");

                var response = await _fixture.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            [Fact]
            public async Task ActualRequest_AllowedOrigin_SetsOriginHeader()
            {
                var request = new HttpRequestMessage(new HttpMethod("GET"), "/health");
                request.Headers.Add("Origin", "http://trusted.com");

                var response = await _fixture.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("http://trusted.com",
                    response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
            }
        }
    }
}