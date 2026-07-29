# Roadmap: FingerprintAgent

**Project:** FingerprintAgent — Local Fingerprint Capture Service
**Mode:** mvp
**Granularity:** standard
**Created:** 2026-07-28

## Phase 1: Foundation — Windows Service + HTTP API skeleton

**Goal:** Agent chạy được như Windows Service, phản hồi `/health` và `/api/capture` với mock scanner, có cấu hình `config.json` + CORS.

**Mode:** mvp

**Success Criteria:**
1. Có thể cài đặt service `FingerprintAgent` trên Windows 10/11 bằng PowerShell script tạm thời (MSI sẽ làm ở Phase 4).
2. Service khởi động thành công và `GET /health` trả về 200 với thông tin uptime/scanner.
3. `POST /api/capture` trả về ảnh PNG mẫu (mock) kèm SHA-256 hash hợp lệ.
4. `config.json` cấu hình được port, CORS mode, logging.
5. CORS từ chối origin không nằm trong danh sách cho phép khi ở chế độ allowlist; chấp nhận mọi origin khi ở chế độ wildcard.

**Requirements Covered:**
- API-01, API-02, API-03, API-04, API-05, API-06
- SVC-01, SVC-02, SVC-03, SVC-04, SVC-05
- CFG-01, CFG-02, CFG-04
- SEC-01, SEC-02, SEC-03, SEC-04
- OBS-01, OBS-02, OBS-03

**Deliverables:**
- `.csproj` + solution .NET Framework 4.8
- Windows Service entry point + lifecycle (ServiceBase)
- HTTP listener với routing `/api/capture`, `/health`
- MockScannerAdapter triển khai `IScannerAdapter`
- Configuration provider + file schema
- Logging sink (file + Event Log)
- `Install-Service.ps1`, `Uninstall-Service.ps1`, `Test-Capture.ps1` (tạm thời cho dev/test; MSI cho end user ở Phase 4)

**Plan Progress:**

| Plan | Title | Status | Completed |
|------|-------|--------|-----------|
| 01   | Walking Skeleton Core | ✅ Complete | 2026-07-28 |
| 02   | Configuration + CORS | ✅ Complete | 2026-07-28 |
| 03   | Windows Service Mode | ✅ Complete | 2026-07-28 |
| 04   | Logging & Observability | ✅ Complete | 2026-07-28 |
| 05   | PowerShell Scripts | ✅ Complete | 2026-07-28 |

---

## Phase 2: Multi-vendor Scanner Adapters

**Goal:** Agent kết nối và quét được ít nhất 3 hãng máy quét vân tay: SecuGen, Digital Persona, Futronic.

**Mode:** mvp

**Success Criteria:**
1. SecuGen FDx SDK Pro adapter khởi tạo, quét và trả về PNG bytes.
2. Digital Persona U.are.U SDK adapter khởi tạo, quét và trả về PNG bytes.
3. Futronic Standard SDK adapter (P/Invoke x86) khởi tạo, quét và trả về PNG bytes.
4. `ScannerManager` chọn adapter theo cấu hình ưu tiên và fallback khi một adapter thất bại.
5. Mỗi adapter xử lý đúng lỗi SDK và chuyển thành error response chuẩn.

**Requirements Covered:**
- SCAN-01, SCAN-02, SCAN-03, SCAN-04, SCAN-05, SCAN-07

**Deliverables:**
- `IScannerAdapter` interface trong Core
- `SecuGenAdapter` triển khai
- `DigitalPersonaAdapter` triển khai
- `FutronicAdapter` triển khai
- `AdapterFactory` + `ScannerManager`
- Native P/Invoke declarations cho Futronic
- Adapter-specific setup notes (SCANNER_SETUP.md)

**Plans:**
- [ ] 02-01-PLAN.md — IScannerAdapter extension + SecuGenAdapter (SCAN-01, SCAN-05)
- [ ] 02-02-PLAN.md — DigitalPersonaAdapter + FutronicAdapter (SCAN-02, SCAN-03, SCAN-07)
- [ ] 02-03-PLAN.md — ScannerManager + wiring (SCAN-04, SCAN-05)

