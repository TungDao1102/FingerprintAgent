# Phase 04 Code Review

**Depth:** DEEP (cross-file analysis + call chain tracing + concurrency review)
**Reviewed:** 4 plans, 28 commits, ~50 new files
**Date:** 2026-08-19
**Reviewer:** gsd-code-reviewer (adversarial stance)

## Summary

| Severity | Count |
|----------|-------|
| Critical | 7     |
| Warning  | 11    |
| Info     | 9     |

**Overall assessment**: **BLOCK** — three Critical findings (CR-01 MSI build broken in CI, CR-02 config corruption self-DoS, CR-04 /health rollback race) must be fixed before v1.0 ships. The remaining four Critical findings are conditional risks (operator opt-in paths).

## Critical Findings

### CR-01: WiX `$(var.Version)` preprocessor variable is undefined — MSI build will fail in CI

**File:** `installer/FingerprintAgent.Installer.wxs:38` + `installer/FingerprintAgent.Installer.wixproj:27,28`

**Issue:** The main .wxs uses `Version="$(var.Version)"` on the `<Product>` element, but the .wixproj's `<DefineConstants>` only exposes `ProductCode` and `UpgradeCode`. The MSBuild `<Version>` property (set via `/p:Version=1.0.1` from `release.yml`) is **not** automatically exposed to `candle.exe` as a preprocessor variable in legacy WiX 3.x projects. When CI builds, `$(var.Version)` is undefined → `candle.exe` either fails with "undefined preprocessor variable" or substitutes an empty string, producing an MSI with `Version=""` which is invalid and may fail Windows Installer validation.

**Impact:** The release workflow artifact (`FingerprintAgent-Setup.msi`) likely never builds successfully. Tag-push releases silently produce no artifact. The `verify MSI artifact` step at `release.yml:81-94` would fail.

**Fix:** Either
1. Add `Version=$(Version)` to `<DefineConstants>` in `.wixproj`:
   ```xml
   <DefineConstants>ProductCode=...;UpgradeCode=...;Version=$(Version)</DefineConstants>
   ```
   Or
2. Use the MSBuild-direct reference form in .wxs (WiX 3.x auto-exposes some properties): `Version="$(Version)"` (without `var.` prefix), since `wix.targets` from WiX 3.x maps `<Version>` MSBuild property → `$(Version)` preprocessor variable.

Recommendation: option 1 is explicit and safe.

---

### CR-02: Non-atomic config.json writes can corrupt user config — self-inflicted DoS

**File:**
- `src/FingerprintAgent/Update/UpdateCheckService.cs:528-536` (`DisableUpdateEnabledInConfig`)
- `src/FingerprintAgent.Installer/CustomActions.cs:276` (`SeedProgramDataConfigCore`)
- `src/FingerprintAgent/Configuration/ConfigLoader.cs:73-75` (Case 2 smart-merge write)

**Issue:** All three call sites use `File.WriteAllText` to overwrite `C:\ProgramData\FingerprintAgent\config.json` in place. If the process crashes, loses power, or is killed mid-write (Windows Update reboot during update flow, msiexec transaction rollback, MSI uninstall during install), the config.json is left as a partial file. `ConfigLoader.LoadFromFile` will then throw `InvalidDataException` / `FormatException` on next boot, which propagates to `OnStart` and **crashes the service repeatedly** (SCM recovery actions restart it; each restart hits the same corrupt file).

**Impact:** Once `update.enabled=false` write (CR-02, UpdateCheckService path) or a smart-merge write (CR-02, CustomAction path) is interrupted, **no future service restart succeeds** until an operator manually fixes the JSON file. This is a self-inflicted permanent outage from the auto-update feature that Phase 4 just shipped. Worst-case trigger: an operator enables auto-update, a release arrives during a Windows Update reboot, the partial config bricks the service.

**Fix:** Atomic write pattern (write-to-temp + rename). The Windows filesystem guarantees `Move` (rename within same volume) is atomic:
```csharp
var tempPath = path + ".tmp";
File.WriteAllText(tempPath, json.ToString(Formatting.Indented));
// Optional: File.Replace preserves ACLs/attributes if path exists.
if (File.Exists(path)) File.Replace(tempPath, path, null);
else File.Move(tempPath, path);
```
Apply to all three call sites (UpdateCheckService, ConfigLoader, CustomActions).

