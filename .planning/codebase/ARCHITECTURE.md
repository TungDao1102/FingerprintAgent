# Architecture

**Analysis Date:** 2026-08-19

## System Overview

```text
┌────────────────────────────────────────────────────────────────────────────┐
│                    Hospital PC (Windows 10/11)                              │
│                                                                             │
│  ┌────────────────────┐       local HTTP        ┌────────────────────────┐ │
│  │  Angular Client    │  ──── POST /api/capture │  FingerprintAgent      │ │
│  │  (HIS web app)     │ ◄──── JSON + base64 ──► │  Windows Service       │ │
│  │  same machine      │       GET  /health      │  (localhost:5043)      │ │
│  └────────────────────┘                         └────────────┬───────────┘ │
│                                                              │             │
│                                              ┌───────────────▼──────────┐  │
│                                              │  HttpServer (HttpListener)│ │
│                                              │  + CorsMiddleware         │ │
│                                              │  + CaptureHandler         │ │
│                                              │  + HealthHandler          │ │
│                                              └───────────────┬──────────┘  │
│                                                              │             │
│                                              ┌───────────────▼──────────┐  │
│                                              │  ScannerManager (IScanner│ │
│                                              │  Adapter facade, priority │ │
│                                              │  fallback + backoff)      │ │
│                                              └──┬─────┬─────┬─────┬─────┘ │
│                                                 │     │     │     │        │
│                              ┌──────────────────┘     │     │     │        │
│                              │            ┌────────────┘     │     │        │
│                              │            │      ┌───────────┘     │        │
│                              ▼            ▼      ▼                 ▼        │
│                          ┌──────┐   ┌──────┐  ┌──────┐       ┌──────┐      │
│                          │ ZK   │   │ Secu │  │ Dig. │       │Futro-│      │
│                          │ Teco │   │ Gen  │  │Perso-│       │ nic  │      │
│                          │adapt.│   │adapt.│  │ na   │       │adapt.│      │
│                          │      │   │      │  │adapt.│       │      │      │
│                          └──────┘   └──────┘  └──────┘       └──────┘      │
│                              │            │          │              │      │
│                              ▼            ▼          ▼              ▼      │
│                          libzkfp.dll   SecuGen    DPUruNet       ftrScan   │
│                          (native)      SDK        wrapper        API.dll   │
│                                                                             │
│                              USB fingerprint scanner(s)                     │
└────────────────────────────────────────────────────────────────────────────┘
```

The FingerprintAgent is a **single-machine proxy** that bridges a browser-based HIS to USB-attached fingerprint scanners. Browser webapps cannot access USB hardware directly; this Windows Service brokers the capture and returns the PNG image (plus a SHA-256 verification hash) over a localhost HTTP API.

## Component Responsibilities

