# External Integrations

**Analysis Date:** 2026-08-19

## APIs & External Services

**HTTP API served by FingerprintAgent (local loopback only):**

| Method | Path | Handler | Purpose |
|---|---|---|---|
| `GET` | `/health` | `Api/HealthHandler.cs` | Probe scanner state, returns 200 (`healthy`) or 503 (`degraded`); also returns uptime + backoff step |
| `POST` | `/api/capture` | `Api/CaptureHandler.cs` | Trigger fingerprint capture; JSON body validated; returns base64 PNG + SHA-256 verificationData |
| `OPTIONS` | `*` | `Api/CorsMiddleware.cs:36` | CORS preflight; returns 204 (allow) or 403 (deny if `mode=allowlist` and origin missing) |

- **Bind address:** `127.0.0.1:5043` (configurable via `config.json` `http.host`/`http.port`) — loopback only; never bound to a public interface
- **Server:** `System.Net.HttpListener` raw loop, single `LongRunning` worker task + in-flight tracking (`Api/HttpServer.cs:104-135`)
- **CORS:** wildcard (`*`) by default, or allowlist via `CorsConfig.AllowedOrigins` (`Api/CorsMiddleware.cs:72-85`)
- **Auth:** None — agent trusts the loopback caller. No API keys, tokens, or origin restrictions beyond CORS

**No outbound HTTP/HTTPS calls.** The agent does not call any external backend, HIS, or third-party service. It is a pure capture-and-respond local service.

## Data Storage

