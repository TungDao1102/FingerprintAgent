using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;

namespace FingerprintAgent.Api
{
    public class HttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _host;
        private readonly int _port;
        private readonly IScannerAdapter _scanner;
        private readonly HealthHandler _healthHandler;
        private readonly CaptureHandler _captureHandler;
        private CancellationTokenSource _cts;
        private Task _workerTask;
        private bool _disposed;

        public HttpServer(string host, int port, IScannerAdapter scanner)
        {
            _host = host;
            _port = port;
            _scanner = scanner;
            _healthHandler = new HealthHandler();
            _captureHandler = new CaptureHandler();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{host}:{port}/");
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener.Start();
            _workerTask = Task.Factory.StartNew(
                () => ProcessRequestLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
            }
            finally
            {
                try
                {
                    if (_listener.IsListening)
                    {
                        _listener.Stop();
                    }
                }
                catch (ObjectDisposedException) { }

                try
                {
                    _workerTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // Swallow exceptions from the stopped listener loop
                }

                _listener.Close();
            }
        }

        private async Task ProcessRequestLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
#pragma warning disable CS4014
                    Task.Run(() => HandleRequest(context), ct);
#pragma warning restore CS4014
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                var method = context.Request.HttpMethod;

                // Set default content type
                context.Response.ContentType = "application/json";

                if (path == "/health" && method == "GET")
                {
                    _healthHandler.Handle(context, _scanner);
                }
                else if (path == "/api/capture" && method == "POST")
                {
                    _captureHandler.Handle(context, _scanner);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    var errorBytes = Encoding.UTF8.GetBytes("{\"error\":\"Not found\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = errorBytes.Length;
                    context.Response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
                    context.Response.OutputStream.Close();
                }
            }
            catch (Exception)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
                _cts?.Dispose();
            }
        }
    }
}
