using System;
using System.IO;
using System.Reflection;
using System.Threading;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests.Configuration
{
    public class ConfigFileWatcherTests : IDisposable
    {
        private readonly string _tempDir;
        private bool _disposed;

        public ConfigFileWatcherTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
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

        // ---------- Helpers ----------

        private string WriteValidConfig()
        {
            string path = Path.Combine(_tempDir, "config.json");
            File.WriteAllText(path, @"{
                ""scanner"": { ""priority"": [""ZKTeco""], ""mockMode"": false },
                ""cors"":    { ""mode"": ""wildcard"", ""allowedOrigins"": [] }
            }");
            return path;
        }

        // ---------- Constructor ----------

        [Fact]
        public void Constructor_NullConfigPath_ThrowsArgumentNullException()
        {
            // Arrange / Act / Assert
            Assert.Throws<ArgumentNullException>(() => new ConfigFileWatcher(null, null));
        }

        [Fact]
        public void Constructor_RelativeConfigPath_ThrowsArgumentException()
        {
            // Arrange / Act / Assert
            // "config.json" has no directory component, so Path.GetDirectoryName returns ""
            // and ConfigFileWatcher rejects it.
            Assert.Throws<ArgumentException>(() => new ConfigFileWatcher("config.json", null));
        }

        [Fact]
        public void Constructor_StartsWatchingFile()
        {
            // Arrange
            string path = WriteValidConfig();

            // Act
            using (var watcher = new ConfigFileWatcher(path, null))
            {
                // Assert — reflectively inspect the internal FileSystemWatcher.EnableRaisingEvents
                var fsw = (FileSystemWatcher)typeof(ConfigFileWatcher)
                    .GetField("_watcher", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(watcher);
                Assert.NotNull(fsw);
                Assert.True(fsw.EnableRaisingEvents);
            }
        }

        // ---------- ConfigReloaded event ----------

        [Fact]
        public void FileChanged_ValidConfig_FiresConfigReloaded()
        {
            // Arrange
            string path = WriteValidConfig();
            int fired = 0;
            AgentConfig received = null;
            var signal = new ManualResetEventSlim(false);

            using (var watcher = new ConfigFileWatcher(path, null))
            {
                watcher.ConfigReloaded += cfg =>
                {
                    Interlocked.Increment(ref fired);
                    received = cfg;
                    signal.Set();
                };

                // Act — overwrite the file to trigger a Changed event
                File.WriteAllText(path, @"{
                    ""scanner"": { ""priority"": [""SecuGen""], ""mockMode"": true },
                    ""cors"":    { ""mode"": ""allowlist"", ""allowedOrigins"": [""http://x.com""] }
                }");

                // Assert — wait for debounce (300ms) plus margin
                Assert.True(signal.Wait(TimeSpan.FromSeconds(3)),
                    "ConfigReloaded did not fire within 3s");
                Assert.Equal(1, Volatile.Read(ref fired));
                Assert.NotNull(received);
                Assert.NotNull(received.Scanner);
                Assert.NotNull(received.Cors);
                Assert.Equal("SecuGen", received.Scanner.Priority[0]);
            }
        }

        [Fact]
        public void FileChanged_MultipleRapidChanges_FiresOnceAfterDebounce()
        {
            // Arrange
            string path = WriteValidConfig();
            int fired = 0;
            var signal = new ManualResetEventSlim(false);

            using (var watcher = new ConfigFileWatcher(path, null))
            {
                watcher.ConfigReloaded += _ =>
                {
                    Interlocked.Increment(ref fired);
                    signal.Set();
                };

                // Act — three rapid writes inside the debounce window
                for (int i = 0; i < 3; i++)
                {
                    File.WriteAllText(path, $@"{{
                        ""scanner"": {{ ""priority"": [""P{i}""], ""mockMode"": false }},
                        ""cors"":    {{ ""mode"": ""wildcard"", ""allowedOrigins"": [] }}
                    }}");
                    Thread.Sleep(50);
                }

                // Wait long enough for debounce to settle and event to fire
                Assert.True(signal.Wait(TimeSpan.FromSeconds(3)),
                    "ConfigReloaded did not fire within 3s");
                Thread.Sleep(500); // extra slack — confirm no late firings

                // Assert — debounced to exactly one event
                Assert.Equal(1, Volatile.Read(ref fired));
            }
        }

        [Fact]
        public void FileChanged_InvalidJson_DoesNotFireConfigReloaded()
        {
            // Arrange — D-08: invalid JSON must not crash, must not fire event
            string path = WriteValidConfig();
            int fired = 0;

            using (var watcher = new ConfigFileWatcher(path, null))
            {
                watcher.ConfigReloaded += _ => Interlocked.Increment(ref fired);

                // Act — write garbage
                File.WriteAllText(path, "{not valid json");

                // Assert — wait past debounce + parse time
                Thread.Sleep(1500);
                Assert.Equal(0, Volatile.Read(ref fired));
            }
        }

        [Fact]
        public void FileChanged_MissingScannerSection_FiresConfigReloadedWithDefaults()
        {
            // Arrange — D-06 documents that only Scanner + Cors are reloadable, but
            // AgentConfig field initializers mean Scanner is never null after
            // ConfigLoader.Load returns. The event therefore fires with default
            // Scanner values rather than rejecting the reload.
            string path = WriteValidConfig();
            int fired = 0;
            AgentConfig received = null;
            var signal = new ManualResetEventSlim(false);

            using (var watcher = new ConfigFileWatcher(path, null))
            {
                watcher.ConfigReloaded += cfg =>
                {
                    Interlocked.Increment(ref fired);
                    received = cfg;
                    signal.Set();
                };

                // Act — write config WITHOUT scanner section
                File.WriteAllText(path, @"{
                    ""cors"": { ""mode"": ""allowlist"", ""allowedOrigins"": [""http://x.com""] }
                }");

                // Assert
                Assert.True(signal.Wait(TimeSpan.FromSeconds(3)),
                    "ConfigReloaded did not fire within 3s");
                Assert.Equal(1, Volatile.Read(ref fired));
                Assert.NotNull(received);
                Assert.NotNull(received.Scanner);
                Assert.Equal(4, received.Scanner.Priority.Length);
                Assert.True(received.Scanner.MockMode);
                Assert.Equal("allowlist", received.Cors.Mode);
            }
        }

        [Fact]
        public void FileChanged_MissingCorsSection_FiresConfigReloadedWithDefaults()
        {
            // Arrange — mirror of the Scanner test. Cors is never null because
            // AgentConfig initializers populate it; missing section yields defaults.
            string path = WriteValidConfig();
            int fired = 0;
            AgentConfig received = null;
            var signal = new ManualResetEventSlim(false);

            using (var watcher = new ConfigFileWatcher(path, null))
            {
                watcher.ConfigReloaded += cfg =>
                {
                    Interlocked.Increment(ref fired);
                    received = cfg;
                    signal.Set();
                };

                // Act — write config WITHOUT cors section
                File.WriteAllText(path, @"{
                    ""scanner"": { ""priority"": [""ZKTeco""], ""mockMode"": false }
                }");

                // Assert
                Assert.True(signal.Wait(TimeSpan.FromSeconds(3)),
                    "ConfigReloaded did not fire within 3s");
                Assert.Equal(1, Volatile.Read(ref fired));
                Assert.NotNull(received);
                Assert.NotNull(received.Cors);
                Assert.Equal("wildcard", received.Cors.Mode);
                Assert.Empty(received.Cors.AllowedOrigins);
                Assert.Equal("ZKTeco", received.Scanner.Priority[0]);
            }
        }

        // ---------- Dispose ----------

        [Fact]
        public void Dispose_IsIdempotent()
        {
            // Arrange
            string path = WriteValidConfig();
            var watcher = new ConfigFileWatcher(path, null);

            // Act
            watcher.Dispose();
            watcher.Dispose(); // second call must not throw

            // Assert — no exception means success; accessing disposed object
            // would throw ObjectDisposedException if Dispose were not idempotent.
            Assert.True(true);
        }

        [Fact]
        public void Dispose_StopsWatching()
        {
            // Arrange
            string path = WriteValidConfig();
            int fired = 0;

            var watcher = new ConfigFileWatcher(path, null);
            watcher.ConfigReloaded += _ => Interlocked.Increment(ref fired);

            // Act
            watcher.Dispose();
            File.WriteAllText(path, @"{
                ""scanner"": { ""priority"": [""Futronic""], ""mockMode"": false },
                ""cors"":    { ""mode"": ""allowlist"", ""allowedOrigins"": [] }
            }");

            // Assert — no event should fire after Dispose
            Thread.Sleep(1500);
            Assert.Equal(0, Volatile.Read(ref fired));
        }
    }
}
