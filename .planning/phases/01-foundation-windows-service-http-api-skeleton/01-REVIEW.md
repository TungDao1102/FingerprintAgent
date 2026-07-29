---
phase: 01-foundation-windows-service-http-api-skeleton
reviewed: 2026-07-29T00:00:00Z
depth: standard
files_reviewed: 24
files_reviewed_list:
  - src/FingerprintAgent/Adapters/CaptureResult.cs
  - src/FingerprintAgent/Adapters/IScannerAdapter.cs
  - src/FingerprintAgent/Adapters/MockScannerAdapter.cs
  - src/FingerprintAgent/Api/CaptureHandler.cs
  - src/FingerprintAgent/Api/CorsMiddleware.cs
  - src/FingerprintAgent/Api/HealthHandler.cs
  - src/FingerprintAgent/Api/HttpServer.cs
  - src/FingerprintAgent/Configuration/AgentConfig.cs
  - src/FingerprintAgent/Configuration/ConfigLoader.cs
  - src/FingerprintAgent/Logging/AgentLogger.cs
  - src/FingerprintAgent/Models/CaptureRequest.cs
  - src/FingerprintAgent/Models/CaptureResponse.cs
  - src/FingerprintAgent/Program.cs
  - src/FingerprintAgent/Service/FingerprintAgentService.cs
  - src/FingerprintAgent/FingerprintAgent.csproj
  - tests/FingerprintAgent.Tests/AgentLoggerTests.cs
  - tests/FingerprintAgent.Tests/ConfigLoaderTests.cs
  - tests/FingerprintAgent.Tests/CorsMiddlewareTests.cs
  - tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj
  - tests/FingerprintAgent.Tests/HttpServerIntegrationTests.cs
  - tests/FingerprintAgent.Tests/MockScannerAdapterTests.cs
  - scripts/Install-Service.ps1
  - scripts/Test-Capture.ps1
  - scripts/Uninstall-Service.ps1
findings:
  critical: 2
  warning: 9
  info: 6
  total: 17
status: issues_found
---

# Phase 01: Code Review Report

**Reviewed:** 2026-07-29T00:00:00Z
**Depth:** standard
**Files Reviewed:** 24
**Status:** issues_found

---

## Summary

