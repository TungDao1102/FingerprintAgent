---
phase: 04
iteration: 2
depth: deep
status: has_findings
files_reviewed: 21
files_reviewed_list:
  - installer/FingerprintAgent.Installer.wixproj
  - installer/FingerprintAgent.Installer.wxs
  - installer/Components/CustomActions.wxs
  - installer/Components/ProgramDataConfig.wxs
  - src/FingerprintAgent.Installer/FingerprintAgent.Installer.csproj
  - src/FingerprintAgent.Installer/CustomActions.cs
  - src/FingerprintAgent/Configuration/AtomicFileWriter.cs
  - src/FingerprintAgent/Configuration/ConfigLoader.cs
  - src/FingerprintAgent/Configuration/ConfigMerger.cs
  - src/FingerprintAgent/FingerprintAgent.csproj
  - src/FingerprintAgent/Update/UpdateCheckService.cs
  - tests/FingerprintAgent.Tests/Configuration/ConfigMergerTests.cs
  - tests/FingerprintAgent.Tests/Installer/CheckVcRedistTests.cs
  - tests/FingerprintAgent.Tests/Installer/ProbeHealthTests.cs
  - tests/FingerprintAgent.Tests/Installer/VietnameseStringsTests.cs
  - tests/FingerprintAgent.Tests/Update/UpdateCheckServiceTests.cs
  - tests/FingerprintAgent.E2E/fixtures/mock-backend.ts
  - tests/FingerprintAgent.E2E/specs/capture-flow.spec.ts
  - tests/FingerprintAgent.E2E/specs/end-to-end.spec.ts
  - .github/workflows/release.yml
  - .github/workflows/e2e.yml
findings:
  critical: 0
  warning: 1
  info: 1
  total: 2
---

# Phase 04 Code Review — Iteration 2

**Reviewed:** 2026-08-20
**Depth:** DEEP (cross-file + call-chain + concurrency re-review)
**Reviewer:** gsd-code-reviewer (adversarial stance)
**Build:** `dotnet build -c Release` → **0 warnings / 0 errors**
**Tests:** `dotnet test -c Release --no-build` → **168 passed, 6 failed** (6 pre-existing ZK9500-hardware-dependent, identical to iteration 1 baseline; no regressions)

## Summary

Compared to iteration 1 (7 Critical + 11 Warning = 18 findings):

| Severity | Count |
|----------|-------|
| Critical | **0** |
| Warning  | **1** (carry-over from WARN-05; see WR-01 below) |
| Info     | **1** (no new tests for CR-03/05/06 concurrency guards) |

**Overall assessment: PASS-WITH-FIXES**

- All 7 Critical findings from iteration 1 are FIXED with verified code paths.
- 10 of 11 Warning findings are FIXED.
- 1 Warning finding (WARN-05 / WiX SHA256 pin) is **PARTIAL**: the verification code exists, but the expected-hash value is an all-zeros placeholder. The build warns and continues — supply-chain protection is **not active** until the operator pastes a real `wix3141rtm` SHA256 into both workflow files. This was flagged as an outstanding follow-up in REVIEW-FIX.md §Recommendations-1; treating it as **WR-01** to keep it visible in the iteration-3 fix loop.
- No regressions introduced by the 10 fix commits.

## Iteration 1 Fixes Verification

### CR-01 (WiX `$(var.Version)` undefined) → **FIXED**
**Evidence:** `installer/FingerprintAgent.Installer.wixproj:27`
```
<DefineConstants>ProductCode=...;UpgradeCode=...;Version=$(Version)</DefineConstants>
```
Plus `wixproj:28` `<Version Condition="'$(Version)' == ''">0.1.0</Version>` ensures local dev builds without `/p:Version` still get a defined preprocessor value. `FingerprintAgent.Installer.wxs:38` correctly references `Version="$(var.Version)"`. Build succeeds with `/p:Version=0.0.0-e2e` in `e2e.yml:81`. **Confirmed via `dotnet build -c Release` success**.

