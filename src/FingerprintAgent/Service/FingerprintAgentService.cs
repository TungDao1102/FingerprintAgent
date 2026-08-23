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
using FingerprintAgent.Update;

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
        private UpdateCheckService _updateCheckService;
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
                var configPath = ConfigLoader.ProgramDataConfigPath;
                _configWatcher = new ConfigFileWatcher(configPath, _logger);
                _configWatcher.ConfigReloaded += OnConfigReloaded;
                _logger.Info(startCid, $"ConfigFileWatcher: started watching {configPath}");
            }
            catch (Exception ex)
            {
                _configWatcher?.Dispose();
                _configWatcher = null;
                var msg = $"Failed to start ConfigFileWatcher: {ex.Message}";
                _logger?.Error(startCid, msg);
                throw;
            }

            // D-14: default DISABLED. Failures here don't crash the service — capture still works.
            try
            {
                _updateCheckService = new UpdateCheckService(_config, _logger);
                _updateCheckService.Start();
                if (_updateCheckService.State == UpdateState.Running)
                {
                    _logger.Info(startCid, $"UpdateCheckService: started (enabled={_config.Update.Enabled})");
                }
                else
                {
                    _logger.Info(startCid, $"UpdateCheckService: not started — Start() returned no-op (enabled={_config.Update.Enabled})");
                }
            }
            catch (Exception ex)
            {
                _updateCheckService = null;
                _logger?.Error(startCid, $"UpdateCheckService: failed to start: {ex.Message}");
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
                _updateCheckService?.Stop();
                _updateCheckService?.Dispose();
                _updateCheckService = null;
            }
            catch (Exception ex)
            {
                _logger?.Error(stopCid, $"UpdateCheckService: dispose error: {ex.Message}");
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

            try
            {
                // Dispose timer AFTER scanner to prevent a queued health check callback
                // from accessing a disposed scanner (WR-01)
                _healthCheckTimer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug(null, $"Health check timer dispose error: {ex.Message}");
            }

            // ZkNativeHost.Close() is safe to call once — static teardown for all ZKTeco sessions.
            // Called after adapter disposal since ZKTecoAdapter.Dispose() deliberately skips it
            // (multi-instance pattern: individual adapter must not close the shared host).
            // M2: if a capture somehow survived the 30s drain, Terminate() here would tear the
            // native context out from under it (AV risk) — skip and let process exit reap it.
            try
            {
                if (ZKTecoAdapter.CaptureInFlight)
                {
                    _logger?.Warn(stopCid, "Shutdown: capture still in flight after drain — skipping native host teardown");
                }
                else
                {
                    ZkNativeHost.Close();
                }
            }
            catch { /* best-effort */ }

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
                else
                {
                    _logger?.Debug(null, "HealthCheck: scanner connected");
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

            // D-06: Reload ScannerConfig + CorsConfig + UpdateConfig
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

            // D-14/D-15: ApplyConfig starts/stops the Timer based on update.enabled toggle.
            _updateCheckService?.ApplyConfig(newConfig);

            _logger?.Info(cid, $"OnConfigReloaded: applied scanner priority=[{string.Join(", ", newConfig.Scanner.Priority)}], cors mode={newConfig.Cors.Mode}, update enabled={newConfig.Update.Enabled}");
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
