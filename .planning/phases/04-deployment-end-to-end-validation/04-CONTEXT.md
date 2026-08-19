# Phase 4: Deployment & End-to-End Validation - Context

**Gathered:** 2026-08-19
**Status:** Ready for planning

<domain>
## Phase Boundary

Phase 4 closes the v1 loop with production-ready deployment:
- **MSI installer** (WiX Toolset) replacing PowerShell-script-only install for hospital IT
- **VC++ x86 runtime detection** as pre-install gate (no bundling; clear Vietnamese error if missing)
- **Auto-update mechanism** (in-service Timer + GitHub Releases) shipped DISABLED by default — operator opt-in for initial rollout / hotfix period
- **E2E browser validation** via Playwright (real Chromium → real CORS preflight → real /api/capture → mock backend receipt)
- **Documentation surface**: `README.md` (combined dev+IT) + `DEPLOYMENT.md` (Vietnamese operations runbook) — both live in GitHub repo, NOT bundled in MSI
- **MSI behaviors**: hard-coded paths, smooth in-place upgrade, /health self-test post-install, graceful uninstall preserving logs by default, Programs and Features "Update" verb for manual updates
- **Config.json lives at `C:\ProgramData\FingerprintAgent\config.json`** (writable without admin, survives upgrade via smart merge)

This phase **does NOT** add new fingerprint functionality, new adapters, or new HTTP endpoints. It packages and validates everything Phases 1-3 built.

Out of scope (deferred to Phase 5+):
- Code signing certificate (EV cert for SmartScreen bypass)
- Advanced auto-update (delta updates, rollback, channel preview/stable)
- Multi-scanner / deviceId routing
- Polling/WebSocket mode for backend SaaS
- ANSI/ISO template conversion

</domain>

<decisions>
## Implementation Decisions

### MSI Toolchain & Build Pipeline

- **D-01:** **WiX Toolset (XML)** chosen as the MSI authoring toolchain. XML-based `.wxs` source files compiled with `candle` + `light`. Industry standard with abundant documentation.
- **D-02:** **GitHub Actions workflow only** — no local `Build-Msi.ps1` script. CI builds MSI when a tag is pushed. Local devs run via `dotnet build` + existing PS1 scripts.
- **D-03:** **Smooth in-place upgrade** via WiX `<MajorUpgrade AllowSameVersionUpgrades="yes">`. Stop service → copy files → start service. `C:\ProgramData\FingerprintAgent\Logs\` is preserved across upgrades.
- **D-04:** **Hard-coded paths** in MSI: `C:\Program Files\FingerprintAgent\` for binaries + `C:\ProgramData\FingerprintAgent\` for runtime config/logs. No install-dir dialog. `C:\ProgramData\FingerprintAgent\Logs\` already used by `AgentLogger`.
- **D-05:** **MSI validates via /health ping after install** — start service, wait up to 5s for it to become "running", GET `http://127.0.0.1:5043/health`. If 200 → install completes; if non-200 → MSI rolls back.
- **D-06:** **C# CustomAction DLL** (`FingerprintAgent.Installer.dll`) for all custom actions. Uses `Microsoft.Deployment.WindowsInstaller` namespace + System.Net.Http.HttpClient. Same .NET Framework 4.8 the agent uses.
- **D-07:** **MSI is unsigned in v1**. Signing deferred to Phase 5+ per PROJECT.md "Code signing certificate" entry. CI build script leaves placeholder for future `signtool sign` invocation.
- **D-08:** **Supports `msiexec /qn` fully silent** install for Group Policy / SCCM deployment. Default UI is basic welcome/finish for interactive use.

### VC++ x86 Redistributable

