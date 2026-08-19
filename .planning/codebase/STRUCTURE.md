# Codebase Structure

**Analysis Date:** 2026-08-19

## Directory Layout

```
FingerprintAgent/                          ← git root
├── .codegraph/                            ← codegraph knowledge-graph index (SQLite) — dev-only, gitignored
├── .omo/                                  ← opencode session/continuation state — dev-only, gitignored
├── .planning/                             ← GSD workflow artifacts
│   ├── codebase/                          ← this document + others (ARCHITECTURE.md, etc.) — single source of truth
│   ├── debug/                             ← gsd-debug state files
│   ├── phases/                            ← per-phase CONTEXT/PLAN/SUMMARY
│   ├── PROJECT.md                         ← project charter
│   ├── REQUIREMENTS.md
│   ├── ROADMAP.md
│   ├── STATE.md
│   └── config.json
├── lib/                                   ← vendor SDK native DLLs (committed only if downloaded via Setup-VendorSdk.ps1)
│   └── ZkTeco/
│       ├── libzkfp.dll
│       ├── libzkfpcsharp.dll
│       └── desktop.ini
├── scripts/                               ← PowerShell automation (install/service/smoke test)
│   ├── Install-Service.ps1
│   ├── Uninstall-Service.ps1
│   ├── Service.ps1
│   ├── Setup-VendorSdk.ps1
│   ├── Test-Capture.ps1
│   └── diagnostic/
│       ├── Test-ZK9500.ps1
│       └── Test-ZK9500-Timing.ps1
├── src/
│   ├── FingerprintAgent/                  ← MAIN LIBRARY (25 hand-written .cs files + config.json)
│   │   ├── Adapters/                      ← IScannerAdapter + 6 impls + CaptureResult + ScannerManager
│   │   ├── Api/                           ← HttpServer, CaptureHandler, HealthHandler, CorsMiddleware
│   │   ├── Configuration/                 ← AgentConfig, ConfigLoader, ConfigFileWatcher
│   │   ├── Logging/                       ← AgentLogger
│   │   ├── Models/                        ← CaptureRequest, CaptureResponse (DTOs)
│   │   ├── Service/                       ← FingerprintAgentService (ServiceBase)
│   │   ├── config.json                    ← template config (Copied to output on build)
│   │   ├── FingerprintAgent.csproj        ← net48 Library, x86, SDK conditional references
│   │   └── Program.cs                     ← DEAD entry point (do not edit as if it runs)
│   └── FingerprintAgent.Host/             ← ACTIVE Windows Service executable (ProjectReference to library)
│       ├── Properties/launchSettings.json
│       ├── FingerprintAgent.Host.csproj   ← net48 Exe, x86, references System.ServiceProcess
│       └── Program.cs                     ← ACTIVE entry: dual-mode (--service | --console)
├── tests/
│   └── FingerprintAgent.Tests/            ← single test project, xUnit 2.9.3 + Moq 4.20.72
│       ├── Api/                           ← HTTP-layer tests
│       │   ├── CorsMiddlewareTests.cs
│       │   ├── ErrorHandlingTests.cs
│       │   └── HttpServerIntegrationTests.cs
│       ├── Configuration/
│       │   └── ConfigLoaderTests.cs
│       ├── Logging/
│       │   └── AgentLoggerTests.cs
│       ├── Scanner/
│       │   ├── MockScannerAdapterTestDoubles.cs
│       │   ├── MockScannerAdapterTests.cs
│       │   ├── ScannerManagerProbeIntegrationTests.cs
│       │   ├── ScannerManagerTests.ExponentialBackoff.cs
│       │   ├── ZkSdkProbe.cs
│       │   └── ZKTecoDeviceIntegrationTests.cs
│       └── FingerprintAgent.Tests.csproj
├── .gitattributes
├── .gitignore
├── AGENTS.md                              ← root knowledge base for AI agents (read first)
├── FingerprintAgent.sln                   ← solution file (3 projects: library + host + tests)
└── SCANNER_SETUP.md                       ← vendor SDK setup guide
```

## Directory Purposes

