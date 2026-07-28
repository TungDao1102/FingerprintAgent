using System;
using System.Diagnostics;
using System.Security;
using System.ServiceProcess;
using System.Threading;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;

namespace FingerprintAgent.Service
{
    public class FingerprintAgentService : ServiceBase
    {
        private HttpServer _httpServer;
        private IScannerAdapter _scanner;
        private AgentConfig _config;
        private CancellationTokenSource _cts;
        private AgentLogger _logger;

        public FingerprintAgentService()
        {
            ServiceName = "FingerprintAgent";
            AutoLog = true;
        }

        public FingerprintAgentService(AgentLogger logger) : this()
        {
            _logger = logger;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                _config = ConfigLoader.Load();
                _logger = _logger ?? new AgentLogger(_config.Logging);
            }
            catch (Exception ex)
            {
                var message = $"Failed to load configuration: {ex.Message}";
                TryWriteEventLog(message, EventLogEntryType.Error);
                throw;
            }

            _cts = new CancellationTokenSource();
            var startCid = AgentLogger.GenerateCorrelationId();
            _logger.Info(startCid, "Service starting");
            _scanner = new MockScannerAdapter();
            _httpServer = new HttpServer(_config, _scanner, _logger);
            _httpServer.Start();

            _logger.Info(startCid, "Service started");
            TryWriteEventLog("Service started successfully", EventLogEntryType.Information);
        }

        protected override void OnStop()
        {
            try
            {
                var stopCid = AgentLogger.GenerateCorrelationId();
                _logger?.Info(stopCid, "Service stopping");
                _cts?.Cancel();
                _httpServer?.Stop();
                _httpServer?.Dispose();
                _logger?.Info(stopCid, "Service stopped");
                TryWriteEventLog("Service stopped", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                TryWriteEventLog($"Error during stop: {ex.Message}", EventLogEntryType.Error);
            }
            finally
            {
                _logger?.Dispose();
                _logger = null;
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
