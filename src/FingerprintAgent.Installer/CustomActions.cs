using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FingerprintAgent.Configuration;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CustomActionAttribute = WixToolset.Dtf.WindowsInstaller.CustomActionAttribute;
using Session = WixToolset.Dtf.WindowsInstaller.Session;
using ActionResult = WixToolset.Dtf.WindowsInstaller.ActionResult;

namespace FingerprintAgent.Installer
{
    /// <summary>
    /// CustomAction entry points for the FingerprintAgent MSI installer.
    /// Each method is decorated with [CustomAction] so the DTF MSBuild target
    /// (WixToolset.Dtf.CustomAction) wires them as MSI CustomAction entries
    /// when wrapping the DLL with MakeSfxCA.exe.
    ///
    /// Each method returns ActionResult.Success or ActionResult.Failure.
    /// Failure causes msiexec to roll back the install; Success continues.
    /// </summary>
    public class CustomActions
    {
        // D-12: VS 2015-2022 VC++ x86 redistributable registry location.
        // On x64 OS the x86 package is under Wow6432Node; on x86 OS it's directly under HKLM\SOFTWARE.
        internal static readonly string[] VcRedistRegistryKeys =
        {
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86",
            @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"
        };

        // D-05: /health probe URL matches HttpServer defaults (127.0.0.1:5043).
        internal const string HealthUrl = "http://127.0.0.1:5043/health";

        // D-05: 5s timeout covers cold-start + first /health endpoint warmup.
        internal static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(5);

        // Standard MSI log message prefix used by all CustomActions for grep-friendly output.
        internal const string LogPrefix = "[FingerprintAgent.Installer] ";

        // D-39: standard MSI property populated by WiX when a previous version is detected.
        internal const string InstalledProperty = "Installed";

        // D-38: standard MSI property this CustomAction populates to drive success-dialog selection.
        internal const string InstallTypeProperty = "InstallType";

        // -----------------------------------------------------------------------
        // CheckVcRedist — VC++ x86 runtime detection (D-09/D-10/D-12)
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point. Probes registry for VC++ x86 runtime. Returns
        /// Failure if neither x86 nor Wow6432Node key has Installed=1 (causes MSI rollback
        /// + triggers Vietnamese error dialog). Returns Success on registry access failure
        /// (fail-open: better to install and let runtime fail than to refuse on transient
        /// registry permissions).
        /// </summary>
        [CustomAction]
        public static ActionResult CheckVcRedist(Session session)
        {
            session.Log(LogPrefix + "Checking VC++ x86 runtime presence...");
            try
            {
                bool installed = IsVcRedistInstalled(out string foundKey);
                if (installed)
                {
                    session.Log(LogPrefix + "VC++ x86 runtime found at HKLM\\" + foundKey);
                    return ActionResult.Success;
                }

                session.Log(LogPrefix + "VC++ x86 runtime NOT installed; install will roll back with Vietnamese dialog");
                session["VcRedistMissingDialog"] = "1";
                return ActionResult.Failure;
            }
            catch (Exception ex)
            {
                // Fail-open: registry probe failed for permission reasons. Let install proceed.
                // The runtime will surface a clear error if VC++ really is missing.
                session.Log(LogPrefix + "VC++ detection failed (fail-open): " + ex.Message);
                return ActionResult.Success;
            }
        }

        /// <summary>
        /// Pure logic helper. Probes both registry locations for VC++ x86 Installed=1.
        /// Returns true if found, plus the key path where it was found.
        /// Exposed as internal static so tests can exercise registry logic without a Session.
        /// </summary>
        internal static bool IsVcRedistInstalled(out string foundKey)
        {
            foreach (var key in VcRedistRegistryKeys)
            {
                using (var reg = Registry.LocalMachine.OpenSubKey(key))
                {
                    var installed = reg?.GetValue("Installed");
                    if (installed != null && Convert.ToInt32(installed) == 1)
                    {
                        foundKey = key;
                        return true;
                    }
                }
            }
            foundKey = null;
            return false;
        }