---

### CR-03: Timer-driven auto-update lacks concurrency guard — overlapping HTTP calls + state corruption

**File:** `src/FingerprintAgent/Update/UpdateCheckService.cs:137, 164-175, 256-267`

**Issue:** `Timer.Change(TimeSpan.Zero, _nextCheckInterval)` in `TriggerImmediateCheck` fires the next callback immediately. `TimerCallback` is fire-and-forget (`CheckForUpdateAsync(...).GetAwaiter().GetResult()`) and does **not** check `_state` before launching. The single shared `HttpClient` is reused for both `releases/latest` GET and the MSI download stream. If two checks overlap (operator clicks Programs and Features "Update" verb while timer fires; or two TriggerImmediateCheck calls in rapid succession), both invocations enter `CheckForUpdateAsync` concurrently:
- Both `GET` the GitHub API (wasted rate-limit budget; 60 req/hour/IP is finite)
- Both may pass version comparison and both call `DownloadAndInstallAsync`
- Both compete to write `update.enabled = false` to the same config.json
- The `finally` block at line 367 races on `_state`

**Impact:** Wasted API quota, double msiexec invocations racing to overwrite the same `%TEMP%\FingerprintAgent-Setup.msi` file, transient config corruption. Not a hard crash but visible operator pain.

**Fix:** Add a `SemaphoreSlim _checkInFlight(1, 1)` or check `_state == Checking` at the top of `TimerCallback`:
```csharp
private void TimerCallback(object state)
{
    lock (_lock)
    {
        if (_state != UpdateState.Running) return; // skip if already checking/downloading/installing
    }
    // ...
}
```

---

### CR-04: `ProbeHealthAfterInstall` runs in MSI transaction — false-positive rollback + leaked service

**File:** `installer/FingerprintAgent.Installer.wxs:112` + `src/FingerprintAgent.Installer/CustomActions.cs:121-145`

**Issue:** The CustomAction is scheduled `After="StartServices"`. WiX `<ServiceControl Start="install">` invokes SCM to start the service, which reports "Running" when the service process calls `OnStart`. **But** `FingerprintAgentService.OnStart` calls `_httpServer.Start()` which spawns a `LongRunning` task to call `HttpListener.GetContextAsync()`. There is no synchronization between "SCM says Running" and "HttpListener bound to 5043". On a cold VM (first .NET Framework JIT, first-time scanner SDK load), the HTTP listener may bind milliseconds or **seconds** after SCM reports running.

If the 5-second `ProbeHealth` timeout expires before the listener binds, `ProbeHealth` returns `ConnectionRefused` → `Unhealthy` → `ActionResult.Failure` → **MSI rollback**. But the service was already started, and WiX `<ServiceControl Stop="both">` only stops on **uninstall/upgrade**, not on rollback. **The FingerprintAgent service continues running on 5043 after the MSI rolls back**, but with the install "failed" state in MSI's database.

**Impact:**
1. Valid installs roll back due to cold-start latency (false-negative)
2. After rollback, an orphaned `FingerprintAgent` service runs with files possibly partially replaced → process crashes mid-operation
3. Operator sees "Install failed" but service is running; confusing state

**Fix:** Two complementary changes:
1. Increase probe timeout from 5s to 15-30s to absorb cold-start (`HealthProbeTimeout = TimeSpan.FromSeconds(30)`)
2. Add a retry loop in `ProbeHealth` (e.g., 5 attempts × 3s = 15s budget) instead of single shot
3. On rollback path, explicitly `sc.exe stop FingerprintAgent` if the service is running

---

### CR-05: `UpdateCheckService` Finally-block overwrites `Installing` state with `Running`

**File:** `src/FingerprintAgent/Update/UpdateCheckService.cs:271, 367-368`