### CR-02 (Non-atomic config.json writes) → **FIXED**
**Evidence:**
- NEW: `src/FingerprintAgent/Configuration/AtomicFileWriter.cs` (94 lines) — temp-file + `File.Replace` (preserves ACLs) / `File.Move` (first-write) pattern with `Guid`-suffixed temp filename to prevent concurrent-write collision on `.tmp`. UTF-8 without BOM.
- `src/FingerprintAgent.Installer/FingerprintAgent.Installer.csproj:46` links `AtomicFileWriter.cs` into the CA DLL (single source of truth — same pattern as `ConfigMerger.cs`).
- All three original call sites migrated:
  - `src/FingerprintAgent/Configuration/ConfigLoader.cs:75-77` (Case 2 smart-merge write) → `AtomicFileWriter.WriteAllText(...)`
  - `src/FingerprintAgent.Installer/CustomActions.cs:311` (`SeedProgramDataConfigCore`) → `AtomicFileWriter.WriteAllText(...)`
  - `src/FingerprintAgent/Update/UpdateCheckService.cs:600` (`DisableUpdateEnabledInConfig`) → `AtomicFileWriter.WriteAllText(...)`
- Grep confirms no remaining `File.WriteAllText`/`WriteAllLines` calls on `config.json` paths in `src/`. The only remaining `File.Create` is the binary MSI download at `UpdateCheckService.cs:472` — that's an idempotent binary download, not a config write, so no atomicity concern.

### CR-03 (Timer concurrency guard) → **FIXED**
**Evidence:** `src/FingerprintAgent/Update/UpdateCheckService.cs:277-304` — `TimerCallback` reads `_state` under `_lock` (line 283-288), returns early if `_state` ∈ {Checking, Downloading, Installing}. The single `HttpClient` (line 33) is reused but no longer invoked concurrently because only one `CheckForUpdateAsync` enters at a time.
**Caveat:** No dedicated unit test for "two overlapping TimerCallbacks result in only one HTTP call". The existing `UpdateCheckServiceTests` exercise single-shot flows. See IN-01 below.

### CR-04 (ProbeHealthAfterInstall race + rollback cleanup) → **FIXED**
**Evidence:**
- `src/FingerprintAgent.Installer/CustomActions.cs:41-43` — `HealthProbeTimeout = 30s` (was 5s), `HealthProbeMaxAttempts = 5`, `HealthProbeRetryDelay = 3s`.
- `CustomActions.cs:161-181` — `ProbeHealth()` now loops up to 5 attempts, sleeping 3s between retries only when outcome is `ConnectionRefused`/`Timeout` (definitive outcomes return immediately).
- `installer/Components/CustomActions.wxs:82-100` — `StopServiceOnRollback` (CR-04) and `StartServiceOnRollback` (WARN-10) CAs declared, both `Execute="rollback"`, `Return="ignore"`.
- `installer/FingerprintAgent.Installer.wxs:135-138` — scheduled in `InstallExecuteSequence` (WiX shares the sequence for forward + rollback actions).
- `src/FingerprintAgent.Installer/CustomActions.cs:425-463` — `StartServiceAfterRollback` mirrors `StopRunningService`, invokes `sc.exe start`.
- Tests: `CheckVcRedistTests.cs:52-57, 60-64` — `HealthProbeTimeout_IsThirtySeconds` and `HealthProbeMaxAttempts_IsFive` assertions.
**Note:** Per REVIEW-FIX.md §Recommendations-2, the suite runtime went from ~1s to ~24s on `ProbeHealth_HardcodedUrl_AtLeastReachesClassifier` (closed port → 5 attempts × 3s = 12s observed). Tests still pass; budget impact acknowledged.

### CR-05 (Finally overwrites Installing state) → **FIXED**
**Evidence:** `src/FingerprintAgent/Update/UpdateCheckService.cs:402-419` — `finally` block now preserves `_state` if it is `Installing` or `Downloading` (sub-operation owns state). Restores to `prevState` only when no sub-operation is in progress. Logic is correct: `DownloadAndInstallAsync` sets `Downloading` (line 459) → `Installing` (line 488) under `_lock`; the outer `finally` reads the same lock and defers to the inner state.

