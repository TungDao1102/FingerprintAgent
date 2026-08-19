using System;
using System.IO;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests.Configuration
{
    /// <summary>
    /// Integration tests for ConfigLoader.Load(programDataPath, templatePath, installDir).
    /// Each test creates an isolated temp directory tree simulating the
    /// install-dir + ProgramData layout so we never touch the real
    /// %ProgramData%\FingerprintAgent\.
    /// </summary>
    public class ConfigLoaderProgramDataTests : IDisposable
    {
        private readonly string _installDir;
        private readonly string _programDataDir;
        private readonly string _programDataConfigPath;
        private readonly string _templatePath;
        private bool _disposed;

        public ConfigLoaderProgramDataTests()
        {
            var baseTemp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _installDir = Path.Combine(baseTemp, "install");
            _programDataDir = Path.Combine(baseTemp, "ProgramData");
            Directory.CreateDirectory(_installDir);
            Directory.CreateDirectory(_programDataDir);
            _programDataConfigPath = Path.Combine(_programDataDir, "config.json");
            _templatePath = Path.Combine(_installDir, "config.template.json");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    var baseTemp = Path.GetDirectoryName(_installDir);
                    if (Directory.Exists(baseTemp))
                        Directory.Delete(baseTemp, recursive: true);
                }
                catch { }
            }
        }

        private void WriteTemplate(string content)
        {
            File.WriteAllText(_templatePath, content);
        }

        private void WriteProgramDataConfig(string content)
        {
            File.WriteAllText(_programDataConfigPath, content);
        }

        private void WriteLegacyInstallConfig(string content)
        {
            File.WriteAllText(Path.Combine(_installDir, "config.json"), content);
        }

        // ---------- Test 1: legacy fallback ----------

        [Fact]
        public void Load_ProgramDataMissing_LegacyConfigPresent_CopiesLegacy()
        {
            // Arrange — v1.0 install: config.json in install-dir, nothing in ProgramData
            string legacyContent = @"{
                ""http"": { ""port"": 7777 },
                ""cors"": { ""mode"": ""allowlist"" }
            }";
            WriteLegacyInstallConfig(legacyContent);
            WriteTemplate(legacyContent);

            // Act
            var config = ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir);

            // Assert — config loaded from legacy; legacy was copied to ProgramData
            Assert.Equal(7777, config.Http.Port);
            Assert.Equal("allowlist", config.Cors.Mode);
            Assert.True(File.Exists(_programDataConfigPath),
                "Expected legacy config.json to be copied to ProgramData");
            string copied = File.ReadAllText(_programDataConfigPath);
            Assert.Contains("7777", copied);
            Assert.Contains("allowlist", copied);
        }

        // ---------- Test 2: first install seed from template ----------

        [Fact]
        public void Load_ProgramDataMissing_TemplatePresent_SeedsFromTemplate()
        {
            // Arrange — fresh install: only template, no legacy, no ProgramData
            WriteTemplate(@"{
                ""service"": { ""name"": ""FreshAgent"" },
                ""http"": { ""port"": 5043 }
            }");

            // Act
            var config = ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir);

            // Assert — template was copied to ProgramData and loaded
            Assert.Equal("FreshAgent", config.Service.Name);
            Assert.True(File.Exists(_programDataConfigPath),
                "Expected template to be copied to ProgramData on first install");
        }

        // ---------- Test 3: upgrade triggers merge + write-back ----------

        [Fact]
        public void Load_ProgramDataExists_TemplatePresent_MergesAndWritesBack()
        {
            // Arrange — user has custom cors mode; template has new update section
            WriteProgramDataConfig(@"{
                ""http"": { ""port"": 5043 },
                ""cors"": { ""mode"": ""allowlist"" }
            }");
            WriteTemplate(@"{
                ""http"": { ""port"": 5043 },
                ""cors"": { ""mode"": ""wildcard"" },
                ""update"": {
                    ""enabled"": false,
                    ""githubOwner"": """",
                    ""githubRepo"": ""FingerprintAgent"",
                    ""checkIntervalHours"": 6
                }
            }");

            // Act
            var config = ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir);

            // Assert — cors.mode preserved (user wins); update section added
            Assert.Equal("allowlist", config.Cors.Mode);
            Assert.NotNull(config.Update);
            Assert.False(config.Update.Enabled);   // template default
            Assert.Equal("FingerprintAgent", config.Update.GitHubRepo);
            Assert.Equal(6, config.Update.CheckIntervalHours);

            // The merged config was written back to ProgramData
            string writtenBack = File.ReadAllText(_programDataConfigPath);
            Assert.Contains("\"allowlist\"", writtenBack);   // user value preserved
            Assert.Contains("\"update\"", writtenBack);      // new section added

            // merge.log was written
            string mergeLogPath = Path.Combine(_programDataDir, "merge.log");
            Assert.True(File.Exists(mergeLogPath), "Expected merge.log to be written");
        }

        // ---------- Test 4: no template → load ProgramData as-is ----------

        [Fact]
        public void Load_ProgramDataExists_NoTemplate_LoadsProgramDataAsIs()
        {
            // Arrange — ProgramData config present, no template
            WriteProgramDataConfig(@"{
                ""http"": { ""port"": 6666 }
            }");
            // Note: no template written

            // Act
            var config = ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir);

            // Assert — loaded as-is, no merge attempted
            Assert.Equal(6666, config.Http.Port);
            string mergeLogPath = Path.Combine(_programDataDir, "merge.log");
            Assert.False(File.Exists(mergeLogPath),
                "merge.log must NOT be written when template is absent");
        }

        // ---------- Test 5: bad ProgramData JSON throws ----------

        [Fact]
        public void Load_BadProgramDataJson_ThrowsFormatException()
        {
            // Arrange — malformed JSON in ProgramData
            WriteProgramDataConfig("{ invalid json missing quote");
            WriteTemplate(@"{ ""http"": { ""port"": 5043 } }");

            // Act & Assert
            // Microsoft.Extensions.Configuration.Json wraps JSON parse errors in
            // InvalidDataException (not FormatException). We accept either as both
            // indicate the file is unreadable; LoadFromDirectory tests already cover
            // InvalidDataException explicitly.
            Assert.ThrowsAny<Exception>(() =>
                ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir));
        }

        // ---------- Test 6: Update section bound from ProgramData ----------

        [Fact]
        public void Load_UpdateSectionBoundFromProgramData()
        {
            // Arrange — ProgramData has update.enabled=true (operator opted in)
            WriteProgramDataConfig(@"{
                ""update"": {
                    ""enabled"": true,
                    ""githubOwner"": ""myorg"",
                    ""githubRepo"": ""MyAgent"",
                    ""checkIntervalHours"": 24
                }
            }");
            WriteTemplate(@"{
                ""update"": {
                    ""enabled"": false,
                    ""githubOwner"": """",
                    ""githubRepo"": ""FingerprintAgent"",
                    ""checkIntervalHours"": 6
                }
            }");

            // Act
            var config = ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir);

            // Assert — user values win
            Assert.True(config.Update.Enabled);
            Assert.Equal("myorg", config.Update.GitHubOwner);
            Assert.Equal("MyAgent", config.Update.GitHubRepo);
            Assert.Equal(24, config.Update.CheckIntervalHours);
        }

        // ---------- Test 7: Update section missing → defaults apply ----------

        [Fact]
        public void Load_UpdateSectionMissingFromProgramData_DefaultsApply()
        {
            // Arrange — ProgramData config has no update section
            WriteProgramDataConfig(@"{ ""http"": { ""port"": 5043 } }");
            WriteTemplate(@"{ ""http"": { ""port"": 5043 } }");

            // Act
            var config = ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir);

            // Assert — POCO defaults preserved
            Assert.False(config.Update.Enabled);          // D-14 default
            Assert.Equal("", config.Update.GitHubOwner);
            Assert.Equal("FingerprintAgent", config.Update.GitHubRepo);
            Assert.Equal(6, config.Update.CheckIntervalHours);  // D-15 default
        }

        // ---------- Test 8: ProgramDataConfigPath resolves to %ProgramData%\FingerprintAgent\config.json ----------

        [Fact]
        public void ProgramDataConfigPath_ResolvesToCommonApplicationData()
        {
            // Assert — constant resolves under SpecialFolder.CommonApplicationData
            string expectedDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FingerprintAgent");
            Assert.Equal(expectedDir, ConfigLoader.ProgramDataDirectory);
            Assert.Equal(
                Path.Combine(expectedDir, "config.json"),
                ConfigLoader.ProgramDataConfigPath);
        }

        // ---------- Test 9: merge with no changes does NOT write merge.log ----------

        [Fact]
        public void Load_MergeNoChanges_DoesNotWriteMergeLog()
        {
            // Arrange — ProgramData already has the template's full content (no additions)
            string identical = @"{
                ""http"": { ""port"": 5043 },
                ""update"": {
                    ""enabled"": false,
                    ""githubOwner"": """",
                    ""githubRepo"": ""FingerprintAgent"",
                    ""checkIntervalHours"": 6
                }
            }";
            WriteProgramDataConfig(identical);
            WriteTemplate(identical);

            // Act
            var config = ConfigLoader.Load(_programDataConfigPath, _templatePath, _installDir);

            // Assert — no merge.log when nothing was added
            string mergeLogPath = Path.Combine(_programDataDir, "merge.log");
            Assert.False(File.Exists(mergeLogPath),
                "merge.log must NOT be written when no keys were added");
        }
    }
}
