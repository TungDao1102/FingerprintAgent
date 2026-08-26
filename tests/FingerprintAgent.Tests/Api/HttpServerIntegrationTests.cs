using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FingerprintAgent.Tests.Api
{
    public class HttpServerIntegrationTests : IDisposable
    {
        private readonly HttpServer _server;
        private readonly MockScannerAdapter _scanner;
        private readonly HttpClient _client;
        private readonly int _port;
        private bool _disposed;

        public HttpServerIntegrationTests()
        {
            // Use TcpListener to find an available port to avoid conflicts
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            _scanner = new MockScannerAdapter();
            _server = new HttpServer("127.0.0.1", _port, _scanner);
            _server.Start();

            _client = new HttpClient();
            _client.BaseAddress = new Uri($"http://127.0.0.1:{_port}");
            _client.Timeout = TimeSpan.FromSeconds(5);
        }

        [Fact]
        public async Task HealthEndpoint_Returns200()
        {
            var response = await _client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);

            Assert.NotNull(json["status"]);
            Assert.NotNull(json["deviceId"]);
            Assert.NotNull(json["uptime"]);
        }

        [Fact]
        public async Task CaptureEndpoint_WithValidBody_Returns200_AndImageBytes()
        {
            var requestBody = new
            {
                requestId = "t1"
            };

            var content = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await _client.PostAsync("/api/capture", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);

            Assert.True((bool)json["isSuccess"]);
            Assert.NotNull(json["imageBytes"]);
            Assert.Equal("image/png", (string)json["mimeType"]);
            Assert.NotNull(json["verificationData"]);
            Assert.Equal(44, ((string)json["verificationData"]).Length);
            Assert.Equal("mock-scanner-001", (string)json["deviceId"]);
            Assert.NotNull(json["capturedAt"]);
        }

        [Fact]
        public async Task CaptureEndpoint_WithEmptyBody_Returns400()
        {
            var content = new StringContent(
                "{}",
                Encoding.UTF8,
                "application/json");

            var response = await _client.PostAsync("/api/capture", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);

            Assert.False((bool)json["isSuccess"]);
            Assert.NotNull(json["errorMessage"]);
        }

        [Fact]
        public async Task CaptureEndpoint_WithMalformedJson_Returns400()
        {
            var content = new StringContent(
                "{invalid json}",
                Encoding.UTF8,
                "application/json");

            var response = await _client.PostAsync("/api/capture", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UnknownEndpoint_Returns404()
        {
            var response = await _client.GetAsync("/nonexistent");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
