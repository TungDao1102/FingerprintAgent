using System;
using System.Collections.Generic;
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
        private readonly List<Task> _inFlightRequests = new List<Task>();
        private readonly object _inFlightLock = new object();
        private bool _disposed;

        public HttpServer(AgentConfig config, IScannerAdapter scanner, AgentLogger logger = null)
        {
            _scanner = scanner;
            _logger = logger;
            _healthHandler = new HealthHandler(_logger);
            _captureHandler = new CaptureHandler(_logger);
            _cors = new CorsMiddleware(config.Cors.Mode, config.Cors.AllowedOrigins);

            _listener = new HttpListener();

            // Resolve bind address. security.bindIp is the AUTHORITATIVE source of truth
            // (the operator's intent for which IP the service binds to). http.host is
            // retained for backward compatibility but ignored when BindIp is set or when
            // they disagree — this prevents the silent-exposure bug where http.host="0.0.0.0"
            // would override an operator-set BindIp="127.0.0.1".
            string bindAddress = !string.IsNullOrEmpty(config.Security?.BindIp)
                ? config.Security.BindIp
                : (!string.IsNullOrEmpty(config.Http?.Host) ? config.Http.Host : "127.0.0.1");

            // If operator explicitly set both, and they disagree, prefer BindIp and log.
            // Never let http.host silently override the security boundary.
            if (!string.IsNullOrEmpty(config.Http?.Host)
                && !string.IsNullOrEmpty(config.Security?.BindIp)
                && !string.Equals(config.Http.Host, config.Security.BindIp, StringComparison.Ordinal))
            {
                _logger?.Warn(null,
                    $"HttpServer: config.http.host='{config.Http.Host}' ignored; " +
                    $"using config.security.bindIp='{config.Security.BindIp}' " +
                    "(security.bindIp takes precedence)");
            }

            _listener.Prefixes.Add($"http://{bindAddress}:{config.Http.Port}/");
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

        /// <summary>
        /// Stops the HTTP server and waits up to 30 seconds for in-flight requests to drain.
        /// Safe to call multiple times (idempotent) - subsequent calls return immediately.
        /// </summary>
        public void Stop()
        {
            if (_disposed)
                return;

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
                catch (AggregateException) { }

                Task[] inFlight;
                lock (_inFlightLock) { inFlight = _inFlightRequests.ToArray(); }
                if (inFlight.Length > 0)
                {
                    try
                    {
                        Task.WaitAll(inFlight, TimeSpan.FromSeconds(30));
                    }
                    catch (AggregateException) { }
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
                    var handlerTask = HandleRequestAsync(context, ct);
                    lock (_inFlightLock) _inFlightRequests.Add(handlerTask);
                    _ = handlerTask.ContinueWith(t =>
                    {
                        lock (_inFlightLock) _inFlightRequests.Remove(t);
                        if (t.IsFaulted)
                        {
                            _logger?.Error(AgentLogger.GenerateCorrelationId(), $"Unhandled request error: {t.Exception}");
                        }
                    }, TaskScheduler.Default);
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

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            var correlationId = AgentLogger.GenerateCorrelationId();
            try
            {
                if (ct.IsCancellationRequested)
                {
                    try
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                    }
                    catch (ObjectDisposedException) { }
                    return;
                }

                var origin = context.Request.Headers["Origin"];

                // CORS preflight check
                if (_cors.HandleCorsPreflight(context.Request, context.Response))
                    return;

                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                var method = context.Request.HttpMethod;

                // Set default content type
                context.Response.ContentType = "application/json";

                // Apply CORS headers before writing response (headers must be set before OutputStream is flushed/closed)
                _cors.ApplyCorsHeaders(context.Response, origin);

                if (path == "/health" && method == "GET")
                {
                    await _healthHandler.HandleAsync(context, _scanner, correlationId);
                }
                else if (path == "/api/capture" && method == "POST")
                {
                    await _captureHandler.HandleAsync(context, _scanner, correlationId);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    var errorBytes = Encoding.UTF8.GetBytes("{\"error\":\"Not found\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = errorBytes.Length;
                    await context.Response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length);
                    await context.Response.OutputStream.FlushAsync();
                    context.Response.OutputStream.Close();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(correlationId, $"HandleRequest: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Updates CORS configuration at runtime. Thread-safe.
        /// </summary>
        public void UpdateCorsConfig(CorsConfig newCors)
        {
            _cors.UpdateConfig(newCors.Mode, newCors.AllowedOrigins);
        }

        /// <summary>
        /// Disposes the server. Calls Stop() internally.
        /// Safe to call multiple times (idempotent) - subsequent calls return immediately.
        /// </summary>
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
