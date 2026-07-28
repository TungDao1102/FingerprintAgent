# Phase 01: Foundation — Windows Service + HTTP API skeleton - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-28
**Phase:** 01-Foundation — Windows Service + HTTP API skeleton
**Areas discussed:** HTTP Stack, PNG Encoding, Logging, Service Host, Project Layout, DI/Config, CORS

---

## HTTP Stack

| Option | Description | Selected |
|--------|-------------|----------|
| System.Net.HttpListener tự xử lý request | Nhẹ nhất, không cần thêm NuGet, dễ kiểm soát. Nhược điểm: phải tự xử lý routing, CORS, JSON. | ✓ |
| ASP.NET Web API 2 self-host (OWIN) | Giống docs cũ, có routing và middleware tương đối chuẩn. Nặng hơn HttpListener một chút, cần thêm package. | |
| TCP listener + HTTP parser thủ công | Đơn giản nhất nếu tự viết toàn bộ HTTP protocol. Không khuyến nghị. | |

**User's choice:** System.Net.HttpListener tự xử lý request
**Notes:** .NET Framework 4.8, không cần IIS. Dùng vì nhẹ và ít dependency.

---

## PNG Encoding

| Option | Description | Selected |
|--------|-------------|----------|
| System.Drawing (GDI+) | Có sẵn trong .NET Framework, dùng để tạo ảnh mock dễ dàng. Windows-only, không cross-platform. | ✓ |
| SkiaSharp | Không phụ thuộc GDI, nhẹ hơn, dùng được cho cả mock và adapter thật sau này. | |
| ImageSharp (SixLabors) | Hoàn toàn managed, không native dependency, phù hợp với .NET Framework 4.8. | |

**User's choice:** System.Drawing (GDI+)
**Notes:** Phù hợp .NET Framework 4.8 Windows-only.

---

## Logging

| Option | Description | Selected |
|--------|-------------|----------|
| System.Diagnostics.Trace + EventLog tự xử lý | Đơn giản, nhẹ, không cần NuGet. Ghi event log qua EventLog.WriteEntry, file log qua TextWriter. | ✓ |
| Serilog | Phổ biến, có sink file + event log, cấu hình linh hoạt. Nhưng thêm dependency. | |
| NLog | Cũng phổ biến, nhẹ hơn Serilog, cấu hình qua config file. | |

**User's choice:** System.Diagnostics.Trace + EventLog tự xử lý
**Notes:** Giữ package nhẹ.

---

## Service Host

| Option | Description | Selected |
|--------|-------------|----------|
| ServiceBase thuần | Không thêm dependency, đúng chuẩn Windows, phù hợp với MSI installer sau này. | ✓ |
| Topshelf | Dễ debug và cài đặt nhanh trong dev, nhưng không phải hướng đi tốt nhất nếu cuối cùng là MSI. | |

**User's choice:** ServiceBase thuần
**Notes:** Người dùng muốn cuối cùng có MSI installer và auto-update từ GitHub Release. Topshelf không chọn vì hướng đến MSI.

---

## Project Layout

| Option | Description | Selected |
|--------|-------------|----------|
| 1 project duy nhất (Console/Service + HTTP + Mock adapter) | Đơn giản, nhanh, dễ bắt đầu. Phù hợp MVP phase 1. | ✓ |
| Tách 3 project: Core, Api, WindowsService | Tách rõ trách nhiệm, dễ mở rộng adapter sau này. Nhưng nhiều project hơn phase 1. | |

**User's choice:** 1 project duy nhất
**Notes:** Tách project sẽ làm từ Phase 2.

---

## DI / Configuration

| Option | Description | Selected |
|--------|-------------|----------|
| Microsoft.Extensions.DependencyInjection + Configuration | Stack hiện đại, quen thuộc với .NET Core dev. Có thể cài qua NuGet trên .NET Framework 4.8. | ✓ |
| Tự viết factory/service locator đơn giản | Không thêm dependency, đơn giản, dễ hiểu, nhưng phải tự quản lý lifecycle. | |

**User's choice:** Microsoft.Extensions.DependencyInjection + Configuration
**Notes:** Cho phép dùng IOptions, ILogger abstraction, dễ mở rộng.

---

## CORS

| Option | Description | Selected |
|--------|-------------|----------|
| Wildcard `*` mặc định, cho phép switch sang allowlist | Giống agent ký số hiện tại (CAPluginService gọi localhost:8888). Dễ dùng cho nhiều app. | ✓ |
| Chỉ allowlist origin cấu hình | An toàn hơn nhưng kém linh hoạt, cần sửa config khi thêm app. | |

**User's choice:** Wildcard `*` mặc định
**Notes:** Agent chỉ lắng nghe localhost, nên wildcard CORS là chấp nhận được.

---

## Agent's Discretion

- Cách tổ chức folder trong 1 project duy nhất.
- Chi tiết format log.
- Cách sinh PNG mock.

## Deferred Ideas

- MSI installer → Phase 4 Deployment.
- Auto-update từ GitHub Release → Phase 4 Deployment.
- Adapter thật SecuGen / Digital Persona / Futronic → Phase 2.
- Config reload runtime / reconnect scanner / polling backend → Phase 3.
