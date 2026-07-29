---
phase: "01-foundation-windows-service-http-api-skeleton"
fixed_at: "2026-07-29T00:00:00Z"
review_path: ".planning/phases/01-foundation-windows-service-http-api-skeleton/01-REVIEW.md"
iteration: 1
findings_in_scope: 11
fixed: 10
skipped: 1
status: partial
---

# Phase 01: Code Review Fix Report

**Fixed at:** 2026-07-29T00:00:00Z
**Source review:** `.planning/phases/01-foundation-windows-service-http-api-skeleton/01-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope (CR + WR): 11
- Fixed: 10
- Skipped: 1

---

## Fixed Issues

### CR-01: Fire-and-forget task + hard timeout means in-flight requests are silently dropped

**Files modified:** `src/FingerprintAgent/Api/HttpServer.cs`
**Commit:** `37aff6a`
**Applied fix:** Extended graceful drain timeout from 5 seconds to 30 seconds in `Stop()` method. Changed `_workerTask?.Wait(TimeSpan.FromSeconds(5))` to `_workerTask?.Wait(TimeSpan.FromSeconds(30))` to allow in-flight `HandleRequest` fire-and-forget tasks more time to complete before force-termination.

---

### CR-02: CaptureResult properties are not validated for null at deserialization boundary

**Files modified:** `src/FingerprintAgent/Api/CaptureHandler.cs`
**Commit:** `6cce149`
**Applied fix:** Added null-coalescing guard for `result.ImageBytes` at line 77-78:
```csharp
var imageBytes = result.ImageBytes ?? Array.Empty<byte>();
response.ImageBytes = Convert.ToBase64String(imageBytes);
```
This prevents `NullReferenceException` when `ImageBytes` is null after deserialization.

---

### WR-02: FingerprintAgentService.OnStop catches all exceptions without distinguishing service errors from disposal errors

**Files modified:** `src/FingerprintAgent/Service/FingerprintAgentService.cs`
**Commit:** `70e0764`
**Applied fix:** Refactored `OnStop()` to log each shutdown step error separately instead of swallowing all exceptions. Now tracks `shutdownError` and logs specific errors for token cancellation, HTTP server stop, HTTP server dispose, and logger dispose operations.

---

### WR-03 + WR-04: HttpServer.Stop/Dispose idempotency and CORS headers in exception path

**Files modified:** `src/FingerprintAgent/Api/HttpServer.cs`
**Commit:** `e0bb4d2`
**Applied fix:**
- **WR-03:** Added `if (_disposed) return;` check at start of `Stop()` method and XML doc comments documenting idempotency. Also added doc comments to `Dispose()`.
- **WR-04:** Moved `ApplyCorsHeaders` call from before the handler to only after the handler succeeds, so CORS headers are not applied to broken/error responses.

---

### WR-05: ConfigLoader.GetStringArray adds null values to the list when section values are missing

**Files modified:** `src/FingerprintAgent/Configuration/ConfigLoader.cs`
**Commit:** `e52a5b0`
**Applied fix:** Added null check for `item.Value` before adding to list:
```csharp
if (item.Value != null)
    list.Add(item.Value);
```

---

### WR-06: CaptureRequest JSON properties are not validated for length or content

**Files modified:** `src/FingerprintAgent/Models/CaptureRequest.cs`
**Commit:** `65ef436`
**Applied fix:** Added `[StringLength(50)]` validation attributes to `ThamChieuId`, `MaPhieu`, `LoaiPhieu`, `VaiKyId`, and `NhanLucId` properties, and added `using System.ComponentModel.DataAnnotations;`.

---

### WR-07: AgentLogger base64 redaction pattern can be bypassed with whitespace or newlines

**Files modified:** `src/FingerprintAgent/Logging/AgentLogger.cs`
**Commit:** `87a29b3`
**Applied fix:** Changed the base64 regex pattern from anchored (`^...$`) to unanchored and adjusted to require 10+ base64 chunks (40+ chars) to reduce false positives:
```csharp
"(?:[A-Za-z0-9+/]{4}){10,}(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})?"
```
This allows detection of embedded base64 substrings like `"data:image/png;base64,/9j/4AAQ..."`.

---

### WR-08: Test class CorsMiddlewareTests.WildcardMode has a nested class that does not implement IDisposable correctly

**Files modified:** `tests/FingerprintAgent.Tests/CorsMiddlewareTests.cs`
**Commit:** `6f59b88`
**Applied fix:** Refactored to use xUnit's `IClassFixture<T>` pattern with proper fixture classes (`WildcardModeFixture` and `AllowlistModeFixture`) that implement `IDisposable`. Test classes now receive fixture instances via constructor injection and use `[Collection]` attribute for proper test isolation.

---

### WR-09: HttpServerIntegrationTests uses a shared hardcoded port 5043 which conflicts with default server port

**Files modified:** `tests/FingerprintAgent.Tests/HttpServerIntegrationTests.cs`
**Commit:** `a40641c`
**Applied fix:** Changed from hardcoded port 5043 to dynamic port allocation using `TcpListener` to find an available port:
```csharp
var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
listener.Start();
_port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
```

---

## Skipped Issues

### WR-01: Nullable logger is used without null-coalescing on every call site

**File:** `src/FingerprintAgent/Api/CaptureHandler.cs:36,41,54,62,70,88,102`
**Reason:** Skipped — requires either (a) creating a `NullLogger` singleton pattern (architectural change requiring new class and null-object pattern), or (b) breaking backward compatibility by making logger required (changing constructor from optional to required). The logger is designed as optional for backward compatibility with existing callers (HttpServer passes null logger). The null-conditional operator pattern (`_logger?.Info(...)`) is already correct and safe; the concern was about new call sites forgetting to use it.

---

_Fixed: 2026-07-29T00:00:00Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_