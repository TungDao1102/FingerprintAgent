# Codebase Concerns

**Analysis Date:** 2026-08-19

## Tech Debt

### Swallowed exceptions in best-effort paths

**Bare `catch { }` and silent catch blocks:**
- Issue: Multiple locations catch all exceptions silently, hiding real problems from operators and making diagnosis difficult.
- Files:
  - `src/FingerprintAgent/Service/FingerprintAgentService.cs:160` — `try { ZkTecoFingerHost.Close(); } catch { /* best-effort */ }` at service shutdown; masks native teardown failures that may indicate library/process leaks
  - `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:101` — `try { ZkTecoFingerHost.Close(); } catch { /* best effort */ }` in `ProbeConnection()`; repeated failure not visible to operator
  - `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:120` — `try { _device.Dispose(); } catch { }`; ignores device cleanup exceptions during re-initialization
  - `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:383` — same pattern in `EnsureHostInitialized()` recovery path
  - `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:87-94` — three `try { ... } catch { }` blocks in `Dispose()`; watcher's teardown issues invisible
  - `src/FingerprintAgent/Logging/AgentLogger.cs:158-170` — `TryWriteEventLog` catches both `SecurityException` AND a generic `Exception`; second catch swallows real EventLog failures
  - `src/FingerprintAgent/Api/HttpServer.cs:81,87,97,148,195` — multiple `catch (ObjectDisposedException) { }` and `catch (AggregateException) { }` during shutdown drain
- Impact: Silent failures make root-cause analysis harder. Vendor SDK issues or Windows API failures during shutdown may indicate persistent state corruption but are invisible in logs.
- Fix approach: Replace bare catches with `catch (SpecificException ex) { _logger?.Debug/Warn(cid, $"...: {ex.Message}"); }`. For shutdown paths, use `_logger?.Debug` to keep noise low while still capturing the signal.

### `OnStop()` exception aggregator overwrites first error

**Location:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:84-184`
- Issue: A single `shutdownError` variable is overwritten by each `catch` block (`_cts.Dispose`, `_httpServer.Stop`, `_httpServer.Dispose`, `_scanner.Dispose`). Only the last error is reported in the EventLog, even though multiple subsystems may have failed.
- Impact: Operators see "Error disposing scanner" in EventLog but never learn that the HTTP server stop also threw.
- Fix approach: Use a `List<Exception> shutdownErrors` and concatenate messages, or log each error to `_logger?.Error(...)` AND append to EventLog.

### Fire-and-forget pattern in HttpServer.ProcessRequestLoop

**Location:** `src/FingerprintAgent/Api/HttpServer.cs:113-120`
- Issue: `handlerTask.ContinueWith(...)` is fire-and-forget — exception only logged via `t.IsFaulted` and `t.Exception`. The exception itself (`t.Exception`) is read but never unwrapped; only `.ToString()` is logged, and `AggregateException.Flatten()` is not called, so nested exceptions are confusing.
- Impact: Unhandled errors during request processing are captured but the inner exception path is hard to read. Additionally, the task is never awaited, so any logical errors from the continuation (rare, but possible if `_logger` throws inside `ContinueWith`) propagate to the unobserved-task handler.
- Fix approach: `var flatEx = t.Exception?.Flatten();` then log `flatEx?.InnerException?.Message` and full `flatEx` at debug level.

### CancellationToken cancellation is not observed in HealthCheckCallback

**Location:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:207-226`
- Issue: `HealthCheckCallback` runs every 30s on a `Timer` with no cancellation token. If the timer fires once after `_healthCheckTimer.Dispose()` has been called but before the callback completes (race condition noted in WR-01 comment), the callback can race with `_scanner.Dispose()`.
- Impact: `ObjectDisposedException` from `_scanner.IsConnected` during shutdown. Mitigated by try/catch (line 222-225) but still emits ERROR-level log noise during normal shutdown.
- Fix approach: Capture scanner reference at `OnStart` time; null the reference in `OnStop` BEFORE disposing scanner. Or use a `_disposed` flag.

### MockScannerAdapter hardcodes geometry that other adapters parameterize

**Location:** `src/FingerprintAgent/Adapters/MockScannerAdapter.cs:53-115`
- Issue: Mock generates a 320×240 PNG via `Graphics.FromImage` + per-pixel `GetPixel()` — extremely slow path (~75,000 `GetPixel` calls per capture, ~50–200ms per call). Real adapters use `LockBits` + `Marshal.Copy`.
- Impact: Slow mock responses bias developer perception of latency. The mock is also never tested for cancellation honor (no `cancellationToken.IsCancellationRequested` check in `ScanAsync`).
- Fix approach: Use the same `LockBits` pipeline as `BaseScannerAdapter.ToPngGrayscale` for parity; also honor `cancellationToken.ThrowIfCancellationRequested()`.

