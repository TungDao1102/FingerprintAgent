using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintAgent.Tests.Update
{
    /// <summary>
    /// Test double for HttpMessageHandler. Returns canned responses based on URL pattern.
    /// Tracks call count + last URL for assertions.
    /// </summary>
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<(Func<Uri, bool> Match, HttpResponseMessage Response)> _responses
            = new List<(Func<Uri, bool>, HttpResponseMessage)>();

        /// <summary>
        /// If non-null, SendAsync throws this exception (for download-failure scenarios).
        /// </summary>
        public Exception ThrowOnSend { get; set; }

        /// <summary>
        /// Number of times SendAsync was invoked.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// The most recent request URL passed to SendAsync.
        /// </summary>
        public Uri LastRequestUri { get; private set; }

        public void QueueResponse(Func<Uri, bool> matcher, HttpResponseMessage response)
        {
            _responses.Add((matcher, response));
        }

        public void QueueResponse(Func<Uri, bool> matcher, System.Net.HttpStatusCode status, string body, string contentType = "application/json")
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType)
            };
            _responses.Add((matcher, response));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;

            if (ThrowOnSend != null)
            {
                throw ThrowOnSend;
            }

            foreach (var (match, response) in _responses)
            {
                if (match(request.RequestUri))
                {
                    return Task.FromResult(response);
                }
            }

            // Default: 404 Not Found
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("no mock match for " + request.RequestUri)
            });
        }
    }
}