| Component | Responsibility | File |
|-----------|----------------|------|
| `FingerprintAgentService` | Windows Service `OnStart`/`OnStop` lifecycle; wires logger, config, scanner, server, watcher | `src/FingerprintAgent/Service/FingerprintAgentService.cs` |
| `FingerprintAgent.Host.Program` | Dual-mode entry: `--service` (SCM) or `--console`/interactive | `src/FingerprintAgent.Host/Program.cs` |
| `HttpServer` | `HttpListener` loop; request routing; CORS preflight; in-flight tracking; graceful drain on stop | `src/FingerprintAgent/Api/HttpServer.cs` |
| `CorsMiddleware` | Apply CORS headers (wildcard or allowlist mode); handle `OPTIONS` preflight; hot-reloadable | `src/FingerprintAgent/Api/CorsMiddleware.cs` |
| `CaptureHandler` | `/api/capture` POST handler; deserializes `CaptureRequest`; calls scanner; maps error codes to HTTP status | `src/FingerprintAgent/Api/CaptureHandler.cs` |
| `HealthHandler` | `/health` GET handler; probes active scanner; reports status, backoff, uptime | `src/FingerprintAgent/Api/HealthHandler.cs` |
| `ScannerManager` | Composite `IScannerAdapter` facade; priority fallback across vendors; exponential backoff; per-call adapter lifecycle | `src/FingerprintAgent/Adapters/ScannerManager.cs` |
| `IScannerAdapter` | Vendor-neutral interface: `IsConnected`, `DeviceId`, `Model`, `Initialize`, `ProbeConnection`, `ScanAsync(ct)`, `VendorErrorCode` | `src/FingerprintAgent/Adapters/IScannerAdapter.cs` |
| `BaseScannerAdapter` | Abstract base providing the common grayscale→PNG encoding path (`ToPngGrayscale`) + SHA-256 verification hash | `src/FingerprintAgent/Adapters/BaseScannerAdapter.cs` |
| `MockScannerAdapter` | Generates deterministic 320×240 PNG ("MOCK SCANNER" label) for dev/CI without hardware | `src/FingerprintAgent/Adapters/MockScannerAdapter.cs` |
| `ZKTecoAdapter` | Real adapter; `ZkTecoFingerPrint` NuGet v1.2.1; rolling-capture retry loop (8s budget); static `ZkTecoFingerHost.Close()` teardown | `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs` |
| `SecuGenAdapter` | Real adapter; `SecuGen.FDxSDKPro.Windows.dll` external SDK; stub types under `#if !SECUGEN_SDK_PRESENT` | `src/FingerprintAgent/Adapters/SecuGenAdapter.cs` |
| `DigitalPersonaAdapter` | Real adapter; `DPUruNet` NuGet v1.0.0.1; event-driven capture wrapped via `TaskCompletionSource` | `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs` |
| `FutronicAdapter` | Real adapter; raw P/Invoke against x86 `ftrScanAPI.dll`; pixel inversion (255 − raw) before PNG | `src/FingerprintAgent/Adapters/FutronicAdapter.cs` |
| `CaptureResult` | Static factories (`Ok`, `Fail`) + DTO for scan outcomes (bytes, mime, sha256, dimensions, error code) | `src/FingerprintAgent/Adapters/CaptureResult.cs` |
| `ConfigLoader` | `Microsoft.Extensions.Configuration.Json` reader; manual section binding into `AgentConfig`; `LoadFromDirectory` for hot-reload | `src/FingerprintAgent/Configuration/ConfigLoader.cs` |
| `AgentConfig` | Strongly-typed config sections: Service, Http, Cors, Scanner, Logging, Security | `src/FingerprintAgent/Configuration/AgentConfig.cs` |
| `ConfigFileWatcher` | `FileSystemWatcher` + 300ms debounce; fires `ConfigReloaded` event with new `AgentConfig` | `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs` |
| `AgentLogger` | Async-safe file appender (`StreamWriter` w/ `AutoFlush`) + EventLog sink; SEC-04 base64 redaction; correlation ID generator | `src/FingerprintAgent/Logging/AgentLogger.cs` |
| `CaptureRequest` / `CaptureResponse` | Newtonsoft.Json DTOs; Vietnamese field names (`thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`, `metadata`) | `src/FingerprintAgent/Models/CaptureRequest.cs`, `CaptureResponse.cs` |

## Pattern Overview

**Overall:** **Layered service facade** with **adapter composite** at the hardware boundary and **hot-reloadable configuration** at the policy boundary.