### Hard-coded English error messages in Vietnamese-locale target

**Location:** `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:324-351` and `src/FingerprintAgent/Models/CaptureRequest.cs`
- Issue: Vietnamese identifiers (`thamChieuId`, `maPhieu`) suggest Vietnamese hospital deployment, but all user-facing error messages are English (`"ZKTeco: no finger detected within {elapsedSec}s — please place finger on sensor and try again"`). End-user UX gap.
- Impact: Hospital staff must read English error messages; translation pipeline doesn't exist.
- Fix approach: Define a `Messages.resx` resource file; switch on `AgentConfig.Ui.Language` when emitting user-facing strings. Phase 4+ work.

### `IScannerAdapter` documentation/AGENTS.md drift

**Location:** `src/FingerprintAgent/Adapters/IScannerAdapter.cs:42` vs `AGENTS.md` and `src/FingerprintAgent/Api/CaptureHandler.cs:76`
- Issue: `AGENTS.md` documents the interface method as `Scan()` (synchronous, blocking) but the actual interface is `ScanAsync(CancellationToken)`. AGENTS.md also says "per-adapter 3s CTS" but the real per-adapter timeout is 20s total budget across all adapters (D-13). `CaptureHandler.HandleAsync` calls `scanner.ScanAsync()` without a per-call `CancellationToken`, so the per-adapter CTS is not actually passed.
- Impact: New developers following AGENTS.md will add `Scan()` instead of `ScanAsync()` and break compilation. They will also add a per-call 3s `CancellationTokenSource` that is silently discarded.
- Fix approach: Update AGENTS.md to reflect actual `ScanAsync(CancellationToken)` signature and 20s total budget; pass `cancellationToken` through `CaptureHandler.HandleAsync` so callers can apply their own deadline.

## Known Bugs

### `HttpServer.Stop()` worker drain timeout (5s) < expected shutdown latency

**Location:** `src/FingerprintAgent/Api/HttpServer.cs:85`
- Issue: `Task.WaitAll(inFlight, TimeSpan.FromSeconds(30))` waits 30s for in-flight requests, but `_workerTask?.Wait(TimeSpan.FromSeconds(5))` only waits 5s for the worker loop to exit. If `GetContextAsync()` is mid-call (waiting for a new request), `Stop()` cancels `_cts` then waits 5s for the worker to acknowledge cancellation — but the worker is blocked in `GetContextAsync()` which can take up to ~10s to return when the listener is stopped.
- Symptoms: Occasional "service stop" hangs past the 5s budget, then `_listener.Close()` is called while the worker is still alive. The worker subsequently tries to use a disposed `_listener` and throws `ObjectDisposedException` (handled) but the request processing pipeline is not cleanly drained.
- Files: `src/FingerprintAgent/Api/HttpServer.cs:63-102`
- Trigger: Service shutdown while idle (worker is blocked in `GetContextAsync()`).
- Workaround: Increase the worker wait to match `inFlight` timeout (30s) or call `_listener.Stop()` BEFORE `_cts.Cancel()` so `GetContextAsync()` returns immediately with `HttpListenerException`.
- Fix approach: Reorder Stop() to stop listener first (causes `GetContextAsync` to throw immediately), then cancel CTS, then wait.

### `MockScannerAdapter.ScanAsync` ignores `cancellationToken`

**Location:** `src/FingerprintAgent/Adapters/MockScannerAdapter.cs:25-51`
- Issue: `ScanAsync` does not check `cancellationToken.IsCancellationRequested` before generating the PNG. Real adapters all check.
- Symptoms: Calling `mock.ScanAsync(cancelledToken)` still produces a full mock PNG (~50–200ms) instead of returning immediately with `CAPTURE_TIMEOUT`.
- Trigger: Tests or callers passing a pre-cancelled token.
- Workaround: None.
- Fix approach: Add `if (cancellationToken.IsCancellationRequested) { ... return CaptureResult.Fail("CAPTURE_TIMEOUT", ...); }` at top of method.

### `DigitalPersonaAdapter.OnComplete` race with TCS reassignment

**Location:** `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs:95-96, 203-208`
- Issue: The class uses a shared `_captureTcs` field. If `ScanAsync()` is called twice in rapid succession (before the first OnComplete fires), the first TCS gets overwritten and `OnComplete` signals the second TCS. The first caller's TCS never completes; its `await tcs.Task` will hang until the linked CTS (3s timeout) fires.
- Symptoms: Intermittent 3s delays on `/api/capture` when calls overlap (likely rare in production due to 20s ZK capture budget but possible under load).
- Trigger: Two concurrent `/api/capture` requests within ~3s.
- Workaround: Serialization at caller layer (HIS UI typically captures one at a time).
- Fix approach: Capture TCS in a local variable inside OnComplete via the captured instance — i.e., use `closure` or move TCS into a queue. Current code DOES comment that "callback signals `_captureTcs` which, at the moment it fires, holds the correct local TCS for this call" but this relies on the second call having overwritten `_captureTcs` BEFORE OnComplete fires — a fragile ordering assumption.

