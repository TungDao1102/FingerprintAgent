# Phase 01 Research: Foundation — Windows Service + HTTP API Skeleton

**Date:** 2026-07-28
**Prepared for:** PLAN.md generation
**Focus:** Technical feasibility, implementation guidance, known pitfalls, NuGet compatibility

---

## 1. Validation Architecture

This section defines how each requirement will be validated — the basis for generating Nyquist validation scripts.

### API-01: HTTP endpoint `POST /api/capture` on `localhost:5043`
- **Validate:** Send `POST http://localhost:5043/api/capture` with valid JSON body → expect HTTP 200 + JSON response with `isSuccess`, `imageBytes`, `verificationData`.
- **Method:** `Invoke-RestMethod` from PowerShell.
- **Negative:** Send to wrong port → connection refused; wrong path → HTTP 404.

### API-02: Request body schema
- **Validate:** POST with minimal valid JSON `{"thamChieuId":"test","maPhieu":"P001"}` → HTTP 200.
- **Negative:** POST with empty body → HTTP 400 + `INVALID_REQUEST`.
- **Negative:** POST with malformed JSON → HTTP 400 + `INVALID_REQUEST`.

### API-03: Response format
- **Validate:** Check response contains `isSuccess` (bool), `imageBytes` (base64 string), `mimeType` ("image/png"), `capturedAt` (ISO 8601), `deviceId` (string), `verificationData` (base64 SHA-256).
- **Method:** Parse JSON response, validate field types and presence.

### API-04: Error response codes
- **Validate:** When no scanner connected, `POST /api/capture` → HTTP 503 with `errorCode: "SCANNER_NOT_CONNECTED"`.
- **Validate:** Bad request → HTTP 400 with `INVALID_REQUEST`.
- **Validate:** Timeout (Phase 3) → HTTP 504 with `CAPTURE_TIMEOUT`.

### API-05: CORS headers
- **Validate:** Send `OPTIONS /api/capture` with `Origin: http://example.com` → response includes `Access-Control-Allow-Origin: *` (wildcard mode).
- **Validate:** In allowlist mode, send `Origin: http://evil.com` → response does NOT include `Access-Control-Allow-Origin`.
- **Method:** `curl` or `Invoke-WebRequest -Method Options` with Origin header.

### API-06: `GET /health`
- **Validate:** `GET http://localhost:5043/health` → HTTP 200 with JSON containing `status`, `deviceId`, `uptime`.
- **Validate:** When scanner disconnected → HTTP 503 (Phase 3, but the skeleton should structure for it).

### SVC-01: Service install and run
- **Validate:** Run `Install-Service.ps1` → `Get-Service FingerprintAgent` returns service with status `Stopped`.
- **Validate:** `Start-Service FingerprintAgent` → status `Running`.
- **Validate:** Service process shows in `Get-Process`.

### SVC-02: Auto-start
- **Validate:** `Get-Service FingerprintAgent` shows `StartType Automatic`.
- **Validate:** Reboot (manual test or `Get-CimInstance` check) → service starts automatically.

### SVC-03: OnStart/OnStop lifecycle
- **Validate:** After `Start-Service`, HTTP endpoints are responsive within 5 seconds.
- **Validate:** After `Stop-Service`, HTTP listener port is released (verified via `netstat -an | findstr 5043` showing no LISTEN).

### SVC-04: Logging to file + EventLog
- **Validate:** After service start, `C:\ProgramData\FingerprintAgent\Logs\agent.log` exists with content containing startup entry.
- **Validate:** `Get-EventLog -LogName Application -Source FingerprintAgent` returns entries.
- **Validate:** Log entry format matches `[timestamp] [LEVEL] [correlationId] message`.

### SVC-05: LocalSystem account
- **Validate:** `Get-Service FingerprintAgent` shows `StartName` as `LocalSystem` or empty (default).

### CFG-01: Read config.json
- **Validate:** Modify `config.json` port to 5044, restart service → endpoint responds on 5044.
- **Validate:** File missing at startup → service fails to start with clear error in EventLog.

### CFG-02: Config schema coverage
- **Validate:** Config contains `http.host`, `http.port`, `cors.mode`, `cors.allowedOrigins`, `logging.level`, `logging.file`, `scanner.priority`, `security.bindIp`.

### CFG-04: Invalid config handling
- **Validate:** Corrupt `config.json` (e.g., missing comma) → service fails to start with descriptive error in EventLog.
- **Validate:** Missing required field → same behavior.

