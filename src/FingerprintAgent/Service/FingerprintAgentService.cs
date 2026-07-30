using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.ServiceProcess;
using System.Threading;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;
using ZkTecoFingerPrint;

namespace FingerprintAgent.Service
{
    public class FingerprintAgentService : ServiceBase
    {
        private HttpServer _httpServer;
        private IScannerAdapter _scanner;
        private AgentConfig _config;
        private CancellationTokenSource _cts;
        private AgentLogger _logger;
        private Timer _healthCheckTimer;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(30);
        private ConfigFileWatcher _configWatcher;
        private readonly object _configLock = new object();

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
            _scanner = new ScannerManager(_config, _logger);
            _httpServer = new HttpServer(_config, _scanner, _logger);
            _httpServer.Start();
            StartHealthCheckTimer();

            // Start config file watcher after service is fully running
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                _configWatcher = new ConfigFileWatcher(configPath, _logger);
                _configWatcher.ConfigReloaded += OnConfigReloaded;
                _logger.Info(startCid, "ConfigFileWatcher: started watching config.json");
            }
            catch (Exception ex)
            {
                _configWatcher?.Dispose();
                _configWatcher = null;
                var msg = $"Failed to start ConfigFileWatcher: {ex.Message}";
                _logger?.Error(startCid, msg);
                throw;
            }

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
                _cts?.Dispose();
            }
            catch (Exception ex)
            {
                shutdownError = ex;
                _logger?.Error(stopCid, $"Error cancelling token: {ex.Message}");
            }

            try
            {
                _healthCheckTimer?.Dispose();
            }
            catch { }

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

            try
            {
                _configWatcher?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Error(stopCid, $"Error disposing ConfigFileWatcher: {ex.Message}");
            }

            try
            {
                (_scanner as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                shutdownError = ex;
                _logger?.Error(stopCid, $"Error disposing scanner: {ex.Message}");
            }

            // ZkTecoFingerHost.Close() is safe to call once — static teardown for all ZKTeco sessions.
            // Called after adapter disposal since ZKTecoAdapter.Dispose() deliberately skips it
            // (multi-instance pattern: individual adapter must not close the shared host).
            try { ZkTecoFingerHost.Close(); } catch { /* best-effort */ }

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

        private void StartHealthCheckTimer()
        {
            _healthCheckTimer = new Timer(HealthCheckCallback, null, _healthCheckInterval, _healthCheckInterval);
        }

        private void HealthCheckCallback(object state)
        {
            try
            {
                bool connected = _scanner.IsConnected;
                if (!connected)
                {
                    var backoffStep = (_scanner as ScannerManager)?.BackoffStep ?? 0;
                    _logger?.Warn(null, $"HealthCheck: scanner not connected (backoff step={backoffStep})");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(null, $"HealthCheck: callback threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnConfigReloaded(AgentConfig newConfig)
        {
            var cid = AgentLogger.GenerateCorrelationId();

            // D-06: Only reload ScannerConfig + CorsConfig
            lock (_configLock)
            {
                _config = newConfig;
            }

            // Update CORS immediately
            if (_httpServer != null)
                _httpServer?.UpdateCorsConfig(newConfig.Cors);
            else
                _logger?.Warn(cid, "OnConfigReloaded: _httpServer is null, skipping CORS update");

            // D-09: active adapter stays the same, but new priority applies on next failure
            var scannerManager = _scanner as ScannerManager;
            scannerManager?.UpdatePriority(newConfig.Scanner.Priority);

            _logger?.Info(cid, $"ConfigFileWatcher: applied scanner priority=[{string.Join(", ", newConfig.Scanner.Priority)}], cors mode={newConfig.Cors.Mode}");
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
