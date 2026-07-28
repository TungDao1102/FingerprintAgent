using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Configuration;

namespace FingerprintAgent.Service
{
    public class FingerprintAgentService : ServiceBase
    {
        private HttpServer _httpServer;
        private IScannerAdapter _scanner;
        private AgentConfig _config;
        private CancellationTokenSource _cts;

        public FingerprintAgentService()
        {
            ServiceName = "FingerprintAgent";
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                _config = ConfigLoader.Load();
            }
            catch (Exception ex)
            {
                var message = $"Failed to load configuration: {ex.Message}";
                EventLog.WriteEntry("FingerprintAgent", message, EventLogEntryType.Error);
                throw;
            }

            _cts = new CancellationTokenSource();
            _scanner = new MockScannerAdapter();
            var cors = new CorsMiddleware(_config.Cors.Mode, _config.Cors.AllowedOrigins);
            _httpServer = new HttpServer(_config, _scanner);
            _httpServer.Start();

            EventLog.WriteEntry("FingerprintAgent", "Service started successfully", EventLogEntryType.Information);
        }

        protected override void OnStop()
        {
            try
            {
                EventLog.WriteEntry("FingerprintAgent", "Service stopping", EventLogEntryType.Information);
                _cts?.Cancel();
                _httpServer?.Stop();
                _httpServer?.Dispose();
                EventLog.WriteEntry("FingerprintAgent", "Service stopped", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                EventLog.WriteEntry("FingerprintAgent", $"Error during stop: {ex.Message}", EventLogEntryType.Error);
            }
        }

        public void StartConsole()
        {
            OnStart(null);
        }

        public void StopConsole()
        {
            OnStop();
        }
    }
}
