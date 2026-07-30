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
using Newtonsoft.Json;
using Xunit;

namespace FingerprintAgent.Tests
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

        private void WaitForContext(int timeoutMs = 5000)
        {
            if (!_contextReady.Wait(timeoutMs))
                throw new TimeoutException(string.Format("HttpListener context not received within {0}ms", timeoutMs));
        }

        private static HttpWebResponse GetResponse(Task<HttpWebResponse> responseTask)
        {
            try
            {
                return responseTask.GetAwaiter().GetResult();
            }
            catch (WebException ex)
            {
                return (HttpWebResponse)ex.Response;
            }
        }

        private static string ReadResponseBody(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        [Fact]
        public void CaptureHandler_Returns503_WhenScannerReturnsScannerNotConnected()
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
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/api/capture");
                request.Method = "POST";
                request.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");
                request.ContentLength = body.Length;
                using (var rs = request.GetRequestStream())
                    rs.Write(body, 0, body.Length);
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, mock);

            var response = GetResponse(responseTask);
            Assert.Equal(503, (int)response.StatusCode);

            string json = ReadResponseBody(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("SCANNER_NOT_CONNECTED", captureResponse.ErrorCode);
            Assert.Equal("NO_DEVICE", captureResponse.VendorErrorCode);
            Assert.NotNull(captureResponse.Timestamp);
        }

        [Fact]
        public void CaptureHandler_Returns504_WhenScannerReturnsCaptureTimeout()
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
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/api/capture");
                request.Method = "POST";
                request.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");
                request.ContentLength = body.Length;
                using (var rs = request.GetRequestStream())
                    rs.Write(body, 0, body.Length);
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, mock);

            var response = GetResponse(responseTask);
            Assert.Equal(504, (int)response.StatusCode);

            string json = ReadResponseBody(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("CAPTURE_TIMEOUT", captureResponse.ErrorCode);
            Assert.Equal("TIMEOUT_ERR", captureResponse.VendorErrorCode);
        }

        [Fact]
        public void CaptureHandler_Returns500_WhenScannerReturnsCaptureFailed()
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
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/api/capture");
                request.Method = "POST";
                request.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");
                request.ContentLength = body.Length;
                using (var rs = request.GetRequestStream())
                    rs.Write(body, 0, body.Length);
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, mock);

            var response = GetResponse(responseTask);
            Assert.Equal(500, (int)response.StatusCode);

            string json = ReadResponseBody(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("CAPTURE_FAILED", captureResponse.ErrorCode);
        }

        [Fact]
        public void CaptureHandler_Returns400_WhenRequestHasMissingFields()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var handler = new CaptureHandler(null);

            ResetContextReady();
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/api/capture");
                request.Method = "POST";
                request.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes("{\"thamChieuId\":\"test\"}");
                request.ContentLength = body.Length;
                using (var rs = request.GetRequestStream())
                    rs.Write(body, 0, body.Length);
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, mock);

            var response = GetResponse(responseTask);
            Assert.Equal(400, (int)response.StatusCode);

            string json = ReadResponseBody(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.False(captureResponse.IsSuccess);
            Assert.Equal("INVALID_REQUEST", captureResponse.ErrorCode);
            Assert.Null(captureResponse.VendorErrorCode);
            Assert.NotNull(captureResponse.Timestamp);
        }

        [Fact]
        public void CaptureHandler_SuccessResponse_DoesNotIncludeVendorErrorCodeOrTimestamp()
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
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/api/capture");
                request.Method = "POST";
                request.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");
                request.ContentLength = body.Length;
                using (var rs = request.GetRequestStream())
                    rs.Write(body, 0, body.Length);
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, mock);

            var response = GetResponse(responseTask);
            Assert.Equal(200, (int)response.StatusCode);

            string json = ReadResponseBody(response);
            var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

            Assert.True(captureResponse.IsSuccess);
            Assert.Null(captureResponse.VendorErrorCode);
            Assert.Null(captureResponse.Timestamp);
            Assert.Null(captureResponse.ErrorCode);
            Assert.Null(captureResponse.ErrorMessage);
        }

        [Fact]
        public void HealthHandler_Returns200_WhenScannerIsConnected()
        {
            var mock = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = true,
                InitializeResult = true,
                ScanResult = CaptureResult.Ok(new byte[] { 1, 2, 3 })
            };
            var handler = new HealthHandler(null);

            ResetContextReady();
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/health");
                request.Method = "GET";
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, mock);

            var response = responseTask.GetAwaiter().GetResult();
            Assert.Equal(200, (int)response.StatusCode);
        }

        [Fact]
        public void HealthHandler_Returns503_WhenDisconnectedAndMaxBackoff()
        {
            var failing = new MockScannerAdapterWithSettableProperties
            {
                IsConnectedValue = false,
                InitializeResult = false
            };
            var sm = new ScannerManager(new[] { failing }, null);

            for (int i = 0; i < 5; i++)
                sm.Scan();

            Assert.Equal(3, sm.BackoffStep);
            Assert.True(sm.InBackoff);

            var handler = new HealthHandler(null);

            ResetContextReady();
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/health");
                request.Method = "GET";
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, sm);

            var response = GetResponse(responseTask);
            Assert.Equal(503, (int)response.StatusCode);
        }

        [Fact]
        public void HealthHandler_Returns200_WhenConnected_RegardlessOfBackoffStep()
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

            sm.Scan();
            sm.Scan();
            sm.Scan();

            Assert.Equal(0, sm.BackoffStep);

            var handler = new HealthHandler(null);

            ResetContextReady();
            var responseTask = Task.Run(() =>
            {
                var request = WebRequest.CreateHttp(BaseUrl + "/health");
                request.Method = "GET";
                return (HttpWebResponse)request.GetResponse();
            });

            WaitForContext(5000);
            handler.Handle(_capturedContext, sm);

            var response = responseTask.GetAwaiter().GetResult();
            Assert.Equal(200, (int)response.StatusCode);
        }
    }
}