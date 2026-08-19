---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: Release
status: complete
stopped_at: Phase 04 complete — code review pending
last_updated: "2026-08-19T18:30:00.000Z"
progress:
  total_phases: 4
  completed_phases: 4
  total_plans: 15
  completed_plans: 15
  percent: 100
---

# State: FingerprintAgent

## Project Reference

See: `.planning/PROJECT.md` (updated 2026-07-28)

**Core value:** Agent luôn sẵn sàng trên máy bệnh viện, kết nối được ít nhất một trong các máy quét vân tay phổ biến, và trả về ảnh PNG đáng tin cậy cho ứng dụng web qua HTTP API địa phương.
**Current focus:** Phase 04 — planning available

## Current Phase

**Phase 4: Deployment & End-to-End Validation** ✓ COMPLETE

- Status: ◆ All 4 plans executed successfully (24 atomic commits)
- Goal: Production-grade MSI installer, GitHub Releases auto-update, E2E Playwright tests, deployment docs.
- Deliverables: ConfigMerger + ProgramData migration (D-33/34/35/36/37); MSI + WiX CustomActions + Vietnamese localization + GitHub Actions release workflow; UpdateCheckService with auto-backoff (D-13/14/15/17/43); Playwright E2E suite + README.md + DEPLOYMENT.md.

## Phase Progress

| Phase | Status | Plans | Progress |
|-------|--------|-------|----------|
| 1     | ●      | 5/5   | 100%     |
| 2     | ●      | 4/4   | 100%     |
| 3     | ●      | 3/3   | 100%     |
| 4     | ●      | 4/4   | 100%     |

## Plan 03-01 Completed

**Exponential Backoff + Health Check Loop**

- `ScannerManager` — `BackoffDelaysSeconds = {10,30,60,120}`, `_backoffStep/_backoffUntil/_backoffLock`; `InBackoff`/`BackoffStep` properties; `ApplyBackoff()` at all-adapter-failure exit; backoff resets on any `IsSuccess=true` capture; D-04 hot-plug retry (lines 146–170) preserved
- `FingerprintAgentService` — `System.Threading.Timer` fires every 30s, observes `IsConnected` only (D-17), logs warning with backoff step; disposed in own try-catch before `httpServer.Stop()` in `OnStop`
- `HealthHandler` — exposes `inBackoff`, `backoffStep`, `status`; returns HTTP 503 only when step=3 AND disconnected
- Release build 0 warnings / 0 errors

## Plan 03-02 Completed

**Config Reload (CFG-03)**

- `ConfigFileWatcher` — `FileSystemWatcher` + 300ms debounce timer; fires `ConfigReloaded(Action<AgentConfig>)` after validate parse; bad config keeps old config, logs error, no crash (D-08); disposal order: timer first then watcher
- `CorsMiddleware.UpdateConfig()` — thread-safe under `_corsLock`; replaces `_allowedOrigins` HashSet atomically
- `HttpServer.UpdateCorsConfig()` — integration point between `ConfigFileWatcher` and `CorsMiddleware`
- `ScannerManager.UpdatePriority()` — recreates adapter list under `_adapterLock`; active adapter and backoff state untouched (D-09)
- `FingerprintAgentService` — wires `ConfigFileWatcher` in `OnStart`, disposes in `OnStop` (own try-catch, before scanner); calls `UpdateCorsConfig` and `UpdatePriority` on reload
- Release build 0 warnings / 0 errors

## Plan 03-03 Completed

**Error Code Mapping + Tests**

- `CaptureResponse` — `VendorErrorCode` (JsonProperty "vendorErrorCode") and `Timestamp` (ISO 8601) fields added; null on success, populated on error
- `CaptureHandler` — `MapErrorCode()` method: `SCANNER_NOT_CONNECTED→503`, `CAPTURE_TIMEOUT→504`, `CAPTURE_FAILED→500`, `INVALID_REQUEST→400`; `WriteErrorResponse` includes `VendorErrorCode` + `Timestamp`
- `CaptureResult.Ok()` — new static factory method added; `ScannerManager` test constructor made public (InternalsVisibleTo issue)
- `MockScannerAdapterWithSettableProperties` — test double with settable `IsConnectedValue`, `InitializeResult`, `ScanResult`, `VendorErrorCodeValue`
- `CaptureHandlerTestFixture` — real `HttpListener`-based integration test fixture
- Backoff unit tests + in-flight fail tests in `ScannerManagerTests.ExponentialBackoff.cs`
- Error handling integration tests (503/504/500/400) in `ErrorHandlingTests.cs`
- Release build 0 warnings / 0 errors

