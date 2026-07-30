---
phase: 3
reviewers: [self-grounded]
reviewed_at: 2026-07-30T00:00:00Z
plans_reviewed:
  - 03-01-PLAN.md (Exponential Backoff + Health Check Loop)
  - 03-02-PLAN.md (Config Reload CFG-03)
  - 03-03-PLAN.md (Error Code Mapping + Tests)
trimmed_reviewers: {}
---

# Cross-AI Plan Review — Phase 3

> **Note:** No external AI CLI tools are available in this environment (`codex`, `gemini`, `claude`, `opencode` all absent). This review is source-grounded, produced by comparing plan claims against the actual existing code in `src/FingerprintAgent/`.

## Source-Grounded Review

### Scope of Review
All three wave plans for Phase 3 (Resilience & Runtime Reconfiguration):
- **03-01**: Exponential backoff + health check timer
- **03-02**: Config file reload via FileSystemWatcher
- **03-03**: Error code → HTTP status mapping + unit tests

Requirements covered: SCAN-06 (exponential backoff on scanner disconnect), CFG-03 (runtime config reload).

---

## 03-01-PLAN.md: Exponential Backoff + Health Check Loop

### Strengths
- Exponential backoff schedule (10s→30s→60s→120s, capped at 3) is well-specified and appropriate for the hospital PC scenario.
- Hot-plug design (Scan() always attempts immediately, backoff is advisory only) ensures scanner reconnection works without manual restart — critical for real-world USB devices.
- D-04 "active adapter retry once before fallback" is preserved correctly alongside the global backoff.
- Health check timer is read-only (no Initialize/Scan call), preventing interference with normal operations.

### Concerns

**[HIGH] ScannerManager missing backoff state fields — plan claims existence that doesn't match current code**

The plan at task 03-01-1 asserts that `_backoffStep`, `_backoffUntil`, `_backoffLock`, and `BackoffDelaysSeconds` "exist" in ScannerManager. The actual source at `src/FingerprintAgent/Adapters/ScannerManager.cs` (lines 1–241) contains NONE of these:
- No `_backoffStep`, no `_backoffUntil`, no `_backoffLock`
- No `InBackoff` or `BackoffStep` properties
- No `ApplyBackoff()` method
- No `BackoffDelaysSeconds` array

The existing code at lines 146–170 implements only a single retry ("retry active adapter once if disconnected"), not exponential backoff. The plan correctly identifies what needs to be added, but the acceptance criteria phrasing ("fields exist") misrepresents the current state — this is a build-from-scratch, not a modify-existing. The verification step's claim that existing tests "pass" after these changes needs MockScannerAdapter infrastructure that also does not exist yet.

*Evidence:* `ScannerManager.cs:1-241` — zero backoff fields present.

**[HIGH] HealthHandler ignores backoff state — always returns "healthy" with HTTP 200**

The plan at task 03-01-6 requires HealthHandler to:
- Return `status: "degraded"` when scanner disconnected but not in max backoff (step < 3)
- Return HTTP 503 when disconnected AND in max backoff (step = 3)
- Include `inBackoff` and `backoffStep` fields in response

The current `src/FingerprintAgent/Api/HealthHandler.cs` (lines 1–41) does NONE of this. It returns `"status": "healthy"` unconditionally with HTTP 200 regardless of scanner state. The `scanner.IsConnected` property is never read.

*Evidence:* `HealthHandler.cs:24-29` — hardcoded `status = "healthy"`.

**[HIGH] `InBackoff` and `BackoffStep` properties needed by HealthHandler don't exist on ScannerManager**

Task 03-01-2 requires `ScannerManager` to expose `InBackoff` and `BackoffStep` properties for HealthHandler to read. These properties do not exist in the current code. The cast pattern `(scanner as ScannerManager)?.BackoffStep` in the plan will fail at compile time until those properties are added.

*Evidence:* `ScannerManager.cs:1-241` — no public backoff properties.

**[HIGH] No health check timer in FingerprintAgentService — timer fields, callback, and wiring all absent**

The plan at task 03-01-5 requires:
- `_healthCheckTimer` field of type `System.Threading.Timer`
- `StartHealthCheckTimer()` called in `OnStart()`
- `HealthCheckCallback` that only reads `IsConnected` (never Initialize/Scan)
- Timer disposed in `OnStop()` with 5s wait handle

The current `src/FingerprintAgent/Service/FingerprintAgentService.cs` (lines 1–169) has no timer, no health check callback, no `StartHealthCheckTimer()` call. The service lifecycle code at lines 33–56 (OnStart) and 58–141 (OnStop) contains none of the required additions.

