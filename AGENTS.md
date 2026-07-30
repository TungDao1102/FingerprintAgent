# FingerprintAgent — Agent Context

> **Purpose:** Instructions for any AI agent working in this repository.
> This file is the **root knowledge base**. Read this first.

---

## Project Identity

**What this is:** A Windows Service that acts as a local HTTP API proxy for USB fingerprint scanners, enabling a web-based HIS (Hospital Information System) to capture fingerprints from any workstation without direct scanner access.

**Core value:** Agent luôn sẵn sàng trên máy bệnh viện, kết nối được ít nhất một trong các máy quét vân tay phổ biến, và trả về ảnh PNG đáng tin cậy cho ứng dụng web qua HTTP API địa phương.

**Business context:** Multi-tenant HIS SaaS for hospitals. Angular frontend calls this local agent (like a USB token signing agent). Backend on company server cannot reach agent through NAT/firewall. Agent is on-premise at each hospital PC.

**Target users:** Hospital medical staff performing biometric patient identification.

---

## Architecture

```
HTTP Client (Angular, same PC)
  → POST /api/capture { thamChieuId, maPhieu }
  → FingerprintAgent (localhost:5043)
    → ScannerManager (priority fallback: SecuGen → DigitalPersona → Futronic → ZKTeco → Mock)
      → IScannerAdapter.Scan()
        → [Vendor SDK] → PNG bytes + SHA-256
  → { imageBytes (base64), sha256, mimeType, capturedAt }
```

```
HTTP Client
  → GET /health
  → FingerprintAgent
    → ScannerManager.InBackoff, ScannerManager.IsConnected
  → { status, deviceId, uptime, inBackoff, backoffStep }
```

**Key classes:**

| Class | File | Responsibility |
|---|---|---|
| `FingerprintAgentService` | `src/FingerprintAgent/Service/FingerprintAgentService.cs` | Windows Service `OnStart`/`OnStop` lifecycle; creates `ScannerManager` + `HttpServer` |
| `HttpServer` | `src/FingerprintAgent/Api/HttpServer.cs` | Raw `HttpListener` loop; routes `/health` → `HealthHandler`, `/api/capture` → `CaptureHandler` |
| `ScannerManager` | `src/FingerprintAgent/Adapters/ScannerManager.cs` | Owns adapter lifecycle, priority fallback, exponential backoff, mock mode |
| `IScannerAdapter` | `src/FingerprintAgent/Adapters/IScannerAdapter.cs` | Interface: `IsConnected`, `DeviceId`, `Model`, `Scan()`, `VendorErrorCode` |
| `CaptureHandler` | `src/FingerprintAgent/Api/CaptureHandler.cs` | Deserializes `CaptureRequest`, calls `scanner.Scan()`, maps error codes, returns `CaptureResponse` |
| `HealthHandler` | `src/FingerprintAgent/Api/HealthHandler.cs` | Reads scanner state, returns HTTP 200/503 based on backoff step |
| `CorsMiddleware` | `src/FingerprintAgent/Api/CorsMiddleware.cs` | Applies CORS headers from `config.json`; hot-reloads on config change |
| `ConfigFileWatcher` | `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs` | `FileSystemWatcher` + 300ms debounce; fires `ConfigReloaded` on valid config |
| `AgentLogger` | `src/FingerprintAgent/Logging/AgentLogger.cs` | Structured logging: file sink + EventLog sink; correlation IDs; SEC-04 base64 redaction |
| `AgentConfig` | `src/FingerprintAgent/Configuration/AgentConfig.cs` | All config sections: service, http, cors, scanner, logging, security |

**Request path (full):**
```
HTTP POST /api/capture
  → HttpServer.HandleRequest()
    → CorsMiddleware.ApplyCorsHeaders()
    → CaptureHandler.Handle(context, _scanner, correlationId)
      → JsonConvert.DeserializeObject<CaptureRequest>(body)
      → _scanner.Scan()        // _scanner = ScannerManager
        → ScannerManager → ActiveAdapter (first healthy in priority list)
          → [VendorAdapter].Scan()
        → CaptureResult (imageBytes, verificationData, mimeType)
      → JsonConvert.SerializeObject(CaptureResponse)
  → HTTP 200 + JSON
```

**Error code → HTTP status mapping:**
| `CaptureResult.ErrorCode` | HTTP Status |
|---|---|
| `SCANNER_NOT_CONNECTED` | 503 |
| `CAPTURE_TIMEOUT` | 504 |
| `CAPTURE_FAILED` | 500 |
| `INVALID_REQUEST` | 400 |

