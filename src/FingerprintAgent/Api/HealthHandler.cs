using System;
using System.Net;
using System.Text;
using FingerprintAgent.Adapters;
using Newtonsoft.Json;

namespace FingerprintAgent.Api
{
    public class HealthHandler
    {
        private readonly DateTime _startTime;

        public HealthHandler()
        {
            _startTime = DateTime.UtcNow;
        }

        public void Handle(HttpListenerContext context, IScannerAdapter scanner)
        {
            var response = new
            {
                status = "healthy",
                deviceId = scanner.DeviceId,
                uptime = (DateTime.UtcNow - _startTime).ToString(@"hh\:mm\:ss")
            };

            string json = JsonConvert.SerializeObject(response);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }
    }
}