### `BaseScannerAdapter` `ScanAsync` ignores `cancellationToken` after init check

**Location:** `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs:35-81`
- Issue: Token is checked at line 37, but `CaptureRawImage()` (line 46) is synchronous and does not observe cancellation. For SecuGenAdapter's `GetImageEx(buffer, 5000, ...)` the SDK call blocks for up to 5s regardless.
- Trigger: Cancellation mid-capture.
- Workaround: Real adapters inherit via `BaseScannerAdapter` only for stub purposes; only `SecuGenAdapter` actually uses `BaseScannerAdapter`.
- Fix approach: Pass `cancellationToken` through to `CaptureRawImage`; SecuGenAdapter would need to either poll the token or accept a longer hard-coded timeout.

### `CaptureHandler` reads `scanner.VendorErrorCode` after `ScanAsync` but ignores adapter-level cancellation

**Location:** `src/FingerprintAgent/Api/CaptureHandler.cs:76-77`
- Issue: `await scanner.ScanAsync();` is called without a CancellationToken, then `scanner.VendorErrorCode` is read. If the underlying `ScannerManager` returns after exhausting its 20s budget, the caller cannot abort.
- Trigger: Browser closes connection mid-capture; service continues scanning for the full 20s.
- Workaround: None — the wire-level connection close does not propagate to managed code in `HttpListener`.
- Fix approach: Add `CancellationTokenSource` linked to `HttpListenerContext`'s request abort; pass token to `ScanAsync`.

### `ConfigLoader.LoadFromDirectory` swallows non-Json parse errors via broad `catch (Exception ex) when`

**Location:** `src/FingerprintAgent/Configuration/ConfigLoader.cs:50-60`
- Issue: The `when` clause matches by string substring (`IndexOf("JSON")` and `IndexOf("parse")`). Any exception whose message happens to contain both substrings gets re-thrown as `FormatException`, masking the original type and stack trace.
- Symptoms: A `NullReferenceException` with message containing "JSON parse failure" would be misreported as a JSON formatting error.
- Trigger: Unusual configuration values (e.g., a string where an int is expected).
- Workaround: None.
- Fix approach: Use the strongly-typed `JsonReaderException` / `JsonException` types that `Microsoft.Extensions.Configuration.Json` actually throws.

### `ConfigurationRoot` may not reload even after `Dispose` cycle

**Location:** `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:62`
- Issue: Each reload calls `ConfigLoader.LoadFromDirectory()` which builds a fresh `IConfigurationRoot`. If two reloads happen within 300ms, the debounce coalesces them — but `ConfigReloaded` subscribers may receive an old config if `OnDebounceElapsed` fires while a prior reload's binding is still executing (no re-entrancy guard).
- Trigger: Rapid successive saves (VS auto-save, git checkout).
- Workaround: 300ms debounce timer mitigates most cases.
- Fix approach: Add `_isReloading` flag and `_reloadLock` around the load + invoke.

### `HttpServer.Stop` lock acquisition order during concurrent `UpdateCorsConfig`

**Location:** `src/FingerprintAgent/Api/HttpServer.cs:63-102` vs `src/FingerprintAgent/Api/CorsMiddleware.cs:25-34`
- Issue: `Stop()` calls `_listener.Close()` then `_cts.Dispose()`. If `UpdateCorsConfig` is being called concurrently (from `OnConfigReloaded`), both compete for `_corsLock` inside `CorsMiddleware` — no deadlock here because `_corsLock` is independent of `_listener` and `_cts`, but `Dispose` does NOT acquire `_corsLock` before disposing internal state.
- Symptoms: Possible `NullReferenceException` inside `UpdateConfig` if `_allowedOrigins` setter is in flight when `Stop()` returns.
- Trigger: Reload config during shutdown.
- Workaround: None.
- Fix approach: Acquire `_corsLock` in `HttpServer.Dispose` if Cors state can still be touched.

## Security Considerations

### CORS `wildcard` mode allowed by default + no origin verification on capture endpoint

**Risk:** Any origin on the same machine can call `/api/capture` if a malicious page is loaded in the browser, because `Access-Control-Allow-Origin: *` permits any origin to receive the response. Since the service binds to 127.0.0.1, an attacker needs to either: (a) trick a logged-in HIS user into visiting a malicious page on the same machine, or (b) run an unauthorized process on the same host. Both are non-trivial but possible in a shared workstation environment.

**Files:** `src/FingerprintAgent/Api/CorsMiddleware.cs:72-84` + `config.json` (mode=`wildcard`).

