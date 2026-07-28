# FingerprintAgent - System Architecture

## 1. High-Level Architecture

### System Context

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Healthcare Worker PC                                  │
│  ┌─────────────────┐      ┌──────────────────────────────────────────┐    │
│  │  Fingerprint    │      │           FingerprintAgent Service        │    │
│  │  Scanner        │◄────►│  ┌─────────────┐  ┌─────────────────┐   │    │
│  │  (SecuGen /     │ USB   │  │  HTTP API   │  │  Scanner        │   │    │
│  │   Digital       │      │  │  (Kestrel)  │  │  Manager         │   │    │
│  │   Persona /     │      │  │  :5043      │  │                 │   │    │
│  │   Futronic)     │      │  └──────┬──────┘  └────────┬────────┘   │    │
│  └─────────────────┘      │         │                  │              │    │
│                           │         │ POST /api/capture │              │    │
│                           │         │                  │              │    │
│                           │  ┌──────▼──────────────────────▼────────┐   │    │
│                           │  │         Adapter Layer                │   │    │
│                           │  │  ┌──────────┐ ┌────────────┐        │   │    │
│                           │  │  │ SecuGen  │ │ Digital    │ ...    │   │    │
│                           │  │  │ Adapter  │ │ Persona    │        │   │    │
│                           │  │  └──────────┘ └────────────┘        │   │    │
│                           │  └───────────────────────────────────────┘   │    │
│                           └──────────────────────────────────────────────┘    │
│                                        │                                       │
│                                   HTTP POST                                    │
│                                        │                                       │
│                                        ▼                                       │
│                           ┌──────────────────────────┐                       │
│                           │     KCB Backend API      │                       │
│                           │  (CaptureESign Handler)  │                       │
│                           └──────────────────────────┘                       │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Component Overview

| Component | Responsibility | Technology |
|-----------|----------------|------------|
| **Windows Service Host** | Service lifecycle, auto-start, event logging | .NET Windows Service (TopShelf or raw ServiceBase) |
| **HTTP API Layer** | Expose capture endpoint, handle requests, route responses | Kestrel (embedded) + OWIN |
| **Scanner Manager** | Track adapters, select active scanner, manage connection state | `ScannerManager` class |
| **Scanner Adapter (base)** | Define contract for vendor-specific adapters | `IScannerAdapter` interface |
| **Vendor Adapters** | Wrap SDK calls, handle brand-specific capture logic | SecuGenAdapter, DigitalPersonaAdapter, FutronicAdapter |
| **Configuration Manager** | Load, watch, and reload config.json | `ConfigurationProvider` |
| **Protocol Handlers** | Mode A (server) vs Mode B (polling) | `ServerProtocolHandler`, `PollingProtocolHandler` |

---

## 2. Scanner Abstraction Layer

### Interface Definition

```csharp
namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Base interface for all fingerprint scanner adapters.
    /// Each vendor (SecuGen, Digital Persona, Futronic) implements this interface.
    /// </summary>
    public interface IScannerAdapter : IDisposable
    {
        /// <summary>SDK-provided human-readable device name</summary>
        string DeviceName { get; }

        /// <summary>Unique identifier for this device, e.g., "secugen-hamster-001"</summary>
        string DeviceId { get; }

        /// <summary>Whether this adapter is currently connected and ready</summary>
        bool IsConnected { get; }

        /// <summary>Initialize the scanner (open USB handle, load calibration data)</summary>
        Task<bool> InitializeAsync(CancellationToken ct = default);

        /// <summary>Check if the scanner is still connected by polling hardware</summary>
        Task<bool> IsConnectedAsync(CancellationToken ct = default);

        /// <summary>
        /// Capture a fingerprint and return raw PNG bytes.
        /// Blocks until fingerprint is captured or timeout.
        /// </summary>
        Task<byte[]> CaptureAsync(int timeoutMs = 30000, CancellationToken ct = default);

        /// <summary>Release hardware resources</summary>
        void Disconnect();
    }

    /// <summary>
    /// Marker interface for adapters that support firmware version queries
    /// </summary>
    public interface IFirmwareInfo
    {
        Task<string> GetFirmwareVersionAsync(CancellationToken ct = default);
    }
}
```

### Adapter Resolution

The `ScannerManager` discovers and registers adapters based on `config.json`:

