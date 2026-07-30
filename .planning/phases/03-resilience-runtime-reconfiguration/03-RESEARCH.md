# Phase 3 Research: Resilience & Runtime Reconfiguration

**Research date:** 2026-07-30

## Exponential Backoff

### Key Findings

#### .NET Framework 4.8 Timer Options

There are three main timer options in .NET Framework 4.8:

| Timer | Thread Pool | Suitable for Service? | Notes |
|-------|-------------|----------------------|-------|
| `System.Threading.Timer` | Yes | ✅ Yes | Lightweight, pooled thread. Good for periodic health checks. |
| `System.Timers.Timer` | Yes | ✅ Yes | Wraps `Threading.Timer`, adds sync infrastructure (auto-start, elapsed event). Slightly heavier. |
| `System.Windows.Forms.Timer` | No (UI) | ❌ No | Requires UI message loop. Not suitable. |

For **backoff scheduling** in a Windows Service, `System.Threading.Timer` is the best choice — it is lightweight, requires no additional synchronization infrastructure, and its callbacks execute on ThreadPool threads which is appropriate for service code.

**Important:** `System.Threading.Timer` is "fire and forget" — if the service shuts down while a timer callback is executing, the callback may be abruptly terminated. The service's `OnStop` must signal cancellation and wait for the timer to complete its current callback before fully stopping. The existing `FingerprintAgentService.OnStop` already uses `_cts.Cancel()` and `_cts.Dispose()`, but does not explicitly wait for any active timer callbacks.

#### Task.Delay vs Manual Timer for Backoff

`Task.Delay` with `async/await` is only viable if the entire call path is `async`. However, `ScannerManager.Scan()` is synchronous (`CaptureResult Scan()` — no `async`). Introducing `async` would require:
- Changing `IScannerAdapter.Scan()` signature to `Task<CaptureResult>`
- Propagating `async` up through `CaptureHandler.Handle()` and the entire `HttpServer` pipeline
- This is a significant interface change (Phase 2 explicitly kept it synchronous)

The **recommended approach** is to keep everything synchronous and use a manual backoff counter that is checked at the start of `Scan()`. No `Timer` is needed for backoff itself — the backoff is just a counter checked on each capture request.

#### Recommended Backoff Implementation

```csharp
// In ScannerManager.cs
private int _backoffStep; // 0=10s, 1=30s, 2=60s, 3=120s (max)
private DateTime _backoffUntil; // UTC time when backoff expires
private readonly object _backoffLock = new object();

private static readonly int[] BackoffDelaysSeconds = new[] { 10, 30, 60, 120 };

// At start of Scan():
lock (_backoffLock)
{
    if (_backoffStep > 0 && DateTime.UtcNow < _backoffUntil)
    {
        // Still in backoff — but always try anyway (hot-plug friendly, per D-04)
        // Just log that we're still in backoff cycle
        _logger?.Debug(cid, $"ScannerManager: in backoff cycle step={_backoffStep}, "
            + $"remaining={(_backoffUntil - DateTime.UtcNow).TotalSeconds:F0}s — will probe anyway");
    }
}

// After capture failure (when we decide to back off):
private void ApplyBackoff()
{
    lock (_backoffLock)
    {
        _backoffStep = Math.Min(_backoffStep + 1, BackoffDelaysSeconds.Length - 1);
        _backoffUntil = DateTime.UtcNow.AddSeconds(BackoffDelaysSeconds[_backoffStep]);
        _logger?.Info(cid, $"ScannerManager: backoff applied step={_backoffStep} "
            + $"for {BackoffDelaysSeconds[_backoffStep]}s");
    }
}

// On successful capture:
lock (_backoffLock)
{
    _backoffStep = 0;
    _backoffUntil = DateTime.MinValue;
}
```

**Hot-plug friendly:** The backoff does NOT block `Scan()` from trying immediately. The backoff state is advisory — each `Scan()` attempt still tries to connect. The backoff step increments only after a confirmed failure.