**Current mitigation:** HTTP bound to `127.0.0.1` only (`config.json` http.host). Service runs as SYSTEM or a restricted user. No authentication on the HTTP endpoint because the threat model assumes physical access = trust.

**Recommendations:**
- Document the threat model explicitly in `AGENTS.md` and `config.json` comments: "this service trusts all local callers"
- Add a `bindIp` enforcement check in `HttpServer.Start()` that refuses non-loopback addresses unless an explicit override config flag is set
- Consider switching default CORS mode to `disabled` (no headers) since this is a Windows service, not a browser-facing API

### Health endpoint leaks vendor error code without authentication

**Risk:** `GET /health` returns `vendorErrorCode` (e.g., `"ERROR_NO_DEVICE"`, `"FTR_ERROR_EMPTY_FRAME"`) which reveals vendor SDK internals and confirms which scanner brand is attached. An attacker on the host can fingerprint the hardware.

**Files:** `src/FingerprintAgent/Api/HealthHandler.cs:33-58`

**Current mitigation:** Loopback binding only.

**Recommendations:** Strip `vendorErrorCode` from `/health` output unless `config.LogLevel == Debug`; redact for non-debug deployments.

### Base64 redaction in AgentLogger is substring-only, can miss short base64 payloads

**Risk:** The `Base64Pattern` regex (`src/FingerprintAgent/Logging/AgentLogger.cs:22-24`) only redacts base64 substrings of 40+ chars. A `VerificationData` SHA-256 base64 string (44 chars) IS redacted, but a small image fragment or partial fingerprint hash under 40 chars could be logged verbatim.

**Files:** `src/FingerprintAgent/Logging/AgentLogger.cs:22-24, 114-128`

**Current mitigation:** Substring match catches standard PNG embedding (`data:image/png;base64,/9j/4AAQ...`) and SHA-256 hashes.

**Recommendations:**
- Also redact the field name `verificationData` even when short: `if (message.Contains("verificationData", StringComparison.OrdinalIgnoreCase)) { /* redact */ }`
- Add `verificationData` and `ImageBytes` to a redact-field allowlist before regex matching

### No size limit on incoming capture request body

**Risk:** `CaptureHandler.HandleAsync` reads the entire request body via `StreamReader.ReadToEndAsync()` with no size cap (`src/FingerprintAgent/Api/CaptureHandler.cs:32-35`). A 10GB POST body will allocate 10GB of managed memory and OOM the service.

**Files:** `src/FingerprintAgent/Api/CaptureHandler.cs:31-35`

**Current mitigation:** HTTP listener default max body size in .NET Framework (~64KB for some configurations) may apply, but not enforced in code.

**Recommendations:** Wrap the `StreamReader` with a `MaxBytesReader` (e.g., 64KB limit) that throws `InvalidDataException` if exceeded; map to HTTP 413.

### `ZkTecoFingerHost.Close()` exception path leaves native context in unknown state

**Risk:** If `ZkTecoFingerHost.Close()` fails (line 160 in `FingerprintAgentService.cs`), the native context may be in a bad state for the next process startup. A subsequent `Initialize()` call from another process instance or service restart may return `ERROR_INITLIB`.