### SEC-01: Bind 127.0.0.1 by default
- **Validate:** `netstat -an | findstr 5043` shows `127.0.0.1:5043` in LISTEN state.
- **Validate:** Override `http.host` to `0.0.0.0` in config → binds all interfaces.

### SEC-02: CORS wildcard/allowlist
- **Validate:** Default mode = wildcard: all origins accepted.
- **Validate:** Mode = allowlist: only origins in `cors.allowedOrigins` accepted.

### SEC-03: No fingerprint data on disk
- **Validate:** Search entire install directory (excluding Logs) — no `.png`, `.bmp`, `.jpg` files created during capture.
- **Validate:** Log files contain only metadata (deviceId, timestamps), no image data.

### SEC-04: Log metadata only
- **Validate:** grep log file for base64 patterns (long alphanumeric strings) — none found matching `imageBytes` content.

### OBS-01: Structured log format
- **Validate:** Log entries match regex `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\s+\[(INFO|WARN|ERROR|DEBUG)\]\s+\[[\w-]+\]\s+.*$`.

### OBS-02: Event logging
- **Validate:** Log contains entries for: `Service starting`, `Service started`, `Scanner connected`, `Capture request received`, `Capture completed`, `Capture failed`, `Service stopping`.

### OBS-03: Health check status codes
- **Validate:** `GET /health` returns `{"status": "healthy"}` → HTTP 200 when scanner connected.
- **Validate:** `GET /health` returns HTTP 503 when scanner disconnected (Phase 1 mock is always "connected" so this is structured for Phase 3).

---

## 2. Concrete Technical Guidance

### 2.1 HttpListener Prefix Registration and URL ACL

**How it works:**
- `HttpListener` uses Windows HTTP Server API (HTTP.sys) in kernel mode.
- Prefixes must be registered: `http://127.0.0.1:5043/` (trailing slash required).
- A **loopback exemption is NOT needed** when binding to `127.0.0.1` — it works with any user account, including non-admin and service accounts.
- If binding to `http://+:5043/` or `http://*:5043/`, a URL ACL reservation is required: `netsh http add urlacl url=http://+:5043/ user=NT AUTHORITY\LOCAL SERVICE`

**For this project (bind to 127.0.0.1):**
- `HttpListener` running as LocalSystem has free access to `http://127.0.0.1:5043/`.
- For developer testing as a console app (not installed as service), the same prefix works without admin rights.
- The Install-Service.ps1 script should still include URL ACL setup for forward-compatibility when the bind address changes.

**Code pattern:**
```csharp
var listener = new HttpListener();
listener.Prefixes.Add($"http://{host}:{port}/");
listener.Start();
```

**Pitfall:** If `Start()` throws `HttpListenerException` (0x5 / Access Denied), it means the URL ACL is missing or the user doesn't have permission. This is most common when using non-loopback IPs.

### 2.2 ServiceBase Lifecycle and OnStart/OnStop

**OnStart contract:**
- MUST return within 30 seconds, or Windows assumes the service failed to start.
- DO NOT block — start async operations on background threads.
- Store a `CancellationTokenSource` for graceful shutdown.

**Pattern:**
```csharp
private HttpListener _listener;
private CancellationTokenSource _cts;

protected override void OnStart(string[] args)
{
    _cts = new CancellationTokenSource();
    // Start HTTP listener
    _listener = new HttpListener();
    _listener.Prefixes.Add("http://127.0.0.1:5043/");
    _listener.Start();
    // Dispatch processing to background thread
    Task.Factory.StartNew(() => ProcessRequests(_cts.Token), TaskCreationOptions.LongRunning);
    // Log start
    Logger.Info("Service started");
}

protected override void OnStop()
{
    _cts?.Cancel();
    _listener?.Stop();
    _listener?.Close();
    Logger.Info("Service stopped");
}
```

**OnStop contract:**
- You have about 20-30 seconds to stop gracefully.
- Set `ServiceBase.RequestAdditionalTime(TimeSpan.FromSeconds(30))` if more time is needed.
- Stop the `HttpListener` first (this causes pending `GetContext()` to throw), then join/abort the worker thread.

**AutoLog property:** By default, `ServiceBase.AutoLog = true` writes start/stop/install events to the Application event log. For Phase 1, leave AutoLog = true for basic operation, but implement a custom logging layer on top (Trace + EventLog.WriteEntry) for richer structured logging.

