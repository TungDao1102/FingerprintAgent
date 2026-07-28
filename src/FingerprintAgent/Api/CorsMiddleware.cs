using System;
using System.Collections.Generic;
using System.Net;

namespace FingerprintAgent.Api
{
    public class CorsMiddleware
    {
        private readonly string _mode;
        private readonly HashSet<string> _allowedOrigins;

        public CorsMiddleware(string mode, string[] allowedOrigins)
        {
            _mode = mode ?? "wildcard";
            _allowedOrigins = new HashSet<string>(
                allowedOrigins ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        public bool HandleCorsPreflight(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "OPTIONS")
                return false;

            var origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin))
                return false;

            ApplyCorsHeaders(response, origin);

            if (_mode == "allowlist" && !_allowedOrigins.Contains(origin))
            {
                response.StatusCode = 403;
                response.Close();
                return true;
            }

            response.StatusCode = 204;
            response.Close();
            return true;
        }

        public void ApplyCorsHeaders(HttpListenerResponse response, string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return;

            if (_mode == "wildcard")
            {
                response.Headers.Add("Access-Control-Allow-Origin", "*");
            }
            else if (_mode == "allowlist" && _allowedOrigins.Contains(origin))
            {
                response.Headers.Add("Access-Control-Allow-Origin", origin);
                response.Headers.Add("Vary", "Origin");
            }

            response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            response.Headers.Add("Access-Control-Max-Age", "86400");
        }
    }
}