**Issue:** `CheckForUpdateAsync` captures `prevState = _state` at entry (line 272), then `DownloadAndInstallAsync` sets `_state = Installing` (line 432). After `DownloadAndInstallAsync` returns, the `finally` block (line 367) restores state from the **saved** `prevState`:
```csharp
_state = prevState == UpdateState.Stopped ? UpdateState.Stopped : UpdateState.Running;
```
Since `prevState` was `Running` (captured before `_state = Checking` was set), the Installing state is **overwritten with Running**. Observers and tests checking `service.State == Installing` see `Running` for the entire msiexec invocation.

**Impact:** Misleading state telemetry. `UpdateCheckServiceTests` doesn't currently assert Installing state (test seam doesn't cover it), so this is invisible to tests. Production log entries at INFO level may show "running" while msiexec is actively executing.

**Fix:** Track current state more carefully:
```csharp
finally
{
    lock (_lock) 
    { 
        _state = (_state == UpdateState.Installing) 
            ? UpdateState.Running      // install succeeded — restore normal
            : (_state == UpdateState.Downloading) 
                ? UpdateState.Running
                : (_state == UpdateState.Checking ? prevState : _state);
    }
}
```

---

### CR-06: Operator-edited `update.enabled` race with in-flight download — `ConfigFileWatcher` reload + `UpdateCheckService` concurrent

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:253-277` + `src/FingerprintAgent/Update/UpdateCheckService.cs:180-206`

**Issue:** `OnConfigReloaded` calls `_updateCheckService.ApplyConfig(newConfig)`. `ApplyConfig` mutates `_config.Update.Enabled` and starts/stops the timer. If a timer callback fired **just before** the reload and is mid-`DownloadAndInstallAsync`, the operator's new config arrives mid-download:
1. Timer fires → `CheckForUpdateAsync` → version newer → `DownloadAndInstallAsync` starts downloading
2. Operator edits `config.json` → `ConfigFileWatcher` debounce fires → `OnConfigReloaded` → `ApplyConfig(stop)`
3. Download completes → `DownloadAndInstallAsync` continues → msiexec installs (despite operator's "disable" intent)

The operator's opt-out does not interrupt an in-flight download. Worse, `OnConfigReloaded` acquires `_configLock` (line 258-261) but subsequent `_httpServer.UpdateCorsConfig`, `_scanner.UpdatePriority`, `_updateCheckService.ApplyConfig` calls are **outside** the lock — concurrent reloads during shutdown are not serialized.

**Impact:** Operator cannot reliably stop an in-flight update via config edit. Service must be restarted via SCM to interrupt.

**Fix:** Track in-flight download state and check before applying config:
```csharp
public void ApplyConfig(AgentConfig newConfig)
{
    bool inFlight;
    lock (_lock) { inFlight = _state == UpdateState.Downloading || _state == UpdateState.Installing; }
    if (inFlight)
    {
        _logger?.Warn(null, "UpdateCheck: config reload during in-flight update — deferring apply");
        return;
    }
    // ... existing logic
}
```

---

### CR-07: Vietnamese VC++ error dialog defined but never displayed — operators see generic MSI failure

**File:** `installer/Dialogs/VcRedistError.wxs:14-16` + `installer/FingerprintAgent.Installer.wxs`

**Issue:** The dialog `VcRedistErrorDialog` is fully defined (title, body, OK button), the Vietnamese strings are in `VietnameseStrings.resx` and `WixUI_Minimal.vi-VN.wxl` (line 33-34), and `CheckVcRedist` sets `session["VcRedistMissingDialog"] = "1"` when VC++ is missing. **But** no `<Publish>` or `<DialogRef>` in `FingerprintAgent.Installer.wxs` ever shows this dialog. The InstallExecuteSequence simply rolls back without UI. The `.wxs` comment at line 16 confirms: `Actual display is scheduled in main .wxs InstallUISequence (TODO: Phase 5+).`

**Impact:** When a hospital workstation lacks VC++ x86 (a documented common scenario per D-09), the operator sees a generic MSI failure dialog (English "Installation failed"), not the curated Vietnamese explanation with the exact download URL. Hospital IT may not know to install `vc_redist.x86.exe`. DEPLOYMENT.md FAQ §7.3 describes the "VC++ missing" workflow as if the friendly dialog exists, but it doesn't.

**Fix:** Wire up the dialog in `InstallUISequence` using `<Publish>` based on the property set by `CheckVcRedist`:
```xml
<InstallUISequence>
  <Show Dialog="VcRedistErrorDialog" Condition="VcRedistMissingDialog = &quot;1&quot;" Before="ExitDialog" />