---

## Phase 3: Resilience & Runtime Reconfiguration

**Goal:** Service tự phục hồi khi scanner mất kết nối, hỗ trợ reload cấu hình runtime, và xử lý lỗi capture rõ ràng.

**Mode:** mvp

**Success Criteria:**
1. Khi máy quét bị rút USB, adapter chuyển sang disconnected và trả về lỗi `SCANNER_NOT_CONNECTED`.
2. Service tự động retry kết nối với lịch exponential backoff (10s, 30s, 60s, 120s).
3. Thay đổi `config.json` khi service đang chạy → reload cấu hình mà không restart service.
4. Capture timeout trả về HTTP 504 + `CAPTURE_TIMEOUT`.
5. SDK error trả về HTTP 500 + `CAPTURE_FAILED` với message rõ ràng.

**Requirements Covered:**
- SCAN-06
- CFG-03

**Deliverables:**
- Background health check / reconnect loop
- FileSystemWatcher + config reload
- Capture timeout handling trong adapter
- Error code mapping table
- Unit/integration tests cho ScannerManager và error flows

---

## Phase 4: Deployment & End-to-End Validation

**Goal:** Package cài đặt nhẹ, script hoàn chỉnh, và luồng end-to-end từ Angular → Agent → Backend hoạt động trên máy bệnh viện thực tế.

**Mode:** mvp

**Success Criteria:**
1. Tạo được MSI installer từ build script (WiX Toolset hoặc WixSharp).
2. MSI cài đặt service, thư mục log, và VC++ redist x86 silently nếu thiếu.
3. MSI gỡ cài đặt sạch sẽ (dừng service, xóa files, xóa service).
4. `Test-Capture.ps1` gọi `/api/capture` và trả về ảnh base64.
5. Angular client gọi `localhost:5043/api/capture` từ domain SaaS (với CORS đã cấu hình) và gửi kết quả về backend.
6. Không có crash khi scanner không cắm — service vẫn healthy.

**Requirements Covered:**
- DEP-01, DEP-02, DEP-03, DEP-04

**Deliverables:**
- Build script tạo MSI (WiX/WixSharp)
- `Install-Service.ps1`, `Uninstall-Service.ps1`, `Test-Capture.ps1` cho dev/test
- Hướng dẫn triển khai cho IT bệnh viện
- Integration test từ browser → agent
- `README.md` + `DEPLOYMENT.md`
- Cơ chế auto-update từ GitHub Release (updater helper hoặc built-in)

---

## Milestone: v1.0 Release

**Goal:** FingerprintAgent v1.0 sẵn sàng triển khai cho HIS SaaS, hỗ trợ 3 hãng máy quét, và có thể tái sử dụng cho các ứng dụng web khác qua cấu hình `allowedOrigins`.

**Definition of Done:**
- Tất cả v1 requirements đã được triển khai và verify.
- PowerShell scripts hoạt động trên Windows 10/11 sạch.
- Manual test với ít nhất 1 máy quét thật (ưu tiên SecuGen).
- Docs cho IT triển khai hoàn chỉnh.
- Không có lỗi nghiêm trọng trong event log sau 24h chạy idle.

---

## Phase 5+ (Future / v1.1+)

- **Polling/WebSocket mode** để backend SaaS có thể đẩy yêu cầu quét xuống agent qua NAT/firewall.
- **Plugin adapter** cho hãng thứ 4+ (ZKTeco, v.v.).
- **ANSI/ISO template conversion** khi SDK hỗ trợ.
- **Code signing certificate** để giảm cảnh báo SmartScreen khi cài MSI.
- **Advanced auto-update** (delta update, rollback, channel preview/stable).

---
*Roadmap created: 2026-07-28*
*Last updated: 2026-07-28 after initial roadmap creation*