**Exponential backoff:** `{10, 30, 60, 120}s` — resets on any successful capture.

---

## Scanner Adapters

| Adapter | Vendor SDK | File |
|---|---|---|
| `MockScannerAdapter` | None (generates deterministic 320×240 PNG) | `Adapters/MockScannerAdapter.cs` |
| `ZKTecoAdapter` | `ZkTecoFingerPrint` NuGet v1.2.1 | `Adapters/ZKTecoAdapter.cs` |
| `SecuGenAdapter` | `SecuGen.FDxSDKPro.Windows.dll` (external) | `Adapters/SecuGenAdapter.cs` |
| `DigitalPersonaAdapter` | `DPUruNet` NuGet v1.0.0.1 | `Adapters/DigitalPersonaAdapter.cs` |
| `FutronicAdapter` | `ftrScanAPI.dll` (x86, P/Invoke) | `Adapters/FutronicAdapter.cs` |

**Important:** `ZKTecoAdapter` static singleton pattern — `ZkTecoFingerHost.Close()` is called once at service shutdown and terminates shared native context for ALL instances. Individual adapter `Dispose()` must NOT call it.

**MockMode:** `config.Scanner.MockMode = true` by default. Agent runs with fake PNG out-of-box.

---

## Entry Points

**Two `Program.cs` files exist — only `FingerprintAgent.Host/` executes:**

| File | Role |
|---|---|
| `src/FingerprintAgent/Program.cs` | **Dead code** — library entry, never runs |
| `src/FingerprintAgent.Host/Program.cs` | **Actual entry** — dual-mode: `--service` (SCM) or `--console`/interactive |

**Service mode:** `ServiceBase.Run(new FingerprintAgentService())`
**Console mode:** `new FingerprintAgentService(logger).StartConsole()`

---

## Configuration

`config.json` at project root (copied to output on build). Key sections:

```json
{
  "service": { "serviceName": "FingerprintAgent" },
  "http": { "host": "127.0.0.1", "port": 5043 },
  "cors": { "allowedOrigins": ["*"] },
  "scanner": { "mockMode": true, "priority": ["SecuGen", "DigitalPersona", "Futronic", "ZKTeco"] },
  "logging": { "logLevel": "Info", "logDirectory": "C:\\ProgramData\\FingerprintAgent\\Logs" },
  "security": { }
}
```

**Config reload:** `ConfigFileWatcher` monitors the file and fires `ConfigReloaded` → updates `CorsMiddleware` (atomic HashSet swap under lock) + `ScannerManager.UpdatePriority()` (recreates adapter list, active adapter untouched).

---

## Test Projects (Two Exist)

| Path | Purpose | Framework | Test Count |
|---|---|---|---|
| `src/FingerprintAgent.Tests/` | Unit tests | xUnit 2.6.2, Moq 4.20.70 | ~24 |
| `tests/FingerprintAgent.Tests/` | Integration tests | xUnit 2.9.3, Moq 4.20.72 | ~35 |

**Test naming:** Descriptive English — `BackoffStep_StartsAtZero`, `CaptureHandler_Returns503_WhenScannerReturnsScannerNotConnected`

**Custom test doubles:**
- `MockScannerAdapterWithSettableProperties` — settable `IsConnectedValue`, `InitializeResult`, `ScanResult`, `VendorErrorCodeValue`
- `CaptureHandlerTestFixture` — real `HttpListener`-based integration test fixture

**Pre-existing test failures:** 2 tests fail due to missing SecuGen SDK DLLs (not in repo). These are adapter integration tests that require vendor binaries.

---

## Build & Run

```powershell
# Build
dotnet build FingerprintAgent.sln
dotnet build FingerprintAgent.sln -c Release   # 0 warnings, 0 errors (production); 2 pre-existing xUnit1031 warnings in test code

# Run tests
dotnet test FingerprintAgent.sln               # ~59 passing, 2 pre-existing failures

# Install service (admin)
.\scripts\Install-Service.ps1

# Service control
.\scripts\Service.ps1 start|stop|restart|status

# Integration smoke test
.\scripts\Test-Capture.ps1                     # hits /health and /api/capture
```

**Platform:** `net48`, x86 (required for vendor SDK compatibility)
**No GitHub Actions CI/CD** — `.github/workflows/` absent

---

## Key Constraints

