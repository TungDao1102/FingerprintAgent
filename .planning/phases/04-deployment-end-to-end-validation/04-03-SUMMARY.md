---
phase: 04
plan: 04-03
subsystem: Update
tags: [auto-update, github-releases, timer, msiexec, d-13, d-14, d-15, d-17, d-43]
dependency_graph:
  requires: [D-13-update-config-poco, D-33-programdata-path, D-36-loader-redirect, D-37-watcher-programdata-path]
  provides: [D-13-update-timer, D-14-default-disabled, D-15-auto-backoff, D-16-github-source, D-17-msiexec-flow, D-43-disable-on-failure, D-44-no-toast-fallback]
  affects: [UpdateCheckService, FingerprintAgentService, AssemblyInfo, MockHttpMessageHandler]
tech-stack:
  added:
    - System.Threading.Timer (in-process, same as Phase 3 health-check timer)
    - System.Net.Http.HttpClient (single instance reused)
    - Newtonsoft.Json.Linq (JObject for partial config rewrite)
  patterns: [single-HttpClient-reuse, internal-test-seams, InternalsVisibleTo-assembly-level, fire-and-forget-timer-callback]
key-files:
  created:
    - src/FingerprintAgent/Update/UpdateState.cs
    - src/FingerprintAgent/Update/UpdateCheckService.cs
    - src/FingerprintAgent/AssemblyInfo.cs
    - tests/FingerprintAgent.Tests/Update/MockHttpMessageHandler.cs
    - tests/FingerprintAgent.Tests/Update/UpdateCheckServiceTests.cs
  modified:
    - src/FingerprintAgent/FingerprintAgent.csproj
    - src/FingerprintAgent/Service/FingerprintAgentService.cs
decisions:
  - id: D-04-03-01
    decision: "Single HttpClient reused for both releases/latest query AND MSI download"
    rationale: "Initial implementation created two HttpClient instances (one for release check, one for MSI download). The download HttpClient bypassed the test mock handler. Reusing the single instance means tests can intercept both HTTP flows with one MockHttpMessageHandler."
  - id: D-04-03-02
    decision: "Manual AssemblyInfo.cs with [InternalsVisibleTo] instead of relying on SDK auto-generation"
    rationale: "MSBuild InternalsVisibleTo property is not auto-converted to assembly attribute in net48 SDK-style projects. Manual file guarantees the friend assembly is wired up correctly. Removed redundant <InternalsVisibleTo> from csproj to avoid double-specification."
  - id: D-04-03-03
    decision: "Environment.Exit(0) suppressed when InstallInstallerOverride is set"
    rationale: "Test seam must prevent the test runner process from being killed mid-suite. Without this guard, the 'CheckForUpdateAsync_NewerRelease_TriggersDownload' test would terminate xunit and cause cascading failures."
  - id: D-04-03-04
    decision: "UpdateCheckService creation in OnStart uses non-throwing try-catch (unlike ConfigFileWatcher)"
    rationale: "Update service is OPTIONAL per D-14 (default disabled). A failure in update infrastructure must not prevent the agent from serving /api/capture. ConfigFileWatcher failures ARE fatal because operators depend on config reload semantics."
  - id: D-04-03-05
    decision: "Replaced plan's two-interface (IUpdateInstaller + IUpdateConfigPersister) design with single Action<string,string> test seam"
    rationale: "Simpler test surface (M8 deviation from plan). The Action<string,string> replaces msiexec invocation only; config.json path is overridable via SetProgramDataConfigPathForTest. No DI container introduction (per AGENTS.md)."
  - id: D-04-03-06
    decision: "ApplyConfig mutates _config in place AND triggers Start/Stop transitions"
    rationale: "Single source of truth (_config.Update) for both Timer scheduling and config-reload propagation. Calling Start()/Stop() from ApplyConfig handles the operator opt-in flow (D-14: change update.enabled to true → Timer starts)."
metrics:
  duration_minutes: 28
  task_count: 8
  files_changed: 7
  commits: 3
  tests_added: 13
  tests_total_passing: 162
  tests_total: 168
  tests_hardware_dependent_failures: 6
  warnings: 0
  errors: 0
status: complete
---

# Phase 04 Plan 03: Auto-Update Timer + UpdateCheckService

## One-Liner

