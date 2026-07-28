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
        private static readonly Regex Base64Pattern = new Regex(
            "^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})$",
            RegexOptions.Compiled);

        private readonly string _filePath;
        private readonly LogLevel _minLevel;
        private readonly StreamWriter _writer;
        private readonly object _lock = new object();
        private bool _disposed;

        public AgentLogger(LoggingConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            _filePath = config.File;
            _minLevel = ParseLogLevel(config.Level);

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
                _writer.WriteLine(entry);
                _writer.Flush();
            }

            TryWriteEventLog(entry, level);
        }

        private string RedactIfImageData(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            var trimmed = message.Trim();
            if (trimmed.Length > 40 && Base64Pattern.IsMatch(trimmed))
            {
                return "[REDACTED: potential image data]";
            }

            return message;
        }

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
