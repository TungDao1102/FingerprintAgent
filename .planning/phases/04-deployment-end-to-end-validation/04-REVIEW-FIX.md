---
phase: 04
iteration: 1
review_path: .planning/phases/04-deployment-end-to-end-validation/04-REVIEW.md
fixed_at: 2026-08-20
findings_in_scope: 18
fixed: 18
skipped: 0
status: all_fixed
---

# Phase 04 Review Fix Report

**Fixed at:** 2026-08-20
**Source review:** [04-REVIEW.md](04-REVIEW.md)
**Iteration:** 1

**Summary:**
- Findings in scope: 18 (7 Critical + 11 Warning)
- Fixed: 18
- Skipped: 0
- Atomic commits: 10 (some grouped by file for related findings)

## Fixed Issues

### FIX-01: CR-01 — WiX `$(var.Version)` undefined
**Commit:** `2530d3d`
**File:** `installer/FingerprintAgent.Installer.wixproj`
**Applied fix:** Appended `Version=$(Version)` to `<DefineConstants>` so the MSBuild `Version` property (set via `/p:Version=1.0.1` in `release.yml`) is exposed to `candle.exe` as the `$(var.Version)` preprocessor variable.

### FIX-02: CR-02 — Non-atomic config.json writes (3 sites)
**Commit:** `970ffbd`
**Files:**
- `src/FingerprintAgent/Configuration/AtomicFileWriter.cs` (NEW — shared helper, write-to-temp + `File.Replace` preserves ACLs / `File.Move` for first-write, `Guid`-suffixed temp filename prevents concurrent-write collision)
- `src/FingerprintAgent.Installer/FingerprintAgent.Installer.csproj` (linked `AtomicFileWriter.cs` into CA DLL via the same pattern as `ConfigMerger.cs` — single source of truth, no drift risk)
- `src/FingerprintAgent/Configuration/ConfigLoader.cs:73-75` (Case 2 smart-merge write)
- `src/FingerprintAgent.Installer/CustomActions.cs:276` (`SeedProgramDataConfigCore`)
- `src/FingerprintAgent/Update/UpdateCheckService.cs:528-536` (`DisableUpdateEnabledInConfig`)

**Applied fix:** Replaced all three `File.WriteAllText` call sites with `AtomicFileWriter.WriteAllText`. The temp filename uses `Guid.NewGuid().ToString("N")` suffix so two simultaneous writes to the same target don't collide on `.tmp`.

### FIX-03: CR-04 — ProbeHealthAfterInstall race
**Commit:** `a4fa170`
**Files:**
- `src/FingerprintAgent.Installer/CustomActions.cs` (`ProbeHealth` now retries up to 5 attempts × 3s with a single 30s timeout per attempt, vs the original 5s single-shot; only transient `ConnectionRefused`/`Timeout` outcomes trigger retry)
- `installer/Components/CustomActions.wxs` (new `StopServiceOnRollback` CA, `Execute="rollback"`, `Return="ignore"` — reuses `StopRunningService` so a rolled-back install stops the service MSI just started)
- `installer/FingerprintAgent.Installer.wxs` (rollback schedule added alongside the execute sequence)

### FIX-04: CR-03 + CR-05 + CR-06 — UpdateCheckService concurrency / state / config race
**Commit:** `733d5b0`
**File:** `src/FingerprintAgent/Update/UpdateCheckService.cs`
**Applied fix:**
- **CR-03** `TimerCallback` now checks `_state` (Checking|Downloading|Installing) under `_lock` before launching `CheckForUpdateAsync` — prevents overlapping HTTP calls when `TriggerImmediateCheck` + Timer fire close together.
- **CR-05** `CheckForUpdateAsync` `finally` block no longer overwrites Installing/Downloading state set by `DownloadAndInstallAsync`. Saves only restore to Running/Stopped when no sub-operation is in progress.
- **CR-06** `ApplyConfig` defers timer start/stop when Downloading/Installing is in flight — operator's config edit no longer interrupts an in-flight msiexec invocation. Config values still mutate in place; next cycle picks them up.

### FIX-05: CR-07 — Vietnamese VC++ dialog never displayed
**Commit:** `e873ab8`
**File:** `installer/FingerprintAgent.Installer.wxs`
**Applied fix:** Added `<DialogRef Id="VcRedistErrorDialog" />` (so the dialog binary ships in the MSI — otherwise `light.exe` strips unreferenced fragments) and `<Show Dialog="VcRedistErrorDialog" Condition="VcRedistMissingDialog = &quot;1&quot;" Before="ExitDialog" />` in `InstallUISequence`. `CheckVcRedist` sets `VcRedistMissingDialog="1"` before returning Failure.

