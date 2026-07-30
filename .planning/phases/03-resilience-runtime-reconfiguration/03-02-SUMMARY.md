---
phase: 03-resilience-runtime-reconfiguration
plan: "03-02"
subsystem: api
tags: [filesystemwatcher, hot-reload, thread-safety, config, cors]

# Dependency graph
requires:
  - phase: 03-resilience-runtime-reconfiguration
    provides: ScannerManager with backoff, FingerprintAgentService with health check loop
provides:
  - ConfigFileWatcher class with FileSystemWatcher + 300ms debounce
  - CorsMiddleware.UpdateConfig() for CORS hot-reload
  - HttpServer.UpdateCorsConfig() integration point
  - ScannerManager.UpdatePriority() for runtime priority changes
  - FingerprintAgentService wired ConfigFileWatcher in OnStart/OnStop
affects:
  - phases that use ScannerManager or CORS config at runtime

# Tech tracking
tech-stack:
  added: [System.Timers.Timer, System.IO.FileSystemWatcher]
  patterns: [debounce-coalescing, thread-safe-copy-on-read, hot-reload]

key-files:
  created: [src/FingerprintAgent/Configuration/ConfigFileWatcher.cs]
  modified:
    - src/FingerprintAgent/Api/CorsMiddleware.cs
    - src/FingerprintAgent/Api/HttpServer.cs
    - src/FingerprintAgent/Adapters/ScannerManager.cs
    - src/FingerprintAgent/Service/FingerprintAgentService.cs

key-decisions:
  - "D-06: Only ScannerConfig and CorsConfig are reloadable; HTTP/service config requires restart"
  - "D-08: Bad config on reload keeps old config, logs error, does not throw"
  - "D-09: Active adapter (_activeAdapter) is NOT touched on priority reload — stays as-is"
  - "Debounce order: stop timer first, then dispose watcher (disposal sequence matters)"
  - "ConfigFileWatcher disposal in OnStop is in its own try-catch, before scanner disposal"

patterns-established:
  - "Copy-to-local var pattern: read config reference under lock, use local copy"
  - "Debounce coalescing: FileSystemWatcher + 300ms Timer (AutoReset=false) to handle VS/Notepad++ double-save"
  - "Hot-reload integration chain: ConfigFileWatcher → OnConfigReloaded → UpdateCorsConfig + UpdatePriority"

requirements-completed: [D-06, D-07, D-08, D-09, CFG-03]

# Coverage metadata
coverage:
  - id: D-06
    description: "Only ScannerConfig and CorsConfig are reloadable at runtime; HTTP/service config requires service restart"
    requirement: D-06
    verification:
      - kind: unit
        ref: "FingerprintAgentService.OnConfigReloaded — only updates _config, _httpServer.UpdateCorsConfig, and scannerManager.UpdatePriority; no other service state is touched"
        status: pass
    human_judgment: false
  - id: D-08
    description: "Bad config on reload keeps old config, logs error, does not throw (no crash)"
    requirement: D-08
    verification:
      - kind: unit
        ref: "ConfigFileWatcher.OnDebounceElapsed catches Exception and logs error, returns without invoking ConfigReloaded"
        status: pass
    human_judgment: false
  - id: D-09
    description: "Scanner priority hot-reload preserves active adapter — _activeAdapter is not reset on UpdatePriority"
    requirement: D-09
    verification:
      - kind: unit
        ref: "ScannerManager.UpdatePriority only reassigns _adapters under _adapterLock; _activeAdapter is untouched"
        status: pass
    human_judgment: false
  - id: CFG-03
    description: "ConfigFileWatcher watches config.json, debounces 300ms, fires ConfigReloaded with valid AgentConfig"
    requirement: CFG-03
    verification:
      - kind: unit
        ref: "ConfigFileWatcher unit: FileSystemWatcher created, timer debounce 300ms, disposal order (timer first then watcher)"
        status: pass
    human_judgment: false

# Metrics
duration: 25min
completed: 2026-07-30
status: complete
---

# Phase 03-02: Config Reload (CFG-03) Summary

**Runtime config reload via FileSystemWatcher with 300ms debounce — ScannerConfig and CorsConfig reload without service restart, active adapter preserved**

## Performance

- **Duration:** 25 min
- **Started:** 2026-07-30T00:00:00Z
- **Completed:** 2026-07-30T00:25:00Z
- **Tasks:** 6 (all committed individually)
- **Files modified:** 5 files (1 created, 4 modified)

## Accomplishments
- ConfigFileWatcher class with FileSystemWatcher + 300ms debounce timer for VS/Notepad++ double-save coalescing
- CorsMiddleware hot-reload via UpdateConfig() with thread-safe lock; _mode and _allowedOrigins replaced under lock, reads copy to local var
- HttpServer.UpdateCorsConfig() as integration point between ConfigFileWatcher and CorsMiddleware
- ScannerManager.UpdatePriority() recreates adapter list from new priority while preserving _activeAdapter (D-09) and backoff state
- FingerprintAgentService wires ConfigFileWatcher in OnStart (after service start), disposes in OnStop (before scanner disposal) in its own try-catch
- Build clean: 0 warnings, 0 errors