</InstallUISequence>
```
Or fall back to a `WixQuietExec` that logs the URL prominently.

---

## Warning Findings

### WARN-01: ConfigMerger does not merge arrays — `scanner.priority` upgrade silently loses new vendors

**File:** `src/FingerprintAgent/Configuration/ConfigMerger.cs:59-67`

**Issue:** `MergeInto` only recurses when **both** user and template values are `JObject` (line 59-64). Arrays are preserved verbatim — the user's full array is kept, no element-wise merge. If template ships `["ZKTeco", "SecuGen", "Futronic", "DigitalPersona"]` (note: re-ordered ZKTeco first per Phase 3 priority) and user has `["ZKTeco"]`, the merge does nothing for the array. The user's missing vendors are NOT added.

**Impact:** Operators expecting "MSI upgrade adds new vendors to priority" silently won't get new vendors. They must manually edit config.json. `merge.log` only reports added **keys**, not added array elements, so the operator isn't alerted.

**Fix:** Either document this as a known limitation in DEPLOYMENT.md, or add element-wise merge for arrays (treat as ordered list: keep user order, append template-only elements).

---

### WARN-02: ConfigMerger null-template behavior — missing key gets `null` template value silently

**File:** `src/FingerprintAgent/Configuration/ConfigMerger.cs:40-45`

**Issue:** If user config lacks a key and template has that key with value `null` (explicit `null` in template), the merge adds the null. Operators will see `"newKey": null` in `config.json` after upgrade. `Merge_NullUserValue_NotTreatedAsDeleted` covers the converse (user has null), but not template-has-null.

**Impact:** Subtle breakage if any future template ships a key with `null` default (currently no such key exists, but the codebase pattern invites it).

**Fix:** Skip null template values during merge: `if (!userObj.ContainsKey(key) && templateValue.Type != JTokenType.Null) { ... }`. Add a test for this case.

---

### WARN-03: `merge.log` write path inconsistency — `File.WriteAllLines` (CA) vs `File.AppendAllLines` (ConfigLoader)

**File:**
- `src/FingerprintAgent.Installer/CustomActions.cs:290` (uses `WriteAllLines` — overwrites)
- `src/FingerprintAgent/Configuration/ConfigLoader.cs:188` (uses `AppendAllLines` — appends)

**Issue:** Two call sites for the same merge algorithm write the same log file with **opposite semantics**. The MSI CustomAction (executed on upgrade via msiexec) **overwrites** any prior merge.log; the in-process ConfigLoader (executed on next service start after MSI rolled back to its prior version) **appends**. The first install via MSI wipes any prior log; subsequent in-process loads append to the empty file.

**Impact:** History is lost on each MSI upgrade. Operator sees "merged N keys" once, then a single entry on next in-process load. The file is no longer a cumulative log.

**Fix:** Both should use `File.AppendAllLines`. The two call sites claiming "single source of truth" via `ConfigMerger.cs` should also share the log-writing helper.

---

### WARN-04: `mock-backend.ts` keeps state across Playwright specs — test isolation break

**File:** `tests/FingerprintAgent.E2E/fixtures/mock-backend.ts:79` + `tests/FingerprintAgent.E2E/specs/end-to-end.spec.ts:54-99`

**Issue:** The single `received[]` array is mutated across all tests sharing the `webServer`. `end-to-end.spec.ts` polls `received` for up to 30 retries waiting for the test's own entry — but if a prior test left entries, the assertion at line 90-99 (`expect(received.length).toBeGreaterThanOrEqual(1)`) is satisfied trivially. The test never asserts that **its own** entry was recorded — only that *some* entry exists. This passes silently even when the page-driven capture fails, as long as some earlier test happened to populate `received`.

**Impact:** False-pass test. The end-to-end flow can break without test failure. A real regression in the CORS-or-capture chain would be masked by entries from earlier runs.

**Fix:** Add a `DELETE /received` endpoint to `mock-backend.ts` and call it in `beforeEach` of `end-to-end.spec.ts`. Or assert `received.length === 1` after the test, not `>= 1`.

---

### WARN-05: e2e.yml downloads WiX 3.14.1 binaries without checksum verification

**File:** `.github/workflows/e2e.yml:56-65`

**Issue:** `Invoke-WebRequest -Uri $wixUrl -OutFile $wixZip` downloads from `github.com/wixtoolset/wix3/releases/...` without comparing against a known SHA256. A compromised GitHub release (or DNS hijack on `github.com` resolution) would silently inject malicious `candle.exe`/`light.exe` into the build.

**Impact:** Supply-chain attack vector. MSI build artifact would be trusted (downloaded by hospital IT) but produced by a tampered compiler. The same `release.yml` shares this pattern (release.yml:67).

**Fix:** Pin SHA256:
```powershell
$expectedHash = "ABCDEF..."  # commit this hash
$actualHash = (Get-FileHash -Algorithm SHA256 $wixZip).Hash
if ($actualHash -ne $expectedHash) { throw "WiX hash mismatch" }
Expand-Archive ...
```

---

### WARN-06: `UpdateCheckService.HandleInstallFailureAsync` swallows config-write exception — silent update.enabled remains true

**File:** `src/FingerprintAgent/Update/UpdateCheckService.cs:505-512`

**Issue:** `DisableUpdateEnabledInConfig` can throw (file locked by AV scan, disk full, ACL denied). The outer `catch` (line 508-512) logs the error but does not propagate it. The method then calls `Stop()` (line 514). The next time the operator restarts the service, `_config.Update.Enabled` is still **true** (write failed), and the auto-update timer starts again. The same failure repeats — operator may not notice for days.

**Impact:** Silent retry loop on persistent config write failure. Per the docs (D-43), update.enabled should be disabled on failure; but if disable fails, the service keeps trying. Operators may not correlate "service keeps trying to update" with "config write keeps failing".

**Fix:** Either retry the config write with backoff before giving up, or emit an EventLog entry at **Error** level (currently Warn) so the operator notices. Consider an operator-visible toast if Session 0 is active.

---

### WARN-07: `UpdateCheckService.tempPath` is predictable — concurrent downloads race

**File:** `src/FingerprintAgent/Update/UpdateCheckService.cs:410`

**Issue:** `Path.Combine(Path.GetTempPath(), "FingerprintAgent-Setup.msi")` always uses the same filename. If `TriggerImmediateCheck` is called while a previous download is in progress, `File.Create(tempPath)` truncates the first download. `msiexec /i <truncated>` fails or installs a partial MSI.

**Impact:** Real risk when Programs and Features "Update" verb is implemented in a future plan. Today, only one auto-update Timer fires, so risk is low. With CR-03 (no concurrency guard), the risk compounds.

**Fix:** Use a unique temp filename: `Path.Combine(Path.GetTempPath(), $"FingerprintAgent-Setup-{Guid.NewGuid():N}.msi")` and delete after msiexec completes.

---

### WARN-08: `HealthUrl` constant in CustomActions duplicates HttpServer default — drift risk

**File:** `src/FingerprintAgent.Installer/CustomActions.cs:36` vs `src/FingerprintAgent/Configuration/AgentConfig.cs:24`

**Issue:** `internal const string HealthUrl = "http://127.0.0.1:5043/health"` is hardcoded in the Installer project. The actual binding comes from `AgentConfig.Http.Host + Port` (defaulted from `HttpConfig`). If the agent's default port changes (or operator-configured port differs), the probe targets the wrong URL. The `CheckVcRedistTests.HealthUrl_MatchesHttpServerDefault` test only asserts the string value, not the relationship to AgentConfig.

**Impact:** Silent install failure on non-default ports. MSI assumes 5043, agent runs on something else.

**Fix:** Read port from `programDataConfigPath`'s config.json during install (or pass via CustomActionData from WiX). At minimum, add an integration test that asserts the URL matches the AgentConfig default.

---

### WARN-09: `ProgramDataConfig.wxs` directory ACL is too permissive — `Everyone:GenericAll`

**File:** `installer/Components/ProgramDataConfig.wxs:24-26, 36-38`

**Issue:** Both `cmp_ProgramDataDir` and `cmp_LogsDir` set `<util:PermissionEx User="Everyone" GenericAll="yes" />`. Any local user (including non-admin) can modify or delete the log directory and the config.json file. A malicious local non-admin user can:
- Replace `config.json` with a config that enables auto-update to a malicious GitHub repo (and triggers an update on next service restart)
- Delete log files to hide tracks

**Impact:** Local privilege escalation by untrusted user. Mitigated by `127.0.0.1` HTTP binding but the file system exposure is broader.

**Fix:** Restrict to `SYSTEM:F, Administrators:F, Users:R`. Keep write only for SYSTEM (the service runs as LocalSystem per `Service.wxs:49`).

---

### WARN-10: MajorUpgrade `Schedule="afterInstallExecute"` stops service mid-transaction

**File:** `installer/FingerprintAgent.Installer.wxs:54-57`

**Issue:** `Schedule="afterInstallExecute"` means the upgrade happens during the transaction's InstallExecute phase. The service is stopped via `<ServiceControl Stop="both">` and files are replaced atomically. If anything fails after the service is stopped but before files are fully replaced, the rollback restores files but **the service remains stopped** (rollback doesn't restart the service). Operator sees the install "succeeded" but service won't start until manual restart.

**Impact:** Manual restart required on failed upgrade. D-03 documents `afterInstallExecute` as the chosen schedule for "smooth UX" but doesn't address this edge case.

**Fix:** Switch to `Schedule="afterInstallInitialize"` (full replacement outside transaction) OR add a `<Custom Action="StartServiceOnRollback" />` CA on the rollback sequence.

---

### WARN-11: UpdateCheckService `currentVersion` derived from Library DLL — version desync between Host EXE and Library

**File:** `src/FingerprintAgent/Update/UpdateCheckService.cs:285` + `src/FingerprintAgent/FingerprintAgent.csproj` (no `<Version>` element)

**Issue:** `Assembly.GetExecutingAssembly()` returns `FingerprintAgent.Library.dll` (where `UpdateCheckService` lives). The Library project's `.csproj` has no `<Version>` or `<AssemblyVersion>` element. SDK defaults `AssemblyVersion` to `1.0.0.0`. The Host EXE has the same default. CI passes `/p:Version=1.0.1` which the SDK propagates to both projects' `AssemblyVersion`, so they sync **in CI builds**.

But the local dev build (no `/p:Version`) produces version `1.0.0.0` for both — this is the "release" version, so updates with tag `v1.0.0` are correctly rejected (equal). Releases `v1.0.1+` correctly trigger updates.

However, the e2e workflow passes `/p:Version=0.0.0-e2e`. The SDK may produce `AssemblyVersion=0.0.0` (suffix dropped). A test agent seeing `version=0.0.0` against any release tag >= 0.0.0 will trigger an update — defeating the test fixture.

**Impact:** Local dev workflow is fine. E2E workflow's mock build could trigger update behavior in CI that masks test results.

**Fix:** Set explicit `<Version>` and `<AssemblyVersion>` elements in `FingerprintAgent.csproj` so the version is controlled independently of `/p:Version` MSBuild property. Or pin the e2e version to something clearly non-updateable like `99.0.0-e2e`.

---

## Info Findings

### INFO-01: `OnConfigReloaded` mutates `_config` outside lock — pre-existing pattern, debounce mitigates

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:258-275`

