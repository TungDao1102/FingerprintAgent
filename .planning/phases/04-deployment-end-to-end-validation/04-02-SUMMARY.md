---
phase: 04
plan: 04-02
subsystem: Deployment
tags: [msi, wix, customaction, deployment, ci, github-actions, dtf, vcredist, vietnamese, smart-merge]
dependency_graph:
  requires: [04-01-ConfigMerger, 04-01-config.template.json, 04-01-UpdateConfig]
  provides: [CustomAction-DLL, WiX-source, ReleaseWorkflow, VietnameseStrings]
  affects: [FingerprintAgent.sln]
tech-stack:
  added: [WixToolset.Dtf.WindowsInstaller-4.0.4, WixToolset.Dtf.CustomAction-4.0.4]
  patterns: [Linked-Source-Compile, SfxCA-type1-wrapper, non-SDK-wixproj, extern-alias-test-isolation]
key-files:
  created:
    - src/FingerprintAgent.Installer/FingerprintAgent.Installer.csproj
    - src/FingerprintAgent.Installer/CustomActions.cs
    - src/FingerprintAgent.Installer/CustomAction.config
    - src/FingerprintAgent.Installer/Properties/VietnameseStrings.resx
    - src/FingerprintAgent.Installer/Properties/VietnameseStrings.Designer.cs
    - src/FingerprintAgent.Installer/Properties/AssemblyInfo.cs
    - installer/FingerprintAgent.Installer.wxs
    - installer/FingerprintAgent.Installer.wixproj
    - installer/Components/Service.wxs
    - installer/Components/ProgramDataConfig.wxs
    - installer/Components/CustomActions.wxs
    - installer/Components/UninstallBehavior.wxs
    - installer/Dialogs/VcRedistError.wxs
    - installer/Dialogs/WixUI_Minimal.vi-VN.wxl
    - .github/workflows/release.yml
    - tests/FingerprintAgent.Tests/Installer/CheckVcRedistTests.cs
    - tests/FingerprintAgent.Tests/Installer/ProbeHealthTests.cs
    - tests/FingerprintAgent.Tests/Installer/SeedProgramDataConfigTests.cs
    - tests/FingerprintAgent.Tests/Installer/VietnameseStringsTests.cs
  modified:
    - FingerprintAgent.sln
    - tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj
decisions:
  - id: D-04-02-01
    decision: "WiX DTF 4.0.4 instead of 3.x"
    rationale: "WiX 3.x packages (3.14.x) are not in the public NuGet feed. WiX DTF 4.0.x retains the Session/CustomAction API surface (just renamed to WixToolset.Dtf.WindowsInstaller namespace). 4.0.4 is the latest OSMF-free version (5/6/7 require OSMF EULA acceptance). The MSBuild target still wraps the managed DLL into .CA.dll via MakeSfxCA.exe."
  - id: D-04-02-02
    decision: "CustomAction DLL imports WixToolset.Dtf.WindowsInstaller.Session via using-aliases (no ProjectReference to legacy assembly)"
    rationale: "WiX 4 moved types from Microsoft.Deployment.WindowsInstaller to WixToolset.Dtf.WindowsInstaller namespace. Using aliases (using Session = WixToolset.Dtf.WindowsInstaller.Session) keeps the legacy code patterns while resolving against the new namespace — easier to read than full qualification."
  - id: D-04-02-03
    decision: "Non-SDK .wixproj with manual candle/light invocation"
    rationale: "WiX 3.x SDK is not a NuGet package (only 4.0+ available, which has breaking schema). Cannot use WixToolset.Sdk/3.14.0 — doesn't resolve. Cannot use WixToolset.Sdk/4.0.4 — .wxs files use 3.x schema. Non-SDK .wixproj + WiX 3.x toolchain install is the only working path."
  - id: D-04-02-04
    decision: "extern alias 'WixCA' in test project to disambiguate linked ConfigMerger"
    rationale: "ConfigMerger.cs is linked into the CA DLL via <Compile Include>. Without alias, tests see TWO ConfigMerger classes (one in FingerprintAgent.Library, one in FingerprintAgent.Installer) with the same FQN — type collision error. The alias isolates the Installer's namespace for tests."
  - id: D-04-02-05
    decision: "Hand-authored VietnameseStrings.Designer.cs (no WinForms SDK)"
    rationale: "SDK-style projects without Microsoft.NET.Sdk.Web/WinForms do not auto-generate Designer.cs from .resx files. Hand-authoring the strongly-typed accessor class avoids adding a NuGet dep (Microsoft.Extensions.ResX.SourceGenerator) for one small file."
  - id: D-04-02-06
    decision: "Two fixed GUIDs (ProductCode + UpgradeCode) generated once via PowerShell"
    rationale: "MajorUpgrade requires stable GUIDs. ProductCode can change on major version bumps; UpgradeCode MUST stay fixed across v1.x. Documented in DEPLOYMENT.md (out of scope for this plan)."