```csharp
public class ScannerManager
{
    private readonly List<IScannerAdapter> _adapters = new();
    private readonly ScannerConfig _config;

    public async Task InitializeAsync(CancellationToken ct)
    {
        foreach (var scannerEntry in _config.Scanners)
        {
            var adapter = AdapterFactory.Create(scannerEntry.AdapterType);
            await adapter.InitializeAsync(ct);
            _adapters.Add(adapter);
        }
    }

    /// <summary>
    /// Select best available scanner: prefer primary if connected,
    /// otherwise fall back to first connected adapter
    /// </summary>
    public IScannerAdapter SelectActiveScanner()
    {
        var primary = _adapters.FirstOrDefault(a => a.IsConnected && IsPrimary(a.DeviceId));
        return primary ?? _adapters.FirstOrDefault(a => a.IsConnected);
    }
}
```

### Vendor Adapter Skeleton

```csharp
/// <summary>
/// SecuGen HAMSTER adapter using SecuGen FDx SDK Pro
/// </summary>
public class SecuGenAdapter : IScannerAdapter
{
    private IntPtr _deviceHandle = IntPtr.Zero;
    private readonly SecuGenSettings _settings;

    public string DeviceName => "SecuGen HAMSTER Pro";
    public string DeviceId { get; private set; }
    public bool IsConnected => _deviceHandle != IntPtr.Zero;

    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = SGFingerLib.Init();
            _deviceHandle = SGFingerLib.OpenDevice(0);
            return _deviceHandle != IntPtr.Zero;
        }, ct);
    }

    public async Task<byte[]> CaptureAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var template = SGFingerLib.GetImage(_deviceHandle);
            var png = ImageEncoder.EncodePng(template);
            return png;
        }, ct);
    }

    public void Disconnect()
    {
        if (_deviceHandle != IntPtr.Zero)
        {
            SGFingerLib.CloseDevice(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
        }
    }
}
```

---

## 3. Communication with Backend

### Mode A: Agent as HTTP Server (Default)

The agent hosts a Kestrel HTTP server on `localhost:5043`. The KCB Backend makes HTTP POST requests to the agent when a capture is needed.

#### Endpoint Specification

```
POST http://localhost:5043/api/capture
Content-Type: application/json
Accept: application/json

X-Request-Id: <UUID>          (optional, for tracing)
X-Correlation-Id: <UUID>       (optional)
```

**Request Schema (CaptureRequest)**
```csharp
public class CaptureRequest
{
    public long ThamChieuId { get; set; }
    public string MaPhieu { get; set; }        // e.g., "PK001"
    public long VaiKyId { get; set; }
    public long? NhanLucId { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}
```

**Success Response (200 OK)**
- Content-Type: `application/json`
- Body: JSON object
  ```json
  {
    "isSuccess": true,
    "imageBytes": "base64-encoded PNG bytes",
    "mimeType": "image/png",
    "capturedAt": "2026-07-28T10:30:00Z",
    "deviceId": "secugen-hamster-001",
    "verificationData": "SHA256-hash",
    "errorMessage": null
  }
  ```
- Headers:
  - `X-Request-Id`: Echoed from request (optional)

**Error Response (400/500)**
```json
{
  "isSuccess": false,
  "errorMessage": "Device not connected",
  "deviceId": null,
  "capturedAt": null
}
```

#### Sequence Diagram (Mode A)

```
KCB Backend              Agent HTTP API           Scanner Manager         Adapter
    │                         │                          │                    │
    │──POST /api/capture──────►                          │                    │
    │                         │──SelectActiveScanner()───►                    │
    │                         │                          │──IsConnected()────►│
    │                         │                          │◄──true─────────────│
    │                         │                          │──CaptureAsync()────►│
    │                         │                          │    (blocks until    │
    │                         │                          │     finger placed)  │
    │                         │                          │◄──PNG bytes────────│
    │                         │◄──PNG bytes─────────────│                    │
    │                         │                          │                    │
    │──200 OK (JSON body)─────►                          │                    │
    │   X-Fingerprint-Hash:   │                          │                    │
    │   X-Device-Id:          │                          │                    │
    │   X-Captured-At:        │                          │                    │
```

### Mode B: Agent Polling Backend Queue

If the backend cannot initiate HTTP calls to the agent, Mode B uses polling:

