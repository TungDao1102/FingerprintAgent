# FingerprintAgent — Agent Context

> Root knowledge base for AI agents. Read first.
> Verified against current source; stale claims from prior versions have been removed.

---

## What this is

Windows Service (net48, x86) that exposes a local HTTP API (localhost:5043) to a web-based HIS frontend, proxying capture requests to USB fingerprint scanners. **Matching is NOT done here** — backend HIS handles 1:1 / 1:N. **No biometric data is persisted** — in-memory only.

Architecture flow:
```
HTTP Client → FingerprintAgent → ScannerManager (priority fallback) → IScannerAdapter.ScanAsync → Vendor SDK
```
`ScannerManager` continues to next adapter on failure (D-12); only returns `SCANNER_NOT_CONNECTED` if all fail.

---

## Critical gotchas (read these before editing)

### Two `Program.cs` files exist
| File | Role |
|---|---|
| `src/FingerprintAgent/Program.cs` | **Dead code** — never runs |
| `src/FingerprintAgent.Host/Program.cs` | **Active entry** — `--service` for SCM, `--console` for interactive |

The Host project's `RootNamespace` is `FingerprintAgent` and `AssemblyName` is `FingerprintAgent` — same as the library's namespace. The library's actual assembly name is `FingerprintAgent.Library` (see `FingerprintAgent.csproj`).

Console mode reads `FA_CONSOLE_TIMEOUT` (seconds) for CI auto-shutdown; 0/negative = infinite. Default waits for Ctrl+C.

### ZKTeco static singleton teardown
`ZkTecoFingerHost.Close()` is process-wide and terminates native context for ALL ZKTeco instances. Called **once** at service shutdown (in `Program.cs` console path + `FingerprintAgentService`). Individual `ZKTecoAdapter.Dispose()` must **NOT** call it.

### SDK presence is conditional
Vendor SDKs are gated by file existence in `lib/`:
```
lib/ZKTeco/libzkfp.dll             → DefineConstants ZKTECO_SDK_PRESENT
lib/SecuGen/SecuGen.FDxSDKPro.Windows.dll  → SECUGEN_SDK_PRESENT
lib/DigitalPersona/DPFPDevNET.dll  → DIGITALPERSONA_SDK_PRESENT
lib/Futronic/ftrScanAPI.dll        → FUTRONIC_SDK_PRESENT
```
Missing SDK = adapter compiles to a stub. Real-device tests skip gracefully when SDK absent (`ZKTecoDeviceIntegrationTests`).

### No DI container (despite package)
`Microsoft.Extensions.DependencyInjection` is referenced in `.csproj` but **never used** — all classes `new`'d directly. Don't introduce DI without explicit reason.

### Fire-and-forget `HttpServer` shutdown
`HttpServer.cs:103` uses `#pragma warning disable CS4014` + `.Wait()` for graceful drain. Fragile; broad `catch (Exception)` also catches `ThreadAbortException`. Don't restructure without testing both stop paths.

---

## Configuration

`src/FingerprintAgent/config.json` is the **template** (copied to bin output via `<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>`). Edit the template, not the bin copies.

```json
{
  "service":  { "name": "FingerprintAgent", "displayName": "Fingerprint Agent", "description": "..." },
  "http":     { "host": "127.0.0.1", "port": 5043 },
  "cors":     { "mode": "wildcard|allowlist", "allowedOrigins": [] },
  "scanner":  { "priority": ["ZKTeco","SecuGen","Futronic","DigitalPersona"], "mockMode": false },
  "logging":  { "level": "INFO", "file": "C:\\ProgramData\\FingerprintAgent\\Logs\\agent.log", "maxSizeMb": 10, "maxFiles": 5 },
  "security": { "bindIp": "127.0.0.1" }
}
```

`ConfigFileWatcher` (`Configuration/ConfigFileWatcher.cs`) monitors the file with 300ms debounce → fires `ConfigReloaded` → updates `CorsMiddleware` (atomic HashSet swap) + `ScannerManager.UpdatePriority()` (active adapter is **not** disturbed).

Default `scanner.mockMode` is `false` in source. To run out-of-box without hardware, flip it to `true`.

---

## Adapters

`src/FingerprintAgent/Adapters/IScannerAdapter.cs`:
```csharp
bool IsConnected { get; }
string DeviceId { get; }
string Model { get; }
string MimeType { get; }
bool Initialize();
bool ProbeConnection();        // light real-time check (~1-10ms); default returns cached IsConnected
string VendorErrorCode { get; } // "NONE" when no error
Task<CaptureResult> ScanAsync(CancellationToken ct = default);
```

`BaseScannerAdapter` (in same folder) provides `ProbeConnection()` default and shared disposal scaffolding — extend it; don't reimplement.

Mock mode (`MockScannerAdapter`) generates a deterministic 320×240 PNG. Use `MockScannerAdapterWithSettableProperties` in tests for controllable behavior.

---

## Error code → HTTP status

| `CaptureResult.ErrorCode` | HTTP |
|---|---|
| `SCANNER_NOT_CONNECTED` | 503 |
| `CAPTURE_TIMEOUT` | 504 |
| `CAPTURE_FAILED` | 500 |
| `INVALID_REQUEST` | 400 |

