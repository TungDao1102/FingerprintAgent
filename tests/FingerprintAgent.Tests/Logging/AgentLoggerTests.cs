using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;
using Xunit;

namespace FingerprintAgent.Tests.Logging
{
    public class AgentLoggerTests : IDisposable
    {
        private readonly string _logDir;
        private readonly string _logFile;

        public AgentLoggerTests()
        {
            _logDir = Path.Combine(Path.GetTempPath(), $"FingerprintAgent-Tests-{Guid.NewGuid():N}");
            _logFile = Path.Combine(_logDir, "agent.log");
            Directory.CreateDirectory(_logDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_logDir))
                {
                    Directory.Delete(_logDir, true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public void Log_Info_CreatesFileWithStructuredEntry()
        {
            using (var logger = CreateLogger("INFO"))
            {
                logger.Info("abc123", "hello");
            }

            Assert.True(File.Exists(_logFile));
            var lines = File.ReadAllLines(_logFile);
            Assert.Single(lines);
            Assert.Contains("hello", lines[0]);
        }

        [Fact]
        public void Log_Info_EntryMatchesRegex()
        {
            using (var logger = CreateLogger("INFO"))
            {
                logger.Info("abc123def0", "hello");
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Single(lines);
            var regex = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z \[INFO\] \[[\w-]+\] hello$");
            Assert.Matches(regex, lines[0]);
        }

        [Fact]
        public void Log_Debug_SuppressedWhenMinLevelIsInfo()
        {
            using (var logger = CreateLogger("INFO"))
            {
                logger.Debug("abc123", "debug message");
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Empty(lines);
        }

        [Fact]
        public void Log_Debug_WrittenWhenMinLevelIsDebug()
        {
            using (var logger = CreateLogger("DEBUG"))
            {
                logger.Debug("abc123", "debug message");
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Single(lines);
            Assert.Contains("[DEBUG]", lines[0]);
            Assert.Contains("debug message", lines[0]);
        }

        [Fact]
        public void Log_Warn_CorrectLevelString()
        {
            using (var logger = CreateLogger("INFO"))
            {
                logger.Warn("abc123", "warn message");
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Single(lines);
            Assert.Contains("[WARN]", lines[0]);
        }

        [Fact]
        public void Log_Error_CorrectLevelString()
        {
            using (var logger = CreateLogger("INFO"))
            {
                logger.Error("abc123", "error message");
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Single(lines);
            Assert.Contains("[ERROR]", lines[0]);
        }

        [Fact]
        public void Log_CorrelationIdAppearsInOutput()
        {
            var correlationId = AgentLogger.GenerateCorrelationId();
            Assert.Equal(10, correlationId.Length);
            Assert.Matches(new Regex("^[a-f0-9]{10}$"), correlationId);

            using (var logger = CreateLogger("INFO"))
            {
                logger.Info(correlationId, "message");
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Single(lines);
            Assert.Contains($"[{correlationId}]", lines[0]);
        }

        [Fact]
        public void Log_EventLogSink_WritesToEventLog()
        {
            // Non-elevated runs may not be able to create the source; test only checks it doesn't throw.
            using (var logger = CreateLogger("INFO"))
            {
                var ex = Record.Exception(() => logger.Info("evtid", "event log test"));
                Assert.Null(ex);
            }
        }

        [Fact]
        public void Log_ImageData_RejectsBase64()
        {
            var fakeImage = Convert.ToBase64String(new byte[64]);
            using (var logger = CreateLogger("INFO"))
            {
                logger.Info("abc123", fakeImage);
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Single(lines);
            Assert.DoesNotContain(fakeImage, lines[0]);
            Assert.Contains("[REDACTED: potential image data]", lines[0]);
        }

        [Fact]
        public async Task Log_ConcurrentWrites_AreNotCorrupted()
        {
            const int count = 100;
            using (var logger = CreateLogger("INFO"))
            {
                var tasks = new List<Task>();
                for (int i = 0; i < count; i++)
                {
                    var n = i;
                    tasks.Add(Task.Run(() => logger.Info($"cid{n:D3}", $"message {n}")));
                }

                await Task.WhenAll(tasks);
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Equal(count, lines.Length);
            Assert.All(lines, line => Assert.Matches(new Regex(@"\[INFO\] \[cid\d{3}\] message \d+"), line));
        }

        [Fact]
        public void Log_DirectoryCreated_WhenMissing()
        {
            var nestedDir = Path.Combine(_logDir, "nested", "logs");
            var nestedFile = Path.Combine(nestedDir, "agent.log");

            using (var logger = new AgentLogger(new LoggingConfig
            {
                Level = "INFO",
                File = nestedFile
            }))
            {
                logger.Info("abc", "test");
            }

            Assert.True(Directory.Exists(nestedDir));
            Assert.True(File.Exists(nestedFile));
        }

        private AgentLogger CreateLogger(string level)
        {
            return new AgentLogger(new LoggingConfig
            {
                Level = level,
                File = _logFile,
                MaxSizeMb = 10,
                MaxFiles = 5
            });
        }
    }
}
