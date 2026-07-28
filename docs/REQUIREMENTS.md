# Local Fingerprint Agent — Requirements

**Version:** 1.0  
**Project:** KCB Fingerprint Capture Agent  
**Platform:** Windows 7+ (32-bit), .NET Framework 4.x  
**Scope:** Local Windows Service for fingerprint image capture and hashing

---

## 1. Overview

The Fingerprint Agent is a **local Windows service** installed on healthcare worker PCs. It acts as a bridge between USB fingerprint scanners and the KCB Backend API. When the backend requests a fingerprint capture, the agent:

1. Communicates with the connected scanner
2. Captures a fingerprint image
3. Returns the raw PNG image bytes along with a SHA-256 hash of those bytes

**Key constraint:** The agent only captures and returns image data. It does **not** perform any biometric matching (1:1 or 1:N). Template generation for matching is handled by the KCB Backend.

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────┐
│              Fingerprint Agent (Windows Service)     │
│                                                      │
│  ┌──────────────┐  ┌──────────────────────────────┐ │
│  │ HTTP API     │  │ Scanner Adapter Layer         │ │
│  │ /capture     │──│ ┌──────────┐ ┌─────────────┐  │ │
│  │ /health      │  │ │ SecuGen  │ │ Digital     │  │ │
│  │ /configure   │  │ │ Adapter  │ │ Persona     │  │ │
│  └──────────────┘  │ │          │ │ Adapter     │  │ │
│                    │ ├──────────┤ ├─────────────┤  │ │
│                    │ │ Futronic │ │ (future)    │  │ │
│                    │ │ Adapter  │ │             │  │ │
│                    │ └──────────┘ └─────────────┘  │ │
│                    └──────────────────────────────┘ │
└─────────────────────────────────────────────────────┘
           │                          │
           ▼                          ▼
   ┌───────────────┐         ┌──────────────────┐
   │ KCB Backend   │         │ USB Fingerprint  │
   │ API           │         │ Scanner         │
   └───────────────┘         └──────────────────┘
