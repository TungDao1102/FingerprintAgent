# FingerprintAgent — End-to-End Code Review

**Date:** 2026-08-20
**Scope:** HEAD (`85b6db10`) + uncommitted changes to `src/FingerprintAgent/Update/UpdateCheckService.cs`
**Base:** `887568ed` (first commit) — 261 commits, 4 development phases
**Build status (before fixes):** UNCOMMITTED CHANGES BREAK THE BUILD
**Test status (before fixes):** 184/185 passed against **stale binaries**

## Methodology

1. Read `AGENTS.md` (repo root) for documented anti-patterns, conventions, and known issues.
2. Read `.planning/codebase/CONCERNS.md` for the existing concern list.
3. Review code across all modules using `codegraph_explore`, `read`, and `lsp_diagnostics`.
4. Pay special attention to the uncommitted changes to `UpdateCheckService.cs`.
5. Verify findings by running `dotnet build`, `dotnet test`, and inspecting source.

Items already documented in `AGENTS.md` (anti-patterns table) and `CONCERNS.md` are referenced but **not re-listed in full**. Items marked "pre-existing" have an upstream ticket and are tracked separately.

---

## Critical Issues (must fix — security / correctness / data loss)

### C1. Build is broken — 3 compile errors + 1 warning from uncommitted changes

**Evidence:** `dotnet build FingerprintAgent.sln -c Release` exits non-zero:
```
UpdateCheckService.cs(268,13): error CS0103: The name '_programDataConfigPathPathForTest' does not exist
UpdateCheckService.cs(406,27): error CS7036: There is no argument given that corresponds to the required parameter 'ct'
UpdateCheckService.cs(476,55): error CS1501: No overload for method 'GetStreamAsync' takes 2 arguments
UpdateCheckService.cs(51,24):  warning CS0649: Field '_programDataConfigPathOverride' is never assigned to
```

**Root causes:**
- **L268:** `SetProgramDataConfigPathForTest` writes to a field (`_programDataConfigPathPathForTest`) that was never declared. The declared field is `_programDataConfigPathOverride` (L51).
- **L406:** `await DownloadAndInstallAsync(release)` is the production caller — the refactor added a `CancellationToken ct` parameter but the caller wasn't updated.
- **L476:** `_httpClient.GetStreamAsync(url, ct)` — `HttpClient.GetStreamAsync(Uri)` has only a 1-arg overload in net48.

**Fix:**
- L268: `_programDataConfigPathOverride = path;` (correct the typo).
- L406: `await DownloadAndInstallAsync(release, ct).ConfigureAwait(false);`
- L476: drop the `ct` argument from `GetStreamAsync` (the token is already propagated to `CopyToAsync` at L479).

---

### C2. Test seam silently broken — tests run against stale binaries

**Evidence:**
- `tests/.../Update/UpdateCheckServiceTests.cs:344, 389, 538` all call `service.SetProgramDataConfigPathForTest(configPath)` then assert against `configPath` (a temp directory).
- Due to C1, the setter writes to a nonexistent field — so `DisableUpdateEnabledInConfig` (L605) falls through to the real `ConfigLoader.ProgramDataConfigPath` (`C:\ProgramData\FingerprintAgent\config.json`), which is **never the temp test path**.
- The 184/185 pass rate comes from `dotnet test` re-using the last-good `bin/Debug/FingerprintAgent.Tests.dll`. Build-cache hit means broken tests **appear green**.
- The first `dotnet test` after a rebuild will fail at minimum the 3 tests above; in CI without the real ProgramData path, all 3 fail.

**Fix:**
- After fixing C1, run `dotnet test --no-incremental` (or delete `bin/`/`obj/` first) to confirm the 3 affected tests actually pass against the fixed code.

---

### C3. `Environment.Exit(0)` removal breaks the SCM-restart contract (silent update failure) — **VERIFIED FALSE on 2026-08-20**

**Original claim:**
- Old `RunMsiexec` was synchronous with `WaitForExit((int)InstallTimeout.TotalMilliseconds)`. After `exitCode == 0`, the old code called `Environment.Exit(0)` to terminate the process — **the SCM detected the process exit and restarted the service with the new binaries**.
- New `RunMsiexecDetached` (L543) starts msiexec and returns immediately. The L537 comment asserts "msiexec will request SCM stop (30s graceful) and restart with new binaries" — this is only true **if the MSI's WiX ServiceControl table** contains `Stop="install"` / `Start="install"` rows for FingerprintAgent.