- **D-09:** **Detect only — error message if missing (no bundling)**. Hospital workstations are expected to have VC++ x86 redist pre-installed (common baseline dep). If missing, MSI shows Vietnamese error dialog pointing to `https://aka.ms/vs/17/release/vc_redist.x86.exe`.
- **D-10:** **Detection via C# CustomAction DLL** — registry probe `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86` for `Installed=1`. Reuses the CustomAction DLL from D-06.
- **D-11:** **Vietnamese error dialog** for VC++ missing (matches hospital IT audience). Bilingual not needed for this single-purpose error.
- **D-12:** **Require VS 2015-2022 only** (`vcruntime140.dll` + `msvcp140.dll`). Forward-compatible with all four vendor SDKs (ZKTeco, SecuGen, DigitalPersona, Futronic). Single download URL simplifies IT.

### Auto-Update (v1)

- **D-13:** **Auto-update architecture stays in code** — `System.Threading.Timer` inside `FingerprintAgentService` (survives hibernate/sleep where `OnStart` would not fire). Timer polls `https://api.github.com/repos/{owner}/FingerprintAgent/releases/latest` and compares semver.
- **D-14:** **Default disabled** via `config.json` `update.enabled: false`. Service ships lightweight. Operator opts in for initial rollout / hotfix period; disables again once stable.
- **D-15:** **Configurable interval + auto-backoff** when no updates available:
  - `update.checkIntervalHours` — base interval (default 6)
  - 3 consecutive no-update checks → interval extends 6h → 12h → 24h (capped)
  - Detected new release → reset to base interval immediately
- **D-16:** **GitHub Releases is the source of truth**. No `CHANGELOG.md` file. Operator manages release notes on GitHub.
- **D-17:** **Update flow**: GET releases/latest → compare semver → if newer, download MSI asset to `%TEMP%\FingerprintAgent-Setup.msi` → run `msiexec /qn <path>` → service auto-restarts via SCM recovery. Old version's MSI is NOT cached for rollback (out of scope for Phase 4).

### Programs and Features Integration

- **D-18:** **MSI registers entry in Control Panel → Programs and Features** (automatic via WiX). Add **Update verb** that triggers the in-service update check immediately (same code path as auto-update, but operator-initiated). Standard Repair/Uninstall verbs also present.
- **D-19:** **Service registration via WiX `<ServiceInstall>` element** (not custom action). `StartType=Automatic`, `Account=LocalSystem`, failure actions: `restart/5000/restart/10000/restart/30000` (matches existing `sc.exe failure` config in `Install-Service.ps1`).

### E2E Validation Surface

- **D-20:** **Playwright (Node.js + Chromium) full E2E** — real browser opens HTML page (mock SaaS domain via `file://` or local stub server), JS does `fetch('http://localhost:5043/api/capture', ...)`, validates real CORS preflight + capture response + mock backend receipt.
- **D-21:** **Separate test project** at `tests/FingerprintAgent.E2E/` with own `package.json` + `playwright.config.ts`. CI runs `npm install && npx playwright test` after agent is built.
- **D-22:** **Full E2E coverage** verifies (a) OPTIONS preflight returns 204 + valid CORS headers, (b) POST /api/capture returns 200 + valid PNG, (c) result is forwarded to mock backend.
- **D-23:** **Manual trigger via `workflow_dispatch`** in GitHub Actions UI. Not auto-run on every push (Playwright is heavy ~10-15 min per run). Operator runs on demand before tagging a release.

### Documentation Surface

- **D-24:** **`README.md` combined for both audiences** (devs + IT). Sections clearly labeled "For Developers" and "For Hospital IT". Lives at repo root.
- **D-25:** **`DEPLOYMENT.md` is a full operations runbook** in Vietnamese. Sections: prerequisites, install steps, silent install, /health verification, update procedure (manual + auto when enabled), uninstall, troubleshooting FAQ, log locations, EventLog source, registry entries.
- **D-26:** **No `CHANGELOG.md` file** — relies on GitHub Releases notes.
- **D-27:** **Delete `docs/` folder**. Its contents are stale (per STRUCTURE.md note: "docs/ARCHITECTURE.md references Kestrel/OWIN which are no longer accurate"). `.planning/codebase/` is the current source of truth.

