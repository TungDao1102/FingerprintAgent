# Walking Skeleton — FingerprintAgent

**Phase:** 1
**Generated:** 2026-07-28

## Capability Proven End-to-End

"A developer can build, run (as console), and verify that the agent responds to `GET /health` with service status and `POST /api/capture` with a mock PNG + SHA-256 hash, driven by `config.json`, with CORS headers and structured logs — all on a Windows 10/11 machine with .NET Framework 4.8."

## Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| HTTP stack | `System.Net.HttpListener` (raw, no OWIN/ASP.NET) | D-01: Avoid heavy dependencies. Agent has 2 endpoints. Pattern matches existing USB token signing agent. |
| Default bind | `127.0.0.1:5043` | D-02: Loopback only by default (SEC-01). Configurable via config.json for LAN testing. |
| PNG generation | `System.Drawing` (GDI+) in memory | D-03: In-box on .NET Framework 4.8, no NuGet. Pure memory ops work in LocalSystem service context. |
| Logging | `System.Diagnostics.Trace` + `EventLog.WriteEntry` (custom wrapper, no Serilog/NLog) | D-04: Keep package count minimal. Structured format `[timestamp] [LEVEL] [correlationId] message`. |
| Log file path | `C:\ProgramData\FingerprintAgent\Logs\agent.log` | D-05: Standard ProgramData location, persistent across updates. |
| Service host | `ServiceBase` pure subclass (no Topshelf) | D-06: Native Windows Service API. Compatible with MSI installer (Phase 4). |
| Install method | PowerShell scripts (dev); MSI (Phase 4) | D-07: Scripts suffice for development and IT-provisioned machines. MSI for end-user convenience. |
| Project structure | Single `.csproj` for Phase 1 | D-08: Architecture not yet stable. Folders-as-boundaries: `Adapters/`, `Api/`, `Configuration/`, `Logging/`, `Service/`, `Models/`. |
| DI / Config | `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Configuration.Json` via NuGet | D-10/D-11: Familiar pattern, enables `IOptions<T>` for Phase 3 reload. NuGet v8.0.x tested on .NET Framework 4.8. |
| CORS default | `Access-Control-Allow-Origin: *` (wildcard) | D-12: Matches existing USB token signing agent pattern. Allowlist mode available via config. |
| Request schema | JSON with `thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`, `metadata` | D-14: Matches HIS frontend contract. Vietnamese field names from existing integration. |
| Response schema | JSON with `isSuccess`, `imageBytes` (base64 PNG), `verificationData` (SHA-256 base64), `capturedAt`, `deviceId`, `mimeType`, `errorMessage` | D-15: Single response DTO for both success and error. |
| Mock scanner | `MockScannerAdapter` — deviceId `mock-scanner-001`, 320×240 PNG gradient | D-16: Deterministic output (same input → same SHA-256). Enables test assertions. |
| Test framework | xUnit + Moq | Industry standard for .NET Framework 4.8. Moq for interface mocking in later phases. |
| JSON serialization | Newtonsoft.Json 13.0.3 | Most compatible with .NET Framework 4.8. Used for request/response DTOs. |

## Stack Touched in Phase 1

- [x] **Project scaffold** — `.sln` + `.csproj` targeting `net48`, NuGet packages (ME.DI 8.0.1, ME.Config.Json 8.0.0, ME.Config.Binder 8.0.2, Newtonsoft.Json 13.0.3)
- [ ] **HTTP routing** — `GET /health` and `POST /api/capture` via raw `HttpListener`
- [ ] **Configuration** — `config.json` read at startup via `ConfigurationBuilder`, strongly-typed `AgentConfig`
- [ ] **CORS** — `CorsMiddleware` with wildcard/allowlist modes, OPTIONS preflight handling
- [ ] **Mock scanner** — `IScannerAdapter` interface + `MockScannerAdapter` with deterministic PNG + SHA-256
- [ ] **Error handling** — HTTP 400 for invalid requests, 503 for scanner disconnected (structured for Phase 3), 404 for unknown routes
- [ ] **Windows Service** — `FingerprintAgentService` (ServiceBase), Program.cs dual-mode (`--service` / `--console`)
- [ ] **Logging** — File + EventLog with structured format `[timestamp] [LEVEL] [correlationId] message`, correlationId per request
- [ ] **PowerShell scripts** — `Install-Service.ps1`, `Uninstall-Service.ps1`, `Test-Capture.ps1`
- [ ] **Unit tests** — xUnit project with tests for MockScannerAdapter, ConfigLoader, CorsMiddleware, Logger

## Out of Scope (Deferred to Later Slices)

- Real scanner adapters (SecuGen, Digital Persona, Futronic) → Phase 2
- Config hot-reload via FileSystemWatcher → Phase 3
- Scanner reconnect / exponential backoff → Phase 3
- Capture timeout handling → Phase 3
- MSI installer → Phase 4
- Auto-update from GitHub Releases → Phase 4
- WebSocket/polling mode → v1.1+
- Image quality scoring → v1.1+
- Multi-scanner / concurrent capture → v1.1+
- API key / JWT authentication → v1.1+

## Subsequent Slice Plan

- **Phase 2:** Multi-vendor Scanner Adapters — real SecuGen, Digital Persona, Futronic adapters, ScannerManager selection logic
- **Phase 3:** Resilience & Runtime Reconfiguration — config hot-reload, scanner reconnect, capture timeout, error code mapping
- **Phase 4:** Deployment & End-to-End Validation — MSI installer, Test-Capture.ps1 refinement, integration test from browser → agent, deployment docs