**Verification (post-review):**
- WiX source **is** in the repo at `installer/Components/Service.wxs:62-68`:
  ```xml
  <ServiceControl Id="svc_FingerprintAgent_Control"
                  Name="FingerprintAgent"
                  Start="install"
                  Stop="both"
                  Wait="yes"
                  Timeout="30"
                  Remove="uninstall" />
  ```
- `Stop="both"` + `Wait="yes"` + `Timeout="30"` → msiexec gracefully stops the running service (30 s) before installing files.
- `Start="install"` → msiexec starts the new service with new binaries after install completes.
- The comments in the new code at L537 and L563 are **accurate**. The detached-msiexec approach is correct given the current `Service.wxs`.

**Decision:** No fix needed. C3 is a false alarm.

**Caveat:** if `Service.wxs` is ever modified to remove `Stop="both"` / `Start="install"`, the detached-msiexec flow silently breaks. The 30 s `Stop` window is also tight — if `FingerprintAgentService.OnStop` exceeds 30 s, msiexec force-kills the process. Not currently a problem (OnStop is fast), worth a regression test if shutdown grows slower.

---

### C4. `InstallTimeout` constant is dead code after the refactor

**Evidence:** `UpdateCheckService.cs:29` declares `InstallTimeout = TimeSpan.FromMinutes(15)`. Grep shows 1 match (declaration site). The old `RunMsiexec` consumed it via `WaitForExit((int)InstallTimeout.TotalMilliseconds)`; new `RunMsiexecDetached` has no `WaitForExit` at all.

**Implication:** msiexec can hang indefinitely with no supervision.

**Fix:** remove the constant. It lies about behavior. Document "no timeout; msiexec self-monitors via MSI ServiceControl + agent's own `Environment.Exit`".

---

## Important Issues (should fix — bugs, error handling gaps, race conditions)

### I1. `CancellationToken` propagation is dead in production

**Evidence:** `UpdateCheckService.cs:251, 305` — both production callers (`CheckForUpdateAsyncPublic`, `TimerCallback`) pass `CancellationToken.None`. The newly-added `ct` parameter on `DownloadAndInstallAsync` and the `Task.Delay(PreInstallDelay, ct)` (L505) only ever sees `None` in production. The cancellation plumbing is exercised only by tests.

**Implication:** The 10s `PreInstallDelay` cannot be cancelled in production — operator clicking "Disable updates" mid-delay must wait the full 10s for the next event-loop iteration.

**Fix:** add an `internal CancellationTokenSource _shutdownCts` field; `Stop()` cancels it. `TimerCallback` and `CheckForUpdateAsync` link `_shutdownCts.Token` (not `None`). L505 becomes effectively cancellable on shutdown.

---

### I2. `ConfigFileWatcher` enables events before subscribing handler

**Evidence:** `ConfigFileWatcher.cs:36-41`:
```csharp
_watcher = new FileSystemWatcher(...) { ..., EnableRaisingEvents = true };
_watcher.Changed += OnRawChanged;
```

**Implication:** Any file change that occurs between the `new FileSystemWatcher` constructor returning and the `+=` line is lost. Cold-start race on a system that actively modifies config.json (e.g., during MSI upgrade, or `git checkout`).

**Fix:** swap order — subscribe first, then set `EnableRaisingEvents = true`.

---

### I3. `HttpServer.ProcessRequestLoop` `ContinueWith` exception flattening (pre-existing, CONCERNS.md L31-34)

**Evidence:** `HttpServer.cs:113-120` — `t.Exception` is logged via `.ToString()` (no `Flatten()`). Surfaces as `AggregateException → AggregateException → actual exception` in logs.

**Fix:** `var flatEx = t.Exception?.Flatten(); _logger?.Error(cid, $"Unhandled request error: {flatEx?.InnerException?.Message}\n{flatEx}");`

---

### I4. `HttpServer.HandleRequestAsync` swallows all exceptions silently