metrics:
  duration_minutes: 60
  task_count: 11
  files_changed: 21
  commits: 7
  tests_added: 24
  tests_total_passing: 149
  tests_total: 155
  warnings: 0
  errors: 0
status: complete
---

# Phase 04 Plan 02: MSI Installer + C# CustomAction DLL + GitHub Actions Release Workflow

## One-Liner

Production-grade WiX 3.x MSI installer with DTF CustomAction DLL (VC++ x86 detection + /health probe + smart-merge), Vietnamese dialogs, log preservation default, GitHub Actions release workflow on tag push.

## Key Achievements

1. **CustomAction DLL project** — `src/FingerprintAgent.Installer/` (net48, OutputType=Library) with 5 entry points: `CheckVcRedist`, `ProbeHealthAfterInstall`, `SeedProgramDataConfig`, `DetectInstallType`, `StopRunningService`. Build produces `FingerprintAgent.Installer.CA.dll` (SfxCA-wrapped type-1 CA binary via `WixToolset.Dtf.CustomAction` MSBuild target).
2. **VC++ x86 detection (D-09/D-10/D-12)** — registry probe of `HKLM\SOFTWARE\[Wow6432Node\]Microsoft\VisualStudio\14.0\VC\Runtimes\x86\Installed == 1`. Fail-open on registry exception (D-12). Sets `VcRedistMissingDialog` property to drive Vietnamese error dialog display.
3. **/health probe with classification (D-05/D-38)** — HTTP GET to `127.0.0.1:5043/health` with 5s timeout. Classifies response into Healthy / DegradedScannerMissing (503) / Unhealthy / Timeout / ConnectionRefused. Sets dialog-routing properties for fresh vs upgrade success paths.
4. **Smart-merge wiring (D-34/D-35)** — `SeedProgramDataConfig` CustomAction links `ConfigMerger.cs` source from main library (no ProjectReference to `FingerprintAgent.Library` — prevents msiexec loading vendor SDKs). Creates `merge.log` in ProgramData when new keys are added.
5. **WiX 3.x source files** — main `FingerprintAgent.Installer.wxs` + 4 Component fragments + 2 dialog files. Fixed `ProductCode` + `UpgradeCode` GUIDs (NOT `Id="*"`) for `MajorUpgrade` compatibility. `MajorUpgrade Schedule="afterInstallExecute" AllowSameVersionUpgrades="yes"` for smooth in-place upgrades.
6. **Vietnamese localization** — `VietnameseStrings.resx` with strongly-typed accessors + `WixUI_Minimal.vi-VN.wxl` for WixUI dialogs (Welcome/Install/Finish/etc.).
7. **Log preservation default (D-28/D-29)** — `Permanent="yes"` on Logs directory Component. `REMOVE_LOGS=1` MSI property triggers conditional `RemoveFolder` for force-clean uninstall.
8. **GitHub Actions release workflow** — `.github/workflows/release.yml` triggers on tag push `v*`, runs on `windows-latest`, downloads WiX 3.14.1 binaries from GitHub release, builds MSI, uploads as workflow artifact.
9. **24 new unit tests** — covers all testable CustomAction helpers (`IsVcRedistInstalled`, `ProbeHealth`, `SeedProgramDataConfigCore`, `VietnameseStrings.*` resource accessors).

## Files Created / Modified