**Databases:** None. The agent holds **no persistent biometric data** — captures live in process memory only (PNG bytes returned to caller, then GC'd). SHA-256 `verificationData` is a checksum, not stored.

**File Storage:** Local filesystem only.
- **Config:** `config.json` next to `FingerprintAgent.exe` (read at startup, watched for changes via `FileSystemWatcher`)
- **Logs:** `C:\ProgramData\FingerprintAgent\Logs\agent.log` (configurable via `logging.file`)
- **Vendor DLLs:** Either installed system-wide (e.g., `C:\Windows\SysWOW64\libzkfp.dll`) or copied into `lib\<Vendor>\` next to the binary
- No biometric image persistence (matches AGENTS.md "Storage: No biometric data persisted — in-memory only")

**Caching:** None. ScannerManager does not cache captured images; it relies on `ScannerManager._activeAdapter` to track the last successful vendor.

## Vendor SDK Integrations (the only "external" integrations)

All four scanner vendors are integrated via native Windows DLLs called through managed wrappers or P/Invoke. None use a network API — every interaction is local USB/PCI.

### ZKTeco (ZKFinger SDK)

- **SDK:** `ZkTecoFingerPrint` NuGet v1.2.1 (managed wrapper) + native `libzkfp.dll` + `libzkfpcsharp.dll` (32-bit)
- **Wrapper API:** `ZkTecoFingerHost.Initialize/Close/OpenDevice`, `ZkFingerPrintDevice.AcquireFingerprintAsync(byte[], CancellationToken)`
- **Models:** ZK4500, ZK6500, ZK7500, ZK8500, ZK8500R, ZK9500, SLK20R, SLK20M (per `SCANNER_SETUP.md:10`)
- **Install:** Manual — requires ZKTeco ZKFinger SDK download (Silver+ membership) per `scripts/Setup-VendorSdk.ps1:91-102`
- **Wrapper source:** GitHub project `rainxh11/ZkTecoFingerPrint` (~13 stars) — flagged as small/supply-chain-risky in csproj comments
- **Fallback path:** Raw `zkfp2` P/Invoke documented in `.planning/phases/02-multi-vendor-scanner-adapters/02-RESEARCH.md` §5 Option A
- **Adapter file:** `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs`
- **Static teardown quirk:** `ZkTecoFingerHost.Close()` is called once globally at service shutdown only — never from individual `Dispose()` (multi-instance pattern)

### SecuGen (FDx SDK Pro)

- **SDK:** `SecuGen.FDxSDKPro.Windows.dll` (managed wrapper) + native `sgfplib.dll` + `sgfpamx.dll`
- **Reference style:** Direct `<Reference Include="SecuGen.FDxSDKPro.Windows">` with `HintPath` to `lib\SecuGen\` (`FingerprintAgent.csproj:33-37`)
- **Wrapper API:** `SGFingerPrintManager.Init/OpenDevice/EnumerateDevice/GetImageEx`
- **Models:** Hamster Pro 20, Hamster IV (FDU04), Hamster III (FDU03), Hamster Plus, Hamster II (per `SCANNER_SETUP.md:62`)
- **Install:** Manual — SecuGen FDx SDK Pro download (free registration, evaluation-only license flagged in `SCANNER_SETUP.md:90`)
- **Adapter file:** `src/FingerprintAgent/Adapters/SecuGenAdapter.cs`
- **Stub types:** When DLL missing, internal stub types (`SGFingerPrintManager`, `SGFPMDeviceName`, etc.) defined at top of file allow compilation without hardware

### DigitalPersona (U.are.U SDK)

- **SDK:** `DPUruNet` NuGet v1.0.0.1 (managed wrapper) + native `dpfpdd.dll` + `dpfj.dll` + managed `DPFPDevNET.dll`, `DPFPCapture.dll`
- **Wrapper API:** `DPFP.ReaderCollection`, `DPFP.Capture.Capture`, `CaptureEventHandler` callback interface (`DigitalPersonaAdapter.cs:201-226`)
- **Models:** U.are.U 4500, 4500B, 5160, 5300 (per `SCANNER_SETUP.md:97`)
- **Install:** Manual — DigitalPersona SDK from HID Global developer portal (registration required, per `Setup-VendorSdk.ps1:196-207`)
- **Adapter file:** `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs`
- **Async pattern:** Callback-based native API bridged to async via `TaskCompletionSource<bool>` with `RunContinuationsAsynchronously` to avoid blocking the native callback thread (`DigitalPersonaAdapter.cs:95-138`)

### Futronic (Standard SDK v4.2)

- **SDK:** `ftrScanAPI.dll` (native, 32-bit only) — direct P/Invoke, **no NuGet wrapper**
- **Import style:** `[DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Cdecl)]` (`FutronicAdapter.cs:197-219`)
- **API:** `ftrScanOpenDevice`, `ftrScanCloseDevice`, `ftrScanGetImageSize`, `ftrScanGetImage`, `ftrScanGetSerialNumber`, `ftrScanGetLastError`
- **Models:** FS80, FS90, FS60, FM200u (per `SCANNER_SETUP.md:128`)
- **Install:** Manual — Futronic Standard SDK v4.2 download (registration required)
- **Adapter file:** `src/FingerprintAgent/Adapters/FutronicAdapter.cs`
- **Pixel inversion quirk:** Raw SDK output is inverted grayscale; adapter applies `255 - rawValue` per pixel before PNG encoding (`FutronicAdapter.cs:115-118`) — flagged as research-based assumption, not vendor-documented

## Authentication & Identity

**Auth Provider:** None. There is no authentication layer in the agent.
- The HTTP listener binds to `127.0.0.1` only (`config.json http.host`), so any caller must already be local on the workstation
- Windows Service runs under a configured service account (set at install time via `sc.exe`); not enforced at the API layer
- CORS provides browser-side origin gating (`Api/CorsMiddleware.cs`) but no cryptographic auth
- The angular/Angular HIS frontend calls the agent as a "local user-agent" — same trust level as a USB token signing app

## Monitoring & Observability

**Error Tracking:** None external. Errors logged locally only.

**Logs:**
- **File sink:** `C:\ProgramData\FingerprintAgent\Logs\agent.log` (configurable). Custom writer in `Logging/AgentLogger.cs`. Log level filter (`DEBUG`/`INFO`/`WARN`/`ERROR`).
- **Windows Event Log sink:** `EventLog.WriteEntry("FingerprintAgent", ...)` (`Logging/AgentLogger.cs:162`) — secondary parallel sink. Source `FingerprintAgent` registered at install via `Install-Service.ps1:64-67`.
- **Format:** `"{timestamp} [{LEVEL}] [{correlationId}] {message}"` where correlation IDs are 10-char GUID prefixes generated per request (`AgentLogger.GenerateCorrelationId()`, `Logging/AgentLogger.cs:53-56`).
- **SEC-04 redaction:** Base64 strings of 40+ chars in log messages are auto-redacted to `[REDACTED: potential image data]` (`Logging/AgentLogger.cs:114-128`) to prevent leaking fingerprint PNG data into logs.
- **Health monitor:** A 30-second timer in `FingerprintAgentService.HealthCheckCallback` (`Service/FingerprintAgentService.cs:202-226`) logs warnings if scanner disconnects.

**No metrics / tracing / APM.** No Prometheus, OpenTelemetry, or Application Insights integration.

## CI/CD & Deployment

**Hosting:** On-premise Windows Service on each hospital workstation. No centralized hosting.

**CI Pipeline:** None. `.github/workflows/` is absent (per AGENTS.md "No GitHub Actions CI/CD"). Tests run locally via `dotnet test`.

**Deploy/Install scripts (`scripts/`):**
- `Install-Service.ps1` — admin install via `sc.exe create`, registers EventLog source, sets service recovery actions
- `Uninstall-Service.ps1` — admin uninstall
- `Service.ps1` — start/stop/restart/status shortcuts
- `Test-Capture.ps1` — calls `GET /health` and `POST /api/capture` as smoke test
- `Setup-VendorSdk.ps1` — locates/copies vendor native DLLs from installed SDKs on the machine
- `scripts/diagnostic/Test-ZK9500.ps1` and `Test-ZK9500-Timing.ps1` — vendor-specific diagnostic PowerShell

**MSI Installer:** Flagged in AGENTS.md as "not yet implemented" — current install path is the PowerShell `Install-Service.ps1`.

## Environment Configuration

**Required env vars:** None. All configuration is file-based (`config.json`).

**Optional env vars:**
- `FA_CONSOLE_TIMEOUT` (seconds) — auto-shutdown timer for `--console` mode, used in CI smoke tests (`src/FingerprintAgent.Host/Program.cs:50-52`)

**Secrets location:** None. The agent does not store credentials. Service-account credentials for the Windows Service are managed by `sc.exe` (Windows SCM), not by the agent.

## Webhooks & Callbacks

**Incoming:** None. The agent is purely HTTP request/response.

**Outgoing:** None. No callbacks, no webhooks, no push notifications. The only "callback" pattern is internal: `DigitalPersonaAdapter` implements the `CaptureEventHandler` interface (`Adapters/DigitalPersonaAdapter.cs:201-226`) to receive `OnComplete`/`OnFingerTouch` events from the native DPUruNet SDK — this is intra-process, not a network callback.

## OS-Level Integrations

| Integration | Where | Purpose |
|---|---|---|
| Windows Service Control Manager | `FingerprintAgentService : ServiceBase` + `Install-Service.ps1` (sc.exe) | Service lifecycle, recovery actions, auto-start |
| Windows Event Log | `AgentLogger.TryWriteEventLog` (`Logging/AgentLogger.cs:158`) | Operator-visible log sink |
| File System Watcher | `ConfigFileWatcher.cs` | Hot-reload of `config.json` |
| File I/O | `AgentLogger.cs` (append-mode `FileStream`) | Rolling log file |
| GDI+ Bitmap | `System.Drawing.Bitmap` (`BaseScannerAdapter.cs:88`, `ZKTecoAdapter.cs:282`, `FutronicAdapter.cs:146`, `MockScannerAdapter.cs:90`) | PNG encoding from 8-bit grayscale raw sensor data |
| `HttpListener` | `System.Net.HttpListener` (`Api/HttpServer.cs:15`) | HTTP server loop |

## Vendor SDK DLL Placement

Per D-08, all native DLLs must be in the same folder as `FingerprintAgent.exe` at runtime:

| Vendor | Required DLLs in install dir |
|---|---|
| ZKTeco | `libzkfpcsharp.dll`, `libzkfp.dll` |
| SecuGen | `SecuGen.FDxSDKPro.Windows.dll`, `sgfplib.dll`, `sgfpamx.dll` |
| DigitalPersona | `DPFPDevNET.dll`, `DPFPCapture.dll`, `dpfpdd.dll`, `dpfj.dll` |
| Futronic | `ftrScanAPI.dll` |

`scripts/Setup-VendorSdk.ps1` automates locating these DLLs from standard install paths (`C:\Program Files\...\`, `C:\Windows\SysWOW64\`) and copying them to `lib\<Vendor>\` for build-time reference, and to the install dir for runtime.

---

*Integration audit: 2026-08-19*
