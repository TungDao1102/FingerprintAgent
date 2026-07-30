using System;
using System.Net;
using System.Text;
using FingerprintAgent.Adapters;
using FingerprintAgent.Logging;
using Newtonsoft.Json;

namespace FingerprintAgent.Api
{
    public class HealthHandler
    {
        private readonly DateTime _startTime;
        private readonly AgentLogger _logger;

        public HealthHandler(AgentLogger logger = null)
        {
            _startTime = DateTime.UtcNow;
            _logger = logger;
        }

        public void Handle(HttpListenerContext context, IScannerAdapter scanner, string correlationId = null)
        {
            _logger?.Debug(correlationId ?? AgentLogger.GenerateCorrelationId(), "Health check requested");

            var inBackoff = (scanner as ScannerManager)?.InBackoff ?? false;
            var backoffStep = (scanner as ScannerManager)?.BackoffStep ?? 0;
            bool connected = scanner.IsConnected;

            string status = connected ? "healthy" : "degraded";
            int httpStatus = (connected || backoffStep < 3) ? 200 : 503;

            var response = new
            {
                status,
                deviceId = scanner.DeviceId,
                uptime = (DateTime.UtcNow - _startTime).ToString(@"hh\:mm\:ss"),
                inBackoff,
                backoffStep
            };

            string json = JsonConvert.SerializeObject(response);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = httpStatus;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }
    }
}