### 2.3 Installing/Uninstalling Windows Service

**PowerShell scripts for dev workflow:**

**Install-Service.ps1:**
```powershell
$serviceName = "FingerprintAgent"
$binPath = "C:\Program Files\FingerprintAgent\FingerprintAgent.exe"

if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "Service $serviceName already exists. Stopping and removing..."
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName
    Start-Sleep -Seconds 2
}

# Create service
New-Service -Name $serviceName `
    -BinaryPathName "$binPath --service" `
    -DisplayName "Fingerprint Agent" `
    -Description "Local fingerprint capture service providing HTTP API for web applications" `
    -StartupType Automatic

# Set recovery options (restart on first and second failure)
sc.exe failure $serviceName reset=86400 actions=restart/5000/restart/10000/restart/30000

# Create log directory
$logDir = "C:\ProgramData\FingerprintAgent\Logs"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force
}

# Register EventLog source (requires admin once)
if (-not [System.Diagnostics.EventLog]::SourceExists($serviceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($serviceName, "Application")
}

Write-Host "Service $serviceName installed successfully."
```

**Uninstall-Service.ps1:**
```powershell
$serviceName = "FingerprintAgent"

$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName
    Write-Host "Service $serviceName removed."
} else {
    Write-Host "Service $serviceName not found."
}
```

**Test-Capture.ps1:**
```powershell
$baseUrl = "http://localhost:5043"

# Test health
$health = Invoke-RestMethod -Uri "$baseUrl/health" -Method Get
Write-Host "Health: $($health | ConvertTo-Json)"

# Test capture
$body = @{
    thamChieuId = "test-001"
    maPhieu = "P2026-0001"
    loaiPhieu = "KB"
    vaiKyId = "VK001"
    nhanLucId = "NL001"
    metadata = @{
        source = "Test-Capture.ps1"
    }
} | ConvertTo-Json

try {
    $capture = Invoke-RestMethod -Uri "$baseUrl/api/capture" -Method Post `
        -Body $body -ContentType "application/json"
    Write-Host "Capture result: isSuccess=$($capture.isSuccess), deviceId=$($capture.deviceId)"
    Write-Host "verificationData (SHA-256): $($capture.verificationData)"
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
}
```

**Pitfalls:**
- `New-Service` and `sc.exe delete` require **elevated** (Run as Administrator) PowerShell.
- After `sc.exe delete`, the service entry may linger for a few seconds. Always wait 2 seconds before `New-Service`.
- The `--service` argument on BinaryPathName tells the application to run as a service (via `ServiceBase.Run`). Without it, the app can run as console for debugging.
- EventLog source registration (`CreateEventSource`) requires admin the **first time**. After registration, any user can write with that source. The Install-Service.ps1 script should handle this.

### 2.4 Project Structure (Single .csproj)

```
FingerprintAgent/
├── FingerprintAgent.sln
├── src/
│   └── FingerprintAgent/
│       ├── FingerprintAgent.csproj
│       ├── Program.cs                          # Entry point: args "--service" or console
│       ├── Service/
│       │   ├── AgentService.cs                 # ServiceBase subclass
│       │   └── ServiceInstaller.cs             # ProjectInstaller + ServiceProcessInstaller
│       ├── Api/
│       │   ├── HttpServer.cs                   # HttpListener wrapper, prefix mgmt, request dispatch
│       │   ├── Router.cs                       # URL-to-handler mapping
│       │   ├── HealthHandler.cs                # GET /health
│       │   ├── CaptureHandler.cs               # POST /api/capture
│       │   └── CorsMiddleware.cs               # CORS header injection
│       ├── Adapters/
│       │   ├── IScannerAdapter.cs              # Interface contract
│       │   ├── MockScannerAdapter.cs           # Mock implementation
│       │   └── CaptureResult.cs                # Result DTO
│       ├── Configuration/
│       │   ├── AgentConfig.cs                  # Strongly-typed config model
│       │   └── ConfigLoader.cs                 # ConfigurationBuilder wrapper
│       └── Logging/
│           ├── Logger.cs                       # Static/logical logging API
│           ├── FileLogger.cs                   # TextWriterTraceListener wrapper
│           └── EventLogLogger.cs               # EventLog wrapper
├── config.json                                 # Default configuration
└── scripts/
    ├── Install-Service.ps1
    ├── Uninstall-Service.ps1
    └── Test-Capture.ps1
