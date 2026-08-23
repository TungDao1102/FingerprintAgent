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

        private readonly string _filePath;
        private readonly LogLevel _minLevel;
        private readonly long _maxSizeBytes;
        private readonly int _maxFiles;
        private StreamWriter _writer;
        private readonly object _lock = new object();
        private bool _disposed;

        public AgentLogger(LoggingConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            _filePath = config.File;
            _minLevel = ParseLogLevel(config.Level);
            _maxSizeBytes = config.MaxSizeMb > 0 ? config.MaxSizeMb * 1024L * 1024L : 0;
            _maxFiles = Math.Max(2, config.MaxFiles);

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            _writer = new StreamWriter(stream) { AutoFlush = true };
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
        }

        private static void ShiftFile(string src, string dst)
        {
            if (!File.Exists(src)) return;
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
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