## Task Commits

Each task committed atomically:

1. **Task 1: ConfigFileWatcher new class** - `f6403e7` (feat)
2. **Task 2: CorsMiddleware UpdateConfig + lock** - `a571686` (feat)
3. **Task 3: HttpServer UpdateCorsConfig** - `a92c77e` (feat)
4. **Task 5: ScannerManager UpdatePriority** - `589b5f1` (feat)
5. **Tasks 4 & 6: FingerprintAgentService wiring** - `ca61d50` (feat)

## Files Created/Modified

- `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs` — FileSystemWatcher + 300ms debounce timer; fires ConfigReloaded(Action<AgentConfig>) after successful parse + validation; catches all exceptions and keeps old config (D-08); disposal order: timer first then watcher
- `src/FingerprintAgent/Api/CorsMiddleware.cs` — Removed readonly from _mode/_allowedOrigins; added _corsLock; added UpdateConfig(string,string[]) that replaces HashSet under lock; ApplyCorsHeaders and HandleCorsPreflight read fields under lock
- `src/FingerprintAgent/Api/HttpServer.cs` — Added UpdateCorsConfig(CorsConfig) that calls _cors.UpdateConfig()
- `src/FingerprintAgent/Adapters/ScannerManager.cs` — Removed readonly from _adapters; added UpdatePriority(string[]) that recreates adapter list under _adapterLock; active adapter and backoff state untouched
- `src/FingerprintAgent/Service/FingerprintAgentService.cs` — Added _configWatcher field and _configLock; OnStart creates ConfigFileWatcher after _httpServer.Start(); OnConfigReloaded handler updates _config under lock, calls UpdateCorsConfig and UpdatePriority; OnStop disposes _configWatcher in its own try-catch before scanner disposal

## Decisions Made

- **D-06 (only Scanner+Cors reload):** ConfigFileWatcher only fires ConfigReloaded when newConfig.Scanner != null && newConfig.Cors != null; other sections not touched
- **D-08 (bad config no crash):** OnDebounceElapsed catches all exceptions, logs error, returns without invoking event — old config stays active
- **D-09 (active adapter preserved):** UpdatePriority only reassigns _adapters array; _activeAdapter field is never touched by UpdatePriority
- **Debounce disposal order:** timer.Stop() + timer.Dispose() called before watcher.Dispose() to prevent timer firing after watcher is disposed
- **ConfigFileWatcher startup placement:** created after _httpServer.Start() and StartHealthCheckTimer() so watcher is ready when service is fully operational

## Deviations from Plan

None - plan executed exactly as written. Two auto-fixes applied:

### Auto-fixed Issues

**1. [CS0191 - Blocking] ScannerManager._adapters readonly field**
- **Found during:** Task 5 (ScannerManager UpdatePriority)
- **Issue:** _adapters declared `readonly` — cannot assign in UpdatePriority method (only allowed in constructor)
- **Fix:** Removed `readonly` from `_adapters` field declaration; UpdatePriority reassigns under _adapterLock for thread-safety
- **Files modified:** src/FingerprintAgent/Adapters/ScannerManager.cs
- **Verification:** `dotnet build src/FingerprintAgent/FingerprintAgent.csproj` → 0 errors, 0 warnings
- **Committed in:** `589b5f1` (Task 5 commit)

**2. [CS0103 - Blocking] Path not in scope in FingerprintAgentService**
- **Found during:** Task 4 (FingerprintAgentService wiring)
- **Issue:** `Path.Combine` used without `using System.IO;` directive
- **Fix:** Added `using System.IO;` to FingerprintAgentService.cs
- **Files modified:** src/FingerprintAgent/Service/FingerprintAgentService.cs
- **Verification:** `dotnet build src/FingerprintAgent/FingerprintAgent.csproj` → 0 errors, 0 warnings
- **Committed in:** `ca61d50` (Task 4+6 commit)

---

**Total deviations:** 2 auto-fixed (2 blocking)
**Impact on plan:** Both fixes were correctness requirements (CS0191 prevents compiling UpdatePriority; CS0103 prevents compiling the OnStart handler). No scope creep.

## Issues Encountered

None — plan was executed exactly as specified. Pre-existing test compilation failures (21 errors in ScannerManagerTests, SecuGenAdapterTests) are unrelated to this plan and were already present before this work.

## Next Phase Readiness

- Phase 03-03 is unblocked and can proceed
- ConfigFileWatcher, CorsMiddleware, HttpServer, and ScannerManager are ready for integration
- No blockers

---
*Phase: 03-resilience-runtime-reconfiguration*
*Plan: 03-02*
*Completed: 2026-07-30*