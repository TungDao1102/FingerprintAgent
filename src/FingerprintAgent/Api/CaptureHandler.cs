using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        private const int MaxBodyBytes = 1 * 1024 * 1024;

        public async Task HandleAsync(HttpListenerContext context, IScannerAdapter scanner, string correlationId = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(correlationId))
            {
                correlationId = AgentLogger.GenerateCorrelationId();
            }

            try
            {
                var (body, tooLarge) = await ReadBodyAsync(
                    // Deliberately no shutdown token here: it would abort pre-cancelled requests
                    // and turn the documented 503 into a 500. The 1 MB cap bounds this read.
                    context.Request.InputStream, context.Request.ContentEncoding, MaxBodyBytes, CancellationToken.None);

                if (tooLarge)

                if (tooLarge)
                {
                    const string errorMessage = "Request body too large";
                    _logger?.Error(correlationId, $"Capture rejected — PAYLOAD_TOO_LARGE: {errorMessage}");
                    await WriteErrorResponseAsync(context, 413, false, errorMessage, "PAYLOAD_TOO_LARGE", null, null, correlationId);
                    return;
                }

                _logger?.Info(correlationId, "Capture request received");

                if (string.IsNullOrWhiteSpace(body))
                {
                    const string errorMessage = "Request body is empty";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    await WriteErrorResponseAsync(context, 400, false, errorMessage, "INVALID_REQUEST", null, null, correlationId);
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
                    await WriteErrorResponseAsync(context, 400, false, errorMessage, "INVALID_REQUEST", null, null, correlationId);
                    return;
                }

                // JSON literal "null" deserializes to a null instance — reject as 400, not NRE → 500.
                if (request == null)
                {
                    const string nullBodyError = "Invalid JSON in request body";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {nullBodyError}");
                    await WriteErrorResponseAsync(context, 400, false, nullBodyError, "INVALID_REQUEST", null, null, correlationId);
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.ThamChieuId))
                {
                    const string errorMessage = "Missing required field: thamChieuId";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    await WriteErrorResponseAsync(context, 400, false, errorMessage, "INVALID_REQUEST", null, null, correlationId);
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.MaPhieu))
                {
                    const string errorMessage = "Missing required field: maPhieu";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    await WriteErrorResponseAsync(context, 400, false, errorMessage, "INVALID_REQUEST", null, null, correlationId);
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger?.Warn(correlationId, "Capture aborted before scanner.ScanAsync: shutdown requested");
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                CaptureResult result = await scanner.ScanAsync(cancellationToken);
                var vendorErrorCode = scanner.VendorErrorCode;

                if (!result.IsSuccess)
                {
                    var (statusCode, errorCode) = MapErrorCode(result.ErrorCode);
                    var timestamp = DateTime.UtcNow.ToString("O");

                    _logger?.Error(correlationId, $"Capture failed — {errorCode}: {result.ErrorMessage}");
                    await WriteErrorResponseAsync(context, statusCode, false, result.ErrorMessage, errorCode, vendorErrorCode, timestamp, correlationId);
                    return;
                }

                var imageBytes = result.ImageBytes ?? Array.Empty<byte>();
                var response = new CaptureResponse
                {
                    IsSuccess = true,
                    ImageBytes = Convert.ToBase64String(imageBytes),
                    MimeType = result.MimeType,
                    CapturedAt = DateTime.UtcNow.ToString("O"),
                    DeviceId = scanner.DeviceId,
                    VerificationData = result.VerificationData,
                    ErrorMessage = null,
                    ErrorCode = null
                };

                _logger?.Info(correlationId, $"Capture completed — deviceId: {scanner.DeviceId}");

                string json = JsonConvert.SerializeObject(response);
                byte[] buffer = Encoding.UTF8.GetBytes(json);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                await context.Response.OutputStream.FlushAsync();
                context.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                var errorMessage = $"Capture failed: {ex.Message}";
                _logger?.Error(correlationId, $"Capture failed — CAPTURE_FAILED: {ex.Message}");
                await WriteErrorResponseAsync(context, 500, false, errorMessage, "CAPTURE_FAILED", null, null, correlationId);
            }
        }

        private static (int statusCode, string errorCode) MapErrorCode(string errorCode)
        {
            switch (errorCode)
            {
                case "SCANNER_NOT_CONNECTED":
                    return (503, errorCode);
                case "CAPTURE_TIMEOUT":
                    return (504, errorCode);
                case "INVALID_REQUEST":
                    return (400, errorCode);
                case "CAPTURE_FAILED":
                    return (500, errorCode);
                case "CONFIG_ERROR":
                    return (500, errorCode);
                default:
                    return (500, errorCode ?? "CAPTURE_FAILED");
            }
        }

        /// <summary>Reads at most maxBytes; tooLarge=true when the payload exceeds the cap.</summary>
        private static async Task<(string body, bool tooLarge)> ReadBodyAsync(
            Stream stream, Encoding encoding, int maxBytes, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + read > maxBytes)
                        return (null, true);
                    ms.Write(buffer, 0, read);
                }
                return (encoding.GetString(ms.ToArray()), false);
            }
        }

        private async Task WriteErrorResponseAsync(HttpListenerContext context, int statusCode, bool isSuccess, string errorMessage, string errorCode, string vendorErrorCode, string timestamp, string correlationId = null)
        {
            var response = new CaptureResponse
            {
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
                VendorErrorCode = vendorErrorCode,
                Timestamp = timestamp ?? DateTime.UtcNow.ToString("O")
            };

            string json = JsonConvert.SerializeObject(response);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            await context.Response.OutputStream.FlushAsync();
            context.Response.OutputStream.Close();
        }
    }
}