**Evidence:** `HttpServer.cs:188-196` — bare `catch (Exception)` with no logging, no correlation ID, no stack trace preservation. The original exception is **only** logged via the `ContinueWith` continuation (L118), but the `await handlerTask` inside the `try` rethrows the same exception to this catch — which discards it.

**Implication:** A bug that throws inside `HandleRequestAsync` produces a 500 response with zero diagnostic context beyond the line number in `catch (Exception)`.

**Fix:** `catch (Exception ex) { _logger?.Error(correlationId, $"HandleRequest: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); ... }`

---

### I5. `ScannerManager.IsConnected` reads `ActiveAdapter` twice with intervening null check (minor)

**Evidence:** `ScannerManager.cs:48-50` — both branches do `ActiveAdapter?.IsConnected ?? false`. `ActiveAdapter` getter takes `_adapterLock`; in `_mockMode`, the `ActiveAdapter` is set in the constructor. `ActiveAdapter` property getter still acquires the lock twice. Not a bug, just inefficient.

**Fix:** cache `var active = ActiveAdapter;` once under lock, then evaluate `active?.IsConnected ?? false`.

---

### I6. `DisableUpdateEnabledInConfig` logs full ProgramData path

**Evidence:** `UpdateCheckService.cs:625` — `_logger?.Info(null, $"UpdateCheck: wrote update.enabled=false to {path}");` logs the absolute system path (`C:\ProgramData\FingerprintAgent\config.json`) to the log file and EventLog.

**Implication:** If logs are forwarded off-host (typical in HIS deployments), internal filesystem layout leaks.

**Fix:** log only the file name, not full path. E.g. `$"UpdateCheck: wrote update.enabled=false to {Path.GetFileName(path)}";`

---

### I7. `ConfigMerger` array merge appends to end, may break priority semantics

**Evidence:** `ConfigMerger.cs:99-117` — when template array has elements missing from user array, the new elements are **appended to the end**. For `scanner.priority`, this means a template upgrade that introduces a new vendor appends it after all user-configured entries.

**Implication:** Quiet priority-order changes on upgrade.

**Fix:** insert template additions at the position they hold in the template (preserving template order). Matches operator expectations of "upgrade inherits layout".

---

### I8. `HttpServer.Stop()` worker-drain timeout mismatch (pre-existing, CONCERNS.md L66)

**Evidence:** `HttpServer.cs:85` — 5s for `_workerTask`, but 30s for `_inFlightRequests`. If `GetContextAsync()` is blocked, 5s elapses without cancellation reaching the worker, then `_listener.Close()` is called while worker is alive → `ObjectDisposedException` (handled) but request pipeline not cleanly drained.

**Fix:** reorder Stop() to call `_listener.Stop()` **before** `_cts.Cancel()` so `GetContextAsync()` returns immediately with `HttpListenerException`.

---

## Minor Issues / Nits

### N1. Unused `_programDataConfigPathOverride` field is `private` despite being a test seam
After fixing C1, the field is referenced only by `DisableUpdateEnabledInConfig` (L605) — make it `internal` for symmetry with `InstallInstallerOverride`.

### N2. `MockScannerAdapter` cancellation honor (CONCERNS.md L76)
Pre-existing, not new.

### N3. `FutronicAdapter` pixel inversion is unverified
`FutronicAdapter.cs:14-19` — `TODO (pre-production)`: pixel inversion (`255 - rawValue`) is not verified against a real test fingerprint. If wrong, all Futronic captures are inverted.
**Fix:** add a manual integration test using a known reference print.

### N4. `CaptureHandler` request body size unbounded (CONCERNS.md L173)
Pre-existing.

### N5. `ConfigLoader.LoadFromFile` substring match for JSON parse errors (CONCERNS.md L110-117)
Pre-existing.

### N6. `BaseScannerAdapter.ScanAsync` cancellation token not propagated past initial check (CONCERNS.md L94-100)
Pre-existing.

### N7. `Stop()` is called for two semantically different reasons
`UpdateCheckService.cs:540` calls `Stop()` after a successful install start. `Stop()`'s doc says "Stops the update Timer" — semantically correct here, but the method name is identical to the public API `Stop()` used by external `ApplyConfig(enabled:false)`. Consider renaming or adding a clarifying comment.