The lock at lines 258-261 protects only the `_config` field assignment. Subsequent `_httpServer?.UpdateCorsConfig`, `_scanner?.UpdatePriority`, `_updateCheckService?.ApplyConfig` calls run outside the lock. ConfigFileWatcher's 300ms debounce + atomic single-write to disk makes concurrent reloads extremely unlikely in practice. Pre-existing pattern.

---

### INFO-02: `MockHttpMessageHandler` defaults to 404 — masks test setup bugs

**File:** `tests/FingerprintAgent.Tests/Update/MockHttpMessageHandler.cs:65-69`

When a test forgets to queue a mock, the handler returns 404. A test assertion like `Assert.Equal(HttpStatusCode.OK, response.StatusCode)` fails with a clear error, but the root cause ("test didn't queue a mock") is buried. Better: throw on unmatched URI with a helpful message.

---

### INFO-03: `customAction.config` has hardcoded .NET 4.8 — no fallback

**File:** `src/FingerprintAgent.Installer/CustomAction.config`

If the runtime doesn't have .NET 4.8 (extremely rare on Win 10/11), the CA DLL fails to load and MSI rolls back with no operator-friendly error.

---

### INFO-04: DEPLOYMENT.md §5.1 Update verb claims browser opens GitHub Releases — verb not implemented