### MSI Uninstall Behavior

- **D-28:** **Preserve logs, remove everything else** by default. MSI uninstall removes service registration, exe + DLLs in `C:\Program Files\FingerprintAgent\`, EventLog source `FingerprintAgent`. PRESERVES `C:\ProgramData\FingerprintAgent\Logs\` for forensics. Preserves `C:\ProgramData\FingerprintAgent\config.json` (handled by smart merge — see D-33).
- **D-29:** **Force-clean option via `msiexec /x FingerprintAgent.msi REMOVE_LOGS=1`** — when set, uninstall also removes `C:\ProgramData\FingerprintAgent\Logs\`. Standard uninstall preserves logs.
- **D-30:** **Graceful service stop (30s wait)** before uninstall. MSI sends stop via sc.exe, waits for in-flight `/api/capture` requests to complete, then deletes service + files.
- **D-31:** **Delete EventLog source `FingerprintAgent` on uninstall**. Clean registry state.

### PowerShell Script Evolution

- **D-32:** **Keep all 5 PS1 scripts as dev/test fallback**:
  - `Install-Service.ps1` — dev machine install via sc.exe (no MSI locally)
  - `Uninstall-Service.ps1` — dev machine uninstall via sc.exe
  - `Service.ps1` — start/stop/restart/status
  - `Setup-VendorSdk.ps1` — dev convenience to copy vendor DLLs from system locations into `lib/`
  - `Test-Capture.ps1` — dev smoke test
  - README.md documents role split: PS1 = dev/test, MSI = production IT.

### Vendor SDK DLL Distribution

- **D-32a:** **Do NOT bundle vendor SDK DLLs in the release MSI**. Production workstations must have the vendor's driver installed first (DLLs placed in `C:\Windows\SysWOW64\` or `C:\Program Files\<Vendor>\` by the vendor installer). Windows DLL search path handles resolution at runtime.
- **D-32b:** **`Setup-VendorSdk.ps1` stays manual** for dev convenience only (copies DLLs from system locations into `lib/` so project builds activate all adapter `<DefineConstants>`).

### Config.json Location & Merge

- **D-33:** **Config lives at `C:\ProgramData\FingerprintAgent\config.json`** (not `C:\Program Files\...`). Writable without admin. Survives upgrade. `C:\ProgramData\FingerprintAgent\Logs\` already follows this convention.
- **D-34:** **MSI seeds ProgramData config only on first install** (when file doesn't exist). MSI also copies default config.json to `C:\Program Files\FingerprintAgent\config.template.json` (read-only reference for IT).
- **D-35:** **Smart merge on upgrade**: `ConfigMerger` class reads new template + existing user config. For each key in new template:
  - Key not in user config → add with template default
  - Key in user config → keep user's value
  - User deletion respected (deleted keys stay deleted)
- **D-36:** **`ConfigLoader.cs` needs path update** — currently reads from `AppDomain.CurrentDomain.BaseDirectory`; needs to read from `%ProgramData%\FingerprintAgent\config.json` first, fall back to install-dir template if missing.
- **D-37:** **`ConfigFileWatcher` watches the ProgramData path**, not the install-dir path.

### First-Run Experience

- **D-38:** **Auto-start service + Vietnamese success dialog** post-install. MSI installs files + registers service with Auto Start, starts service immediately, pings /health.
  - `/health` returns 200 → dialog: "Cài đặt thành công! Dịch vụ đang chạy."
  - `/health` returns 503 with scanner-disconnected code → dialog: "Cài đặt thành công nhưng chưa phát hiện máy quét. Cắm máy quét và đợi 30 giây."
  - `/health` fails for other reason (port in use, service not started) → generic error with link to DEPLOYMENT.md troubleshooting.
- **D-39:** **Different dialogs for fresh install vs upgrade**:
  - Fresh install: "Cài đặt thành công. Dịch vụ FingerprintAgent đã sẵn sàng."
  - Upgrade: "Đã cập nhật lên phiên bản vX.Y.Z."
- **D-40:** **Version from `AssemblyVersion` + MSI `ProductVersion`** (synced from same source in `FingerprintAgent.Host.csproj`).

### Update Notification UX (when auto-update ENABLED)

- **D-41:** **Windows toast before + after install**. Pre-install: "FingerprintAgent đang cập nhật phiên bản mới..." with 10s delay. Post-install: "Đã cập nhật lên vX.Y.Z thành công."
- **D-42:** **10-second pre-install delay** (fixed). Long enough to read, short enough to feel responsive.
- **D-43:** **On update failure**: error toast "Cập nhật thất bại. Vui lòng liên hệ IT." + Error-level log entry + Error EventLog entry + auto-update disabled (`update.enabled = false` written to config.json).
- **D-44:** **Always attempt toast**; Windows handles no-user-session case (toast silently doesn't display if no logged-in user). Service still installs.
- **D-45:** **Auto-update flows are silent in default config** (because `update.enabled: false`). Operator opts in by editing config.json + restarting service.

### Agent's Discretion

- WiX `<MajorUpgrade>` schedule attribute (`afterInstallInitialize` vs `afterInstallExecute`) — pick whichever produces smoother upgrade UX
- Custom action sequencing (when in the MSI lifecycle to run VC++ check, /health probe, etc.)
- Smart-merge implementation details (recursive vs flat merge, JSON serialization approach)
- Programs and Features icon (use existing FingerprintAgent.ico if available, else default Windows icon)
- WiX UI level (`WixUI_Minimal` vs `WixUI_InstallDir` vs custom)

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project Context
- `.planning/PROJECT.md` — Core value, business context, key decisions (esp. "Code signing certificate" deferred to Phase 5+), out-of-scope items
- `.planning/REQUIREMENTS.md` §DEP-01 through DEP-04 — Deployment requirements (DEP-01: install package contents, DEP-02: install script, DEP-03: uninstall script, DEP-04: smoke test). Note: existing PS1 scripts already cover DEP-02/03/04; MSI is the new DEP-01 deliverable.
- `.planning/ROADMAP.md` §Phase 4 — Goal, success criteria, deliverables (MSI, install/uninstall scripts, E2E test, README+DEPLOYMENT, auto-update)
- `.planning/STATE.md` — Phase 4 plan count 0/4 (currently being planned)

### Prior Phase Decisions (carry forward)
- `.planning/phases/01-foundation-windows-service-http-api-skeleton/01-CONTEXT.md` — D-06 ServiceBase, D-12 CORS wildcard default, D-13 allowlist mode
- `.planning/phases/02-multi-vendor-scanner-adapters/02-CONTEXT.md` — D-05 x86 only, D-08 SDK DLLs in install directory (overridden by D-32a for production), D-09 ZKTeco NuGet
- `.planning/phases/03-resilience-runtime-reconfiguration/03-CONTEXT.md` — D-15 health check timer, D-13 timeout enforcement at ScannerManager, D-08 keep old config on bad config

### Existing Code Insights
- `src/FingerprintAgent/Adapters/IScannerAdapter.cs` — Adapter contract (no changes)
- `src/FingerprintAgent/Adapters/ScannerManager.cs` — Priority fallback + backoff (no changes)
- `src/FingerprintAgent/Api/HttpServer.cs` — HTTP loop on `127.0.0.1:5043` (used by D-05 /health probe)
- `src/FingerprintAgent/Configuration/AgentConfig.cs` — Config POCO with 6 nested sections; needs new `UpdateConfig` POCO for D-14
- `src/FingerprintAgent/Configuration/ConfigLoader.cs` — Needs path update per D-36; needs to call ConfigMerger on first load
- `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs` — Watch path needs update per D-37
- `src/FingerprintAgent/Service/FingerprintAgentService.cs` — Needs new Timer for auto-update (D-13); needs new CustomAction DLL is separate project
- `src/FingerprintAgent.Host/Program.cs` — Active exe entry; AssemblyVersion used for D-40 dialog version display
- `src/FingerprintAgent.Host/FingerprintAgent.Host.csproj` — AssemblyVersion source for D-40
- `scripts/Install-Service.ps1` — Reference for existing service registration params; will be preserved as dev fallback per D-32
- `scripts/Uninstall-Service.ps1` — Reference for existing uninstall; preserved per D-32
- `scripts/Service.ps1` — start/stop/restart/status; preserved as-is
- `scripts/Setup-VendorSdk.ps1` — Dev convenience; preserved per D-32b
- `scripts/Test-Capture.ps1` — Dev smoke; preserved per D-32

### External References
- WiX Toolset documentation: https://wixtoolset.org/docs/ (specifically `WixUI_Minimal`, `<MajorUpgrade>`, `<ServiceInstall>`, `<CustomAction>`, `<Binary>`)
- Microsoft.Deployment.WindowsInstaller namespace: Custom action entry point signature
- GitHub Releases API: https://docs.github.com/en/rest/releases/releases#get-the-latest-release
- VC++ 2015-2022 redistributable registry key: `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86` with `Installed` DWORD
- Playwright docs: https://playwright.dev/dotnet/docs/intro (or JS variant since CI uses Node.js)

No vendor-specific specs needed beyond what's already in `.planning/phases/02-multi-vendor-scanner-adapters/02-RESEARCH.md`.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **HttpServer.cs** (existing): Real `HttpListener` on `127.0.0.1:5043` with `/health` endpoint. D-05 /health probe uses it directly.
- **AgentLogger.cs** (existing): Structured logger with file + EventLog sinks, correlation IDs. Reused by CustomAction DLL for VC++ check error logging.
- **AgentConfig.cs** (existing): 6 nested POCO sections. New `UpdateConfig` POCO added for D-14/D-15 fields.
- **FingerprintAgentService.cs** (existing): Owns Timer, ServiceBase lifecycle. New `UpdateCheckTimer` added for D-13.
- **ConfigLoader.cs** (existing): Loads JSON via `Microsoft.Extensions.Configuration.Json`. Path needs update per D-36; merge logic added per D-35.
- **GitHub Actions runner**: Implicit (D-02). Workflow file `.github/workflows/release.yml` to be created — currently absent per AGENTS.md "No CI/CD".
- **WiX standard UI**: `WixUI_Minimal` reference built into WiX — covers welcome + finish dialogs without custom XAML.

### Established Patterns
- **xUnit 2.9.3 + Moq 4.20.72** in test project (per STACK.md). Phase 4 adds separate Node.js project (D-21) for E2E — NOT mixed into xUnit project.
- **JSON via Newtonsoft.Json** throughout (per STACK.md). Config file, HTTP responses all use `[JsonProperty]` attributes.
- **Result factories**: `CaptureResult.Ok()` / `CaptureResult.Fail()` — same pattern for ConfigMerger result or similar.
- **Correlation IDs**: 10-char hex, regex `^[a-f0-9]{10}$`. Reuse for update-check flow.
- **AgentLog methods**: `_logger?.Info(...)` — null-safe; CustomAction DLL can use same pattern.

### Integration Points
- **MSI → Agent process**: CustomAction DLL calls service start/stop via `sc.exe` (or `ServiceController` API)
- **CustomAction DLL → /health**: HTTP GET via `System.Net.Http.HttpClient`
- **CustomAction DLL → AgentLogger**: Same logging semantics; writes to `C:\ProgramData\FingerprintAgent\Logs\installer.log`
- **Auto-update Timer → GitHub Releases API**: HTTPS GET via `HttpClient`
- **Auto-update Timer → MSI trigger**: `Process.Start("msiexec", "/qn <path>")` then `Environment.Exit(0)` after delay; SCM recovery restarts service
- **MSI upgrade → ConfigMerger**: CustomAction invokes `ConfigMerger.Merge(template, userConfig, output)` before service starts
- **MSI uninstall → log preservation**: WiX `<Component>` with `Permanent="yes"` for log dir; or CustomAction to skip removal of ProgramData\Logs
- **Programs and Features Update verb**: WiX `<Verb>` element in `<Interface>` block
- **E2E Playwright → Agent**: Spin up agent process in CI, run `msiexec /qn` to install, then Playwright hits `http://127.0.0.1:5043`

