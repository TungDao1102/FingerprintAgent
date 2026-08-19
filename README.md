# FingerprintAgent

Local fingerprint capture agent for hospital HIS SaaS web apps.

A Windows Service (`.NET Framework 4.8`, x86) that exposes an HTTP API on
`http://127.0.0.1:5043`. Web apps (Angular, React, anything that can
issue `fetch`) call `POST /api/capture`, the agent drives the connected
USB fingerprint scanner, and returns PNG bytes + a SHA-256 hash. No
biometric data is persisted — everything stays in memory for the life
of a single request.

Matching (1:1 or 1:N) is the back-end's job. The agent is a thin
adapter layer between the scanner SDKs and the SaaS front-end.

## For Developers

### Architecture

```
HTTP Client (browser, mobile, etc.)
        |
        v
+-----------------------------+
|   FingerprintAgent service  |
|   localhost:5043            |
|                             |
|   +----------------------+  |
|   | HttpServer           |  |
|   |   /api/capture       |  |
|   |   /health            |  |
|   +----------------------+  |
|             |               |
|             v               |
|   +----------------------+  |
|   | ScannerManager       |  |
|   |   ZKTeco -> SecuGen  |  |
|   |   -> Futronic -> DP  |  |
|   +----------------------+  |
+-----------------------------+
```

Source of truth: `.planning/codebase/ARCHITECTURE.md`.

### Prerequisites

- Windows 10 / 11 (x64)
- .NET Framework 4.8 (pre-installed on Win 10/11)
- .NET SDK 9.0 (for building — `winget install Microsoft.DotNet.SDK.9`)
- Node.js 22 LTS (for the Playwright E2E suite only)
- (Optional) one of: ZKTeco, SecuGen, DigitalPersona, or Futronic scanner
  + its vendor SDK DLLs dropped in `lib/<Vendor>/`. Without any, the
  project still builds — `MockScannerAdapter` is always available.

### Build

```powershell
dotnet build FingerprintAgent.sln             # debug
dotnet build FingerprintAgent.sln -c Release  # 0 warnings / 0 errors
```

### Dev workflow

Run the service in the foreground with hot-reload-friendly logging:

```powershell
dotnet run --project src\FingerprintAgent.Host -- --console
```

This is equivalent to a service start, but prints logs to stdout and
lets you Ctrl-C cleanly. Useful during adapter development.

### PowerShell scripts (`scripts/`)

PS1 scripts are the **dev/test** surface. Production IT uses the MSI
installer (see `DEPLOYMENT.md`).

| Script | Role |
|---|---|
| `Install-Service.ps1` | Install service via `sc.exe` on a dev box (no MSI needed) |
| `Uninstall-Service.ps1` | Reverse of the above |
| `Service.ps1` | `start \| stop \| restart \| status` |
| `Setup-VendorSdk.ps1` | Copy vendor DLLs from system locations into `lib/` so all adapter `<DefineConstants>` activate |
| `Test-Capture.ps1` | Quick smoke test of `/health` + `/api/capture` |

### Tests

Two suites, two runners:

- **Unit / integration** — xUnit 2.9.3 + Moq 4.20.72 in
  `tests/FingerprintAgent.Tests/`. Run with `dotnet test`.
- **End-to-end** — Playwright 1.55.1 in `tests/FingerprintAgent.E2E/`.
  Real Chromium does CORS preflight + capture against a running
  agent on `localhost:5043`. Run with `npm ci && npx playwright test`.
  See `tests/FingerprintAgent.E2E/README.md`.

### Configuration

`src/FingerprintAgent/config.json` is the **template** (copied to build
output via `<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>`).
Edit the template, not the bin copies.

At runtime the agent reads from `C:\ProgramData\FingerprintAgent\config.json`
(programdata path wins when present, falls back to template). On upgrade
the MSI runs a smart merge — new keys are added, user values preserved,
deletions respected. See `.planning/codebase/CONVENTIONS.md`.

### More info

- Deployment / operations runbook (Vietnamese): see `DEPLOYMENT.md`.
- Product context: see `.planning/PROJECT.md`.
- Codebase map: see `.planning/codebase/`.

## For Hospital IT

FingerprintAgent is a small background service that lets your HIS web
app request a fingerprint scan from a USB scanner plugged into the
workstation. Once installed, the service is always on and binds only
to `127.0.0.1:5043` (never reachable from the network — the browser
running on the same machine calls it directly).

Quick install:

1. Download `FingerprintAgent-Setup.msi` from the GitHub Releases page.
2. Double-click the MSI, follow the Vietnamese dialog.
3. Service starts automatically. A success dialog confirms installation.
4. Plug in your USB fingerprint scanner. The agent detects it within
   30 seconds.

If something goes wrong, the full troubleshooting guide (in Vietnamese)
is in `DEPLOYMENT.md` in this repository.

Support contact: _add your organization's support email / phone here_.