```

**Rationale for single .csproj:** Phase 1 is MVP/Walking Skeleton. Splitting projects before the architecture stabilizes creates overhead. The folder structure mirrors the intended future project boundaries (Adapters → separate library in Phase 2).

### 2.5 Mock PNG Generation with System.Drawing and SHA-256

**GDI+ in Windows Service context:**
- Microsoft officially states GDI+ is "not supported for use within a Windows service" (MSDN documentation).
- **In practice:** For in-memory bitmap operations (no window station interaction), it works reliably when running as LocalSystem.
- The risk is around window station/desktop access, not pure memory operations.
- For Phase 1 (mock only), this risk is negligible. For Phase 2 (real scanner data), consider whether real scanners provide image bytes directly (most do), making `System.Drawing` only needed for validation/debug overlays.

**Mock generation code pattern:**
```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;

public byte[] GenerateMockPng(int width = 320, int height = 240)
{
    using (var bitmap = new Bitmap(width, height))
    using (var graphics = Graphics.FromImage(bitmap))
    {
        // Fill background with light gray
        graphics.Clear(Color.LightGray);

        // Draw gradient-like pattern
        using (var brush = new SolidBrush(Color.FromArgb(50, 100, 150)))
        {
            graphics.FillEllipse(brush, 10, 10, width - 20, height - 20);
        }

        // Draw border
        using (var pen = new Pen(Color.DarkGray, 2))
        {
            graphics.DrawRectangle(pen, 1, 1, width - 2, height - 2);
        }

        // Add label
        using (var font = new Font("Consolas", 10))
        using (var brush = new SolidBrush(Color.Black))
        {
            graphics.DrawString("MOCK SCANNER", font, brush, 10, 10);
        }

        // Save to MemoryStream as PNG
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}

public static string ComputeSha256Base64(byte[] imageBytes)
{
    using (var sha256 = SHA256.Create())
    {
        return Convert.ToBase64String(sha256.ComputeHash(imageBytes));
    }
}
```

**DeviceId:** `"mock-scanner-001"` for Phase 1.

### 2.6 Config.json Schema and Microsoft.Extensions.Configuration.Json

**Schema:**
```json
{
  "service": {
    "name": "FingerprintAgent",
    "displayName": "Fingerprint Agent",
    "description": "Local fingerprint capture service"
  },
  "http": {
    "host": "127.0.0.1",
    "port": 5043
  },
  "cors": {
    "mode": "wildcard",
    "allowedOrigins": []
  },
  "scanner": {
    "priority": ["SecuGen", "DigitalPersona", "Futronic"],
    "mockMode": true
  },
  "logging": {
    "level": "INFO",
    "file": "C:\\ProgramData\\FingerprintAgent\\Logs\\agent.log",
    "maxSizeMb": 10,
    "maxFiles": 5
  },
  "security": {
    "bindIp": "127.0.0.1"
  }
}
```

**Loading (with .NET Framework 4.8):**
```csharp
var config = new ConfigurationBuilder()
    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
    .AddJsonFile("config.json", optional: false, reloadOnChange: false)  // reloadOnChange=true for Phase 3
    .Build();
```

**Important for .NET Framework 4.8:**
- `reloadOnChange: true` uses `FileSystemWatcher` internally. This works on .NET Framework 4.8 via `Microsoft.Extensions.Configuration.Json` package, but is deferred to Phase 3.
- For Phase 1, use `reloadOnChange: false`. The config is read once at startup.
- Use `IConfiguration.GetSection(...).Get<AgentConfig>()` or bind manually. .NET Framework 4.8 doesn't have `Get<T>()` built in the abstractions — you need `Microsoft.Extensions.Configuration.Binder` NuGet package for that.
- Alternative: Parse JSON with `Newtonsoft.Json` (not recommended for new work, but most .NET Framework projects already have it).

**If config file is missing or invalid:**
```csharp
try
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
        .AddJsonFile("config.json", optional: false, reloadOnChange: false)
        .Build();
}
catch (FileNotFoundException ex)
{
    EventLog.WriteEntry("FingerprintAgent",
        $"FATAL: config.json not found at {AppDomain.CurrentDomain.BaseDirectory}. {ex.Message}",
        EventLogEntryType.Error);
    throw; // Service won't start
}
catch (FormatException ex) // Invalid JSON
{
    EventLog.WriteEntry("FingerprintAgent",
        $"FATAL: config.json is not valid JSON. {ex.Message}",
        EventLogEntryType.Error);
    throw;
}
```

### 2.7 CORS in Raw HttpListener

Since the project uses raw `HttpListener` (no ASP.NET middleware), CORS must be implemented manually.

**Algorithm in the request handler:**
```
For every request:
  1. Read Origin header
  2. If no Origin header → skip CORS (not a cross-origin request)
  3. If OPTIONS request → respond to preflight:
     - Set Access-Control-Allow-Origin (based on mode)
     - Set Access-Control-Allow-Methods: POST, GET, OPTIONS
     - Set Access-Control-Allow-Headers: Content-Type
     - Set Access-Control-Max-Age: 86400
     - Return 204 No Content
  4. For actual requests (GET/POST):
     - Set Access-Control-Allow-Origin header in response
     - If mode = allowlist AND origin not in allowedOrigins → return 403 Forbidden