### Created
- `src/FingerprintAgent.Installer/FingerprintAgent.Installer.csproj` — net48, WixToolset.Dtf.WindowsInstaller 4.0.4, links ConfigMerger.cs, embeds VietnameseStrings.resx
- `src/FingerprintAgent.Installer/CustomActions.cs` — 5 [CustomAction] entry points + internal testable helpers
- `src/FingerprintAgent.Installer/CustomAction.config` — `<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />`
- `src/FingerprintAgent.Installer/Properties/VietnameseStrings.resx` — Vietnamese VC++ error, scanner-not-detected, install success/fresh/upgrade, generic error
- `src/FingerprintAgent.Installer/Properties/VietnameseStrings.Designer.cs` — hand-authored strongly-typed ResourceManager accessor
- `src/FingerprintAgent.Installer/Properties/AssemblyInfo.cs` — `[InternalsVisibleTo("FingerprintAgent.Tests")]` for test access
- `installer/FingerprintAgent.Installer.wxs` — Product definition with fixed GUIDs, MajorUpgrade, INSTALLFOLDER/PROGRAMDATAFOLDER Properties, 5-step CustomAction sequence
- `installer/FingerprintAgent.Installer.wixproj` — non-SDK legacy wixproj for WiX 3.x toolchain (requires WixToolPath MSBuild property)
- `installer/Components/Service.wxs` — `<ServiceInstall>` + `<util:ServiceConfig>` + `<ServiceControl>` 30s graceful stop
- `installer/Components/ProgramDataConfig.wxs` — ProgramData dir + Logs dir (Permanent=yes) + EventLog source
- `installer/Components/CustomActions.wxs` — `<Binary>` declaration + 5 `<CustomAction>` entries
- `installer/Components/UninstallBehavior.wxs` — REMOVE_LOGS=1 conditional RemoveFolder
- `installer/Dialogs/VcRedistError.wxs` — Vietnamese VC++ error dialog (uses !(loc.VcRedistErrorTitle/Body))
- `installer/Dialogs/WixUI_Minimal.vi-VN.wxl` — Vietnamese overrides for all standard WixUI strings
- `.github/workflows/release.yml` — CI release pipeline
- `tests/FingerprintAgent.Tests/Installer/CheckVcRedistTests.cs` — 6 tests
- `tests/FingerprintAgent.Tests/Installer/ProbeHealthTests.cs` — 5 tests
- `tests/FingerprintAgent.Tests/Installer/SeedProgramDataConfigTests.cs` — 6 tests
- `tests/FingerprintAgent.Tests/Installer/VietnameseStringsTests.cs` — 7 tests

### Modified
- `FingerprintAgent.sln` — added `FingerprintAgent.Installer` project + `installer/` solution folder
- `tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj` — ProjectReference to Installer with `Aliases="WixCA"`

## Verification Results

| Check | Result |
|-------|--------|
| `dotnet build src/FingerprintAgent.Installer/ -c Release` | ✅ 0 warnings, 0 errors |
| `dotnet build FingerprintAgent.sln -c Release` | ✅ 0 warnings, 0 errors |
| `dotnet test tests/FingerprintAgent.Tests/ -c Release --no-build` | ✅ 149 pass / 6 pre-existing ZKTeco device integration failures (unrelated) |
| `FingerprintAgent.Installer.CA.dll` produced | ✅ 200KB type-1 wrapped CA DLL (verified MZ header) |
| `CustomAction.config` in output | ✅ present at `bin/Release/CustomAction.config` |
| `VietnameseStrings.resources` embedded | ✅ ResourceManager can load all 6 keys |
| MSI build verification (`dotnet build installer/*.wixproj`) | ⚠️ Deferred to CI — WiX 3.x toolchain not installed locally |

## Deviations from Plan

### Auto-fixed

**1. [D-04-02-01] WiX DTF version: 4.0.4 instead of 3.14.x**
- **Found during:** Initial build attempt of Installer project
- **Issue:** Plan specified `WixToolset.Dtf.WindowsInstaller` and `WixToolset.Dtf.CustomAction` at version 3.14.x. These packages are NOT in the public NuGet feed (WixToolset.Dtf.* 3.x never published; lowest is 4.0.0-preview.1).
- **Fix:** Used `4.0.4` (latest OSMF-free version; 5/6/7 require Open Source Maintenance Fee EULA acceptance). The MSBuild target still wraps the managed DLL into `FingerprintAgent.Installer.CA.dll` correctly (verified: 200KB type-1 binary produced with valid PE header).
- **Files modified:** `src/FingerprintAgent.Installer/FingerprintAgent.Installer.csproj`
- **Commit:** 0e13adc

