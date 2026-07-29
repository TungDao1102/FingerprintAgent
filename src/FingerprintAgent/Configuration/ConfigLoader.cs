using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace FingerprintAgent.Configuration
{
    public static class ConfigLoader
    {
        public static AgentConfig Load()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            return LoadFromDirectory(basePath);
        }

        public static AgentConfig LoadFromDirectory(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException(
                    $"Configuration directory not found: {directoryPath}");
            }

            string configPath = Path.Combine(directoryPath, "config.json");

            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException(
                    $"config.json not found at {configPath}. " +
                    "Please ensure config.json exists in the application directory.",
                    configPath);
            }

            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(directoryPath)
                    .AddJsonFile("config.json", optional: false, reloadOnChange: false)
                    .Build();

                return BindConfig(config);
            }
            catch (FormatException)
            {
                throw; // Rethrow FormatException from invalid JSON
            }
            catch (FileNotFoundException)
            {
                throw; // Rethrow FileNotFoundException from missing file
            }
            catch (Exception ex) when (
                ex.GetType().Name.IndexOf("Json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (ex.InnerException != null && ex.InnerException.GetType().Name.IndexOf("Json", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (ex.Message.IndexOf("JSON", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 ex.Message.IndexOf("parse", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                throw new FormatException(
                    $"config.json at {configPath} contains invalid JSON. " +
                    $"Please verify the file is valid JSON.",
                    ex);
            }
        }

        private static AgentConfig BindConfig(IConfigurationRoot configuration)
        {
            var config = new AgentConfig();

            // Service section
            config.Service.Name = GetString(configuration, "service:name") ?? config.Service.Name;
            config.Service.DisplayName = GetString(configuration, "service:displayName") ?? config.Service.DisplayName;
            config.Service.Description = GetString(configuration, "service:description") ?? config.Service.Description;

            // HTTP section
            config.Http.Host = GetString(configuration, "http:host") ?? config.Http.Host;
            config.Http.Port = GetInt(configuration, "http:port") ?? config.Http.Port;

            // CORS section
            config.Cors.Mode = GetString(configuration, "cors:mode") ?? config.Cors.Mode;
            config.Cors.AllowedOrigins = GetStringArray(configuration, "cors:allowedOrigins") ?? config.Cors.AllowedOrigins;

            // Scanner section
            config.Scanner.Priority = GetStringArray(configuration, "scanner:priority") ?? config.Scanner.Priority;
            config.Scanner.MockMode = GetBool(configuration, "scanner:mockMode") ?? config.Scanner.MockMode;

            // Logging section
            config.Logging.Level = GetString(configuration, "logging:level") ?? config.Logging.Level;
            config.Logging.File = GetString(configuration, "logging:file") ?? config.Logging.File;
            config.Logging.MaxSizeMb = GetInt(configuration, "logging:maxSizeMb") ?? config.Logging.MaxSizeMb;
            config.Logging.MaxFiles = GetInt(configuration, "logging:maxFiles") ?? config.Logging.MaxFiles;

            // Security section
            config.Security.BindIp = GetString(configuration, "security:bindIp") ?? config.Security.BindIp;

            return config;
        }

        private static string GetString(IConfiguration configuration, string key)
        {
            return configuration.GetSection(key)?.Value;
        }

        private static int? GetInt(IConfiguration configuration, string key)
        {
            string value = configuration.GetSection(key)?.Value;
            if (string.IsNullOrEmpty(value))
                return null;
            if (int.TryParse(value, out int result))
                return result;
            return null;
        }

        private static bool? GetBool(IConfiguration configuration, string key)
        {
            string value = configuration.GetSection(key)?.Value;
            if (string.IsNullOrEmpty(value))
                return null;
            if (bool.TryParse(value, out bool result))
                return result;
            return null;
        }

        private static string[] GetStringArray(IConfiguration configuration, string key)
        {
            var section = configuration.GetSection(key);
            if (!section.Exists())
                return null;

            try
            {
                return section.Get<string[]>();
            }
            catch
            {
                // If binding fails, fall back to manual enumeration
                var items = section.GetChildren();
                if (items == null)
                    return null;

                var list = new System.Collections.Generic.List<string>();
                foreach (var item in items)
                {
                    if (item.Value != null)
                        list.Add(item.Value);
                }
                return list.ToArray();
            }
        }
    }
}
