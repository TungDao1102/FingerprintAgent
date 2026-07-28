using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using FingerprintAgent.Adapters;
using FingerprintAgent.Api;

namespace FingerprintAgent
{
    internal class Program
    {
        static void Main(string[] args)
        {
            bool consoleMode = Environment.UserInteractive || args.Contains("--console");

            if (consoleMode)
            {
                RunAsConsole();
            }
            else
            {
                ServiceBase.Run(new FingerprintAgentService());
            }
        }

        private static void RunAsConsole()
        {
            var scanner = new MockScannerAdapter();
            var server = new HttpServer("127.0.0.1", 5043, scanner);
            server.Start();

            Console.WriteLine("Service running. Press Ctrl+C to stop.");

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

