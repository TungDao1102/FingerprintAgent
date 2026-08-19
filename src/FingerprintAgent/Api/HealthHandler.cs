using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
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

        public async Task HandleAsync(HttpListenerContext context, IScannerAdapter scanner, string correlationId = null)
        {
            _logger?.Debug(correlationId ?? AgentLogger.GenerateCorrelationId(), "Health check requested");

            var mgr = scanner as ScannerManager;
            var inBackoff = mgr?.InBackoff ?? false;
            var backoffStep = mgr?.BackoffStep ?? 0;

            bool connected;
            string deviceId;
            string model;
            string vendorErrorCode;
            if (mgr != null)
            {
                connected = mgr.TryProbe(out deviceId, out model, out vendorErrorCode);
            }
            else
            {
                connected = scanner.IsConnected;
                deviceId = scanner.DeviceId;
                model = scanner.Model;
                vendorErrorCode = scanner.VendorErrorCode;
            }

            string status = connected ? "healthy" : "degraded";
            int httpStatus = (connected || backoffStep < 3) ? 200 : 503;

            var response = new
            {
                status,
                deviceId,
                model,
                vendorErrorCode,
                uptime = (DateTime.UtcNow - _startTime).ToString(@"hh\:mm\:ss"),
                inBackoff,
                backoffStep
            };

            string json = JsonConvert.SerializeObject(response);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = httpStatus;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            await context.Response.OutputStream.FlushAsync();
            context.Response.OutputStream.Close();
        }
    }
}
