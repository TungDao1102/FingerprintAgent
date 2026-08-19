extern alias WixCA;

using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using CustomActions = WixCA::FingerprintAgent.Installer.CustomActions;

namespace FingerprintAgent.Tests.Installer
{
    /// <summary>
    /// Tests for CustomActions.SeedProgramDataConfigCore — the pure-logic helper that
    /// copies the install-dir template into ProgramData on first install, or performs
    /// the ConfigMerger smart-merge on upgrade. Exposed as internal so tests can call
    /// it without spinning up an MSI session.
    /// </summary>
    public class SeedProgramDataConfigTests : IDisposable
    {
        private readonly string _baseTemp;
        private readonly string _installDir;
        private readonly string _programDataDir;
        private readonly string _templatePath;
        private readonly string _programDataConfigPath;
        private bool _disposed;

        public SeedProgramDataConfigTests()
        {
            _baseTemp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _installDir = Path.Combine(_baseTemp, "install");
            _programDataDir = Path.Combine(_baseTemp, "ProgramData");
            Directory.CreateDirectory(_installDir);
            _programDataConfigPath = Path.Combine(_programDataDir, "config.json");
            _templatePath = Path.Combine(_installDir, "config.template.json");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (Directory.Exists(_baseTemp))
                    Directory.Delete(_baseTemp, recursive: true);
            }
            catch { }
        }

        private void WriteTemplate(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_templatePath));
            File.WriteAllText(_templatePath, json);
        }
        private void WriteProgramDataConfig(string json)
        {
            Directory.CreateDirectory(_programDataDir);
            File.WriteAllText(_programDataConfigPath, json);
        }

        [Fact]
        public void FirstInstall_NoProgramDataConfig_CopiesTemplate()
        {
            string template = @"{ ""http"": { ""port"": 5043 } }";
            WriteTemplate(template);

            string outcome = CustomActions.SeedProgramDataConfigCore(_templatePath, _programDataConfigPath);

            Assert.Contains("Seeded ProgramData config from template", outcome);
            Assert.True(File.Exists(_programDataConfigPath));
            Assert.Equal(template.Replace("\r\n", "\n"), File.ReadAllText(_programDataConfigPath).Replace("\r\n", "\n"));
        }

        [Fact]
        public void FirstInstall_CreatesProgramDataDirectoryIfMissing()
        {
            Assert.False(Directory.Exists(_programDataDir));
            WriteTemplate(@"{ ""http"": { ""port"": 5043 } }");

            CustomActions.SeedProgramDataConfigCore(_templatePath, _programDataConfigPath);

            Assert.True(Directory.Exists(_programDataDir));
            Assert.True(File.Exists(_programDataConfigPath));
        }

        [Fact]
        public void Upgrade_BothFilesExist_PerformsMergeAndPreservesUserValue()
        {
            string template = @"{ ""http"": { ""port"": 5043 }, ""update"": { ""enabled"": false } }";
            string user = @"{ ""http"": { ""port"": 8080 } }";
            WriteTemplate(template);
            WriteProgramDataConfig(user);

            string outcome = CustomActions.SeedProgramDataConfigCore(_templatePath, _programDataConfigPath);

            Assert.Contains("Merged template into ProgramData config", outcome);

            var merged = JObject.Parse(File.ReadAllText(_programDataConfigPath));
            Assert.Equal(8080, (int)merged["http"]["port"]); // user value preserved
            Assert.NotNull(merged["update"]);                  // new section added
            Assert.False((bool)merged["update"]["enabled"]);
        }

        [Fact]
        public void Upgrade_AddedKeys_WritesMergeLog()
        {
            string template = @"{ ""http"": { ""port"": 5043 }, ""update"": { ""enabled"": false, ""githubRepo"": ""X"" } }";
            string user = @"{ ""http"": { ""port"": 5043 } }";
            WriteTemplate(template);
            WriteProgramDataConfig(user);

            CustomActions.SeedProgramDataConfigCore(_templatePath, _programDataConfigPath);

            string mergeLogPath = Path.Combine(_programDataDir, "merge.log");
            Assert.True(File.Exists(mergeLogPath), "merge.log should be written when keys are added");
            string logContent = File.ReadAllText(mergeLogPath);
            Assert.Contains("update", logContent);
            Assert.Contains("Added keys", logContent);
        }

        [Fact]
        public void Upgrade_NoAddedKeys_NoMergeLogWritten()
        {
            // Both configs identical (just port differs slightly — user wins, no add)
            string template = @"{ ""http"": { ""port"": 5043 } }";
            string user = @"{ ""http"": { ""port"": 9999 } }";
            WriteTemplate(template);
            WriteProgramDataConfig(user);

            string outcome = CustomActions.SeedProgramDataConfigCore(_templatePath, _programDataConfigPath);

            Assert.Contains("no new keys", outcome);
            string mergeLogPath = Path.Combine(_programDataDir, "merge.log");
            Assert.False(File.Exists(mergeLogPath));
        }

        [Fact]
        public void TemplateMissing_ThrowsFileNotFoundException()
        {
            // No template written
            string bogusPath = Path.Combine(_installDir, "nonexistent.json");
            string bogusTarget = Path.Combine(_programDataDir, "config.json");

            Assert.Throws<FileNotFoundException>(() =>
                CustomActions.SeedProgramDataConfigCore(bogusPath, bogusTarget));
        }
    }
}