| Constraint | Detail |
|---|---|
| **Tech stack** | .NET Framework 4.8, framework-dependent |
| **Storage** | No biometric data persisted — in-memory only |
| **Matching** | NOT done here — backend HIS handles 1:1/1:N matching |
| **Windows version** | Windows 10/11 target; unofficially Win7 SP1 if .NET 4.8 installed |
| **Deployment target** | MSI installer (not yet implemented) |
| **No DI container** | All classes `new`'d directly; `Microsoft.Extensions.DependencyInjection` listed but unused |

---

## Coding Conventions

| Rule | Pattern |
|---|---|
| **Namespace** | `FingerprintAgent.<Module>` |
| **Private fields** | `_camelCase` (e.g., `_config`, `_logger`, `_adapterLock`) |
| **Static readonly arrays** | `private static readonly int[] BackoffDelaysSeconds = { 10, 30, 60, 120 };` |
| **Locks** | `private readonly object _lock = new object();` |
| **Logger null-conditional** | `_logger?.Info(...)` — logger often optional |
| **Guard clauses** | `config?.Scanner ?? throw new ArgumentNullException(nameof(config))` |
| **Static factories** | `CaptureResult.Ok()`, `CaptureResult.Fail()` |
| **Correlation IDs** | `AgentLogger.GenerateCorrelationId()` passed through call chains |

---

## Commit Convention

```
<type>(<phase-number>): <description>
```
Types: `feat`, `docs`, `test`, `fixup`

Examples: `feat(03-01): add exponential backoff`, `docs(03-02): update configuration docs`

---

## Known Issues / Anti-Patterns

| Issue | Location | Notes |
|---|---|---|
| Bare `catch { }` | `FingerprintAgentService.cs:111` | Silently swallows disposal errors in `OnStop` |
| Bare `catch { }` | `AgentLogger.cs:167` | `TryWriteEventLog` fallback |
| Bare `catch { }` | `HttpServer.cs:88` | `AggregateException` drain wait |
| Fire-and-forget | `HttpServer.cs:103` | `#pragma warning disable CS4014` + `Wait()` for graceful drain — fragile |
| Dual test project | `src/` and `tests/` | Only `tests/FingerprintAgent.Tests/` referenced in phase artifacts; `src/` version may be legacy |
| Static singleton teardown | `ZKTecoAdapter` | `ZkTecoFingerHost.Close()` called once globally; individual `Dispose()` must not call it |
| CS4014 fire-and-forget | `HttpServer.cs:103,166` | `Wait()` drain is fragile; `catch (Exception)` also catches `ThreadAbortException` |
| Nullable not enforced | Project-wide | `net48` + LangVersion 8.0, no `<Nullable>enable</Nullable>` |
| `FutronicAdapter` TODO | `FutronicAdapter.cs:15` | `/// TODO (pre-production): verify against a known test fingerprint image` |

---

## Project Layout

```
FingerprintAgent/                    ← git root
├── src/
│   ├── FingerprintAgent/           ← main library (25 .cs files)
│   │   ├── Adapters/                ← IScannerAdapter + 6 implementations + CaptureResult
│   │   ├── Api/                     ← HttpServer, CaptureHandler, HealthHandler, CorsMiddleware
│   │   ├── Configuration/           ← AgentConfig, ConfigLoader, ConfigFileWatcher
│   │   ├── Models/                  ← CaptureRequest, CaptureResponse
│   │   ├── Logging/                 ← AgentLogger
│   │   └── Service/                 ← FingerprintAgentService
│   ├── FingerprintAgent.Host/       ← Windows Service entry point (1 Program.cs)
│   └── FingerprintAgent.Tests/      ← unit tests (6 .cs)
├── tests/
│   └── FingerprintAgent.Tests/      ← integration tests (9 .cs)
├── scripts/
│   ├── Install-Service.ps1          ← admin install via sc.exe
│   ├── Uninstall-Service.ps1
│   ├── Service.ps1                  ← start/stop/restart/status
│   ├── Test-Capture.ps1             ← /health + /api/capture smoke test
│   └── Setup-VendorSdk.ps1          ← downloads vendor SDKs to lib/
├── .planning/                       ← GSD phase artifacts
├── FingerprintAgent.sln
├── config.json                      ← template config (copied to output)
└── SCANNER_SETUP.md                 ← vendor SDK setup guide
```

---

## Phase Status

| Phase | Name | Plans | Status |
|---|---|---|---|
| 01 | Foundation: Windows Service + HTTP API Skeleton | 4 | ✅ Complete |
| 02 | Multi-Vendor Scanner Adapters | 4 | ✅ Complete |
| 03 | Resilience & Runtime Reconfiguration | 3 | ✅ Complete |
| 04 | — | 4 planned | ○ Not started |

---

*Generated by `/init-deep` — do not edit manually*