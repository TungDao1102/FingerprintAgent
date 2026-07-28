# Phase 01: Pattern Map — Foundation (Windows Service + HTTP API)

**Generated:** 2026-07-28
**Status:** Greenfield — no existing codebase analogs.

---

## Legend

| Column     | Meaning |
|------------|---------|
| Role       | Architectural role (entry, host, api, config, logging, adapter, model, test, deploy) |
| Lifecycle  | When in the process this file is read/created/used |
| Data Flow  | Direction of data: in (→), out (←), bidirectional (↔), internal (⤷) |
| Conventions| Relevant D-NN decisions from CONTEXT.md + derived rules |
| Analog     | Closest existing pattern in this repo (none — greenfield) |

---

## File Pattern Map

### 1. `FingerprintAgent.sln`

| Property     | Value |
|--------------|-------|
| **Role**     | `entry` — Solution orchestration |
| **Lifecycle**| Created once; updated when projects added (Phase 2+ for test project) |
| **Data Flow**| ⤷ References `.csproj` projects |
| **Conventions**| D-08: single project for Phase 1. Test project included as separate `.csproj` within the same solution. |
| **Analog**   | None (greenfield). Standard .NET solution pattern. |
| **Notes**    | Use Visual Studio 2022 solution format. Include `FingerprintAgent.csproj` and `FingerprintAgent.Tests.csproj`. |

---

### 2. `FingerprintAgent/FingerprintAgent.csproj`

| Property     | Value |
|--------------|-------|
| **Role**     | `host` — Build + package definition |
| **Lifecycle**| Created once; dependencies evolve per phase |
| **Data Flow**| ⤷ Declares NuGet packages → resolved at build time |
| **Conventions**| D-08, D-10: single project, ME.DI + ME.Config.Json via NuGet. D-03: System.Drawing (in-box, no NuGet). Target `net48` (or `v4.8` in classic format). |
| **Analog**   | None (greenfield). |
| **Packages** | `Microsoft.Extensions.DependencyInjection` 8.0.1, `Configuration.Json` 8.0.0, `Configuration.Binder` 8.0.2, `Newtonsoft.Json` 13.0.3 |
| **Notes**    | OutputType = Exe (Windows Service executable). Use SDK-style `<Project Sdk="Microsoft.NET.Sdk">` with `<TargetFramework>net48</TargetFramework>` for cleaner package references. |

---

### 3. `FingerprintAgent/Program.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `entry` — Application entry point, dual-mode dispatcher |
| **Lifecycle**| Called by OS at process start (console or SCM) |
| **Data Flow**| → Parses `args` → decides console vs service mode → instantiates `FingerprintAgentService` → calls `service.Start()` or `ServiceBase.Run()` |
| **Conventions**| D-06: pure `ServiceBase`, no Topshelf. `--service` / `--console` arg switching. `Environment.UserInteractive` detection. |
| **Analog**   | None (greenfield). Pattern borrowed from .NET Framework `ServiceBase` documentation. |
| **Pitfalls** | OnStart 30s timeout — Main doesn't handle it here, but the Service does. Ensure service.Start() is non-blocking. Ctrl+C handler for console mode must call `service.Stop()` cleanly. |

---

### 4. `FingerprintAgent/Service/FingerprintAgentService.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `host` — Windows Service lifecycle controller |
| **Lifecycle**| Instantiated by `Program.cs` → `OnStart` / `OnStop` called by SCM |
| **Data Flow**| ↔ `HttpServer` (start/stop listener) ↔ `AgentLogger` ↔ `AgentConfig` |
| **Conventions**| D-06: `ServiceBase` subclass. D-07: PowerShell scripts for install, MSI in Phase 4. SVC-03: OnStart returns within 30s (background thread for listener). |
| **Analog**   | None (greenfield). |
| **State**    | Holds `CancellationTokenSource`, `HttpServer`, `AgentLogger` as fields. |
| **Lifecycle**| `OnStart(...)` → load config → init logger → start HTTP listener. `OnStop(...)` → cancel token → stop listener → flush log. |
| **Pitfalls** | OnStart must NOT block. OnStop: `HttpListener.Stop()` unblocks `GetContext()`. AutoLog = true for basic SCM logging. |

---

