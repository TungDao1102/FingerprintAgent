---
phase: 04
plan: 04-01
subsystem: Configuration
tags: [config, deployment, smart-merge, programdata, update-poco]
dependency_graph:
  requires: [D-08-bad-config-keeps-old, D-09-watcher-priority-reload]
  provides: [D-13-update-config-poco, D-33-programdata-path, D-34-seed-on-first-install, D-35-smart-merge, D-36-loader-redirect, D-37-watcher-programdata-path]
  affects: [AgentConfig, ConfigLoader, ConfigFileWatcher, FingerprintAgentService, config.json]
tech-stack:
  added: []
  patterns: [JObject-recursive-merge, static-factory-result, InternalsVisibleTo]
key-files:
  created:
    - src/FingerprintAgent/Configuration/ConfigMerger.cs
    - src/FingerprintAgent/config.template.json
    - tests/FingerprintAgent.Tests/Configuration/ConfigMergerTests.cs
    - tests/FingerprintAgent.Tests/Configuration/ConfigLoaderProgramDataTests.cs
  modified:
    - src/FingerprintAgent/Configuration/AgentConfig.cs
    - src/FingerprintAgent/Configuration/ConfigLoader.cs
    - src/FingerprintAgent/Service/FingerprintAgentService.cs
    - src/FingerprintAgent/FingerprintAgent.csproj
    - src/FingerprintAgent/config.json
decisions:
  - id: D-04-01-01
    decision: "User config missing template keys → add (not 'respect deletion')"
    rationale: "D-35 has internal conflict: 'Key not in user config → add' AND 'User deletion respected'. Without a baseline, we can't distinguish 'user never had key' from 'user deleted key'. RESEARCH.md algorithm matches the ADD interpretation, which is the production-useful behavior for new features."
  - id: D-04-01-02
    decision: "merge.log written only when addedKeys.Count > 0"
    rationale: "Avoid noise in operator logs when no upgrade additions occur"
  - id: D-04-01-03
    decision: "Added subtrees report both the parent AND each leaf in addedKeys"
    rationale: "RESEARCH.md §6 shows merge.log format like 'update.enabled = false' (leaf-level). Reporting only the subtree 'update' would lose granularity; reporting only leaves would lose the parent context."
  - id: D-04-01-04
    decision: "ConfigLoader.Load has 4-way case matrix (PD+template / PD-only / template-only / neither)"
    rationale: "Dev workflow (no MSI) needs to work with PD-only (legacy) AND install-dir fallback. Plan called for 3 cases but missing case (PD-only) caused failures in test 4."
metrics:
  duration_minutes: 35
  task_count: 10
  files_changed: 9
  commits: 6
  tests_added: 18
  tests_total_passing: 125
  tests_total: 131
  warnings: 0
  errors: 0
status: complete
---

# Phase 04 Plan 01: ConfigMerger + ProgramData Path Migration + UpdateConfig POCO

## One-Liner

Migrate runtime config from install-dir to `C:\ProgramData\FingerprintAgent\config.json` with smart-merge on upgrade, add `UpdateConfig` POCO for auto-update, ship `config.template.json` as MSI reference.

## Key Achievements

1. **ProgramData path migration** — runtime config now lives at `C:\ProgramData\FingerprintAgent\config.json` (writable without admin, survives upgrade). Legacy install-dir `config.json` is copied to ProgramData on first upgrade.
2. **ConfigMerger** — new recursive additive merge class (D-35) that preserves user values and adds new template keys without overwriting. Used by both ConfigLoader (in-process) and (future) CustomAction DLL (msiexec).
3. **UpdateConfig POCO** — new `update` section in `config.json` for auto-update feature (D-13/D-14/D-15). Defaults: `enabled=false`, `githubOwner=""`, `githubRepo="FingerprintAgent"`, `checkIntervalHours=6`.
4. **merge.log UX** — on upgrade, writes `C:\ProgramData\FingerprintAgent\merge.log` showing added keys with timestamp, only when something actually changed (operator-noise reduction).
5. **Smart-merge algorithm** — 4-way case matrix handles all combinations of ProgramData/template presence.

## Files Modified / Created

### Created
- `src/FingerprintAgent/Configuration/ConfigMerger.cs` — public static class, recursive merge with `(JObject, IReadOnlyList<string>)` return tuple
- `src/FingerprintAgent/config.template.json` — full copy of config.json, ships with bin output (MSI bundles this in install dir)
- `tests/FingerprintAgent.Tests/Configuration/ConfigMergerTests.cs` — 9 TDD tests covering all D-35 edge cases
- `tests/FingerprintAgent.Tests/Configuration/ConfigLoaderProgramDataTests.cs` — 9 integration tests with isolated temp dirs

