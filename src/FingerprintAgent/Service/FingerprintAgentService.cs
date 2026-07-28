using System.ServiceProcess;

namespace FingerprintAgent
{
    internal class FingerprintAgentService : ServiceBase
    {
        protected override void OnStart(string[] args)
        {
            // Service mode is implemented in Plan 03.
            // For now, use --console mode for development.
            base.OnStart(args);
        }

        protected override void OnStop()
        {
            base.OnStop();
        }
    }
}
