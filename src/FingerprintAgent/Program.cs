using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;
using FingerprintAgent.Configuration;

namespace FingerprintAgent
{
    internal class Program
    {
        static void Main(string[] args)
        {
            bool consoleMode = Environment.UserInteractive || args.Contains("--console");

            AgentConfig config;
            try
            {
                config = ConfigLoader.Load();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: Failed to load configuration. {ex.Message}");
                Environment.Exit(1);
                return;
            }

            if (consoleMode)
            {
                RunAsConsole(config);
            }
            else
            {
                ServiceBase.Run(new FingerprintAgentService());
            }
        }

        private static void RunAsConsole(AgentConfig config)
        {
            var scanner = new MockScannerAdapter();
            var server = new HttpServer(config, scanner);
            server.Start();

            Console.WriteLine($"Service running on http://{config.Http.Host}:{config.Http.Port}/. Press Ctrl+C to stop.");

            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Shutdown requested...");
                exitEvent.Set();
            };

            exitEvent.WaitOne();

            server.Stop();
            Console.WriteLine("Service stopped.");
        }
    }
}