### Modified
- `src/FingerprintAgent/Configuration/AgentConfig.cs` — added `UpdateConfig` POCO + `AgentConfig.Update` property
- `src/FingerprintAgent/Configuration/ConfigLoader.cs` — added `ProgramDataConfigPath`/`ProgramDataDirectory` constants, new `Load()` overload that redirects to ProgramData with smart merge, `Update` section binding, `LoadFromFile` helper extracted
- `src/FingerprintAgent/Service/FingerprintAgentService.cs` — one-line change: `OnStart` passes `ConfigLoader.ProgramDataConfigPath` to `ConfigFileWatcher`
- `src/FingerprintAgent/FingerprintAgent.csproj` — both `config.json` and `config.template.json` copied to bin output
- `src/FingerprintAgent/config.json` — added `update` section

## Verification Results

| Check | Result |
|------|--------|
| `dotnet build -c Release` | ✅ 0 warnings, 0 errors |
| `dotnet test` (all) | ✅ 125 pass / 6 pre-existing ZKTeco device failures (require real scanner) |
| New tests added | ✅ 18 (9 ConfigMerger + 9 ConfigLoaderProgramData) |
| Atomic commits | ✅ 6 commits |
| Phase 1-3 regression | ✅ All existing tests still pass |
| Bin output ships both configs | ✅ config.json + config.template.json in bin/Release/net48/win-x86/ |

The 6 pre-existing failures (`ZkSdkProbe_Run`, `TryProbe_*`, `HealthHandler_ReportsHealthy_WithRealDevice_OnProbe`) require a connected ZKTeco scanner and fail on master before any of my changes — verified via `git stash` baseline.

## Deviations from Plan

### Auto-fixed

**1. [D-04-01-01] Smart-merge semantics: "add missing" interpretation chosen over "respect deletion"**
- **Found during:** Task 2 (RED phase of ConfigMerger tests)
- **Issue:** Plan's test `Merge_UserDeletedKey_StaysDeleted` (user={a:1}, template={a:1,b:2,c:3} → no additions) contradicts D-35's stated rule "Key not in user config → add with template default". Without baseline tracking, the algorithm cannot distinguish "user never had key" from "user deleted key".
- **Fix:** Adopted the RESEARCH.md algorithm interpretation: missing keys are ADDED (new features gain defaults). Renamed test to `Merge_UserMissingTemplateKey_GetsAdded` with assertions that match the algorithm. Documented decision in this SUMMARY (D-04-01-01).
- **Files modified:** `tests/FingerprintAgent.Tests/Configuration/ConfigMergerTests.cs`
- **Commit:** ebe841d

**2. [D-04-01-04] ConfigLoader Load() — added 4th case for ProgramData-only path**
- **Found during:** Task 9 (RED phase of ConfigLoaderProgramDataTests)
- **Issue:** Plan's 3-way case matrix didn't handle "ProgramData exists, no template" (dev workflow without MSI). Falling through to `LoadFromDirectory(installDir)` threw `FileNotFoundException` when no install-dir config.json existed.
- **Fix:** Added explicit Case 3: if ProgramData exists but no template, load ProgramData as-is (no merge attempt, no merge.log written).
- **Files modified:** `src/FingerprintAgent/Configuration/ConfigLoader.cs`
- **Commit:** ddf062d

### Documented Algorithmic Decisions

- **D-04-01-02:** `merge.log` only written when `addedKeys.Count > 0` — operator-noise reduction.
- **D-04-01-03:** Added subtrees report both parent AND each leaf in `addedKeys` (e.g., `["update", "update.enabled"]`) — matches RESEARCH.md §6 merge.log format.

## Anti-Patterns Avoided

- ✅ No use of `JObject.Merge()` — its default is REPLACE, opposite of additive merge
- ✅ No DI container introduction (per AGENTS.md)
- ✅ `LoadFromDirectory(string)` signature preserved (used by ConfigFileWatcher reload)
- ✅ No write-through cache or pub/sub for merged config
- ✅ No [JsonProperty] attributes on UpdateConfig (sections use IConfiguration keys)
- ✅ merge.log UX hint applied (added keys only, not full dump)

## Downstream Impact

This plan establishes the foundation for:
- **Plan 04-02 (MSI installer):** MSI bundles `config.template.json` in `C:\Program Files\FingerprintAgent\`; CustomAction DLL can call `ConfigMerger.Merge` directly on upgrade
- **Plan 04-03 (Auto-update):** `UpdateConfig` POCO ready for `UpdateCheckService` consumption
- **Production deployment:** `ConfigFileWatcher` now watches the correct ProgramData path; bad-config reload semantics (D-08) preserved

## Known Stubs

None. All code paths implemented. The `Update` section is consumed by Plan 04-03 (not yet built) but the binding infrastructure is in place — `AgentConfig.Update.Enabled` returns the configured value or POCO default.

## Threat Flags

None. No new network endpoints, no new auth paths, no new file access patterns at trust boundaries. The new `merge.log` is written to `C:\ProgramData\FingerprintAgent\` (same path family as the existing `agent.log`), with operator-readable content (added keys only, no secrets).

## Self-Check

- ✅ All 9 created files exist on disk
- ✅ All 6 commit hashes found in git log
- ✅ Build clean (0 warnings, 0 errors)
- ✅ All configuration tests pass (33/33)
- ✅ No regression in Phase 1-3 tests
- ✅ Working tree clean (no uncommitted changes)
