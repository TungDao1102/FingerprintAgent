using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;
using FingerprintAgent.Service;
using ZkTecoFingerPrint;

namespace FingerprintAgent
{
    internal class Program
    {
        static void Main(string[] args)
        {
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
                    ZkTecoFingerHost.Close();
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
                }

                Console.WriteLine("Service stopped.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: Failed to run service. {ex.Message}");
                Environment.Exit(1);
            }
        }
    }
}