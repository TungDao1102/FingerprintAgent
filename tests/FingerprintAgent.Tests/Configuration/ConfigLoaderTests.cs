using System;
using System.IO;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests.Configuration
{
    public class ConfigLoaderTests : IDisposable
    {
        private readonly string _tempDir;
        private bool _disposed;

        public ConfigLoaderTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [Fact]
        public void Load_ValidConfig_ReturnsAgentConfigWithCorrectValues()
        {
            // Arrange
            string configPath = Path.Combine(_tempDir, "config.json");
            File.WriteAllText(configPath, @"{
                ""service"": {
                    ""name"": ""TestAgent"",
                    ""displayName"": ""Test Agent"",
                    ""description"": ""Test description""
                },
                ""http"": {
                    ""host"": ""0.0.0.0"",
                    ""port"": 5044
                },
                ""cors"": {
                    ""mode"": ""allowlist"",
                    ""allowedOrigins"": [""http://trusted.com""]
                },
                ""scanner"": {
                    ""priority"": [""SecuGen""],
                    ""mockMode"": false
                },
                ""logging"": {
                    ""level"": ""DEBUG"",
                    ""file"": ""C:\\Logs\\test.log"",
                    ""maxSizeMb"": 20,
                    ""maxFiles"": 10
                },
                ""security"": {
                    ""bindIp"": ""0.0.0.0""
                }
            }");

            // Act
            var config = ConfigLoader.LoadFromDirectory(_tempDir);

            // Assert
            Assert.NotNull(config);
            Assert.NotNull(config.Service);
            Assert.Equal("TestAgent", config.Service.Name);
            Assert.Equal("Test Agent", config.Service.DisplayName);
            Assert.Equal("Test description", config.Service.Description);

            Assert.NotNull(config.Http);
            Assert.Equal("0.0.0.0", config.Http.Host);
            Assert.Equal(5044, config.Http.Port);

            Assert.NotNull(config.Cors);
            Assert.Equal("allowlist", config.Cors.Mode);
            Assert.Single(config.Cors.AllowedOrigins);
            Assert.Equal("http://trusted.com", config.Cors.AllowedOrigins[0]);

            Assert.NotNull(config.Scanner);
            Assert.Single(config.Scanner.Priority);
            Assert.Equal("SecuGen", config.Scanner.Priority[0]);
            Assert.False(config.Scanner.MockMode);

            Assert.NotNull(config.Logging);
            Assert.Equal("DEBUG", config.Logging.Level);
            Assert.Equal(@"C:\Logs\test.log", config.Logging.File);
            Assert.Equal(20, config.Logging.MaxSizeMb);
            Assert.Equal(10, config.Logging.MaxFiles);

            Assert.NotNull(config.Security);
            Assert.Equal("0.0.0.0", config.Security.BindIp);
        }

        [Fact]
        public void Load_ConfigMissing_ThrowsFileNotFoundException()
        {
            // Arrange
            string emptyDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);

            // Act & Assert
            var ex = Assert.Throws<FileNotFoundException>(() =>
                ConfigLoader.LoadFromDirectory(emptyDir));
            Assert.Contains("config.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Load_InvalidJson_ThrowsInvalidDataException()
        {
            // Arrange
            string configPath = Path.Combine(_tempDir, "config.json");
            File.WriteAllText(configPath, "{invalid json missing quotes}");

            // Act & Assert - Microsoft.Extensions.Configuration.Json wraps JSON parse errors
            // in InvalidDataException (not FormatException) via JsonConfigurationProvider
            Assert.Throws<InvalidDataException>(() =>
                ConfigLoader.LoadFromDirectory(_tempDir));
        }

        [Fact]
        public void Load_ConfigHttpPortOverride_ReturnsCustomPort()
        {
            // Arrange
            string configPath = Path.Combine(_tempDir, "config.json");
            File.WriteAllText(configPath, @"{
                ""http"": {
                    ""port"": 5044
                }
            }");

            // Act
            var config = ConfigLoader.LoadFromDirectory(_tempDir);

            // Assert
            Assert.Equal(5044, config.Http.Port);
        }

        [Fact]
        public void Load_OptionalFields_UseDefaults()
        {
            // Arrange
            string configPath = Path.Combine(_tempDir, "config.json");
            File.WriteAllText(configPath, @"{}");

            // Act
            var config = ConfigLoader.LoadFromDirectory(_tempDir);

            // Assert - defaults
            Assert.Equal("FingerprintAgent", config.Service.Name);
            Assert.Equal("127.0.0.1", config.Http.Host);
            Assert.Equal(5043, config.Http.Port);
            Assert.Equal("wildcard", config.Cors.Mode);
            Assert.Empty(config.Cors.AllowedOrigins);
            Assert.True(config.Scanner.MockMode);
            Assert.Equal("INFO", config.Logging.Level);
            Assert.Equal("127.0.0.1", config.Security.BindIp);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    if (Directory.Exists(_tempDir))
                        Directory.Delete(_tempDir, recursive: true);
                }
                catch { }
            }
        }
    }
}
