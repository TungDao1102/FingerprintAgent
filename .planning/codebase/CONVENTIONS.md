# Coding Conventions

**Analysis Date:** 2026-08-19

## Naming Patterns

**Files:**
- One type per file, filename matches class name (e.g., `ZKTecoAdapter.cs` contains `ZKTecoAdapter`)
- Subfolders group by module/responsibility: `Adapters/`, `Api/`, `Configuration/`, `Logging/`, `Models/`, `Service/`
- Test files mirror the same subfolder layout (`tests/FingerprintAgent.Tests/Api/`, `Configuration/`, `Logging/`, `Scanner/`)

**Namespaces:**
- `FingerprintAgent.<Module>` for all production code (e.g., `FingerprintAgent.Adapters`, `FingerprintAgent.Api`)
- `FingerprintAgent.Tests.<Module>` for tests (e.g., `FingerprintAgent.Tests.Scanner`)
- `<RootNamespace>` declared explicitly in `.csproj` files: `FingerprintAgent` (library + host) and `FingerprintAgent.Tests` (tests)

**Private fields:**
- `_camelCase` underscore prefix is mandatory — e.g., `_config`, `_logger`, `_cts`, `_activeAdapter`
- Inherited from `BaseScannerAdapter`: `protected string _lastError` (also uses underscore prefix even though protected)
- Always declared at top of class, grouped together before constructors/properties
- Reference: `src/FingerprintAgent/Adapters/ScannerManager.cs:30-40`, `src/FingerprintAgent/Api/HttpServer.cs:15-25`

**Public properties / methods / types:**
- `PascalCase` — `IsConnected`, `ScanAsync`, `CaptureResult`, `VendorErrorCode`, `BackoffStep`
- Interface members use `I` prefix: `IScannerAdapter`
- Boolean getters/properties named positively: `IsConnected`, `IsSuccess`, `InBackoff`, `MockMode`

**Constants and static readonly:**
- `PascalCase` — `BackoffDelaysSeconds`, `Base64Pattern`, `ErrorStrings`
- `static readonly` arrays initialized inline — `private static readonly int[] BackoffDelaysSeconds = { 10, 30, 60, 120 };` (`src/FingerprintAgent/Adapters/ScannerManager.cs:40`)
- `static readonly` dictionaries used for error-string lookup tables (e.g., `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:50-79`, `src/FingerprintAgent/Adapters/SecuGenAdapter.cs:41-60`, `src/FingerprintAgent/Adapters/DigitalPersonaAdapter.cs:31-38`)

**Locks:**
- Always declared `private readonly object _lockName = new object();`
- Naming: `_adapterLock`, `_backoffLock`, `_corsLock`, `_inFlightLock`, `_configLock`, `_lock`, `_hostLock`
- All locks take `new object()` initializer (not `Monitor`-style)
- Reference: `src/FingerprintAgent/Adapters/ScannerManager.cs:35,39`, `src/FingerprintAgent/Api/CorsMiddleware.cs:11`

**Static factories:**
- `CaptureResult.Ok(byte[] imageBytes, string mimeType = "image/png", string deviceId = null)` — factory named `Ok` for success path (`src/FingerprintAgent/Adapters/CaptureResult.cs:18`)
- `CaptureResult.Fail(string errorCode, string message)` — factory named `Fail` for error path (`src/FingerprintAgent/Adapters/CaptureResult.cs:36`)

**Correlation IDs:**
- Generated via `AgentLogger.GenerateCorrelationId()` — returns `Guid.NewGuid().ToString("N").Substring(0, 10)` (10 lowercase hex chars, no dashes) (`src/FingerprintAgent/Logging/AgentLogger.cs:53-56`)
- Variables holding a correlation id use short suffixes: `cid`, `startCid`, `stopCid`, `correlationId`
- Always passed as the first parameter to logger methods: `_logger?.Info(correlationId, "...")`
- Pass `null` when no correlation context is available (e.g., background health-check callback) — logger substitutes `"-"` for null