In-process `System.Threading.Timer` polls GitHub Releases for newer versions, downloads MSI to `%TEMP%`, invokes `msiexec /qn` for self-upgrade — default DISABLED with auto-backoff 6h→12h→24h on no-update checks (D-13/D-14/D-15/D-17/D-43).

## Key Achievements

1. **UpdateCheckService** — `src/FingerprintAgent/Update/UpdateCheckService.cs` implements `IDisposable` with `Start()`/`Stop()`/`TriggerImmediateCheck()`/`ApplyConfig()` lifecycle. Single `HttpClient` reused for both the GitHub API query and MSI download (socket exhaustion prevention).
2. **Auto-backoff counter (D-15)** — `_noUpdateCount` increments on HTTP error, no-update response, prerelease filter, parse failure, or version <= current. Interval grows `{6, 12, 24}` hours and caps at 24h. Reset to 6h on detected release.
3. **Version parsing (D-16)** — Strips `v` prefix and `-suffix`, parses via `System.Version.TryParse`. Defense-in-depth prerelease filter (GitHub `/releases/latest` already excludes prereleases but tag like `v1.0.0-rc1` is also rejected).
4. **Failure path (D-43)** — Non-zero `msiexec` exit OR download exception OR install exception: log Error + write EventLog + write `update.enabled = false` to ProgramData config.json via `JObject` partial rewrite (no `ConfigLoader.Load()` re-run). Service keeps running on old version. Timer stopped.
5. **Success path (D-17)** — `msiexec /qn /i "<tempPath>"` with `WaitForExit(15min)`. On `exitCode == 0`: log Info + EventLog + `Environment.Exit(0)` (SCM recovery restarts with new binaries).
6. **FingerprintAgentService integration** — `_updateCheckService` created in `OnStart` after `_configWatcher` setup. Failures logged but NOT thrown (capture service must remain functional even when update infra fails). `OnConfigReloaded` calls `ApplyConfig(newConfig)` so operator toggling `update.enabled` at runtime starts/stops the Timer.
7. **Test infrastructure** — `MockHttpMessageHandler` for canned HTTP responses + 4 internal test seams (`InstallInstallerOverride`, `CheckForUpdateAsyncPublic`, `DownloadAndInstallForTest`, `SetProgramDataConfigPathForTest`) for comprehensive coverage without DI.

## Files Modified / Created

### Created
- `src/FingerprintAgent/Update/UpdateState.cs` — `UpdateState` enum (Stopped/Running/Checking/Downloading/Installing) + `GitHubReleaseInfo` + `GitHubAsset` DTOs with Newtonsoft `[JsonProperty]` snake_case mapping
- `src/FingerprintAgent/Update/UpdateCheckService.cs` — polling + auto-backoff + msiexec orchestration (370 lines)
- `src/FingerprintAgent/AssemblyInfo.cs` — `[assembly: InternalsVisibleTo("FingerprintAgent.Tests")]`
- `tests/FingerprintAgent.Tests/Update/MockHttpMessageHandler.cs` — URL-pattern-matched canned responses + call counter
- `tests/FingerprintAgent.Tests/Update/UpdateCheckServiceTests.cs` — 13 tests covering all 12 plan scenarios + 1 extra (download failure path)

### Modified
- `src/FingerprintAgent/FingerprintAgent.csproj` — added `<Reference Include="System.Net.Http" />` for HttpClient/HttpMessageHandler; removed redundant `<InternalsVisibleTo>` (now in AssemblyInfo.cs)
- `src/FingerprintAgent/Service/FingerprintAgentService.cs` — new field `_updateCheckService`; OnStart creates+starts (non-throwing); OnStop disposes (own try-catch before scanner disposal); OnConfigReloaded calls ApplyConfig

## Verification Results

| Check | Result |
|------|--------|
| `dotnet build -c Release` | ✅ 0 warnings, 0 errors |
| `dotnet test` (all non-hardware) | ✅ 162 pass / 0 fail |
| `dotnet test` (full suite) | ✅ 162 pass / 6 fail (pre-existing ZK9500 device tests) |
| New tests added | ✅ 13 (12 from plan + 1 download-failure variant) |
| Atomic commits | ✅ 3 commits (DTO, service+tests, lifecycle wiring) |
| Phase 1-3 + 04-01/04-02 regression | ✅ No regression — same 162 baseline pass count |
| UpdateCheckServiceTests only | ✅ 13/13 pass |