**2. [D-04-02-02] CustomAction namespace migration: `WixToolset.Dtf.WindowsInstaller` instead of `Microsoft.Deployment.WindowsInstaller`**
- **Found during:** Initial compile attempt
- **Issue:** WiX 4 moved `Session`, `ActionResult`, `[CustomAction]` attribute types from `Microsoft.Deployment.WindowsInstaller` namespace to `WixToolset.Dtf.WindowsInstaller`. Legacy `Microsoft.Deployment.WindowsInstaller.dll` not in NuGet either.
- **Fix:** Added `using` aliases in `CustomActions.cs`:
  ```csharp
  using Session = WixToolset.Dtf.WindowsInstaller.Session;
  using ActionResult = WixToolset.Dtf.WindowsInstaller.ActionResult;
  using CustomActionAttribute = WixToolset.Dtf.WindowsInstaller.CustomActionAttribute;
  ```
  Keeps code reading like the original plan (just `Session session` parameter) while resolving against the new namespace.
- **Files modified:** `src/FingerprintAgent.Installer/CustomActions.cs`
- **Commit:** 0e13adc

**3. [D-04-02-03] .wixproj: non-SDK legacy style with manual candle/light Exec calls**
- **Found during:** Trying to build WiX installer project locally
- **Issue:** `WixToolset.Sdk/3.14.0` MSBuild SDK does not exist (only 4.0+). `WixToolset.Sdk/4.0.4` rejects v3 schema .wxs files. SDK-style .wixproj has no working WiX 3.x option.
- **Fix:** Switched to legacy non-SDK .wixproj (`<Project ToolsVersion="4.0">` + `<Import Project="$(WixToolPath)\wix.targets" />` + `<Exec>` for candle/light). CI downloads WiX 3.14.1 binaries from GitHub release and sets `WixToolPath` MSBuild property. Local developers must install WiX 3.x manually.
- **Files modified:** `installer/FingerprintAgent.Installer.wixproj`, `.github/workflows/release.yml`
- **Commit:** 971e73b

**4. [D-04-02-04] Test isolation: `extern alias WixCA` for linked ConfigMerger**
- **Found during:** Initial test compile
- **Issue:** ConfigMerger.cs is linked into the Installer DLL via `<Compile Include>`. Test project references BOTH `FingerprintAgent.Library` and `FingerprintAgent.Installer`, so `FingerprintAgent.Configuration.ConfigMerger` exists in both assemblies — type collision.
- **Fix:** Added `Aliases="WixCA"` to the Installer ProjectReference, used `extern alias WixCA` + `using CustomActions = WixCA::FingerprintAgent.Installer.CustomActions` in test files.
- **Files modified:** `tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj`, all 4 new test files
- **Commit:** c2d12dd

**5. [D-04-02-05] Hand-authored VietnameseStrings.Designer.cs**
- **Found during:** Trying to use resx in CA DLL
- **Issue:** SDK-style projects without WinForms SDK don't auto-generate Designer.cs from .resx. Adding Microsoft.Extensions.ResX.SourceGenerator adds a NuGet dep for one small file.
- **Fix:** Hand-authored Designer.cs (129 lines) with ResourceManager strongly-typed accessor pattern. Resource is auto-embedded by SDK default; ResourceManager.GetString lookup works.
- **Files modified:** `src/FingerprintAgent.Installer/Properties/VietnameseStrings.Designer.cs`

### Documented Decisions

- **D-04-02-06:** Two fixed GUIDs for ProductCode + UpgradeCode (generated via `[guid]::NewGuid()` once in PowerShell). Documented as "must stay fixed for MajorUpgrade compatibility".

### Documented Limitations

**Environment: WiX 3.x toolchain not installed on dev machine.**
- The .wxs files use WiX 3.x schema (correct, per plan D-01).
- The .wixproj is configured for WiX 3.x toolchain (legacy non-SDK style with WixToolPath).
- The CI workflow (`.github/workflows/release.yml`) downloads `wix314-binaries.zip` from `https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm` and sets `WixToolPath=C:\wix314` before invoking `dotnet build`.
- **Local MSI build verification is deferred to CI.** A developer who wants to build the MSI locally must install WiX 3.14.x manually from the same GitHub release page (download `wix314.exe`) and run `dotnet build installer/FingerprintAgent.Installer.wixproj -c Release /p:WixToolPath=C:\wix314`.

