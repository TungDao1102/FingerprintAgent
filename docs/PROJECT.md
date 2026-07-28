# FingerprintAgent - Project Overview

## 1. Project Name

**FingerprintAgent** - Local Fingerprint Capture Service

## 2. Purpose

FingerprintAgent is a **local Windows service** that bridges fingerprint scanner hardware with a remote KCB Backend API. It runs as a background service on healthcare workers' PCs, manages connected fingerprint scanners, captures fingerprint images on demand, and delivers raw PNG image bytes to the backend via HTTP POST.

The agent operates **independently** of any calling application. Any system that can make an HTTP request can trigger a capture by calling the agent's local endpoint.

## 3. Target Platform

| Component | Specification |
|-----------|---------------|
| **OS** | Windows 7 Ultimate (32-bit), Windows 10, Windows 11 |
| **Runtime** | .NET Framework 4.8 (full Windows compatibility) |
| **Deployment** | Standalone Windows Service (no installer dependency) |
| **Minimum RAM** | 512 MB |
| **Disk Space** | 100 MB |
| **Architecture** | 32-bit and 64-bit builds |

## 4. Supported Fingerprint Scanners

### Primary Support
| Brand | Model Examples | SDK Type |
|-------|----------------|----------|
| **SecuGen** | HAMSTER Pro, HAMSTER Pro 20, HAMSTER IV | SecuGen FDx SDK Pro |
| **Digital Persona** | U.are.U 4500, 4600, 5160 | Digital Persona SDK |
| **Futronic** | MSO 1300, MSO 1350, MSO 1500 | Futronic SDK |

### Adapter Architecture
Each scanner brand has a dedicated **Scanner Adapter** implementing the `IScannerAdapter` interface. Adapters are registered at startup via configuration.

### Scanner Adapter Interface
```csharp
public interface IScannerAdapter : IDisposable
{
    /// <summary>Human-readable device name</summary>
    string DeviceName { get; }

    /// <summary>Unique device identifier (e.g., "secugen-hamster-001")</summary>
    string DeviceId { get; }

    /// <summary>Whether this adapter is currently connected and ready</summary>
    bool IsConnected { get; }

    /// <summary>Initialize the scanner (open handle, load calibration)</summary>
    Task<bool> InitializeAsync(CancellationToken ct = default);

    /// <summary>Poll hardware to check connection status</summary>
    Task<bool> IsConnectedAsync(CancellationToken ct = default);

    /// <summary>Capture fingerprint and return raw PNG bytes</summary>
    Task<byte[]> CaptureAsync(int timeoutMs = 30000, CancellationToken ct = default);

    /// <summary>Release hardware resources</summary>
    void Disconnect();
}
```

## 5. Communication Protocol with Backend

### Topology
```
[Fingerprint Scanner] ←USB→ [FingerprintAgent Service] ←HTTP POST→ [KCB Backend API]
                                          ↑
                                   Runs locally on
                                   healthcare worker PC
```

### Mode A: Agent as HTTP Server (Primary Design)
The agent **exposes an HTTP endpoint** on localhost. The KCB Backend calls this endpoint when a capture is needed.

```
Backend → POST http://localhost:5043/api/capture
Body: { "thamChieuId": 12345, "maPhieu": "BenhAnNgoaiTru", "loaiPhieu": 1, "vaiKyId": 2, "metadata": {...} }
Response: JSON with base64-encoded PNG image
```

### Mode B: Agent Polls Backend Queue
If the backend cannot be modified to call the agent directly, the agent polls a backend endpoint for pending capture requests.

```
Agent → GET http://kcb-backend/api/capture-queue/pending
Agent ← Response: { captureRequestId, thamChieuId, maPhieu, vaiKyId, metadata }
[Capture fingerprint]
Agent → POST http://kcb-backend/api/capture-queue/complete/{captureRequestId}
Body: { imageBytes: raw PNG, deviceId, capturedAt, verificationData }
```

**Mode A is preferred** for lower latency and simpler logic.

### Backend API Contract (Mode A)
The agent's capture endpoint accepts:

```json
POST /api/capture
Content-Type: application/json

Request Body:
{
  "thamChieuId": 12345,
  "maPhieu": "BenhAnNgoaiTru",
  "loaiPhieu": 1,
  "vaiKyId": 2,
  "nhanLucId": null,
  "xmlBase64": null,
  "metadata": {
    "benhNhanId": "BN001",
    "bacSiId": "BS001"
  }
}

Success Response (200 OK):
{
  "isSuccess": true,
  "imageBytes": "base64-encoded-PNG",
  "mimeType": "image/png",
  "capturedAt": "2026-07-28T10:30:00Z",
  "deviceId": "secugen-hamster-001",
  "verificationData": "SHA256-base64-hash",
  "errorMessage": null
}

Error Response (400/500):
{
  "isSuccess": false,
  "imageBytes": null,
  "errorMessage": "Device not connected",
  "errorCode": "SCANNER_NOT_CONNECTED"
}
```

