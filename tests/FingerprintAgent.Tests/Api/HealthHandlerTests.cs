using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Tests.Scanner;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FingerprintAgent.Tests.Api
{
    public class HealthHandlerTests : IDisposable
    {
        private readonly CaptureHandlerTestFixture _fixture;
        private bool _disposed;

        public HealthHandlerTests()
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

        private async Task<(int status, string body)> InvokeHealthAsync(IScannerAdapter scanner)
        {
            var request = (HttpWebRequest)WebRequest.Create(_fixture.BaseUrl);
            request.Method = "GET";
            request.ContentType = "application/json";

            Task<HttpWebResponse> responseTask = Task.Run(() => GetResponseWithErrors(request));

            HttpListenerContext ctx = _fixture.WaitForContext(5000);
            var handler = new HealthHandler(null);
            await handler.HandleAsync(ctx, scanner);

            HttpWebResponse response = await responseTask;
            string body;
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            int status = (int)response.StatusCode;
            response.Close();
            return (status, body);
        }

        private static HttpWebResponse GetResponseWithErrors(HttpWebRequest request)
        {
            try
            {
                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                return (HttpWebResponse)ex.Response;
            }
        }

        [Fact]
        public async Task HandleAsync_ConnectedScanner_Returns200AndHealthy()
        {
            // Arrange
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                DeviceIdValue = "scanner-A",
                ModelValue = "Connected Scanner"
            };

            // Act
            var (status, body) = await InvokeHealthAsync(mock);

            // Assert
            Assert.Equal(200, status);
            JObject json = JObject.Parse(body);
            Assert.Equal("healthy", (string)json["status"]);
            Assert.Equal("scanner-A", (string)json["deviceId"]);
        }

        [Fact]
        public async Task HandleAsync_DisconnectedScanner_BackoffBelowMax_Returns200AndDegraded()
        {
            // Arrange
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false,
                DeviceIdValue = "no-device",
                ModelValue = "no-device"
            };
            using (var sm = new ScannerManager(new[] { (IScannerAdapter)failing }, null))
            {
                await sm.ScanAsync();
                Assert.True(sm.BackoffStep > 0 && sm.BackoffStep < 3);

                // Act
                var (status, body) = await InvokeHealthAsync(sm);

                // Assert
                Assert.Equal(200, status);
                JObject json = JObject.Parse(body);
                Assert.Equal("degraded", (string)json["status"]);
                Assert.True((bool)json["inBackoff"]);
                Assert.True((int)json["backoffStep"] < 3);
            }
        }

        [Fact]
        public async Task HandleAsync_DisconnectedScanner_BackoffAtMax_Returns503AndDegraded()
        {
            // Arrange
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false,
                DeviceIdValue = "no-device",
                ModelValue = "no-device"
            };
            using (var sm = new ScannerManager(new[] { (IScannerAdapter)failing }, null))
            {
                for (int i = 0; i < 5; i++)
                    await sm.ScanAsync();
                Assert.Equal(3, sm.BackoffStep);

                // Act
                var (status, body) = await InvokeHealthAsync(sm);

                // Assert
                Assert.Equal(503, status);
                JObject json = JObject.Parse(body);
                Assert.Equal("degraded", (string)json["status"]);
                Assert.True((bool)json["inBackoff"]);
                Assert.Equal(3, (int)json["backoffStep"]);
            }
        }

        [Fact]
        public async Task HandleAsync_NonScannerManager_PassesThroughIsConnectedAndDeviceId()
        {
            // Arrange
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false,
                DeviceIdValue = "plain-adapter-device",
                ModelValue = "Plain Adapter Model",
                VendorErrorCodeValue = "PLAIN_ERR"
            };

            // Act
            var (status, body) = await InvokeHealthAsync(mock);

            // Assert
            Assert.Equal(200, status);
            JObject json = JObject.Parse(body);
            Assert.Equal("degraded", (string)json["status"]);
            Assert.Equal("plain-adapter-device", (string)json["deviceId"]);
            Assert.Equal("Plain Adapter Model", (string)json["model"]);
            Assert.Equal("PLAIN_ERR", (string)json["vendorErrorCode"]);
        }

        [Fact]
        public async Task HandleAsync_NonScannerManager_OmitsBackoffFields()
        {
            // Arrange
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };

            // Act
            var (status, body) = await InvokeHealthAsync(mock);

            // Assert
            Assert.Equal(200, status);
            JObject json = JObject.Parse(body);
            Assert.False((bool)json["inBackoff"]);
            Assert.Equal(0, (int)json["backoffStep"]);
        }

        [Fact]
        public async Task HandleAsync_JsonContainsUptime()
        {
            // Arrange
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true
            };

            // Act
            var (status, body) = await InvokeHealthAsync(mock);

            // Assert
            Assert.Equal(200, status);
            JObject json = JObject.Parse(body);
            string uptime = (string)json["uptime"];
            Assert.NotNull(uptime);
            Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", uptime);
        }

        [Fact]
        public async Task HandleAsync_GeneratesCorrelationIdWhenNotProvided_WritesResponse()
        {
            // Arrange
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true
            };

            // Act
            var (status, body) = await InvokeHealthAsync(mock);

            // Assert - handler must not throw; response is written and valid JSON
            Assert.Equal(200, status);
            Assert.NotNull(body);
            JObject json = JObject.Parse(body);
            Assert.NotNull(json["status"]);
        }

        [Fact]
        public async Task HandleAsync_UsesProvidedCorrelationId_WritesResponse()
        {
            // Arrange
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true
            };

            var request = (HttpWebRequest)WebRequest.Create(_fixture.BaseUrl);
            request.Method = "GET";
            Task<HttpWebResponse> responseTask = Task.Run(() => GetResponseWithErrors(request));

            HttpListenerContext ctx = _fixture.WaitForContext(5000);
            var handler = new HealthHandler(null);

            // Act
            await handler.HandleAsync(ctx, mock, "my-correlation-id-123");

            HttpWebResponse response = await responseTask;
            string body;
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            response.Close();

            // Assert - handler must not throw; response is valid JSON
            Assert.Equal(200, (int)response.StatusCode);
            Assert.NotNull(body);
            JObject json = JObject.Parse(body);
            Assert.Equal("healthy", (string)json["status"]);
        }
    }
}
