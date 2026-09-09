extern alias WixCA;

using System;
using System.Collections.Generic;
using Xunit;
using CustomActions = WixCA::FingerprintAgent.Installer.CustomActions;
using InstallMessage = WixToolset.Dtf.WindowsInstaller.InstallMessage;

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
        public void RegistryKeys_AreBothWow6432AndNativePaths()
        {
            // If either path is dropped, CheckVcRedist silently fails on that OS variant.
            Assert.Equal(2, CustomActions.VcRedistRegistryKeys.Length);
            Assert.Contains(CustomActions.VcRedistRegistryKeys,
                k => k.Contains(@"Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"));
            Assert.Contains(CustomActions.VcRedistRegistryKeys,
                k => k.Contains(@"Microsoft\VisualStudio\14.0\VC\Runtimes\x86"));
        }

        [Fact]
        public void IsVcRedistInstalled_FakeReader_ReturnsTrueWhenInstalled()
        {
            Func<string, object> reader = key => 1;
            bool installed = CustomActions.IsVcRedistInstalled(out string foundKey, reader);
            Assert.True(installed);
            Assert.NotNull(foundKey);
            Assert.Contains("VisualStudio\\14.0\\VC\\Runtimes\\x86", foundKey);
        }

        [Fact]
        public void IsVcRedistInstalled_FakeReader_ReturnsFalseWhenNotInstalled()
        {
            Func<string, object> reader = key => 0;
            bool installed = CustomActions.IsVcRedistInstalled(out string foundKey, reader);
            Assert.False(installed);
            Assert.Null(foundKey);
        }

        [Fact]
        public void IsVcRedistInstalled_FakeReader_ReturnsFalseWhenKeysAbsent()
        {
            Func<string, object> reader = key => null;
            bool installed = CustomActions.IsVcRedistInstalled(out string foundKey, reader);
            Assert.False(installed);
            Assert.Null(foundKey);
        }

        [Fact]
        public void IsVcRedistInstalled_OnDeveloperMachine_DoesNotThrow()
        {
            bool installed = CustomActions.IsVcRedistInstalled(out string foundKey);
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
            var httpConfig = new FingerprintAgent.Configuration.HttpConfig();
            var expectedUrl = $"http://{httpConfig.Host}:{httpConfig.Port}/health";
            var actualUrl = CustomActions.HealthUrl;
            Assert.Equal(expectedUrl, actualUrl);
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

        // -------------------------------------------------------------------
        // CR-07 follow-up: Vietnamese missing-VC++ notice via Session.Message
        // -------------------------------------------------------------------

        [Fact]
        public void VcRedistUserMessageType_IsUserMessageWithOkButtonsWarningIconAndForeground()
        {
            // Pinned against the raw Win32 MB_* contract rather than DTF enums so a DTF
            // enum drift cannot silently change what the message box looks like.
            int value = (int)CustomActions.VcRedistUserMessageType;
            Assert.Equal(0x03000000, value & unchecked((int)0xFF000000)); // INSTALLMESSAGE_USER
            Assert.Equal(0x0, value & 0xF);                              // MB_OK
            Assert.Equal(0x30, value & 0xF0);                            // MB_ICONWARNING
            Assert.Equal(0x00010000, value & 0x00010000);                // MB_SETFOREGROUND
        }

        [Fact]
        public void VcRedistErrorPropertyNames_MatchWixPropertyDeclarations()
        {
            // Must match <Property Id="..."> in FingerprintAgent.Installer.wxs —
            // Windows Installer property names are case-sensitive.
            Assert.Equal("VcRedistErrorTitleText", CustomActions.VcRedistErrorTitleProperty);
            Assert.Equal("VcRedistErrorBodyText", CustomActions.VcRedistErrorBodyProperty);
        }

        [Fact]
        public void BuildVcRedistMessageText_TitleAndBody_JoinsWithBlankLine()
        {
            string text = CustomActions.BuildVcRedistMessageText("Tiêu đề", "Nội dung");
            Assert.Equal("Tiêu đề\r\n\r\nNội dung", text);
        }

        [Fact]
        public void BuildVcRedistMessageText_BrMarkersInBody_ConvertedToRealNewlines()
        {
            // [BR] is an MSI Text-control marker; inside a Session.Message formatted
            // field it would be swallowed by the property-reference parser. The
            // message box needs literal CRLF instead.
            string text = CustomActions.BuildVcRedistMessageText(
                "Thiếu VC++ (x86)",
                "Dòng 1[BR]Dòng 2");
            Assert.Equal("Thiếu VC++ (x86)\r\n\r\nDòng 1\r\nDòng 2", text);
        }

        [Fact]
        public void BuildVcRedistMessageText_NullTitle_ReturnsNormalizedBodyOnly()
        {
            Assert.Equal("Dòng 1\r\nDòng 2", CustomActions.BuildVcRedistMessageText(null, "Dòng 1[BR]Dòng 2"));
        }

        [Fact]
        public void BuildVcRedistMessageText_EmptyBody_ReturnsTitleOnly()
        {
            Assert.Equal("Chỉ tiêu đề", CustomActions.BuildVcRedistMessageText("Chỉ tiêu đề", string.Empty));
        }

        [Fact]
        public void BuildVcRedistMessageText_BothEmpty_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, CustomActions.BuildVcRedistMessageText(null, null));
        }

        [Fact]
        public void ShowVcRedistMissingNotice_Core_LogsNoticeAndSendsLocalizedMessage()
        {
            // The NOTICE log line must be written unconditionally — /qn silent installs
            // suppress message boxes, so /l*v (or MsiLogging) output is the only trace
            // of the Vietnamese reason. Covers review point: silent-deploy observability.
            var logLines = new List<string>();
            InstallMessage? sentType = null;
            string sentText = null;
            CustomActions.ShowVcRedistMissingNotice(
                "Thiếu VC++ (x86)",
                "Dòng 1[BR]Dòng 2",
                logLines.Add,
                (type, record) =>
                {
                    sentType = type;
                    // Read inside the sink: production owns the Record's native handle
                    // and disposes it when its using block exits.
                    sentText = (string)record[0];
                });

            string expectedText = "Thiếu VC++ (x86)\r\n\r\nDòng 1\r\nDòng 2";
            Assert.Contains(CustomActions.LogPrefix + "NOTICE: " + expectedText, logLines);
            Assert.Equal(CustomActions.VcRedistUserMessageType, sentType.Value);
            Assert.Equal(expectedText, sentText);
        }

        [Fact]
        public void ShowVcRedistMissingNotice_Core_EmptyText_SkipsMessageAndLogsSkipReason()
        {
            var logLines = new List<string>();
            bool messageSent = false;
            CustomActions.ShowVcRedistMissingNotice(
                null,
                null,
                logLines.Add,
                (type, record) => messageSent = true);

            Assert.False(messageSent);
            Assert.Contains(logLines, line => line.Contains("skipping notice"));
        }
    }
}