**Thread safety:** All backoff state access is protected by `_backoffLock`. `_backoffStep` is `int` (atomic read/write), `_backoffUntil` is `DateTime` (also safe for simple reads/writes under lock).

#### Where to Apply Backoff

Per D-04 (hot-plug friendly) and D-02: backoff should be applied when `Scan()` has tried ALL adapters and ALL have failed. This is the point where we know we genuinely have no scanner — not when a single adapter fails. This matches the existing logic in `ScannerManager.Scan()` which already handles this:

```csharp
// At the point where all adapters have failed and we return SCANNER_NOT_CONNECTED:
ApplyBackoff();
return CaptureResult.Fail("SCANNER_NOT_CONNECTED", ...);
```

**Reset on success:** When any adapter returns `IsSuccess=true`, reset backoff step to 0.

---

## FileSystemWatcher Reliability

### Key Findings

#### Common Pitfalls

1. **File locking**: When a text editor saves `config.json`, it often writes to a temp file then rename. This can cause `FileSystemWatcher` to miss the event or see a transient empty file.

2. **Buffer overflow**: The `FileSystemWatcher` internal buffer is 4KB by default. Rapid successive changes can overflow, causing events to be lost. Use `NotifyFilter` to filter to only `LastWrite` and `Size` changes to reduce buffer pressure.

3. **Duplicate events**: A single save can generate multiple events (`Changed`, `Created`, `Renamed`). Always deduplicate using a debounce timer.

4. **Subsequent save timing**: Some editors (notably Visual Studio, Notepad++) write the file twice — first truncate, then write content. This results in two `Changed` events in quick succession.

5. **Internal buffer overflow**: MSDN explicitly warns that the `FileSystemWatcher` buffer can overflow on heavily-used directories. Watching a file that changes very rapidly will lose events.

#### Recommended FileSystemWatcher Implementation

```csharp
using System.IO;
using System.Timers;

public class ConfigFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly AgentLogger _logger;
    private readonly string _configPath;
    private bool _disposed;

    public event Action<string> ConfigReloaded;

    public ConfigFileWatcher(string configPath, AgentLogger logger)
    {
        _configPath = configPath;
        _logger = logger;

        var directory = Path.GetDirectoryName(configPath);
        var fileName = Path.GetFileName(configPath);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnRawChanged;

        // 300ms debounce — enough to coalesce VS/Notepad++ double-save patterns
        // without delaying real single saves noticeably
        _debounceTimer = new Timer(300);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += OnDebounceElapsed;
    }

    private void OnRawChanged(object sender, FileSystemEventArgs e)
    {
        // Reset debounce timer on every change event
        // If multiple events come in quickly, only the last one fires
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
    {
        try
        {
            _logger?.Info(null, "ConfigFileWatcher: config.json changed, attempting reload");

            // Try reloading config
            var newConfig = ConfigLoader.LoadFromDirectory(
                Path.GetDirectoryName(_configPath));

            // Validate partial reload (only ScannerConfig and CorsConfig per D-06)
            if (newConfig.Scanner == null || newConfig.Cors == null)
            {
                _logger?.Error(null, "ConfigFileWatcher: reload missing ScannerConfig or CorsConfig — keeping old config");
                return;
            }

            ConfigReloaded?.Invoke(_configPath);
            _logger?.Info(null, "ConfigFileWatcher: config.json reload complete");
        }
        catch (Exception ex)
        {
            _logger?.Error(null, $"ConfigFileWatcher: reload failed, keeping old config — {ex.Message}");
            // Per D-08: keep old config, log error, don't crash
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer?.Dispose();
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnRawChanged;
        _watcher?.Dispose();
    }
}
```

#### File Locking Workaround

When reading the config file in `ConfigLoader.LoadFromDirectory()`, use `FileShare.ReadWrite` to avoid locking issues:

```csharp
// In ConfigLoader — add reloadOnChange support
var config = new ConfigurationBuilder()
    .SetBasePath(directoryPath)
    .AddJsonFile("config.json", optional: false, reloadOnChange: false)
    .Build();
```

For manual file reading (better for reload scenarios):

