using System;
using System.IO;
using System.Net;
using System.Text;
using FingerprintAgent.Adapters;
using FingerprintAgent.Models;
using Newtonsoft.Json;

namespace FingerprintAgent.Api
{
    public class CaptureHandler
    {
        public void Handle(HttpListenerContext context, IScannerAdapter scanner)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    body = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    WriteErrorResponse(context, 400, false, "Request body is empty", "INVALID_REQUEST");
                    return;
                }

                CaptureRequest request;
                try
                {
                    request = JsonConvert.DeserializeObject<CaptureRequest>(body);
                }
                catch (JsonException)
                {
                    WriteErrorResponse(context, 400, false, "Invalid JSON in request body", "INVALID_REQUEST");
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.ThamChieuId))
                {
                    WriteErrorResponse(context, 400, false, "Missing required field: thamChieuId", "INVALID_REQUEST");
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.MaPhieu))
                {
                    WriteErrorResponse(context, 400, false, "Missing required field: maPhieu", "INVALID_REQUEST");
                    return;
                }

                CaptureResult result = scanner.Scan();

                var response = new CaptureResponse
                {
                    IsSuccess = true,
                    ImageBytes = Convert.ToBase64String(result.ImageBytes),
                    MimeType = result.MimeType,
                    CapturedAt = DateTime.UtcNow.ToString("O"),
                    DeviceId = scanner.DeviceId,
                    VerificationData = result.VerificationData,
                    ErrorMessage = null
                };

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
                WriteErrorResponse(context, 500, false, $"Capture failed: {ex.Message}", "CAPTURE_FAILED");
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
