extern alias WixCA;

using Xunit;
using CustomActions = WixCA::FingerprintAgent.Installer.CustomActions;

namespace FingerprintAgent.Tests.Installer
{
    /// <summary>
    /// Tests for CustomActions.BuildDelayedAutoStartArguments — argument construction
    /// for the SetDelayedAutoStart deferred CustomAction (D-32a).
    ///
    /// Background: the MSI previously authored a DelayedAutoStart RegistryValue directly
    /// inside HKLM\SYSTEM\CurrentControlSet\Services\FingerprintAgent. On uninstall,
    /// DeleteServices (seq 2000) destroys that SCM-owned key before RemoveRegistryValues
    /// (seq 2600) tries to remove the value → MSI Error 1409 ("Could not read security
    /// information for key...") → 1603 rollback. The value must be set post-InstallServices
    /// by the CA instead; these tests guard the exact sc.exe argument contract.
    /// </summary>
    public class SetDelayedAutoStartTests
    {
        [Fact]
        public void BuildDelayedAutoStartArguments_WithServiceName_ReturnsScConfigDelayedAuto()
        {
            string args = CustomActions.BuildDelayedAutoStartArguments("FingerprintAgent");

            // sc.exe requires a space AFTER 'start=' (before the value) — 'start=delayed-auto'
            // is silently misparsed. Assert the full string so neither the space nor the
            // subcommand can regress.
            Assert.Equal("config FingerprintAgent start= delayed-auto", args);
        }

        [Fact]
        public void BuildDelayedAutoStartArguments_CustomServiceName_UsesProvidedName()
        {
            string args = CustomActions.BuildDelayedAutoStartArguments("OtherService");

            Assert.Equal("config OtherService start= delayed-auto", args);
        }
    }
}