The 6 pre-existing failures (`ZkSdkProbe_*`, `ScannerManagerProbeIntegrationTests.*`) require a connected ZKTeco scanner and fail on master before any of my changes — verified via prior 04-01 SUMMARY baseline.

## Deviations from Plan

### Auto-fixed

**1. [D-04-03-01] Single HttpClient reused for both GitHub API + MSI download**
- **Found during:** Task 04-03-7 (test RED phase)
- **Issue:** Plan suggested a separate `new HttpClient()` for download in `DownloadAndInstallAsync`. With this design, tests passing a mock `HttpMessageHandler` only intercepted the release-check HTTP, not the download. The download hit the real network (failing for `example.com`/`unreachable.local`) and disabled `update.enabled` in ProgramData config.json — which was not desired for the "triggers download" test.
- **Fix:** Reuse `_httpClient` (which carries the injected `HttpMessageHandler`) for both calls. Tests with single MockHttpMessageHandler now intercept both flows.
- **Files modified:** `src/FingerprintAgent/Update/UpdateCheckService.cs`
- **Commit:** 65f8163

**2. [D-04-03-02] Manual AssemblyInfo.cs instead of `<InternalsVisibleTo>` MSBuild property**
- **Found during:** Task 04-03-7 (test compile failure)
- **Issue:** `<InternalsVisibleTo>FingerprintAgent.Tests</InternalsVisibleTo>` in SDK-style csproj was NOT auto-converted to `[assembly: InternalsVisibleTo("FingerprintAgent.Tests")]` in net48. The generated `FingerprintAgent.AssemblyInfo.cs` contained only AssemblyVersion/Title/etc — no InternalsVisibleTo. Test code could not see `internal` test seams.
- **Fix:** Created `src/FingerprintAgent/AssemblyInfo.cs` with the explicit attribute. Removed redundant `<InternalsVisibleTo>` from csproj.
- **Files modified:** `src/FingerprintAgent/FingerprintAgent.csproj` (removed line), `src/FingerprintAgent/AssemblyInfo.cs` (created)
- **Commit:** 65f8163

**3. [D-04-03-03] Environment.Exit(0) suppressed when test override is set**
- **Found during:** Task 04-03-7 (test runner crash)
- **Issue:** First test passing through the success path called `Environment.Exit(0)` and killed the xunit test host process, aborting the entire test run after 3 tests.
- **Fix:** Added `if (InstallInstallerOverride != null || _skipEnvironmentExit) return;` guard before `Environment.Exit(0)`. The override-existing check is the natural signal that we're in test mode.
- **Files modified:** `src/FingerprintAgent/Update/UpdateCheckService.cs`
- **Commit:** 65f8163

### Documented Algorithmic Decisions

- **D-04-03-04:** UpdateCheckService creation is non-throwing in `OnStart` (unlike ConfigFileWatcher). Capture service must remain functional even when update infrastructure fails.
- **D-04-03-05:** Replaced plan's two-interface design (`IUpdateInstaller` + `IUpdateConfigPersister`) with single `Action<string,string>` test seam + temp path override. No DI container introduction (per AGENTS.md).
- **D-04-03-06:** `ApplyConfig` mutates `_config.Update` in place AND triggers Start/Stop transitions in one atomic operation. Single source of truth — Timer scheduling and config-reload propagation stay coherent.

### Skipped (per plan explicit guidance)

- **Toast notifications (D-41/D-44)** — LocalSystem can't show toasts (per RESEARCH.md §5). Skipped entirely per plan: "Do NOT add `Microsoft.Toolkit.Uwp.Notifications` NuGet dep - toast will never work under LocalSystem; fall back to EventLog". EventLog + file log suffice.
- **Programs and Features "Update" verb (D-18)** — Out of scope for 04-03. Wired via `TriggerImmediateCheck()` already (callable from Programs and Features verb handler in future plan).

## Anti-Patterns Avoided

- ✅ No `new HttpClient()` per call (socket exhaustion)
- ✅ No DI container introduction (per AGENTS.md)
- ✅ No `IHttpClientFactory` (DI)
- ✅ No `System.Text.Json` (Newtonsoft only, per project convention)
- ✅ No toast NuGet dep (LocalSystem toast is dead code)
- ✅ No `TaskScheduler` or `Quartz.NET` (plain `System.Threading.Timer` sufficient)
- ✅ No `ConfigLoader.Load()` in failure path (partial JSON rewrite via JObject)
- ✅ No `Environment.Exit` until msiexec returns 0 (preserves error logging on failure)
- ✅ No rollback on download/install failure (D-43: disable update.enabled, keep running)