## 6. Architecture Pattern

### Windows Service + HTTP API
- **Windows Service** (`FingerprintAgent.WindowsService`): Hosts the application, handles service lifecycle (Start, Stop, Pause, Resume), logs to Windows Event Log
- **HTTP API Layer** (`FingerprintAgent.Api`):OWIN/Kestrel-based HTTP listener on `http://localhost:5043`
- **Scanner Manager** (`FingerprintAgent.Core`): Orchestrates adapters, selects active scanner
- **Protocol Handlers**: Mode A and Mode B implementations

### Process Flow
```
1. Service Starts → Load config.json → Initialize registered adapters → Start HTTP listener
2. Capture request arrives (HTTP POST or polled from backend queue)
3. Scanner Manager selects active adapter (first connected, or by deviceId preference)
4. Adapter.CaptureAsync() → returns raw PNG bytes
5. Compute SHA-256 hash over raw PNG bytes
6. Return PNG bytes via HTTP response (Mode A) or POST to backend (Mode B)
7. On device disconnect → log error → retry connection in background
```

## 7. Configuration (config.json)

```json
{
  "service": {
    "displayName": "FingerprintAgent",
    "description": "Local fingerprint capture service for KCB e-sign"
  },
  "http": {
    "host": "127.0.0.1",
    "port": 5043,
    "requireLocalhost": true
  },
  "backend": {
    "baseUrl": "http://kcb-api.internal:5000",
    "pollIntervalSeconds": 5,
    "timeoutSeconds": 30,
    "apiKey": "optional-shared-secret"
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
        "sensitivity": 5
      }
    },
    {
      "adapterType": "DigitalPersonaAdapter",
      "deviceId": "digitalpersona-001",
      "isPrimary": false,
      "autoConnect": true,
      "settings": {
        "timeoutMs": 30000
      }
    }
  ],
  "logging": {
    "level": "Information",
    "eventLog": true,
    "filePath": "C:\\ProgramData\\FingerprintAgent\\Logs\\agent.log"
  },
  "security": {
    "allowRemoteHosts": false,
    "minTlsVersion": "TLS1.2"
  }
}
```

## 8. Delivery Package Contents

```
FingerprintAgent/
├── FingerprintAgent.exe              # Main service executable
├── config.json                       # Configuration file
├── adapters/
│   ├── SecuGenAdapter.dll
│   ├── DigitalPersonaAdapter.dll
│   └── FutronicAdapter.dll
├── lib/                              # Third-party SDK DLLs
│   ├── SecuGen.FDxSDKPro.Windows.dll
│   ├── SGFinger SDK files...
├── Scripts/
│   ├── Install-Service.ps1           # PowerShell install script
│   ├── Uninstall-Service.ps1
│   └── Test-Capture.ps1              # Manual capture test
├── Documentation/
│   ├── README.md
│   └── SCANNER_SETUP.md             # Per-brand setup instructions
└── Logs/                             # Runtime logs
```

## 9. Non-Functional Requirements

### Reliability
- Service must **survive scanner disconnection** without crashing
- Background reconnection attempts every 10 seconds when scanner is disconnected
- All failures return structured error responses, never crash the service

### Performance
- Capture latency: < 3 seconds from request to response (excluding human interaction)
- Memory footprint: < 100 MB at idle
- No CPU usage when idle (waiting on HTTP request or polling interval)

### Security
- HTTP endpoint binds **only to localhost** by default
- No authentication required for local requests (service PC access is the trust boundary)
- Optional TLS for backend communication
- No sensitive data stored on disk (no credentials, no fingerprint templates)

### Compatibility
- Compatible with existing `FingerprintSignatureProvider` contract on backend (SignatureProviderResult schema)
- The agent is **drop-in replaceable** for the current Mock adapter

## 10. Out of Scope

- Fingerprint template matching or verification (backend responsibility)
- User interface or capture confirmation screen (device button triggers capture)
- Multiple concurrent capture requests (handled sequentially)
- Storing fingerprint images beyond transit
- Certificate management or PKI

## 11. References

- Existing interface: `IScannerAdapter` in KCB Backend
- Existing provider: `FingerprintSignatureProvider` in KCB Backend
- Backend endpoint: `POST /api/kcb/tonghopkyso/captureesign`
- Scanner SDKs: SecuGen FDx SDK Pro, Digital Persona SDK, Futronic SDK