```

**Code sketch:**
```csharp
public class CorsMiddleware
{
    private readonly string _mode;
    private readonly HashSet<string> _allowedOrigins;

    public CorsMiddleware(string mode, string[] allowedOrigins)
    {
        _mode = mode;
        _allowedOrigins = new HashSet<string>(allowedOrigins ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool HandleCorsPreflight(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "OPTIONS") return false;

        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return false;

        ApplyCorsHeaders(response, origin);

        if (_mode == "allowlist" && !_allowedOrigins.Contains(origin))
        {
            response.StatusCode = 403;
            response.Close();
            return true;
        }

        response.StatusCode = 204;
        response.Close();
        return true;
    }

    public void ApplyCorsHeaders(HttpListenerResponse response, string origin)
    {
        if (string.IsNullOrEmpty(origin)) return;

        if (_mode == "wildcard")
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
        }
        else if (_mode == "allowlist" && _allowedOrigins.Contains(origin))
        {
            response.Headers.Add("Access-Control-Allow-Origin", origin);
            response.Headers.Add("Vary", "Origin");
        }

        if (origin != "*") // Not a preflight-only header
        {
            response.Headers.Add("Access-Control-Expose-Headers", "Content-Type");
        }
    }
}
```

### 2.8 File Logging to C:\ProgramData\FingerprintAgent\Logs\agent.log

**Using System.Diagnostics for file logging:**

```csharp
public class FileLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new object();
    private readonly long _maxSizeBytes;

    public FileLogger(string filePath, int maxSizeMb = 10, int maxFiles = 5)
    {
        var dir = Path.GetDirectoryName(filePath);
        Directory.CreateDirectory(dir);  // Ensure directory exists

        _maxSizeBytes = maxSizeMb * 1024L * 1024L;
        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream);
    }

    public void Log(LogLevel level, string correlationId, string message)
    {
        var entry = $"{DateTime.UtcNow:O} [{level}] [{correlationId}] {message}";
        lock (_lock)
        {
            _writer.WriteLine(entry);
            _writer.Flush();
        }
    }

    // File rotation — deferred to Phase 3 or kept simple
}
```

**Structured log format:** `2026-07-28T10:30:00.0000000Z [INFO] [correlation-id] Service started successfully`

**Log levels:** `DEBUG`, `INFO`, `WARN`, `ERROR` (mapped from `TraceLevel`).

**Log directory:** Creation is handled by `Install-Service.ps1`. Service also creates it on startup as fallback.

### 2.9 EventLog Source and Writing

**Critical insight:** `EventLog.CreateEventSource()` requires **administrator privileges** and only needs to be called **once** per source name. After creation, `EventLog.WriteEntry()` with the existing source name works from any account.

**Pattern for Phase 1:**
```csharp
public static void EnsureEventLogSource(string sourceName)
{
    try
    {
        if (!EventLog.SourceExists(sourceName))
        {
            // This fails at runtime if not admin
            // Best handled in Install-Service.ps1
            EventLog.CreateEventSource(sourceName, "Application");
        }
    }
    catch (System.Security.SecurityException)
    {
        // Non-admin, source doesn't exist yet — log to Application source as fallback
        EventLog.WriteEntry("Application", $"Event source '{sourceName}' not registered. Run Install-Service.ps1 as Administrator.",
            EventLogEntryType.Warning);
    }
}