**Guard clauses (null-checks):**
- Use C# 7 throw-expression pattern: `_config = config?.Scanner ?? throw new ArgumentNullException(nameof(config));` (`src/FingerprintAgent/Adapters/ScannerManager.cs:174`)
- Or `if (config == null) throw new ArgumentNullException(nameof(config));` (`src/FingerprintAgent/Logging/AgentLogger.cs:34`)
- Always reference the parameter name via `nameof(...)`

**Mock naming:**
- Real adapter mocks used in production defaults: `"mock-device"`, `"mock-scanner-001"`, `"Mock Scanner v1.0"`
- Test doubles declared in `MockScannerAdapterTestDoubles.cs`

## Code Style

**Formatting:**
- 4-space indentation (no tabs in production code)
- Allman-style braces (opening brace on its own line for types, methods, control flow)
- Single-line `lock` blocks permitted: `lock (_adapterLock) return _activeAdapter;` (`src/FingerprintAgent/Adapters/ScannerManager.cs:44`)
- One statement per line; no semicolon-prefixed statement stacking

**Linting:**
- No `.editorconfig` present in the repo
- No `stylecop.json` or `stylecop.*` files
- No SonarLint / Roslyn analyzer configuration
- Build is configured with `<LangVersion>8.0</LangVersion>` and `<Nullable>enable</Nullable>` is **NOT** set (`src/FingerprintAgent/FingerprintAgent.csproj:4`)
- The only pre-existing test warning is `xUnit1031` (2 occurrences) for `MockScannerAdapterWithSettableProperties` not implementing `Task<CaptureResult> ScanAsync` (it does, but with a different signature `Scan(...)`)

**XML documentation:**
- Use `/// <summary>` doc comments on public types and methods that need design rationale
- Multi-line summaries preferred over single-line
- Reference: `src/FingerprintAgent/Adapters/IScannerAdapter.cs:12-42`, `src/FingerprintAgent/Adapters/ScannerManager.cs:10-26,84-98,147-152,166-171,199-202,212-222,276-286`

## Import Organization

**Order (alphabetical within group, .NET Framework convention):**
1. `System.*` namespaces (e.g., `System`, `System.Collections.Generic`, `System.Threading.Tasks`)
2. External NuGet namespaces (e.g., `Newtonsoft.Json`, `Microsoft.Extensions.Configuration`, `DPFP`, `ZkTecoFingerPrint`)
3. Internal `FingerprintAgent.*` namespaces

**Path aliases:**
- No `using static` directives
- No global usings (file-scoped namespaces not used in this codebase — all types use traditional block-scoped `namespace { ... }`)
- File-scoped `#nullable enable` is used in **one** file only (`src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:1`) — adopted as the file most exposed to nullable warnings due to the wrapper SDK's response model

## Error Handling

**Strategy:**
- Top-of-method guard clauses for invalid input
- Vendor-specific errors mapped to `CaptureResult` with two fields: `ErrorCode` (transport-level, drives HTTP status mapping) and `VendorErrorCode` (SDK-specific, surfaced in response)
- HTTP status mapping lives in `CaptureHandler.MapErrorCode` (`src/FingerprintAgent/Api/CaptureHandler.cs:122-139`):
  - `SCANNER_NOT_CONNECTED` → 503
  - `CAPTURE_TIMEOUT` → 504
  - `INVALID_REQUEST` → 400
  - `CAPTURE_FAILED` → 500
  - `CONFIG_ERROR` → 500
- Service-level fatal errors are written to `EventLog` via `TryWriteEventLog` (`src/FingerprintAgent/Service/FingerprintAgentService.cs:186-200`) and then re-thrown so the SCM stops the service

**Patterns:**
- **Adapter exceptions → CaptureResult.Fail:** Catch in `ScanAsync`, set `_vendorErrorCode`, return `CaptureResult.Fail(...)`. Never rethrow from adapter — `ScannerManager` iterates adapters and must catch per-adapter exceptions to continue (see `ScannerManager.cs:138-142, 359-362`)
- **Fail-fast on config typos:** `ScannerManager.CreateAdapter` throws `InvalidOperationException` for unknown vendor names (`src/FingerprintAgent/Adapters/ScannerManager.cs:270-273`)
- **Config reload resilience:** `ConfigFileWatcher.OnDebounceElapsed` catches all exceptions, logs, keeps old config — does NOT throw (`src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:74-78`)
- **Bare `catch { }` blocks (acknowledged anti-pattern, see AGENTS.md):**
  - `src/FingerprintAgent/Adapters/ZKTecoAdapter.cs:101, 120, 383` — wrapping native SDK calls that may throw `Exception` from unmanaged code
  - `src/FingerprintAgent/Service/FingerprintAgentService.cs:160` — wrapping `ZkTecoFingerHost.Close()` at service shutdown
  - `src/FingerprintAgent/Configuration/ConfigFileWatcher.cs:87, 88, 94` — wrapping disposal of `Timer` and `FileSystemWatcher`