## Plan 01-04 Completed

**Logging — File + EventLog + Structured Format**

- `AgentLogger` with file sink, EventLog sink, log level filtering, correlation IDs
- Structured log format: `YYYY-MM-DDTHH:MM:SS.ffffffZ [LEVEL] [correlationId] message`
- SEC-04 base64 redaction for messages that look like image data
- Wired logging into `FingerprintAgentService`, `HttpServer`, `HealthHandler`, `CaptureHandler`, `Program.cs`
- 11 new `AgentLoggerTests` (file creation, format regex, level filtering, correlation IDs, redaction, EventLog fallback, concurrency, directory creation)
- Release build 0 warnings / 0 errors; 35/35 tests pass; console smoke test produced structured log file
- 1 atomic commit for 01-04 code + tests, 1 docs commit

## Plan 01-03 Completed

**Windows Service Hosting + PowerShell Scripts**

- `FingerprintAgentService` extends `ServiceBase` with `OnStart`/`OnStop` lifecycle
- `Program.cs` dual-mode dispatch: `--service` for SCM, `--console`/interactive for debug
- `scripts/Install-Service.ps1` — admin check, idempotent install, EventLog source, log dir, failure recovery
- `scripts/Uninstall-Service.ps1` — admin check, idempotent removal
- `scripts/Test-Capture.ps1` — smoke tests `/health` and `/api/capture`
- EventLog writes wrapped for resilience in console/non-admin runs
- Release build 0 warnings / 0 errors; 24/24 tests pass; console `/health` smoke test returns 200
- 4 atomic commits for 01-03 (subagent failed; implemented inline)

## Plan 01-02 Completed

**Configuration + CORS + Error Responses**

- `AgentConfig` with 6 nested config classes, `ConfigLoader` reading `config.json` via `Microsoft.Extensions.Configuration.Json`
- `CorsMiddleware` with wildcard/allowlist modes, OPTIONS preflight (204/403)
- `HttpServer` and `Program.cs` wired with `AgentConfig` instead of hardcoded values
- `config.json` at project root with full schema (service, http, cors, scanner, logging, security)
- 5 ConfigLoader unit tests + 6 CORS integration tests — all 24 tests passing
- 6 atomic commits (RED→GREEN per task)

## Plan 01-01 Completed

**Walking Skeleton Core — Project Scaffold + HTTP Listener + Mock Capture**

- Solution + projects (.NET Framework 4.8, SDK-style csproj) created
- `IScannerAdapter` interface + `CaptureResult` DTO established
- `MockScannerAdapter` produces deterministic 320×240 PNG with SHA-256
- `HttpServer` on `http://127.0.0.1:5043/` with async GetContextAsync loop
- `GET /health` returns status/deviceId/uptime (200)
- `POST /api/capture` validates required fields, returns PNG+SHA-256 (200) or 400 on validation failure
- `Program.cs` dual-mode: `--console` for debugging, ServiceBase path for production
- 8 unit tests + 5 integration tests — all passing
- 3 atomic commits: test(01-01) RED → feat(01-01) GREEN → feat(01-01) HTTP

## Active Blockers

None. Phase 4 complete pending `/gsd-code-review --depth=deep`.

## Plan 04-04 Decisions (Wave 4: E2E + Docs)

- **Playwright 1.55.1 PINNED** (not 1.56+) — Chromium 142+ blocks public-origin fetch to private network by default. 1.55.1 works around this without `--ip-address-space-overrides`. Documented as migration note for 1.56+ future.
- **E2E project is TypeScript, separate from xUnit** — different runner, different dependency graph, different language. Playwright 1.55.1 + Chromium-only for v1 (no Firefox/WebKit).
- **`docs/` deleted** — `.planning/codebase/` is the single source of truth. Stale docs cause confusion; single source prevents drift.
- **NO CHANGELOG.md** — GitHub Releases IS the changelog (D-26). Operators point users at releases page, not a markdown file.
- **PS1 scripts preserved unchanged** — MSI is production path, PS1 is dev/test fallback (D-32). Both documented in README.
- **Removed `*.e2e` .gitignore rule** — it was case-insensitively matching the new `tests/FingerprintAgent.E2E/` directory. Added negation exceptions. No `.e2e` files exist in repo (rule was dead VS template code).

