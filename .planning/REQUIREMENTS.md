# Requirements: FingerprintAgent

**Defined:** 2026-07-28
**Core Value:** Agent luôn sẵn sàng trên máy bệnh viện, kết nối được ít nhất một trong các máy quét vân tay phổ biến, và trả về ảnh PNG đáng tin cậy cho ứng dụng web qua HTTP API địa phương.

## v1 Requirements

### Scanner Adapter (SCAN)

- [ ] **SCAN-01**: Agent phát hiện và khởi tạo máy quét SecuGen thông qua SecuGen FDx SDK Pro.
- [ ] **SCAN-02**: Agent phát hiện và khởi tạo máy quét Digital Persona thông qua U.are.U SDK.
- [ ] **SCAN-03**: Agent phát hiện và khởi tạo máy quét Futronic thông qua Futronic Standard SDK (P/Invoke x86).
- [ ] **SCAN-04**: Agent chọn máy quét hoạt động theo thứ tự ưu tiên cấu hình (SecuGen → Digital Persona → Futronic → auto).
- [ ] **SCAN-05**: Adapter chung `IScannerAdapter` định nghĩa contract cho mọi hãng máy quét.
- [ ] **SCAN-06**: Khi máy quét bị ngắt kết nối, adapter đánh dấu `IsConnected = false` và thử kết nối lại theo lịch backoff.
- [ ] **SCAN-07**: Mỗi adapter trả về ảnh PNG byte[] từ SDK raw image; phân giải/hệ màu do SDK quyết định.

### HTTP API (API)

- [ ] **API-01**: Agent mở HTTP endpoint `POST /api/capture` trên `localhost:5043`.
- [ ] **API-02**: Request body chấp nhận JSON với các trường `thamChieuId`, `maPhieu`, `loaiPhieu`, `vaiKyId`, `nhanLucId`, `metadata`.
- [ ] **API-03**: Response trả về JSON chứa `isSuccess`, `imageBytes` (base64 PNG), `mimeType`, `capturedAt`, `deviceId`, `verificationData` (SHA-256 base64), `errorMessage`.
- [ ] **API-04**: Response lỗi trả về HTTP 400/503/504 với `errorCode` rõ ràng (`SCANNER_NOT_CONNECTED`, `CAPTURE_TIMEOUT`, `CAPTURE_FAILED`, `INVALID_REQUEST`).
- [ ] **API-05**: Agent hỗ trợ CORS với danh sách `allowedOrigins` cấu hình trong `config.json`.
- [ ] **API-06**: Agent cung cấp `GET /health` trả về trạng thái service, scanner đã kết nối, model, uptime.

### Windows Service (SVC)

- [ ] **SVC-01**: Agent cài đặt và chạy như Windows Service với tên `FingerprintAgent`.
- [ ] **SVC-02**: Service tự khởi động cùng Windows (Auto Start).
- [ ] **SVC-03**: Service khởi tạo adapter và HTTP listener trong `OnStart`, dọn dẹp trong `OnStop`.
- [ ] **SVC-04**: Service ghi log vào Windows Event Log và file log theo cấu hình.
- [ ] **SVC-05**: Service chạy được dưới tài khoản LocalSystem (mặc định).

### Configuration (CFG)

- [ ] **CFG-01**: Agent đọc cấu hình từ `config.json` trong thư mục cài đặt.
- [ ] **CFG-02**: Cấu hình hỗ trợ các mục: service name, HTTP host/port, allowedOrigins, scanner list, backend (nếu cần polling mode v2), logging, security.
- [ ] **CFG-03**: Agent tải lại cấu hình khi file thay đổi (file watcher) mà không cần restart service.
- [ ] **CFG-04**: Nếu `config.json` thiếu hoặc không hợp lệ, service ghi lỗi rõ ràng và dừng khởi động.

### Security (SEC)

- [ ] **SEC-01**: HTTP endpoint mặc định bind `127.0.0.1`; cho phép ghi đè IP LAN nếu cấu hình rõ ràng.
- [ ] **SEC-02**: CORS chỉ cho phép origin nằm trong `allowedOrigins`; không cho phép wildcard `*`.
- [ ] **SEC-03**: Agent không lưu trữ ảnh vân tay, template, hoặc credential trên đĩa.
- [ ] **SEC-04**: Log không chứa dữ liệu vân tay; chỉ ghi metadata (deviceId, timestamp, error code).

### Observability (OBS)

- [ ] **OBS-01**: Agent ghi log structured với timestamp, level, message, correlationId.
- [ ] **OBS-02**: Agent ghi log các sự kiện: startup, scanner connect/disconnect, capture request/result, errors.
- [ ] **OBS-03**: `GET /health` trả về 200 khi service healthy và scanner connected; 503 khi scanner disconnected.