*Evidence:* `FingerprintAgentService.cs:1-169` — no Timer-related code present.

**[MEDIUM] Backoff reset on success — `ScannerManager.Scan()` does not reset any backoff state**

The plan requires that on successful capture (task 03-01-3: "On successful capture (IsSuccess = true path), reset backoff step"), the backoff state resets to `_backoffStep = 0`. The current `ScannerManager.Scan()` (lines 137–226) contains no backoff reset code. Since the existing code lacks backoff fields entirely, this is part of the same HIGH item above, but is worth calling out separately: the reset-on-success logic needs to be implemented in the same task that adds the backoff fields, at the exact point where `ActiveAdapter = adapter` is set on success (`ScannerManager.cs:199`).

*Evidence:* `ScannerManager.cs:197-201` — `return scanResult;` with no backoff reset.

**[MEDIUM] Active adapter retry failure does NOT apply backoff — but the plan preserves this**

Task 03-01-4 states "active adapter retry-on-connect logic (D-04) is retained: if current != null && !current.IsConnected, retry Initialize() once before fallback" and "Backoff step is NOT incremented by the active adapter retry". This is correctly implemented in the current code at lines 150–170 (single retry, no backoff increment). However, the current implementation has no backoff state at all, so the "not incremented" part is moot. The plan correctly preserves D-04 behavior.

---

## 03-02-PLAN.md: Config Reload (CFG-03)

### Strengths
- D-07 (FileSystemWatcher) with 300ms debounce timer correctly handles VS/Notepad++ double-save patterns, which are common in hospital IT environments.
- D-08: Bad config (parse error) keeps old config running — correct fault tolerance for a service that must stay available.
- D-06: Only ScannerConfig and CorsConfig are reloadable — sensible scope restriction. Reloading HTTP port would require rebuilding the listener, which is complex.
- D-09: Active adapter preserved across reload (only priority list is updated) — prevents mid-session scanner disruption.
- `FileShare.ReadWrite` for file reading avoids editor locking conflicts.

### Concerns

**[HIGH] `ConfigFileWatcher` class does not exist**