```

### Component Responsibilities

| Component | Responsibility |
|-----------|----------------|
| `HttpApiServer` | Listens for capture requests, exposes `/api/capture`, `/health` |
| `ScannerManager` | Routes scanner operations to the correct adapter based on configuration |
| `SecuGenAdapter` | Wraps SecuGen FDx SDK Pro for Hamster Plus/Pro 20 scanners |
| `DigitalPersonaAdapter` | Wraps HID U.are.U SDK for Digital Persona scanners |
| `FutronicAdapter` | Wraps Futronic Standard SDK via P/Invoke |
| `ConfigurationService` | Loads and manages `config.json` settings |
| `LoggingService` | Logs all operations with timestamps |
| `HealthMonitor` | Tracks scanner connection state, device events |

---

## 3. Functional Requirements

### 3.1 Scanner Connectivity

| ID | Requirement | Details |
|----|-------------|---------|
| **FR-01** | Connect to SecuGen scanners via FDx SDK Pro | Initialize `SecuGen.FDxSDKPro.Windows.dll`. Call `Init`, `OpenDevice`. Support Hamster Plus, Hamster Pro 20, and other SgBioEntry-compatible models. Device ID string: `"SecuGen Fingerprint Scanner"` |
| **FR-02** | Connect to Digital Persona scanners via U.are.U SDK | Initialize DPFP library. Call `dpfpdd_init`, `dpfpdd_open`. Require SDK ≥ 2.2.3. Use **legacy non-WBF driver** on Windows 10+ to avoid WBF driver conflict |
| **FR-03** | Connect to Futronic scanners via Standard SDK | Use P/Invoke (`DllImport`) to call `FtrScanOpenDevice`, `FtrScanCaptureImage`. Platform target **must be x86**. Note: Free Standard SDK uses proprietary format only (no ANSI 378 / ISO 19794-2) |
| **FR-04** | Auto-detect connected scanner | On startup, enumerate USB devices. Attempt to open each supported scanner type. Use the first successfully opened device as the active scanner |
| **FR-07** | Handle device disconnection/reconnection | Subscribe to device removal/insertion events. On disconnect: mark scanner as unavailable, return error to pending capture requests. On reconnect: re-initialize scanner automatically |

### 3.2 Capture Operations

| ID | Requirement | Details |
|----|-------------|---------|
| **FR-05** | Capture fingerprint image (raw PNG bytes) | Call the active scanner's image capture API. Convert the raw image to PNG byte array (no compression artifacts). Image must be in 8-bit grayscale or 24-bit color PNG format |
| **FR-06** | Return SignatureProviderResult | Response object containing: `ImageBytes` (PNG byte array), `Sha256Hash` (SHA-256 of ImageBytes), `Timestamp` (ISO 8601 UTC), `ScannerModel` (string), `DeviceId` (string). **SHA-256 must be computed over raw PNG bytes before base64 encoding** |

### 3.3 Service Operation

| ID | Requirement | Details |
|----|-------------|---------|
| **FR-08** | Run as Windows Service | Install as a Windows Service. No UI required. Run in `LocalSystem` or a designated service account. Support Start/Stop/Restart via `services.msc` and `sc.exe` |
| **FR-09** | Expose HTTP API endpoint | Host HTTP server on configurable port (default: `5043`). No authentication required (local service only). Endpoint: `POST /api/capture` — accepts JSON body, returns JSON with base64-encoded PNG image. See Section 5 for request/response schema |
| **FR-10** | Configuration via JSON file | All settings loaded from `config.json` in the agent's install directory. See Section 6 for schema. Support runtime reconfiguration via `POST /configure` without service restart |
| **FR-11** | Logging of all capture operations | Log: startup, scanner initialization, capture requests, capture results (success/failure), device events (connect/disconnect), errors. Use a structured logging format (timestamp + level + message + metadata). Write to `logs/fingerprint-agent-{date}.log` |

### 3.4 Health and Monitoring

| ID | Requirement | Details |
|----|-------------|---------|
| **FR-12** | Health check endpoint | `GET /health` returns HTTP 200 when service is running and a scanner is connected. Returns HTTP 503 when scanner is disconnected or not initialized. Response body: `{ "status": "healthy" | "unhealthy", "scannerConnected": bool, "scannerModel": string, "uptime": int }` |

---

## 4. HTTP API Specification

### 4.1 POST /api/capture

**Description:** Request a fingerprint capture from the connected scanner.

**Request Body:**
```json
{
  "thamChieuId": 12345,
  "maPhieu": "BenhAnNgoaiTru",
  "loaiPhieu": 1,
  "vaiKyId": 2,
  "nhanLucId": null,
  "xmlBase64": null,
  "metadata": {}
}
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `thamChieuId` | long | Yes | - | Document reference ID |
| `maPhieu` | string | Yes | - | Document type code (e.g., "BenhAnNgoaiTru") |
| `loaiPhieu` | int | Yes | - | BenhAnDienTuLoaiPhieu enum value |
| `vaiKyId` | long | Yes | - | Signature role ID |
| `nhanLucId` | long? | No | null | Optional medical staff ID |
| `xmlBase64` | string? | No | null | Optional XML |
| `metadata` | Dictionary<string, string>? | No | {} | Additional metadata |

**Success Response (200 OK):**
```json
{
  "isSuccess": true,
  "imageBytes": "base64-encoded-PNG",
  "mimeType": "image/png",
  "capturedAt": "2026-07-28T10:30:00Z",
  "deviceId": "secugen-hamster-001",
  "verificationData": "SHA256-base64-hash",
  "errorMessage": null
}
```

**Error Responses:**

| HTTP Code | Scenario |
|-----------|----------|
| 400 | Invalid request body |
| 503 | Scanner not connected or not initialized |
| 504 | Capture timeout |

**Error Response Body:**
```json
{
  "isSuccess": false,
  "imageBytes": null,
  "errorMessage": "Device not connected",
  "errorCode": "SCANNER_NOT_CONNECTED"
}
```

### 4.2 GET /health

