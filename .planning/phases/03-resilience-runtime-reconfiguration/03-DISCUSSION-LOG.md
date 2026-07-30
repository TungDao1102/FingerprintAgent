# Phase 3: Resilience & Runtime Reconfiguration - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-30
**Phase:** 03-resilience-runtime-reconfiguration
**Areas discussed:** Exponential Backoff Schedule, Config Reload at Runtime, Error Code Mapping, Background Health Check Loop

---

## Area 1: Exponential Backoff Schedule

| Option | Description | Selected |
|--------|-------------|----------|
| Chung — 1 timer chia sẻ | Một bộ đếm backoff dùng chung cho mọi adapter | ✓ |
| Riêng — mỗi adapter 1 timer | Mỗi vendor adapter theo dõi backoff riêng | |
| Bạn quyết định | Trust agent — chọn cách đơn giản nhất | |

**User's choice:** Chung — 1 timer chia sẻ
**Notes:** Simpler, avoids race condition between adapters

---

| Option | Description | Selected |
|--------|-------------|----------|
| Fail ngay với SCANNER_NOT_CONNECTED | Không thử kết nối khi đang backoff. Trả lỗi 503 | |
| Đợi đến lượt backoff rồi thử | Yêu cầu mới được xử lý khi backoff sẵn sàng | |
| Bỏ qua backoff, thử kết nối lại ngay | Yêu cầu mới kích hoạt thử kết nối | ✓ |

**User's choice:** Bỏ qua backoff — luôn thử kết nối mới (hot-plug friendly)
**Notes:** User clarified concern about breaking hot-plug if failing immediately during backoff. Design refined: scan requests ALWAYS attempt connection regardless of backoff state. Backoff only applied when THAT specific request also fails.

---

| Option | Description | Selected |
|--------|-------------|----------|
| Reset về 10s | Mỗi lần restart, bắt đầu từ đầu | ✓ |
| Reset về 120s (max) | Áp dụng max backoff sau restart | |
| Bạn quyết định | Trust agent | |

**User's choice:** Reset về 10s
**Notes:** Simple, allows multiple attempts if scanner not yet plugged in when machine starts

---

## Area 2: Config Reload at Runtime

| Option | Description | Selected |
|--------|-------------|----------|
| Chỉ scanner + CORS | Phase 3 chỉ cần thay đổi scanner priority và CORS | ✓ |
| Tất cả sections | Full reload mọi config. Cần rebuild HTTP listener nếu port đổi | |
| Bạn quyết định | Trust agent | |

**User's choice:** Chỉ scanner + CORS
**Notes:** User asked if Windows Services support config change while running. Clarified that FileSystemWatcher + runtime reload works regardless of hosting model.

---

| Option | Description | Selected |
|--------|-------------|----------|
| Giữ config cũ, log lỗi | Tiếp tục dùng config cũ, ghi error. Service không crash | ✓ |
| Fatal — service stop | CFG-04 strict fail, crash ngay | |

**User's choice:** Giữ config cũ, log lỗi

---

| Option | Description | Selected |
|--------|-------------|----------|
| Active adapter giữ nguyên đến khi disconnect | Priority mới áp dụng cho connection attempt tiếp theo | ✓ |
| Reset ScannerManager với priority mới ngay | Recreate ngay lập tức | |

**User's choice:** Active adapter giữ nguyên đến khi disconnect

---

## Area 3: Error Code Mapping

| Option | Description | Selected |
|--------|-------------|----------|
| 503 Service Unavailable | Đúng semantic — scanner không có | ✓ |
| 504 Gateway Timeout | Giống timeout nhưng không chính xác | |
| Bạn quyết định | Trust agent | |

**User's choice:** 503 Service Unavailable

---

| Option | Description | Selected |
|--------|-------------|----------|
| Có, trong errorMessage | VendorErrorCode gửi kèm errorMessage | ✓ |
| Không — logs only | Chỉ ghi vào log, response chỉ generic message | |

**User's choice:** Có, trong errorMessage — IT support đọc logs để debug nhanh hơn

---

| Option | Description | Selected |
|--------|-------------|----------|
| Mỗi adapter tự định nghĩa | Adapter biết SDK của mình nhất | ✓ |
| ScannerManager định nghĩa tập trung | Mapping table tập trung trong ScannerManager | |

**User's choice:** Mỗi adapter tự định nghĩa

---

| Option | Description | Selected |
|--------|-------------|----------|
| ScannerManager tập trung | 10s total budget enforced tại ScannerManager | ✓ |
| Mỗi adapter tự enforce | Mỗi adapter tự quản lý timeout | |

**User's choice:** ScannerManager tập trung

---

## Area 4: Background Health Check Loop

| Option | Description | Selected |
|--------|-------------|----------|
| Timer | System.Threading.Timer nhẹ, đơn giản | ✓ |
| Dedicated Background Thread | Vòng lặp while(true) riêng | |
| Bạn quyết định | Trust agent | |

**User's choice:** Timer

---

| Option | Description | Selected |
|--------|-------------|----------|
| 30 giây | Đủ nhanh phát hiện, không spam CPU | ✓ |
| 10 giây | Nhanh hơn, tốn resource hơn | |
| 60 giây | Chậm hơn, dành cho máy yếu | |

**User's choice:** 30 giây

---

| Option | Description | Selected |
|--------|-------------|----------|
| Log + bắt đầu backoff | Ghi log, bắt đầu backoff. Không fail health endpoint ngay | ✓ |
| Log + cập nhật health endpoint ngay | Health endpoint trả 503 ngay lập tức | |

**User's choice:** Log + bắt đầu backoff

---

## Area 5: In-Flight Request Handling

| Option | Description | Selected |
|--------|-------------|----------|
| Fail ngay với SCANNER_NOT_CONNECTED | Return 503 ngay. Client tự retry | ✓ |
| Đợi backoff cycle rồi retry một lần | Đợi, retry, nếu fail thì 503 | |

**User's choice:** Fail ngay với SCANNER_NOT_CONNECTED

---

## Area 6: Backoff State Persistence

| Option | Description | Selected |
|--------|-------------|----------|
| In-memory only | Chỉ lưu trong RAM. Restart → reset về 10s. ~4 bytes | ✓ |
| File (ProgramData) | Lưu vào file. Restart/reboot vẫn tiếp tục | |

**User's choice:** In-memory only
**Notes:** User concerned about RAM usage on low-spec machines. Clarified: in-memory = one int variable (~4 bytes), not a cache system. "File" would actually add MORE overhead (I/O, race condition handling). In-memory is lightest possible option.

---

## the agent's Discretion

None — all decisions made by user directly.

## Deferred Ideas

- **Polling/WebSocket mode** — Backend SaaS push lệnh xuống agent qua NAT/firewall. Phase 5+.
- **Full config reload (all sections)** — Có thể reload HTTP port/service name khi cần. Giới hạn ở scanner + CORS trong Phase 3.
- **Backoff state file persistence** — Từ chối. Thêm complexity, không cần thiết cho v1.

---

*Discussion completed: 2026-07-30*
*Next: /gsd-plan-phase 3*