        // -----------------------------------------------------------------------
        // ProbeHealthAfterInstall — D-05 /health probe
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point. Issues HTTP GET to the agent's /health endpoint
        /// after the service has been started by WiX ServiceControl. Returns Success
        /// on HTTP 200 or HTTP 503 with degraded body (scanner absent is acceptable —
        /// a separate dialog prompts operator to plug in scanner). Returns Failure
        /// for any other outcome, triggering rollback.
        /// </summary>
        [CustomAction]
        public static ActionResult ProbeHealthAfterInstall(Session session)
        {
            session.Log(LogPrefix + "Probing /health at " + HealthUrl + "...");
            var probeResult = ProbeHealth();
            session.Log(LogPrefix + "/health probe outcome: " + probeResult.Outcome
                + (probeResult.HttpStatus.HasValue ? " (HTTP " + probeResult.HttpStatus.Value + ")" : "")
                + (probeResult.Body != null ? " body=" + probeResult.Body : ""));

            // Set dialog-routing properties based on probe outcome.
            switch (probeResult.Outcome)
            {
                case HealthProbeOutcome.Healthy:
                    session["ScannerNotDetectedDialog"] = "0";
                    return ActionResult.Success;

                case HealthProbeOutcome.DegradedScannerMissing:
                    session["ScannerNotDetectedDialog"] = "1";
                    return ActionResult.Success;

                default:
                    // Unhealthy: roll back
                    return ActionResult.Failure;
            }
        }

        /// <summary>
        /// Pure logic helper. Performs the HTTP probe and classifies the result.
        /// Exposed as internal static so tests can verify classification rules.
        /// </summary>
        internal static HealthProbeResult ProbeHealth()
        {
            using (var client = new HttpClient { Timeout = HealthProbeTimeout })
            {
                try
                {
                    var task = client.GetAsync(HealthUrl);
                    task.Wait(HealthProbeTimeout);
                    if (!task.IsCompleted)
                    {
                        return new HealthProbeResult(HealthProbeOutcome.Timeout, null, null);
                    }
                    var response = task.Result;
                    int status = (int)response.StatusCode;
                    string body = null;
                    try
                    {
                        var bodyTask = response.Content.ReadAsStringAsync();
                        bodyTask.Wait(TimeSpan.FromSeconds(2));
                        body = bodyTask.Result;
                    }
                    catch
                    {
                        // Body read failure is non-fatal — classification based on status only.
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        return new HealthProbeResult(HealthProbeOutcome.Healthy, status, body);
                    }
                    if (status == 503)
                    {
                        // 503 from HealthHandler when scanner is in max backoff (D-38).
                        // Body should contain "degraded" — accept either way for v1.
                        return new HealthProbeResult(HealthProbeOutcome.DegradedScannerMissing, status, body);
                    }
                    return new HealthProbeResult(HealthProbeOutcome.Unhealthy, status, body);
                }
                catch (AggregateException)
                {
                    return new HealthProbeResult(HealthProbeOutcome.ConnectionRefused, null, null);
                }
                catch (HttpRequestException)
                {
                    return new HealthProbeResult(HealthProbeOutcome.ConnectionRefused, null, null);
                }
                catch (Exception)
                {
                    return new HealthProbeResult(HealthProbeOutcome.Unhealthy, null, null);
                }
            }
        }

        // -----------------------------------------------------------------------
        // SeedProgramDataConfig — D-33/D-34/D-35 first-install seed + upgrade merge
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point. Copies config.template.json from INSTALLFOLDER to
        /// C:\ProgramData\FingerprintAgent\config.json on first install; on upgrade,
        /// performs smart-merge via ConfigMerger (adds new keys, preserves user values).
        /// Writes merge.log to ProgramData when keys were added.
        /// </summary>
        [CustomAction]
        public static ActionResult SeedProgramDataConfig(Session session)
        {
            string installTemplate = session.CustomActionData.ContainsKey("InstallTemplatePath")
                ? session.CustomActionData["InstallTemplatePath"]
                : null;
            string programDataConfig = session.CustomActionData.ContainsKey("ProgramDataConfigPath")
                ? session.CustomActionData["ProgramDataConfigPath"]
                : null;

            if (string.IsNullOrEmpty(installTemplate) || string.IsNullOrEmpty(programDataConfig))
            {
                session.Log(LogPrefix + "SeedProgramDataConfig: missing path properties (InstallTemplatePath='" + installTemplate + "', ProgramDataConfigPath='" + programDataConfig + "')");
                return ActionResult.Failure;
            }

            session.Log(LogPrefix + "Seeding ProgramData config from " + installTemplate + " -> " + programDataConfig);
            try
            {
                var outcome = SeedProgramDataConfigCore(installTemplate, programDataConfig);
                session.Log(LogPrefix + "SeedProgramDataConfig result: " + outcome);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log(LogPrefix + "SeedProgramDataConfig failed: " + ex.Message);
                return ActionResult.Failure;
            }
        }