### Deployment (DEP)

- [ ] **DEP-01**: Package cài đặt chỉ chứa exe, adapter DLL, SDK DLL cần thiết, `config.json`, PowerShell scripts.
- [ ] **DEP-02**: PowerShell script `Install-Service.ps1` tạo Windows Service và thư mục log.
- [ ] **DEP-03**: PowerShell script `Uninstall-Service.ps1` dừng và xóa service.
- [ ] **DEP-04**: PowerShell script `Test-Capture.ps1` gọi `/api/capture` để kiểm tra nhanh.

## v2 Requirements

### Scanner Adapter

- **SCAN-08**: Hỗ trợ thêm hãng máy quét thứ 4 trở đi (ví dụ ZKTeco) thông qua plugin adapter.
- **SCAN-09**: Hỗ trợ chế độ multi-scanner (nhiều máy quét cùng lúc, chọn theo deviceId).
- **SCAN-10**: Chuyển đổi ảnh sang định dạng chuẩn ANSI 378 / ISO 19794-2 nếu SDK hỗ trợ.

### HTTP API

- **API-07**: Endpoint `POST /configure` để reload cấu hình từ xa (với xác thực).
- **API-08**: Hỗ trợ WebSocket/SocketMode để backend SaaS có thể đẩy lệnh quét xuống agent qua NAT/firewall.

### Authentication / Multi-tenant

- **SEC-05**: API key hoặc JWT để xác thực agent với backend SaaS (khi cần polling mode).
- **SEC-06**: Phân biệt tenant thông qua tenantId trong cấu hình agent.

### Installer

- **DEP-05**: Tạo MSI installer với giao diện cấu hình cơ bản và cài VC++ redist nếu thiếu.
- **DEP-06**: Cơ chế auto-update agent khi có phiên bản mới.

## Out of Scope

| Feature | Reason |
|---------|--------|
| Fingerprint matching / verification (1:1, 1:N) | Backend HIS chịu trách nhiệm so khớp template |
| Lưu trữ ảnh vân tay hoặc template | Agent chỉ truyền dữ liệu qua bộ nhớ trong |
| Windows 7 32-bit chính thức | Giảm phức tạp; tập trung Windows 10/11 + .NET Framework 4.8 |
| Giao diện người dùng capture confirmation | Frontend Angular xử lý UX; agent chỉ cung cấp API |
| Multiple concurrent capture requests | Xử lý tuần tự trong v1 để tránh xung đột thiết bị |
| Backend poll mode | V1 dùng Angular gọi agent trực tiếp; poll mode chuyển v2 |
| PKI / certificate management | Thuộc về ký số backend, không phải capture agent |
| Image quality scoring nâng cao | Chỉ trả ảnh gốc; backend tự đánh giá chất lượng |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| SCAN-01 | Phase 2 | Pending |
| SCAN-02 | Phase 2 | Pending |
| SCAN-03 | Phase 2 | Pending |
| SCAN-04 | Phase 2 | Pending |
| SCAN-05 | Phase 1 | Pending |
| SCAN-06 | Phase 3 | Pending |
| SCAN-07 | Phase 2 | Pending |
| API-01 | Phase 1 | Pending |
| API-02 | Phase 1 | Pending |
| API-03 | Phase 1 | Pending |
| API-04 | Phase 1 | Pending |
| API-05 | Phase 1 | Pending |
| API-06 | Phase 1 | Pending |
| SVC-01 | Phase 1 | Pending |
| SVC-02 | Phase 1 | Pending |
| SVC-03 | Phase 1 | Pending |
| SVC-04 | Phase 1 | Pending |
| SVC-05 | Phase 1 | Pending |
| CFG-01 | Phase 1 | Pending |
| CFG-02 | Phase 1 | Pending |
| CFG-03 | Phase 3 | Pending |
| CFG-04 | Phase 1 | Pending |
| SEC-01 | Phase 1 | Pending |
| SEC-02 | Phase 1 | Pending |
| SEC-03 | Phase 1 | Pending |
| SEC-04 | Phase 1 | Pending |
| OBS-01 | Phase 1 | Pending |
| OBS-02 | Phase 1 | Pending |
| OBS-03 | Phase 1 | Pending |
| DEP-01 | Phase 4 | Pending |
| DEP-02 | Phase 4 | Pending |
| DEP-03 | Phase 4 | Pending |
| DEP-04 | Phase 4 | Pending |

**Coverage:**
- v1 requirements: 34 total
- Mapped to phases: 34
- Unmapped: 0 ✓

---
*Requirements defined: 2026-07-28*
*Last updated: 2026-07-28 after initial definition*