```
Agent                          KCB Backend
  │                                  │
  │──GET /api/capture-queue/pending──►
  │◄──{ captureRequestId, ... }──────│
  │                                  │
  │──Capture (local scanner)─────────│
  │                                  │
  │──POST /api/capture-queue/         │
  │    complete/{captureRequestId}──►
  │◄──200 OK──────────────────────────│
```

**Configuration**
```json
{
  "mode": "polling",
  "backend": {
    "baseUrl": "http://kcb-api.internal:5000",
    "pollIntervalSeconds": 5,
    "timeoutSeconds": 30
  }
}
```

---

## 4. Security Considerations

### Network Binding
- HTTP server binds **exclusively to 127.0.0.1** (localhost). No wildcard binding.
- `config.json` option `security.allowRemoteHosts: false` enforces this
- If remote access is required (not typical), only explicit IP whitelisting is allowed

### Transport Security
- When communicating with backend in Mode B, TLS 1.2 minimum is enforced
- Self-signed certificates: configurable to skip validation for internal dev/test environments only
- Production: backend certificate must be valid and trusted

### Local Access Control
- Any user logged into the PC can call `localhost:5043`
- Trust boundary is PC access - service PC should be a controlled/managed device
- No per-user authentication in the agent itself (handled at backend)

### Data Handling
- No fingerprint images written to disk (in-memory only, returned directly)
- No template storage (only raw image bytes transit through agent)
- SHA-256 hash computed in-memory, not stored
- No logging of biometric data

### Attack Surface
| Vector | Mitigation |
|--------|------------|
| Remote code execution via HTTP | Localhost-only binding prevents remote attacks |
| Denial of service (large requests) | Max request body size: 1 MB |
| Scanner malware injection | Adapters loaded from known paths only |
| Configuration injection | JSON config parsed with schema validation |

---

## 5. Configuration Schema

### File Location
- Default: `C:\ProgramData\FingerprintAgent\config.json`
- Per-install override: same directory as `FingerprintAgent.exe`

### Schema

```json
{
  "$schema": "fingerprint-agent-config-v1",
  "service": {
    "displayName": "FingerprintAgent",
    "description": "Local fingerprint capture service for KCB e-sign",
    "autoStart": true
  },
  "http": {
    "host": "127.0.0.1",
    "port": 5043,
    "requireLocalhost": true,
    "maxRequestBodyBytes": 1048576,
    "requestTimeoutSeconds": 60
  },
  "backend": {
    "baseUrl": "http://kcb-api.internal:5000",
    "pollIntervalSeconds": 5,
    "timeoutSeconds": 30,
    "apiKey": "",
    "validateCertificate": true
  },
  "mode": "server",
  "scanners": [
    {
      "adapterType": "SecuGenAdapter",
      "deviceId": "secugen-hamster-001",
      "isPrimary": true,
      "autoConnect": true,
      "settings": {
        "timeoutMs": 30000,
        "sensitivity": 5,
        "retryCount": 3
      }
    }
  ],
  "logging": {
    "level": "Information",
    "eventLog": true,
    "eventLogSource": "FingerprintAgent",
    "filePath": "C:\\ProgramData\\FingerprintAgent\\Logs\\agent.log",
    "maxFileSizeMb": 10,
    "retainFiles": 5
  },
  "security": {
    "allowRemoteHosts": false,
    "minTlsVersion": "TLS1.2",
    "skipBackendCertValidation": false
  }
}
```

### Configuration Watching
The service watches `config.json` for changes and reloads dynamically:
- Scanner list changes → reconnect adapters
- HTTP port changes → require service restart (logged warning)
- Log level changes → apply immediately

---

## 6. Error Handling

### Error Categories

| Category | Condition | Behavior |
|----------|-----------|----------|
| **Device Disconnected** | `IsConnectedAsync()` returns false | Log warning, attempt reconnect every 10s, return 503 |
| **Capture Timeout** | No fingerprint placed within timeout | Return 408 with clear message, device stays connected |
| **SDK Error** | Vendor SDK returns error code | Log error details, attempt recovery, return 500 |
| **Invalid Request** | Malformed JSON or missing fields | Return 400 with validation errors |
| **No Scanner Available** | No adapter connected | Return 503 "No scanner available" |
| **PNG Encoding Failure** | Image data cannot be encoded | Return 500 "Capture processing failed" |

### Error Response Format