public static void WriteEventLog(string source, string message, EventLogEntryType type)
{
    try
    {
        EventLog.WriteEntry(source, message, type);
    }
    catch (Exception ex)
    {
        // Last-resort fallback
        try { EventLog.WriteEntry("Application", $"Failed to write to {source}: {ex.Message}", EventLogEntryType.Error); } catch { }
    }
}
```

**Recommendation:** Create the event source during `Install-Service.ps1` (admin context) rather than trying to do it at runtime. The service startup code should only call `EventLog.WriteEntry()` using the pre-created source.

```
In Install-Service.ps1:
if (-not [System.Diagnostics.EventLog]::SourceExists("FingerprintAgent")) {
    [System.Diagnostics.EventLog]::CreateEventSource("FingerprintAgent", "Application")
}
```

**Note:** When running as LocalSystem, the service COULD create the source at runtime (since LocalSystem is effectively admin). However, it's cleaner to have the install script do it. The source name should match the service name (`FingerprintAgent`).

### 2.10 Test/Verification Commands

| Scenario | Command |
|----------|---------|
| Health check | `Invoke-RestMethod -Uri http://localhost:5043/health -Method Get` |
| Capture (full) | `Invoke-RestMethod -Uri http://localhost:5043/api/capture -Method Post -Body '{"thamChieuId":"t1","maPhieu":"P1"}' -ContentType "application/json"` |
| Capture (save image) | See `Test-Capture.ps1` in scripts/ |
| CORS preflight | `Invoke-WebRequest -Uri http://localhost:5043/api/capture -Method Options -Headers @{Origin="http://example.com"}` |
| Check listener | `netstat -an | findstr 5043` |
| Service status | `Get-Service FingerprintAgent` |
| Event log | `Get-EventLog -LogName Application -Source FingerprintAgent -Newest 10` |
| Log file | `Get-Content "C:\ProgramData\FingerprintAgent\Logs\agent.log" -Tail 20` |

---

## 3. Known Pitfalls and How to Avoid Them

### 3.1 HttpListenerException (0x5) / Access Denied / 503 on Start

**Cause:** URL prefix not registered in HTTP.sys ACL.

**Fix for 127.0.0.1:** No action needed — loopback is exempt. If it still fails, check that no other process is listening on port 5043:
```powershell
netstat -ano | findstr :5043
```

**Fix for non-loopback IPs:** Run once as admin:
```powershell
netsh http add urlacl url=http://+:5043/ user=NT AUTHORITY\LOCAL SERVICE
```

### 3.2 OnStart Timeout (Service Fails to Start)

**Cause:** OnStart does I/O (config loading, adapter init) synchronously and takes > 30 seconds.

**Fix:** Move all blocking work to a background thread. OnStart should only create the `CancellationTokenSource`, start the worker thread, and return. If config loading fails, communicate the failure through a shared state variable and shut down gracefully.

### 3.3 GDI+ "Object is in use elsewhere" or Random Failures in Service

**Cause:** GDI+ objects are not thread-safe. When `HttpListener` processes requests concurrently, sharing `Pen`, `Brush`, `Font` across threads causes errors.

**Fix:**
- Create GDI+ objects per-request (inside the handler method).
- Always dispose (`using` pattern).
- Do NOT cache `Pen`, `Brush`, `Font` as static fields.

### 3.4 EventLog Source Creation Fails at Runtime

**Cause:** `EventLog.CreateEventSource()` requires admin. Service runs as LocalSystem which IS admin, but checking `SourceExists` can also fail due to Security event log being inaccessible.

**Fix:** Create the source in `Install-Service.ps1` (elevated). At service runtime, only call `EventLog.WriteEntry()` — never `CreateEventSource()`.

### 3.5 FileLog Directory Not Created