### 5. `FingerprintAgent/Api/HttpServer.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `api` — HTTP listener wrapper, request dispatch engine |
| **Lifecycle**| Created in `FingerprintAgentService.OnStart()` → runs until `OnStop()` |
| **Data Flow**| ← HTTP request (in) → routes to handler → HTTP response (out) |
| **Conventions**| D-01: `System.Net.HttpListener`. D-02: bind `127.0.0.1:5043` default, configurable. D-12/D-13: CORS applied per-request. |
| **Analog**   | None (greenfield). Pattern inspired by raw `HttpListener` async loop from MSDN docs. |
| **Structure**| `Start(host, port)` → add prefix → `Start()` → async loop on `GetContextAsync()`. `Stop()` → `listener.Stop()` → `listener.Close()`. In-flight requests are dispatched via `Task.Run(...)`. |
| **Pitfalls** | Trailing slash required in prefix. 30s OnStart window. `GetContextAsync()` throws `ObjectDisposedException` when listener stops — must catch as stop signal. |

---

### 6. `FingerprintAgent/Api/HealthHandler.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `api` — `GET /health` endpoint |
| **Lifecycle**| Called per health-check request |
| **Data Flow**| → Reads scanner status → returns JSON ← |
| **Conventions**| API-06, OBS-03: returns `{"status":"healthy","deviceId":"mock-scanner-001","uptime":"00:05:12"}`. HTTP 200 when healthy, 503 when disconnected (structured for Phase 3 — mock is always healthy). |
| **Analog**   | None (greenfield). |
| **Dependencies**| `IScannerAdapter` (for `DeviceId`, `IsConnected`), `AgentLogger` |
| **Response** | `{"status": "healthy"|"degraded", "deviceId": string, "uptime": string}` |

---

### 7. `FingerprintAgent/Api/CaptureHandler.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `api` — `POST /api/capture` endpoint |
| **Lifecycle**| Called per capture request |
| **Data Flow**| ← JSON body (CaptureRequest) → validate → `IScannerAdapter.Scan()` → JSON response (CaptureResponse) → |
| **Conventions**| D-14: request has `thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`, `metadata`. D-15: response with `isSuccess`, `imageBytes` (base64), `verificationData` (SHA-256 base64). API-04: error codes `INVALID_REQUEST`, `SCANNER_NOT_CONNECTED`. |
| **Analog**   | None (greenfield). |
| **Validation** | Required fields → 400. Valid JSON parse → else 400. Malformed body → 400. Scanner not connected → 503 (structured for Phase 3). |
| **Flow**     | 1. Read body stream → string 2. Deserialize with Newtonsoft.Json 3. Validate required fields 4. Call `scanner.Scan()` 5. Build response DTO 6. Serialize → write to response stream |

---

### 8. `FingerprintAgent/Api/CorsMiddleware.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `api` — CORS header injection |
| **Lifecycle**| Called per-request (preflight + actual) |
| **Data Flow**| ← Reads `Origin` header → validates against mode → injects response headers → |
| **Conventions**| D-12: default wildcard `*`. D-13: `cors.mode = wildcard|allowlist`. API-05: OPTIONS preflight returns 204 + CORS headers. |
| **Analog**   | None (greenfield). Pattern inspired by ASP.NET CORS middleware but implemented raw. |
| **Methods**  | `HandleCorsPreflight(request, response)` → returns bool (true if preflight handled). `ApplyCorsHeaders(response, origin)` → sets headers on actual requests. |
| **Preflight**| OPTIONS + Origin → 204 with `Access-Control-Allow-Methods`, `Allow-Headers`, `Max-Age`. |
| **Allowlist**| If mode=allowlist and origin not in set → 403 Forbidden. |

---

### 9. `FingerprintAgent/Configuration/AgentConfig.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `config` — Strongly-typed configuration model |
| **Lifecycle**| Deserialized once at startup from `config.json` |
| **Data Flow**| ⤷ `ConfigurationBuilder` → `IConfiguration` → `IConfiguration.Get<AgentConfig>()` |
| **Conventions**| D-10: `Microsoft.Extensions.Configuration.Json`. CFG-02: covers service, http, cors, scanner, logging, security sections. |
| **Analog**   | None (greenfield). |
| **Sections** | `ServiceConfig`, `HttpConfig`, `CorsConfig`, `ScannerConfig`, `LoggingConfig`, `SecurityConfig` as nested POCO classes (or flat `AgentConfig` with sub-objects). |
| **Notes**    | Config Binder NuGet enables `configuration.Get<AgentConfig>()`. If config is missing or invalid → service fails to start with clear EventLog entry (CFG-04). |