**`src/FingerprintAgent/Adapters/`:**
- Purpose: Vendor SDK wrappers and the multi-vendor orchestrator
- Contains: 8 files — `IScannerAdapter.cs` (contract), `BaseScannerAdapter.cs` (template-method base), `MockScannerAdapter.cs` (dev-mode), `ZKTecoAdapter.cs`, `SecuGenAdapter.cs`, `DigitalPersonaAdapter.cs`, `FutronicAdapter.cs`, `ScannerManager.cs` (composite facade), `CaptureResult.cs` (result DTO with `Ok`/`Fail` factories)
- Key files: `ScannerManager.cs` (composite IScannerAdapter implementing priority fallback + backoff); `ZKTecoAdapter.cs` (only adapter with `#nullable enable` + static `_hostLock` singleton coordination)

**`src/FingerprintAgent/Api/`:**
- Purpose: HTTP transport — request loop, routing, response serialization, CORS
- Contains: 4 files — `HttpServer.cs` (HttpListener loop), `CorsMiddleware.cs` (preflight + headers with atomic HashSet swap), `CaptureHandler.cs` (`/api/capture`), `HealthHandler.cs` (`/health`)
- Key files: `HttpServer.cs:104-135` (`ProcessRequestLoop` long-running task), `CaptureHandler.cs:122-139` (`MapErrorCode` → HTTP status)

**`src/FingerprintAgent/Configuration/`:**
- Purpose: `config.json` loading + hot-reload
- Contains: 3 files — `AgentConfig.cs` (root POCO + 6 nested section types: ServiceConfig, HttpConfig, CorsConfig, ScannerConfig, LoggingConfig, SecurityConfig), `ConfigLoader.cs` (Microsoft.Extensions.Configuration.Json reader + manual bind), `ConfigFileWatcher.cs` (FileSystemWatcher + 300ms debounce + ConfigReloaded event)
- Key files: `ConfigFileWatcher.cs:43-45` (debounce Timer setup); `ConfigLoader.cs:33-60` (LoadFromDirectory)

**`src/FingerprintAgent/Logging/`:**
- Purpose: Structured logging (file + EventLog)
- Contains: 1 file — `AgentLogger.cs` (IDisposable; `LogLevel` enum; 4 methods `Debug`/`Info`/`Warn`/`Error`; static `GenerateCorrelationId`; SEC-04 base64 regex redaction)

**`src/FingerprintAgent/Models/`:**
- Purpose: Wire-format DTOs
- Contains: 2 files — `CaptureRequest.cs` (input), `CaptureResponse.cs` (output). Both use Newtonsoft.Json `[JsonProperty]` attributes to pin wire names (Vietnamese vocabulary)
- Key files: `CaptureRequest.cs:9-30` (the 5 business fields: `thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`, plus `metadata` dictionary)

**`src/FingerprintAgent/Service/`:**
- Purpose: Windows Service lifecycle and composition root
- Contains: 1 file — `FingerprintAgentService.cs` (inherits `System.ServiceProcess.ServiceBase`; owns ScannerManager + HttpServer + ConfigFileWatcher + health-check Timer + logger)

**`src/FingerprintAgent.Host/`:**
- Purpose: Process entry point executable
- Contains: `Program.cs` (dual-mode dispatch: `--service` → `ServiceBase.Run`, else interactive/console), `FingerprintAgent.Host.csproj` (net48 Exe + `System.ServiceProcess` reference), `Properties/launchSettings.json`

**`tests/FingerprintAgent.Tests/`:**
- Purpose: xUnit test suite (58 tests)
- Contains: 11 test files organized into 4 subfolders mirroring the library's layer boundaries (`Api/`, `Configuration/`, `Logging/`, `Scanner/`)
- Key files: `Scanner/MockScannerAdapterTestDoubles.cs` (test doubles: `MockScannerAdapterWithSettableProperties`, `CaptureHandlerTestFixture` — real HttpListener on random port)

**`scripts/`:**
- Purpose: PowerShell automation for service lifecycle
- Contains: 5 top-level scripts + 2 ZK9500 diagnostic scripts in `scripts/diagnostic/`. All scripts use `#Requires -Version` / `-RunAsAdministrator` where appropriate

**`lib/<Vendor>/`:**
- Purpose: Vendor native SDK DLLs (downloaded by `Setup-VendorSdk.ps1`)
- Contains at present: `lib/ZkTeco/{libzkfp.dll, libzkfpcsharp.dll}`. Empty placeholder directories for SecuGen, DigitalPersona, Futronic are NOT created — vendor DLLs must be added manually per `SCANNER_SETUP.md`
- Critical: `<PlatformTarget>x86</PlatformTarget>` and `<RuntimeIdentifier>win-x86</RuntimeIdentifier>` on the library; mixing x64 native DLLs will fail at runtime with `BadImageFormatException`

