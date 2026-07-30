# Phase 3: Resilience & Runtime Reconfiguration - Context

**Gathered:** 2026-07-30
**Status:** Ready for planning

<domain>
## Phase Boundary

Phase 3 tập trung vào khả năng tự phục hồi của service khi scanner bị ngắt kết nối, reload cấu hình runtime khi `config.json` thay đổi, và xử lý lỗi capture rõ ràng. Phase này KHÔNG thay đổi adapter interface hay thêm adapter mới — chỉ mở rộng behavior của `ScannerManager` (backoff, health check) và thêm config reload mechanism.

</domain>

<decisions>
## Implementation Decisions

### Exponential Backoff (SCAN-06)

- **D-01:** Global shared backoff — một bộ đếm dùng chung cho tất cả adapter. Đơn giản, tránh race condition khi nhiều adapter cùng backoff độc lập.
- **D-02:** Schedule: 10s → 30s → 60s → 120s (max). Reset về 10s mỗi lần service restart.
- **D-03:** In-memory only — một biến `int currentBackoffStep` (~4 bytes RAM). Không lưu file.
- **D-04:** Hot-plug friendly: yêu cầu capture mới LUÔN kích hoạt thử kết nối ngay, không blocked bởi backoff. Backoff chỉ áp dụng khi yêu cầu đó CŨNG thất bại kết nối. Cắm scanner → dùng được ngay.
- **D-05:** Reset trigger: khi `Scan()` thành công (adapter trả về ảnh), reset `currentBackoffStep = 0`.

### Config Reload at Runtime (CFG-03)

- **D-06:** Chỉ reload `ScannerConfig` (priority, MockMode) và `CorsConfig` (mode, allowedOrigins). Không reload HTTP port/service name.
- **D-07:** `FileSystemWatcher` theo dõi `config.json`. Trigger reload khi file thay đổi.
- **D-08:** On bad config (syntax error / parse fails): giữ config cũ đang hoạt động, ghi error vào log. Service không crash.
- **D-09:** Active adapter giữ nguyên khi config reload — không recreate ScannerManager ngay. Priority mới áp dụng cho connection attempt tiếp theo khi adapter hiện tại fail.

### Error Code Mapping

- **D-10:** HTTP status codes:
  - `SCANNER_NOT_CONNECTED` → **503 Service Unavailable**
  - `CAPTURE_TIMEOUT` → **504 Gateway Timeout**
  - `CAPTURE_FAILED` (SDK error) → **500 Internal Server Error**
  - `INVALID_REQUEST` → **400 Bad Request**
- **D-11:** `VendorErrorCode` được include trong `errorMessage` của response JSON (để IT support debug qua logs, không expose cho end-user).
- **D-12:** Per-adapter error translation — mỗi adapter tự translate SDK error codes sang `CAPTURE_FAILED` / `CAPTURE_TIMEOUT`. ScannerManager nhận `CaptureResult` đã có error code chuẩn, không cần hiểu vendor-specific errors.
- **D-13:** Timeout enforced tập trung ở `ScannerManager` qua `CancelAfter(TimeSpan.FromSeconds(10))` cho total budget. Adapter không tự enforce timeout — chỉ trả kết quả hoặc throw.

### In-Flight Request Handling

- **D-14:** Yêu cầu capture đang xử lý mà scanner disconnect → fail ngay với 503 + `SCANNER_NOT_CONNECTED`. Client tự retry. Đơn giản, không blocking.

### Background Health Check Loop

- **D-15:** `System.Threading.Timer` kiểm tra `IsConnected` mỗi 30 giây. Nhẹ, đơn giản.
- **D-16:** Khi phát hiện disconnect (IsConnected = false): ghi log rõ ràng + bắt đầu exponential backoff. Không fail `/health` endpoint ngay — đợi backoff cycle để client có chance recover trước khi health endpoint trả 503.
- **D-17:** Health check timer chỉ kiểm tra state — không gọi `Initialize()` hay `Scan()`. Không trigger reconnection, chỉ observe.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

