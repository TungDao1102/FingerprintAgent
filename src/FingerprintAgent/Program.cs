using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;
using FingerprintAgent.Service;

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

            AgentConfig config;
            AgentLogger logger = null;
            try
            {
                config = ConfigLoader.Load();
                logger = new AgentLogger(config.Logging);
                logger.Info(AgentLogger.GenerateCorrelationId(), "Console mode starting");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: Failed to load configuration. {ex.Message}");
                Environment.Exit(1);
                return;
            }

            var service = new FingerprintAgentService(logger);
            service.StartConsole();

            Console.WriteLine($"Service running on http://{config.Http.Host}:{config.Http.Port}/. Press Ctrl+C to stop.");

            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Shutdown requested...");
                service.StopConsole();
                exitEvent.Set();
            };

            if (!exitEvent.WaitOne(TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine("Shutdown timed out, forcing exit...");
            }

            Console.WriteLine("Service stopped.");
        }
    }
}
