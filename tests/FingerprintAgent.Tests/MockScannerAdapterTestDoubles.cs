using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FingerprintAgent.Adapters;

namespace FingerprintAgent.Tests
{
    /// <summary>
    /// Test double for IScannerAdapter with fully settable properties.
    /// Resolves CONCERN 2 from code review.
    /// </summary>
    public class MockScannerAdapterWithSettableProperties : IScannerAdapter
    {
        public bool IsConnectedValue { get; set; } = true;
        public bool InitializeResult { get; set; } = true;
        public bool ProbeConnectionResult { get; set; } = true;
        public CaptureResult ScanResult { get; set; } = CaptureResult.Ok(new byte[] { 1, 2, 3 });
        public string VendorErrorCodeValue { get; set; } = "MOCK";
        public string DeviceIdValue { get; set; } = "mock-test-device";
        public string ModelValue { get; set; } = "Mock Scanner (Test Double)";
        public string MimeTypeValue { get; set; } = "image/png";

        public bool IsConnected => IsConnectedValue;
        public string DeviceId => DeviceIdValue;
        public string Model => ModelValue;
        public string MimeType => MimeTypeValue;

        public bool Initialize() => InitializeResult;

        public bool ProbeConnection() => ProbeConnectionResult;

        public CaptureResult Scan() => ScanResult;

        public string VendorErrorCode => VendorErrorCodeValue;
    }

    /// <summary>
    /// Creates a real HttpListener context on a random free port for integration testing
    /// of CaptureHandler and other HttpListener-based handlers.
    /// Resolves CONCERN 3 from code review.
    /// </summary>
    public class CaptureHandlerTestFixture : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _serverThread;
        private readonly ManualResetEventSlim _contextReady = new ManualResetEventSlim(false);
        private HttpListenerContext _capturedContext;
        private bool _disposed;
        private readonly string _baseUrl;

        public HttpListenerContext CapturedContext => _capturedContext;
        public string BaseUrl => _baseUrl;

        public CaptureHandlerTestFixture()
        {
            // Use a socket to find an available port since HttpListener on .NET Framework
            // doesn't expose LocalEndpoint without a bound prefix
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _baseUrl = $"http://localhost:{((IPEndPoint)socket.LocalEndPoint).Port}/";
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
                    // Expected on disposal
                }
            });
            _serverThread.IsBackground = true;
            _serverThread.Start();
        }

        /// <summary>
        /// Waits for the context to be captured, then returns it.
        /// Throws if timeout is exceeded.
        /// </summary>
        public HttpListenerContext WaitForContext(int timeoutMs = 5000)
        {
            if (!_contextReady.Wait(timeoutMs))
                throw new TimeoutException($"HttpListener context not received within {timeoutMs}ms");
            return _capturedContext;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _contextReady.Set();
            _contextReady.Dispose();
            _serverThread.Join(2000);
        }
    }
}