---

### 10. `FingerprintAgent/Configuration/ConfigLoader.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `config` — ConfigurationBuilder wrapper |
| **Lifecycle**| Called once at service startup |
| **Data Flow**| → Reads `config.json` from `BaseDirectory` → builds `IConfigurationRoot` → binds to `AgentConfig` |
| **Conventions**| D-11: `config.json` is single source of truth. Phase 1: `reloadOnChange: false`. Phase 3: `reloadOnChange: true` + file watcher. |
| **Analog**   | None (greenfield). |
| **Error handling**| `FileNotFoundException` → log FATAL to EventLog, throw. `FormatException` (invalid JSON) → same. |
| **Forward-compat**| Returns `IConfigurationRoot` so Phase 3 can inject `IOptionsSnapshot<T>` without changing callers. |

---

### 11. `FingerprintAgent/Configuration/config.json`

| Property     | Value |
|--------------|-------|
| **Role**     | `config` — Default configuration file |
| **Lifecycle**| Read once at startup; shipped with installer |
| **Data Flow**| → `ConfigLoader` → `AgentConfig` |
| **Conventions**| CFG-02: schema covers all sections. CFG-04: must be valid JSON or service fails. |
| **Analog**   | None (greenfield). |
| **Schema**   | See RESEARCH.md §2.6 for full schema. Key defaults: `http.host: 127.0.0.1`, `http.port: 5043`, `cors.mode: wildcard`, `scanner.mockMode: true`. |
| **Location** | `AppDomain.CurrentDomain.BaseDirectory` (same dir as exe). |

---

### 12. `FingerprintAgent/Logging/AgentLogger.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `logging` — Logging facade (file + EventLog) |
| **Lifecycle**| Created at startup, disposed at shutdown |
| **Data Flow**| ⤷ Writes to file (StreamWriter) ⤷ Writes to Windows EventLog |
| **Conventions**| D-04: `System.Diagnostics.Trace` + `EventLog.WriteEntry`. D-05: log file at `C:\ProgramData\FingerprintAgent\Logs\agent.log`. OBS-01: structured format `[timestamp] [LEVEL] [correlationId] message`. OBS-02: logs startup, capture, errors. SEC-04: no fingerprint data in logs. |
| **Analog**   | None (greenfield). |
| **Levels**   | DEBUG, INFO, WARN, ERROR. Filtered by `logging.level` in config. |
| **Implementation**| Single class with `FileLogger` (StreamWriter) and `EventLog` fallback. CorrelationId generated per-request or passed from caller. |
| **Thread safety**| Lock around file writes. Separate lock for EventLog (not strictly needed but consistent). |

---

### 13. `FingerprintAgent/Adapters/IScannerAdapter.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `adapter` — Scanner abstraction contract |
| **Lifecycle**| Defined in Phase 1; implemented by mock in Phase 1, real scanners in Phase 2 |
| **Data Flow**| ⤷ `Scan()` → `CaptureResult`. `IsConnected` → bool. `DeviceId` → string. |
| **Conventions**| SCAN-05: shared contract for all vendors. D-09: adapter interface in main project for Phase 1, extracted in Phase 2. |
| **Analog**   | None (greenfield). Standard interface-pattern for hardware abstraction in .NET. |
| **Members**  | `bool IsConnected { get; }`, `string DeviceId { get; }`, `string Model { get; }`, `CaptureResult Scan()` (throws on failure). Phase 3: async `ScanAsync()` + `ConnectAsync()`. |

---

### 14. `FingerprintAgent/Adapters/MockScannerAdapter.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `adapter` — Mock scanner implementation |
| **Lifecycle**| Instantiated in DI container for Phase 1; replaced by real adapters in Phase 2 |
| **Data Flow**| ⤷ `Scan()` → creates 320x240 PNG in memory → computes SHA-256 → returns `CaptureResult` |
| **Conventions**| D-16: creates PNG gradient/placeholder. DeviceId = `mock-scanner-001`. D-03: uses `System.Drawing` (GDI+) in memory. `IsConnected` = always `true` in Phase 1. |
| **Analog**   | None (greenfield). |
| **Implementation**| `GenerateMockPng()` → `Bitmap(320,240)` → `Graphics` → fill ellipse + border + label → save to `MemoryStream` as PNG → `ComputeSha256Base64(imageBytes)`. |
| **Thread safety**| GDI+ objects created per-call (not cached). Each `Scan()` call is self-contained. Do NOT share `Pen`, `Brush`, `Font` across calls. |

