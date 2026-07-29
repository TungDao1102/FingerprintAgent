using System;
using System.IO;
using System.Net;
using System.Text;
using FingerprintAgent.Adapters;
using FingerprintAgent.Logging;
using FingerprintAgent.Models;
using Newtonsoft.Json;

namespace FingerprintAgent.Api
{
    public class CaptureHandler
    {
        private readonly AgentLogger _logger;

        public CaptureHandler(AgentLogger logger = null)
        {
            _logger = logger;
        }

        public void Handle(HttpListenerContext context, IScannerAdapter scanner, string correlationId = null)
        {
            if (string.IsNullOrEmpty(correlationId))
            {
                correlationId = AgentLogger.GenerateCorrelationId();
            }

            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    body = reader.ReadToEnd();
                }

                _logger?.Info(correlationId, "Capture request received");

                if (string.IsNullOrWhiteSpace(body))
                {
                    const string errorMessage = "Request body is empty";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    WriteErrorResponse(context, 400, false, errorMessage, "INVALID_REQUEST");
                    return;
                }

                CaptureRequest request;
                try
                {
                    request = JsonConvert.DeserializeObject<CaptureRequest>(body);
                }
                catch (JsonException)
                {
                    const string errorMessage = "Invalid JSON in request body";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    WriteErrorResponse(context, 400, false, errorMessage, "INVALID_REQUEST");
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.ThamChieuId))
                {
                    const string errorMessage = "Missing required field: thamChieuId";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    WriteErrorResponse(context, 400, false, errorMessage, "INVALID_REQUEST");
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.MaPhieu))
                {
                    const string errorMessage = "Missing required field: maPhieu";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    WriteErrorResponse(context, 400, false, errorMessage, "INVALID_REQUEST");
                    return;
                }

                CaptureResult result = scanner.Scan();

                var imageBytes = result.ImageBytes ?? Array.Empty<byte>();
                var response = new CaptureResponse
                {
                    IsSuccess = true,
                    ImageBytes = Convert.ToBase64String(imageBytes),
                    MimeType = result.MimeType,
                    CapturedAt = DateTime.UtcNow.ToString("O"),
                    DeviceId = scanner.DeviceId,
                    VerificationData = result.VerificationData,
                    ErrorMessage = null
                };

                _logger?.Info(correlationId, $"Capture completed — deviceId: {scanner.DeviceId}");

                string json = JsonConvert.SerializeObject(response);
                byte[] buffer = Encoding.UTF8.GetBytes(json);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                var errorMessage = $"Capture failed: {ex.Message}";
                _logger?.Error(correlationId, $"Capture failed — CAPTURE_FAILED: {ex.Message}");
                WriteErrorResponse(context, 500, false, errorMessage, "CAPTURE_FAILED");
            }
        }

        private static void WriteErrorResponse(HttpListenerContext context, int statusCode, bool isSuccess, string errorMessage, string errorCode)
        {
            var response = new CaptureResponse
            {
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode
            };

            string json = JsonConvert.SerializeObject(response);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }
    }
}
