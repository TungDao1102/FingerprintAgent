# FingerprintAgent

## What This Is

FingerprintAgent là một **Windows Service chạy nền** trên máy tính tại bệnh viện, cung cấp một HTTP API cục bộ (`localhost:5043`) để ứng dụng web HIS SaaS (Angular + .NET API) có thể yêu cầu quét vân tay từ máy quét USB đang kết nối với máy đó. Agent trả về ảnh vân tay dạng PNG bytes kèm SHA-256 hash để backend xử lý ký số. Thiết kế hướng đến việc **có thể tái sử dụng cho nhiều ứng dụng web khác** thông qua cấu hình `allowedOrigins`.

## Core Value

Agent luôn sẵn sàng trên máy bệnh viện, kết nối được ít nhất một trong các máy quét vân tay phổ biến (SecuGen, Digital Persona, Futronic), và trả về ảnh PNG đáng tin cậy cho ứng dụng web qua HTTP API địa phương.

## Business Context

- **Customer / User**: Nhân viên y tế tại các cơ sở khám chữa bệnh sử dụng HIS SaaS.
- **Revenue model**: Không trực tiếp tính phí — là phần mềm hỗ trợ ký số cho hệ thống HIS.
- **Success metric**: Tỷ lệ yêu cầu quét vân tay thành công trong lần gọi đầu tiên.
- **Strategy notes**: Cần tách rõ agent khỏi HIS cụ thể để tái sử dụng cho các sản phẩm SaaS khác.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- [ ] Agent chạy ổn định như Windows Service trên Windows 10/11 (.NET Framework 4.8).
- [ ] Agent cung cấp HTTP API `POST /api/capture` trên `localhost:5043`.
- [ ] Agent hỗ trợ ít nhất 3 hãng máy quét vân tay phổ biến (SecuGen, Digital Persona, Futronic).
- [ ] Agent trả về ảnh PNG bytes kèm SHA-256 hash.
- [ ] Agent cấu hình được origin cho phép gọi API (`allowedOrigins`) để dùng cho nhiều ứng dụng web.
- [ ] Agent có thể cài đặt/gỡ bỏ dễ dàng qua PowerShell script.

### Out of Scope

- **Không thực hiện matching/template 1:1 hay 1:N** — backend HIS xử lý.
- **Không lưu trữ ảnh vân tay** — chỉ truyền qua bộ nhớ trong lúc request.
- **Không hỗ trợ Windows 7 32-bit** — tập trung Windows 10/11 với .NET Framework 4.8.
- **Không tích hợp sâu vào HIS cụ thể** — giao thức HTTP API chung, HIS tự map payload.
- **Không làm UI capture confirmation** — chỉ gọi SDK, trả kết quả.

## Context

- HIS của chủ dự án là SaaS multi-tenant, Angular frontend gọi trực tiếp local agent (mô hình tương tự sign USB token agent `localhost:8888/signhash`).
- Backend .NET API nằm trên server công ty, không thể chủ động gọi agent qua NAT/firewall.
- Các máy tính tại bệnh viện có cấu hình thấp, vì vậy agent phải nhẹ và dùng .NET Framework có sẵn thay vì self-contained.
- Các SDK vân tay (SecuGen, Digital Persona) có native .NET assembly; Futronic chỉ có P/Invoke x86.

## Constraints

- **Tech stack**: .NET Framework 4.8, framework-dependent, Windows Service, HTTP listener (System.Net.HttpListener hoặc self-host OWIN).
- **Deployment**: Gói nhẹ — chỉ copy exe + dll adapter + config.json + PowerShell scripts; không self-contained.
- **Compatibility**: Windows 10/11; có thể chạy trên Windows 7 SP1 nếu .NET Framework 4.8 đã cài (không cam kết chính thức).
- **Security**: HTTP API bind `localhost` (hoặc IP LAN nếu cần) với CORS `allowedOrigins`; không lưu trữ biometric data.
- **Multi-adapter**: Ít nhất 3 hãng SecuGen, Digital Persona, Futronic; ưu tiên SecuGen vì SDK free + native .NET.
- **Resource usage**: Idle footprint thấp, capture latency < 3 giây (không tính thời gian người dùng đặt ngón tay).

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Windows Service (chạy nền) | Phù hợp máy cố định ở bệnh viện, tự khởi động, luôn sẵn sàng | — Pending |
| .NET Framework 4.8 | Có sẵn trên Win10/11, nhẹ, tương thích SDK vân tay | — Pending |
| Agent là HTTP server trên localhost:5043 | Giống mô hình sign USB token đang dùng, Angular gọi trực tiếp | — Pending |
| CORS `allowedOrigins` configurable | Giúp agent dùng cho nhiều ứng dụng web khác, không riêng HIS | — Pending |
| Không hỗ trợ Win7 32-bit | Giảm phức tạp, tập trung nguồn lực vào stack hiện đại hơn | — Pending |
| Backend không gọi agent trực tiếp | Agent ở PC bệnh viện sau NAT/firewall; Angular → agent là hướng thực tế | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-07-28 after initialization*
