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
using Record = WixToolset.Dtf.WindowsInstaller.Record;
using InstallMessage = WixToolset.Dtf.WindowsInstaller.InstallMessage;

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

        internal const string ServiceName = "FingerprintAgent";

        // CR-04: 30s timeout + multi-attempt retry absorbs cold-start latency (JIT, scanner
        // SDK load) on first install. Original 5s timeout caused false-positive rollbacks
        // when the HttpListener bound milliseconds-to-seconds after SCM reported Running.
        internal static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(30);
        internal const int HealthProbeMaxAttempts = 5;
        internal static readonly TimeSpan HealthProbeRetryDelay = TimeSpan.FromSeconds(3);

        // Standard MSI log message prefix used by all CustomActions for grep-friendly output.
        internal const string LogPrefix = "[FingerprintAgent.Installer] ";

        // D-39: standard MSI property populated by WiX when a previous version is detected.
        internal const string InstalledProperty = "Installed";

        // D-38: standard MSI property this CustomAction populates to drive success-dialog selection.
        internal const string InstallTypeProperty = "InstallType";

        // CR-07 follow-up: loc-driven Property-table entries (<Property Id="..."> in
        // FingerprintAgent.Installer.wxs) holding the Vietnamese notice text. The .wxl
        // stays the single text source; the CA only renders it.
        internal const string VcRedistErrorTitleProperty = "VcRedistErrorTitleText";
        internal const string VcRedistErrorBodyProperty = "VcRedistErrorBodyText";

        // Win32 message-box style for the notice: INSTALLMESSAGE_USER + MB_ICONWARNING
        // (0x30) + MB_SETFOREGROUND (0x10000); MB_OK (0x0) is the default when no button
        // bits are set. MsiProcessMessage ORs message-box flags into the message type.
        // MB_SETFOREGROUND has no DTF enum wrapper, hence the raw flag.
        private const int MbIconWarning = 0x00000030;
        private const int MbSetForeground = 0x00010000;
        internal static readonly InstallMessage VcRedistUserMessageType =
            (InstallMessage)((int)InstallMessage.User | MbIconWarning | MbSetForeground);

        // MsiLogFileLocation: standard MSI property holding the active log path when
        // logging is enabled (MsiLogging property or msiexec /l). Read by ArchiveInstallLog.
        internal const string MsiLogFileLocationProperty = "MsiLogFileLocation";

        // Public MSI property (FingerprintAgent.Installer.wxs) holding the ProgramData root
        // that already hosts the runtime agent.log — install logs consolidate next to it.
        internal const string ProgramDataFolderProperty = "PROGRAMDATAFOLDER";

        // CustomActionData keys fed to ArchiveInstallLogOnRollback by its Type-51 setter
        // (deferred CAs cannot read session properties directly).
        internal const string MsiLogSourceKey = "MsiLogSource";
        internal const string LogTargetDirKey = "LogTargetDir";

        // -----------------------------------------------------------------------
        // CheckVcRedist — VC++ x86 runtime detection (D-09/D-10/D-12)
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point. Probes registry for VC++ x86 runtime and LOGS a
        /// warning when it is missing — the install continues either way.
        ///
        /// TEMPORARY policy (2026-09-09), superseding the D-09 hard gate: the original
        /// "fail the install when missing" behavior assumed all four vendor SDKs link
        /// the VC++ CRT. An import-table audit of the SDKs actually shipped
        /// (lib/ZkTeco/libzkfp.dll, libzkfpcsharp.dll) found only kernel32/user32
        /// imports — pure Win32, no vcruntime140/msvcp140 dependency — so a machine
        /// lacking the x86 redist (e.g. an x64 box with only vc_redist.x64) was being
        /// blocked for nothing. The single x86 MSI now installs on both 32-bit and
        /// 64-bit Windows regardless of VC++ presence. A /MD-linked vendor SDK
        /// (possible for SecuGen/DigitalPersona/Futronic) surfaces at runtime as
        /// DllNotFoundException → SCANNER_NOT_CONNECTED + agent.log entry; the fix for
        /// operators stays the same (install vc_redist.x86.exe). If such a vendor
        /// ships, revisit a Burn bootstrapper bundling vc_redist.x86.
        /// Always returns Success
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

                session.Log(LogPrefix + "WARNING: VC++ x86 runtime NOT installed — install continues (warn-only policy)");
                session["VcRedistMissingDialog"] = "1";
                LogVcRedistWarning(session);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                // Fail-open: registry probe failed for permission reasons. Let install proceed.
                // The runtime will surface a clear error if VC++ really is missing.
                session.Log(LogPrefix + "VC++ detection failed (fail-open): " + ex.Message);
                return ActionResult.Success;
            }
        }

        // Warn-only since 2026-09-09: log replaces the Session.Message popup (rationale in CheckVcRedist doc).
        private static void LogVcRedistWarning(Session session)
        {
            string text = BuildVcRedistMessageText(
                session[VcRedistErrorTitleProperty],
                session[VcRedistErrorBodyProperty]);
            if (!string.IsNullOrEmpty(text))
            {
                session.Log(LogPrefix + "NOTICE: " + text);
            }
        }

        /// <summary>
        /// CR-07 follow-up: renders the Vietnamese missing-runtime notice through
        /// Session.Message (MsiProcessMessage). NOT called by CheckVcRedist since the
        /// 2026-09-09 warn-only policy — kept for the testable-core contract and a
        /// possible future re-enable (Burn / UI-sequence dialog).
        /// </summary>
        internal static void ShowVcRedistMissingNotice(Session session)
        {
            ShowVcRedistMissingNotice(
                session[VcRedistErrorTitleProperty],
                session[VcRedistErrorBodyProperty],
                message => session.Log(message),
                (type, record) => session.Message(type, record));
        }

        /// <summary>
        /// Testable core. The NOTICE log line is written unconditionally — silent /qn
        /// installs suppress the message box, so the MSI log is the only trace of the
        /// Vietnamese reason there.
        /// </summary>
        internal static void ShowVcRedistMissingNotice(
            string title,
            string body,
            Action<string> logSink,
            Action<InstallMessage, Record> messageSink)
        {
            string text = BuildVcRedistMessageText(title, body);
            if (string.IsNullOrEmpty(text))
            {
                logSink(LogPrefix + "VcRedistError text properties empty; skipping notice (install still fails)");
                return;
            }

            logSink(LogPrefix + "NOTICE: " + text);
            using (Record record = new Record(0))
            {
                record[0] = text;
                messageSink(VcRedistUserMessageType, record);
            }
        }

        /// <summary>
        /// Pure logic helper. Combines the localized title/body into message-box text and
        /// converts the MSI Text-control line-break markers ([BR]) to CRLF — inside a
        /// Session.Message formatted field, unconverted "[BR]" is parsed as a property
        /// reference and swallowed, losing the line structure.
        /// </summary>
        internal static string BuildVcRedistMessageText(string title, string body)
        {
            string normalizedBody = (body ?? string.Empty).Replace("[BR]", "\r\n");
            if (string.IsNullOrEmpty(title))
            {
                return normalizedBody;
            }

            if (string.IsNullOrEmpty(normalizedBody))
            {
                return title;
            }

            return title + "\r\n\r\n" + normalizedBody;
        }

        // -----------------------------------------------------------------------
        // ArchiveInstallLog — copy the active MSI log into ProgramData\Logs
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point. Copies the active MSI log (MsiLogFileLocation) into
        /// [PROGRAMDATAFOLDER]\Logs\install-yyyyMMdd-HHmmss.log so install diagnostics sit
        /// next to agent.log. Scheduled both IMMEDIATE after InstallFinalize (success path,
        /// reads session properties) and as Execute="rollback" (failure path, reads
        /// CustomActionData fed by SetArchiveInstallLogRollbackData — deferred CAs cannot
        /// read session properties directly). Always Success: archiving is best-effort
        /// support tooling and must never fail an install or a rollback.
        /// </summary>
        [CustomAction]
        public static ActionResult ArchiveInstallLog(Session session)
        {
            try
            {
                string sourceFile;
                string targetDirectory;
                if (session.CustomActionData != null && session.CustomActionData.Count > 0)
                {
                    sourceFile = session.CustomActionData.ContainsKey(MsiLogSourceKey)
                        ? session.CustomActionData[MsiLogSourceKey]
                        : null;
                    targetDirectory = session.CustomActionData.ContainsKey(LogTargetDirKey)
                        ? session.CustomActionData[LogTargetDirKey]
                        : null;
                }
                else
                {
                    sourceFile = session[MsiLogFileLocationProperty];
                    targetDirectory = Path.Combine(session[ProgramDataFolderProperty] ?? string.Empty, "Logs");
                }

                if (string.IsNullOrEmpty(targetDirectory))
                {
                    session.Log(LogPrefix + "Install log archive skipped (no target directory property)");
                    return ActionResult.Success;
                }

                CopyInstallLogFile(
                    sourceFile,
                    Path.Combine(targetDirectory, BuildInstallLogFileName(DateTime.Now)),
                    message => session.Log(message));
            }
            catch (Exception ex)
            {
                session.Log(LogPrefix + "Install log archive failed (best-effort): " + ex.Message);
            }

            return ActionResult.Success;
        }

        /// <summary>
        /// Pure logic helper. Local-time stamp so hospital IT can correlate the file name
        /// with wall-clock events.
        /// </summary>
        internal static string BuildInstallLogFileName(DateTime timestamp)
        {
            return "install-" + timestamp.ToString("yyyyMMdd-HHmmss") + ".log";
        }

        /// <summary>
        /// Pure-ish core (filesystem only). Copies the MSI log snapshot to targetFile,
        /// creating the target directory when missing. Snapshot semantics: MSI keeps
        /// writing the %TEMP% original after the copy. Every failure path logs via
        /// logSink and returns instead of throwing — archiving must never break an
        /// install or a rollback.
        /// </summary>
        internal static void CopyInstallLogFile(string sourceFile, string targetFile, Action<string> logSink)
        {
            if (string.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile))
            {
                logSink(LogPrefix + "Install log archive skipped (no active MSI log at '" + (sourceFile ?? "<null>") + "')");
                return;
            }

            try
            {
                string targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(sourceFile, targetFile, overwrite: true);
                logSink(LogPrefix + "Install log archived to " + targetFile);
            }
            catch (Exception ex)
            {
                logSink(LogPrefix + "Install log archive failed (best-effort): " + ex.Message);
            }
        }

        /// <summary>
        /// Pure logic helper. Probes both registry locations for VC++ x86 Installed=1.
        /// Returns true if found, plus the key path where it was found.
        /// Exposed as internal static so tests can exercise registry logic without a Session.
        /// </summary>
        internal static bool IsVcRedistInstalled(out string foundKey, Func<string, object> registryReader = null)
        {
            registryReader = registryReader ?? DefaultRegistryReader;
            foreach (var key in VcRedistRegistryKeys)
            {
                var installed = registryReader(key);
                if (installed != null && Convert.ToInt32(installed) == 1)
                {
                    foundKey = key;
                    return true;
                }
            }
            foundKey = null;
            return false;
        }

        private static object DefaultRegistryReader(string key)
        {
            using (var reg = Registry.LocalMachine.OpenSubKey(key))
            {
                return reg?.GetValue("Installed");
            }
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
        ///
        /// CR-04: Performs up to HealthProbeMaxAttempts probes with HealthProbeRetryDelay
        /// between attempts. A single transient failure (cold-start delay between SCM
        /// Running and HttpListener bind) no longer causes false-positive rollback.
        /// Returns the first non-connection-refused/non-timeout result, or the final
        /// outcome if all attempts fail.
        /// </summary>
        internal static HealthProbeResult ProbeHealth()
        {
            HealthProbeResult last = new HealthProbeResult(HealthProbeOutcome.Unhealthy, null, null);
            for (int attempt = 1; attempt <= HealthProbeMaxAttempts; attempt++)
            {
                last = ProbeHealthSingleAttempt();
                // ConnectionRefused/Timeout are the transient outcomes that retry can fix.
                // Anything else (Healthy, DegradedScannerMissing, Unhealthy with HTTP status)
                // is a definitive outcome — stop retrying.
                if (last.Outcome != HealthProbeOutcome.ConnectionRefused
                    && last.Outcome != HealthProbeOutcome.Timeout)
                {
                    return last;
                }
                if (attempt < HealthProbeMaxAttempts)
                {
                    System.Threading.Thread.Sleep(HealthProbeRetryDelay);
                }
            }
            return last;
        }

        private static HealthProbeResult ProbeHealthSingleAttempt()
        {
            using (var client = new HttpClient { Timeout = HealthProbeTimeout })
            {
                try
                {
                    var task = client.GetAsync(HealthUrl);
                    task.Wait(HealthProbeTimeout);
                    if (!task.IsCompleted)
                    {
                        return ClassifyHealthResponse(TimeoutOutcome(), null, null);
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
                    return ClassifyHealthResponse(status, body, null);
                }
                catch (AggregateException ex)
                {
                    return ClassifyHealthResponse(null, null, ex);
                }
                catch (HttpRequestException ex)
                {
                    return ClassifyHealthResponse(null, null, ex);
                }
                catch (Exception ex)
                {
                    return ClassifyHealthResponse(null, null, ex);
                }
            }
        }

        private static int TimeoutOutcome() => -1;

        /// <summary>
        /// Pure logic helper. Classifies an HTTP probe result into a HealthProbeOutcome.
        /// Exposed as internal static so tests can exercise the classifier without spinning
        /// up an HttpListener or depending on the hardcoded HealthUrl.
        /// </summary>
        /// <param name="status">HTTP status code, or -1 / null if the request did not complete.</param>
        /// <param name="body">Response body, or null if the request did not complete.</param>
        /// <param name="ex">Exception thrown by the HTTP call, or null on success.</param>
        internal static HealthProbeResult ClassifyHealthResponse(int? status, string body, Exception ex)
        {
            if (ex is AggregateException || ex is HttpRequestException)
            {
                return new HealthProbeResult(HealthProbeOutcome.ConnectionRefused, null, null);
            }
            if (ex != null)
            {
                return new HealthProbeResult(HealthProbeOutcome.Unhealthy, null, null);
            }
            if (!status.HasValue || status.Value < 0)
            {
                return new HealthProbeResult(HealthProbeOutcome.Timeout, null, null);
            }
            if (status >= 200 && status < 300)
            {
                return new HealthProbeResult(HealthProbeOutcome.Healthy, status.Value, body);
            }
            if (status == 503)
            {
                // 503 from HealthHandler when scanner is in max backoff (D-38).
                return new HealthProbeResult(HealthProbeOutcome.DegradedScannerMissing, status.Value, body);
            }
            return new HealthProbeResult(HealthProbeOutcome.Unhealthy, status.Value, body);
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
                string templateText = File.ReadAllText(installTemplatePath);
                AtomicFileWriter.WriteAllText(programDataConfigPath, templateText);
                return "Seeded ProgramData config from template (first install)";
            }

            // Upgrade: smart merge.
            string userJsonText = File.ReadAllText(programDataConfigPath);
            string templateJsonText = File.ReadAllText(installTemplatePath);
            var userConfig = JObject.Parse(userJsonText);
            var templateConfig = JObject.Parse(templateJsonText);
            var (merged, addedKeys) = ConfigMerger.Merge(userConfig, templateConfig);
            // CR-02: atomic write via shared AtomicFileWriter (linked from FingerprintAgent.Configuration).
            // A process crash mid-write would otherwise leave a partial config.json that bricks
            // the service on next boot.
            AtomicFileWriter.WriteAllText(programDataConfigPath, merged.ToString(Formatting.Indented));

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
                // WARN-03: use AppendAllLines for cumulative history across MSI upgrades.
                // ConfigLoader.WriteMergeLog uses AppendAllLines; this CA must match so
                // the log file is a single cumulative record (not wiped on each upgrade).
                File.AppendAllLines(mergeLogPath, lines);
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
                    Arguments = "stop " + ServiceName,
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

        /// <summary>
        /// WARN-10: Re-starts the service in the rollback path. On a failed upgrade,
        /// StopRunningService stopped the old service before InstallFiles. If the upgrade
        /// rolls back, files are restored to the prior version but the service stays
        /// stopped. This CA starts it back up so the operator sees a working service
        /// (with the OLD code) instead of "install succeeded but service won't start".
        ///
        /// Always returns Success — failure to start is logged but does not block the
        /// rollback (which has already restored files and is essentially complete).
        /// </summary>
        [CustomAction]
        public static ActionResult StartServiceAfterRollback(Session session)
        {
            session.Log(LogPrefix + "WARN-10: restart FingerprintAgent service after rollback...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "start " + ServiceName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        session.Log(LogPrefix + "StartServiceAfterRollback: sc.exe could not be launched");
                        return ActionResult.Success;
                    }
                    if (!p.WaitForExit(30000))
                    {
                        session.Log(LogPrefix + "StartServiceAfterRollback: sc.exe start timed out after 30s");
                        try { p.Kill(); } catch { }
                    }
                    else
                    {
                        session.Log(LogPrefix + "StartServiceAfterRollback: sc.exe start exited with code " + p.ExitCode);
                    }
                }
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log(LogPrefix + "StartServiceAfterRollback failed: " + ex.Message + " (continuing)");
                return ActionResult.Success;
            }
        }

        // -----------------------------------------------------------------------
        // SetDelayedAutoStart — D-32a delayed auto-start via CA (Error 1409 fix)
        // -----------------------------------------------------------------------

        /// <summary>
        /// CustomAction entry point (deferred, Impersonate=no). Flips the freshly created
        /// service from plain auto-start to delayed auto-start (~2 min after boot) via
        /// sc.exe config.
        ///
        /// WHY a CA instead of a RegistryValue: MSI must never author values inside the
        /// SCM-owned service key (SYSTEM\CurrentControlSet\Services\FingerprintAgent).
        /// On uninstall DeleteServices (seq 2000) destroys that key before
        /// RemoveRegistryValues (seq 2600) tries to remove the value → MSI Error 1409
        /// ("Could not read security information for key ... Verify that you have
        /// sufficient access to that key") → 1603 rollback. DeleteService deletes the
        /// whole key, so nothing needs cleanup on uninstall.
        ///
        /// Best-effort: always returns Success; failure only degrades to immediate
        /// auto-start (loudly logged in the MSI log). InstallServices must have run
        /// already (sequence: After="InstallServices", condition NOT REMOVE).
        /// </summary>
        [CustomAction]
        public static ActionResult SetDelayedAutoStart(Session session)
        {
            session.Log(LogPrefix + "Setting delayed auto-start for service " + ServiceName + "...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = BuildDelayedAutoStartArguments(ServiceName),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        session.Log(LogPrefix + "SetDelayedAutoStart: sc.exe could not be launched (not on PATH?)");
                        return ActionResult.Success;
                    }
                    if (!p.WaitForExit(30000))
                    {
                        session.Log(LogPrefix + "SetDelayedAutoStart: sc.exe config timed out after 30s; service keeps immediate auto-start");
                        try
                        {
                            p.Kill();
                        }
                        catch (Exception killEx)
                        {
                            session.Log(LogPrefix + "SetDelayedAutoStart: sc.exe Kill failed: " + killEx.Message);
                        }
                    }
                    else
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        if (p.ExitCode != 0)
                        {
                            session.Log(LogPrefix + "SetDelayedAutoStart: sc.exe config exited with code " + p.ExitCode
                                + "; service keeps immediate auto-start. Output: " + output);
                        }
                        else
                        {
                            session.Log(LogPrefix + "SetDelayedAutoStart: service start type set to delayed-auto");
                        }
                    }
                }
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log(LogPrefix + "SetDelayedAutoStart failed: " + ex.Message + " (continuing; service keeps immediate auto-start)");
                return ActionResult.Success;
            }
        }

        /// <summary>
        /// Pure logic helper. Builds the sc.exe argument string that flips a service from
        /// plain auto-start to "automatic (delayed start)". sc.exe requires a space after
        /// 'start=' — 'start=delayed-auto' is silently misparsed.
        /// Exposed as internal static so tests can verify the contract.
        /// </summary>
        internal static string BuildDelayedAutoStartArguments(string serviceName)
        {
            return "config " + serviceName + " start= delayed-auto";
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