- **Specific catch order:** `ObjectDisposedException`, `HttpListenerException`, `OperationCanceledException` handled specifically in `HttpServer.ProcessRequestLoop` (`src/FingerprintAgent/Api/HttpServer.cs:122-133`); `AggregateException` swallowed when draining in-flight requests (`HttpServer.cs:87, 97`)

## Logging

**Framework:** Custom `AgentLogger` (`src/FingerprintAgent/Logging/AgentLogger.cs`) — no Microsoft.Extensions.Logging abstraction used despite `Microsoft.Extensions.DependencyInjection` being referenced (but unused per AGENTS.md).

**Sinks:**
1. **File sink** — `StreamWriter` writing to a configured path (`C:\ProgramData\FingerprintAgent\Logs\agent.log` by default), appended, write-locked via `_lock`
2. **EventLog sink** — `EventLog.WriteEntry("FingerprintAgent", ...)` (`AgentLogger.cs:162`) — best-effort, SecurityException swallowed

**Log levels:** `Debug` < `Info` < `Warn` < `Error` — filter compares `LogLevel` enum at `Write` (`AgentLogger.cs:88-93`).

**Patterns:**
- **Logger null-conditional:** `_logger?.Info(...)` — used everywhere because logger is optional in most constructors (e.g., `CaptureHandler(AgentLogger logger = null)`)
- **Structured format:** `{ISO-8601 timestamp} [{LEVEL}] [{correlationId}] {message}` (`AgentLogger.cs:103`)
- **Base64 redaction:** Any log message containing 40+ consecutive base64-valid characters is replaced with `[REDACTED: potential image data]` (`AgentLogger.cs:22-24, 114-128`) — prevents accidental fingerprint data leakage in logs
- **AutoFlush:** `StreamWriter.AutoFlush = true` for crash durability (`AgentLogger.cs:50`)
- **Correlation ID propagation:** Every request handler creates a correlation ID at entry, passes it through logger calls, returns it in `X-Correlation-Id` if needed
- **Console logging:** Only used in `Program.cs` for human-facing startup/shutdown messages; production code uses `Debug.WriteLine` only as last-resort fallback when logger itself is being disposed

## Comments

**When to Comment:**
- Public types and methods carry `/// <summary>` with rationale, not just summary
- Long, complex blocks (e.g., `ScannerManager.ScanAsync`, `ZKTecoAdapter.EnsureHostInitialized`) include inline `//` comments explaining non-obvious decisions
- Reference comments to design docs (e.g., `D-06`, `D-12`, `SCAN-06`, `SCAN-10`) link code to requirement IDs — searchable via `git grep "D-06"`
- Vendor SDK quirks documented inline with SDK parameter codes (e.g., `ZKTecoAdapter.cs:125-128`)

**JSDoc/TSDoc:**
- N/A — this is C# (no JSDoc/TSDoc)
- XML doc comments use `<summary>`, `<param>`, `<returns>` tags where helpful
- `/// <inheritdoc />` not used

**TODO/FIXME:**
- Only one `TODO` in production code: `src/FingerprintAgent/Adapters/FutronicAdapter.cs:16` — pre-production pixel-inversion verification gap
- No `FIXME`, `HACK`, or `XXX` markers in production code

## Function Design

**Size:** No enforced limit; largest functions are `ScannerManager.ScanAsync` (~90 lines) and `ZKTecoAdapter.InitializeInternal` (~70 lines). Most functions fit on one screen.