Reviewed 24 source files (C# service, tests, PowerShell scripts) at standard depth.
The implementation is a Windows service exposing an HTTP API for fingerprint capture,
using `HttpListener` with CORS middleware and a mock scanner adapter.

Two **Critical** issues were found:
1. The `ProcessRequestLoop` intentionally discards task completion (CS4014) while
   `Stop()` only waits 5 seconds for the worker loop — in-flight `HandleRequest`
   fire-and-forget tasks are abruptly terminated on timeout with no 503 response
   to the client and no graceful drain.
2. `result.ImageBytes` is dereferenced with no null check in `CaptureHandler.Handle`
   (line 80), so a null `ImageBytes` causes an unhandled `NullReferenceException`
   that surfaces as a 500 with implementation-detail error text.

Nine **Warning**-level issues were found covering nullable reference handling,
dispose patterns, JSON path-traversal checks, CORS `applyCorsHeaders` call in the
exception path, and test-class `IDisposable` correctness. PowerShell scripts are
clean.

---

## Critical Issues

### CR-01: Fire-and-forget task + hard timeout means in-flight requests are silently dropped

**File:** `src/FingerprintAgent/Api/HttpServer.cs:85-109`
**Issue:** `ProcessRequestLoop` uses `#pragma warning disable CS4014` to spawn
`HandleRequest` as a fire-and-forget `Task.Run()`. The `Stop()` method signals
cancellation then waits at most 5 seconds via `_workerTask?.Wait(TimeSpan.FromSeconds(5))`
(line 75). On a loaded system or slow client connection, in-flight requests are
terminated with no graceful shutdown, no 503 retry-after header, and no peer
notification. The caller receives a connection-reset TCP error rather than a
meaningful response.

**Fix:**
```csharp
// In Stop(), replace the 5-second hard wait with a graceful drain:
// 1. Cancel the token first (already done).
// 2. Then wait indefinitely for _workerTask, or until the token fires.
// 3. For in-flight HandleRequest calls, add a per-request CancellationToken
//    derived from a shared "drain" timeout (e.g. 30s) so that they can
//    return 503 before the process exits.
```

---

### CR-02: CaptureResult properties are not validated for null at deserialization boundary

**File:** `src/FingerprintAgent/Adapters/CaptureResult.cs:1-15`
**Issue:** `CaptureResult` declares `ImageBytes` as `byte[]` (non-nullable reference
type). When deserializing JSON with `JsonConvert`, a malformed or malicious payload
with `"imageBytes": null` will set `ImageBytes = null`. In `CaptureHandler.Handle`
(line 80), `Convert.ToBase64String(result.ImageBytes)` is called with no null check,
throwing `NullReferenceException` which surfaces as a 500 with message
"Object reference not set..." — exposing internal implementation details.

**Fix:**
```csharp
// In CaptureHandler, guard against null ImageBytes:
var imageBytes = result.ImageBytes ?? Array.Empty<byte>();
response.ImageBytes = Convert.ToBase64String(imageBytes);
```

---

## Warnings

### WR-01: Nullable logger is used without null-coalescing on every call site

**File:** `src/FingerprintAgent/Api/CaptureHandler.cs:36,41,54,62,70,88,102`
**Issue:** `_logger` is a readonly field that may be null (injected as optional,
line 16). Every call site uses the null-conditional operator (`_logger?.Info(...)`)
which is correct, but this pattern is repeated 7 times and easy to forget on new
call sites added later. There is no central guard.

**Fix:** Consider a `NullLogger` singleton pattern or a guard-throw in the
constructor so the field is guaranteed non-null and call sites can use the
non-conditional operator.

---

### WR-02: `FingerprintAgentService.OnStop` catches all exceptions without distinguishing service errors from disposal errors

**File:** `src/FingerprintAgent/Service/FingerprintAgentService.cs:69-77`
**Issue:** The `catch (Exception ex)` block at line 69 swallows every error during
shutdown, including failures in `_httpServer?.Stop()`, `_httpServer?.Dispose()`,
and `_logger?.Dispose()`. If `Dispose()` throws, the original service error
that triggered `OnStop` is lost and the service may appear to stop cleanly when
it did not.

**Fix:** At minimum, log exceptions that indicate incomplete shutdown rather than
swallowing them silently. Ideally, dispose errors should not mask the original
service failure.

---

### WR-03: `HttpServer.Stop()` calls `Dispose()` from `finally` block — double-dispose risk

**File:** `src/FingerprintAgent/Api/HttpServer.cs:161-169`
**Issue:** `Stop()` at line 56 already closes the listener and waits for the worker.
`Dispose()` calls `Stop()` unconditionally. If a caller calls both `Stop()` and
`Dispose()`, `Stop()` is called twice. While `_disposed` guards against re-running
`Stop()`, the method has no idempotency guarantee documented.

**Fix:** Document that `Stop()` and `Dispose()` are not safe to call multiple times,
or refactor so only one of them calls the other.

---

### WR-04: `CorsMiddleware.ApplyCorsHeaders` is called in `HandleRequest` even when an exception is thrown before the handler runs

**File:** `src/FingerprintAgent/Api/HttpServer.cs:122`
**Issue:** `ApplyCorsHeaders` is called on every request path, including after the
handler throws but before the catch block at line 150 returns a 500. If a handler
throws, CORS headers are still written to the broken response, which is harmless
but wasteful. More importantly, the exception handler at line 150 swallows all
exceptions silently, making debugging difficult.

**Fix:** Move `ApplyCorsHeaders` call to only the happy path, or log the exception
before swallowing it.

---

### WR-05: `ConfigLoader.GetStringArray` adds `null` values to the list when section values are missing

**File:** `src/FingerprintAgent/Configuration/ConfigLoader.cs:139`
**Issue:** When iterating `section.GetChildren()` in the fallback path (line 132-141),
`item.Value` can be `null` if the JSON element is present but has no value
(e.g., `"metadata": {}`). The code adds `null` to the list and returns it, causing
`NullReferenceException` in callers that enumerate the array without null checks.

**Fix:**
```csharp
if (item.Value != null)
    list.Add(item.Value);
// else skip null entries, or throw a descriptive configuration error.
```

---

### WR-06: `CaptureRequest` JSON properties are not validated for length or content

**File:** `src/FingerprintAgent/Models/CaptureRequest.cs:6-25`
**Issue:** `ThamChieuId`, `MaPhieu`, etc. are accepted as unbounded strings.
If a caller sends a 1 MB string, it is stored and later serialized back into
logs or responses without truncation. No maximum length is enforced at the model
level. For a fingerprint capture service expected to run on a local machine,
this is a low-risk issue but could enable DoS if exposed to untrusted callers.

**Fix:** Add `[StringLength(50)]` (or appropriate limit) data annotations to the
request model properties.

---

### WR-07: `AgentLogger` base64 redaction pattern can be bypassed with whitespace or newlines

**File:** `src/FingerprintAgent/Logging/AgentLogger.cs:120`
**Issue:** `RedactIfImageData` calls `trimmed.Trim()` then checks `Base64Pattern.IsMatch`.
However, the regex requires the ENTIRE string to be valid base64
(`^(?:[A-Za-z0-9+/]{4})*(?:...)$`). A string like `"data:image/png;base64,/9j/4AAQ..."`
will NOT match the pattern (due to the prefix) and therefore will NOT be redacted.
Image data embedded in a larger string is not caught.

**Fix:** Change the pattern to match base64 substrings within any string, or apply
redaction before trimming.

---

### WR-08: Test class `CorsMiddlewareTests.WildcardMode` has a nested class that does not implement `IDisposable` correctly

**File:** `tests/FingerprintAgent.Tests/CorsMiddlewareTests.cs:22-86`
**Issue:** `WildcardMode` is a nested class that implements `IDisposable` but has
no `using` or `IClassFixture` declaration in the test methods. The xUnit runner
does NOT automatically call `.Dispose()` on nested classes. Each test creates
a new instance via the parameterless constructor (implicit via the class),
but the previous server/client is never disposed between tests if tests run
in unpredictable order. Port conflicts can occur.

**Fix:** Use xUnit's `IClassFixture<T>` pattern or `[Collection]` attribute to
ensure server/client instances are shared and disposed properly.

---

### WR-09: `HttpServerIntegrationTests` uses a shared hardcoded port 5043 which conflicts with default server port

**File:** `tests/FingerprintAgent.Tests/HttpServerIntegrationTests.cs:24`
**Issue:** The integration test uses port 5043, which is also the default port in
`AgentConfig.Http.Port`. If the service is already running on the machine, these
tests will fail with `HttpListenerException: Another process is using port 5043`.

**Fix:** Use a random available port (e.g., `TcpListener` to find a free port)
or a port reserved for test scenarios.

---

## Info

### IN-01: `Program.Main` has unreachable code after `Environment.Exit`

**File:** `src/FingerprintAgent/Program.cs:37`
**Issue:** After `Environment.Exit(1)` at line 35, the `return` on line 36 is
unreachable. This is harmless but indicates a copy-paste artifact.

**Fix:** Remove line 36.

---

### IN-02: `CaptureHandler.WriteErrorResponse` duplicates response-writing logic

**File:** `src/FingerprintAgent/Api/CaptureHandler.cs:107-124`
**Issue:** Both `Handle` (line 96-97) and `WriteErrorResponse` (line 122-123) contain
identical patterns: serialize JSON → get bytes → set status → set content-type →
set content-length → write → close. This violates DRY and could diverge over time.

**Fix:** Extract to a shared `WriteJsonResponse(HttpListenerResponse, object, int)`
helper method.

---

### IN-03: `ConfigLoaderTests.Dispose` catches all exceptions silently

**File:** `tests/FingerprintAgent.Tests/ConfigLoaderTests.cs:161`
**Issue:** The `Dispose` method has an empty `catch { }` block. If directory deletion
fails (e.g., due to file locks), the test author has no way to know.

**Fix:** At minimum, remove the silent catch or assert in debug builds.

---

### IN-04: `AgentLogger` creates a `FileStream` without `FileOptions.Asynchronous`

**File:** `src/FingerprintAgent/Logging/AgentLogger.cs:43-48`
**Issue:** `AgentLogger` uses a synchronous `FileStream` with a `StreamWriter`.
For a service that is expected to be long-running and write many logs, using
the default synchronous file mode is acceptable but could block the request
thread under heavy load.

**Fix:** Use `FileOptions.Asynchronous` flag when creating the `FileStream`
to enable true async file I/O, or use a background queue with `Channel<T>`.

---

### IN-05: `GenerateCorrelationId` uses `Substring` instead of `Guid.ToString("N")` with explicit length

**File:** `src/FingerprintAgent/Logging/AgentLogger.cs:53`
**Issue:** `Guid.NewGuid().ToString("N").Substring(0, 10)` creates a 36-character
GUID string then truncates. While this works, `Guid` already has a format that
produces exactly 10 characters: `Guid.NewGuid().ToString("N").Substring(0, 10)`
is the idiomatic approach. Using `Take(10)` from LINQ is slightly clearer.

**Fix:**
```csharp
return new string(Guid.NewGuid().ToString("N").Take(10).ToArray());
```

---

### IN-06: `Program.Main` uses `Environment.UserInteractive` for mode detection but also has explicit `--service` flag

**File:** `src/FingerprintAgent/Program.cs:15-22`
**Issue:** The logic `serviceMode || !consoleMode` means that if neither flag is
passed and the process is non-interactive (e.g., launched by task scheduler),
it runs as a service. This is correct, but the intent is not obvious and
`Environment.UserInteractive` does not detect all non-interactive scenarios
(e.g., session 0 isolation).

**Fix:** Document the decision matrix clearly or refactor to use only flag-based
detection.

---

_Reviewed: 2026-07-29T00:00:00Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_