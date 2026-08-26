using System;
using System.Collections.Generic;
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
        private const int MetadataMaxKeys = 20;
        private const int MetadataMaxFieldLength = 100;

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
                {
                    const string errorMessage = "Request body too large";
                    _logger?.Error(correlationId, $"Capture rejected — PAYLOAD_TOO_LARGE: {errorMessage}");
                    await WriteErrorResponseAsync(context, 413, false, errorMessage, "PAYLOAD_TOO_LARGE", null, null, correlationId, requestId: null);
                    return;
                }

                _logger?.Info(correlationId, "Capture request received");

                if (string.IsNullOrWhiteSpace(body))
                {
                    const string errorMessage = "Request body is empty";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    await WriteErrorResponseAsync(context, 400, false, errorMessage, "INVALID_REQUEST", null, null, correlationId, requestId: null);
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
                    await WriteErrorResponseAsync(context, 400, false, errorMessage, "INVALID_REQUEST", null, null, correlationId, requestId: null);
                    return;
                }

                // JSON literal "null" deserializes to a null instance — reject as 400, not NRE → 500.
                if (request == null)
                {
                    const string nullBodyError = "Invalid JSON in request body";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {nullBodyError}");
                    await WriteErrorResponseAsync(context, 400, false, nullBodyError, "INVALID_REQUEST", null, null, correlationId, requestId: null);
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.RequestId))
                {
                    const string errorMessage = "Missing required field: requestId";
                    _logger?.Error(correlationId, $"Capture failed — INVALID_REQUEST: {errorMessage}");
                    await WriteErrorResponseAsync(context, 400, false, errorMessage, "INVALID_REQUEST", null, null, correlationId, requestId: null);
                    return;
                }

                var sanitizedMetadata = SanitizeMetadata(request.Metadata, correlationId);
                LogRequestContext(correlationId, request, sanitizedMetadata);

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
                    await WriteErrorResponseAsync(context, statusCode, false, result.ErrorMessage, errorCode, vendorErrorCode, timestamp, correlationId, requestId: request.RequestId);
                    return;
                }

                var imageBytes = result.ImageBytes ?? Array.Empty<byte>();
                var response = new CaptureResponse
                {
                    RequestId = request.RequestId,
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

                await WriteJsonResponseAsync(context, 200, response);
            }
            catch (Exception ex)
            {
                var errorMessage = $"Capture failed: {ex.Message}";
                _logger?.Error(correlationId, $"Capture failed — CAPTURE_FAILED: {ex.Message}");
                // request is out of scope in the catch — try to recover requestId from a re-parse is not worth it.
                await WriteErrorResponseAsync(context, 500, false, errorMessage, "CAPTURE_FAILED", null, null, correlationId, requestId: null);
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

        /// <summary>
        /// Drops oversized metadata entries to keep the agent cheap and predictable.
        /// - More than MetadataMaxKeys entries: keep the first N (deterministic order from the caller's JSON).
        /// - Key or value longer than MetadataMaxFieldLength: drop the entry.
        /// Null/empty input returns null (caller distinguishes "no metadata" from "empty metadata").
        /// </summary>
        private Dictionary<string, string> SanitizeMetadata(Dictionary<string, string> metadata, string correlationId)
        {
            if (metadata == null)
            {
                return null;
            }

            var sanitized = new Dictionary<string, string>(Math.Min(metadata.Count, MetadataMaxKeys));
            int dropped = 0;
            foreach (var kvp in metadata)
            {
                if (sanitized.Count >= MetadataMaxKeys)
                {
                    dropped++;
                    continue;
                }
                if (string.IsNullOrEmpty(kvp.Key)
                    || kvp.Key.Length > MetadataMaxFieldLength
                    || kvp.Value == null
                    || kvp.Value.Length > MetadataMaxFieldLength)
                {
                    dropped++;
                    continue;
                }
                sanitized[kvp.Key] = kvp.Value;
            }

            if (dropped > 0)
            {
                _logger?.Warn(correlationId, $"metadata: dropped {dropped} entry(ies) (max {MetadataMaxKeys} keys, {MetadataMaxFieldLength} chars/key/value)");
            }

            return sanitized;
        }

        private void LogRequestContext(string correlationId, CaptureRequest request, Dictionary<string, string> sanitizedMetadata)
        {
            _logger?.Info(correlationId, $"requestId: {request.RequestId}");
            if (!string.IsNullOrEmpty(request.Purpose))
            {
                _logger?.Info(correlationId, $"purpose: {request.Purpose}");
            }
            if (sanitizedMetadata != null && sanitizedMetadata.Count > 0)
            {
                var sb = new StringBuilder("metadata: ");
                bool first = true;
                foreach (var kvp in sanitizedMetadata)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(kvp.Key).Append('=').Append(kvp.Value);
                    first = false;
                }
                _logger?.Debug(correlationId, sb.ToString());
            }
        }

        private async Task WriteJsonResponseAsync(HttpListenerContext context, int statusCode, CaptureResponse response)
        {
            string json = JsonConvert.SerializeObject(response);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            await context.Response.OutputStream.FlushAsync();
            context.Response.OutputStream.Close();
        }

        private async Task WriteErrorResponseAsync(HttpListenerContext context, int statusCode, bool isSuccess, string errorMessage, string errorCode, string vendorErrorCode, string timestamp, string correlationId = null, string requestId = null)
        {
            var response = new CaptureResponse
            {
                RequestId = requestId,
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
                VendorErrorCode = vendorErrorCode,
                Timestamp = timestamp ?? DateTime.UtcNow.ToString("O")
            };

            await WriteJsonResponseAsync(context, statusCode, response);
        }
    }
}