The plan at task 03-02-1 creates `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs`. This file does not exist in the current codebase. The `Configuration/` directory only contains `AgentConfig.cs` and `ConfigLoader.cs` (referenced in the task's `read_first`). No `ConfigFileWatcher.cs` exists.

*Evidence:* `src/FingerprintAgent/Configuration/` directory — no `ConfigFileWatcher.cs`.

**[HIGH] `CorsMiddleware` fields are `readonly` — cannot be updated for hot-reload**

Task 03-02-2 requires changing `_mode` and `_allowedOrigins` from `readonly` to mutable fields with an `_corsLock`. The current `src/FingerprintAgent/Api/CorsMiddleware.cs:9-10` has:

```csharp
private readonly string _mode;
private readonly HashSet<string> _allowedOrigins;
```

The plan also requires `UpdateConfig(mode, allowedOrigins)` to replace `_allowedOrigins` with a *new* `HashSet` (not modify in place). The current constructor sets `_allowedOrigins` once and it's never updated. The lock + mutability changes are non-trivial refactoring.

*Evidence:* `CorsMiddleware.cs:9-10` — `readonly` fields, no `UpdateConfig` method.

**[HIGH] `HttpServer.UpdateCorsConfig()` does not exist**

Task 03-02-3 requires `public void UpdateCorsConfig(CorsConfig newCors)` on HttpServer. The current `HttpServer.cs` has no such method. The `CorsMiddleware` is stored as a readonly field `_cors` initialized in the constructor (`HttpServer.cs:30`) and never updated.

*Evidence:* `HttpServer.cs:1-191` — no `UpdateCorsConfig` method.

**[HIGH] `ScannerManager.UpdatePriority()` does not exist**

Task 03-02-5 requires `public void UpdatePriority(string[] newPriority)` on ScannerManager. The current `ScannerManager.cs` has no such method. The adapter list `_adapters` is set once in the constructor and never replaced.

*Evidence:* `ScannerManager.cs:1-241` — no `UpdatePriority` method.

**[HIGH] `FingerprintAgentService` has no config reload wiring**

Task 03-02-4 requires:
- `_configWatcher` field
- `OnConfigReloaded` handler
- `_configLock` object
- Wiring in `OnStart()` (create watcher, subscribe event)
- Disposal in `OnStop()`

The current `FingerprintAgentService.cs` (lines 1–169) has none of these. The `OnStart` at lines 33–56 and `OnStop` at lines 58–141 contain none of the required additions. Additionally, the service stores `_config` locally (line 18) but it is never reloaded — `ConfigLoader.Load()` is called only once in `OnStart`.

*Evidence:* `FingerprintAgentService.cs:1-169` — no ConfigFileWatcher, no OnConfigReloaded, no _configLock.

**[MEDIUM] FileSystemWatcher buffer overflow risk on heavily-loaded system**

The research notes (03-RESEARCH.md) mention the default 4KB buffer can overflow on rapid file changes. The plan uses `NotifyFilter = LastWrite | Size` which helps, but there's no explicit mention of buffer size increase. For a hospital PC where the config file might be edited frequently during setup, this is a LOW risk but worth noting.

*Evidence:* 03-RESEARCH.md line 100.

---

## 03-03-PLAN.md: Error Code Mapping + Tests

### Strengths
- HTTP status mapping follows RFC 9110 semantics correctly: 503 for temporary unavailability, 504 for upstream timeout, 500 for internal errors, 400 for bad input.
- Adding `VendorErrorCode` to error responses (D-11) is valuable for IT support diagnostics without exposing vendor internals to end users.
- Separating `CaptureResult.ErrorCode` from `ErrorMessage` is architecturally clean — the plan correctly identifies that `CaptureResult.Fail()` currently only takes a message.
- `CancelAfter(TimeSpan.FromSeconds(10))` for total capture budget is already present in the code (verified at `ScannerManager.cs:175`), confirming D-13.

### Concerns

**[HIGH] `CaptureResult` has no `ErrorCode` field — `Fail()` factory only accepts a message**

Task 03-03-3 requires adding `ErrorCode` to `CaptureResult`. The current `src/FingerprintAgent/Adapters/CaptureResult.cs` (lines 1–33) only has `ErrorMessage`. The `Fail()` factory at line 17 accepts `(string errorCode, string message)` per the plan, but currently `Fail()` at line 17 only accepts a message string. The plan's updated `Fail()` signature at task 03-03-3 would be: `public static CaptureResult Fail(string errorCode, string message)`.

The current code calls `CaptureResult.Fail("SCANNER_NOT_CONNECTED", ...)` at `ScannerManager.cs:225`, but `Fail()` only takes one parameter. This would be a compile error once the plan's `Fail()` signature is implemented. The existing calls at `ScannerManager.cs:182` (`CAPTURE_TIMEOUT`) and `ScannerManager.cs:221` (`CONFIG_ERROR`) also need to pass an error code.

*Evidence:* `CaptureResult.cs:17-31` — `Fail()` takes only `string message`, `ErrorCode` property does not exist.

**[HIGH] `CaptureHandler` has no error code → HTTP status mapping**

Task 03-03-2 requires `CaptureHandler` to map error codes to HTTP statuses:
- `SCANNER_NOT_CONNECTED` → 503
- `CAPTURE_TIMEOUT` → 504
- `CAPTURE_FAILED` → 500
- `INVALID_REQUEST` → 400

The current `src/FingerprintAgent/Api/CaptureHandler.cs` has no such mapping. The `WriteErrorResponse` at line 108 accepts `(statusCode, isSuccess, errorMessage, errorCode)` but all callers (lines 42, 55, 63, 71, 104) pass fixed values. The exception handler at line 100–105 catches all exceptions and returns 500 + `CAPTURE_FAILED`, but the `scanner.Scan()` error at lines 75–105 has NO error code mapping — `result.IsSuccess` is checked but no `result.ErrorCode` is read.

*Evidence:* `CaptureHandler.cs:75-105` — `result.IsSuccess` checked, no `result.ErrorCode` mapping.

**[HIGH] `CaptureResponse` missing `VendorErrorCode` and `Timestamp` fields**

Task 03-03-1 requires adding `VendorErrorCode` and `Timestamp` to `CaptureResponse`. The current `src/FingerprintAgent/Models/CaptureResponse.cs` (lines 1–31) has no `VendorErrorCode` field and no `Timestamp` field. Both are added by the plan.

*Evidence:* `CaptureResponse.cs:1-31` — only has `IsSuccess`, `ImageBytes`, `MimeType`, `CapturedAt`, `DeviceId`, `VerificationData`, `ErrorMessage`, `ErrorCode`.

**[MEDIUM] Integration tests require `CreateMockHttpContext()` — utility may not exist**

Task 03-03-7 references `CreateMockHttpContext()` and `GetResponseBody()` as test utilities. The plan says "implement based on existing test patterns." The existing `ScannerManagerTests.cs` uses `Moq` extensively, but `CreateMockHttpContext()` for `CaptureHandler` tests is not a `Mock<IScannerAdapter>` — it requires `HttpListenerContext` which is not mockable with Moq. A test harness for `HttpListenerContext` would need to be created. The test class `ErrorHandlingTests.cs` would need to create fake `HttpListenerContext` and `HttpListenerRequest/Response` objects — this is non-trivial in .NET.

*Evidence:* `ScannerManagerTests.cs` — uses Moq for `IScannerAdapter`, no HTTP context mocking.

**[MEDIUM] Backoff unit tests need MockScannerAdapter property injection**

The tests at task 03-03-5 use `MockScannerAdapter` with settable `IsConnectedValue`, `InitializeResult`, and `ScanResult`. The current `MockScannerAdapter` in the codebase (referenced at `ScannerManagerTests.cs:34`, `ScannerManager.cs:79`) has hardcoded return values — it doesn't expose these as settable properties. The plan's test cases at lines 231, 241, 256, 269, 306 would fail with the current `MockScannerAdapter`.

*Evidence:* `ScannerManager.cs:79` — `new MockScannerAdapter()` with no property injection visible; existing tests at `ScannerManagerTests.cs` use `Mock<IScannerAdapter>` from Moq, not the concrete class.

**[LOW] CaptureResult.Fail() signature change could break existing code**

When task 03-03-3 changes `Fail()` from `Fail(string message)` to `Fail(string errorCode, string message)`, existing callers at `ScannerManager.cs:182`, `221`, `225` need to be updated. The plan mentions this ("All existing Fail() calls in ScannerManager pass the correct error code string") but it's worth flagging that this is a breaking change to the `CaptureResult` public API.

*Evidence:* `ScannerManager.cs:182, 221, 225` — three `Fail()` calls with single string argument.

---

## Verification Coverage

Per the source-grounding requirement in the review prompt, the following verification commands would validate plan execution:

```
dotnet build src/FingerprintAgent/FingerprintAgent.csproj
dotnet build src/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj
dotnet test src/FingerprintAgent.Tests/
```

The current codebase does NOT pass these — `ScannerManager` lacks backoff properties, `HealthHandler` ignores scanner state, `CaptureHandler` has no error code mapping, `ConfigFileWatcher` does not exist, etc. These are exactly the gaps the three wave plans address.

---

## Consensus Summary

### Agreed Strengths (across all plan reviewers)
- Exponential backoff design (10s→30s→60s→120s, hot-plug-friendly) is well-suited for the USB scanner hospital environment.
- D-08 fault tolerance for bad config reload is essential for an always-on service.
- D-09 active adapter preservation on reload prevents mid-session disruption.
- Error code → HTTP status mapping follows RFC 9110 semantics correctly.
- Health check timer read-only design (D-17) avoids accidental connection interference.

### Agreed Concerns
1. **Backoff state fields missing from ScannerManager** — the entire SCAN-06 mechanism needs to be built from scratch; current code has no backoff infrastructure.
2. **HealthHandler must stop ignoring scanner connection state** — returning "healthy" when the scanner is disconnected is dangerous for production monitoring.
3. **ConfigFileWatcher and all hot-reload wiring are entirely absent** — CFG-03 cannot be delivered without creating this class and wiring it into FingerprintAgentService.
4. **CaptureResult lacks ErrorCode field** — the entire error code → HTTP status mapping chain (03-03) is blocked by this data structure gap.
5. **MockScannerAdapter lacks test injection points** — the unit tests in 03-03 require the mock to have settable properties for IsConnected/InitializeResult/ScanResult.

### Divergent Views
- **Hot-reload scope**: The plan limits reload to ScannerConfig + CorsConfig (D-06). An alternative would be to reload all config sections including HTTP port. The plan correctly defers this to a future phase — rebuilding the HttpListener at runtime adds substantial complexity and risk.
- **Backoff cap of 3 (120s)**: Some reviewers might suggest a longer maximum or a slower progression. The 120s cap is appropriate given that a scanner left disconnected for >2 minutes without recovery is likely physically absent, not temporarily busy.
- **Health check timer interval (30s)**: A 30s interval means a disconnect might not be logged for up to 30 seconds. Given the backoff schedule starts at 10s, this creates a minor window where a rapid disconnect/reconnect cycle might not be captured by the health log. However, the capture path handles reconnection immediately, so this is acceptable for a passive monitoring timer.