**`.planning/codebase/`:**
- Purpose: GSD-generated codebase mapping documents (this file + siblings)
- Contains: `ARCHITECTURE.md`, `STRUCTURE.md`, and other focus-area maps (STACK.md, INTEGRATIONS.md, CONVENTIONS.md, TESTING.md, CONCERNS.md). Read by `/gsd-plan-phase` and `/gsd-execute-phase` to ground plan generation

**`.codegraph/`:**
- Purpose: Codebase knowledge-graph index (SQLite) for the `codegraph_explore` MCP tool
- Contains: `codegraph.db` (≈2.6 MB), WAL/SHM journals, daemon log. Auto-regenerates on file changes; falls back to `Read`/`Grep` if absent

## Key File Locations

**Entry Points:**
- `src/FingerprintAgent.Host/Program.cs` — **active** executable entry (`Main(string[] args)`)
- `src/FingerprintAgent/Program.cs` — **dead** duplicate (library project emits Library, not Exe)
- `src/FingerprintAgent/Service/FingerprintAgentService.cs` — `OnStart`/`OnStop` lifecycle (called by either entry path)

**Configuration:**
- `src/FingerprintAgent/config.json` — template config (copied to output via `<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>`)
- `src/FingerprintAgent/Configuration/AgentConfig.cs` — typed config root + 6 section POCOs
- `src/FingerprintAgent/Configuration/ConfigLoader.cs` — reads config.json via `Microsoft.Extensions.Configuration.Json`
- `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs` — hot-reload trigger

**Core Logic:**
- `src/FingerprintAgent/Adapters/ScannerManager.cs` — multi-vendor orchestrator (the most architecturally important file)
- `src/FingerprintAgent/Adapters/IScannerAdapter.cs` — vendor-neutral contract
- `src/FingerprintAgent/Api/HttpServer.cs` — request loop + graceful shutdown
- `src/FingerprintAgent/Api/CaptureHandler.cs` — `/api/capture` request → response

**Vendor Adapters:**
- `src/FingerprintAgent/Adapters/MockScannerAdapter.cs` — deterministic 320×240 PNG (always builds; no SDK required)
- `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs` — only adapter with `#nullable enable` + static host teardown coordination
- `src/FingerprintAgent/Adapters/SecuGenAdapter.cs` — only adapter with stub types under `#if !SECUGEN_SDK_PRESENT` so the project compiles without the SDK
- `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs` — event-driven capture wrapped with `TaskCompletionSource` + 3s local timeout via linked CTS
- `src/FingerprintAgent/Adapters/FutronicAdapter.cs` — raw P/Invoke against x86 `ftrScanAPI.dll`; defines its own `ToPngGrayscale` (does NOT use `BaseScannerAdapter`) because of pre-PNG pixel inversion (D-07)

**Testing:**
- `tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTestDoubles.cs` — shared test doubles used across Scanner and Api test files
- `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs` — verifies `_backoffStep` increments to `{10,30,60,120}s`
- `tests/FingerprintAgent.Tests/Api/HttpServerIntegrationTests.cs` — real `HttpListener` round-trip
- `tests/FingerprintAgent.Tests/Scanner/ZkSdkProbe.cs` — runtime probe that skips when `libzkfp.dll` is missing

**Build:**
- `FingerprintAgent.sln` — 3 projects: `FingerprintAgent` (library, GUID `A17E0656...`), `FingerprintAgent.Host` (executable, GUID `C03DD2D2...`), `FingerprintAgent.Tests` (xUnit, GUID `8B58553E...`)
- `src/FingerprintAgent/FingerprintAgent.csproj` — `<InternalsVisibleTo>FingerprintAgent.Tests</InternalsVisibleTo>` (tests can see `internal` types)
- `src/FingerprintAgent.Host/FingerprintAgent.Host.csproj` — `<ProjectReference>` to the library + `System.ServiceProcess`

## Naming Conventions

**Files:**
- One type per file, filename matches type name (`ScannerManager.cs` → `class ScannerManager`)
- Tests mirror source folder layout: `tests/FingerprintAgent.Tests/Scanner/ScannerManager.cs` → `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.{Feature}.cs` (e.g. `.ExponentialBackoff.cs` partial-class style)
- Test doubles co-located with consumers: `MockScannerAdapterTestDoubles.cs` lives in the Scanner test folder (consumed by both Scanner and Api tests)