---

### 15. `FingerprintAgent/Models/CaptureRequest.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `model` — Request DTO |
| **Lifecycle**| Deserialized per `POST /api/capture` call |
| **Data Flow**| ← JSON body → `CaptureHandler.Handle()` validates → passed to scanner (for logging/tracking only in Phase 1) |
| **Conventions**| D-14: fields match Angular frontend contract. C# PascalCase with `[JsonProperty("camelCase")]` attributes. |
| **Analog**   | None (greenfield). |
| **Fields**   | `ThamChieuId`, `MaPhieu`, `LoaiPhieu`, `VaiKyId`, `NhanLucId`, `Metadata` (Dictionary<string, string>). |
| **Validation**| At minimum `ThamChieuId` and `MaPhieu` required (validation logic in `CaptureHandler`). |

---

### 16. `FingerprintAgent/Models/CaptureResponse.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `model` — Response DTO |
| **Lifecycle**| Created per `POST /api/capture` call |
| **Data Flow**| `CaptureHandler` builds → serialized to JSON → HTTP response ← |
| **Conventions**| D-15: all fields required by contract. `ImageBytes` = base64 PNG. `VerificationData` = SHA-256 base64. `CapturedAt` = ISO 8601 UTC string. `ErrorMessage` = null on success. |
| **Analog**   | None (greenfield). |
| **Fields**   | `IsSuccess`, `ImageBytes`, `MimeType` ("image/png"), `CapturedAt`, `DeviceId`, `VerificationData`, `ErrorMessage`. |
| **Error shape**| When `IsSuccess = false`: `errorMessage` + `errorCode` ("INVALID_REQUEST" | "SCANNER_NOT_CONNECTED" | "CAPTURE_TIMEOUT" | "CAPTURE_FAILED"). |

---

### 17. `FingerprintAgent/Properties/AssemblyInfo.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `host` — Assembly metadata |
| **Lifecycle**| Compiled into assembly; read by OS File Properties + SCM |
| **Data Flow**| ⤷ Static metadata |
| **Conventions**| Standard .NET Framework `AssemblyInfo.cs`. Contains `AssemblyTitle`, `Description`, `Company`, `Product`, `Copyright`, `ComVisible`, `Guid`, `AssemblyVersion`, `AssemblyFileVersion`. |
| **Analog**   | None (greenfield). Standard .NET Framework convention. |
| **Notes**    | Only present in classic `.csproj` format. SDK-style projects auto-generate from `.csproj` properties. Decide format at project creation. |

---

### 18. `FingerprintAgent.Tests/FingerprintAgent.Tests.csproj`

