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
| 03   | 3/3 | Complete   | 2026-07-28 |
| 04   | Logging & Observability | ✅ Complete | 2026-07-28 |
| 05   | PowerShell Scripts | ✅ Complete | 2026-07-28 |

---

## Phase 2: Multi-vendor Scanner Adapters

**Goal:** Agent kết nối và quét được ít nhất 4 hãng máy quét vân tay: SecuGen, Digital Persona, Futronic, ZKTeco.

**Mode:** mvp

**Success Criteria:**

1. SecuGen FDx SDK Pro adapter khởi tạo, quét và trả về PNG bytes.
2. Digital Persona U.are.U SDK adapter khởi tạo, quét và trả về PNG bytes.
3. Futronic Standard SDK adapter (P/Invoke x86) khởi tạo, quét và trả về PNG bytes.
4. ZKTeco adapter (ZkTecoFingerPrint NuGet) khởi tạo, quét và trả về PNG bytes.
5. `ScannerManager` chọn adapter theo cấu hình ưu tiên và fallback khi một adapter thất bại.
6. Mỗi adapter xử lý đúng lỗi SDK và chuyển thành error response chuẩn.

**Requirements Covered:**

- SCAN-01, SCAN-02, SCAN-03, SCAN-04, SCAN-05, SCAN-07, SCAN-08, SCAN-09, SCAN-10

**Deliverables:**

- `IScannerAdapter` interface trong Core (extended with Initialize + VendorErrorCode)
- `SecuGenAdapter` triển khai
- `DigitalPersonaAdapter` triển khai
- `FutronicAdapter` triển khai
- `ZKTecoAdapter` triển khai (ZkTecoFingerPrint NuGet)
- `ScannerManager` (composite IScannerAdapter, priority fallback)
- Native P/Invoke declarations cho Futronic
- Adapter-specific setup notes (SCANNER_SETUP.md)

**Plans:**

- [ ] 02-01-PLAN.md — IScannerAdapter extension + SecuGenAdapter (SCAN-01, SCAN-05)
- [ ] 02-02-PLAN.md — DigitalPersonaAdapter + FutronicAdapter (SCAN-02, SCAN-03, SCAN-07)
- [ ] 02-03-PLAN.md — ScannerManager + wiring (SCAN-04, SCAN-05)
- [ ] 02-04-PLAN.md — ZKTecoAdapter (SCAN-08, SCAN-09, SCAN-10)

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

**Status:** ✓ COMPLETE (2026-08-19)

**Success Criteria:**

1. ✓ Tạo được MSI installer từ build script (WiX Toolset 3.14.1 + DTF CustomAction).
2. ⚠ MSI cài đặt service, thư mục log. **VC++ x86 detect-only** (Vietnamese error dialog, no bundling) — deviation from ROADMAP SC #2 wording per locked D-09 decision. IT downloads from aka.ms.
3. ✓ MSI gỡ cài đặt sạch sẽ (dừng service, xóa files, xóa service). Logs preserved by default; `REMOVE_LOGS=1` escape hatch.
4. ✓ `Test-Capture.ps1` (preserved from Phase 1) gọi `/api/capture` và trả về ảnh base64.
5. ✓ Angular client gọi `localhost:5043/api/capture` từ domain SaaS — Playwright E2E suite (`tests/FingerprintAgent.E2E/`) validates real CORS preflight + capture flow against running agent.
6. ✓ Không có crash khi scanner không cắm — service vẫn healthy (covered by Phase 3 backoff tests).

**Requirements Covered:**

- DEP-01, DEP-02, DEP-03, DEP-04, DEP-05, DEP-06

**Deliverables:**

- ✓ `src/FingerprintAgent.Installer/` — WiX 3.x DTF CustomAction DLL (net48): CheckVcRedist, ProbeHealthAfterInstall, SeedProgramDataConfig, DetectInstallType, StopRunningService
- ✓ `installer/` — WiX 3.x source: main `.wxs` + Components/ProgramDataConfig.wxs, Service.wxs, CustomActions.wxs, UninstallBehavior.wxs + Dialogs/VcRedistError.wxs + WixUI_Minimal.vi-VN.wxl
- ✓ `src/FingerprintAgent/Configuration/ConfigMerger.cs` — recursive additive merge (D-35) with `MergeIntoFile` static method (shared between ConfigLoader + MSI CustomAction)
- ✓ Runtime config migrated to `C:\ProgramData\FingerprintAgent\config.json` (D-33/D-34/D-36/D-37); legacy v1.0 install-dir config auto-copied on upgrade
- ✓ `src/FingerprintAgent/Update/` — `UpdateCheckService` (Timer-based polling, auto-backoff 6h→12h→24h, msiexec self-upgrade) + `UpdateState` enum + `GitHubReleaseInfo` DTO
- ✓ `.github/workflows/release.yml` — MSI build on tag push (windows-latest, downloads WiX 3.14.1)
- ✓ `.github/workflows/e2e.yml` — E2E Playwright workflow (manual `workflow_dispatch`)
- ✓ `tests/FingerprintAgent.E2E/` — Playwright 1.55.1 + Chromium + TypeScript: CORS preflight, capture flow, end-to-end browser→agent→backend
- ✓ `README.md` — combined dev + IT guide (100 lines)
- ✓ `DEPLOYMENT.md` — Vietnamese operations runbook (326 lines, 10 sections)
- ✓ `docs/` folder REMOVED — `.planning/codebase/` is the single source of truth (D-27)
- ✓ PS1 scripts preserved unchanged: `Install-Service.ps1`, `Uninstall-Service.ps1`, `Service.ps1`, `Setup-VendorSdk.ps1`, `Test-Capture.ps1` (D-32)

**Plan Progress:**

| Plan | Title | Status | Completed |
|------|-------|--------|-----------|
| 04-01 | ConfigMerger + ProgramData path migration | ✅ Complete | 2026-08-19 |
| 04-02 | MSI Installer + WiX CustomActions + Release CI | ✅ Complete | 2026-08-19 |
| 04-03 | Auto-Update Timer + UpdateCheckService | ✅ Complete | 2026-08-19 |
| 04-04 | E2E Playwright + DEPLOYMENT.md + docs cleanup | ✅ Complete | 2026-08-19 |

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
- **Multi-scanner mode** — nhiều máy quét cùng lúc, chọn theo deviceId.
- **ANSI/ISO template conversion** khi SDK hỗ trợ.
- **Code signing certificate** để giảm cảnh báo SmartScreen khi cài MSI.
- **Advanced auto-update** (delta update, rollback, channel preview/stable).

---
*Roadmap created: 2026-07-28*
*Last updated: 2026-08-19 after Phase 4 completion*