```csharp
string json;
using (var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var reader = new StreamReader(fs))
{
    json = reader.ReadToEnd();
}
```

#### Debounce Duration

300ms handles:
- VS double-save (~200ms apart)
- Notepad++ double-save (~100-200ms apart)
- Single immediate saves (<50ms)

This is a reasonable balance between responsiveness and coalescing duplicate events.

---

## Thread-Safe Config Reload

### Key Findings

#### ReaderWriterLockSlim vs `lock` (Monitor)

`ReaderWriterLockSlim` is designed for scenarios with **frequent reads** and **infrequent writes**. It allows multiple concurrent readers but exclusive writers.

However, in this application:
- Reads happen on **every HTTP request** (CORS headers, capture handler accessing config)
- Writes happen **only when config file changes** (very infrequent — minutes or hours between changes)

The read-to-write ratio is extremely high, but the absolute write frequency is very low.

**Analysis:**

| Aspect | `lock` (Monitor) | `ReaderWriterLockSlim` |
|--------|-----------------|------------------------|
| Simplicity | ✅ Simple | More complex |
| Read performance | ✅ Good enough | Slightly better under high contention |
| Write performance | ✅ Good enough | Slightly better |
| Deadlock risk | ✅ Low | Can deadlock if not used carefully |
| .NET 4.8 support | ✅ Full | ✅ Full |
| Upgrade read→write | N/A | ❌ Not supported in Slim |

For this use case, **simple `lock`** is preferred because:
1. Writes are extremely infrequent (minutes to hours apart)
2. Request handlers are very short-lived (milliseconds)
3. A brief blocking of readers during reload is acceptable
4. Simpler code = fewer bugs

#### `volatile` and Memory Barriers

`volatile` fields in .NET provide:
- Acquire semantics on read (subsequent code sees all writes before the volatile read)
- Release semantics on write (all writes before the volatile write are visible to subsequent readers)

However, `volatile` alone is insufficient for complex state (like a struct or object graph). For the config reload scenario, `volatile` is useful for **primitive type** fields.

For the `AgentConfig` object (a reference type), we can use:

```csharp
// Approach 1: Simple lock for the object reference
private AgentConfig _currentConfig;
private readonly object _configLock = new object();

// For reads (every request handler):
lock (_configLock)
{
    var mode = _currentConfig.Cors.Mode;
    var origins = _currentConfig.Cors.AllowedOrigins;
    // Use copied values locally
}

// For writes (ConfigFileWatcher callback):
var newConfig = ConfigLoader.LoadFromDirectory(...);
lock (_configLock)
{
    _currentConfig = newConfig;
}
```

**Alternative: `volatile` for the reference itself**

```csharp
private volatile AgentConfig _currentConfig;

// Read — no lock needed for the reference read itself
// But if you need to read multiple fields consistently, you still need a local copy:
var config = _currentConfig; // volatile read — safe for reference
var mode = config.Cors.Mode; // now reading from local variable
```

The `volatile` approach for the reference is sufficient if we copy the reference to a local variable before reading multiple fields. The C# memory model guarantees the reference copy is complete before accessing fields.

#### Recommended Implementation for Config Access

```csharp
// In a shared ConfigManager or FingerprintAgentService:

private AgentConfig _currentConfig;
private readonly object _configLock = new object();

// For CaptureHandler / CorsMiddleware — read on every request:
public ScannerConfig GetScannerConfig()
{
    lock (_configLock)
    {
        return _currentConfig.Scanner; // return copy or reference to subsection
    }
}

public CorsConfig GetCorsConfig()
{
    lock (_configLock)
    {
        // Return a copy to avoid the caller reading partially-replaced fields
        return new CorsConfig
        {
            Mode = _currentConfig.Cors.Mode,
            AllowedOrigins = (string[])_currentConfig.Cors.AllowedOrigins.Clone()
        };
    }
}

// For ConfigFileWatcher — write on file change:
public void ReloadConfig(AgentConfig newConfig)
{
    lock (_configLock)
    {
        _currentConfig = newConfig;
    }
}
```