### FIX-06: WARN-02 + WARN-03 + WARN-06 + WARN-07 — ConfigMerger null skip, merge.log append, install-failure event log, unique temp MSI path
**Commit:** `37e7c4a`
**Files:**
- `src/FingerprintAgent/Configuration/ConfigMerger.cs` — WARN-02: skip explicit null template values (adding `null` to user config is a template error pattern; user null is still respected per D-35).
- `src/FingerprintAgent.Installer/CustomActions.cs:290` — WARN-03: `File.WriteAllLines` → `File.AppendAllLines` for cumulative history across MSI upgrades. Matches `ConfigLoader.WriteMergeLog`.
- `src/FingerprintAgent/Update/UpdateCheckService.cs:HandleInstallFailureAsync` — WARN-06: writes an EventLog Error entry if the config disable-write fails (operators see the silent retry loop in Event Viewer).
- `src/FingerprintAgent/Update/UpdateCheckService.cs:DownloadAndInstallAsync` — WARN-07: per-download `Guid`-suffixed temp MSI path prevents concurrent `TriggerImmediateCheck` calls from truncating each other's downloads.
- `tests/FingerprintAgent.Tests/Configuration/ConfigMergerTests.cs` — new test `Merge_NullTemplateValue_NotAddedToUserConfig`.

### FIX-07: WARN-04 — mock-backend state isolation
**Commit:** `89e9264`
**Files:**
- `tests/FingerprintAgent.E2E/fixtures/mock-backend.ts` — added `DELETE /received` endpoint that clears the array and returns `{dropped: N}`.
- `tests/FingerprintAgent.E2E/specs/end-to-end.spec.ts` — added `test.beforeEach` calling `DELETE /received` so each test starts with an empty array. Tightened assertion from `>= 1` to `=== 1` (exact count proves THIS test's capture chain produced the entry, not a prior test's leftover).

### FIX-08: WARN-05 — WiX 3.14.1 SHA256 pin
**Commit:** `b139486`
**Files:**
- `.github/workflows/release.yml`
- `.github/workflows/e2e.yml`

**Applied fix:** Added `Get-FileHash -Algorithm SHA256` verification step in both workflows. Hash value is a zero-placeholder that warns-but-doesn't-fail in CI (allows local dev); operator must compute the real `wix3141rtm` hash once with `Get-FileHash` and paste into both files.

### FIX-09: WARN-08 + WARN-09 + WARN-11 — HealthUrl drift, ACL restrict, explicit Version
**Commit:** `ae7ab28`
**Files:**
- `tests/FingerprintAgent.Tests/Installer/CheckVcRedistTests.cs` — WARN-08: new `HealthUrl_MatchesAgentConfigDefault` test computes expected URL from `HttpConfig` default and asserts it matches `CustomActions.HealthUrl`. Drift caught automatically. Also renamed `HealthProbeTimeout_IsFiveSeconds` → `_IsThirtySeconds` (CR-04) and added `HealthProbeMaxAttempts_IsFive`.
- `installer/Components/ProgramDataConfig.wxs` — WARN-09: replaced `User="Everyone" GenericAll="yes"` with SYSTEM (full, service runs as LocalSystem) + Administrators (full, IT maintenance) + Users (read, operator diagnostics). Added `util:` xmlns declaration.
- `src/FingerprintAgent/FingerprintAgent.csproj` — WARN-11: explicit `<Version>0.1.0</Version>`, `<VersionPrefix>0.1.0</VersionPrefix>`, `<AssemblyVersion>0.1.0.0</AssemblyVersion>`, `<FileVersion>0.1.0.0</FileVersion>`. Local dev no longer defaults to SDK's 1.0.0; e2e builds keep the suffix.