- `.planning/ROADMAP.md` §Phase 3 — goal, success criteria, deliverables
- `.planning/REQUIREMENTS.md` §SCAN-06, §CFG-03 — specific requirements for this phase
- `.planning/PROJECT.md` — core value, constraints, business context
- `.planning/phases/02-multi-vendor-scanner-adapters/02-CONTEXT.md` — prior decisions (ScannerManager, IScannerAdapter interface, capture timeout budget, adapter priority)
- `.planning/phases/01-foundation-windows-service-http-api-skeleton/01-CONTEXT.md` — foundational decisions (HttpListener, config.json structure, logging)
- `src/FingerprintAgent/Adapters/ScannerManager.cs` — existing implementation (to be extended with backoff + health check)
- `src/FingerprintAgent/Adapters/IScannerAdapter.cs` — interface (unchanged by Phase 3)
- `src/FingerprintAgent/Configuration/AgentConfig.cs` — config classes (ScannerConfig, CorsConfig affected by CFG-03 reload)

No external specs — requirements fully captured in decisions above.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ScannerManager` in `src/FingerprintAgent/Adapters/ScannerManager.cs` — already has basic single-retry backoff (lines 146-170). Phase 3 extends this with full exponential backoff.
- `CaptureResult.Fail()` — static factory method for creating error results (already used in ScannerManager)
- `AgentLogger` — already wired, used for logging connection attempts and errors
- `AgentConfig.Scanner` and `AgentConfig.Cors` — already exist in `AgentConfig.cs`

### Integration Points
- `FingerprintAgentService.OnStart`: currently creates `ScannerManager`. Phase 3 adds `FileSystemWatcher` and `Timer` here too.
- `HttpServer`: CorsMiddleware reads `AgentConfig.Cors` per-request. Need to refresh this on reload without rebuilding HttpServer.
- `CaptureHandler`: calls `scanner.Scan()`. Already returns CaptureResult — HTTP status mapping happens here.
- `HealthHandler`: uses `scanner.IsConnected`. Background timer updates state; HealthHandler reads it.
- `AgentConfig` reload: needs thread-safe read/write because FileSystemWatcher callback and request handlers access it concurrently.

### Patterns Established
- `ScannerManager.Scan()` returns `CaptureResult` — error code in `ErrorMessage`, HTTP status determined in `CaptureHandler`
- Per-capture lazy connect (D-01 from Phase 2)
- 10s total capture budget, ~3s per adapter (D-06 from Phase 2)

### Extension Points
- Add `currentBackoffStep` field to `ScannerManager` (int)
- Add `Timer` field to `FingerprintAgentService` for health check loop
- Add `ConfigReloadHandler` or similar for FileSystemWatcher callback
- `CorsMiddleware` needs to re-read `AgentConfig.Cors` on each request (not cache) for hot-reload to work

</code_context>

<specifics>
## Specific Ideas

- Exponential backoff implementation: `currentBackoffStep = Math.Min(currentBackoffStep + 1, 3)` where index maps to `[]{10, 30, 60, 120}` seconds.
- FileSystemWatcher on `config.json` with `NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size` to catch saves.
- Thread-safe config access: use `ReaderWriterLockSlim` or `volatile` + interlocked for backoff step, or simple lock since writes are infrequent.
- Health check timer callback: `if (!_activeAdapter.IsConnected) { _logger.Warn(...); StartBackoff(); }`
- Error code mapping table format: `Dictionary<VendorErrorCode, (HttpStatus, ServiceErrorCode)>` per adapter.

</specifics>

<deferred>
## Deferred Ideas

- **Config full reload (all sections)** — Có thể reload HTTP port/service name nếu cần trong tương lai. Hiện tại scope giới hạn ở scanner + CORS.
- **Backoff state persistence in file** — Nếu muốn backoff survive service crash/reboot, lưu vào `C:\ProgramData\FingerprintAgent\state.json`. Từ chối vì thêm complexity và không cần thiết cho v1.
- **Polling/WebSocket mode (Phase 5+)** — Backend SaaS push lệnh xuống agent qua NAT. Thuộc Phase 5+.

</deferred>

---

*Phase: 03-Resilience-Runtime-Reconfiguration*
*Context gathered: 2026-07-30*