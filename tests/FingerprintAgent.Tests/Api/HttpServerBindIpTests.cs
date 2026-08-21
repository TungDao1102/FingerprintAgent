using System;
using System.Net;
using System.Reflection;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Configuration;
using FingerprintAgent.Tests.Scanner;
using Xunit;

namespace FingerprintAgent.Tests.Api
{
    /// <summary>
    /// Verifies HttpServer honors config.security.bindIp as the AUTHORITATIVE bind
    /// address and ignores config.http.host when they disagree (Bug #1: bindIp was
    /// previously a dead config field — http.host was used unconditionally, so an
    /// operator-set bindIp="127.0.0.1" was silently overridden by http.host="0.0.0.0").
    /// </summary>
    public class HttpServerBindIpTests
    {
        private static string GetListenerPrefix(HttpServer server)
        {
            var listenerField = typeof(HttpServer).GetField(
                "_listener",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(listenerField);
            var listener = (HttpListener)listenerField.GetValue(server);
            Assert.NotNull(listener);
            // HttpListener has exactly one prefix in our setup
            Assert.Single(listener.Prefixes);
            foreach (string prefix in listener.Prefixes)
                return prefix;
            return null; // unreachable
        }

        private static int FindFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [Fact]
        public void Ctor_BindIpOnly_UsesBindIpAsPrefix()
        {
            // Arrange — BindIp set, Http.Host set to a different value
            var scanner = new MockScannerAdapterWithSettableProperties();
            var config = new AgentConfig
            {
                Http = new HttpConfig { Host = "0.0.0.0", Port = FindFreePort() },
                Security = new SecurityConfig { BindIp = "127.0.0.1" },
                Cors = new CorsConfig()
            };

            // Act
            using (var server = new HttpServer(config, scanner, null))
            {
                // Assert — prefix reflects BindIp, NOT http.host
                string prefix = GetListenerPrefix(server);
                Assert.Contains("127.0.0.1", prefix);
                Assert.DoesNotContain("0.0.0.0", prefix);
            }
        }

        [Fact]
        public void Ctor_HostOnly_UsesHostAsBackwardCompatFallback()
        {
            // Arrange — only http.host set (legacy configs); Security.BindIp null
            var scanner = new MockScannerAdapterWithSettableProperties();
            var config = new AgentConfig
            {
                Http = new HttpConfig { Host = "127.0.0.1", Port = FindFreePort() },
                Security = new SecurityConfig { BindIp = null },
                Cors = new CorsConfig()
            };

            // Act
            using (var server = new HttpServer(config, scanner, null))
            {
                // Assert — fallback to http.host when BindIp not set
                string prefix = GetListenerPrefix(server);
                Assert.Contains("127.0.0.1", prefix);
            }
        }

        [Fact]
        public void Ctor_BothSetAndAgree_UsesEither()
        {
            // Arrange — both agree
            var scanner = new MockScannerAdapterWithSettableProperties();
            var config = new AgentConfig
            {
                Http = new HttpConfig { Host = "127.0.0.1", Port = FindFreePort() },
                Security = new SecurityConfig { BindIp = "127.0.0.1" },
                Cors = new CorsConfig()
            };

            // Act
            using (var server = new HttpServer(config, scanner, null))
            {
                string prefix = GetListenerPrefix(server);
                Assert.Contains("127.0.0.1", prefix);
            }
        }

        [Fact]
        public void Ctor_BothSetAndDisagree_PrefersBindIp()
        {
            // Arrange — THE SECURITY BUG: operator set bindIp="127.0.0.1" but
            // http.host is "0.0.0.0". Pre-fix, the service bound to 0.0.0.0
            // (network-reachable) despite operator intent. Post-fix, BindIp wins.
            var scanner = new MockScannerAdapterWithSettableProperties();
            var config = new AgentConfig
            {
                Http = new HttpConfig { Host = "0.0.0.0", Port = FindFreePort() },
                Security = new SecurityConfig { BindIp = "127.0.0.1" },
                Cors = new CorsConfig()
            };

            // Act
            using (var server = new HttpServer(config, scanner, null))
            {
                // Assert — BindIp wins
                string prefix = GetListenerPrefix(server);
                Assert.Contains("127.0.0.1", prefix);
                Assert.DoesNotContain("0.0.0.0", prefix);
            }
        }

        [Fact]
        public void Ctor_NeitherSet_DefaultsToLoopback()
        {
            // Arrange — both null (should never happen via ConfigLoader, but defensive)
            var scanner = new MockScannerAdapterWithSettableProperties();
            var config = new AgentConfig
            {
                Http = new HttpConfig { Host = null, Port = FindFreePort() },
                Security = new SecurityConfig { BindIp = null },
                Cors = new CorsConfig()
            };

            // Act
            using (var server = new HttpServer(config, scanner, null))
            {
                // Assert — defaults to loopback (safest)
                string prefix = GetListenerPrefix(server);
                Assert.Contains("127.0.0.1", prefix);
            }
        }

        [Fact]
        public void Ctor_EmptyStrings_DefaultsToLoopback()
        {
            // Arrange — both empty strings
            var scanner = new MockScannerAdapterWithSettableProperties();
            var config = new AgentConfig
            {
                Http = new HttpConfig { Host = "", Port = FindFreePort() },
                Security = new SecurityConfig { BindIp = "" },
                Cors = new CorsConfig()
            };

            // Act
            using (var server = new HttpServer(config, scanner, null))
            {
                string prefix = GetListenerPrefix(server);
                Assert.Contains("127.0.0.1", prefix);
            }
        }
    }
}
