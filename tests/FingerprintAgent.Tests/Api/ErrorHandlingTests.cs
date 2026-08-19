using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Models;
using FingerprintAgent.Tests.Scanner;
using Newtonsoft.Json;
using Xunit;

namespace FingerprintAgent.Tests.Api
{
    public class ErrorHandlingTests : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _serverThread;
        private HttpListenerContext _capturedContext;
        private readonly ManualResetEventSlim _contextReady = new ManualResetEventSlim(false);
        private bool _disposed;
        private readonly string _baseUrl;

        public ErrorHandlingTests()
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _baseUrl = string.Format("http://localhost:{0}/", ((IPEndPoint)socket.LocalEndPoint).Port);
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add(_baseUrl);
            _listener.Start();

            _serverThread = new Thread(() =>
            {
                try
                {
                    _capturedContext = _listener.GetContext();
                    _contextReady.Set();
                    _contextReady.Wait(_disposed ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5));
                }
                catch (HttpListenerException) when (_disposed)
                {
                }
            });
            _serverThread.IsBackground = true;
            _serverThread.Start();
        }

        private string BaseUrl => _baseUrl;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _contextReady.Set();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _contextReady.Dispose();
            _serverThread.Join(2000);
        }

        private void ResetContextReady()
        {
            _contextReady.Reset();
        }

        private async Task WaitForContextAsync(int timeoutMs = 5000)
        {
            // ManualResetEventSlim.WaitAsync is not available on .NET Framework 4.8
            // (added in .NET Core 3.0); use Task.Run to offload the blocking wait.
            bool signaled = await Task.Run(() => _contextReady.Wait(timeoutMs));
            if (!signaled)
                throw new TimeoutException(string.Format("HttpListener context not received within {0}ms", timeoutMs));
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

        private static async Task<string> ReadResponseBodyAsync(HttpWebResponse response)
        {
            // HttpWebResponse.GetResponseStreamAsync is not available on .NET Framework 4.8;
            // use the sync stream but still read the body asynchronously via StreamReader.ReadToEndAsync.
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private async Task<HttpWebResponse> SendHttpRequestAsync(string body = "", string path = "/api/capture", string method = "POST", string contentType = "application/json")
        {
            var request = WebRequest.CreateHttp(BaseUrl + path);
            request.Method = method;
            if (method != "GET")
            {
                request.ContentType = contentType;
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bodyBytes.Length;
                using (var rs = await request.GetRequestStreamAsync())
                    await rs.WriteAsync(bodyBytes, 0, bodyBytes.Length);
            }
            return (HttpWebResponse)await request.GetResponseAsync();
        }

        [Fact]
        public async Task CaptureHandler_Returns503_WhenScannerReturnsScannerNotConnected()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false,
                ScanResult = CaptureResult.Fail("SCANNER_NOT_CONNECTED", "No scanner connected"),
                VendorErrorCodeValue = "NO_DEVICE"
            };
            var handler = new CaptureHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, mock);

            var response = await GetResponseAsync(responseTask);
            Assert.Equal(503, (int)response.StatusCode);

            string json = await ReadResponseBodyAsync(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("SCANNER_NOT_CONNECTED", captureResponse.ErrorCode);
            Assert.Equal("NO_DEVICE", captureResponse.VendorErrorCode);
            Assert.NotNull(captureResponse.Timestamp);
        }

        [Fact]
        public async Task CaptureHandler_Returns504_WhenScannerReturnsCaptureTimeout()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Fail("CAPTURE_TIMEOUT", "Capture timed out after 10 seconds"),
                VendorErrorCodeValue = "TIMEOUT_ERR"
            };
            var handler = new CaptureHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, mock);

            var response = await GetResponseAsync(responseTask);
            Assert.Equal(504, (int)response.StatusCode);

            string json = await ReadResponseBodyAsync(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("CAPTURE_TIMEOUT", captureResponse.ErrorCode);
            Assert.Equal("TIMEOUT_ERR", captureResponse.VendorErrorCode);
        }

        [Fact]
        public async Task CaptureHandler_Returns500_WhenScannerReturnsCaptureFailed()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Fail("CAPTURE_FAILED", "Sensor error"),
                VendorErrorCodeValue = "SENSOR_ERR"
            };
            var handler = new CaptureHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, mock);

            var response = await GetResponseAsync(responseTask);
            Assert.Equal(500, (int)response.StatusCode);

            string json = await ReadResponseBodyAsync(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("CAPTURE_FAILED", captureResponse.ErrorCode);
        }

        [Fact]
        public async Task CaptureHandler_Returns400_WhenRequestHasMissingFields()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var handler = new CaptureHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync("{\"thamChieuId\":\"test\"}");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, mock);

            var response = await GetResponseAsync(responseTask);
            Assert.Equal(400, (int)response.StatusCode);

            string json = await ReadResponseBodyAsync(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("INVALID_REQUEST", captureResponse.ErrorCode);
            Assert.Null(captureResponse.VendorErrorCode);
            Assert.NotNull(captureResponse.Timestamp);
        }

        [Fact]
        public async Task CaptureHandler_SuccessResponse_DoesNotIncludeVendorErrorCodeOrTimestamp()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 }),
                VendorErrorCodeValue = "SOME_VENDOR_CODE"
            };
            var handler = new CaptureHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, mock);

            var response = await GetResponseAsync(responseTask);
            Assert.Equal(200, (int)response.StatusCode);

            string json = await ReadResponseBodyAsync(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.True(captureResponse.IsSuccess);
            Assert.Null(captureResponse.VendorErrorCode);
            Assert.Null(captureResponse.Timestamp);
            Assert.Null(captureResponse.ErrorCode);
            Assert.Null(captureResponse.ErrorMessage);
        }

        [Fact]
        public async Task HealthHandler_Returns200_WhenScannerIsConnected()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var handler = new HealthHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync(path: "/health", method: "GET");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, mock);

            var response = await responseTask;
            Assert.Equal(200, (int)response.StatusCode);
        }

        [Fact]
        public async Task HealthHandler_Returns503_WhenDisconnectedAndMaxBackoff()
        {
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var sm = new ScannerManager(new[] { failing }, null);

            for (int i = 0; i < 5; i++)
                await sm.ScanAsync();

            Assert.Equal(3, sm.BackoffStep);
            Assert.True(sm.InBackoff);

            var handler = new HealthHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync(path: "/health", method: "GET");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, sm);

            var response = await GetResponseAsync(responseTask);
            Assert.Equal(503, (int)response.StatusCode);
        }

        [Fact]
        public async Task HealthHandler_Returns200_WhenConnected_RegardlessOfBackoffStep()
        {
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var succeeding = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var sm = new ScannerManager(new[] { failing, succeeding }, null);

            await sm.ScanAsync();
            await sm.ScanAsync();
            await sm.ScanAsync();

            Assert.Equal(0, sm.BackoffStep);

            var handler = new HealthHandler(null);

            ResetContextReady();
            var responseTask = SendHttpRequestAsync(path: "/health", method: "GET");

            await WaitForContextAsync(5000);
            await handler.HandleAsync(_capturedContext, sm);

            var response = await responseTask;
            Assert.Equal(200, (int)response.StatusCode);
        }
    }
}
