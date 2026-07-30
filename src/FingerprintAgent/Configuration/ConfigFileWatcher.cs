using System;
using System.IO;
using System.Timers;
using FingerprintAgent.Logging;

namespace FingerprintAgent.Configuration
{
    /// <summary>
    /// Watches config.json for changes using FileSystemWatcher with a 300ms debounce
    /// timer to coalesce double-save patterns from VS/Notepad++.
    ///
    /// Fires ConfigReloaded only when Scanner and Cors sections are both present.
    /// D-08: bad parse / missing sections keep old config — logs error, does NOT throw.
    /// </summary>
    public class ConfigFileWatcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounceTimer;
        private readonly AgentLogger _logger;
        private readonly string _configPath;
        private bool _disposed;

        public event Action<AgentConfig> ConfigReloaded;

        public ConfigFileWatcher(string configPath, AgentLogger logger)
        {
            _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
            _logger = logger;

            var directory = Path.GetDirectoryName(configPath);
            var fileName = Path.GetFileName(configPath);

            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException($"configPath must be a full path, got: {configPath}", nameof(configPath));

            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnRawChanged;

            _debounceTimer = new Timer(300);
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += OnDebounceElapsed;
        }

        private void OnRawChanged(object sender, FileSystemEventArgs e)
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                _logger?.Info(null, "ConfigFileWatcher: config changed, reloading");

                // Parse via ConfigLoader
                var directory = Path.GetDirectoryName(_configPath);
                var newConfig = ConfigLoader.LoadFromDirectory(directory);

                // Validate D-06: only ScannerConfig and CorsConfig are reloadable at runtime
                if (newConfig.Scanner == null || newConfig.Cors == null)
                {
                    _logger?.Error(null, "ConfigFileWatcher: reload missing ScannerConfig or CorsConfig — keeping old config");
                    return;
                }

                ConfigReloaded?.Invoke(newConfig);
                _logger?.Info(null, "ConfigFileWatcher: config reload complete");
            }
            catch (Exception ex)
            {
                // D-08: keep old config, log error, don't crash
                _logger?.Error(null, $"ConfigFileWatcher: reload failed, keeping old config — {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Order matters: stop/dispose timer first, then watcher
            try { _debounceTimer?.Stop(); } catch { }
            try { _debounceTimer?.Dispose(); } catch { }

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnRawChanged;
                try { _watcher.Dispose(); } catch { }
            }
        }
    }
}