### FIX-10: WARN-01 + WARN-10 — ConfigMerger array merge + StartServiceOnRollback
**Commit:** `9400cdc`
**Files:**
- `src/FingerprintAgent/Configuration/ConfigMerger.cs` — WARN-01: arrays merge element-wise when both user and template values are `JArray`. Preserves user order, appends template-only elements to the end. Without this, template upgrades adding a new scanner vendor silently disappear.
- `tests/FingerprintAgent.Tests/Configuration/ConfigMergerTests.cs` — three new tests: `Merge_UserMissingArrayElement_AppendedToUserArray`, `Merge_UserHasAllArrayElements_NoAppend`, `Merge_UserHasExtraArrayElements_Preserved`.
- `src/FingerprintAgent.Installer/CustomActions.cs` — WARN-10: new `StartServiceAfterRollback` CA (mirrors `StopRunningService`, uses `sc.exe start`).
- `installer/Components/CustomActions.wxs` — `StartServiceOnRollback` CustomAction declared, `Execute="rollback"`, `Return="ignore"`.
- `installer/FingerprintAgent.Installer.wxs` — `StartServiceOnRollback` scheduled in the rollback sequence alongside `StopServiceOnRollback`.
- `tests/FingerprintAgent.Tests/Installer/CheckVcRedistTests.cs` — xUnit2000 warning fixed (variable split for `expected`/`actual`).

## Skipped Issues

None.

## Verification Results

- `dotnet build FingerprintAgent.sln -c Release`: **0 warnings / 0 errors**
- `dotnet test tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj -c Release`: **168 passed, 6 pre-existing failures, 0 skipped, 174 total**
  - Pre-existing failures are hardware-dependent tests (`ZkSdkProbe_Run` and the 5 `ScannerManagerProbeIntegrationTests`) that require a real ZK9500 scanner + `libzkfp.dll` in the test bin dir. Verified they fail on master BEFORE my changes (via `git stash`). Per AGENTS.md: *"Missing SDK = adapter compiles to a stub. Real-device tests skip gracefully when SDK absent"* — these tests should ideally be wrapped in `Skip = ...` attributes when SDK is absent (Phase 5+ work, out of scope for this fix-up phase).
- Atomic commits: **10 total** (some grouped by file for related findings)
- Files modified: **17 total** (across src/, installer/, tests/, .github/)

## Recommendations for Re-Review

1. **WiX SHA256 placeholder** (WARN-05): the operator MUST compute the actual `wix3141rtm` SHA256 once with `(Get-FileHash -Algorithm SHA256 wix314-binaries.zip).Hash` and paste the result into BOTH `.github/workflows/release.yml` and `.github/workflows/e2e.yml`. Current zero-placeholder warns-but-allows the build through; once the operator fills in the real hash, mismatches hard-fail the workflow.

2. **CR-04 retry budget in tests** (regression risk): the new `ProbeHealth` makes 5 attempts × 3s delay = up to 12s on `ConnectionRefused`. Tests like `ProbeHealth_HardcodedUrl_AtLeastReachesClassifier` (which targets unbound port 5043) take ~12s. The `ProbeHealthTests` suite went from ~1s to 24s. If CI test budgets are tight, consider mocking the timer.

3. **WARN-07 unique MSI temp filename** + WARN-07's GUID-suffixed path means msiexec now runs against a different filename on each invocation. Verify the Windows Defender / AV software on the operator workstation allows this pattern (some heuristic scanners flag random-named MSI files). If false-positives appear, consider a per-version-fixed-but-per-attempt-unique pattern.

4. **ConfigMerger array merge semantics** (WARN-01): the current implementation appends template-only elements to the END of the user's array. If the operator has manually REORDERED the array (e.g., moved SecuGen before ZKTeco), that order is preserved. If the operator has REMOVED a template-default element (e.g., removed Futronic because they don't own one), that removal is preserved (test `Merge_UserHasExtraArrayElements_Preserved`). This matches the existing D-35 "user wins" contract, but operators may not expect template-added vendors to appear at the end of their list. Document in DEPLOYMENT.md.

5. **Pre-existing ZK9500 hardware-dependent tests** (6 failures): these should be wrapped in `Skip = ...` attributes when `libzkfp.dll` is not in the test bin dir. Out of scope for this fix-up phase but worth a Phase 5+ task.

6. **MajorUpgrade Schedule** (WARN-10): we added `StartServiceOnRollback` but kept `Schedule="afterInstallExecute"`. The alternative `afterInstallInitialize` would eliminate the rollback-stop/start dance entirely but has its own trade-offs (no transaction around file replacement). Current fix is the lower-risk option.

7. **WARN-08 test** (`HealthUrl_MatchesAgentConfigDefault`) currently constructs a fresh `HttpConfig()` to get the defaults. If anyone ever switches the production probe URL to a value other than `127.0.0.1:5043` (e.g., to `localhost` or `[::1]`), the test will fail with a clear message. That's by design — drift is the bug we're guarding against.

---

_Fixed: 2026-08-20_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