For maximum safety during partial config updates (D-09: active adapter stays the same), we replace the entire `AgentConfig` object atomically rather than updating individual sections in place.

#### What to Reload (D-06, D-09)

Per D-06, only `ScannerConfig` and `CorsConfig` are reloaded. The `HttpServer` does NOT rebuild its listener on reload — the HTTP port stays fixed for the service lifetime.

The `CorsMiddleware` already stores `CorsConfig` fields at construction time. For hot reload to work, `CorsMiddleware` should re-read `_config.Cors` on each request rather than caching:

```csharp
// Current (in HttpServer.cs constructor):
_cors = new CorsMiddleware(config.Cors.Mode, config.Cors.AllowedOrigins);

// Fixed (re-read on each request — already the case in HandleRequest calling _cors.ApplyCorsHeaders):
// CorsMiddleware.ApplyCorsHeaders reads from stored fields
// To support hot reload: pass a Func<CorsConfig> or store the config reference

// Simpler: update _cors fields directly on reload
public void UpdateCorsConfig(CorsConfig newCors)
{
    // If CorsMiddleware stores fields:
    lock (_corsLock)
    {
        _mode = newCors.Mode;
        _allowedOrigins = newCors.AllowedOrigins;
    }
}
```

The cleanest approach is to give `HttpServer` a `UpdateCorsConfig()` method and call it from `FingerprintAgentService` after a successful config reload.

---

## Health Check Implementation

### Key Findings

#### Timer vs Dedicated Thread

A dedicated background thread with `Thread.Sleep` or a `ManualResetEvent` wait is viable but offers no advantage over `System.Threading.Timer` for this scenario. The timer is lighter weight and integrates with the ThreadPool.

**Options:**

| Approach | Pros | Cons |
|----------|------|------|
| `System.Threading.Timer` (30s interval) | Lightweight, auto-managed | Callbacks on ThreadPool |
| `System.Timers.Timer` | Same, with sync context support | Slightly heavier |
| Dedicated `Thread` + `WaitHandle` | Full control over timing | More code, manual lifecycle management |
| Task.Delay loop | Modern async style | Requires async pipeline changes |

`System.Threading.Timer` is the correct choice for .NET Framework 4.8.

#### Health Check Logic (D-15, D-16, D-17)

The health check timer should:
1. **Only observe `IsConnected`** — NOT call `Initialize()` or `Scan()`. This avoids interfering with normal capture requests and avoids accidentally keeping a connection alive artificially.
2. **Log disconnect events clearly**
3. **Start or continue backoff** — when `IsConnected = false`, increment backoff (if not already in backoff)
4. **NOT fail the `/health` endpoint** immediately — per D-16, health endpoint stays healthy until backoff cycle fully expires

```csharp
// In FingerprintAgentService (or a dedicated HealthMonitor class)
private Timer _healthCheckTimer;
private readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(30);

public void StartHealthCheckTimer()
{
    _healthCheckTimer = new Timer(HealthCheckCallback, null,
        _healthCheckInterval, _healthCheckInterval);
}

private void HealthCheckCallback(object state)
{
    try
    {
        // D-17: Only observe state, don't probe with Initialize/Scan
        bool connected = _scanner.IsConnected;

        if (!connected)
        {
            // D-16: Log clearly
            _logger?.Warn(null, "HealthCheck: scanner not connected");

            // Trigger backoff (if ScannerManager owns the backoff, call into it)
            // Or: _scanner.BackoffIfDisconnected() — requires new interface method
        }
    }
    catch (Exception ex)
    {
        _logger?.Error(null, $"HealthCheck: callback threw {ex.GetMessage()}");
    }
}
```

#### Backoff Interaction

Since `ScannerManager` owns backoff state, the health check timer should signal `ScannerManager` when it observes a disconnect. Options:

