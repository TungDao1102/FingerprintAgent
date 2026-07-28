using System;
using System.Diagnostics;
using System.Security;
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
                TryWriteEventLog(message, EventLogEntryType.Error);
                throw;
            }

            _cts = new CancellationTokenSource();
            _scanner = new MockScannerAdapter();
            _httpServer = new HttpServer(_config, _scanner);
            _httpServer.Start();

            TryWriteEventLog("Service started successfully", EventLogEntryType.Information);
        }

        protected override void OnStop()
        {
            try
            {
                TryWriteEventLog("Service stopping", EventLogEntryType.Information);
                _cts?.Cancel();
                _httpServer?.Stop();
                _httpServer?.Dispose();
                TryWriteEventLog("Service stopped", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                TryWriteEventLog($"Error during stop: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private static void TryWriteEventLog(string message, EventLogEntryType type)
        {
            try
            {
                EventLog.WriteEntry("FingerprintAgent", message, type);
            }
            catch (SecurityException securityEx)
            {
                Debug.WriteLine($"[FingerprintAgent] {type}: {message} (event log access denied: {securityEx.Message})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FingerprintAgent] Failed to write event log: {ex.Message}");
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
