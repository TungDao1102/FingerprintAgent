using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using FingerprintAgent.Configuration;

namespace FingerprintAgent.Logging
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    public class AgentLogger : IDisposable
    {
        // Matches base64 substrings of 40+ chars anywhere in a string (not just full-string).
        // Allows detection of embedded image data like "data:image/png;base64,/9j/4AAQ...".
        private static readonly Regex Base64Pattern = new Regex(
            "(?:[A-Za-z0-9+/]{4}){10,}(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})?",
            RegexOptions.Compiled);

        private const string InstallLogPattern = "install-*.log";

        private readonly string _filePath;
        private readonly string _baseName;
        private readonly LogLevel _minLevel;
        private readonly long _maxSizeBytes;
        private readonly int _maxFiles;
        private readonly int _retentionDays;
        private StreamWriter _writer;
        private readonly object _lock = new object();
        private bool _disposed;

        public AgentLogger(LoggingConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            _filePath = config.File;
            _baseName = Path.GetFileName(_filePath);
            _minLevel = ParseLogLevel(config.Level);
            _maxSizeBytes = config.MaxSizeMb > 0 ? config.MaxSizeMb * 1024L * 1024L : 0;
            _maxFiles = Math.Max(2, config.MaxFiles);
            _retentionDays = config.RetentionDays;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            int removedFiles = CleanupOldFiles();

            var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            _writer = new StreamWriter(stream) { AutoFlush = true };

            if (removedFiles > 0)
            {
                WriteRetentionSummary(removedFiles);
            }
        }

        public static string GenerateCorrelationId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        public void Debug(string correlationId, string message)
        {
            Write(LogLevel.Debug, correlationId, message);
        }

        public void Info(string correlationId, string message)
        {
            Write(LogLevel.Info, correlationId, message);
        }

        public void Warn(string correlationId, string message)
        {
            Write(LogLevel.Warn, correlationId, message);
        }

        public void Error(string correlationId, string message)
        {
            Write(LogLevel.Error, correlationId, message);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _writer?.Flush();
                _writer?.Dispose();
            }
        }

        private void Write(LogLevel level, string correlationId, string message)
        {
            if (level < _minLevel)
            {
                return;
            }

            if (string.IsNullOrEmpty(correlationId))
            {
                correlationId = "-";
            }

            var safeMessage = RedactIfImageData(message);
            var timestamp = DateTime.UtcNow.ToString("O");
            var levelName = level.ToString().ToUpperInvariant();
            var entry = $"{timestamp} [{levelName}] [{correlationId}] {safeMessage}";

            lock (_lock)
            {
                if (_writer == null) return;

                RotateIfNeeded();
                if (_writer == null) return;

                _writer.WriteLine(entry);
                _writer.Flush();
            }

            if (level >= LogLevel.Warn)
            {
                TryWriteEventLog(entry, level);
            }
        }

        private void RotateIfNeeded()
        {
            if (_maxSizeBytes <= 0) return;

            var fs = _writer.BaseStream as FileStream;
            if (fs == null || fs.Length < _maxSizeBytes) return;

            try { _writer.Dispose(); } catch { }
            _writer = null;

            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                string name = Path.GetFileName(_filePath);

                for (int i = _maxFiles - 2; i >= 1; i--)
                {
                    ShiftFile(Path.Combine(dir, $"{name}.{i}"), Path.Combine(dir, $"{name}.{i + 1}"));
                }
                ShiftFile(_filePath, Path.Combine(dir, $"{name}.1"));
            }
            catch
            {
                // AV/IO lock during shift: keep appending to the same file rather than losing output.
            }

            try
            {
                var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream) { AutoFlush = true };
            }
            catch
            {
                // Reopen failed: _writer stays null; Write() drops entries until a later success.
            }

            int removedFiles = CleanupOldFiles();
            if (removedFiles > 0)
            {
                WriteRetentionSummary(removedFiles);
            }
        }

        private static void ShiftFile(string src, string dst)
        {
            if (!File.Exists(src)) return;
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }

        /// <summary>
        /// Retention sweep, run at startup and after each rotation.
        /// 1. Count rule: rotated files "{base}.{N}" with N >= MaxFiles are orphans
        ///    (left over after MaxFiles was lowered) and are deleted.
        /// 2. Age rule: rotated files and install-*.log older than RetentionDays are
        ///    deleted (RetentionDays &lt;= 0 disables the age rule).
        /// The current log file is never touched. Returns the number of files removed;
        /// the caller is responsible for writing the summary.
        /// </summary>
        private int CleanupOldFiles()
        {
            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return 0;
                }

                DateTime? cutoffUtc = _retentionDays <= 0
                    ? (DateTime?)null
                    : DateTime.UtcNow.AddDays(-_retentionDays);
                int removed = 0;

                foreach (string file in Directory.GetFiles(dir, _baseName + ".*"))
                {
                    string fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, _baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int index;
                    if (!TryGetRotationIndex(fileName, _baseName, out index))
                    {
                        continue;
                    }

                    bool isOrphan = index >= _maxFiles;
                    bool isExpired = cutoffUtc.HasValue && File.GetLastWriteTimeUtc(file) < cutoffUtc.Value;
                    if (!isOrphan && !isExpired)
                    {
                        continue;
                    }

                    if (TryDelete(file))
                    {
                        removed++;
                    }
                }

                if (cutoffUtc.HasValue)
                {
                    foreach (string file in Directory.GetFiles(dir, InstallLogPattern))
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoffUtc.Value && TryDelete(file))
                        {
                            removed++;
                        }
                    }
                }

                return removed;
            }
            catch (Exception ex)
            {
                // Best-effort sweep, but never silently: operators watching the Event Log
                // must be able to see that retention stopped working (disk-fill symptom).
                TryWriteEventLog($"Log retention sweep failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
                return 0;
            }
        }

        private void WriteRetentionSummary(int removedFiles)
        {
            string entry = $"{DateTime.UtcNow:O} [INFO] [-] Log retention: removed {removedFiles} old log file(s) from {Path.GetDirectoryName(_filePath)}";

            lock (_lock)
            {
                if (_writer == null)
                {
                    TryWriteEventLog(entry, LogLevel.Info);
                    return;
                }

                _writer.WriteLine(entry);
                _writer.Flush();
            }
        }

        private static bool TryGetRotationIndex(string fileName, string baseName, out int index)
        {
            index = 0;
            if (!fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string suffix = fileName.Substring(baseName.Length + 1);
            return int.TryParse(suffix, out index);
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private string RedactIfImageData(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            var trimmed = message.Trim();
            // Oversized entries skip the regex — nested-quantifier backtracking is O(n²) on hostile input.
            if (trimmed.Length > RedactionScanLimit)
            {
                return "[REDACTED: oversized log entry]";
            }

            if (trimmed.Length > 40 && Base64Pattern.IsMatch(trimmed))
            {
                return "[REDACTED: potential image data]";
            }

            return message;
        }

        private const int RedactionScanLimit = 8192;

        private static LogLevel ParseLogLevel(string level)
        {
            if (string.IsNullOrWhiteSpace(level))
            {
                return LogLevel.Info;
            }

            switch (level.Trim().ToUpperInvariant())
            {
                case "DEBUG": return LogLevel.Debug;
                case "INFO": return LogLevel.Info;
                case "WARN":
                case "WARNING": return LogLevel.Warn;
                case "ERROR": return LogLevel.Error;
                default: return LogLevel.Info;
            }
        }

        private static EventLogEntryType ToEventLogEntryType(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return EventLogEntryType.Error;
                case LogLevel.Warn: return EventLogEntryType.Warning;
                default: return EventLogEntryType.Information;
            }
        }

        private static void TryWriteEventLog(string entry, LogLevel level)
        {
            try
            {
                EventLog.WriteEntry("FingerprintAgent", entry, ToEventLogEntryType(level));
            }
            catch (SecurityException)
            {
            }
            catch (Exception)
            {
            }
        }
    }
}
