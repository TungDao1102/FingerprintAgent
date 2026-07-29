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
            var stopCid = AgentLogger.GenerateCorrelationId();
            Exception shutdownError = null;

            try
            {
                _logger?.Info(stopCid, "Service stopping");
            }
            catch (Exception ex)
            {
                shutdownError = ex;
                TryWriteEventLog($"Error logging service stop begin: {ex.Message}", EventLogEntryType.Warning);
            }

            try
            {
                _cts?.Cancel();
            }
            catch (Exception ex)
            {
                shutdownError = ex;
                _logger?.Error(stopCid, $"Error cancelling token: {ex.Message}");
            }

            try
            {
                _httpServer?.Stop();
            }
            catch (Exception ex)
            {
                shutdownError = ex;
                _logger?.Error(stopCid, $"Error stopping HTTP server: {ex.Message}");
            }

            try
            {
                _httpServer?.Dispose();
            }
            catch (Exception ex)
            {
                shutdownError = ex;
                _logger?.Error(stopCid, $"Error disposing HTTP server: {ex.Message}");
            }

            if (shutdownError != null)
            {
                TryWriteEventLog($"Service stopped with error: {shutdownError.Message}", EventLogEntryType.Error);
            }
            else
            {
                _logger?.Info(stopCid, "Service stopped");
                TryWriteEventLog("Service stopped", EventLogEntryType.Information);
            }

            try
            {
                _logger?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FingerprintAgent] Error disposing logger: {ex.Message}");
            }
            finally
            {
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