**Directories:**
- Layer-named subfolders: `Adapters/`, `Api/`, `Configuration/`, `Logging/`, `Models/`, `Service/`
- Namespace mirrors folder: `FingerprintAgent.<Layer>.<Type>` (e.g. `FingerprintAgent.Adapters.ScannerManager`)

**C# Identifiers:**
- PascalCase for types, methods, properties, public fields, constants
- `_camelCase` for private fields (with underscore prefix; e.g. `_config`, `_logger`, `_adapterLock`, `_backoffStep`)
- Static readonly arrays: PascalCase plural — `BackoffDelaysSeconds = { 10, 30, 60, 120 }`
- Interfaces prefixed `I` — `IScannerAdapter`
- Async methods suffixed `Async` — `ScanAsync`, `ConfigureAwait`, `TrySetAsync`, etc.
- Factory methods on DTOs: `CaptureResult.Ok(...)`, `CaptureResult.Fail(...)`

**Namespaces:**
- Root: `FingerprintAgent`
- Sub-namespace per layer: `FingerprintAgent.Adapters`, `FingerprintAgent.Api`, `FingerprintAgent.Configuration`, `FingerprintAgent.Logging`, `FingerprintAgent.Models`, `FingerprintAgent.Service`
- Test root: `FingerprintAgent.Tests.<Layer>` (e.g. `FingerprintAgent.Tests.Scanner`)

**Config keys:**
- Lowercase section names: `service`, `http`, `cors`, `scanner`, `logging`, `security`
- Lowercase field names: `mockMode`, `allowedOrigins`, `maxSizeMb`, `bindIp`
- ConfigLoader uses colon-separated keys: `configuration.GetSection("http:host")`

**Wire DTO fields (capture endpoint):**
- Vietnamese business vocabulary — `thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`
- camelCase per Newtonsoft.Json convention
- Pinned via `[JsonProperty("thamChieuId")]` — do NOT rename the C# property without updating the attribute (or wire compatibility breaks)

**Test naming:**
- Descriptive English: `BackoffStep_StartsAtZero`, `CaptureHandler_Returns503_WhenScannerReturnsScannerNotConnected`, `Cors_AppliesWildcardWhen_ModeIsWildcard`
- Pattern: `<UnitOfWork>_<ExpectedOutcome>_<Condition>` (xUnit `[Fact]` style; no `MethodName_StateUnderTest_ExpectedBehavior` underscore pattern enforced by attribute — descriptive English is the convention)

## Where to Add New Code

**New HTTP endpoint (e.g. `POST /api/cancel`, `GET /api/devices`):**
- Handler: `src/FingerprintAgent/Api/<Name>Handler.cs` (mirror `CaptureHandler` shape — `public async Task HandleAsync(HttpListenerContext, IScannerAdapter, string correlationId)`)
- Route: `src/FingerprintAgent/Api/HttpServer.cs:169-186` — add a branch in `HandleRequestAsync`
- DTO: `src/FingerprintAgent/Models/<Name>Request.cs` and `<Name>Response.cs`
- Tests: `tests/FingerprintAgent.Tests/Api/<Name>HandlerTests.cs` (use `CaptureHandlerTestFixture` for real `HttpListener` integration; otherwise mock `HttpListenerContext`)

**New vendor adapter (e.g. `IntegratedBiometricsAdapter`):**
- Implementation: `src/FingerprintAgent/Adapters/<Vendor>Adapter.cs` (implement `IScannerAdapter` directly OR extend `BaseScannerAdapter` if your raw output is 8bpp grayscale; mirror `SecuGenAdapter` if you have a managed SDK; mirror `FutronicAdapter` for raw P/Invoke)
- Conditional SDK presence: add `<DefineConstants>` and `<Reference>` blocks in `src/FingerprintAgent/FingerprintAgent.csproj` following the existing 4-vendor pattern
- Vendor registration: `src/FingerprintAgent/Adapters/ScannerManager.cs:257-274` — add a `case "<VendorName>": return new <Vendor>Adapter();` in `CreateAdapter`. Unknown vendor names throw `InvalidOperationException` (fail-fast on typos, per T-02-09)
- Default priority: `src/FingerprintAgent/Configuration/AgentConfig.cs:32-36` (`ScannerConfig.Priority`)
- Stub types for SDK-absent compilation: if your SDK has interop structs, add a `#if !<VENDOR>_SDK_PRESENT` stub block at the top of the adapter file (see `SecuGenAdapter.cs:5-27` for the pattern)
- Tests: `tests/FingerprintAgent.Tests/Scanner/<Vendor>AdapterTests.cs` (use `MockScannerAdapterWithSettableProperties` if you need to test `ScannerManager` integration)