**File:** `DEPLOYMENT.md:189-193`

Per 04-03 SUMMARY's "Skipped" section, the Programs and Features "Update" verb (D-18) is **out of scope for 04-03**. DEPLOYMENT.md §5.1 instructs operators: "Nhấp chuột phải, chọn Update. Trình duyệt sẽ mở trang GitHub Releases". The verb does not exist; clicking Update does nothing.

---

### INFO-05: `ProbeHealth` async-over-sync pattern inside MSI CustomAction

**File:** `src/FingerprintAgent.Installer/CustomActions.cs:155-176`

`task.Wait(HealthProbeTimeout)` blocks the MSI transaction thread for up to 5s. Inside MSI's transaction, this is generally OK but is an antipattern. A synchronous `HttpWebRequest` would be cleaner.

---

### INFO-06: `DisableUpdateEnabledInConfig` is a private method with all logic inline

**File:** `src/FingerprintAgent/Update/UpdateCheckService.cs:517-544`

The method is untestable in isolation (private + file-system-dependent). The tests exercise it via the public path (`DownloadAndInstallAsync_InstallFailure_DisablesUpdateEnabled`), but a dedicated unit test for the JObject parse/write would catch JSON-edge-case regressions.

---

### INFO-07: MajorUpgrade fixed `UpgradeCode` GUID — collision risk is theoretical but real