        /// <summary>
        /// Pure logic helper. Performs the file copy / smart-merge / merge.log write.
        /// Exposed as internal static so tests can verify the algorithm.
        /// Returns a string describing the outcome (for logging).
        /// </summary>
        internal static string SeedProgramDataConfigCore(string installTemplatePath, string programDataConfigPath)
        {
            if (!File.Exists(installTemplatePath))
            {
                throw new FileNotFoundException(
                    "Template config not found at install path (MSI should ship it).", installTemplatePath);
            }

            string programDataDir = Path.GetDirectoryName(programDataConfigPath);
            if (!string.IsNullOrEmpty(programDataDir) && !Directory.Exists(programDataDir))
            {
                Directory.CreateDirectory(programDataDir);
            }

            // First install: seed ProgramData from template.
            if (!File.Exists(programDataConfigPath))
            {
                File.Copy(installTemplatePath, programDataConfigPath);
                return "Seeded ProgramData config from template (first install)";
            }

            // Upgrade: smart merge.
            string userJsonText = File.ReadAllText(programDataConfigPath);
            string templateJsonText = File.ReadAllText(installTemplatePath);
            var userConfig = JObject.Parse(userJsonText);
            var templateConfig = JObject.Parse(templateJsonText);
            var (merged, addedKeys) = ConfigMerger.Merge(userConfig, templateConfig);
            File.WriteAllText(programDataConfigPath, merged.ToString(Formatting.Indented));

            if (addedKeys != null && addedKeys.Count > 0)
            {
                string mergeLogPath = Path.Combine(programDataDir, "merge.log");
                var lines = new List<string>
                {
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC - Config merged from template",
                    "Added keys (" + addedKeys.Count + "):"
                };
                foreach (var key in addedKeys)
                {
                    lines.Add("  + " + key);
                }
                File.WriteAllLines(mergeLogPath, lines);
                return "Merged template into ProgramData config, added " + addedKeys.Count + " keys: " + string.Join(", ", addedKeys);
            }

            return "Merged template into ProgramData config (no new keys)";
        }

        // -----------------------------------------------------------------------
        // DetectInstallType — D-39 fresh vs upgrade decision
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point. Inspects the standard `Installed` MSI property
        /// (set by WiX when a previous version is found) and writes `InstallType`
        /// = "fresh" or "upgrade". Always returns Success (informational only).
        /// </summary>
        [CustomAction]
        public static ActionResult DetectInstallType(Session session)
        {
            // The `Installed` property is set by MSI when an existing product is detected.
            // For fresh install it is empty; for upgrade it contains the existing ProductCode.
            string installed = session[InstalledProperty];
            if (!string.IsNullOrEmpty(installed))
            {
                session[InstallTypeProperty] = "upgrade";
                session.Log(LogPrefix + "Upgrade detected — existing installation at ProductCode=" + installed);
            }
            else
            {
                session[InstallTypeProperty] = "fresh";
                session.Log(LogPrefix + "Fresh install — no previous version found");
            }
            return ActionResult.Success;
        }

        // -----------------------------------------------------------------------
        // StopRunningService — graceful 30s stop on upgrade (D-30)
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point. Stops the running service before file replacement
        /// during upgrade. Uses sc.exe (graceful stop with 30s timeout).
        /// Failure is logged but does not block install (file copy will still work; the
        /// ServiceControl element on uninstall/reinstall handles final cleanup).
        /// </summary>
        [CustomAction]
        public static ActionResult StopRunningService(Session session)
        {
            session.Log(LogPrefix + "Stopping running FingerprintAgent service (30s timeout)...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "stop FingerprintAgent",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        session.Log(LogPrefix + "sc.exe could not be launched (not on PATH?)");
                        return ActionResult.Success;
                    }
                    if (!p.WaitForExit(30000))
                    {
                        session.Log(LogPrefix + "sc.exe stop timed out after 30s; continuing (files will still copy)");
                        try { p.Kill(); } catch { }
                    }
                    else
                    {
                        session.Log(LogPrefix + "sc.exe stop exited with code " + p.ExitCode);
                    }
                }
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log(LogPrefix + "StopRunningService failed: " + ex.Message + " (continuing)");
                return ActionResult.Success;
            }
        }

        // -----------------------------------------------------------------------
        // Probe types
        // -----------------------------------------------------------------------

        internal enum HealthProbeOutcome
        {
            Healthy,
            DegradedScannerMissing,
            Unhealthy,
            Timeout,
            ConnectionRefused
        }

        internal class HealthProbeResult
        {
            public HealthProbeOutcome Outcome { get; }
            public int? HttpStatus { get; }
            public string Body { get; }

            public HealthProbeResult(HealthProbeOutcome outcome, int? httpStatus, string body)
            {
                Outcome = outcome;
                HttpStatus = httpStatus;
                Body = body;
            }
        }
    }
}
