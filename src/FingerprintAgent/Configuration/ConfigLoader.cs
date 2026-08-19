using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FingerprintAgent.Configuration
{
    public static class ConfigLoader
    {
        public static readonly string ProgramDataDirectory =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FingerprintAgent");

        public static readonly string ProgramDataConfigPath =
            Path.Combine(ProgramDataDirectory, "config.json");

        /// <summary>
        /// Default production entry point. Resolves paths via the constants above
        /// (ProgramDataConfigPath + install-dir config.template.json) and applies
        /// the smart-merge logic per D-33/D-34/D-35/D-36.
        /// </summary>
        public static AgentConfig Load()
        {
            string installDir = AppDomain.CurrentDomain.BaseDirectory;
            string templatePath = Path.Combine(installDir, "config.template.json");
            return Load(ProgramDataConfigPath, templatePath, installDir);
        }

        /// <summary>
        /// Test-friendly overload: lets callers specify ProgramData + template paths
        /// without touching the real %ProgramData% directory.
        /// </summary>
        public static AgentConfig Load(string programDataConfigPath, string templatePath, string installDir)
        {
            bool hasProgramData = File.Exists(programDataConfigPath);
            bool hasTemplate = File.Exists(templatePath);

            // Case 1: first install (no ProgramData config yet) — seed from template
            //         or copy legacy install-dir config.json if present.
            if (!hasProgramData && hasTemplate)
            {
                EnsureProgramDataDirectory(programDataConfigPath);

                string legacyConfigPath = Path.Combine(installDir, "config.json");
                if (File.Exists(legacyConfigPath))
                {
                    // Legacy v1.0 install: copy user-customized config to ProgramData
                    // so IT customizations survive the upgrade.
                    File.Copy(legacyConfigPath, programDataConfigPath);
                }
                else
                {
                    File.Copy(templatePath, programDataConfigPath);
                }

                return LoadFromFile(programDataConfigPath);
            }

            // Case 2: upgrade — smart merge template → ProgramData user config.
            if (hasProgramData && hasTemplate)
            {
                try
                {
                    var userJson = JObject.Parse(File.ReadAllText(programDataConfigPath));
                    var templateJson = JObject.Parse(File.ReadAllText(templatePath));
                    var (_, addedKeys) = ConfigMerger.Merge(userJson, templateJson);

                    if (addedKeys.Count > 0)
                    {
                        File.WriteAllText(
                            programDataConfigPath,
                            userJson.ToString(Formatting.Indented));

                        WriteMergeLog(programDataConfigPath, addedKeys);
                    }
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is JsonReaderException ||
                    ex is JsonException)
                {
                    // D-08 carryover: keep old config, don't crash. The exception
                    // bubbles only for unexpected types; for the expected ones we
                    // log via the caller's logger if present and proceed with the
                    // existing ProgramData config.
                    System.Diagnostics.Debug.WriteLine(
                        $"[FingerprintAgent] ConfigMerger skipped: {ex.Message}");
                }

                return LoadFromFile(programDataConfigPath);
            }

            // ProgramData present, no template — load as-is, no merge.
            if (hasProgramData)
            {
                return LoadFromFile(programDataConfigPath);
            }

            // Fallback: no ProgramData, no template — legacy install-dir config.
            return LoadFromDirectory(installDir);
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

            return LoadFromFile(configPath);
        }

        private static AgentConfig LoadFromFile(string configPath)
        {
            string directoryPath = Path.GetDirectoryName(configPath);

            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(directoryPath)
                    .AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: false)
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

        private static void EnsureProgramDataDirectory(string programDataConfigPath)
        {
            var dir = Path.GetDirectoryName(programDataConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private static void WriteMergeLog(string programDataConfigPath, IReadOnlyList<string> addedKeys)
        {
            var logPath = Path.Combine(
                Path.GetDirectoryName(programDataConfigPath) ?? ProgramDataDirectory,
                "merge.log");

            try
            {
                var lines = new List<string>
                {
                    $"{DateTime.UtcNow:O} Config merged from template → user config",
                    "Added:"
                };
                foreach (var key in addedKeys)
                {
                    lines.Add($"  + {key}");
                }
                File.AppendAllLines(logPath, lines);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Best-effort: don't fail the merge if we can't write the log
                System.Diagnostics.Debug.WriteLine(
                    $"[FingerprintAgent] merge.log write skipped: {ex.Message}");
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

            // Update section (Phase 4 D-13/D-14/D-15)
            config.Update.Enabled = GetBool(configuration, "update:enabled") ?? config.Update.Enabled;
            config.Update.GitHubOwner = GetString(configuration, "update:githubOwner") ?? config.Update.GitHubOwner;
            config.Update.GitHubRepo = GetString(configuration, "update:githubRepo") ?? config.Update.GitHubRepo;
            config.Update.CheckIntervalHours = GetInt(configuration, "update:checkIntervalHours") ?? config.Update.CheckIntervalHours;

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

                var list = new List<string>();
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