**File:** `installer/FingerprintAgent.Installer.wxs:34` + `installer/FingerprintAgent.Installer.wixproj:27`

`UpgradeCode = E00CD299-9D25-46A2-837B-226177F20210` is generated via `[guid]::NewGuid()` once. Probability of collision with another vendor's product is ~10^-38, but **vendor GUID collisions have happened historically** (notably with smaller Windows apps). WiX 3.x's `MajorUpgrade` block requires the UpgradeCode to be **stable across the entire product line**; if the agent is ever bundled with another product using the same UpgradeCode, both products' installers will incorrectly identify each other as in-place upgrades.

---

### INFO-08: `ARPHELPLINK` and `ARPURLUPDATEINFO` use placeholder `YOUR-ORG`

**File:** `installer/FingerprintAgent.Installer.wxs:72-73`

The `.wxs` ships with `https://github.com/YOUR-ORG/FingerprintAgent` literally. Hospital IT clicking the Programs and Features "Support" link lands on a 404. Tracked as Phase 5+ work per D-07/D-18.

---

### INFO-09: `IsVcRedistInstalled` fail-open on registry exception

**File:** `src/FingerprintAgent.Installer/CustomActions.cs:78-84`

Catches all Exception during registry probe and returns Success. Comment says "fail-open: better to install and let runtime fail than to refuse on transient registry permissions". This is documented design intent; downstream runtime errors will surface the missing VC++. Acceptable.

---

## Architecture Observations

1. **Single source of truth for `ConfigMerger` is achieved** — the Installer project links `ConfigMerger.cs` via `<Compile Include="..\FingerprintAgent\Configuration\ConfigMerger.cs" Link="..." />` (Installer.csproj:45). Drift between in-process ConfigLoader and msiexec CustomAction is impossible at the algorithm level. **However**, the surrounding write logic (`WriteMergeLog`, atomic write, AppendAllLines vs WriteAllLines) is duplicated in both projects, allowing drift — see WARN-03.

