using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Adapters;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;
using FingerprintAgent.Service;

namespace FingerprintAgent
{
    internal class Program
    {
        static void Main(string[] args)
        {
            InstallGlobalExceptionHandlers();

            bool serviceMode = args.Contains("--service");
            bool consoleMode = args.Contains("--console") || Environment.UserInteractive;

            if (serviceMode || !consoleMode)
            {
                ServiceBase.Run(new FingerprintAgentService());
                return;
            }

            AgentLogger logger = null;
            try
            {
                var config = ConfigLoader.Load();
                logger = new AgentLogger(config.Logging);
                logger.Info(AgentLogger.GenerateCorrelationId(), "Console mode starting");

                var service = new FingerprintAgentService(logger);
                service.StartConsole();

                Console.WriteLine($"Service running on http://{config.Http.Host}:{config.Http.Port}/. Press Ctrl+C to stop.");

                var exitEvent = new ManualResetEvent(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("Shutdown requested...");
                    service.StopConsole();
                    try { ZkNativeHost.Close(); } catch { /* best-effort — double-Close benign */ }
                    exitEvent.Set();
                };

                // Wait indefinitely for Ctrl+C by default. Set FA_CONSOLE_TIMEOUT (seconds)
                // for CI smoke tests that need auto-shutdown; 0 or negative = infinite.
                int consoleTimeoutSec = 0;
                var envTimeout = Environment.GetEnvironmentVariable("FA_CONSOLE_TIMEOUT");
                if (!string.IsNullOrEmpty(envTimeout) && int.TryParse(envTimeout, out var t))
                    consoleTimeoutSec = t;

                TimeSpan timeout = consoleTimeoutSec > 0
                    ? TimeSpan.FromSeconds(consoleTimeoutSec)
                    : Timeout.InfiniteTimeSpan;

                if (!exitEvent.WaitOne(timeout))
                {
                    Console.WriteLine($"Shutdown timed out after {consoleTimeoutSec}s, forcing exit...");
                    try { service.StopConsole(); } catch (Exception ex) { Console.Error.WriteLine($"Cleanup error: {ex.Message}"); }
                    try { ZkNativeHost.Close(); } catch { /* best-effort */ }
                }

                Console.WriteLine("Service stopped.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: Failed to run service. {ex.Message}");
                Environment.Exit(1);
            }
        }

        // Crash forensics: unhandled exceptions previously died with only WER, never agent.log.
        static void InstallGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                WriteCrashEntry(e.ExceptionObject as Exception, e.IsTerminating);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                WriteCrashEntry(e.Exception, false);
                e.SetObserved();
            };
        }

        static void WriteCrashEntry(Exception ex, bool isTerminating)
        {
            if (ex == null) return;
            string entry = $"{DateTime.UtcNow:O} [CRASH] terminating={isTerminating} {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";
            try
            {
                EventLog.WriteEntry("FingerprintAgent", entry, EventLogEntryType.Error);
            }
            catch { /* source may be missing on dev boxes */ }
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "FingerprintAgent", "Logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), entry + Environment.NewLine);
            }
            catch { /* disk full / ACL — nothing left to do */ }
        }
    }
}