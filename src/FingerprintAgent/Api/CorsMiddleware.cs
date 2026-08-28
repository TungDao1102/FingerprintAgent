using System;
using System.Collections.Generic;
using System.Net;

namespace FingerprintAgent.Api
{
    public class CorsMiddleware
    {
        private string _mode;
        private HashSet<string> _allowedOrigins;
        private readonly object _corsLock = new object();

        public CorsMiddleware(string mode, string[] allowedOrigins)
        {
            _mode = mode ?? "wildcard";
            _allowedOrigins = new HashSet<string>(
                allowedOrigins ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Updates CORS configuration at runtime. Thread-safe.
        /// null mode is treated as "wildcard". null allowedOrigins is treated as empty.
        /// </summary>
        public void UpdateConfig(string mode, string[] allowedOrigins)
        {
            lock (_corsLock)
            {
                _mode = mode ?? "wildcard";
                _allowedOrigins = new HashSet<string>(
                    allowedOrigins ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public bool HandleCorsPreflight(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "OPTIONS")
                return false;

            var origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin))
                return false;

            string mode;
            HashSet<string> allowedOrigins;
            lock (_corsLock) { mode = _mode; allowedOrigins = _allowedOrigins; }

            // Security: reject BEFORE applying headers — denied origins must not see CORS policy.
            if (mode == "allowlist" && !allowedOrigins.Contains(origin))
            {
                response.StatusCode = 403;
                response.Close();
                return true;
            }

            ApplyCorsHeaders(response, origin, request.Headers["Access-Control-Request-Headers"]);

            response.StatusCode = 204;
            response.Close();
            return true;
        }

        public void ApplyCorsHeaders(HttpListenerResponse response, string origin, string requestedHeaders = null)
        {
            if (string.IsNullOrEmpty(origin))
                return;

            string mode;
            HashSet<string> allowedOrigins;
            lock (_corsLock) { mode = _mode; allowedOrigins = _allowedOrigins; }

            if (mode == "wildcard")
            {
                response.Headers.Add("Access-Control-Allow-Origin", "*");
            }
            else if (mode == "allowlist" && allowedOrigins.Contains(origin))
            {
                response.Headers.Add("Access-Control-Allow-Origin", origin);
                response.Headers.Add("Vary", "Origin");
            }

            response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers",
                string.IsNullOrWhiteSpace(requestedHeaders) ? "Content-Type" : requestedHeaders);
            response.Headers.Add("Access-Control-Max-Age", "86400");
        }
    }
}
