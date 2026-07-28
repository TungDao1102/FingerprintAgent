# State: FingerprintAgent

## Project Reference

See: `.planning/PROJECT.md` (updated 2026-07-28)

**Core value:** Agent luôn sẵn sàng trên máy bệnh viện, kết nối được ít nhất một trong các máy quét vân tay phổ biến, và trả về ảnh PNG đáng tin cậy cho ứng dụng web qua HTTP API địa phương.
**Current focus:** Phase 1 — Foundation (Windows Service + HTTP API skeleton)

## Current Phase

**Phase 1: Foundation — Windows Service + HTTP API skeleton**

- Status: ◆ Context gathered
- Goal: Agent chạy được như Windows Service, phản hồi `/health` và `/api/capture` với mock scanner, có cấu hình `config.json` + CORS.
- Success criteria: install service, start, respond /health, mock /api/capture returns PNG+hash, config + CORS works.

## Phase Progress

| Phase | Status | Plans | Progress |
|-------|--------|-------|----------|
| 1     | ○      | 0/5   | 0%       |
| 2     | ○      | 0/5   | 0%       |
| 3     | ○      | 0/3   | 0%       |
| 4     | ○      | 0/4   | 0%       |

## Active Blockers

None.

## Recent Decisions

- Windows Service chạy nền.
- .NET Framework 4.8, framework-dependent.
- Agent là HTTP server trên `localhost:5043`.
- CORS `allowedOrigins` configurable.
- Angular gọi trực tiếp agent (giống USB token signing agent).
- Không hỗ trợ Windows 7 32-bit chính thức.

---
*State created: 2026-07-28*
*Last updated: 2026-07-28 after initialization*