**Parameters:**
- Cancellation token always last with default value: `CancellationToken cancellationToken = default`
- `IScannerAdapter` is the primary abstraction passed between API and scanner layers
- Configuration classes passed by reference (no `in` parameters used)

**Return Values:**
- Async methods return `Task<T>` where `T` is a value type or `CaptureResult`
- `ScanAsync` returns `Task<CaptureResult>` — never throws; failures encoded as `CaptureResult.Fail(...)`
- `Task.FromResult(...)` is used to wrap synchronous results for the async signature (`BaseScannerAdapter.ScanAsync`, `MockScannerAdapter.ScanAsync`, `FutronicAdapter.ScanAsync`)

## Module Design

**Exports:**
- Public surface: classes implementing `IScannerAdapter` (5 production adapters + 1 test double), HTTP handlers, configuration classes, logger
- Internal types declared `internal` (e.g., `FingerprintAgent.Program`)
- Stub adapter classes guarded by `#if !XYZ_SDK_PRESENT` — the file ships both real and stub implementations under conditional compilation (`src/FingerprintAgent/Adapters/FutronicAdapter.cs:1,281`, `DigitalPersonaAdapter.cs:1,257`)

**Barrel Files:**
- None — every type lives in its own file

**File-scoped namespace:**
- NOT used — all files use block-scoped `namespace { ... }` with explicit braces

**InternalsVisibleTo:**
- `<InternalsVisibleTo>FingerprintAgent.Tests</InternalsVisibleTo>` declared in `src/FingerprintAgent/FingerprintAgent.csproj:8` — but no `internal` members are actually exercised by tests (test code accesses only `public` surface)

**Constructor patterns:**
- Public constructor with required dependencies
- Optional `logger = null` parameter on HTTP handlers, adapters, and managers
- ScannerManager has a second internal constructor for tests that takes pre-built adapter array (bypasses config-based resolution): `ScannerManager(IScannerAdapter[] adapters, AgentLogger logger)` (`src/FingerprintAgent/Adapters/ScannerManager.cs:203-210`)
- HttpServer retains a legacy `(string host, int port, IScannerAdapter scanner)` constructor for backward compatibility (`src/FingerprintAgent/Api/HttpServer.cs:40-46`)

**`#if` Conditional compilation:**
- Three SDK presence flags driven by MSBuild conditions: `ZKTECO_SDK_PRESENT`, `SECUGEN_SDK_PRESENT`, `DIGITALPERSONA_SDK_PRESENT`, `FUTRONIC_SDK_PRESENT` (declared in `src/FingerprintAgent/FingerprintAgent.csproj:17-31`)
- Each SDK-gated adapter file ships a stub implementation in the `#else` branch — allows `dotnet build` and `dotnet test` to succeed on machines without vendor SDKs

## Commit Convention

**Format:** `<type>(<phase-number>): <description>`

**Types:**
- `feat` — new feature
- `docs` — documentation only
- `test` — test additions/corrections
- `fixup` — minor fix-up commit (often auto-generated during phase execution)

**Examples (from AGENTS.md):**
- `feat(03-01): add exponential backoff`
- `docs(03-02): update configuration docs`

## Architectural Constraints (informs coding style)

- **No DI container:** `Microsoft.Extensions.DependencyInjection` package is referenced but unused; all classes are `new`'d directly (e.g., `FingerprintAgentService.OnStart` at `src/FingerprintAgent/Service/FingerprintAgentService.cs:55-57`)
- **Nullable not enforced project-wide:** `null` is a normal value; defensive `_logger?.Info(...)` pattern is the substitute
- **LangVersion 8.0:** switch expressions, tuples, out vars, throw-expressions, `nameof` are all available; records, init-only, primary constructors are NOT used
- **x86 only:** `<PlatformTarget>x86</PlatformTarget>` + `<RuntimeIdentifier>win-x86</RuntimeIdentifier>` — all P/Invoke and vendor SDK calls assume 32-bit
- **Static singleton teardown:** `ZkTecoFingerHost.Close()` is a process-wide teardown that affects all ZKTeco sessions; called once at service shutdown (`FingerprintAgentService.cs:160`), NOT inside `ZKTecoAdapter.Dispose()`

---

*Convention analysis: 2026-08-19*