### CR-06 (Config-reload race with in-flight download) → **FIXED**
**Evidence:** `src/FingerprintAgent/Update/UpdateCheckService.cs:187-227` — `ApplyConfig` checks `inFlight` (`_state == Downloading || _state == Installing`) under `_lock`, mutates config fields in place, then defers timer start/stop with a warning. Config values take effect on the next cycle.
**Note:** Mutating `_config.Update.Enabled = false` while the timer is still running means the next `TimerCallback` will see `Enabled = false` but Timer doesn't read `_config.Update.Enabled` before launching — so the operator's disable intent truly only applies at the next cycle boundary. This matches the comment intent ("next cycle"). No race because `TimerCallback` re-reads `_state` under `_lock` and skips if another operation is in flight.

### CR-07 (Vietnamese VC++ dialog never displayed) → **FIXED**
**Evidence:**
- `installer/FingerprintAgent.Installer.wxs:99` — `<DialogRef Id="VcRedistErrorDialog" />` so the dialog binary ships in the MSI (without this, `light.exe` would strip unreferenced fragments).
- `installer/FingerprintAgent.Installer.wxs:148-150` — `<Show Dialog="VcRedistErrorDialog" Condition="VcRedistMissingDialog = &quot;1&quot;" Before="ExitDialog" />` in `InstallUISequence`.
- `src/FingerprintAgent.Installer/CustomActions.cs:79` — `CheckVcRedist` sets `session["VcRedistMissingDialog"] = "1"` before returning Failure.
- The Show condition evaluates at runtime during the post-InstallExecuteSequence return to InstallUISequence; on a failed install, `VcRedistMissingDialog=1` is set and the dialog displays before the default ExitDialog.

### WARN-01 (ConfigMerger does not merge arrays) → **FIXED**
**Evidence:**
- `src/FingerprintAgent/Configuration/ConfigMerger.cs:73-103` — array branch added; iterates template elements, appends to user array only those not already present (`JToken.DeepEquals` comparison). Preserves user order at head.
- Tests `tests/FingerprintAgent.Tests/Configuration/ConfigMergerTests.cs:189-245`:
  - `Merge_UserMissingArrayElement_AppendedToUserArray` (line 189-209)
  - `Merge_UserHasAllArrayElements_NoAppend` (line 211-224)
  - `Merge_UserHasExtraArrayElements_Preserved` (line 226-245)
- Behavior matches D-35 "user wins" contract for non-array values.

### WARN-02 (Null-template merge silently adds null) → **FIXED**
**Evidence:** `src/FingerprintAgent/Configuration/ConfigMerger.cs:43-46` — explicit `if (templateValue.Type == JTokenType.Null) continue;` at the top of the loop. User-side null is still respected via `JObject.ContainsKey` semantics. Test `Merge_NullTemplateValue_NotAddedToUserConfig` (lines 168-186).

### WARN-03 (merge.log write mode inconsistent) → **FIXED**
**Evidence:** `src/FingerprintAgent.Installer/CustomActions.cs:328` — `File.AppendAllLines(mergeLogPath, lines)`. `ConfigLoader.cs:190` already used `File.AppendAllLines`. Both call sites now cumulative.