### Extension Points
- New `src/FingerprintAgent.Installer/` project for C# CustomAction DLL
- New `installer/` directory at repo root for WiX `.wxs` source files + `WixProj` references
- New `.github/workflows/release.yml` for CI build
- New `tests/FingerprintAgent.E2E/` directory for Playwright specs
- Modify `src/FingerprintAgent/Configuration/ConfigLoader.cs` for new ProgramData path
- Modify `src/FingerprintAgent/Configuration/AgentConfig.cs` to add `UpdateConfig` POCO
- Modify `src/FingerprintAgent/Service/FingerprintAgentService.cs` to add UpdateCheckTimer
- Modify `src/FingerprintAgent.Host/FingerprintAgent.Host.csproj` to expose AssemblyVersion for dialog

</code_context>

<specifics>
## Specific Ideas

- **WiX Burn vs raw MSI**: explicitly rejected Burn (D-09 decision "no bundling"). Raw MSI only.
- **Smart merge UX hint**: when merge adds new keys, optionally write a `merge.log` to ProgramData showing "Added: update.enabled=false" so IT can see what changed.
- **Auto-update version comparison**: use `System.Version` parse; treat `tag_name` like `v1.2.3` → `1.2.3`. Pre-release tags (`v1.2.3-rc1`) ignored by default.
- **Playwright mock SaaS page**: simple static HTML file in `tests/FingerprintAgent.E2E/fixtures/saas-page.html` with embedded JS doing fetch.
- **Playwright mock backend**: simple `http.createServer` in test fixture that listens on random port, captures POST body, returns 200.
- **First-run dialog Vietnamese strings**: defined in CustomAction DLL resources (`.resx` file) so they can be edited without recompiling XAML.
- **Log file naming**: `agent.log` for runtime, `installer.log` for MSI custom actions, `update.log` for auto-update events.
- **ConfigMerger preserves comments**: actually no — Newtonsoft.Json doesn't preserve JSON comments. Use `JObject` parse instead for round-trip comment preservation (or document this limitation).
- **Programs and Features display name**: "Fingerprint Agent" (matches existing `Install-Service.ps1`).
- **Programs and Features publisher**: "FingerprintAgent" (placeholder; would be your org name when signing is added in Phase 5+).

</specifics>

<deferred>
## Deferred Ideas

- **Code signing certificate** (Phase 5+) — PROJECT.md confirmed deferral; MSI unsigned in v1
- **Advanced auto-update features** (Phase 5+) — delta updates, rollback, channel preview/stable
- **Bundling vendor SDK DLLs in MSI** — licensing/registration concerns (Futronic, DP need account; ZKTeco Silver+ membership) block this for v1
- **VC++ 2010 runtime probe** — only VS 2015-2022 needed for our SDKs
- **Repair verb customization** — Standard WiX Repair verb is sufficient; no custom logic needed for v1
- **Multi-language MSI UI** — Vietnamese for error dialogs only; English for general UI. Full localization deferred.
- **Code signing / Authenticode timestamp** — required for SmartScreen reputation; deferred with signing itself
- **MSI transform (.mst) for IT customization** — out of scope; hard-coded paths per D-04
- **Bootstrapper (.exe) wrapping MSI + prerequisites** — rejected per D-09; no bundling approach
- **Telemetry / usage reporting from agent** — out of scope; agent only exposes /health and /api/capture

---

*Phase: 04-Deployment-End-to-End-Validation*
*Context gathered: 2026-08-19*
