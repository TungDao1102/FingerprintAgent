using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Models;
using FingerprintAgent.Tests.Scanner;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace FingerprintAgent.Tests.Api
{
    public class CaptureHandlerTests : IDisposable
    {
        private readonly CaptureHandlerTestFixture _fixture;
        private bool _disposed;

        public CaptureHandlerTests()
        {
            _fixture = new CaptureHandlerTestFixture();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _fixture.Dispose();
            }
        }

        // ---------- Helpers ----------

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

        private static async Task<HttpWebResponse> GetResponseAsync(Task<HttpWebResponse> responseTask)
        {
            try
            {
                return await responseTask;
            }
            catch (WebException ex)
            {
                return (HttpWebResponse)ex.Response;
            }
        }

        private static async Task<string> ReadBodyAsync(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private Task WaitForContextAsync(int timeoutMs = 5000)
        {
            return Task.Run(() => _fixture.WaitForContext(timeoutMs));
        }

        // ---------- Tests: request validation (400 INVALID_REQUEST) ----------

        [Fact]
        public async Task HandleAsync_EmptyBody_Returns400InvalidRequest()
        {
            // Arrange
            var scanner = new MockScannerAdapterWithSettableProperties();
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(400, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            Assert.Contains("INVALID_REQUEST", body);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.False(parsed.IsSuccess);
            Assert.Equal("INVALID_REQUEST", parsed.ErrorCode);
        }

        [Fact]
        public async Task HandleAsync_InvalidJson_Returns400InvalidRequest()
        {
            // Arrange
            var scanner = new MockScannerAdapterWithSettableProperties();
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{not json");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(400, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            Assert.Contains("INVALID_REQUEST", body);
        }

        [Fact]
        public async Task HandleAsync_MissingRequestId_Returns400()
        {
            // Arrange — body is valid JSON but has no requestId field
            var scanner = new MockScannerAdapterWithSettableProperties();
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"purpose\":\"enrollment\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(400, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.Equal("INVALID_REQUEST", parsed.ErrorCode);
            Assert.Contains("requestId", parsed.ErrorMessage);
        }

        [Fact]
        public async Task HandleAsync_WhitespaceRequestId_Returns400()
        {
            // Arrange — requestId is non-empty but all whitespace
            var scanner = new MockScannerAdapterWithSettableProperties();
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"requestId\":\"   \"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(400, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.Equal("INVALID_REQUEST", parsed.ErrorCode);
        }

        // ---------- Tests: success path (200) ----------

        [Fact]
        public async Task HandleAsync_Success_Returns200AndBase64Image()
        {
            // Arrange
            byte[] imageBytes = new byte[] { 1, 2, 3, 4, 5 };
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Ok(imageBytes, "image/png"),
                DeviceIdValue = "test-device-001"
            };
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"requestId\":\"REF-001\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(200, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);

            Assert.True(parsed.IsSuccess);
            Assert.Equal("test-device-001", parsed.DeviceId);
            Assert.Equal("image/png", parsed.MimeType);
            Assert.NotNull(parsed.ImageBytes);

            // Verify it's actually base64-encoded of the original bytes
            byte[] decoded = Convert.FromBase64String(parsed.ImageBytes);
            Assert.Equal(imageBytes, decoded);
        }

        [Fact]
        public async Task HandleAsync_Success_EchoesRequestIdInResponse()
        {
            // Arrange — requestId must round-trip so the caller can correlate
            // request ↔ response without parsing agent log files.
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 }),
                DeviceIdValue = "test-device-001"
            };
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"requestId\":\"echo-test-42\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(200, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.Equal("echo-test-42", parsed.RequestId);
        }

        [Fact]
        public async Task HandleAsync_Success_AcceptsPurposeAndMetadata_DoesNotBreakScan()
        {
            // Arrange — optional fields are accepted without altering the scan path
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Ok(new byte[] { 9 }),
                DeviceIdValue = "dev-purpose"
            };
            var handler = new CaptureHandler(null);

            // Act
            var body = "{\"requestId\":\"r1\",\"purpose\":\"enrollment\",\"metadata\":{\"formCode\":\"F-1\",\"appVersion\":\"1.2.3\"}}";
            var responseTask = SendAsync(body);
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(200, (int)response.StatusCode);
            string responseBody = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(responseBody);
            Assert.Equal("r1", parsed.RequestId);
            Assert.True(parsed.IsSuccess);
        }

        [Fact]
        public async Task HandleAsync_OversizedMetadata_DropsInvalidEntries_Returns200()
        {
            // Arrange — metadata with: one valid entry, one over-long value (dropped),
            // one over-long key (dropped), 19 extras (kept up to the 20-key cap),
            // and a 22nd entry (excess dropped by cap).
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Ok(new byte[] { 7 }),
                DeviceIdValue = "dev-md"
            };
            var handler = new CaptureHandler(null);

            var sb = new StringBuilder("{\"requestId\":\"r-md\",\"metadata\":{");
            sb.Append("\"valid\":\"ok\"");
            sb.Append(",\"longValue\":\"").Append(new string('v', 150)).Append("\"");  // value too long → dropped
            sb.Append(",\"").Append(new string('k', 150)).Append("\":\"ok\"");        // key too long → dropped
            for (int i = 0; i < 19; i++)
            {
                sb.Append(",\"k").Append(i).Append("\":\"v").Append(i).Append("\"");
            }
            sb.Append(",\"kExtra\":\"vExtra\"");  // 22nd entry → dropped by 20-key cap
            sb.Append("}}");

            // Act
            var responseTask = SendAsync(sb.ToString());
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert — capture still succeeds; oversized entries are silently dropped,
            // not turned into a 400 or 500.
            Assert.Equal(200, (int)response.StatusCode);
            string responseBody = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(responseBody);
            Assert.True(parsed.IsSuccess);
            Assert.Equal("r-md", parsed.RequestId);
        }

        [Fact]
        public async Task HandleAsync_Success_GeneratesCorrelationIdWhenNotProvided()
        {
            // Arrange
            // The correlationId is internal-only (used for logging) and is NOT echoed
            // in the response, so we can only verify the handler completes successfully
            // when no correlationId is supplied.
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Ok(new byte[] { 9, 9, 9 })
            };
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"requestId\":\"REF-X\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner, correlationId: null);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(200, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.True(parsed.IsSuccess);
        }

        // ---------- Tests: scanner error mapping ----------

        [Fact]
        public async Task HandleAsync_ScannerReturnsScannerNotConnected_Returns503()
        {
            // Arrange
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Fail("SCANNER_NOT_CONNECTED", "No scanner attached"),
                VendorErrorCodeValue = "NO_DEVICE"
            };
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"requestId\":\"REF-001\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(503, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.False(parsed.IsSuccess);
            Assert.Equal("SCANNER_NOT_CONNECTED", parsed.ErrorCode);
            Assert.Equal("NO_DEVICE", parsed.VendorErrorCode);
            // requestId must echo even on scanner-failure path so caller can still correlate
            Assert.Equal("REF-001", parsed.RequestId);
        }

        [Fact]
        public async Task HandleAsync_ScannerReturnsCaptureTimeout_Returns504()
        {
            // Arrange
            var scanner = new MockScannerAdapterWithSettableProperties
            {
                ScanResult = CaptureResult.Fail("CAPTURE_TIMEOUT", "Timed out after 10s"),
                VendorErrorCodeValue = "TIMEOUT_ERR"
            };
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"requestId\":\"REF-001\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, scanner);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(504, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.Equal("CAPTURE_TIMEOUT", parsed.ErrorCode);
            Assert.Equal("TIMEOUT_ERR", parsed.VendorErrorCode);
            Assert.Equal("REF-001", parsed.RequestId);
        }

        [Fact]
        public async Task HandleAsync_ScannerThrows_Returns500CaptureFailed()
        {
            // Arrange
            // MockScannerAdapterWithSettableProperties can't throw on ScanAsync, so use Moq
            // for this single case to verify the catch-all path produces 500/CAPTURE_FAILED.
            var throwingScanner = new Mock<IScannerAdapter>();
            throwingScanner.Setup(s => s.DeviceId).Returns("throw-device");
            throwingScanner.Setup(s => s.ScanAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));
            var handler = new CaptureHandler(null);

            // Act
            var responseTask = SendAsync("{\"requestId\":\"REF-001\"}");
            await WaitForContextAsync(5000);
            await handler.HandleAsync(_fixture.CapturedContext, throwingScanner.Object);

            var response = await GetResponseAsync(responseTask);

            // Assert
            Assert.Equal(500, (int)response.StatusCode);
            string body = await ReadBodyAsync(response);
            var parsed = JsonConvert.DeserializeObject<CaptureResponse>(body);
            Assert.False(parsed.IsSuccess);
            Assert.Equal("CAPTURE_FAILED", parsed.ErrorCode);
            Assert.Contains("boom", parsed.ErrorMessage);
            // requestId unknown on the catch-all path (already past request scope)
            Assert.Null(parsed.RequestId);
        }
    }
}
