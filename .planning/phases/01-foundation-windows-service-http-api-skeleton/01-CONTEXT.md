# Phase 01: Foundation — Windows Service + HTTP API skeleton - Context

**Gathered:** 2026-07-28
**Status:** Ready for planning

## Phase Boundary

Phase 1 xây dựng bộ khung Windows Service + HTTP API cho FingerprintAgent. Mục tiêu là agent có thể:
- Cài đặt và chạy như Windows Service trên Windows 10/11 (.NET Framework 4.8).
- Lắng nghe HTTP trên `localhost:5043` với endpoint `GET /health` và `POST /api/capture`.
- Trả về ảnh PNG mẫu (mock scanner) kèm SHA-256 hash cho `/api/capture`.
- Đọc cấu hình từ `config.json` (port, CORS mode, logging).
- Hỗ trợ CORS mặc định wildcard (`*`), có thể chuyển sang allowlist qua config.
- Ghi log vào file và Windows Event Log.

Phase này **không** bao gồm adapter thật cho SecuGen / Digital Persona / Futronic (Phase 2), cũng không bao gồm MSI installer hay auto-update (Phase 4).

## Implementation Decisions

### HTTP Listener Stack
- **D-01:** Dùng `System.Net.HttpListener` tự xử lý request/response. Không dùng OWIN/ASP.NET Web API 2 để tránh dependency nặng trên .NET Framework 4.8.
- **D-02:** Bind mặc định `127.0.0.1:5043`, có thể ghi đè qua `config.json` (`http.host`, `http.port`).

### Image Encoding (Mock + Chuyển đổi sau này)
- **D-03:** Dùng `System.Drawing` (GDI+) để tạo/tiếp nhận ảnh PNG. Phù hợp .NET Framework 4.8 Windows-only, không cần thêm NuGet.

### Logging
- **D-04:** Dùng `System.Diagnostics.Trace` + `EventLog.WriteEntry` tự xử lý. Không dùng Serilog/NLog để giữ package nhẹ.
- **D-05:** Log file đặt tại `C:\ProgramData\FingerprintAgent\Logs\agent.log` (hoặc đường dẫn cấu hình).

### Windows Service Hosting
- **D-06:** Dùng `ServiceBase` thuần (không dùng Topshelf) để phù hợp với mục tiêu MSI installer cuối cùng.
- **D-07:** Phase 1 cài đặt service qua PowerShell script tạm thời (`Install-Service.ps1`). MSI sẽ làm trong Phase 4.

### Project Structure
- **D-08:** Một project duy nhất cho Phase 1: `FingerprintAgent.csproj` chứa tất cả (Service host, HTTP listener, Mock adapter, Config, Logging).
- **D-09:** Tách adapter thật và project riêng sẽ làm từ Phase 2 trở đi.

### Dependency Injection / Configuration
- **D-10:** Dùng `Microsoft.Extensions.DependencyInjection` và `Microsoft.Extensions.Configuration.Json` qua NuGet trên .NET Framework 4.8.
- **D-11:** `config.json` là single source of truth; hỗ trợ reload khi file thay đổi (file watcher) từ Phase 3.

### CORS
- **D-12:** Mặc định trả về `Access-Control-Allow-Origin: *` cho mọi request, giống agent ký số hiện tại.
- **D-13:** Cấu hình `cors.mode` = `wildcard` | `allowlist`; nếu `allowlist` thì kiểm tra `Origin` header với `cors.allowedOrigins`.

### Request/Response Format
- **D-14:** `POST /api/capture` nhận JSON body với trường `thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`, `metadata`.
- **D-15:** Response JSON chứa `isSuccess`, `imageBytes` (base64 PNG), `mimeType`, `capturedAt`, `deviceId`, `verificationData` (SHA-256 base64), `errorMessage`.

### Mock Scanner
- **D-16:** `MockScannerAdapter` triển khai `IScannerAdapter`, tạo ảnh PNG gradient/placeholder, deviceId = `mock-scanner-001`.

### Agent's Discretion
- Cách tổ chức folder trong 1 project duy nhất (ví dụ `Adapters/`, `Api/`, `Configuration/`, `Logging/`, `Service/`).
- Chi tiết format log (timestamp, level, message, correlationId).
- Cách sinh PNG mock (gradient, noise, hoặc hình vuông màu xám).

## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project Context
- `.planning/PROJECT.md` — Ngữ cảnh dự án, core value, constraints, key decisions, out of scope.
- `.planning/REQUIREMENTS.md` — v1 requirements, bao gồm API-01..API-06, SVC-01..SVC-05, CFG-01..CFG-04, SEC-01..SEC-04, OBS-01..OBS-03, DEP-01..DEP-04.
- `.planning/ROADMAP.md` — Phase 1 goal, success criteria, deliverables, mapping requirements.
- `.planning/STATE.md` — Current focus là Phase 1.

### Existing Docs (from user, may need review/correction)
- `docs/ARCHITECTURE.md` — Kiến trúc đề xuất ban đầu (có thể điều chỉnh theo quyết định phase 1).
- `docs/DEVICE-COMPATIBILITY.md` — Thông tin SDK các hãng máy quét (dùng cho Phase 2+).
- `docs/REQUIREMENTS.md` — Requirements ban đầu (có thể điều chỉnh theo PROJECT.md đã cập nhật).

## Existing Code Insights

### Reusable Assets
- Chưa có codebase. Phase 1 xây dựng từ đầu.

### Established Patterns
- None — greenfield project.

### Integration Points
- Angular frontend sẽ gọi `http://localhost:5043/api/capture` cross-origin với CORS wildcard.
- Backend .NET API SaaS nhận kết quả từ Angular (không gọi agent trực tiếp).

## Specific Ideas

- Mô hình tích hợp giống agent ký số USB token hiện tại (`CAPluginService` gọi `localhost:8888/signhash`), nên CORS wildcard là chấp nhận được.
- Dùng .NET Framework 4.8 framework-dependent để gói cài đặt nhẹ, phù hợp máy cấu hình thấp.

## Deferred Ideas

- **MSI installer** — Thuộc Phase 4 Deployment.
- **Auto-update từ GitHub Release** — Thuộc Phase 4 Deployment.
- **Adapter thật SecuGen / Digital Persona / Futronic** — Thuộc Phase 2 Multi-vendor Scanner Adapters.
- **Config reload runtime / reconnect scanner / polling backend** — Thuộc Phase 3 Resilience & Runtime Reconfiguration.

---

*Phase: 01-Foundation — Windows Service + HTTP API skeleton*
*Context gathered: 2026-07-28*