**New configuration section (e.g. `telemetry`):**
- POCO: `src/FingerprintAgent/Configuration/AgentConfig.cs` — add `public TelemetryConfig Telemetry { get; set; } = new TelemetryConfig();`
- Bind: `src/FingerprintAgent/Configuration/ConfigLoader.cs:63-94` — add bind calls in `BindConfig`
- Default values: set on the POCO property initializers (never in the loader — loader uses `?? config.X.Y` to preserve defaults)
- Template: add a `telemetry: { ... }` block in `src/FingerprintAgent/config.json`
- Hot-reload decision: if the section is safe to reload at runtime, add validation in `ConfigFileWatcher.OnDebounceElapsed:64-69` (currently only `Scanner` and `Cors` pass). If not reloadable, leave it out of the validation check.

**New logging field (e.g. add `Fatal` level):**
- `src/FingerprintAgent/Logging/AgentLogger.cs:10-16` — add enum value
- `src/FingerprintAgent/Logging/AgentLogger.cs:130-146` — add parser case
- Mirror the level in `ToEventLogEntryType` if you want it to map to a non-Information EventLog type

**New error code (e.g. `SCANNER_LOCKED`):**
- Decide HTTP status mapping: `src/FingerprintAgent/Api/CaptureHandler.cs:122-139` (`MapErrorCode`) — add a `case`
- Decide what adapter returns: `CaptureResult.Fail("SCANNER_LOCKED", message)` from the vendor adapter that detects this condition
- Document the new code in the response DTO if needed: `src/FingerprintAgent/Models/CaptureResponse.cs` (currently no enum, just strings)

**New adapter feature (e.g. async cancel-mid-capture):**
- `src/FingerprintAgent/Adapters/IScannerAdapter.cs:42` — the `ScanAsync` signature already takes `CancellationToken`; honor it at SDK checkpoints (do NOT interrupt mid-native-call — corrupts native state, per the XML doc)
- `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs:35-81` — base implementation already checks `cancellationToken.IsCancellationRequested` at start; vendors extend it
- `ScannerManager` does NOT enforce per-adapter timeouts — the 20s `totalCts.CancelAfter` is the only centralized budget

**New health-check metric (e.g. `lastCaptureAt`):**
- Track on `ScannerManager` under a new lock (or extend `_backoffLock` — it's only mutated on capture success/failure)
- Surface in `src/FingerprintAgent/Api/HealthHandler.cs:49-58` (anonymous response object) and add to `ScannerManager` via a new property

## Special Directories

**`.codegraph/`:**
- Purpose: Codebase knowledge-graph index (SQLite) for the `codegraph_explore` MCP tool
- Generated: Yes (CodeGraph daemon watches files)
- Committed: No (gitignored; per `.gitignore` patterns)

**`.omo/`:**
- Purpose: opencode session/continuation state (Boulder/Stop-hook artifacts)
- Generated: Yes
- Committed: No

**`.planning/`:**
- Purpose: GSD workflow artifacts (phases, codebase maps, debug state)
- Generated: Mixed — `codebase/` and `phases/` are committed; `debug/` is ephemeral
- Committed: Partially — `codebase/` and top-level PROJECT/REQUIREMENTS/ROADMAP/STATE files are committed; `phases/<phase>/SUMMARY.md` is committed per phase close; `debug/` should not be

**`bin/` and `obj/`:**
- Purpose: Build output and intermediate artifacts
- Generated: Yes (dotnet build/test)
- Committed: No (gitignored)

**`TestResults/`:**
- Purpose: xUnit test runner output
- Generated: Yes (`dotnet test`)
- Committed: No

**`lib/<Vendor>/`:**
- Purpose: Vendor native SDK DLLs
- Generated: No (downloaded manually or via `scripts/Setup-VendorSdk.ps1`)
- Committed: Only what you've explicitly added — currently `lib/ZkTeco/` is populated (the project has been used with ZK9500 hardware); `lib/SecuGen/`, `lib/DigitalPersona/`, `lib/Futronic/` are empty (operators must drop the DLLs in per `SCANNER_SETUP.md`)

---

*Structure analysis: 2026-08-19*