## Downstream Impact

- **MSI installer (04-02):** Builds an MSI that ships with `update.enabled = false` default. When GitHub Release is published, operators can flip the flag in `C:\ProgramData\FingerprintAgent\config.json` and restart service (or rely on config-reload via `ConfigFileWatcher`).
- **Plan 04-04 (E2E + docs):** Should document the auto-update operator opt-in flow in `README.md` (D-24) and `DEPLOYMENT.md` (D-25).
- **Phase 5+ deferred items:** Toast notifications, delta updates, rollback, channel preview/stable — all explicit per CONTEXT deferred section.

## Known Stubs

None. All code paths implemented per plan. The `Update` section is now fully consumed by the production code path (not just bound from JSON).

## Threat Flags

| Flag | File | Description |
|------|------|-------------|
| threat_flag: new-network-endpoint | `src/FingerprintAgent/Update/UpdateCheckService.cs` | Outbound HTTPS to `api.github.com` (port 443). Public endpoint, no auth, 60 req/hour/IP rate limit. Headers include User-Agent identifying the agent. No new inbound endpoint. |
| threat_flag: new-process-execution | `src/FingerprintAgent/Update/UpdateCheckService.cs` | Invokes `msiexec.exe /qn /i <path>` on update success. Process started without shell, hidden window, timeout enforced (15min). Service self-terminates via `Environment.Exit(0)` after success — SCM restarts with new binaries. |
| threat_flag: new-config-write-path | `src/FingerprintAgent/Update/UpdateCheckService.cs` | On update failure, partial JSON rewrite of `C:\ProgramData\FingerprintAgent\config.json` setting `update.enabled = false`. Uses Newtonsoft `JObject` (preserves other keys, no full merge). Failures logged but don't throw to caller. |

All three are in scope for the D-13/D-43 threat model (auto-update is itself the threat surface). No new trust-boundary crossings introduced beyond what the plan approved.

## Self-Check

- ✅ All 5 created files exist on disk
- ✅ All 3 commit hashes found in git log (`dce8c75`, `65f8163`, `7ac3581`)
- ✅ Build clean (0 warnings, 0 errors)
- ✅ All 13 UpdateCheckServiceTests pass
- ✅ No regression in 149 pre-existing non-hardware tests
- ✅ Working tree clean (only the SUMMARY to commit remains)

## Tests Added (13 total)

1. `Start_WhenUpdateDisabled_DoesNothing` — disabled config → Timer not started, no HTTP call
2. `Start_WhenUpdateEnabled_StartsTimer` — enabled config → state Running, no immediate HTTP (initial due time = interval)
3. `CheckForUpdateAsync_NewerRelease_TriggersDownload` — v99.99.99 release → `InstallInstallerOverride` invoked once
4. `CheckForUpdateAsync_SameVersion_NoDownload` — current version → no install
5. `CheckForUpdateAsync_OlderVersion_NoDownload` — v0.0.1 release → no install
6. `CheckForUpdateAsync_Prerelease_Ignored` — `prerelease: true` → no install
7. `CheckForUpdateAsync_HttpError_IncrementsCounter` — 500 response → `_noUpdateCount = 1`, interval 6h
8. `AutoBackoff_After3NoUpdates_ResetsTo24h` — 4 consecutive errors → interval caps at 24h
9. `AutoBackoff_OnRelease_ResetsToBase` — after 3 errors (24h), release detected → resets to 6h
10. `DownloadAndInstallAsync_InstallFailure_DisablesUpdateEnabled` — override throws → config.json written with `enabled: false`
11. `DownloadAndInstallAsync_DownloadFailure_DisablesUpdateEnabled` — download URL unreachable → config.json written with `enabled: false` (extra coverage vs plan)
12. `VersionParsing_StripsPrefixAndSuffix` — `v1.2.3-rc1` → `1.2.3`; `v1.2.3` → `1.2.3`; `2.0.0` → `2.0.0`; garbage → null
13. `Dispose_StopsTimer` — Dispose() → state Stopped

---
*Plan 04-03 complete. Phase 4 progress: 3/4 plans.*