**Key Characteristics:**
- **Manual composition root** — `FingerprintAgentService.OnStart` is the only `new`-everything site; no DI container despite `Microsoft.Extensions.DependencyInjection` being referenced (intentional, per AGENTS.md). Every component takes a constructor-injected `AgentLogger`.
- **Composite + Strategy** — `ScannerManager` *is-an* `IScannerAdapter` (composite facade) that delegates to a priority-ordered array of *real* adapters (strategies). The consumer (`CaptureHandler`) only ever sees `IScannerAdapter`.
- **Hot-reload via event** — `ConfigFileWatcher` publishes a strongly-typed `AgentConfig` to a single `ConfigReloaded` event; `FingerprintAgentService.OnConfigReloaded` atomically swaps CORS (HashSet under lock) and re-registers `ScannerManager.UpdatePriority(...)` without touching the active adapter or backoff state (D-09).
- **Per-call adapter lifecycle** — `ScannerManager` calls `adapter.Initialize()` then `adapter.ScanAsync()` on every request. No persistent connection state between requests (D-01); enables lazy-connect semantics across heterogeneous SDKs.
- **Total-budget timeout only** — `ScannerManager.ScanAsync` wraps the iteration in a 20s `CancellationTokenSource.CancelAfter`. Per-adapter budgets are NOT enforced (ZKTeco needs 15s rolling capture for UX). The 3s figure cited in adapter comments is the DigitalPersona local timeout, not ScannerManager's contract.
- **Fire-and-forget capture pipeline** — `HttpServer.ProcessRequestLoop` awaits `HandleRequestAsync` then `.ContinueWith` removes the task from `_inFlightRequests` and logs faults; the long-running task lives on `TaskScheduler.Default`.
- **Static SDK teardown** — `ZKTecoAdapter.Dispose()` deliberately does NOT call `ZkTecoFingerHost.Close()`. The host is a process-wide singleton — close happens exactly once at `FingerprintAgentService.OnStop` after all adapters dispose (and again in the console path's `Console.CancelKeyPress`).
- **Manual JSON binding over ConfigurationBuilder** — `ConfigLoader.BindConfig` reads `configuration.GetSection(...)` per key rather than `ConfigurationBinder.Bind`. Trades brevity for explicit control of defaults and array parsing.

## Layers

**Adapter Layer (`src/FingerprintAgent/Adapters/`):**
- Purpose: Wrap vendor SDKs behind a single interface; orchestrate multi-vendor fallback
- Location: `src/FingerprintAgent/Adapters/`
- Contains: `IScannerAdapter` (contract), `BaseScannerAdapter` (template method for PNG encoding + SHA-256), 5 concrete adapters (Mock + 4 vendors), `ScannerManager` (composite facade), `CaptureResult` (DTO)
- Depends on: `Configuration` (for `AgentConfig`/`ScannerConfig`), `Logging` (`AgentLogger`)
- Used by: `Api/CaptureHandler`, `Api/HealthHandler`, `Service/FingerprintAgentService`

**API Layer (`src/FingerprintAgent/Api/`):**
- Purpose: HTTP transport — accept requests, route to handlers, write JSON responses, manage CORS
- Location: `src/FingerprintAgent/Api/`
- Contains: `HttpServer` (HttpListener loop + graceful stop), `CorsMiddleware` (preflight + headers), `CaptureHandler` (`/api/capture`), `HealthHandler` (`/health`)
- Depends on: `Adapters` (`IScannerAdapter`), `Logging`, `Models` (request/response DTOs)
- Used by: `Service/FingerprintAgentService`

**Service Layer (`src/FingerprintAgent/Service/`):**
- Purpose: Windows Service lifecycle (`OnStart`/`OnStop`/`StartConsole`/`StopConsole`); composition root; health-check timer; config-reload dispatcher
- Location: `src/FingerprintAgent/Service/`
- Contains: `FingerprintAgentService : ServiceBase`
- Depends on: All layers — owns instances of `ScannerManager`, `HttpServer`, `AgentLogger`, `ConfigFileWatcher`
- Used by: `FingerprintAgent.Host/Program` (via `ServiceBase.Run` or `StartConsole`)

**Configuration Layer (`src/FingerprintAgent/Configuration/`):**
- Purpose: Read & hot-reload `config.json`
- Location: `src/FingerprintAgent/Configuration/`
- Contains: `AgentConfig` (POCO + section types), `ConfigLoader` (Microsoft.Extensions.Configuration.Json reader), `ConfigFileWatcher` (FileSystemWatcher + debounce)
- Depends on: `Logging` (for reload errors)
- Used by: `Service/FingerprintAgentService`, `Adapters/ScannerManager`, `Api/HttpServer`

**Logging Layer (`src/FingerprintAgent/Logging/`):**
- Purpose: Structured file + EventLog sink; correlation IDs; base64 redaction
- Location: `src/FingerprintAgent/Logging/`
- Contains: `AgentLogger`, `LogLevel` enum
- Depends on: `Configuration` (for `LoggingConfig`)
- Used by: All layers

**Models Layer (`src/FingerprintAgent/Models/`):**
- Purpose: Wire-format DTOs (`CaptureRequest`, `CaptureResponse`) with Newtonsoft.Json attributes
- Location: `src/FingerprintAgent/Models/`
- Contains: 2 DTO classes
- Depends on: nothing project-internal
- Used by: `Api/CaptureHandler`

**Host Layer (`src/FingerprintAgent.Host/`):**
- Purpose: Process entry point; dual-mode dispatch (service vs console)
- Location: `src/FingerprintAgent.Host/`
- Contains: `Program.Main`
- Depends on: `Service`, `Configuration`, `Logging`, `ZkTecoFingerPrint` (for `ZkTecoFingerHost.Close()` on console Ctrl+C)
- Used by: OS (`sc.exe start FingerprintAgent` → `--service`) or developer (`FingerprintAgent.exe --console`)

## Data Flow

### Primary Capture Request Path

1. **HTTP arrives at OS networking stack** — listener registered on `http://{Http.Host}:{Http.Port}/` (`src/FingerprintAgent/Api/HttpServer.cs:36`).
2. **`HttpServer.ProcessRequestLoop`** (`src/FingerprintAgent/Api/HttpServer.cs:104`) — `await _listener.GetContextAsync()`, then fire-and-forget `HandleRequestAsync(context, ct)`; tracks each task in `_inFlightRequests` under `_inFlightLock`.
3. **`HandleRequestAsync`** (`src/FingerprintAgent/Api/HttpServer.cs:137`) — checks `ct.IsCancellationRequested` (returns 503 if draining), runs `_cors.HandleCorsPreflight(...)` (writes 204/403 for `OPTIONS`), sets content-type, applies `_cors.ApplyCorsHeaders(...)`, generates a 10-char correlation ID via `AgentLogger.GenerateCorrelationId()`.
4. **Route** (`src/FingerprintAgent/Api/HttpServer.cs:169-186`) — `path == "/health"` → `HealthHandler.HandleAsync`; `path == "/api/capture" && method == "POST"` → `CaptureHandler.HandleAsync`; otherwise 404 with `{"error":"Not found"}` JSON.
5. **`CaptureHandler.HandleAsync`** (`src/FingerprintAgent/Api/CaptureHandler.cs:22`) — reads request body via `StreamReader`, deserializes `CaptureRequest` via `JsonConvert.DeserializeObject`; validates `thamChieuId` + `maPhieu` are non-empty (400 `INVALID_REQUEST` otherwise); calls `await scanner.ScanAsync()`.
6. **`ScannerManager.ScanAsync`** (`src/FingerprintAgent/Adapters/ScannerManager.cs:287`) — branches on `_mockMode` (delegates to `MockScannerAdapter`); else attempts SCAN-06 reconnect on `_activeAdapter` if `!IsConnected`; iterates `_adapters` in priority order with a 20s `CancellationTokenSource.CancelAfter` total budget; for each adapter, `adapter.Initialize()` → `adapter.ScanAsync(totalCts.Token)`. On success, sets `ActiveAdapter = adapter`, resets backoff, returns. On total failure, calls `ApplyBackoff(cid)` and returns `SCANNER_NOT_CONNECTED`.
7. **Vendor adapter** (`src/FingerprintAgent/Adapters/{ZKTeco|SecuGen|DigitalPersona|Futronic}Adapter.cs`) — performs device-specific capture; converts raw grayscale bytes to PNG via either vendor SDK (DigitalPersona) or `BaseScannerAdapter.ToPngGrayscale`; computes SHA-256 of PNG; returns `CaptureResult` with `IsSuccess=true`.
8. **`CaptureHandler`** maps `CaptureResult` → HTTP: success → 200 with `CaptureResponse` JSON (base64 PNG + SHA-256 + deviceId); failure → status from `MapErrorCode` (`SCANNER_NOT_CONNECTED`→503, `CAPTURE_TIMEOUT`→504, `CAPTURE_FAILED`/`CAPTURE_ERROR`→500, `INVALID_REQUEST`→400, `CONFIG_ERROR`→500) with error fields populated.
9. **`HttpServer`** finalizes — `Response.ContentLength64 = buffer.Length`; `OutputStream.WriteAsync`; `FlushAsync`; `Close`.

### Health Check Path

1. **HTTP GET `/health`** → `HttpServer.HandleRequestAsync` → `HealthHandler.HandleAsync` (`src/FingerprintAgent/Api/HealthHandler.cs:22`).
2. **`HealthHandler`** casts `scanner as ScannerManager`; calls `mgr.TryProbe(out deviceId, out model, out vendorErrorCode)` (`src/FingerprintAgent/Adapters/ScannerManager.cs:99`) — fast path reuses cached `ActiveAdapter.ProbeConnection()` (ZKTeco's 1ms device-count query); slow path iterates adapters calling `Initialize()` until one succeeds.
3. **Response**: 200 if `connected || backoffStep < 3`, else 503. Body: `{ status, deviceId, model, vendorErrorCode, uptime (hh:mm:ss), inBackoff, backoffStep }`.

### Config Hot-Reload Path

1. **`ConfigFileWatcher.OnRawChanged`** (`src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:48`) — stops + restarts the 300ms debounce `Timer` (coalesces VS/Notepad++ double-saves).
2. **`ConfigFileWatcher.OnDebounceElapsed`** (`src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:54`) — calls `ConfigLoader.LoadFromDirectory(directory)`; validates `newConfig.Scanner != null && newConfig.Cors != null` (D-08: bad parse / missing sections keeps old config, logs error, does NOT throw); invokes `ConfigReloaded?.Invoke(newConfig)`.
3. **`FingerprintAgentService.OnConfigReloaded`** (`src/FingerprintAgent/Service/FingerprintAgentService.cs:228`) — under `_configLock`, replaces `_config`; calls `_httpServer.UpdateCorsConfig(newConfig.Cors)` and `(_scanner as ScannerManager).UpdatePriority(newConfig.Scanner.Priority)`.
4. **`CorsMiddleware.UpdateConfig`** (`src/FingerprintAgent/Api/CorsMiddleware.cs:25`) — under `_corsLock`, atomically swaps the mode string and rebuilds the `HashSet<string>` of allowed origins.
5. **`ScannerManager.UpdatePriority`** (`src/FingerprintAgent/Adapters/ScannerManager.cs:223`) — under `_adapterLock`, replaces the `_adapters` array; disposes old adapters that are NOT `_activeAdapter`; preserves backoff state.

**State Management:**
- **Connection state** is per-adapter, per-call (D-01). No persistent connection; `Initialize()` is called on every `ScanAsync`.
- **Backoff state** is global on `ScannerManager` (`_backoffStep`, `_backoffUntil`, `_backoffLock`). Reset on every successful capture. Preserved across priority changes (D-09).
- **CORS state** is a `HashSet<string>` under `_corsLock`. Swapped atomically on `UpdateConfig`.
- **Config state** is replaced wholesale inside `_configLock` (only `Scanner.Priority` and `Cors` are reloadable; other sections are ignored on reload by convention).
- **In-flight request state** is `List<Task>` under `_inFlightLock`. `Stop()` waits up to 30s for drain.

## Key Abstractions

**`IScannerAdapter`:**
- Purpose: Vendor-neutral scanner contract — every vendor SDK is hidden behind this interface so the API layer has one type to talk to
- Examples: `src/FingerprintAgent/Adapters/{Mock,ZKTeco,SecuGen,DigitalPersona,Futronic}Adapter.cs`, `ScannerManager` (composite)
- Pattern: Strategy + Composite; `ScannerManager` IS-A `IScannerAdapter` (delegates to a priority array of adapters), so the API layer never branches on whether `MockMode` is on.

**`BaseScannerAdapter`:**
- Purpose: Template-method base providing the grayscale → PNG path; concrete adapters only implement `InitializeDevice()`, `CaptureRawImage()`, `ImageWidth`, `ImageHeight`
- Examples: `SecuGenAdapter` extends it; `FutronicAdapter` does NOT extend it (defines its own local `ToPngGrayscale` because it needs pre-PNG pixel inversion, D-07); `ZKTecoAdapter` and `DigitalPersonaAdapter` do NOT extend it (different image-encoding pipelines)
- Pattern: Template Method with optional shared utility.

**`AgentConfig` + section types:**
- Purpose: Strongly-typed configuration root with per-section nested POCOs
- Examples: `ServiceConfig`, `HttpConfig`, `CorsConfig`, `ScannerConfig`, `LoggingConfig`, `SecurityConfig`
- Pattern: POCO with defaults — every property has a default so missing JSON keys fall back gracefully via `ConfigLoader.GetString(... ) ?? config.X.Y`.

**`CaptureResult`:**
- Purpose: Discriminated-ish DTO for scan outcomes with static factory methods
- Examples: `CaptureResult.Ok(byte[])` and `CaptureResult.Fail(errorCode, message)`
- Pattern: Result object; consumers check `IsSuccess` rather than throwing exceptions across the adapter→handler boundary.

## Entry Points

**`FingerprintAgent.Host.exe` (active):**
- Location: `src/FingerprintAgent.Host/Program.cs`
- Triggers: OS Service Control Manager (via `--service` flag added by `Install-Service.ps1` to the binary path) OR user double-click / `dotnet run` (interactive → `Environment.UserInteractive` → console mode)
- Responsibilities:
  - `--service` → `ServiceBase.Run(new FingerprintAgentService())` (block until SCM stop)
  - console mode → manual `new FingerprintAgentService(logger)` + `StartConsole()` + `Console.CancelKeyPress` + `ManualResetEvent.WaitOne(Timeout.InfiniteTimeSpan)` (or `FA_CONSOLE_TIMEOUT` env var for CI smoke tests)
  - Console path also explicitly calls `ZkTecoFingerHost.Close()` on Ctrl+C (the service path delegates this to `FingerprintAgentService.OnStop`)

**`src/FingerprintAgent/Program.cs` (dead code):**
- Location: `src/FingerprintAgent/Program.cs` — 61 lines, also a `ServiceBase.Run` entry point
- Status: **Dead**. The library `FingerprintAgent.csproj` is `OutputType=Library`, so this `Main` is never emitted into a built assembly. The active executable is `FingerprintAgent.Host`. The duplicate exists for legacy reasons; do not edit under the assumption it runs. Tests reference the library only — `InternalsVisibleTo FingerprintAgent.Tests`.

**`OnStart(string[] args)` → `OnStop()`:**
- Location: `src/FingerprintAgent/Service/FingerprintAgentService.cs:38-184`
- Triggers: SCM `Start`/`Stop` control codes OR `StartConsole()`/`StopConsole()` from the console path
- Responsibilities:
  - OnStart: `ConfigLoader.Load()` → create `AgentLogger` → create `ScannerManager` → create `HttpServer` → `HttpServer.Start()` → start 30s health-check `Timer` → start `ConfigFileWatcher` → write "Service started" to EventLog
  - OnStop: cancel `_cts` → `HttpServer.Stop()` (cancels CTS, closes listener, waits up to 5s for worker, waits up to 30s for in-flight) → dispose `ConfigFileWatcher` → dispose scanner → dispose health-check timer → `ZkTecoFingerHost.Close()` (static singleton teardown) → write "Service stopped" to EventLog

## Architectural Constraints

- **Threading:** `HttpServer` runs one long-running `Task.Factory.StartNew` worker on `TaskScheduler.Default` that loops `_listener.GetContextAsync()` and dispatches each request as a fire-and-forget task. Adapters honor an optional `CancellationToken` at SDK checkpoints (not mid-native-call). All shared mutable state uses explicit `lock` objects (`_adapterLock`, `_backoffLock`, `_inFlightLock`, `_corsLock`).
- **Global state:**
  - `ScannerManager._adapters`, `_activeAdapter`, `_backoffStep`, `_backoffUntil` (mutable, locked)
  - `CorsMiddleware._mode`, `_allowedOrigins` (mutable, locked; atomic HashSet swap)
  - `HttpServer._inFlightRequests`, `_cts`, `_workerTask` (mutable, locked)
  - `ZKTecoAdapter._hostLock` (static — process-wide serialization of `ZkTecoFingerHost.Initialize/Close`)
  - `FingerprintAgentService._configLock` (mutable; replaced under lock)
- **Circular imports:** None observed. Dependency direction is one-way: `Models` ← `Api` ← `Service`; `Configuration`/`Logging`/`Models` are leaves; `Adapters` consumes `Configuration` + `Logging`.
- **Vendor SDK presence is compile-time:** `FingerprintAgent.csproj` defines `ZKTECO_SDK_PRESENT`, `SECUGEN_SDK_PRESENT`, `DIGITALPERSONA_SDK_PRESENT`, `FUTRONIC_SDK_PRESENT` via `Condition="Exists('lib/<vendor>/<native>.dll')"`. Missing SDK → stub types + conditional compilation (e.g. `SecuGenAdapter.cs` defines stub `SGFingerPrintManager` under `#if !SECUGEN_SDK_PRESENT`). Real-device tests are skipped at runtime when vendor DLLs are absent (see `tests/FingerprintAgent.Tests/Scanner/ZkSdkProbe.cs`).
- **Process-wide x86 native SDKs:** `<PlatformTarget>x86</PlatformTarget>` and `<RuntimeIdentifier>win-x86</RuntimeIdentifier>` on the library — required because `libzkfp.dll` and `ftrScanAPI.dll` are x86-only. Service install path uses the x86 `FingerprintAgent.exe`.
- **No DI container despite NuGet reference:** `Microsoft.Extensions.DependencyInjection` is referenced but never called — composition is manual in `FingerprintAgentService.OnStart`. Intentional (per AGENTS.md: "All classes `new`'d directly").
- **Locale of wire fields:** DTO property names use Vietnamese business vocabulary (`thamChieuId`, `maPhieu`, `loaiKy`, `vaiKyId`, `nhanLucId`). The `[JsonProperty]` attributes pin the wire names; renaming requires updating both the property name and the attribute.

## Anti-Patterns

### Dual `Program.cs` (Dead Entry Point)

**What happens:** `src/FingerprintAgent/Program.cs` contains a full `Main` that calls `ServiceBase.Run(new FingerprintAgentService())` — identical in intent to `src/FingerprintAgent.Host/Program.cs`.
**Why it's wrong:** Confuses the build entry point. The library project emits `OutputType=Library` so this `Main` is never compiled into the assembly, but future maintainers may "fix" it thinking it runs. The AGENTS.md notes this explicitly ("Dead code — library entry, never runs").
**Do this instead:** All entry-point changes go to `src/FingerprintAgent.Host/Program.cs`. Consider deleting `src/FingerprintAgent/Program.cs` outright — it's dead and adds nothing the host doesn't already do.

### Bare `catch { }` Blocks

**What happens:** Multiple sites silently swallow exceptions: `FingerprintAgentService.cs:111` (OnStop disposal errors), `AgentLogger.cs:167` (event log write failures), `HttpServer.cs:88` (AggregateException drain wait), `FingerprintAgentService.cs:160` (`ZkTecoFingerHost.Close()` final fallback).
**Why it's wrong:** Hides unexpected failures from operators reading the log. Best-effort cleanup with no diagnostic is indistinguishable from successful cleanup.
**Do this instead:** Use `catch (Exception ex) { _logger?.Debug(null, $"<context>: {ex.GetType().Name}: {ex.Message}"); }` — keep the swallow, but record what happened so a Debug-level scrape can find it.

### Fire-and-Forget via `Task.Wait()`

**What happens:** `HttpServer.ProcessRequestLoop` does `_ = handlerTask.ContinueWith(...)` and `HttpServer.Stop` calls `_workerTask?.Wait(TimeSpan.FromSeconds(5))` and `Task.WaitAll(inFlight, TimeSpan.FromSeconds(30))` with bare `catch (AggregateException) { }`.
**Why it's wrong:** `Wait()` blocks a thread and may throw `ThreadAbortException` (also caught by the generic `catch (Exception)`), which terminates the appdomain. `AggregateException` is flattened before throw so the bare catch misses inner exceptions.
**Do this instead:** `await _workerTask` from an async `StopAsync`; or accept the fire-and-forget cost and log with `_logger?.Error(...)` if `Wait` actually throws (don't swallow silently).

### Configuration-Binder-Free Manual Bind

**What happens:** `ConfigLoader.BindConfig` reads every key via `GetString/GetInt/GetBool/GetStringArray` and assigns defaults manually — does not use `ConfigurationBinder.Bind`.
**Why it's wrong:** More code, more chances for typos in keys (`"http:port"` vs `"Http:Port"` matters in case-sensitive setups). Easy to forget a key when adding a new section.
**Do this instead:** For new sections, bind via `config.GetSection("x:y").Bind(obj)` and override defaults in `AgentConfig`'s property initializers. Reserve manual bind for cases where you need special array parsing.

## Error Handling

**Strategy:** `CaptureResult` carries an `ErrorCode` enum-string and `ErrorMessage`; never throw across the adapter→handler boundary. `ScannerManager` returns `CaptureResult.Fail("SCANNER_NOT_CONNECTED", ...)` after exhausting adapters and applying backoff. `CaptureHandler.MapErrorCode` translates codes to HTTP status (SCANNER_NOT_CONNECTED→503, CAPTURE_TIMEOUT→504, CAPTURE_FAILED→500, INVALID_REQUEST→400, CONFIG_ERROR→500, default→500 with code).

**Patterns:**
- **Adapters translate vendor errors** — every concrete adapter owns a `MapError(...)` (DigitalPersona `MapException`, Futronic `MapErrorCode`, SecuGen `MapError`, ZKTeco `_zkResponseStrings` dictionary) that produces a stable string code for `VendorErrorCode` and the body of `CaptureResponse.vendorErrorCode`.
- **Validation errors → 400** — `CaptureHandler` checks for empty body, JSON parse failure, missing `thamChieuId`, missing `maPhieu` — all return 400 with `ErrorCode = "INVALID_REQUEST"` and a human message.
- **Unhandled exceptions in `HandleRequestAsync`** — caught by a single `catch (Exception)` that returns 500 + closes the response (`src/FingerprintAgent/Api/HttpServer.cs:188-196`). The exception is NOT logged here; logged instead by the `.ContinueWith(t => { ... if (t.IsFaulted) _logger?.Error(...) ...})` wrapper that detects `t.IsFaulted`.
- **Shutdown errors are aggregated** — `FingerprintAgentService.OnStop` assigns the first exception to `shutdownError` and writes one final EventLog entry at the end (line 162-170).
- **Config reload errors are non-fatal** — `ConfigFileWatcher.OnDebounceElapsed` catches everything, logs an error, and keeps the old config in place (D-08).

## Cross-Cutting Concerns

**Logging:** `AgentLogger` is the sole logger. Every class accepts `AgentLogger` via constructor (optional, `_logger?.Info(...)` everywhere). Two sinks: append-only file (`StreamWriter` with `AutoFlush = true`) and Windows EventLog (`EventLog.WriteEntry("FingerprintAgent", ...)`). Correlation IDs are 10-char GUID hex prefixes (`GenerateCorrelationId`); every log entry is `{timestamp} [{LEVEL}] [{cid}] {message}`. **SEC-04 base64 redaction** — `RedactIfImageData` matches any 40+ char base64 substring and replaces with `[REDACTED: potential image data]` to prevent fingerprint image bytes from being written to disk logs.

**Validation:** Two layers:
- DTO-level (`StringLength(50)` attributes on `CaptureRequest` fields — declarative only, NOT enforced since `JsonConvert.DeserializeObject` ignores them by default; treat as documentation)
- Handler-level (`CaptureHandler` requires non-empty `thamChieuId` and `maPhieu`; rejects empty body and malformed JSON with 400 `INVALID_REQUEST`)

**Authentication:** None. The service binds to `127.0.0.1` only (`http://127.0.0.1:5043/`) and trusts the OS process boundary for caller identity. The `Security.BindIp` config field is read but not enforced by `HttpServer` — `_listener.Prefixes.Add($"http://{config.Http.Host}:{config.Http.Port}/")` uses `Http.Host` directly. CORS does provide an allowlist mode (`"allowlist"`) that 403s cross-origin preflights from disallowed origins.

---

*Architecture analysis: 2026-08-19*
