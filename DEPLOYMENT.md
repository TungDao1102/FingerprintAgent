# DEPLOYMENT.md — Sổ tay vận hành FingerprintAgent

> Tài liệu này dành cho nhân viên IT bệnh viện vận hành FingerprintAgent trên
> máy trạm Windows. Nếu bạn là lập trình viên, xem `README.md` và
> `.planning/codebase/` trong repository này.

---

## 1. Yêu cầu hệ thống (Prerequisites)

### Phần cứng

- Máy tính chạy Windows 10 hoặc Windows 11 (phiên bản 64-bit).
- Ít nhất 100 MB dung lượng trống trên ổ C:.
- Cổng USB cho máy quét vân tay (xem danh sách hãng hỗ trợ bên dưới).

### Phần mềm bắt buộc

| Phần mềm | Phiên bản | Ghi chú |
|---|---|---|
| .NET Framework | 4.8 | Đã có sẵn trên Windows 10/11 |
| Microsoft Visual C++ Redistributable (x86) | 2015-2022 | MSI sẽ phát hiện nếu thiếu và yêu cầu cài trước |
| Driver của hãng máy quét | tùy hãng | Cài trước khi cắm máy quét |

### Driver máy quét vân tay

Agent hỗ trợ các hãng máy quét sau (cần cài driver từ hãng trước):