**Verification scope:**
- Source-level: ✅ All CustomAction code compiles + 24 unit tests pass
- Assembly-level: ✅ CA DLL builds to valid type-1 PE binary (verified MZ header)
- MSI-level: ⚠️ Deferred to CI (cannot run candle.exe without WiX 3.x install)
- Install-level: ⚠️ Deferred to CI (requires MSI artifact)

## Anti-Patterns Avoided

- ✅ No local `Build-Msi.ps1` script (D-02: CI-only MSI build)
- ✅ No MSI signing (D-07: unsigned v1)
- ✅ No vendor SDK DLLs bundled (D-32a: separate vendor installers)
- ✅ No `<CustomAction>` for service registration (D-19: `<ServiceInstall>` only)
- ✅ No Burn bootstrapper (D-09: detect-only, no bundling)
- ✅ No `<MajorUpgrade Schedule="afterInstallInitialize">` (D-03: afterInstallExecute for smooth upgrade)
- ✅ No custom EULA dialog (D-04: hard-coded paths, no install-dir picker)
- ✅ No `<Property Id="ARPNOMODIFY">` (Modify verb fine for v1)
- ✅ No unconditional `RemoveFolder` on ProgramData\Logs (D-28: Permanent=yes)
- ✅ VC++ check before file copy (D-09: fail-fast)
- ✅ No telemetry / crash reporting (out of scope)

## Downstream Impact

This plan establishes:
- **Plan 04-03 (Auto-update):** can use the same `CustomActions.cs` patterns for download-and-install flow; `SeedProgramDataConfig` already preserves user config on upgrade.
- **Plan 04-04 (E2E + docs):** can reference the GitHub Actions workflow for CI test setup.
- **Production deployment:** MSI artifact (when CI builds successfully) replaces the per-machine PowerShell install path for hospital IT rollouts.

## Known Stubs

None. All code paths implemented. The `dotnet build installer/` step requires WiX 3.x toolchain (deferred to CI as documented limitation above).

## Threat Flags

| Flag | File | Description |
|------|------|-------------|
| threat_flag: msiexec-loads-managed-dll | src/FingerprintAgent.Installer/CustomActions.cs | CustomAction DLL runs with full LOCAL SYSTEM privileges during install (D-19). All 5 entry points are reachable from MSI. Mitigated by: (a) using only known safe APIs (Registry.LocalMachine read-only, HttpClient to localhost, sc.exe, file I/O in well-defined paths); (b) all actions log full intent via session.Log for audit; (c) actions never write outside ProgramData + INSTALLFOLDER. |
| threat_flag: replaceable-binary | installer/Components/Service.wxs | MSI ships FingerprintAgent.exe + FingerprintAgent.Library.dll from bin output. An attacker who can modify the build pipeline could swap these. Mitigated by: unsigned v1 (D-07) — explicit Phase 5+ scope is code signing with EV cert for SmartScreen + binary integrity. |

## Self-Check

- ✅ All 19 created files exist on disk
- ✅ All 7 commit hashes found in git log
- ✅ Build clean (0 warnings, 0 errors across all 4 projects)
- ✅ 24 new tests pass; 125 pre-existing tests still pass (149/155)
- ✅ CA DLL produces valid type-1 PE binary
- ✅ VietnameseStrings resource accessor finds all 6 keys
- ✅ Working tree clean (no uncommitted changes — `.planning/04-01-SUMMARY.md` untracked was pre-existing)

## Commit History

| # | Hash | Subject |
|---|------|---------|
| 1 | 0e13adc | feat(04-02): FingerprintAgent.Installer project (net48 + DTF 4.0.4 + 4 CustomActions) |
| 2 | c2d12dd | test(04-02): unit tests for CustomAction helpers + VietnameseStrings |
| 3 | 6195ea2 | feat(04-02): WiX installer source files (.wxs + .wxl + .wixproj) |
| 4 | 8c0c12e | feat(04-02): GitHub Actions release workflow (.github/workflows/release.yml) |
| 5 | ec48859 | feat(04-02): add FingerprintAgent.Installer to solution + installer/ folder |
| 6 | 971e73b | chore(04-02): switch to non-SDK .wixproj + download WiX 3.14.1 in CI |
| 7 | df49f5e | chore(04-02): fix .wixproj path concatenation in candle/light invocation |
