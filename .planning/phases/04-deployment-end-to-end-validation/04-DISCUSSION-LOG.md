# Phase 4: Deployment & End-to-End Validation - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-08-19
**Phase:** 4-Deployment & End-to-End Validation
**Areas discussed:** MSI Toolchain, VC++ redist handling, Auto-update v1 scope, E2E validation surface, Documentation surface, MSI uninstall behavior, PowerShell script evolution, First-run experience, Update notification UX, User follow-up questions (4 items)

---

## 1. MSI Toolchain

| Option | Description | Selected |
|--------|-------------|----------|
| WiX Toolset (XML) | Industry standard, mature, abundant docs | ✓ |
| WixSharp (C# DSL) | Type-safe, refactor-friendly, less mature | |
| Advanced Installer (commercial) | Repackage mode, ~$500/license, external dep | |

**User's choice:** WiX Toolset (XML)
**Notes:** Standard choice for hospital IT deployment. Steeper XML learning curve but better debugging.

| Option | Description | Selected |
|--------|-------------|----------|
| scripts/Build-Msi.ps1 | Standalone PowerShell script (matches existing pattern) | |
| Separate .wixproj + MSBuild | Integrated via `dotnet build` | |
| GitHub Actions workflow only | CI-driven, no local build | ✓ |

**User's choice:** GitHub Actions workflow only
**Notes:** No local MSI build script. CI produces the artifact. Implies adding `.github/workflows/` (currently absent per AGENTS.md).

| Option | Description | Selected |
|--------|-------------|----------|
| Smooth in-place upgrade | Stop service, copy files, start | ✓ |
| Force uninstall first | MSI fails if existing | |
| Versioned side-by-side | Complex; overkill | |

| Option | Description | Selected |
|--------|-------------|----------|
| Hard-coded paths | `C:\Program Files\FingerprintAgent\` + ProgramData | ✓ |
| Customizable install dir | IT picks binary dir at install | |
| Both customizable | Max flexibility, more support | |

| Option | Description | Selected |
|--------|-------------|----------|
| Yes — start service + ping /health | Self-validating install | ✓ |
| Start service only, skip health check | Simpler | |
| No service start, leave manual | Matches current PS1 behavior | |

| Option | Description | Selected |
|--------|-------------|----------|
| C# CustomAction DLL | Same .NET Framework as agent; HttpClient for /health | ✓ |
| Inline PowerShell / VBScript | No DLL, fragile on stripped images | |
| Standalone helper EXE | Cleaner separation, larger MSI | |

| Option | Description | Selected |
|--------|-------------|----------|
| Unsigned v1, signing deferred to Phase 5+ | SmartScreen warning acceptable; matches PROJECT.md | ✓ |
| Self-signed cert (dev/CI only) | Removes 'Unknown publisher' in CI; still triggers SmartScreen on workstations | |
| Real EV cert now | $300-$600/year; recurring cost | |

| Option | Description | Selected |
|--------|-------------|----------|
| msiexec /qn fully silent | Built into WiX standard UI levels | ✓ |
| Basic UI only, no /qn | Blocks unattended deployment | |
| Custom dialog flow | Overkill | |

---

## 2. VC++ Redist Handling

| Option | Description | Selected |
|--------|-------------|----------|
| Burn bootstrapper .exe | Standard WiX pattern | |
| Bundled inside MSI | Single .msi, harder debugging | |
| Separate MSI + VC++ download | 2-step install | |
| Detect only — error if missing | No bundling, clear error | ✓ |

**User's choice (free-text Vietnamese):** "máy tính muốn sử dụng phải có sẵn vc++ x86, nếu không có thì hiển thị thông báo lỗi khì cài đặt"
**Translation:** Computer must have VC++ x86 pre-installed; if not, show error message during install.

| Option | Description | Selected |
|--------|-------------|----------|
| C# CustomAction DLL registry probe | Code reuse with /health probe | ✓ |
| WiX `<Property>` + `<RegistrySearch>` | Pure XML, no DLL | |
| Bundle vcredist_x86.exe always | Always install VC++ | |

| Option | Description | Selected |
|--------|-------------|----------|
| English | Matches existing agent logs | |
| Vietnamese | Matches hospital audience | ✓ |
| Bilingual | Covers both audiences | |

| Option | Description | Selected |
|--------|-------------|----------|
| VS 2015-2022 only | Forward-compatible, single URL | ✓ |
| VS 2010 + VS 2015-2022 | More thorough | |
| Probe all VC++ versions | Loosest, risk missing exact version | |

---

## 3. Auto-update v1 Scope

| Option | Description | Selected |
|--------|-------------|----------|
| Daily Task Scheduler job | Predictable, but misses hibernation | (initial) |
| On service startup only | Simple but always-on machines miss updates | |
| Both startup + daily | Complex | |
| Manual only, no auto-poll | Aligns with deferred | (initial) |

**User's feedback (Vietnamese):** Concerns about 2 AM machine off, hotfixes between scheduled times, and daily being wasteful.

**Probed follow-up:** Does Win10/11 hybrid shutdown hide `OnStart`?

| Option | Description | Selected |
|--------|-------------|----------|
| Periodic Timer inside service | Survives hibernate/sleep | ✓ |
| Hybrid: startup OR every 24h | Combines both | |
| Use Task Scheduler with wake timers | Aggressive wake | |
| Defer auto-update to Phase 5+ | No code surface | (initial) |

**User's feedback (Vietnamese):** Wants minimal hotfix lag early, less overhead later. Suggested config-driven interval.

**Proposed strategy:** `System.Threading.Timer` inside service, configurable interval, auto-backoff to 24h when stable, reset on detected release.

| Option | Description | Selected |
|--------|-------------|----------|
| Proceed with auto-update | Configurable interval + auto-backoff | ✓ (final) |
| Defer auto-update to Phase 5+ | No code surface | |
| Lightweight idle-only check | Check only when idle | |

**User's final choice (after reviewing resource costs):** Keep auto-update code but default disabled (`update.enabled: false`). Operator opt-in for initial rollout / hotfix period. Service ships lightweight.

---

## 4. E2E Validation Surface

| Option | Description | Selected |
|--------|-------------|----------|
| Playwright (Node.js + Chromium) full E2E | Real browser, real CORS | ✓ |
| Browser-mock CORS test | No browser cost | |
| Manual runbook + curl/Postman | Cheapest, error-prone | |
| Defer E2E test to Phase 5+ | No E2E in v1 | |

| Option | Description | Selected |
|--------|-------------|----------|
| tests/FingerprintAgent.E2E/ separate | Clean separation from xUnit | ✓ |
| tests/e2e/ at repo root | Top-level visibility | |
| Inline as scripts/e2e/ PowerShell wrapper | No Node.js dev dep | |

| Option | Description | Selected |
|--------|-------------|----------|
| Full E2E (CORS + capture + mock backend) | Covers full ROADMAP SC #5 | ✓ |
| CORS + capture only (skip backend roundtrip) | Lighter test | |
| CORS preflight only | Lightest | |

| Option | Description | Selected |
|--------|-------------|----------|
| On tag push only (release validation) | ~15 min CI per release | |
| On PR push + tag push | Faster feedback, slows dev | |
| Manual trigger via workflow_dispatch | On-demand | ✓ |
| Daily + tag push | Catch regressions between releases | |

---

## 5. Documentation Surface

| Option | Description | Selected |
|--------|-------------|----------|
| README.md for developers | Project overview, build commands | |
| README.md for hospital IT (non-dev) | Install-focused | |
| README.md combined for both | Sections clearly labeled | ✓ |

| Option | Description | Selected |
|--------|-------------|----------|
| Full operations runbook (Vietnamese) | Comprehensive | ✓ |
| Minimal install guide | Smaller scope | |
| Minimal in English only | No translation cost | |

| Option | Description | Selected |
|--------|-------------|----------|
| Manual CHANGELOG.md (Keep a Changelog) | In-repo visibility | |
| Auto-generate from git via git-cliff | Requires conventional commit discipline | |
| Skip CHANGELOG.md, GitHub Releases only | Minimal scope | ✓ |

| Option | Description | Selected |
|--------|-------------|----------|
| Delete docs/ folder | Single source of truth | ✓ |
| Keep docs/ with deprecation banner | Slow migration | |
| Keep docs/ as-is | Two sources of truth | |

---

## 6. MSI Uninstall Behavior

| Option | Description | Selected |
|--------|-------------|----------|
| Preserve logs, remove everything else | Standard forensic preservation | ✓ |
| Preserve everything (deep uninstall) | Maximum preservation | |
| Full clean uninstall, remove everything | Risk: lose forensic evidence | |
| Prompt IT: what to preserve | Maximum flexibility | |

| Option | Description | Selected |
|--------|-------------|----------|
| No force-clean flag | Simple | |
| MSI property REMOVE_LOGS=1 for force clean | Documented escape hatch | ✓ |
| Separate cleanup PowerShell script | Two-step | |

| Option | Description | Selected |
|--------|-------------|----------|
| Graceful stop (30s wait) then uninstall | Prevents data loss for active capture | ✓ |
| Forced stop (5s timeout) then uninstall | Faster uninstall | |
| Direct delete (skip stop) | Simplest | |

| Option | Description | Selected |
|--------|-------------|----------|
| Delete EventLog source on uninstall | Clean registry state | ✓ |
| Keep EventLog source on uninstall | Reuse on reinstall | |

---

## 7. PowerShell Script Evolution

| Option | Description | Selected |
|--------|-------------|----------|
| Keep PS1 as dev/test fallback, document roles | PS1 for dev, MSI for IT | ✓ |
| Refactor Install/Uninstall to call MSI | PS1 wraps MSI | |
| Remove Install/Uninstall PS1 | MSI-only | |

| Option | Description | Selected |
|--------|-------------|----------|
| Setup-VendorSdk.ps1 stays manual | Production has vendor driver pre-installed | ✓ |
| MSI invokes Setup-VendorSdk.ps1 post-install | Auto-download | |
| MSI ships all 4 vendor DLLs pre-bundled | No PS1 needed | |

**User's clarification (Vietnamese):** "không, các file ps1 chỉ với mục đích test, để có thể chạy với môi trường production thì máy tính đó phải được cài driver của hãng máy quét trước"
**Translation:** No, PS1 files are only for testing. For production environment, vendor driver must be installed first on the workstation.

| Option | Description | Selected |
|--------|-------------|----------|
| Keep Test-Capture.ps1 as-is for dev smoke | Dev convenience | ✓ |
| Add --ci mode with non-zero exit on failure | CI-friendly | |
| Replace with Playwright-only | Minimal duplication | |

| Option | Description | Selected |
|--------|-------------|----------|
| Keep Service.ps1 as-is | start/stop/restart/status only | ✓ |
| Add `Service.ps1 update` manual trigger | Force update check | |
| Add update + detailed status | More ops visibility | |

---

## 8. First-Run Experience

| Option | Description | Selected |
|--------|-------------|----------|
| Auto-start service + Vietnamese success dialog | Self-validating | ✓ |
| Auto-start silent, no dialog | Minimal | |
| Manual start, show post-install instructions | Same as PS1 behavior | |

| Option | Description | Selected |
|--------|-------------|----------|
| Show specific error with troubleshooting hint | Detailed diagnostics | |
| Always show generic success | Avoid confusion | |
| Show warning if scanner not detected, success otherwise | Middle ground | ✓ |

| Option | Description | Selected |
|--------|-------------|----------|
| Same dialog for fresh install and upgrade | Simple | |
| Different dialogs (fresh vs upgrade) | Distinct messaging | ✓ |
| Silent upgrade, only show dialog on fresh install | Minimal | |

| Option | Description | Selected |
|--------|-------------|----------|
| AssemblyVersion + MSI ProductVersion | Synced source | ✓ |
| Custom Version field in config.json | More flexible | |
| Read from Windows Registry | Decoupled | |

---

## 9. Update Notification UX

| Option | Description | Selected |
|--------|-------------|----------|
| Silent install, log entry only | Minimal disruption | |
| Toast before + after | User awareness | ✓ |
| Defer update to idle time, silent install | Quiet periods | |
| Pre-install toast only, no post | Less chatty | |

| Option | Description | Selected |
|--------|-------------|----------|
| 10 seconds (fixed) | OS-update-like | ✓ |
| 30 seconds | More conservative | |
| Configurable via config.json | Operator control | |

| Option | Description | Selected |
|--------|-------------|----------|
| Error toast + log + EventLog + disable auto-update | Defensive | ✓ |
| Silent fail + log only | No UX noise | |
| Error toast + auto-rollback | Complex; rollback may also fail | |

| Option | Description | Selected |
|--------|-------------|----------|
| Configurable, default on | IT opt-out | |
| Always show toast, fail silently if no session | Simple | ✓ |
| No toast at all, log only | Minimal | |

---

## 10. User Follow-up Questions

| Question | User's answer (or paraphrase) | Notes |
|----------|-------------------------------|-------|
| Should SDK DLLs be bundled or require vendor driver? | Vendor driver required; DLLs not bundled | Consistent with prior D-32a |
| Where should README.md / DEPLOYMENT.md live if not in MSI? | GitHub repo only, no Release assets | User wants docs separated from installer |
| Where should config.json live for runtime use + IT editing? | `C:\ProgramData\FingerprintAgent\config.json` | Major path change; existing ConfigLoader needs update |
| Should Programs and Features entry include Update verb? | Yes, add Update verb | Enables manual update from Control Panel |
| How should MSI upgrade handle config.json when both user and developer have edited it? | Smart merge — add new keys, preserve user values; respect user deletions | Requires new ConfigMerger class |

---

## Agent's Discretion

Areas where the agent has flexibility (see CONTEXT.md "Agent's Discretion" section):
- WiX `<MajorUpgrade>` schedule attribute choice
- Custom action sequencing in MSI lifecycle
- Smart-merge implementation details (recursive vs flat)
- Programs and Features icon selection
- WiX UI level selection

---

## Deferred Ideas

Captured during discussion:
- Code signing certificate (Phase 5+) — PROJECT.md confirmed
- Advanced auto-update (delta, rollback, channels) — Phase 5+
- Bundling vendor SDK DLLs — licensing/registration concerns block
- VC++ 2010 runtime probe — only VS 2015-2022 needed
- Repair verb customization — standard WiX Repair sufficient
- Multi-language MSI UI — Vietnamese error dialogs only
- Code signing / Authenticode timestamp — deferred with signing
- MSI transform (.mst) — out of scope; hard-coded paths
- Bootstrapper (.exe) wrapping MSI — rejected
- Telemetry / usage reporting from agent — out of scope