- **SecuGen** (khuyến nghị) — driver miễn phí từ [secugen.com](https://www.secugen.com)
- **DigitalPersona** — driver U.are.U từ [digitalpersona.com](https://www.digitalpersona.com)
- **Futronic** — driver từ [futronic-tech.com](http://www.futronic-tech.com)
- **ZKTeco** (ví dụ: ZK9500) — driver từ nhà cung cấp thiết bị

Lưu ý: máy quét **chưa** cắm vào thì cài FingerprintAgent vẫn bình thường —
agent sẽ chờ và tự động phát hiện trong vòng 30 giây sau khi cắm.

### Tải xuống Visual C++ Redistributable (nếu thiếu)

Nếu MSI thông báo thiếu VC++, tải từ đường link chính thức của Microsoft:

```
https://aka.ms/vs/17/release/vc_redist.x86.exe
```

Cài xong VC++ rồi chạy lại MSI FingerprintAgent.

---

## 2. Cài đặt (Installation)

### Bước 1 — Tải gói cài đặt

1. Mở trình duyệt, truy cập trang **Releases** trên GitHub của dự án.
2. Tải file `FingerprintAgent-Setup.msi` của phiên bản mới nhất (ví dụ `v1.0.0`).
3. Lưu vào máy (mặc định thư mục `Downloads`).

### Bước 2 — Chạy trình cài đặt

1. Đóng các ứng dụng đang dùng cổng 5043 (nếu có).
2. Nhấp đúp vào file `FingerprintAgent-Setup.msi`.
3. Nếu Windows hỏi **User Account Control**, chọn **Yes** (cần quyền admin).
4. Đợi trình cài đặt chạy và hộp thoại tiếng Việt hiện ra.

### Bước 3 — Hộp thoại thành công

Có ba trường hợp:

| Tình huống | Hộp thoại |
|---|---|
| Cài đặt mới, máy quét đã cắm và hoạt động | "Cài đặt thành công! Dịch vụ đang chạy." |
| Cài đặt mới, chưa cắm máy quét | "Cài đặt thành công nhưng chưa phát hiện máy quét. Cắm máy quét và đợi 30 giây." |
| Nâng cấp từ phiên bản cũ | "Đã cập nhật lên phiên bản vX.Y.Z." |

### Bước 4 — Kiểm tra nhanh

Mở trình duyệt, truy cập:

```
http://127.0.0.1:5043/health
```

Nếu thấy chuỗi JSON trả về (ví dụ `{"status":"healthy",...}`), agent đã
hoạt động bình thường.

---

## 3. Cài đặt im lặng (Silent install)

Dành cho triển khai hàng loạt qua **Group Policy**, **SCCM**, hoặc script
tự động. Không hiển thị bất kỳ hộp thoại nào.

### Lệnh cài đặt

```cmd
msiexec /qn /i FingerprintAgent-Setup.msi /l*v install.log
```

- `/qn` — chế độ im lặng hoàn toàn (no UI).
- `/l*v install.log` — ghi log chi tiết ra file `install.log` cùng thư mục.

### Kiểm tra kết quả

```cmd
echo %ERRORLEVEL%
```

- `0` — cài đặt thành công.
- `1603` — lỗi nghiêm trọng, xem `install.log` để biết chi tiết (thường là
  thiếu VC++ hoặc service đã được cài từ phiên bản cũ hơn không tương thích).

### Gỡ cài đặt im lặng

```cmd
msiexec /qn /x FingerprintAgent-Setup.msi /l*v uninstall.log
```

### Lưu ý: hộp thoại tiếng Việt chỉ hiện ở chế độ tương tác

Hộp thoại cảnh báo tiếng Việt khi thiếu Visual C++ (x86) chỉ hiện khi cài
đặt tương tác (chạy MSI bằng cách nhấp đúp). Khi triển khai với `/qn`, msiexec
bỏ qua toàn bộ chuỗi UI, không hiện hộp thoại — install sẽ thất bại ngay với
mã thoát 1603 và ghi `VcRedistMissingDialog=1` vào log. Trước khi triển khai
silent cho máy mới, hãy:

1. Cài VC++ x86 trước bằng GPO/SCCM:
   ```cmd
   vc_redist.x86.exe /quiet /norestart
   ```
2. Hoặc dùng script PowerShell kiểm tra registry trước khi gọi `msiexec`:
   ```powershell
   if (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86')) {
       Write-Error 'VC++ x86 missing — aborting'
       exit 1
   }
   ```

---

## 4. Kiểm tra sau cài đặt (Post-install verification)

### 4.1 Kiểm tra service đang chạy

Mở **Services** (`services.msc`) hoặc PowerShell:

```powershell
Get-Service FingerprintAgent
```

Trạng thái phải là **`Running`**.

### 4.2 Kiểm tra HTTP endpoint

Dùng PowerShell:

```powershell
Invoke-WebRequest http://127.0.0.1:5043/health -UseBasicParsing
```

Kết quả mong đợi:

| HTTP Status | Ý nghĩa | Hành động |
|---|---|---|
| `200` | Agent khỏe, máy quét đã kết nối | Không cần làm gì |
| `503` với `status=degraded` | Agent chạy nhưng máy quét chưa kết nối | Cắm máy quét, đợi 30 giây |
| `503` với `status=inBackoff` | Agent đang trong giai đoạn chờ retry | Bình thường nếu vừa mất kết nối, đợi |
| Không phản hồi | Service không chạy hoặc port bị chiếm | Xem mục 7. Troubleshooting |

### 4.3 Kiểm tra EventLog

Mở **Event Viewer** (`eventvwr.msc`), vào:

```
Applications and Services Logs -> FingerprintAgent
```

Mỗi lần agent khởi động sẽ có một entry **Information** với correlation ID.
Nếu có lỗi, sẽ có entry **Error** kèm mô tả chi tiết.

### 4.4 Test capture thủ công (tuỳ chọn)

```powershell
$body = @{
    thamChieuId = 'test'
    maPhieu     = 'TEST-001'
    loaiPhieu   = 'signature'
    vaiKyId     = $null
    nhanLucId   = $null
    metadata    = @{}
} | ConvertTo-Json

Invoke-WebRequest http://127.0.0.1:5043/api/capture `
    -Method POST `
    -ContentType 'application/json' `
    -Body $body `
    -UseBasicParsing
```

Phản hồi 200 với `isSuccess=true` và `imageBytes` chứa PNG base64.

---

## 5. Cập nhật phiên bản (Update procedure)

Có hai cách: **thủ công** (khuyến nghị cho môi trường IT) hoặc **tự động**
(cần bật qua config, mặc định TẮT).

### 5.1 Cập nhật thủ công

1. Mở **Control Panel -> Programs and Features**.
2. Tìm **Fingerprint Agent** trong danh sách.
3. Nhấp chuột phải, chọn **Update**.
4. Trình duyệt sẽ mở trang GitHub Releases — tải phiên bản mới nhất.
5. Chạy MSI mới. Service sẽ tự động:
   - Dừng service cũ (đợi tối đa 30 giây cho request đang xử lý).
   - Ghi đè file cũ bằng file mới.
   - Giữ nguyên `config.json` ở `C:\ProgramData\FingerprintAgent\` (smart merge).
   - Khởi động lại service.

### 5.2 Cập nhật tự động (opt-in)

Mặc định **TẮT**. Để bật:

1. Mở file `C:\ProgramData\FingerprintAgent\config.json` bằng Notepad (chạy
   với quyền admin).
2. Tìm mục `update`:
   ```json
   "update": {
       "enabled": false,
       "checkIntervalHours": 6
   }
   ```
3. Đổi `enabled` thành `true`:
   ```json
   "update": {
       "enabled": true,
       "checkIntervalHours": 6
   }
   ```
4. Lưu file, khởi động lại service:
   ```powershell
   Restart-Service FingerprintAgent
   ```

Service sẽ tự động:
- Mỗi 6 giờ kiểm tra GitHub Releases một lần.
- Nếu có phiên bản mới, tải MSI về `%TEMP%\FingerprintAgent-Setup.msi`.
- Hiển thị toast (nếu có user session), đợi 10 giây.
- Chạy `msiexec /qn` để cài đặt.
- Tự khởi động lại.

### 5.3 Khi cập nhật thất bại

Nếu auto-update gặp lỗi (ví dụ: mất mạng, MSI lỗi):

- Service sẽ **ghi Error vào EventLog và file log**.
- Service sẽ **tự tắt `update.enabled`** trong config.json để không lặp lại
  lỗi.
- Service vẫn chạy bình thường với phiên bản hiện tại.
- Liên hệ IT để cập nhật thủ công.

---

## 6. Gỡ cài đặt (Uninstall)

### 6.1 Gỡ qua Programs and Features (giữ log)

1. Mở **Control Panel -> Programs and Features**.
2. Tìm **Fingerprint Agent**, nhấp **Uninstall**.
3. Trình gỡ sẽ:
   - Dừng service (đợi tối đa 30 giây).
   - Xóa service registration.
   - Xóa file ở `C:\Program Files\FingerprintAgent\`.
   - Xóa EventLog source `FingerprintAgent`.
   - **Giữ lại** `C:\ProgramData\FingerprintAgent\Logs\` (để điều tra nếu cần).
   - **Giữ lại** `C:\ProgramData\FingerprintAgent\config.json`.

### 6.2 Gỡ sạch (xóa cả log)

Khi cần xóa hoàn toàn (tuân thủ quy định lưu trữ, ổ đĩa đầy, v.v.):

```cmd
msiexec /x FingerprintAgent-Setup.msi REMOVE_LOGS=1 /qn /l*v uninstall.log
```

Cờ `REMOVE_LOGS=1` ép trình gỡ xóa cả thư mục `Logs\` (mặc định giữ lại).

### 6.3 Gỡ qua PowerShell

```powershell
$product = Get-WmiObject -Class Win32_Product | Where-Object { $_.Name -eq 'Fingerprint Agent' }
$product.Uninstall()
```

**Lưu ý:** lệnh này chậm (~10 giây) vì WMI quét toàn bộ package. Nên dùng
cách `msiexec /x` ở trên cho nhanh.

---

## 7. Khắc phục sự cố (Troubleshooting FAQ)

### 7.1 Service không start

**Triệu chứng:** `Get-Service FingerprintAgent` trả về `Stopped` ngay sau khi
cài đặt.

**Kiểm tra:**

1. Mở **Event Viewer -> Applications and Services Logs -> FingerprintAgent**,
   xem entry lỗi gần nhất.
2. Kiểm tra port 5043 có bị chiếm không:
   ```powershell
   netstat -ano | findstr :5043
   ```
   Nếu có PID khác đang nghe -> dừng ứng dụng đó trước.
3. Kiểm tra VC++ Redistributable đã cài chưa (xem mục 1).

### 7.2 Scanner không phát hiện

**Triệu chứng:** `/health` trả về 503 với `status=degraded`, máy quét đã cắm
USB nhưng agent không nhận.

**Kiểm tra:**

1. **Driver hãng đã cài chưa?** Mở **Device Manager** (`devmgmt.msc`), tìm
   mục **Biometric devices**. Nếu thấy dấu chấm than vàng -> cài driver từ
   hãng.
2. **Thử cắm cổng USB khác.** Một số cổng USB 3.0 có vấn đề tương thích.
3. **Đợi 30 giây.** Agent retry theo backoff (10s, 30s, 60s, 120s). Cắm máy
   quét rồi đợi ít nhất 30 giây.
4. **Xem EventLog** để biết lý do cụ thể.

### 7.3 Lỗi "VC++ missing" khi cài

**Triệu chứng:** MSI thoát ngay với hộp thoại tiếng Việt báo thiếu VC++.

**Cách xử lý:**

1. Tải `vc_redist.x86.exe` từ
   `https://aka.ms/vs/17/release/vc_redist.x86.exe`.
2. Cài đặt (cần quyền admin).
3. Chạy lại MSI FingerprintAgent.

### 7.4 Capture trả về SCANNER_NOT_CONNECTED

**Triệu chứng:** `POST /api/capture` trả về 503 với `errorCode=SCANNER_NOT_CONNECTED`.

**Nguyên nhân:** giống mục 7.2 — máy quét chưa sẵn sàng. Kiểm tra:

- Máy quét đã cắm USB và đèn LED sáng.
- Driver hãng đã cài (Device Manager không có dấu chấm than vàng).
- Đợi 30 giây để agent retry.
- Nếu vẫn lỗi, khởi động lại service:
  ```powershell
  Restart-Service FingerprintAgent
  ```

### 7.5 Service crash liên tục

**Triệu chứng:** service start xong vài giây thì dừng, EventLog có nhiều
Error liên tiếp.

**Kiểm tra:**

1. Mở **Event Viewer -> Windows Logs -> Application**, lọc theo Source
   `.NET Runtime` hoặc `Application Error`.
2. Copy **correlation ID** từ EventLog FingerprintAgent, tra trong file log:
   ```
   C:\ProgramData\FingerprintAgent\Logs\agent.log
   ```
3. Gửi log cho nhà phát triển kèm phiên bản Windows, phiên bản .NET Framework.

### 7.6 Auto-update không hoạt động

**Triệu chứng:** đã bật `update.enabled=true` nhưng service không tự cập nhật.

**Kiểm tra:**

1. File config đã được lưu chưa? `update.enabled` phải là boolean `true`.
2. Máy có truy cập được `api.github.com` không?
   ```powershell
   Invoke-WebRequest https://api.github.com/repos/.../releases/latest -UseBasicParsing
   ```
3. Xem file log `C:\ProgramData\FingerprintAgent\Logs\agent.log`, tìm
   `UpdateCheck`.
4. Có thể service đã tự tắt `update.enabled` do lỗi trước đó — kiểm tra lại
   config.json.

### 7.7 Capture chậm (> 3 giây)

**Triệu chứng:** `POST /api/capture` mất hơn 3 giây mới trả về.

**Bình thường nếu:**

- Người dùng chưa đặt ngón tay lên máy quét (thời gian chờ tín hiệu).
- Máy quét chuyển sang chế độ tiết kiệm điện.

**Bất thường nếu:**

- Mọi request đều chậm dù đã đặt ngón tay -> xem EventLog, có thể adapter
  của hãng đang lỗi.
- Tăng timeout trong request phía HIS nếu cần (mặc định agent không giới hạn,
  chờ scanner trả về).

### 7.8 Port 5043 đã bị ứng dụng khác dùng

**Triệu chứng:** agent không start, log báo lỗi bind port.

**Kiểm tra:**

```powershell
netstat -ano | findstr :5043
```

Nếu có PID khác -> dừng ứng dụng đó. Nếu không rõ ứng dụng nào:

```powershell
tasklist /FI "PID eq <PID>"
```

Liên hệ nhà phát triển nếu cần đổi port (mặc định 5043, có thể cấu hình
trong `C:\ProgramData\FingerprintAgent\config.json`).

---

## 8. Vị trí file (Log + Config locations)

| Loại | Đường dẫn | Ghi chú |
|---|---|---|
| File cài đặt | `C:\Program Files\FingerprintAgent\` | Chỉ đọc, do admin quản lý |
| File thực thi | `C:\Program Files\FingerprintAgent\FingerprintAgent.exe` | — |
| Cấu hình template | `C:\Program Files\FingerprintAgent\config.template.json` | Tham khảo, không sửa |
| Cấu hình runtime | `C:\ProgramData\FingerprintAgent\config.json` | **Có thể sửa**, được MSI bảo vệ qua smart merge |
| Log | `C:\ProgramData\FingerprintAgent\Logs\agent.log` | Tự xoay vòng, giữ 5 file × 10 MB |
| Installer log | `C:\ProgramData\FingerprintAgent\Logs\installer.log` | Ghi khi cài đặt |
| Update log | `C:\ProgramData\FingerprintAgent\Logs\update.log` | Ghi khi auto-update |
| Merge log | `C:\ProgramData\FingerprintAgent\merge.log` | Ghi khi MSI smart-merge config |

**Lưu ý:** `C:\ProgramData\` bị ẩn theo mặc định. Để hiện, gõ
`C:\ProgramData\FingerprintAgent` trực tiếp vào thanh địa chỉ File Explorer.

---

## 9. Registry entries

| Vị trí | Mục đích |
|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Services\FingerprintAgent` | Service registration (Start, Account, FailureActions) |
| `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\FingerprintAgent` | EventLog source cho ứng dụng |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}` | Programs and Features entry |
| `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}` | Programs and Features entry (x86 view) |

`ProductCode` của FingerprintAgent:

```
FF16181A-F127-4ED9-921B-D69E05AB70B7
```

**Khuyến cáo:** không sửa registry thủ công trừ khi có hướng dẫn cụ thể từ
nhà phát triển. Để thay đổi cấu hình service, dùng `sc.exe` hoặc gỡ+cài
lại qua MSI.

---

## 10. Liên hệ hỗ trợ (Support)

Khi gửi yêu cầu hỗ trợ, vui lòng kèm:

1. **Phiên bản FingerprintAgent** (xem Programs and Features hoặc EventLog).
2. **Phiên bản Windows** (`winver`).
3. **Hãng và model máy quét**.
4. **File log** tại `C:\ProgramData\FingerprintAgent\Logs\agent.log` (file
   mới nhất).
5. **Screenshot EventLog** nếu có lỗi.
6. **Correlation ID** từ entry lỗi (nếu có).

### Thông tin liên hệ

- **Email:** _them email hỗ trợ của tổ chức vào đây_
- **Hotline:** _them hotline IT vào đây_
- **Issue tracker:** _them link GitHub Issues của dự án vào đây_

---

*Tài liệu này được cập nhật cùng phiên bản v1.0 của FingerprintAgent.*
