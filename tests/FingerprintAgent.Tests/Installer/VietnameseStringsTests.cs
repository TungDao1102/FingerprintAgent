extern alias WixCA;

using System.IO;
using System.Reflection;
using Xunit;
using VietnameseStrings = WixCA::FingerprintAgent.Installer.Properties.VietnameseStrings;

namespace FingerprintAgent.Tests.Installer
{
    /// <summary>
    /// Tests that Vietnamese dialog strings load from the embedded .resx resource.
    /// The Designer.cs is hand-authored to access these via the strongly-typed
    /// VietnameseStrings class — this test confirms the resource wiring is correct.
    /// </summary>
    public class VietnameseStringsTests
    {
        [Fact]
        public void VcRedistMissingTitle_LoadsFromResource()
        {
            string title = VietnameseStrings.VcRedistMissingTitle;
            Assert.False(string.IsNullOrEmpty(title));
            Assert.Contains("Visual C++", title);
        }

        [Fact]
        public void VcRedistMissingBody_ContainsDownloadUrl()
        {
            string body = VietnameseStrings.VcRedistMissingBody;
            Assert.False(string.IsNullOrEmpty(body));
            Assert.Contains("https://aka.ms/vs/17/release/vc_redist.x86.exe", body);
        }

        [Fact]
        public void ScannerNotDetectedBody_IsVietnamese()
        {
            string body = VietnameseStrings.ScannerNotDetectedBody;
            Assert.False(string.IsNullOrEmpty(body));
            // "Cài đặt" (installation) is one of the few unmistakable Vietnamese diacritic markers
            Assert.Contains("Cài đặt", body);
        }

        [Fact]
        public void InstallSuccessFresh_IsVietnamese()
        {
            string s = VietnameseStrings.InstallSuccessFresh;
            Assert.False(string.IsNullOrEmpty(s));
            Assert.Contains("Dịch vụ", s);
        }

        [Fact]
        public void InstallSuccessUpgrade_ContainsVersionPlaceholder()
        {
            string s = VietnameseStrings.InstallSuccessUpgrade;
            Assert.False(string.IsNullOrEmpty(s));
            Assert.Contains("{version}", s);
        }

        [Fact]
        public void InstallGenericError_ReferencesDeploymentDoc()
        {
            string s = VietnameseStrings.InstallGenericError;
            Assert.False(string.IsNullOrEmpty(s));
            Assert.Contains("DEPLOYMENT.md", s);
        }

        [Fact]
        public void ResourceManager_FindsAllExpectedKeys()
        {
            // Compile-time / load-time check: every key referenced by Designer.cs is present.
            var rm = new System.Resources.ResourceManager(
                "FingerprintAgent.Installer.Properties.VietnameseStrings",
                typeof(VietnameseStrings).Assembly);

            string[] expectedKeys =
            {
                "VcRedistMissingTitle",
                "VcRedistMissingBody",
                "ScannerNotDetectedBody",
                "InstallSuccessFresh",
                "InstallSuccessUpgrade",
                "InstallGenericError"
            };

            foreach (var key in expectedKeys)
            {
                string value = rm.GetString(key);
                Assert.False(string.IsNullOrEmpty(value), $"Resource key '{key}' missing or empty");
            }
        }
    }
}
