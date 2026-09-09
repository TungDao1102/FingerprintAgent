extern alias WixCA;

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using CustomActions = WixCA::FingerprintAgent.Installer.CustomActions;

namespace FingerprintAgent.Tests.Installer
{
    /// <summary>
    /// Tests for the ArchiveInstallLog helpers — copying the active MSI log
    /// (MsiLogFileLocation, guaranteed by the MsiLogging property) into
    /// C:\ProgramData\FingerprintAgent\Logs\ so install diagnostics sit next to agent.log.
    /// </summary>
    public class ArchiveInstallLogTests
    {
        [Fact]
        public void BuildInstallLogFileName_FixedTimestamp_MatchesExpectedName()
        {
            var timestamp = new DateTime(2026, 9, 9, 15, 1, 2);
            Assert.Equal("install-20260909-150102.log", CustomActions.BuildInstallLogFileName(timestamp));
        }

        [Fact]
        public void CopyInstallLogFile_NullSource_SkipsWithoutCreatingTarget()
        {
            var logs = new List<string>();
            string dir = Path.Combine(Path.GetTempPath(), "fa-test-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(dir, "install.log");

            CustomActions.CopyInstallLogFile(null, target, logs.Add);

            Assert.Contains(logs, line => line.Contains("skipped"));
            Assert.False(File.Exists(target));
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void CopyInstallLogFile_MissingSourceFile_SkipsWithoutCreatingTarget()
        {
            var logs = new List<string>();
            string dir = Path.Combine(Path.GetTempPath(), "fa-test-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(dir, "does-not-exist.log");
            string target = Path.Combine(dir, "install.log");

            CustomActions.CopyInstallLogFile(source, target, logs.Add);

            Assert.Contains(logs, line => line.Contains("skipped"));
            Assert.False(File.Exists(target));
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void CopyInstallLogFile_ValidSource_CopiesContentAndCreatesTargetDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fa-test-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(dir, "source.log");
            Directory.CreateDirectory(dir);
            File.WriteAllText(source, "MSI log content 123");

            string target = Path.Combine(dir, "Logs", "install-20260909-150102.log");
            var logs = new List<string>();
            CustomActions.CopyInstallLogFile(source, target, logs.Add);

            Assert.True(File.Exists(target));
            Assert.Equal("MSI log content 123", File.ReadAllText(target));
            Assert.Contains(logs, line => line.Contains("Install log archived to"));
        }

        [Fact]
        public void CopyInstallLogFile_ExistingTarget_OverwritesWithSourceContent()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fa-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "source.log");
            File.WriteAllText(source, "newer MSI run");
            string target = Path.Combine(dir, "install.log");
            File.WriteAllText(target, "older MSI run");

            var logs = new List<string>();
            CustomActions.CopyInstallLogFile(source, target, logs.Add);

            Assert.Equal("newer MSI run", File.ReadAllText(target));
            Assert.Contains(logs, line => line.Contains("Install log archived to"));
        }

        [Fact]
        public void CopyInstallLogFile_IoFailure_LogsFailureWithoutThrowing()
        {
            // Target path occupied by a DIRECTORY forces File.Copy to throw IOException.
            string dir = Path.Combine(Path.GetTempPath(), "fa-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "source.log");
            File.WriteAllText(source, "content");
            string target = Path.Combine(dir, "blocked");
            Directory.CreateDirectory(target);

            var logs = new List<string>();
            Exception thrown = Record.Exception(() => CustomActions.CopyInstallLogFile(source, target, logs.Add));

            Assert.Null(thrown);
            Assert.Contains(logs, line => line.Contains("archive failed"));
        }
    }
}
