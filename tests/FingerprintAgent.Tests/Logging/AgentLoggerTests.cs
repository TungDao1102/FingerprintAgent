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

        [Fact]
        public void Cleanup_OrphanFilesBeyondMaxFiles_Deleted()
        {
            SeedFile("agent.log.3");
            SeedFile("agent.log.5");
            SeedFile("agent.log.6");

            using (CreateLoggerWith(maxFiles: 5))
            {
            }

            Assert.False(File.Exists(Path.Combine(_logDir, "agent.log.5")));
            Assert.False(File.Exists(Path.Combine(_logDir, "agent.log.6")));
            Assert.True(File.Exists(Path.Combine(_logDir, "agent.log.3")));
        }

        [Fact]
        public void Cleanup_OldRotatedFileBeyondRetention_Deleted()
        {
            SeedFile("agent.log.1", ageDays: 100);
            SeedFile("agent.log.2", ageDays: 10);

            using (CreateLoggerWith(retentionDays: 90))
            {
            }

            Assert.False(File.Exists(Path.Combine(_logDir, "agent.log.1")));
            Assert.True(File.Exists(Path.Combine(_logDir, "agent.log.2")));
        }

        [Fact]
        public void Cleanup_RetentionDaysZero_AgeSweepDisabled()
        {
            SeedFile("agent.log.1", ageDays: 100);
            SeedFile("install-20200101-000000.log", ageDays: 100);

            using (CreateLoggerWith(retentionDays: 0))
            {
            }

            Assert.True(File.Exists(Path.Combine(_logDir, "agent.log.1")));
            Assert.True(File.Exists(Path.Combine(_logDir, "install-20200101-000000.log")));
        }

        [Fact]
        public void Cleanup_NeverDeletesCurrentLogFile()
        {
            var path = SeedFile("agent.log", "seed content", ageDays: 100);

            using (var logger = CreateLoggerWith(retentionDays: 90))
            {
                logger.Info("abc123", "after cleanup");
            }

            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.Equal("seed content", lines[0]);
        }

        [Fact]
        public void Cleanup_OldInstallLogsBeyondRetention_Deleted()
        {
            SeedFile("install-20200101-000000.log", ageDays: 100);
            SeedFile("install-20260901-000000.log", ageDays: 5);

            using (CreateLoggerWith(retentionDays: 90))
            {
            }

            Assert.False(File.Exists(Path.Combine(_logDir, "install-20200101-000000.log")));
            Assert.True(File.Exists(Path.Combine(_logDir, "install-20260901-000000.log")));
        }

        [Fact]
        public void Rotate_WithOrphanFilesPresent_CleansOrphans()
        {
            SeedFile("agent.log.5");
            SeedFile("agent.log.6");

            using (var logger = CreateLoggerWith(maxSizeMb: 1, maxFiles: 5, retentionDays: 0))
            {
                for (int i = 0; i < 2000; i++)
                {
                    logger.Info($"cid{i:D4}", $"rotation test {i} " + new string('-', 700));
                }
            }

            Assert.True(File.Exists(Path.Combine(_logDir, "agent.log.1")), "rotation should have produced agent.log.1");
            Assert.False(File.Exists(Path.Combine(_logDir, "agent.log.5")));
            Assert.False(File.Exists(Path.Combine(_logDir, "agent.log.6")));
        }

        [Fact]
        public void Cleanup_SummaryWrittenToLogFile_WhenFilesRemoved()
        {
            SeedFile("agent.log.9");

            using (CreateLoggerWith(maxFiles: 5))
            {
            }

            var lines = File.ReadAllLines(_logFile);
            Assert.Contains(lines, line => line.Contains("[INFO] [-] Log retention: removed 1 old log file(s)"));
        }

        private string SeedFile(string fileName, string content = "seed", int ageDays = 0)
        {
            var path = Path.Combine(_logDir, fileName);
            File.WriteAllText(path, content + Environment.NewLine);
            if (ageDays > 0)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
            }
            return path;
        }

        private AgentLogger CreateLoggerWith(int maxSizeMb = 10, int maxFiles = 5, int retentionDays = 90)
        {
            return new AgentLogger(new LoggingConfig
            {
                Level = "INFO",
                File = _logFile,
                MaxSizeMb = maxSizeMb,
                MaxFiles = maxFiles,
                RetentionDays = retentionDays
            });
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