1. **Add a method to `IScannerAdapter`:** `void ObserveDisconnection()` — calls into `ScannerManager` backoff logic. Simple but pollutes the interface.
2. **Keep backoff entirely in `ScannerManager`:** Health check timer does nothing special — it just observes. `ScannerManager` applies backoff on capture failure. The health check log message is the only "action".
3. **Event-based:** `ScannerManager` raises an event; service subscribes to update health status.

Option 2 is the simplest and matches D-16: health check observes and logs, backoff is triggered by actual capture failures (not by health check).

#### Interaction with `/health` Endpoint

The `/health` endpoint (in `HealthHandler`) currently returns `"status": "healthy"`. Per D-16, it should observe backoff state:

```csharp
public void Handle(...)
{
    bool connected = scanner.IsConnected;
    bool inBackoff = /* check backoff state somehow */;

    string status;
    int httpStatus;

    if (connected)
    {
        status = "healthy";
        httpStatus = 200;
    }
    else if (inBackoff && currentBackoffStep >= BackoffDelaysSeconds.Length - 1)
    {
        // Backoff fully exhausted — report degraded
        status = "degraded";
        httpStatus = 503;
    }
    else
    {
        status = "degraded";
        httpStatus = 200; // D-16: still return 200 during backoff recovery
    }
}
```

However, exposing backoff state to `HealthHandler` requires `ScannerManager` to expose it (a property or method). Since `ScannerManager` already has backoff state internally, adding a public getter `InBackoff` and `BackoffStep` is reasonable.

---

## Error Response Design

### Key Findings

#### HTTP Status Code Selection

The existing code maps error codes to HTTP status in `CaptureHandler.WriteErrorResponse`. Currently all exceptions get 500. The decisions D-10, D-11, D-12 establish the mapping:

| Error Code | HTTP Status | Rationale |
|------------|-------------|-----------|
| `SCANNER_NOT_CONNECTED` | 503 | Service temporarily unavailable (scanner not present) |
| `CAPTURE_TIMEOUT` | 504 | Gateway timeout — upstream scanner did not respond in time |
| `CAPTURE_FAILED` | 500 | Internal server error — SDK error during capture |
| `INVALID_REQUEST` | 400 | Bad request — client sent malformed request |
| `CONFIG_ERROR` | 500 | Internal error — misconfiguration |
| Any other | 500 | Defensive default |

These align with RFC 9110 HTTP semantics:
- 503 = "temporary server-side condition preventing successful response"
- 504 = "upstream server failed to provide timely response"
- 500 = "unexpected internal server error"

#### Structured Error Response Format

The existing `CaptureResponse` class already has `IsSuccess`, `ErrorMessage`, and `ErrorCode` fields. The `CaptureHandler.WriteErrorResponse` creates a `CaptureResponse` with `IsSuccess = false` and the error fields.

For IT debugging (D-11: `VendorErrorCode` in `errorMessage`):

```csharp
public class ErrorResponse
{
    [JsonProperty("isSuccess")]
    public bool IsSuccess { get; set; }

    [JsonProperty("errorCode")]
    public string ErrorCode { get; set; }

    [JsonProperty("errorMessage")]
    public string ErrorMessage { get; set; }

    [JsonProperty("vendorErrorCode")]
    public string VendorErrorCode { get; set; } // Only present when non-null

    [JsonProperty("timestamp")]
    public string Timestamp { get; set; }
}
```

Example response:
```json
{
  "isSuccess": false,
  "errorCode": "SCANNER_NOT_CONNECTED",
  "errorMessage": "No scanner connected — all adapters failed to initialize or capture",
  "vendorErrorCode": "zkfp2.E_INIT_FAILED",
  "timestamp": "2026-07-30T10:15:30.000Z"
}
```

The `vendorErrorCode` field is only included when non-null, per D-11 (only expose for debugging, not user-facing).

#### Error Code Exposure Architecture

Per D-12, per-adapter error translation happens in each adapter, producing a standardized `CaptureResult.ErrorMessage` that already contains the vendor error code. `CaptureHandler` does not need to look at `VendorErrorCode` separately — it's already baked into `ErrorMessage`.

