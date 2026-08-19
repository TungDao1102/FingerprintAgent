# Technology Stack

**Analysis Date:** 2026-08-19

## Languages

**Primary:**
- C# 8.0 (`<LangVersion>8.0</LangVersion>` in `src/FingerprintAgent/FingerprintAgent.csproj:4`) — used for all 30 .cs source files across the library, host, and tests
- All code targets the .NET Framework 4.8 BCL; nullable reference types are **not** enabled project-wide

**Secondary:**
- PowerShell 5+ — used for deployment/ops scripts under `scripts/`
- JSON — config schema (`src/FingerprintAgent/config.json`) and HTTP request/response wire format

## Runtime

**Environment:**
- .NET Framework 4.8 (`net48`) — `src/FingerprintAgent/FingerprintAgent.csproj:3`, `src/FingerprintAgent.Host/FingerprintAgent.Host.csproj:3`, `tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj:3`
- Framework-dependent deployment (no self-contained publish; relies on .NET Framework 4.8 being installed on the host)
- Windows-only — uses `System.ServiceProcess`, `System.Drawing`, `System.Net.HttpListener`, `System.Diagnostics.EventLog`, `FileSystemWatcher`

**Platform Architecture:**
- **x86 only** (`<PlatformTarget>x86</PlatformTarget>` + `<RuntimeIdentifier>win-x86</RuntimeIdentifier>` in `src/FingerprintAgent/FingerprintAgent.csproj:9-10`) — non-negotiable because every vendor SDK DLL is 32-bit (SecuGen, DigitalPersona, Futronic, ZKTeco)
- Solution config also maps `x86` → `Any CPU` (`FingerprintAgent.sln` ProjectConfigurationPlatforms); `x64` configurations exist as inactive entries

**Package Manager:**
- NuGet via PackageReference (SDK-style csproj)
- Lockfiles present: `src/FingerprintAgent/obj/project.assets.json`, `src/FingerprintAgent.Host/obj/project.assets.json`, `tests/FingerprintAgent.Tests/obj/project.assets.json`
- No `packages.config` (legacy format not used)

## Frameworks

**Core:**
- `System.ServiceProcess.ServiceBase` — base class for `FingerprintAgentService` (`src/FingerprintAgent/Service/FingerprintAgentService.cs:15`); provides Windows Service `OnStart`/`OnStop` lifecycle
- `System.Net.HttpListener` — raw HTTP server in `src/FingerprintAgent/Api/HttpServer.cs:15` (no ASP.NET, no Kestrel)
- `System.Drawing` — GDI+ Bitmap for PNG encoding from 8-bit grayscale raw sensor buffers (`Adapters/BaseScannerAdapter.cs:88-110`)
- `System.Diagnostics.EventLog` — secondary log sink, writes to source `"FingerprintAgent"` (`Logging/AgentLogger.cs:162`)

**Testing:**
- xUnit 2.9.3 — `tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj:11`
- xunit.runner.visualstudio 2.8.2 — Visual Studio test adapter
- Microsoft.NET.Test.Sdk 17.13.0 — `dotnet test` host
- Moq 4.20.72 — mocking framework (`FingerprintAgent.Tests.csproj:17`)

**Build/Dev:**
- dotnet CLI (SDK-style csproj — `Microsoft.NET.Sdk`)
- Visual Studio 17 (`FingerprintAgent.sln` VisualStudioVersion = 17.0.31903.59)
- No MSBuild custom targets, no Cake/Fake/psake build scripts; PowerShell scripts handle install/service-control

## Key Dependencies

**Critical (NuGet, from `src/FingerprintAgent/FingerprintAgent.csproj`):**
- `Newtonsoft.Json` 13.0.3 — JSON request/response serialization in `Api/CaptureHandler.cs:9`, `Api/HealthHandler.cs:7`
- `Microsoft.Extensions.Configuration.Json` 8.0.0 — config.json binding root in `Configuration/ConfigLoader.cs:35`
- `Microsoft.Extensions.Configuration.Binder` 8.0.2 — POCO binding into `AgentConfig`
- `Microsoft.Extensions.DependencyInjection` 8.0.1 + `Abstractions` 8.0.2 — **declared but unused**; project uses direct `new` (AGENTS.md "Known Issues / Anti-Patterns" row)

**Vendor SDKs (NuGet):**
- `ZkTecoFingerPrint` 1.2.1 — ZKTeco wrapper (`Adapters/ZKTecoAdapter.cs:11` `using ZkTecoFingerPrint;`); pinned to exact version per supply-chain note in csproj
- `DPUruNet` 1.0.0.1 — DigitalPersona wrapper (`Adapters/DigitalPersonaAdapter.cs:11-12` `using DPFP;` / `using DPFP.Capture;`)