| Property     | Value |
|--------------|-------|
| **Role**     | `test` — Test project definition |
| **Lifecycle**| Created in Phase 1, extended in every subsequent phase |
| **Data Flow**| ⤷ References `FingerprintAgent.csproj` + test framework |
| **Conventions**| Use xUnit (most widely supported on .NET Framework 4.8) or NUnit. `TargetFramework: net48`. Add `FluentAssertions` or `Shouldly` for readability. |
| **Analog**   | None (greenfield). |
| **Dependencies**| `xunit` 2.9.x, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Moq` 4.x (for mocking scanner adapter in later phases). |

---

### 19. `FingerprintAgent.Tests/MockScannerAdapterTests.cs`

| Property     | Value |
|--------------|-------|
| **Role**     | `test` — Unit tests for `MockScannerAdapter` |
| **Lifecycle**| Run during CI/dev. Validates mock behavior before real scanners exist. |
| **Data Flow**| ⤷ Calls `adapter.Scan()` → asserts on `CaptureResult` |
| **Conventions**| TDD: tests written against `IScannerAdapter` interface, not concrete class. Phase 2+ tests reuse same test fixtures with real adapters. |
| **Analog**   | None (greenfield). |
| **Test cases**| - `Scan()` returns non-null `CaptureResult` - `ImageBytes` is valid PNG (header `0x89504E47`) - `VerificationData` is non-empty base64 - `DeviceId` = "mock-scanner-001" - `IsConnected` = true - SHA-256 is deterministic (same call → same hash) - `MimeType` = "image/png" - Image is exactly 320x240 |

---

### 20. `FingerprintAgent.Tests/HttpServerTests.cs` (integration test stub)

| Property     | Value |
|--------------|-------|
| **Role**     | `test` — Integration/functional tests for HTTP API |
| **Lifecycle**| Run during CI/dev. Requires the server to be running (or use `HttpServer` in-process). |
| **Data Flow**| ⤷ Starts `HttpServer` (or runs exe) → sends HTTP requests → asserts responses |
| **Conventions**| D-01: test against raw HTTP (no ASP.NET test host). Use `HttpClient` or raw `WebRequest`. |
| **Analog**   | None (greenfield). |
| **Test cases**| - `GET /health` → 200 + `status: "healthy"` - `POST /api/capture` with valid body → 200 + `isSuccess: true` - `POST /api/capture` with empty body → 400 - `POST /api/capture` with malformed JSON → 400 - `OPTIONS /api/capture` with Origin → 204 + CORS headers - `GET /nonexistent` → 404 |
| **Notes**    | Phase 1 tests can be acceptance-level (run exe as console, send HTTP). Phase 2+ add in-process integration tests. |

---

### 21. `scripts/Install-Service.ps1`

| Property     | Value |
|--------------|-------|
| **Role**     | `deploy` — Windows Service installation script |
| **Lifecycle**| Run once (elevated) per machine by admin/dev |
| **Data Flow**| → Registers EventLog source → creates service via `New-Service` → creates log directory → sets recovery options |
| **Conventions**| D-07: PowerShell script for dev/test. DEP-02: creates service, log dir, EventLog source. SVC-01: service name `FingerprintAgent`, display name `Fingerprint Agent`. SVC-02: `StartupType Automatic`. |
| **Analog**   | None (greenfield). Pattern from Windows Service documentation. |
| **Elevation**| Requires administrator. Validates `BinaryPathName` target exists. |
| **Forward-compat**| Arguments: optionally accept `-BinPath` and `-Port` parameters. Phase 4: MSI replaces this for end users. |

---

### 22. `scripts/Uninstall-Service.ps1`

| Property     | Value |
|--------------|-------|
| **Role**     | `deploy` — Service removal script |
| **Lifecycle**| Run (elevated) when removing service |
| **Data Flow**| → Stops service → `sc.exe delete` |
| **Conventions**| DEP-03: idempotent (checks if service exists first). |
| **Analog**   | None (greenfield). |
| **Elevation**| Requires administrator. |

---

### 23. `scripts/Test-Capture.ps1`

| Property     | Value |
|--------------|-------|
| **Role**     | `deploy` — Quick smoke-test script |
| **Lifecycle**| Run by dev or support to verify agent is operational |
| **Data Flow**| → `Invoke-RestMethod` to `/health` then `/api/capture` → prints results |
| **Conventions**| DEP-04: validates both endpoints. Port configurable (default 5043). |
| **Analog**   | None (greenfield). |
| **Test flow**| 1. Check `/health` 2. Send capture request with sample body 3. Print `isSuccess`, `deviceId`, `verificationData` 4. Optionally save PNG to Temp |

---

## Data Flow Diagram (Logical)

```
                    ┌──────────────────────────────────────────────┐
                    │               Program.cs                     │
                    │  (args parse: --service / --console)         │
                    └──────────┬───────────────────────────────────┘
                               │ instantiates
                               ▼
                    ┌──────────────────────────────────────────────┐
                    │         FingerprintAgentService              │
                    │  (ServiceBase: OnStart / OnStop)             │
                    └──┬──────────┬──────────┬─────────────────────┘
                       │          │          │
              loads     │          │          │  creates
              ┌─────────▼──┐  ┌───▼────┐  ┌──▼───────────┐
              │ ConfigLoader│  │AgentLog│  │  HttpServer   │
              │ (config.json│  │(file + │  │ (HttpListener)│
              │  → AgentCfg)│  │ Event) │  └──┬───────────┘
              └─────────────┘  └────────┘     │ async loop
                                              │
                              ┌───────────────┼──────────────────┐
                              │               │                  │
                         ┌────▼───┐     ┌─────▼─────┐     ┌─────▼──────┐
                         │CorsMid.│     │HealthHndlr│     │CaptureHndlr│
                         │(CORS   │     │GET /health│     │POST /captr │
                         │ header)│     │           │     └──┬───┬─────┘
                         └────────┘     └───────────┘        │   │
                                                     validates│   │ calls
                                                              │   └──► ┌──────────────┐
                                                              │        │IScannerAdapter│
                                                              │        │ (MockScanner) │
                                                              │        └──────┬───────┘
                                                              │               │ returns
                                                              │        ┌──────▼───────┐
                                                              │        │CaptureResult │
                                                              │        │ (PNG + SHA256)│
                                                              │        └──────────────┘
                                                              ▼
                                                   ┌──────────────────┐
                                                   │ JSON Response    │
                                                   │ (CaptureResponse)│
                                                   └──────────────────┘