**Success Response (200 OK):**
```json
{
  "status": "healthy",
  "scannerConnected": true,
  "scannerModel": "SecuGen Hamster Plus",
  "uptime": 3600
}
```

**Unhealthy Response (503 Service Unavailable):**
```json
{
  "status": "unhealthy",
  "scannerConnected": false,
  "scannerModel": null,
  "uptime": 3600
}
```

### 4.3 POST /configure

**Description:** Reload configuration from `config.json` or apply a partial configuration patch.

**Request Body:**
```json
{
  "scannerType": "secugen",
  "backendUrl": "https://kcb-api.example.com",
  "port": 9150
}
```

All fields are optional. Only the provided fields are updated. To fully reset, pass an empty object `{}`.

**Success Response (200 OK):**
```json
{
  "success": true,
  "config": { /* current full configuration */ }
}
```

---

## 5. Configuration Schema (config.json)

```json
{
  "service": {
    "name": "FingerprintAgent",
    "displayName": "Fingerprint Agent Service",
    "description": "Local fingerprint scanner service for KCB E-Sign"
  },
  "http": {
    "port": 5043,
    "host": "localhost"
  },
  "scanners": {
    "preferredBrand": "auto",
    "timeoutMs": 30000,
    "retryCount": 3
  },
  "logging": {
    "level": "Information",
    "path": "C:\\ProgramData\\FingerprintAgent\\logs"
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `service.name` | string | `"FingerprintAgent"` | Service identifier |
| `service.displayName` | string | `"Fingerprint Agent Service"` | Display name |
| `service.description` | string | - | Service description |
| `http.port` | integer | `5043` | HTTP API listening port |
| `http.host` | string | `"localhost"` | HTTP binding host |
| `scanners.preferredBrand` | string | `"auto"` | Scanner brand. `"auto"` tries all supported scanners until one connects |
| `scanners.timeoutMs` | integer | `30000` | Max time to wait for a fingerprint capture |
| `scanners.retryCount` | integer | `3` | Number of retry attempts |
| `logging.level` | string | `"Information"` | Minimum log level to write |
| `logging.path` | string | `"C:\\ProgramData\\FingerprintAgent\\logs"` | Log file path |

---

## 6. Project Structure

```
fingerprint-agent/
├── FingerprintAgent.sln
├── config.json
├── FingerprintAgent/
│   ├── FingerprintAgent.csproj      # .NET 4.x, x86 platform target
│   ├── Program.cs                   # Service entry point, ServiceBase registration
│   ├── Configuration/
│   │   ├── ConfigurationService.cs
│   │   └── AppConfiguration.cs      # POCO for config.json schema
│   ├── HttpApi/
│   │   ├── HttpApiServer.cs         # Self-hosted HTTP listener (System.Net.HttpListener)
│   │   ├── CaptureController.cs     # Handles /capture, /health, /configure
│   │   └── Models/
│   │       ├── CaptureRequest.cs
│   │       ├── CaptureResponse.cs
│   │       └── HealthResponse.cs
│   ├── Scanner/
│   │   ├── IScannerAdapter.cs       # Common interface for all adapters
│   │   ├── ScannerManager.cs        # Routes to correct adapter
│   │   ├── SecuGen/
│   │   │   ├── SecuGenAdapter.cs
│   │   │   └── SecuGenNative.cs     # P/Invoke declarations for SecuGen DLL
│   │   ├── DigitalPersona/
│   │   │   ├── DigitalPersonaAdapter.cs
│   │   │   └── DpfpNative.cs        # P/Invoke declarations for DP SDK
│   │   └── Futronic/
│   │       ├── FutronicAdapter.cs
│   │       └── FutronicNative.cs    # P/Invoke declarations for Futronic SDK
│   ├── Logging/
│   │   └── LoggingService.cs
│   └── Service/
│       └── FingerprintAgentService.cs  # Windows Service implementation
└── tests/
    └── FingerprintAgent.Tests/
        ├── FingerprintAgent.Tests.csproj
        ├── ScannerDispatcherTests.cs
        ├── SecuGenAdapterTests.cs
        ├── CaptureResponseTests.cs   # SHA-256 hash verification tests
        └── IntegrationTests.cs