However, to separate the concerns cleanly:
1. `CaptureResult.ErrorMessage` = user-readable message (localized)
2. `CaptureResult.VendorErrorCode` = raw vendor code string (for IT logs)

The HTTP response should expose both:
```json
{
  "isSuccess": false,
  "errorCode": "CAPTURE_FAILED",
  "errorMessage": "Capture failed: device busy",
  "vendorErrorCode": "SGFDxLibrary.SG_DV生_CONNECT_ERROR"
}
```

#### CaptureHandler Error Mapping Update

The existing `WriteErrorResponse` method needs to accept the vendor error code:

```csharp
private static void WriteErrorResponse(
    HttpListenerContext context,
    int statusCode,
    bool isSuccess,
    string errorMessage,
    string errorCode,
    string vendorErrorCode = null)
{
    var response = new CaptureResponse
    {
        IsSuccess = isSuccess,
        ErrorMessage = errorMessage,
        ErrorCode = errorCode,
        VendorErrorCode = vendorErrorCode  // new field
    };
    // ... serialize and send
}
```

And in `CaptureHandler.Handle()`:
```csharp
catch (Exception ex)
{
    var errorMessage = $"Capture failed: {ex.Message}";
    _logger?.Error(cid, $"Capture failed — CAPTURE_FAILED: {ex.Message}");
    WriteErrorResponse(context, 500, false, errorMessage, "CAPTURE_FAILED",
        scanner.VendorErrorCode);
}
```

---

## Recommendations

### Exponential Backoff
- **Implement as a simple integer counter + DateTime in `ScannerManager`**, checked on each `Scan()` call. No `Timer` needed for backoff itself.
- Use `_backoffLock` to protect all backoff state access.
- Always allow `Scan()` to proceed (hot-plug friendly), but log the current backoff state when in a backoff cycle.
- Increment backoff step only when ALL adapters have failed (at the point of returning `SCANNER_NOT_CONNECTED`).
- Reset backoff step to 0 on any successful capture.

### FileSystemWatcher
- Use `NotifyFilter = LastWrite | Size` to minimize buffer pressure.
- Implement a 300ms debounce timer to coalesce duplicate save events from VS and Notepad++.
- On reload failure (bad JSON, parse error), keep the old config and log an error — do not crash.
- Wrap file reading in `try-catch` and always validate that `ScannerConfig` and `CorsConfig` are non-null after reload.
- Dispose `FileSystemWatcher` and debounce timer in `OnStop`.

### Thread-Safe Config Reload
- Use a simple `lock` around the `AgentConfig` reference. Writes are extremely infrequent (minutes to hours), so reader blocking is acceptable.
- Copy the reference to a local variable before reading multiple fields in a request handler.
- For `CorsMiddleware` hot-reload: give `HttpServer` an `UpdateCorsConfig(CorsConfig)` method; call it after successful reload.
- Do NOT rebuild `HttpServer` on config reload. Only update the fields that are allowed to change.

### Health Check
- Use `System.Threading.Timer` with 30-second interval.
- Timer callback only reads `_scanner.IsConnected` — does NOT call `Initialize()` or `Scan()`.
- Log disconnect events clearly with correlation ID and current backoff step.
- The health check timer does NOT apply backoff — it only observes. Backoff is triggered by `ScannerManager` on capture failures.
- Add `InBackoff` and `BackoffStep` public properties to `ScannerManager` so `HealthHandler` can report `"status": "degraded"` appropriately.
- Dispose the timer in `OnStop` before disposing other resources.

### Error Response Design
- Map `SCANNER_NOT_CONNECTED` → 503, `CAPTURE_TIMEOUT` → 504, `CAPTURE_FAILED` → 500, `INVALID_REQUEST` → 400.
- Add `VendorErrorCode` field to `CaptureResponse` — only populate when non-null.
- Include `Timestamp` in error responses for log correlation.
- `VendorErrorCode` comes from `scanner.VendorErrorCode` property — already available on `IScannerAdapter`.
- Keep `ErrorMessage` as the primary user-facing message; `VendorErrorCode` is supplemental for IT debugging only.