```csharp
public class CaptureErrorResponse
{
    public bool IsSuccess { get; set; } = false;
    public string ErrorMessage { get; set; }        // Human-readable
    public string ErrorCode { get; set; }           // e.g., "DEVICE_DISCONNECTED"
    public string DeviceId { get; set; }             // null when no device
    public DateTime? CapturedAt { get; set; }       // null
}
```

### Connection Recovery

```
Device Disconnected
       │
       ▼
┌──────────────────┐
│ Log ERROR + Event│
│ (Windows Event   │
│  Log Warning)    │
└────────┬─────────┘
         │
         ▼
┌──────────────────┐
│ Retry schedule:  │
│ 0s, 10s, 30s,   │
│ 60s, 120s       │
│ (exponential     │
│  backoff)        │
└────────┬─────────┘
         │
         ▼
┌──────────────────┐
│ Adapter          │
│ InitializeAsync()│
└────────┬─────────┘
         │
    ┌────┴────┐
    │ success │
    ▼         ▼
  Log INFO  Log ERROR
  "Ready"   retry again
```

### Panic Recovery
- Unhandled exceptions are caught at the HTTP handler level
- Service **never crashes** due to a capture failure
- After 5 consecutive failures, service logs critical error and continues serving (marking device as unavailable)

---

## 7. Component Projects

```
FingerprintAgent/
├── FingerprintAgent.sln
│
├── src/
│   ├── FingerprintAgent.Core/
│   │   ├── ScannerManager.cs
│   │   ├── IScannerAdapter.cs
│   │   ├── ConfigurationProvider.cs
│   │   ├── CaptureRequest.cs
│   │   ├── CaptureResponse.cs
│   │   └── HashComputer.cs                   # SHA-256 computation
│   │
│   ├── FingerprintAgent.Adapters/
│   │   ├── FingerprintAgent.Adapters.csproj
│   │   ├── SecuGenAdapter.cs
│   │   ├── DigitalPersonaAdapter.cs
│   │   ├── FutronicAdapter.cs
│   │   └── AdapterFactory.cs
│   │
│   ├── FingerprintAgent.Api/
│   │   ├── Program.cs                         # Kestrel setup
│   │   ├── CaptureController.cs               # /api/capture endpoint
│   │   ├── HealthController.cs                # /health endpoint
│   │   ├── ProtocolHandlers/
│   │   │   ├── ServerProtocolHandler.cs      # Mode A: receive HTTP
│   │   │   └── PollingProtocolHandler.cs     # Mode B: poll backend
│   │   └── Middleware/
│   │       ├── RequestLoggingMiddleware.cs
│   │       └── ErrorHandlingMiddleware.cs
│   │
│   └── FingerprintAgent.WindowsService/
│       ├── Program.cs                        # Service entry point
│       ├── FingerprintAgentService.cs        # ServiceBase subclass
│       └── EventLogWriter.cs                 # Windows Event Log sink
│
├── tests/
│   ├── FingerprintAgent.Core.Tests/
│   │   ├── ScannerManagerTests.cs
│   │   ├── HashComputerTests.cs
│   │   └── ConfigurationProviderTests.cs
│   │
│   └── FingerprintAgent.Adapter.Tests/
│       ├── MockScannerAdapter.cs            # Test double
│       └── CaptureFlowTests.cs
│
├── adapters/                                 # Third-party adapter DLLs
│   ├── SecuGenAdapter.dll
│   └── FutronicAdapter.dll
│
├── Scripts/
│   ├── Install-Service.ps1
│   ├── Uninstall-Service.ps1
│   └── Test-Capture.ps1
│
└── Documentation/
    ├── SCANNER_SETUP.md
    └── TROUBLESHOOTING.md
```

---

## 8. Data Flow

### Full Capture Flow (Mode A)

```
1. [KCB Backend] POST http://localhost:5043/api/capture
   Content-Type: application/json

2. [CaptureController]
   - Deserialize CaptureRequest
   - Validate required fields (thamChieuId, vaiKyId)
   - Forward to ServerProtocolHandler

3. [ServerProtocolHandler]
   - Select scanner via ScannerManager.SelectActiveScanner()
   - If none available → return 503

4. [Scanner Manager] → [Selected Adapter].CaptureAsync()
   - Call vendor SDK: SG_FingerPrint_GetImage() / equivalent
   - SDK returns raw grayscale bitmap template data
   - Convert to PNG in-memory (System.Drawing or SkiaSharp)

5. [HashComputer]
   - Compute SHA-256 over raw PNG bytes
   - Base64 encode the hash

6. [CaptureController] Build JSON response
   - Set Content-Type: application/json
   - Build JSON object with base64-encoded image and metadata
   - Return: isSuccess, imageBytes, mimeType, capturedAt, deviceId, verificationData, errorMessage

7. [KCB Backend] CaptureESignRequestHandler
   - Receives PNG bytes + hash
   - Forwards to FingerprintSignatureProvider
   - Calls _eSignProvider.CaptureAsync() which returns SignatureProviderResult
   - Workflow continues (digital signature creation)
```