### WARN-04 (E2E mock-backend state isolation) → **FIXED**
**Evidence:**
- `tests/FingerprintAgent.E2E/fixtures/mock-backend.ts:130-139` — new `DELETE /received` handler clearing the array and returning `{dropped: N}`.
- `tests/FingerprintAgent.E2E/specs/end-to-end.spec.ts:24-32` — new `test.beforeEach` calling `DELETE /received` (asserts 200 status).
- `tests/FingerprintAgent.E2E/specs/end-to-end.spec.ts:95` — assertion tightened from `toBeGreaterThanOrEqual(1)` to `toBe(1)` (exact count proves the current test's capture chain produced the entry, not a prior test's leftover).
- **Note:** The iteration-1 task description listed `capture-flow.spec.ts` for WARN-04, but `capture-flow.spec.ts` does NOT use the mock-backend (it uses Playwright's request API directly against the agent at `127.0.0.1:5043`). The actual fix is correctly applied to `end-to-end.spec.ts`, which is the only consumer of `mock-backend.ts:received`. Task description inaccuracy, not a code defect.

### WARN-05 (WiX SHA256 pin) → **PARTIAL** — see **WR-01** below

### WARN-06 (Install-failure event log) → **FIXED**
**Evidence:** `src/FingerprintAgent/Update/UpdateCheckService.cs:570-573` — `TryWriteEventLog(...EventLogEntryType.Error)` for the `AND config-disable FAILED` case. Operators see the persistent retry loop in Event Viewer. (The success case was already logged at Info per iteration 1.)

### WARN-07 (Predictable temp MSI path) → **FIXED**
**Evidence:** `src/FingerprintAgent/Update/UpdateCheckService.cs:464-466` — `var tempPath = Path.Combine(Path.GetTempPath(), $"FingerprintAgent-Setup-{Guid.NewGuid():N}.msi");`. Each download uses a unique filename so concurrent `TriggerImmediateCheck` calls cannot truncate each other's MSI.

### WARN-08 (HealthUrl drift) → **FIXED**
**Evidence:** `tests/FingerprintAgent.Tests/Installer/CheckVcRedistTests.cs:67-75` — new `HealthUrl_MatchesAgentConfigDefault` test constructs a fresh `HttpConfig()` and asserts `CustomActions.HealthUrl` equals `$"http://{httpConfig.Host}:{httpConfig.Port}/health"`. Drift caught automatically.

### WARN-09 (Permissive Everyone:GenericAll ACL) → **FIXED**
**Evidence:** `installer/Components/ProgramDataConfig.wxs:29-50` — both `cmp_ProgramDataDir` and `cmp_LogsDir` now use:
- `SYSTEM: GenericAll=yes` (service runs as LocalSystem per `Service.wxs:49`)
- `Administrators: GenericAll=yes` (IT maintenance)
- `Users: GenericRead=yes` (operator diagnostics; read-only — no write/delete)

Removed the prior `Everyone: GenericAll=yes`. `util:` xmlns declared at `ProgramDataConfig.wxs:14` for the `PermissionEx` extension.

### WARN-10 (Service stopped mid-transaction upgrade) → **FIXED**
**Evidence:**
- `src/FingerprintAgent.Installer/CustomActions.cs:425-463` — new `StartServiceAfterRollback` CustomAction invokes `sc.exe start`.
- `installer/Components/CustomActions.wxs:96-100` — declared as `Execute="rollback"`, `Return="ignore"`.
- `installer/FingerprintAgent.Installer.wxs:135-138` — scheduled in `InstallExecuteSequence`.
- The `<MajorUpgrade Schedule="afterInstallExecute">` setting is preserved (alternative `afterInstallInitialize` has its own trade-offs).

### WARN-11 (Version desync between Library and Host) → **FIXED**
**Evidence:** `src/FingerprintAgent/FingerprintAgent.csproj:18-21` — explicit `<Version>0.1.0</Version>`, `<VersionPrefix>0.1.0</VersionPrefix>`, `<AssemblyVersion>0.1.0.0</AssemblyVersion>`, `<FileVersion>0.1.0.0</FileVersion>`. Local dev builds now report a stable `0.1.0` instead of SDK's default `1.0.0`. The e2e build's `/p:Version=0.0.0-e2e` still overrides this via the SDK's `VersionPrefix` semantics. (`Assembly.GetExecutingAssembly()` at `UpdateCheckService.cs:322` now returns a deterministic version.)

## Remaining Findings

### WR-01: WiX SHA256 pin is a zero-placeholder — supply-chain protection is not active
**File:** `.github/workflows/release.yml:78` and `.github/workflows/e2e.yml:65`
**Severity:** **Warning** (carry-over from WARN-05; was not fully resolved)
**Issue:** Both workflows contain:
```powershell
$expectedSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
```
with a follow-up `if ($expectedSha256 -eq "000...000") { Write-Warning "WiX SHA256 pin is unset ..."}` that warns but **continues the build**. The actual hash of `wix314-binaries.zip` was never pinned.
**Impact:** Supply-chain compromise of the WiX 3.14.1 release (or DNS hijack) still injects malicious `candle.exe`/`light.exe` into the build pipeline. The verification code is correctly wired, but the comparison value is a sentinel that bypasses all checks.
**Fix:** Operator must compute the real SHA256 once:
```powershell
(Get-FileHash -Algorithm SHA256 wix314-binaries.zip).Hash
```
and paste the 64-character hex string into BOTH `release.yml:78` and `e2e.yml:65`. Once pasted, mismatches hard-fail the workflow (`Write-Error ...; exit 1` at line 85).
**Reference:** REVIEW-FIX.md §Recommendations-1 explicitly calls this out as the outstanding task.

## Info Findings

### IN-01: No dedicated unit tests for CR-03/05/06 concurrency guards
**File:** `tests/FingerprintAgent.Tests/Update/UpdateCheckServiceTests.cs`
**Severity:** **Info** (defense-in-depth gap)
**Issue:** Iteration 1 added three concurrency/state guards to `UpdateCheckService` (Timer in-flight skip, finally-block state preservation, ApplyConfig deferral). The existing tests exercise single-shot flows but do not directly assert:
- Two overlapping `TimerCallback`s produce only one HTTP call (CR-03).
- `State == Installing` remains observable while msiexec is running (CR-05).
- `ApplyConfig` called while `_state == Downloading` defers `Start`/`Stop` (CR-06).
**Impact:** A future refactor that removes the lock guard would not be caught by CI. The behavior is correct today, but lacks a regression fence.
**Fix:** Add focused tests in iteration 3+ using the existing `MockHttpMessageHandler` infrastructure:
```csharp
[Fact]
public async Task TimerCallback_WhenAlreadyChecking_SkipsSecondCall()
[Fact]
public void ApplyConfig_DuringDownload_DoesNotStopTimer()
[Fact]
public async Task DownloadInProgress_StateRemainsInstalling()
```
These are small additions (~30 LOC each) and require no new infrastructure.

## Regression Notes

- **Build:** Clean (0 warnings / 0 errors) on `dotnet build -c Release`. All 4 projects compile, including the new `AtomicFileWriter.cs` linked into the CA DLL.
- **Tests:** 168 passed, 6 failed. The 6 failures are identical to the iteration-1 baseline (ZK9500 hardware-dependent: `ZkSdkProbe_Run` and 5 `ScannerManagerProbeIntegrationTests`). Per AGENTS.md, real-device tests should skip gracefully when SDK is absent; these tests lack `Skip = ...` attributes (Phase 5+ work, not introduced by iteration 1 fixes).
- **File scope:** Exactly 17 source files modified by iteration 1, matching the FIX-01 through FIX-10 commits plus the iteration-1 review/FIX.md docs. `AgentLogger.cs` was NOT modified (the CR-03/05/06 fixes were applied entirely to `UpdateCheckService.cs`; the iteration-1 task description's mention of `AgentLogger.cs` was inaccurate).
- **Subtle behavior changes worth flagging downstream:**
  - `ProbeHealth` now retries up to 5× — tests targeting an unbound port take ~12s (was ~1s). CI test budgets may need adjustment.
  - Update MSI files are now named `FingerprintAgent-Setup-<guid>.msi` in `%TEMP%`. Some AV heuristic scanners flag random-named MSI files; flag to hospital IT if false-positives appear.
  - `ConfigMerger` array merge appends template-only elements to the END of the user's array. If operators manually reordered (e.g., SecuGen before ZKTeco), that order is preserved. If operators removed a template-default element, that removal is preserved. Matches D-35 "user wins" but worth documenting in DEPLOYMENT.md (REVIEW-FIX.md §Recommendations-4).

## Verification Recommendations (for iteration 3 if WR-01 still outstanding)

1. **WR-01 — paste real WiX SHA256**: Operator action required. ~5 minutes:
   ```powershell
   Invoke-WebRequest https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip -OutFile wix.zip
   (Get-FileHash -Algorithm SHA256 wix.zip).Hash
   ```
   Copy result into `release.yml:78` and `e2e.yml:65`.

2. **IN-01 — add concurrency guard tests**: Phase 5+ scope, but small enough to do now if iteration 3 needs work.

3. **Smoke-test MSI build locally**: Once WiX 3.14.1 is installed, run `dotnet build installer/FingerprintAgent.Installer.wixproj -c Release /p:WixToolPath=C:\wix314 /p:Version=1.0.5` and confirm the MSI artifact is produced (proves CR-01 end-to-end in a non-CI environment).

---

_Reviewed: 2026-08-20_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: deep_
_Working tree: clean (verified via `git status`)_
_Iteration: 2 of 3 (auto-loop)_
_Compared to: 04-REVIEW.md (iteration 1) and 04-REVIEW-FIX.md_