**Vendor SDKs (native DLLs, x86):**
| Vendor | Native DLL | Adapter file | Integration style |
|---|---|---|---|
| ZKTeco | `libzkfp.dll`, `libzkfpcsharp.dll` (typically from `C:\Windows\SysWOW64\`) | `Adapters/ZKTecoAdapter.cs` | via `ZkTecoFingerPrint` NuGet wrapper |
| DigitalPersona | `dpfpdd.dll`, `dpfj.dll` (native) + `DPFPDevNET.dll`, `DPFPCapture.dll` (managed) | `Adapters/DigitalPersonaAdapter.cs` | via `DPUruNet` NuGet wrapper |
| SecuGen | `sgfplib.dll`, `sgfpamx.dll` (native) + `SecuGen.FDxSDKPro.Windows.dll` (managed) | `Adapters/SecuGenAdapter.cs` | direct Reference (HintPath to `lib\SecuGen\`) |
| Futronic | `ftrScanAPI.dll` (x86, native) | `Adapters/FutronicAdapter.cs` | direct P/Invoke `DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)` |

Conditional compilation symbols gate each adapter:
- `ZKTECO_SDK_PRESENT`, `SECUGEN_SDK_PRESENT`, `DIGITALPERSONA_SDK_PRESENT`, `FUTRONIC_SDK_PRESENT` (`FingerprintAgent.csproj:17-31`)
- Each symbol auto-defined only when the corresponding DLL is detected at `lib\<Vendor>\...` — stubs allow compilation on machines without hardware

## Configuration

**Sources:**
- `config.json` — single source, copied to output on build (`src/FingerprintAgent/FingerprintAgent.csproj:67-69` `<None Update="config.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>`)
- Loaded at startup via `ConfigLoader.Load()` from `AppDomain.CurrentDomain.BaseDirectory` (`Configuration/ConfigLoader.cs:11-13`)
- Live reload via `FileSystemWatcher` + 300ms debounce timer (`Configuration/ConfigFileWatcher.cs:43`) → only `ScannerConfig` and `CorsConfig` sections are reloaded at runtime

**Schema (`src/FingerprintAgent/config.json`):**
```json
{
  "service":  { "name", "displayName", "description" },
  "http":     { "host": "127.0.0.1", "port": 5043 },
  "cors":     { "mode": "wildcard"|"allowlist", "allowedOrigins": [] },
  "scanner":  { "priority": ["ZKTeco", "SecuGen", "Futronic", "DigitalPersona"], "mockMode": false },
  "logging":  { "level": "INFO", "file": "C:\\ProgramData\\FingerprintAgent\\Logs\\agent.log", "maxSizeMb": 10, "maxFiles": 5 },
  "security": { "bindIp": "127.0.0.1" }
}
```

**Binding:**
- `Microsoft.Extensions.Configuration` + `ConfigurationBinder` for POCO mapping (`Configuration/ConfigLoader.cs:35`)
- Hand-rolled typed accessors (`GetString`, `GetInt`, `GetBool`, `GetStringArray`) instead of generic `Bind` — likely to control null/default behavior

**Environment Variables:**
- `FA_CONSOLE_TIMEOUT` (seconds) — only consumed by `src/FingerprintAgent.Host/Program.cs:50` to auto-shutdown the console host during CI smoke tests

**Build Configuration:**
- No `.editorconfig`, no `Directory.Build.props`, no `Directory.Packages.props` (central package management not used)
- No `stylecop.json` / `GlobalSuppressions.cs`
- Two pre-existing `xUnit1031` warnings in test code per AGENTS.md (Release build: 0 warnings, 0 errors)

## Platform Requirements

**Development:**
- Windows 10/11 (or Windows 7 SP1 with .NET Framework 4.8 installed — per AGENTS.md "Key Constraints")
- Visual Studio 2022 or `dotnet` SDK 8.x+ (SDK-style projects; net48 needs reference assemblies)
- x86 vendor SDKs in `lib\<Vendor>\` for full adapter activation; otherwise only stubs compile
- PowerShell 5+ for deployment scripts

**Production:**
- Windows 10/11 (32-bit or 64-bit OS, but agent process is **always x86**)
- .NET Framework 4.8 installed
- USB port for fingerprint hardware
- Vendor DLLs placed next to `FingerprintAgent.exe` per D-08 (or installed at standard locations)
- Writes to: `C:\ProgramData\FingerprintAgent\Logs\` (log directory created on demand by `AgentLogger.cs:39-43`)
- Listens on: `127.0.0.1:5043` only (loopback; no external network binding)
- Registered as Windows Service via `sc.exe` (`scripts/Install-Service.ps1`)
- MSI installer noted in AGENTS.md as **not yet implemented**; current install is via PowerShell

## Version Pinning Notes

All dependencies use **exact-version pinning** (no floating versions) — AGENTS.md and csproj comments emphasize supply-chain safety:
- `DPUruNet` 1.0.0.1 (comment: "Pin to 1.0.0.1 (offline cache)")
- `ZkTecoFingerPrint` 1.2.1 (comment: "Pin to EXACT version 1.2.1 — do not use floating version")
- `Microsoft.Extensions.*` packages pinned to specific 8.0.x versions

---

*Stack analysis: 2026-08-19*