### Image Encoding
- All adapters return raw PNG bytes (not BMP, not JPEG, not template)
- PNG encoding is done inside the adapter (or a shared `ImageEncoder` utility)
- Grayscale, 500 DPI preferred, but adapter may produce what the sensor provides

---

## 9. Service Lifecycle

### Startup Sequence
```
1. Windows starts FingerprintAgent service
2. Load config.json (throw fatal if missing/malformed)
3. Initialize logging (file + Event Log)
4. Register adapters from config
5. For each adapter: InitializeAsync()
   - If all fail → start anyway in "degraded mode" (HTTP API up, no scanner)
6. Start Kestrel HTTP server on localhost:5043
7. If Mode B: start background polling task
8. Log "FingerprintAgent started successfully"
```

### Shutdown Sequence
```
1. Windows sends STOP signal
2. Stop Kestrel HTTP server (drain active requests with 5s timeout)
3. If Mode B: cancel polling task
4. For each adapter: Disconnect() + Dispose()
5. Flush logs
6. Log "FingerprintAgent stopped"
```

### Idle Behavior
- No CPU usage when no requests pending
- HTTP listener remains active, waiting for connections
- Adapters poll connection status every 30 seconds in background

---

## 10. Testing Strategy

### Unit Tests
- `HashComputer`: Verify SHA-256 matches expected value for known PNG
- `ScannerManager`: Verify primary selection logic, fallback behavior
- `ConfigurationProvider`: Verify JSON parsing and defaults
- `CaptureController`: Verify request validation, error mapping

### Integration Tests
- Full capture flow with `MockScannerAdapter` (simulates hardware)
- End-to-end HTTP round-trip: POST /api/capture → PNG bytes returned
- Config reload: modify config.json → verify reconfiguration

### Hardware Tests (Manual)
- Connect SecuGen HAMSTER Pro → verify adapter returns valid PNG
- Disconnect scanner mid-capture → verify 503 + recovery
- Capture with finger not placed → verify timeout handling

---

## 11. Deployment Notes

### Windows 7 Compatibility
- .NET Framework 4.8 (not .NET Core/5+)
- Use `Topshelf` library for simplified Windows Service hosting
- Windows Event Log source registration requires admin on first install
- No `System.Drawing.Common` (Windows-only GDI), use `SkiaSharp` or `ImageSharp` for PNG encoding

### Permissions
- Service runs as `LocalSystem` by default (recommended)
- USB device access requires appropriate driver installed
- Log directory `C:\ProgramData\FingerprintAgent\Logs\` must be writable by service

### Installation
```powershell
# Install service
.\Install-Service.ps1

# Verify
Get-Service FingerprintAgent | Select Name, Status, StartType

# View logs
Get-EventLog -Source FingerprintAgent -LogName Application -Newest 50
```

---

## 12. Dependencies

### NuGet Packages (Core)
| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Owin.Host.Kestrel` | 4.2+ | OWIN integration with Kestrel |
| `Newtonsoft.Json` | 13.0+ | JSON parsing |
| `SkiaSharp` | 2.88+ | PNG encoding (cross-platform) |
| `Topshelf` | 4.3+ | Windows Service hosting |
| `Microsoft.Extensions.Logging` | 8.0+ | Logging abstractions |
| `Serilog.Sinks.File` | 5.0+ | File logging |

### Third-Party SDKs (Bundled)
- **SecuGen**: `SecuGen FDx SDK Pro` (includes `SecuGen.FDxSDKPro.Windows.dll`)
- **Digital Persona**: `U.are.U SDK` (includes `DPFP*.dll`)
- **Futronic**: `Futronic SDK` (includes `FtrScanAPI.dll`)

> Note: SDK DLLs are placed in the output directory as content/copy-local items.