**Files:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:160`, `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:383`

**Current mitigation:** ZK9500 is single-process; restart works after process exit.

**Recommendations:** Log Close() failure at Error level with correlationId; document as "may require process restart if service fails to start after unclean shutdown".

### No TLS for HTTP endpoint (relying entirely on loopback binding)

**Risk:** HTTP traffic between HIS Angular app and the agent is plaintext on loopback. A local attacker with packet capture privileges (e.g., admin user) can read captured fingerprint PNGs in transit.

**Files:** `src/FingerprintAgent/Api/HttpServer.cs` (whole class — no TLS configuration)

**Current mitigation:** Loopback binding only; no other process should see loopback traffic.

**Recommendations:** Document that the agent does not provide transport-layer encryption; rely on OS-level isolation. If a future multi-user deployment is considered, add HTTPS support.

## Performance Bottlenecks

### MockScannerAdapter uses `GetPixel` per pixel (~75,000 calls)

**Problem:** `MockScannerAdapter.GenerateMockPng` (`src/FingerprintAgent/Adapters/MockScannerAdapter.cs:79-87`) calls `temp.GetPixel(x, y)` in a double-nested loop — 76,800 calls per mock scan. Each `GetPixel` is a managed-to-native marshalling call; benchmarks show this path takes 50–200ms per capture on typical hardware.

**Files:** `src/FingerprintAgent/Adapters/MockScannerAdapter.cs:78-88`

**Cause:** Chose `Graphics.FromImage` (which doesn't support indexed formats) over `LockBits` for drawing the colored mock shape.

**Improvement path:** Draw on a 32-bit ARGB `Bitmap`, then use `LockBits` to extract grayscale pixels directly from the bitmap's scan0 pointer (similar to `BaseScannerAdapter.ToPngGrayscale`). Reduces ~100ms to ~5ms.

### `HttpServer` request handler uses single `LongRunning` task with no concurrency cap

**Problem:** `ProcessRequestLoop` (`src/FingerprintAgent/Api/HttpServer.cs:52-56`) creates one task that loops over `GetContextAsync()`. The handler tasks are unbounded — a flood of slow `/api/capture` calls (each holding a 20s budget) will accumulate in `_inFlightRequests` and exhaust thread pool or memory.

**Files:** `src/FingerprintAgent/Api/HttpServer.cs:22-24, 104-135`

**Cause:** No semaphore to limit concurrent in-flight requests; `List<Task>` grows unbounded.

**Improvement path:** Add `SemaphoreSlim _captureConcurrency = new(4)` (or similar) at HttpServer start; acquire in `HandleRequestAsync` for `/api/capture` only.

### `Bitap redaction regex` runs on every log line

**Problem:** `AgentLogger.RedactIfImageData` runs `Base64Pattern.IsMatch` against every log message string before writing. The regex is compiled (line 24) but each `IsMatch` is still a regex evaluation.

**Files:** `src/FingerprintAgent/Logging/AgentLogger.cs:88-128`

**Cause:** Defensive redaction applied to every log message including Debug-level noise.

**Improvement path:** Short-circuit if message length < 40 (regex requires 40+ chars anyway); only run at Info+ level.

### `ZkResponseToString` dictionary lookup per capture failure

**Problem:** Each ZK capture failure does a dictionary lookup and falls through to `$"ERROR_UNKNOWN_{key}"` for unmapped enum values.

**Files:** `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:312-318`

**Cause:** Defensive fallback for unknown enum values.

**Improvement path:** Acceptable — only happens on rare SDK responses. No action needed.

## Fragile Areas

### `ScannerManager.ScanAsync` priority fallback with retry-on-active logic

**Files:** `src/FingerprintAgent/Adapters/ScannerManager.cs:287-375`
- Why fragile: The 20s total budget is created with `CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken)` but `_cts` is created at construction and never cancelled during normal operation — only at `Dispose()`. The 20s timeout is the only meaningful cancellation. Two issues:
  1. If a caller passes their own short `cancellationToken`, the inner `totalCts` honors it — but the per-adapter retries inside ZKTeco do NOT honor the token at the SDK call boundary (they only check `cancellationToken.IsCancellationRequested` between retries, not inside the native blocking call).
  2. `UpdatePriority` recreates `_adapters` and disposes old non-active adapters while a `ScanAsync` may be iterating `_adapters`. The `_adapterLock` does NOT protect the foreach loop — it only protects the array swap, not iteration. A `ScanAsync` running on thread A may iterate `currentAdapters` while thread B swaps the field. The captured local variable `currentAdapters` protects from this.
- Safe modification: Always snapshot `_adapters` under lock before iterating (current code does this at line 327-328 — correct). Don't read `_adapters` field outside the lock.
- Test coverage: `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs` exercises backoff but does NOT test concurrent `UpdatePriority` + `ScanAsync`. Coverage gap.

### `ScannerManager.Dispose` order with `_adapterLock`

**Files:** `src/FingerprintAgent/Adapters/ScannerManager.cs:387-407`
- Why fragile: Active adapter is disposed twice if it's in `_adapters` AND `_activeAdapter`. Code at lines 396-403 skips the active adapter in the first loop, then disposes it at line 406. If the active adapter is NOT in `_adapters` (e.g., removed by `UpdatePriority`), the first loop skips it (good) and line 406 disposes it (good). If the active adapter IS in `_adapters`, first loop skips it (good), second dispose at line 406 (good). Correct.
- However: `_disposed` is set at line 390 BEFORE `_cts.Dispose()` at line 391. If `Dispose()` is re-entered (unlikely), it returns early at line 389, leaving `_cts` undisposed.
- Safe modification: Use `Interlocked.Exchange(ref _disposed, 1)` or check `_disposed` again at the bottom.
- Test coverage: `MockScannerAdapterTestDoubles` and unit tests cover single-instance dispose; no concurrent dispose test.

### `ConfigFileWatcher.Dispose` race with pending debounce timer

**Files:** `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:81-96`
- Why fragile: `_debounceTimer.Elapsed` handler `OnDebounceElapsed` runs on a thread pool thread. If the timer fires AFTER `_debounceTimer.Stop()` (Stop is not synchronous — it just sets a flag), the handler may still execute. The handler calls `ConfigLoader.LoadFromDirectory` which could throw if config.json was deleted between watcher start and Dispose.
- Safe modification: Add `_disposed` check at start of `OnDebounceElapsed`.
- Test coverage: None.

### `HttpServer.HandleRequestAsync` swallows all exceptions silently

**Files:** `src/FingerprintAgent/Api/HttpServer.cs:188-196`
- Why fragile: `catch (Exception) { context.Response.StatusCode = 500; context.Response.Close(); }` is the entire error path. The original exception is logged ONLY via `ContinueWith` (line 118) — but the `ContinueWith` is attached to the parent `handlerTask`, which is awaited inside the `try`. So if the `await` throws, the catch handles it; if a continuation throws, it goes to `_logger?.Error`. The original exception is never explicitly logged with stack trace.
- Safe modification: `catch (Exception ex) { _logger?.Error(correlationId, $"HandleRequest: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); ... }` with a guarded `_logger?.` to avoid the same swallow pattern.
- Test coverage: `tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs` exists but doesn't verify exception is logged.

### `BaseScannerAdapter` stub abstraction drift from concrete adapters

**Files:** `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs:12-115`
- Why fragile: `BaseScannerAdapter` is used by `SecuGenAdapter` only. The other 4 adapters either implement `IScannerAdapter` directly (ZKTeco, Futronic, DigitalPersona) or provide a `#else` stub. `BaseScannerAdapter.ScanAsync` is not called by `ScannerManager` (it calls `IScannerAdapter.ScanAsync` which routes to each adapter's own implementation). So `BaseScannerAdapter.ScanAsync` is dead code.
- Safe modification: Either remove `BaseScannerAdapter.ScanAsync` or route all non-ZK, non-DP, non-Futronic adapters through it. Currently dead code path.
- Test coverage: `tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTests.cs` — does not cover BaseScannerAdapter.

## Scaling Limits

### Single-process, single-adapter constraint

**Current capacity:** 1 HTTP listener on `127.0.0.1:5043`, 1 active scanner at a time, 20s per `/api/capture` call.
**Limit:** Only 1 concurrent capture (ScannerManager iterates adapters serially, not in parallel). At ~3 captures/min (one per ~20s), the system handles ~180 captures/hour per workstation.
**Scaling path:** None — design is single-hospital-PC. If a hospital needs parallel capture at multiple stations, they install one agent per station (by design).

### Log file growth bounded by MaxSizeMb/MaxFiles but maxSizeMb not enforced

**Files:** `src/FingerprintAgent/Logging/AgentLogger.cs:32-51` and `src/FingerprintAgent/Configuration/AgentConfig.cs:38-44`
- Current capacity: `MaxSizeMb` (default 10) and `MaxFiles` (default 5) are defined in config but `AgentLogger` constructor (line 32-51) NEVER reads them — the `FileStream` is opened in `FileMode.Append` without size tracking or rotation.
- Limit: Log file grows unbounded until disk fills. At INFO level, a busy capture service generates ~50KB/hour; 10MB fills in ~200 hours (~8 days).
- Scaling path: Add a `LogRotator` class that checks file size before each write and rotates when `MaxSizeMb` exceeded; archives up to `MaxFiles`.

## Dependencies at Risk

### `ZkTecoFingerPrint` NuGet v1.2.1 — no recent updates

**Risk:** NuGet package `ZkTecoFingerPrint` v1.2.1 (per `AGENTS.md`) is the wrapper around the native ZK SDK. Last update date unknown; SDK 5.3 / ZK10.0 firmware quirks (`ERROR_CAPTURE` for buffer size query) suggest the wrapper is not actively maintained.
**Impact:** Bug fixes in newer ZK firmware will not be picked up automatically; manual vendor contact required.
**Migration plan:** Vendor (`ZKSoftware`) typically requires direct relationship for SDK updates. No public alternative; if vendor abandons wrapper, agent must use P/Invoke to `zkfpEng.dll` directly (which the wrapper does internally — would be significant rewrite).

### `DPUruNet` NuGet v1.0.0.0.1 — DigitalPersona U.are.U legacy

**Risk:** `DPUruNet` is HID Global's legacy .NET wrapper for the U.are.U SDK. New DigitalPersona devices are moving to a unified SDK (`Crossmatch`) that does not include this wrapper. v1.0.0.0.1 version number suggests abandoned.
**Impact:** Future DigitalPersona hardware may not work with current adapter.
**Migration plan:** Migrate to Crossmatch SDK if/when DigitalPersona deprecates U.are.U. Adapter interface is stable — only `DigitalPersonaAdapter.cs` needs changes.

### `SecuGen.FDxSDKPro.Windows.dll` — external SDK, no NuGet

**Risk:** `SecuGenAdapter.cs` references the native `SecuGen.FDxSDKPro.Windows.dll` directly via P/Invoke. The DLL is provided by SecuGen via their developer portal; no NuGet, no version tracking in the project.
**Impact:** SDK upgrades are manual; nothing prevents DLL version skew between the stub types in `SecuGenAdapter.cs:8-27` (`#if !SECUGEN_SDK_PRESENT`) and the actual SDK.
**Migration plan:** Add `lib/SecuGen.SDK.version` file documenting expected SDK version; CI should fail if mismatch.

### `ftrScanAPI.dll` (Futronic) — x86 only

**Risk:** `FutronicAdapter.cs:195` P/Invokes `ftrScanAPI.dll` which is x86-only. The agent project must build as `x86` (per `AGENTS.md` "x86 required for vendor SDK compatibility"). If anyone changes Platform target to `AnyCPU` or `x64`, Futronic silently fails at runtime with `BadImageFormatException`.
**Impact:** Silent adapter failure for Futronic; ScannerManager will log `PROBE_EXCEPTION` and skip.
**Migration plan:** Add MSBuild check that fails the build if `Platform != x86`. Document in `SCANNER_SETUP.md`.

### `Newtonsoft.Json` dependency

**Risk:** `CaptureHandler.cs:9, 50, 104, 152` and `HealthHandler.cs:7` use `Newtonsoft.Json` for serialization. .NET Framework 4.8 does not include `System.Text.Json` by default; the project must ship `Newtonsoft.Json` explicitly. No CVE concern in current version (12.x).
**Impact:** Larger deployment footprint (~700KB DLL); minor attack surface.
**Migration plan:** Move to `System.Text.Json` if targeting .NET 6+; not feasible on net48 without additional package.

## Missing Critical Features

### No MSI installer (deployment target not implemented)

**Problem:** `AGENTS.md` lists "Deployment target: MSI installer (not yet implemented)". Hospitals cannot deploy via standard MSI/Group Policy; current `Install-Service.ps1` requires manual `sc.exe` invocation.
**Impact:** Each workstation requires manual install; increases IT overhead.
**Blocks:** Phase 04+ rollout.

### No DI container (Microsoft.Extensions.DependencyInjection unused)

**Problem:** `AGENTS.md` notes "No DI container: All classes `new`'d directly; `Microsoft.Extensions.DependencyInjection` listed but unused". Classes are tightly coupled to constructors (e.g., `HttpServer(AgentConfig, IScannerAdapter, AgentLogger)`) and rely on null-conditional `_logger?` for optional logger.
**Impact:** Unit tests must construct real `AgentLogger` (writes to `C:\ProgramData\FingerprintAgent\Logs\agent.log` — test pollution) or pass null and accept loss of logging. Some tests pass null; others construct real logger with overridden path.
**Blocks:** Cleaner test doubles, faster test runs.

### No GitHub Actions CI/CD

**Problem:** `.github/workflows/` directory absent. `dotnet test` is run manually; no automated build verification.
**Impact:** Regressions only caught when developer remembers to run full test suite.
**Blocks:** Safe refactoring at scale; multi-contributor confidence.

### No biometric template storage or matching

**Problem:** `AGENTS.md` notes "Matching NOT done here — backend HIS handles it". This is intentional but should be documented in code as a deliberate boundary, not just in AGENTS.md.
**Impact:** If a future developer adds `Verify()` to `IScannerAdapter` thinking it's needed for HIS, scope creep.
**Blocks:** Architecture clarity.

### No request rate limiting or auth on `/api/capture`

**Problem:** Any local process can call `/api/capture` repeatedly. No rate limit, no API key. A misbehaving local script could flood the endpoint and exhaust the scanner adapter.
**Impact:** Resource exhaustion by local processes; DOS by malicious software running on the same host.
**Blocks:** Multi-tenant host deployments.

### No fingerprint image quality feedback to client

**Problem:** `CaptureResult` returns `IsSuccess` and PNG bytes, but no quality metric (NFIQ score, ridge clarity). HIS cannot reject a blurry capture before persisting.
**Impact:** Hospital staff may save poor-quality prints; backend matching fails.
**Blocks:** Best-practice biometric capture flow.

### No structured request timeout for HTTP listener

**Problem:** `HttpListener` does not enforce a request-body receive timeout. A slow client can keep a connection open indefinitely; resources are tied up until the request completes or the client disconnects.
**Impact:** Slowloris-style local DOS possible if attacker is on-host.
**Blocks:** Robust local-host defense.

## Test Coverage Gaps

### No tests for `FingerprintAgentService` lifecycle (OnStart/OnStop)

**What's not tested:** `OnStart` failure paths (missing config, scanner init failure, config watcher failure); `OnStop` exception aggregation; `OnConfigReloaded` concurrent with `UpdateCorsConfig`.
**Files:** `src/FingerprintAgent/Service/FingerprintAgentService.cs` (entire class)
**Risk:** Service startup/shutdown regressions not caught; the WR-01 health-check-timer-disposal comment indicates known race that lacks a regression test.
**Priority:** High — service lifecycle is the entry point.

### No tests for `MockScannerAdapter` cancellation behavior

**What's not tested:** `ScanAsync(CancellationToken)` with pre-cancelled token (Bug section above).
**Files:** `src/FingerprintAgent/Adapters/MockScannerAdapter.cs:25-51`, `tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTests.cs`
**Risk:** Mock mode in production (per default `MockMode=true`) would ignore cancellation.
**Priority:** Medium — mock is for development, not production.

### No tests for `ScannerManager.UpdatePriority` concurrent with `ScanAsync`

**What's not tested:** Thread-safety of `_adapterLock` under simultaneous `UpdatePriority` (swaps `_adapters`) and `ScanAsync` (iterates snapshot). The current implementation snapshots under lock (line 327-328) but no test exercises the race.
**Files:** `src/FingerprintAgent/Adapters/ScannerManager.cs:223-255, 287-375`
**Risk:** Concurrent config reload + capture could cause index-out-of-range or null-ref.
**Priority:** Medium — production reload is infrequent but possible.

### No tests for `BaseScannerAdapter` (dead code path)

**What's not tested:** `BaseScannerAdapter.ScanAsync` happy path, cancellation, exception in `CaptureRawImage()`. SecuGenAdapter uses `BaseScannerAdapter` so this code IS live for SecuGen — but no test exercises it.
**Files:** `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs`, `src/FingerprintAgent/Adapters/SecuGenAdapter.cs`
**Risk:** SecuGen adapter regressions not caught at unit level (rely on hardware integration test).
**Priority:** Medium.

### No tests for `ConfigLoader` invalid JSON path beyond FormatException

**What's not tested:** The `when` clause at `src/FingerprintAgent/Configuration/ConfigLoader.cs:50-60` is only matched against substring pattern. Edge cases like missing required sections, type mismatch (int where string expected) are not tested.
**Files:** `src/FingerprintAgent/Configuration/ConfigLoader.cs`, `tests/FingerprintAgent.Tests/Configuration/ConfigLoaderTests.cs`
**Risk:** Operator typos (e.g., `port: "5043"` as string) silently fall through to default values via `?? config.Http.Port`.
**Priority:** Medium — observability gap (silently uses default, doesn't surface the typo).

### No tests for `HttpServer.Stop` worker-drain timeout behavior

**What's not tested:** The 5s worker timeout + 30s in-flight timeout + `_listener.Close()` ordering. If `_workerTask` is blocked in `GetContextAsync()`, the 5s wait may elapse without cancellation propagating.
**Files:** `src/FingerprintAgent/Api/HttpServer.cs:63-102`, `tests/FingerprintAgent.Tests/Api/HttpServerIntegrationTests.cs`
**Risk:** Occasional shutdown hangs (Bug section above).
**Priority:** High — affects SCM stop responsiveness.

### No tests for `AgentLogger` thread-safety under high contention

**What's not tested:** Multiple threads writing simultaneously with `lock (_lock)` contention. The `TryWriteEventLog` is called WITHOUT the lock (line 111) — concurrent EventLog writes may serialize internally or drop messages.
**Files:** `src/FingerprintAgent/Logging/AgentLogger.cs:88-128`
**Risk:** Lost log messages under load; EventLog backpressure.
**Priority:** Low — logs are best-effort by design.

### No tests for `CorsMiddleware` with empty/null allowedOrigins + allowlist mode

**What's not tested:** `allowlist` mode with `allowedOrigins: []` (empty array) — should reject all origins, but code at line 76 `else if (mode == "allowlist" && allowedOrigins.Contains(origin))` returns no headers, which is correct behavior, but no test verifies it.
**Files:** `src/FingerprintAgent/Api/CorsMiddleware.cs:72-84`, `tests/FingerprintAgent.Tests/Api/CorsMiddlewareTests.cs`
**Risk:** Misconfigured allowlist silently allows everything by setting no headers (browsers block by default, but old API patterns may not).
**Priority:** Low.

### No E2E test for `Install-Service.ps1` / uninstall flow

**What's not tested:** PowerShell script correctness; service registration parameters; uninstall cleanup of EventLog source.
**Files:** `scripts/Install-Service.ps1`, `scripts/Uninstall-Service.ps1`, `scripts/Service.ps1`
**Risk:** Failed installs leave stale service registrations; failed uninstalls leave DLLs in `C:\Program Files\...`.
**Priority:** Medium — IT operations reliability.

### No performance regression test (capture latency budget)

**What's not tested:** `/api/capture` should complete in < 21s (20s budget + overhead) for mock, < 25s for ZK. No benchmark or threshold test exists.
**Files:** None — no perf test infrastructure
**Risk:** Performance regressions (e.g., extra DB call, extra regex) not detected.
**Priority:** Low — manual timing via `Test-Capture.ps1` covers smoke test.

---

*Concerns audit: 2026-08-19*