**Cause:** `C:\ProgramData\FingerprintAgent\Logs\` doesn't exist.

**Fix:** Service creates the directory on startup (`Directory.CreateDirectory`). Also ensure `Install-Service.ps1` creates it.

### 3.6 HttpListener.GetContext() Blocks Forever After OnStop

**Cause:** `HttpListener.Stop()` is called but there's a thread blocked on `GetContext()`.

**Fix:** Call `listener.Stop()` first, which causes pending `GetContext()` to throw `ObjectDisposedException`. The worker thread should catch this as the stop signal.

```csharp
protected override void OnStop()
{
    _listener?.Stop();  // This unblocks GetContext()
    _cts?.Cancel();
    _workerThread?.Join(TimeSpan.FromSeconds(10));
}
```

### 3.7 Port Conflict During Development

**Cause:** Service is installed and running, but developer tries to run console version on same port.

**Fix:** Check port availability before binding. Or use a different port for development (configurable). The `Test-Capture.ps1` script should use the configured port.

### 3.8 JSON Deserialization with .NET Framework 4.8

**Cause:** `Microsoft.Extensions.Configuration.Json` uses `System.Text.Json` internally (in newer versions via netstandard2.0 polyfills), but the API shape differs from `Newtonsoft.Json`.

**Fix:** Use `IConfiguration.GetSection(...).Value` or `IConfiguration.Bind(...)` for config binding. For request body parsing, use `Newtonsoft.Json` (already referenced by most .NET Framework projects, or add via NuGet). Phase 1 request JSON parsing can use simple `Newtonsoft.Json.Linq.JObject.Parse()`.

### 3.9 Service Not Starting After Install — No Error Message

**Cause:** The service executable path is wrong, or dependencies are missing.

**Fix:** The install script should verify the executable exists at the target path. Always check `Get-Service` and `Get-EventLog` after install. A good practice: run the executable as console (`--console` flag) first to verify it starts, before installing as service.

### 3.10 HttpListener Requires Trailing Slash in Prefix

**Cause:** Missing trailing slash in `listener.Prefixes.Add("http://127.0.0.1:5043")`.

**Fix:** Always include trailing slash: `"http://127.0.0.1:5043/"`. Without it, the prefix is invalid and `Start()` throws.

---

## 4. NuGet Package Version Recommendations

All packages target .NET Standard 2.0, making them compatible with .NET Framework 4.8 (which implements .NET Standard 2.0).

### Core Packages

| Package | Recommended Version | Notes |
|---------|-------------------|-------|
| `Microsoft.Extensions.DependencyInjection` | **8.0.1** | v8 is the most widely tested with .NET Framework 4.x. v9+ also works but pulls more dependencies. |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | **8.0.2** | Match core version. |
| `Microsoft.Extensions.Configuration` | **8.0.0** | Configuration base. |
| `Microsoft.Extensions.Configuration.Json` | **8.0.0** | JSON file provider. |
| `Microsoft.Extensions.Configuration.Binder` | **8.0.2** | `IConfiguration.Get<T>()` binding. |
| `Microsoft.Extensions.Options` | **8.0.2** | `IOptions<T>` pattern. |
| `Microsoft.Extensions.Primitives` | **8.0.0** | Required by Configuration. |

### Transitive Dependencies (auto-included)

| Package | Min Version Required | Notes |
|---------|---------------------|-------|
| `Microsoft.Bcl.AsyncInterfaces` | 8.0.0 | Polyfill for .NET Framework (IAsyncDisposable etc.) |
| `System.Threading.Tasks.Extensions` | 4.5.4 | Part of .NET Framework 4.8 already, but newer version for netstandard2.0 compat. |
| `System.Text.Encodings.Web` | 8.0.0 | Transitive from Configuration.Json. |
| `System.Text.Json` | 8.0.5 | Transitive from Configuration.Json. |

### Optional

| Package | Recommended Version | Notes |
|---------|-------------------|-------|
| `Newtonsoft.Json` | 13.0.3 | For request/response JSON serialization (if not using built-in). Most .NET Framework projects already have this. |

### Version Compatibility Matrix

```
Microsoft.Extensions.DependencyInjection
  ├── 6.0.1 → targets net462, netstandard2.0 ✓ SAFE
  ├── 7.0.0 → targets net462, netstandard2.0 ✓ SAFE
  ├── 8.0.1 → targets net462, netstandard2.0 ✓ SAFE (RECOMMENDED)
  ├── 9.0.x → targets net462, netstandard2.0 ✓ SAFE
  └── 10.x  → targets net462, netstandard2.0 ✓ SAFE (but heavy)

Microsoft.Extensions.Configuration.Json
  ├── 6.0.1 → targets net462, netstandard2.0 ✓ SAFE
  ├── 7.0.0 → targets net462, netstandard2.0 ✓ SAFE
  ├── 8.0.0 → targets net462, netstandard2.0 ✓ SAFE (RECOMMENDED)
  ├── 9.0.x → targets net462, netstandard2.0 ✓ SAFE
  └── 10.x  → targets net462, netstandard2.0 ✓ SAFE
