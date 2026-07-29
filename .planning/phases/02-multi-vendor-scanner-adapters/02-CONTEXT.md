# Phase 2: Multi-vendor Scanner Adapters - Context

**Gathered:** 2026-07-29
**Status:** Ready for planning

<domain>
## Phase Boundary

Replace `MockScannerAdapter` with real vendor SDK adapters for SecuGen, Digital Persona, and Futronic. `ScannerManager` selects an adapter per capture request using lazy connect + priority-based fallback. Deliver `SecuGenAdapter`, `DigitalPersonaAdapter`, `FutronicAdapter`, and `ScannerManager`.

</domain>

<decisions>
## Implementation Decisions

### Init / Discovery
- **D-01:** Lazy connect per capture — each `/api/capture` triggers adapter selection → connection attempt → capture. No persistent connection state between requests. ScannerManager tracks last working adapter per attempt.

### IScannerAdapter Interface
- **D-02:** Extend `IScannerAdapter` directly — add `Initialize()` method and `VendorErrorCode` (string) property. All adapters implement vendor-specific init and error translation. `MockScannerAdapter` gets trivial stubs.

### Device Selection
- **D-03:** First found — each adapter enumerates available devices via SDK and uses the first device found. Simplest; works for typical single-scanner deployments.

### Fallback Strategy
- **D-04:** Priority-based fallback per capture — each `/api/capture` tries adapters in order (SecuGen → Digital Persona → Futronic) until one succeeds. If all fail, return `SCANNER_NOT_CONNECTED`. No sticky "last working" state — always fresh evaluation.

### Futronic x86 Constraint
- **D-05:** Agent runs as x86 (32-bit) — `<PlatformTarget>x86</PlatformTarget>` in csproj. All vendor SDKs are 32-bit; single-process simplicity.

### Capture Timeout
- **D-06:** 10 seconds total budget — covers all adapter attempts combined. Each adapter gets ~3s connect + ~3s capture before trying next. On timeout, return `CAPTURE_TIMEOUT`.

### PNG Output
- **D-07:** Pass through as-is — each adapter returns whatever the SDK produces (native resolution, bit depth, color). No normalization step. `MimeType` in `CaptureResult` reflects actual format.

### SDK DLL Distribution
- **D-08:** Copy to install directory alongside exe — vendor SDK DLLs live in `C:\Program Files\FingerprintAgent\` next to the agent exe. Relative path resolution via `AppDomain.CurrentDomain.BaseDirectory`.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

- `.planning/ROADMAP.md` §Phase 2 — goal, success criteria, deliverables
- `.planning/REQUIREMENTS.md` §SCAN-01 through SCAN-07 — specific requirements for scanner adapters
- `.planning/PROJECT.md` — core value, constraints, business context
- `src/FingerprintAgent/Adapters/IScannerAdapter.cs` — existing interface (to be extended)
- `src/FingerprintAgent/Adapters/CaptureResult.cs` — existing DTO (unchanged)
- `src/FingerprintAgent/Adapters/MockScannerAdapter.cs` — existing implementation (reference for patterns)
- `src/FingerprintAgent/Service/FingerprintAgentService.cs` — wires the scanner into `HttpServer`
- `src/FingerprintAgent/Api/CaptureHandler.cs` — calls `scanner.Scan()`, handles error responses
- `src/FingerprintAgent/Api/HealthHandler.cs` — uses `scanner.IsConnected`, `scanner.DeviceId`, `scanner.Model`
- `src/FingerprintAgent/Configuration/AgentConfig.cs` §ScannerConfig — `Priority` array and `MockMode` flag

No external specs — requirements fully captured in decisions above.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `IScannerAdapter` interface: already defined in `src/FingerprintAgent/Adapters/IScannerAdapter.cs` — extend it, don't replace
- `CaptureResult` DTO: already handles `IsSuccess`, `ImageBytes`, `MimeType`, `CapturedAt`, `DeviceId`, `VerificationData`, `ErrorMessage`, `Width`, `Height`
- `MockScannerAdapter`: reference implementation in same folder
- `AgentLogger`: already wired in `FingerprintAgentService` — use for connection attempts and error logging

### Established Patterns
- Eager assignment in `OnStart` → `FingerprintAgentService` creates `_scanner = new MockScannerAdapter()` and passes to `HttpServer`. ScannerManager will replace this assignment.
- Error codes are already defined: `SCANNER_NOT_CONNECTED`, `CAPTURE_TIMEOUT`, `CAPTURE_FAILED`, `INVALID_REQUEST` — adapters use these via `CaptureResult.ErrorMessage` or throw
- GDI+ per-call object creation (from `MockScannerAdapter`): real scanners likely create SDK objects per capture too

### Integration Points
- `FingerprintAgentService.OnStart`: replaces `new MockScannerAdapter()` with `ScannerManager` instantiation
- `HttpServer` constructor: accepts `IScannerAdapter` — ScannerManager implements `IScannerAdapter` (composite pattern) OR ScannerManager is passed alongside so it can swap active adapter
- `CaptureHandler`: calls `scanner.Scan()` — already correct for any `IScannerAdapter`
- `HealthHandler`: uses `scanner.DeviceId`, `scanner.Model` — ScannerManager should expose the active adapter's properties
- `ScannerConfig.Priority`: already exists in `AgentConfig.cs` — `["SecuGen", "DigitalPersona", "Futronic"]`
- `ScannerConfig.MockMode`: already exists — Phase 2 adapters should only activate when `MockMode = false`

</code_context>

<specifics>
## Specific Ideas

- Vendor error codes: each adapter translates SDK-specific error codes into human-readable strings via `VendorErrorCode` property — used for logging and future debugging
- Futronic uses P/Invoke x86 declarations — native SDK lives in the install folder, called via `[DllImport]` with `CallingConvention = CallingConvention.Cdecl`
- Setup documentation: `SCANNER_SETUP.md` for each vendor (per ROADMAP deliverables) — SecuGen free SDK, Digital Persona U.are.U SDK, Futronic Standard SDK

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope.

</deferred>

---

*Phase: 02-Multi-vendor-Scanner-Adapters*
*Context gathered: 2026-07-29*