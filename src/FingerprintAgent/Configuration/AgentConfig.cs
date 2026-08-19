namespace FingerprintAgent.Configuration
{
    public class AgentConfig
    {
        public ServiceConfig Service { get; set; } = new ServiceConfig();
        public HttpConfig Http { get; set; } = new HttpConfig();
        public CorsConfig Cors { get; set; } = new CorsConfig();
        public ScannerConfig Scanner { get; set; } = new ScannerConfig();
        public LoggingConfig Logging { get; set; } = new LoggingConfig();
        public SecurityConfig Security { get; set; } = new SecurityConfig();
        public UpdateConfig Update { get; set; } = new UpdateConfig();
    }

    public class ServiceConfig
    {
        public string Name { get; set; } = "FingerprintAgent";
        public string DisplayName { get; set; } = "Fingerprint Agent";
        public string Description { get; set; } = "Local fingerprint capture service";
    }

    public class HttpConfig
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 5043;
    }

    public class CorsConfig
    {
        public string Mode { get; set; } = "wildcard";
        public string[] AllowedOrigins { get; set; } = new string[0];
    }

    public class ScannerConfig
    {
        public string[] Priority { get; set; } = new[] { "ZKTeco", "SecuGen", "DigitalPersona", "Futronic" };
        public bool MockMode { get; set; } = true;
    }

    public class LoggingConfig
    {
        public string Level { get; set; } = "INFO";
        public string File { get; set; } = @"C:\ProgramData\FingerprintAgent\Logs\agent.log";
        public int MaxSizeMb { get; set; } = 10;
        public int MaxFiles { get; set; } = 5;
    }

    public class SecurityConfig
    {
        public string BindIp { get; set; } = "127.0.0.1";
    }

    public class UpdateConfig
    {
        public bool Enabled { get; set; } = false;
        public string GitHubOwner { get; set; } = "";
        public string GitHubRepo { get; set; } = "FingerprintAgent";
        public int CheckIntervalHours { get; set; } = 6;
    }
}