### N8. `MockHttpMessageHandler` accepts `Func<Uri, bool>` — cannot inspect request headers
Minor: `Func<HttpRequestMessage, bool>` would expose headers (useful for testing the `Accept: application/vnd.github+json` header at L336).

---

## Strengths (what's well-done)

- **`AtomicFileWriter`** (`Configuration/AtomicFileWriter.cs:35-118`) — exemplary. Documents its durability gap explicitly (`TODO(04)`), uses `File.Replace` to preserve ACLs, validates path, cleans up temp on failure. Self-documenting and reusable across agent + installer CA.
- **`ConfigMerger`** (`Configuration/ConfigMerger.cs`) — correctly distinguishes "user deleted key" from "template shipping null default" (WARN-02), reports per-leaf added keys, handles arrays element-wise with `JToken.DeepEquals`.
- **`ScannerManager`** — clean lock-ordering comment, fail-fast on unknown vendor names, priority fallback with active-adapter preservation, lock-snapshot pattern for concurrent `UpdatePriority` + `ScanAsync`.
- **`ZKTecoAdapter`** — correctly avoids `ZkTecoFingerHost.Close()` from Dispose (process-wide singleton), has `_hostLock` for concurrent `ProbeConnection`/`Scan`, handles the `AlreadyInit` quirk with explicit comment, logs vendor error codes.
- **`CorsMiddleware`** — atomic HashSet swap with `lock(_corsLock)`, ordinal-ignore-case comparer, default-to-wildcard with explicit allowlist mode.
- **`CustomActions.cs`** (Installer CA) — VC++ redist detection with fail-open, `/health` probe with multi-attempt retry, explicit `StopRunningService` + `StartServiceAfterRollback` pair.
- **`MockScannerAdapter` + `MockScannerAdapterWithSettableProperties`** — good test-double infrastructure; the latter enables deterministic test scenarios without Moq magic.
- **Backoff state** (`BackoffDelaysSeconds = {10, 30, 60, 120}s`, `BackoffHours = {6, 12, 24}h`) — explicit constants, exponential-then-cap, separate counters for capture (transient) and update check (HTTP).
- **Correlation IDs** (`AgentLogger.GenerateCorrelationId()`) — 10-char hex, threaded through HTTP → adapter → update. Good for log aggregation.
- **`FingerprintAgentService.OnStop`** — explicit `_healthCheckTimer?.Dispose()` ordering (dispose timer AFTER scanner), `ZkTecoFingerHost.Close()` called exactly once with bare-catch swallow.
- **Update concurrency guards** (CR-03 in-flight skip, CR-05 finally-state preservation, CR-06 in-flight deferral) — well-documented race conditions, tested via `SetStateForTest` injection.

---

## Overall Assessment

**Ship-readiness:** NOT READY TO COMMIT.

The uncommitted changes break the build (3 compile errors + 1 warning). The test seam they introduce is broken so 3 of the new tests would fail if rebuilt — they currently appear green only because `dotnet test` reuses stale binaries. Compounding the issue, the refactor removed `Environment.Exit(0)` (the old SCM-restart contract) and replaced it with a detached msiexec that relies on MSI `ServiceControl` entries that are not visible in the reviewed source.

**Top 3 priorities:**
1. **Fix the compile errors (C1)** — typo'd field name on L268, missing `ct` arg on L406, `GetStreamAsync` arity on L476.
2. **Rebuild + re-run tests (C2)** — `dotnet test --no-incremental` or delete `bin/`/`obj/` first. If those 3 tests fail, the bug isn't fully fixed.
3. **Decide the SCM-restart contract (C3)** — verify WiX ServiceControl rows OR restore `Environment.Exit(0)` with a delay as a fallback.

**Secondary priorities** (post-merge): fix `EnableRaisingEvents` ordering (I2), make `CancellationToken` propagation actually live in production (I1), drop dead `InstallTimeout` constant (C4), fix the silent exception swallow in `HttpServer.HandleRequestAsync` (I4).

**Not in scope for this review (already documented elsewhere):**
- `AGENTS.md` anti-patterns table (HttpServer shutdown CS4014, ZKTeco Close singleton, two Program.cs files, project-wide nullable toggle).
- `.planning/codebase/CONCERNS.md` items are referenced but not re-listed.
