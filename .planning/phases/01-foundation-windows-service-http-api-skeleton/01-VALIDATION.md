---
phase: 01
slug: foundation-windows-service-http-api-skeleton
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-07-28
---

# Phase 01 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | PowerShell integration tests + .NET unit tests (xUnit/NUnit) |
| **Config file** | `FingerprintAgent.Tests.csproj` — Wave 0 installs if missing |
| **Quick run command** | `Invoke-RestMethod http://localhost:5043/health` |
| **Full suite command** | `Install-Service.ps1` → `Test-Capture.ps1` → `Uninstall-Service.ps1` |
| **Estimated runtime** | ~30 seconds (service install/start overhead) |

---

## Sampling Rate

- **After every task commit:** Run `Invoke-RestMethod http://localhost:5043/health`
- **After every plan wave:** Run full PowerShell integration suite
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 01-01-01 | 01 | 1 | API-01 | — | Bind localhost:5043, respond to POST /api/capture | integration | `Invoke-RestMethod -Method POST http://localhost:5043/api/capture -Body $body` | ❌ W0 | ⬜ pending |
| 01-01-02 | 01 | 1 | API-06 | — | GET /health returns status/uptime/scanner | integration | `Invoke-RestMethod http://localhost:5043/health` | ❌ W0 | ⬜ pending |
| 01-02-01 | 02 | 1 | CFG-01 | — | config.json drives host/port/log path | integration | Restart service on changed port; probe new endpoint | ❌ W0 | ⬜ pending |
| 01-02-02 | 02 | 1 | CFG-04 | — | Missing/invalid config fails startup with EventLog entry | integration | `Get-EventLog -LogName Application -Source FingerprintAgent` | ❌ W0 | ⬜ pending |
| 01-03-01 | 03 | 1 | SVC-01 | — | Service installs and starts as `FingerprintAgent` | integration | `Get-Service FingerprintAgent` | ❌ W0 | ⬜ pending |
| 01-03-02 | 03 | 1 | SVC-02 | — | Service StartType Automatic | integration | `(Get-Service FingerprintAgent).StartType` | ❌ W0 | ⬜ pending |
| 01-04-01 | 04 | 1 | API-05 | — | CORS wildcard/allowlist headers correct | integration | `curl -i -X OPTIONS -H Origin:... http://localhost:5043/api/capture` | ❌ W0 | ⬜ pending |
| 01-04-02 | 04 | 1 | SEC-03 | — | No fingerprint image files written outside log dir | integration | `Get-ChildItem -Recurse -Include *.png,*.bmp,*.jpg` | ❌ W0 | ⬜ pending |
| 01-05-01 | 05 | 1 | OBS-01 | — | Structured log file exists at configured path | integration | `Test-Path C:\ProgramData\FingerprintAgent\Logs\agent.log` | ❌ W0 | ⬜ pending |
| 01-05-02 | 05 | 1 | SVC-04 | — | Windows EventLog source registered and entries written | integration | `Get-EventLog -LogName Application -Source FingerprintAgent` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `FingerprintAgent.sln` + `FingerprintAgent.csproj` (.NET Framework 4.8)
- [ ] `FingerprintAgent.Tests.csproj` with xUnit/NUnit stubs for core logic
- [ ] PowerShell scripts: `Install-Service.ps1`, `Uninstall-Service.ps1`, `Test-Capture.ps1`
- [ ] `config.json` schema document and sample file

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Windows reboot auto-start | SVC-02 | Requires OS reboot | Reboot machine; verify `Get-Service FingerprintAgent` status = Running |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
