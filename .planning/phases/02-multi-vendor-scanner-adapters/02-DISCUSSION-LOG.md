# Phase 2: Multi-vendor Scanner Adapters - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-29
**Phase:** 02-Multi-vendor-Scanner-Adapters
**Areas discussed:** Init/discovery, IScannerAdapter extension, Device selection, Fallback strategy, Futronic x86 constraint, Capture timeout budget, PNG output normalization, SDK DLL distribution

---

## Init / Discovery

| Option | Description | Selected |
|--------|-------------|----------|
| Eager at startup | Try all scanners when service starts; /health reflects real status | |
| Lazy on first capture | Defer until first request; faster startup but slower first capture | |
| Configurable per adapter | Each scanner entry specifies eager or lazy mode | |

**User's choice:** "tôi nghĩ là mỗi lần capture ta đều phải try to connect" (lazy per capture)
**Notes:** Each /api/capture triggers connection attempt. No persistent state. ScannerManager tracks last working adapter for potential fallback ordering.

---

## IScannerAdapter Extension

| Option | Description | Selected |
|--------|-------------|----------|
| Extend interface directly | Add Initialize() + VendorErrorCode — simple, explicit | ✓ |
| Abstract base class | BaseScannerAdapter with shared logic; adapters inherit | |
| Keep interface clean | ScannerManager handles init/error separately; adapters only implement Scan() | |

**User's choice:** Extend interface directly
**Notes:** User selected the recommended option.

---

## Device Selection

| Option | Description | Selected |
|--------|-------------|----------|
| First found | Use first device enumerated by SDK — simplest | ✓ |
| Config-specified device ID | Pick device by serial in config.json — explicit but requires knowing serial | |
| Enumerate and pick first | List all devices, use first working — explicit enumeration | |

**User's choice:** First found
**Notes:** Works for typical single-scanner deployments.

---

## Fallback Strategy

| Option | Description | Selected |
|--------|-------------|----------|
| Try next in priority | Each capture tries adapters in order until one works | ✓ |
| Remember last working adapter | Stick with working adapter until it fails | |
| Config-specified fallback mode | Add `fallback: immediate | sticky` option | |

**User's choice:** Try next in priority (recommended)
**Notes:** Fresh evaluation every capture; adapts to hot-plugged scanners.

---

## Futronic x86 Constraint

| Option | Description | Selected |
|--------|-------------|----------|
| Run as x86 | Build with `<PlatformTarget>x86</PlatformTarget>` — all scanners 32-bit anyway | ✓ |
| Isolate in x86 subprocess | FutronicAdapter in separate x86 process — complex | |
| Try x64 Futronic first | Attempt x64 driver first; fall back to x86 | |

**User's choice:** Run as x86 (recommended)
**Notes:** All vendor SDKs are 32-bit; 4GB memory limit irrelevant for this use case.

---

## Capture Timeout Budget

| Option | Description | Selected |
|--------|-------------|----------|
| 10 seconds total | Total budget across all adapter attempts | ✓ |
| 5s per adapter, no cap | Each adapter 5s; worst case 15s | |
| 15 seconds total | Generous budget for real-world behavior | |

**User's choice:** 10 seconds total (recommended)
**Notes:** ~3s connect + ~3s capture per adapter. Returns CAPTURE_TIMEOUT after 10s total.

---

## PNG Output Normalization

| Option | Description | Selected |
|--------|-------------|----------|
| Pass through as-is | Return what SDK produces — simpler, more faithful | ✓ |
| Normalize to 500dpi grayscale | Convert all to 500dpi 8-bit grayscale | |
| Let each adapter decide | Return what SDK gives with vendor/model tags | |

**User's choice:** Pass through as-is (recommended)
**Notes:** SCAN-07 implies SDK decides. Backend handles variance in resolution/bit depth.

---

## SDK DLL Distribution

| Option | Description | Selected |
|--------|-------------|----------|
| Copy to install directory | All SDK DLLs in app install folder alongside exe | |
| Separate vendor subdirectories | Adapters/SecuGen/, Adapters/DigitalPersona/, Adapters/Futronic/ | |
| GAC / system registration | Register DLLs in GAC or System32 | |

**User's choice:** Let the agent decide
**Notes:** Agent recommended "Copy to install directory" — simplest, portable, appropriate for .NET Framework 4.8 services.

---

## the agent's Discretion

- **SDK DLL distribution**: Agent selected "Copy to install directory alongside exe" as the appropriate pattern for .NET Framework 4.8 with vendor SDK DLLs.