Exponential backoff: `{10, 30, 60, 120}s`, resets on any successful capture.

---

## Build & Run

```powershell
dotnet build FingerprintAgent.sln          # debug
dotnet build FingerprintAgent.sln -c Release   # 0 warnings / 0 errors expected in production code
dotnet test  FingerprintAgent.sln          # all tests (real-device tests skip if SDK missing)
.\scripts\Install-Service.ps1               # admin: install via sc.exe
.\scripts\Service.ps1 start|stop|restart|status
.\scripts\Test-Capture.ps1                  # /health + /api/capture smoke test
.\scripts\Setup-VendorSdk.ps1               # downloads vendor SDKs to lib/
```

**No CI/CD** — `.github/workflows/` absent. No MSI installer yet.

---

## Conventions

- Namespace: `FingerprintAgent.<Module>` (Adapters, Api, Configuration, Logging, Models, Service)
- Private fields: `_camelCase`; static readonly arrays PascalCase (e.g. `BackoffDelaysSeconds`)
- Lock objects: `private readonly object _lock = new object();` (or domain-specific name)
- Logger: `_logger?.Info(...)` — logger is optional everywhere
- Guards: `config?.Scanner ?? throw new ArgumentNullException(nameof(config))`
- Result factories: `CaptureResult.Ok(...)`, `CaptureResult.Fail(...)`
- Correlation IDs: `AgentLogger.GenerateCorrelationId()` → 10-char hex, regex `^[a-f0-9]{10}$`
- JSON: **Newtonsoft.Json** (not System.Text.Json); `[JsonProperty("camelCase")]` on model props
- HTTP: raw `HttpListener` — no MVC/WebAPI
- Nullable: not enforced project-wide (`net48` + LangVersion 8.0, no `<Nullable>enable</Nullable>`). `#nullable enable` is per-file (see `ZKTecoAdapter`).
- Commits: `<type>(<phase-number>): <description>` — types: `feat`, `docs`, `test`, `fixup`

---

## Tests

`tests/FingerprintAgent.Tests/` — xUnit 2.9.3 + Moq 4.20.72. Test project has `<InternalsVisibleTo>FingerprintAgent.Tests</InternalsVisibleTo>` (see `FingerprintAgent.csproj`) so tests can reach internal members.

Layout by system boundary: `Api/`, `Configuration/`, `Logging/`, `Scanner/`. Naming: `Method_Scenario_ExpectedResult` (e.g. `BackoffStep_StartsAtZero`).

Custom test doubles preferred over Moq for vendor SDK surfaces:
- `MockScannerAdapterWithSettableProperties` — settable `IsConnectedValue`, `InitializeResult`, `ScanResult`, `VendorErrorCodeValue`
- `CaptureHandlerTestFixture` — real `HttpListener`-based end-to-end
- `MockScannerAdapterTestDoubles.cs` — shared doubles file

`HttpServer` integration tests use `TcpListener` to find a random free port (no port conflicts in CI).

---

## Anti-patterns (do not extend)

| Location | Pattern | Note |
|---|---|---|
| `FingerprintAgentService.cs:111` | Bare `catch { }` | Silently swallows disposal errors |
| `AgentLogger.cs:167` | Bare `catch { }` | `TryWriteEventLog` fallback |
| `HttpServer.cs:88` | Bare `catch { }` | `AggregateException` drain |
| `HttpServer.cs:103,166` | Fire-and-forget CS4014 | `Wait()` is fragile; don't restructure without full stop-path test |
| `FutronicAdapter.cs:15` | `/// TODO (pre-production)` | Verify against test fingerprint image |
| Project-wide | Nullable not enforced | Pre-existing constraint; don't toggle globally |

---

## Layout

```
FingerprintAgent/
├── src/
│   ├── FingerprintAgent/                ← main library (21 .cs files; OutputType=Library)
│   │   ├── Adapters/                    ← IScannerAdapter + Base + 5 implementations + CaptureResult
│   │   ├── Api/                         ← HttpServer, CaptureHandler, HealthHandler, CorsMiddleware
│   │   ├── Configuration/               ← AgentConfig, ConfigLoader, ConfigFileWatcher
│   │   ├── Logging/                     ← AgentLogger
│   │   ├── Models/                      ← CaptureRequest, CaptureResponse
│   │   ├── Service/                     ← FingerprintAgentService
│   │   ├── config.json                  ← template (copied to output)
│   │   └── FingerprintAgent.csproj      ← AssemblyName=FingerprintAgent.Library
│   └── FingerprintAgent.Host/           ← Exe entry (--service|--console)
├── tests/FingerprintAgent.Tests/
├── scripts/                             ← Install-Service, Service, Test-Capture, Setup-VendorSdk, Uninstall-Service, diagnostic/
├── lib/                                 ← vendor SDK DLLs (created by Setup-VendorSdk.ps1; gitignored)
├── .planning/codebase/                  ← verified codebase map (STACK/ARCHITECTURE/CONVENTIONS/TESTING/CONCERNS/...)
├── FingerprintAgent.sln
└── SCANNER_SETUP.md
```
