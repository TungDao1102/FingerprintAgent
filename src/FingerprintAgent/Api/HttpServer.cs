using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;

namespace FingerprintAgent.Api
{
    public class HttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly IScannerAdapter _scanner;
        private readonly HealthHandler _healthHandler;
        private readonly CaptureHandler _captureHandler;
        private readonly CorsMiddleware _cors;
        private readonly AgentLogger _logger;
        private CancellationTokenSource _cts;
        private Task _workerTask;
        private bool _disposed;

        public HttpServer(AgentConfig config, IScannerAdapter scanner, AgentLogger logger = null)
        {
            _scanner = scanner;
            _logger = logger;
            _healthHandler = new HealthHandler(_logger);
            _captureHandler = new CaptureHandler(_logger);
            _cors = new CorsMiddleware(config.Cors.Mode, config.Cors.AllowedOrigins);

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{config.Http.Host}:{config.Http.Port}/");
        }

        // Keep the old constructor for backward compatibility
        public HttpServer(string host, int port, IScannerAdapter scanner)
            : this(new AgentConfig
            {
                Http = new HttpConfig { Host = host, Port = port }
            }, scanner, null)
        {
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
                var origin = context.Request.Headers["Origin"];

                // CORS preflight check
                if (_cors.HandleCorsPreflight(context.Request, context.Response))
                    return;

                // Apply CORS headers for actual requests
                _cors.ApplyCorsHeaders(context.Response, origin);

                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                var method = context.Request.HttpMethod;

                // Set default content type
                context.Response.ContentType = "application/json";

                var correlationId = AgentLogger.GenerateCorrelationId();

                if (path == "/health" && method == "GET")
                {
                    _healthHandler.Handle(context, _scanner, correlationId);
                }
                else if (path == "/api/capture" && method == "POST")
                {
                    _captureHandler.Handle(context, _scanner, correlationId);
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