```

**Recommendation:** Use **8.0.x** for all packages. It's the most recent version with broad .NET Framework 4.8 testing. Version 9+ may use `System.Text.Json` features not fully tested on .NET Framework. Version 6.x is also safe but older.

### PackageReference Format in .csproj

```xml
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" />
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="8.0.2" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
  <!-- System.Drawing is already in .NET Framework, no NuGet needed -->
  <!-- System.ServiceProcess is already in .NET Framework, no NuGet needed -->
</Project>
```

**Important:** For a .NET Framework 4.8 project using SDK-style format:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
```

However, the classic .csproj format may be preferred if using Visual Studio's Windows Service project template. The SDK-style format works fine too with `Microsoft.NET.Sdk` and `net48` target.

---

## 5. Additional Technical Notes

### 5.1 Walking Skeleton / MVP Strategy

Phase 1 is a **Walking Skeleton** — the thinnest possible end-to-end slice:
1. Build the project → compile
2. Run as console → verify `/health` responds
3. Install as service → verify auto-start
4. Call `/api/capture` → verify PNG + SHA-256 returned
5. Verify CORS preflight
6. Verify log files and event log entries

### 5.2 Program.cs Dual Mode (Console + Service)

```csharp
class Program
{
    static void Main(string[] args)
    {
        if (Environment.UserInteractive || args.Contains("--console"))
        {
            // Run as console app (for debugging)
            using (var service = new AgentService())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    service.Stop();
                };
                service.Start();
                Console.WriteLine("Service running. Press Ctrl+C to stop.");
                Console.ReadLine();
                service.Stop();
            }
        }
        else
        {
            // Run as Windows Service
            ServiceBase.Run(new AgentService());
        }
    }
}
```

### 5.3 Mock Scanner Implementation

The `MockScannerAdapter` simulates a connected scanner with deterministic output:
- `IsConnected` = always `true` in Phase 1
- `Scan()` returns a fixed 320x240 gray gradient PNG with `"mock-scanner-001"` deviceId
- SHA-256 hash is deterministic (same input → same hash) — use `SHA256.Create().ComputeHash(imageBytes)`
- `DeviceId` = `"mock-scanner-001"`
- `Model` = `"Mock Scanner v1.0"`

### 5.5 Request Body Parsing (for POST /api/capture)

```csharp
public class CaptureRequest
{
    public string ThamChieuId { get; set; }
    public string MaPhieu { get; set; }
    public string LoaiPhieu { get; set; }
    public string VaiKyId { get; set; }
    public string NhanLucId { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}
```

Use `Newtonsoft.Json.JsonConvert.DeserializeObject<CaptureRequest>(bodyString)` for parsing.

**Validation:** If required fields missing, return HTTP 400:
```json
{
  "isSuccess": false,
  "errorMessage": "Missing required field: thamChieuId",
  "errorCode": "INVALID_REQUEST"
}
```

### 5.6 Response DTO

```csharp
public class CaptureResponse
{
    public bool IsSuccess { get; set; }
    public string ImageBytes { get; set; }       // base64 PNG
    public string MimeType { get; set; }          // "image/png"
    public string CapturedAt { get; set; }        // ISO 8601 UTC
    public string DeviceId { get; set; }
    public string VerificationData { get; set; } // SHA-256 base64
    public string ErrorMessage { get; set; }
}
```

### 5.7 Request Dispatch in HttpServer

```csharp
private async void ProcessRequests(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await _listener.GetContextAsync();
            // Dispatch to a thread pool thread to avoid blocking
            Task.Run(() => HandleRequest(context), ct);
        }
        catch (ObjectDisposedException)
        {
            break; // Listener stopped
        }
        catch (HttpListenerException)
        {
            break;
        }
    }
}

private void HandleRequest(HttpListenerContext context)
{
    // CORS preflight check
    if (_cors.HandleCorsPreflight(context.Request, context.Response))
        return;

    // CORS headers for actual request
    var origin = context.Request.Headers["Origin"];
    _cors.ApplyCorsHeaders(context.Response, origin);

    // Route
    var path = context.Request.Url.AbsolutePath.TrimEnd('/');
    var method = context.Request.HttpMethod;

    if (path == "/health" && method == "GET")
        _healthHandler.Handle(context);
    else if (path == "/api/capture" && method == "POST")
        _captureHandler.Handle(context);
    else
    {
        context.Response.StatusCode = 404;
        context.Response.Close();
    }
}
```

---

## RESEARCH COMPLETE