2. **`FingerprintAgentService` lifecycle now manages 3 timers + 1 watcher**: `_healthCheckTimer` (Phase 3), `_configWatcher` (Phase 1), `_updateCheckService._timer` (Phase 4), plus `_cts` (Phase 1). `OnStop` ordering (line 96-209) is correct (most-dependent first, health-check timer disposed LAST per WR-01). But **no integration test** exists for `OnStop` — see missing coverage in CONCERNS.md.

3. **HttpClient is properly reused** in UpdateCheckService — single instance for both `releases/latest` GET and MSI download stream (D-04-03-01). DTO-04-03-01 deviation note: the original plan had two clients, but D-04-03-01 deviation correctly fixes it for testability. No socket exhaustion risk.

4. **MSI binary content trusts `bin/Release` output** — Service.wxs:21-31 lists four files from `$(var.FingerprintAgent.Host.TargetDir)`. If the build pipeline ever swaps these, the unsigned MSI ships tampered binaries (D-07 documents Phase 5+ signing as the mitigation). For v1, this is an accepted risk with a documented threat flag.

5. **CORS contract is verified by E2E twice** — `cors-preflight.spec.ts` (HTTP-only Playwright request API) AND `end-to-end.spec.ts:102-122` (real Chromium via `page.evaluate`). Defense-in-depth against browser-only CORS quirks.

6. **Config reload doesn't disturb the active adapter** — `ScannerManager.UpdatePriority` (ScannerManager.cs:223-255) preserves `_activeAdapter` across config reloads. Per D-09 / Phase 3 documentation. `OnConfigReloaded` calls `UpdatePriority` then `ApplyConfig` — both safe.

7. **WiX 3.x toolchain is genuinely unavailable locally** — the .wixproj is non-SDK legacy. CI downloads WiX 3.14.1 per release.yml:62-75 / e2e.yml:55-64. Local developers must install manually. This is documented in 04-02 SUMMARY as a known limitation.

8. **No code signing (D-07)** — MSI is unsigned in v1. Windows SmartScreen will warn on first run. Documented as Phase 5+ scope.

---

## Verification Recommendations

The following would significantly strengthen confidence before v1.0 ships:

1. **Run an actual MSI build in CI** to confirm CR-01 — push a tag, watch for the artifact. If the build fails on undefined `$(var.Version)`, fix the .wixproj `<DefineConstants>`. The current 04-02 SUMMARY defers this to CI but does not include a smoke test step.

2. **Add a `DELETE /received` endpoint to mock-backend.ts and call it in `beforeEach`** — verifies WARN-04 fix and unblocks reliable E2E coverage.

3. **Write a `OnStop` integration test** — instantiate `FingerprintAgentService`, start in console mode, trigger stop, assert no exceptions thrown, no leaked Timer callbacks. CONCERNS.md flags this as a High-priority gap.

4. **Add a SHA256 pin for the WiX 3.14.1 download** in both `release.yml` and `e2e.yml`. Pins should match the value published at `https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm`.

5. **Add a `merge.log` write semantics test** — verify both ConfigLoader and CustomActions use the same write mode (append). Currently this is only verified by reading the code.

6. **Force `update.enabled` reload race test** — write a test that fires `ConfigFileWatcher.Changed` while `UpdateCheckService.DownloadAndInstallAsync` is mid-stream. Assert that the in-flight download is allowed to complete (current behavior per CR-06 analysis) OR is interrupted (future fix).

7. **Manually install the MSI on a clean Win 10 VM without VC++** — confirm whether the Vietnamese dialog appears. Per CR-07, it's currently defined but never displayed; only `install.log` records the failure.

8. **Verify atomic-write fix (CR-02) with a fault injection test** — start `UpdateCheckService`, simulate process kill mid-`DisableUpdateEnabledInConfig`, restart, assert service starts cleanly (would fail today; would pass after fix).

---

_Reviewed: 2026-08-19_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: deep_
_Working tree: clean (verified via `git status`)_
_24 Phase 4 commits reviewed across 50 files_
