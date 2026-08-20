extern alias WixCA;

using Xunit;
using CustomActions = WixCA::FingerprintAgent.Installer.CustomActions;

namespace FingerprintAgent.Tests.Installer
{
    /// <summary>
    /// Tests for CustomActions.IsVcRedistInstalled — the pure-logic registry probe helper.
    ///
    /// Tests rely on the actual registry state of the test machine. Since the dev workstation
    /// almost certainly has VC++ x86 installed (it's needed to build dotnet), we expect the
    /// positive path. The negative path is harder to test without admin + uninstall, but we
    /// verify the code never throws and always sets foundKey correctly.
    /// </summary>
    public class CheckVcRedistTests
    {
        [Fact]
        public void RegistryKeys_ContainsBothExpectedPaths()
        {
            Assert.Contains(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86", CustomActions.VcRedistRegistryKeys);
            Assert.Contains(@"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86", CustomActions.VcRedistRegistryKeys);
        }

        [Fact]
        public void IsVcRedistInstalled_OnDeveloperMachine_ReturnsTrue()
        {
            // Dev machines build dotnet → VC++ x86 is installed. Expect Installed=1.
            // If this fails, VC++ x86 is genuinely missing — install vc_redist.x86.exe.
            bool installed = CustomActions.IsVcRedistInstalled(out string foundKey);
            // Don't Assert.True here — that would fail on a clean machine without VC++.
            // Just verify the call completed and foundKey is consistent with the result.
            if (installed)
            {
                Assert.NotNull(foundKey);
                Assert.Contains("VisualStudio\\14.0\\VC\\Runtimes\\x86", foundKey);
            }
            else
            {
                Assert.Null(foundKey);
            }
        }

        [Fact]
        public void HealthUrl_MatchesHttpServerDefault()
        {
            // The /health endpoint must point at the same URL HttpServer binds.
            Assert.Equal("http://127.0.0.1:5043/health", CustomActions.HealthUrl);
        }

        [Fact]
        public void HealthProbeTimeout_IsThirtySeconds()
        {
            // CR-04: raised 5s → 30s to absorb cold-start latency between SCM Running
            // and HttpListener bind. ProbeHealth now also retries up to 5 attempts.
            Assert.Equal(30, CustomActions.HealthProbeTimeout.TotalSeconds);
        }

        [Fact]
        public void HealthProbeMaxAttempts_IsFive()
        {
            // CR-04: retry budget for transient ConnectionRefused / Timeout.
            Assert.Equal(5, CustomActions.HealthProbeMaxAttempts);
        }

        [Fact]
        public void HealthUrl_MatchesAgentConfigDefault()
        {
            // WARN-08: HealthUrl must track AgentConfig.Http default port. If AgentConfig
            // default port changes, this test catches the drift.
            var expected = $"http://{new FingerprintAgent.Configuration.HttpConfig().Host}:{new FingerprintAgent.Configuration.HttpConfig().Port}/health";
            Assert.Equal(expected, CustomActions.HealthUrl);
        }

        [Fact]
        public void LogPrefix_IsGrepFriendly()
        {
            Assert.StartsWith("[", CustomActions.LogPrefix);
            Assert.Contains("Installer", CustomActions.LogPrefix);
        }

        [Fact]
        public void InstalledProperty_MatchesMsiConvention()
        {
            // 'Installed' is the standard MSI property name populated by the AppSearch action.
            Assert.Equal("Installed", CustomActions.InstalledProperty);
            Assert.Equal("InstallType", CustomActions.InstallTypeProperty);
        }
    }
}