```

---

## Lifecycle State Machine (Per Process)

```
[Process Start]
    │
    ▼
[Program.Main()]
    │
    ├── --service flag? ──► [ServiceBase.Run(FingerprintAgentService)]
    │                              │
    │                              ├── OnStart()
    │                              │   ├── ConfigLoader.Load() → AgentConfig
    │                              │   ├── AgentLogger.Init()
    │                              │   ├── HttpServer.Start(host, port)
    │                              │   └── return (< 30s)
    │                              │
    │                              ├── [Running]
    │                              │   ├── HttpListener loop
    │                              │   ├── Handle requests (health, capture)
    │                              │   └── EventLog + file logging
    │                              │
    │                              └── OnStop()
    │                                  ├── HttpServer.Stop()
    │                                  ├── AgentLogger.Flush()
    │                                  └── return
    │
    └── --console flag? ──► [Manual Start]  (Ctrl+C handler)
                             └── same lifecycle, different entry
```

---

## Convention Summary (from D-01..D-16)

| ID | Convention | Applies To |
|----|-----------|------------|
| D-01 | `System.Net.HttpListener` — no OWIN/ASP.NET | `HttpServer.cs` |
| D-02 | Default bind `127.0.0.1:5043`, configurable | `HttpServer.cs`, `config.json` |
| D-03 | `System.Drawing` (GDI+) for PNG in memory | `MockScannerAdapter.cs` |
| D-04 | `Trace` + `EventLog.WriteEntry` — no Serilog/NLog | `AgentLogger.cs` |
| D-05 | Log file at `C:\ProgramData\FingerprintAgent\Logs\agent.log` | `AgentLogger.cs`, `config.json`, `Install-Service.ps1` |
| D-06 | Pure `ServiceBase` — no Topshelf | `FingerprintAgentService.cs`, `Program.cs` |
| D-07 | PowerShell script install (dev); MSI in Phase 4 | `Install-Service.ps1`, `Uninstall-Service.ps1` |
| D-08 | Single `.csproj` for Phase 1 | `FingerprintAgent.csproj` |
| D-09 | Real adapters separated in Phase 2+ | `IScannerAdapter.cs` (interface stays in main) |
| D-10 | ME.DI + ME.Configuration.Json via NuGet | `FingerprintAgent.csproj`, `ConfigLoader.cs` |
| D-11 | `config.json` single source of truth; reload in Phase 3 | `ConfigLoader.cs`, `config.json` |
| D-12 | Default CORS wildcard `*` | `CorsMiddleware.cs`, `config.json` |
| D-13 | `cors.mode` = `wildcard`\|`allowlist` | `CorsMiddleware.cs`, `AgentConfig.cs` |
| D-14 | POST body fields: `thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`, `metadata` | `CaptureRequest.cs`, `CaptureHandler.cs` |
| D-15 | Response fields: `isSuccess`, `imageBytes` (base64 PNG), `verificationData` (SHA-256 base64), etc. | `CaptureResponse.cs`, `CaptureHandler.cs` |
| D-16 | Mock deviceId = `mock-scanner-001`, PNG gradient | `MockScannerAdapter.cs` |

---

## Requirement-to-File Mapping (Phase 1 scope)

| Req ID | File(s) | Role |
|--------|---------|------|
| API-01 | `HttpServer.cs`, `CaptureHandler.cs` | api |
| API-02 | `CaptureRequest.cs`, `CaptureHandler.cs` | model, api |
| API-03 | `CaptureResponse.cs`, `CaptureHandler.cs` | model, api |
| API-04 | `CaptureHandler.cs` | api |
| API-05 | `CorsMiddleware.cs`, `AgentConfig.cs` | api, config |
| API-06 | `HealthHandler.cs` | api |
| SVC-01 | `FingerprintAgentService.cs`, `Install-Service.ps1` | host, deploy |
| SVC-02 | `Install-Service.ps1` (StartupType = Automatic) | deploy |
| SVC-03 | `FingerprintAgentService.cs` | host |
| SVC-04 | `AgentLogger.cs` | logging |
| SVC-05 | `Install-Service.ps1` (LocalSystem default) | deploy |
| CFG-01 | `ConfigLoader.cs`, `config.json` | config |
| CFG-02 | `AgentConfig.cs`, `config.json` | config |
| CFG-04 | `ConfigLoader.cs` (error handling) | config |
| SCAN-05 | `IScannerAdapter.cs` | adapter |
| SEC-01 | `HttpServer.cs` (bind 127.0.0.1), `config.json` | api, config |
| SEC-02 | `CorsMiddleware.cs` | api |
| SEC-03 | `MockScannerAdapter.cs` (in-memory only), `AgentLogger.cs` (no image data) | adapter, logging |
| SEC-04 | `AgentLogger.cs` | logging |
| OBS-01 | `AgentLogger.cs` | logging |
| OBS-02 | `AgentLogger.cs`, `HealthHandler.cs` | logging, api |
| OBS-03 | `HealthHandler.cs` | api |
| DEP-02 | `Install-Service.ps1` | deploy |
| DEP-03 | `Uninstall-Service.ps1` | deploy |
| DEP-04 | `Test-Capture.ps1` | deploy |
| SCAN-01..04 | *(deferred to Phase 2)* | — |
| CFG-03 | *(deferred to Phase 3)* | — |

---

## Classification Summary

| Role        | Files |
|-------------|-------|
| **entry**   | `Program.cs` |
| **host**    | `FingerprintAgentService.cs`, `FingerprintAgent.csproj`, `AssemblyInfo.cs`, `FingerprintAgent.sln` |
| **api**     | `HttpServer.cs`, `HealthHandler.cs`, `CaptureHandler.cs`, `CorsMiddleware.cs` |
| **config**  | `AgentConfig.cs`, `ConfigLoader.cs`, `config.json` |
| **logging** | `AgentLogger.cs` |
| **adapter** | `IScannerAdapter.cs`, `MockScannerAdapter.cs` |
| **model**   | `CaptureRequest.cs`, `CaptureResponse.cs` |
| **test**    | `FingerprintAgent.Tests.csproj`, `MockScannerAdapterTests.cs`, `HttpServerTests.cs` |
| **deploy**  | `Install-Service.ps1`, `Uninstall-Service.ps1`, `Test-Capture.ps1` |

---

## Key Pattern Decisions

1. **No framework analogs exist** — every file is greenfield. Patterns are drawn from .NET Framework documentation, MSDN patterns, and the decisions in CONTEXT.md.

2. **Single-project constraint** (D-08) means namespace-as-folder-boundary: `FingerprintAgent.Adapters`, `FingerprintAgent.Api`, `FingerprintAgent.Configuration`, `FingerprintAgent.Logging`, `FingerprintAgent.Service`, `FingerprintAgent.Models`.

3. **Request dispatch is synchronous on thread pool** — `HttpListener` async loop dispatches `Task.Run(...)` for each request. This is intentional: `CaptureHandler` stays synchronous in Phase 1 (no long-running scan). Phase 3 upgrades to async scan.

4. **Interface-first adapter design** — `IScannerAdapter` is the seam between API and hardware. All handlers code against the interface, enabling test doubles without DI container changes.

5. **No DI container for Phase 1 startup** — `Microsoft.Extensions.DependencyInjection` is referenced but for Phase 1 the service manually wires dependencies in `OnStart()`. DI container is set up but not heavily used until Phase 2 when adapter selection logic is needed.

6. **CorrelationId generation** — Each capture request gets a `Guid.NewGuid().ToString("N")` correlationId. Logged with every entry for that request. Passed through `CaptureHandler` → `IScannerAdapter.Scan()` as parameter (interface will be updated in Phase 2).

7. **Self-contained mock data** — `MockScannerAdapter` generates deterministic output (same call → same image → same SHA-256). This enables test assertions on `VerificationData` across test runs.

---

## PATTERN MAPPING COMPLETE
<!-- OMO_INTERNAL_INITIATOR -->