## Plan 04-03 Decisions (Wave 3: Auto-Update)

- **Single HttpClient reused** for both GitHub API + MSI download — avoids socket exhaustion under polling. Test injection via `internal UpdateCheckService(..., HttpMessageHandler handler)` overload.
- **Auto-backoff per D-15**: base 6h → 12h after 3 no-updates → 24h after another 3 → resets to 6h on detected release.
- **On download/install failure**: disable `update.enabled` in config.json (via ConfigMerger), write to log + EventLog (D-43), service keeps running on old version — never crash.
- **Manual AssemblyInfo.cs** with InternalsVisibleTo — SDK-style net48 doesn't auto-generate; needed for test injection of HttpMessageHandler.

## Plan 04-02 Decisions (Wave 2: MSI Installer)

- **WiX DTF 4.0.4 instead of 3.14.x** — 3.x not in NuGet feed; 4.0.4 is latest OSMF-free version. CustomAction namespace migrated to `WixToolset.Dtf.WindowsInstaller` with `extern alias` aliases preserving legacy code patterns.
- **Non-SDK .wixproj** — WixToolset.Sdk 3.x is not a NuGet package; WiX 4 SDK rejects v3 schema. CI workflow downloads WiX 3.14.1 binaries from `https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm`.
- **VC++ x86 detect-only (no bundling)** per D-09 — Chinese dialog shows aka.ms download URL. Deviation from ROADMAP SC #2 (which said "silent install"); D-09 is the locked decision.
- **`REMOVE_LOGS=1` escape hatch** for uninstall — log preservation is the default (D-28/29). Power users can opt in to log removal.

## Plan 04-01 Decisions (Wave 1: Config + ProgramData)

- **D-35 algorithm**: additive-only merge. User values preserved, user deletions respected, template keys ADD only (never re-add after user deletion).
- **DO NOT use `JObject.Merge()`** — its default REPLACES, not additive. Custom recursive walk is required.
- **`config.template.json` added** alongside `config.json` — both ship to bin output. MSI uses `config.template.json` (read-only reference); ProgramData holds the live user config.
- **Legacy fallback**: v1.0 install-dir `config.json` is COPIED to ProgramData on first upgrade (not overwritten by template) — preserves IT customizations across upgrade.
- **4-way case matrix in `ConfigLoader.Load()`** (not 3 as plan said) — added "ProgramData-only without template" case for dev workflow without MSI.

## Plan 01-02 Decisions

- Manual config binding (GetSection/Value) instead of IConfiguration.Get<T>() — avoids Binder extension issues on .NET Framework 4.8
- CorsMiddleware tested via real HttpServer + HttpClient because HttpListenerRequest/Response cannot be unit-tested in isolation
- HttpServer dual constructor for backward compatibility with Plan 01 integration tests
- Config load failure: prints fatal error to stderr and exits with code 1 (logger in Plan 04)

### Prior Decisions

- Windows Service chạy nền.
- .NET Framework 4.8, framework-dependent.
- Agent là HTTP server trên `localhost:5043`.
- CORS `allowedOrigins` configurable.
- Angular gọi trực tiếp agent (giống USB token signing agent).
- Không hỗ trợ Windows 7 32-bit chính thức.
- SDK-style .csproj với net48 target — works with .NET SDK 9.0 targeting .NET Framework 4.8.
- Integration tests start real HttpServer in-process with HttpClient.
- MockScannerAdapter tạo GDI+ objects per-call (không shared state).
- FingerprintAgentService giữ minimal stub; full SCM lifecycle ở Plan 03.

---
*State created: 2026-07-28*
*Last updated: 2026-08-19 after Phase 4 completion*

## Session

**Last session:** 2026-08-19T18:30:00.000Z
**Stopped at:** Phase 04 complete — code review pending
**Next:** Run `/gsd-code-review --depth=deep 4` for post-implementation review