```

---

## 7. Non-Functional Requirements

| ID | Requirement | Target | Notes |
|----|-------------|--------|-------|
| **NFR-01** | Target .NET Framework 4.x | 4.7.2 or higher | Must run on Windows 7 which does not support .NET Core |
| **NFR-02** | Platform: x86 (32-bit) | x86 | Required by SecuGen, Futronic SDKs. Set in project file: `<PlatformTarget>x86</PlatformTarget>` |
| **NFR-03** | Windows 7 Ultimate 32-bit support | Windows 7 SP1+ 32-bit | Test on clean Win7 VM before release |
| **NFR-04** | Startup time | < 5 seconds | From service start to `/health` returning 200 |
| **NFR-05** | Capture latency | < 2 seconds | Time from capture API call to PNG bytes returned (excluding human interaction) |
| **NFR-06** | Memory usage | < 100 MB | Steady-state memory footprint |
| **NFR-07** | Service auto-restart on crash | Enabled | Configure via `sc.exe config` FailureActions or via the service installer |

---

## 8. Error Codes

| Code | HTTP Status | Description |
|------|-------------|-------------|
| `SCANNER_NOT_CONNECTED` | 503 | No scanner detected or scanner disconnected |
| `SCANNER_INIT_FAILED` | 503 | Scanner initialization failed (missing SDK, wrong driver) |
| `CAPTURE_TIMEOUT` | 504 | Fingerprint not placed on sensor within timeout period |
| `CAPTURE_FAILED` | 500 | Device error during capture |
| `INVALID_REQUEST` | 400 | Malformed request body or missing required fields |
| `HASH_MISMATCH` | 500 | SHA-256 computed on backend does not match (indicates data corruption) |

---

## 9. Out of Scope

The following are explicitly **NOT** part of this project's scope:

- **WebUSB** — browser-based fingerprint capture is handled separately by the frontend application
- **macOS / Linux support** — this is a Windows-only service
- **Biometric matching** (1:1 or 1:N) — the agent only captures images; matching is performed by the KCB Backend
- **Template storage** — no local enrollment or template database
- **Multi-finger capture** — single fingerprint per capture request
- **Image quality scoring** — beyond returning the image, no quality analysis is performed
- **SSL/TLS** — the HTTP API is localhost-only; no TLS configuration needed

---

## 10. Dependencies and SDKs

| Dependency | Version | Purpose | License |
|------------|---------|---------|---------|
| .NET Framework | 4.7.2+ | Runtime | Microsoft |
| SecuGen.FDxSDKPro.Windows.dll | ≥ 3.5 | SecuGen scanner interface | Free (SecuGen SDK License) |
| Futronic SDK (Standard) | ≥ 2.8 | Futronic scanner interface | Proprietary / Free Standard SDK |
| HID U.are.U SDK | ≥ 2.2.3 | Digital Persona scanner interface | HID Global (royalty-free distribution) |
| VC++ Redistributable 2015+ | x86 | Required by SecuGen SDK | Microsoft (free) |

---

## 11. Security Considerations

- HTTP API binds to `localhost` only (127.0.0.1). It does NOT listen on external interfaces.
- No authentication on the HTTP API (local service only, not network-accessible)
- SHA-256 hash is computed over raw PNG bytes to detect transmission corruption
- Log files may contain employee IDs — ensure log directory has appropriate ACLs
- Service account permissions: `LocalSystem` is recommended; minimum required is USB device access + file system write for logs

---

## 12. Installer Notes

The agent should be packaged as a Windows Installer (MSI or InstallShield) that:

1. Copies all files to `C:\Program Files\KCB\FingerprintAgent\`
2. Installs the Windows Service via `sc.exe create`
3. Installs the VC++ Redistributable 2015 x86 silently
4. Sets the service to **Auto-Start**
5. Opens a firewall rule for `localhost` access only on the configured port
6. Creates an uninstaller that removes the service and all files