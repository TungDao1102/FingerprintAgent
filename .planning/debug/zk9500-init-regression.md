---
status: resolved
trigger: ZK9500 + FingerprintAgent: tất cả capture fail với "ZKTeco: no finger detected within 0s". Test-ZK9500-Timing.ps1 báo T1 fail trong 3.64s, T2 trong 3.57s, T3 trong 3.56s. Vendor demo tại C:\Users\admin\ZKFingerSDK 5.3_ZK10.0\ZKFingerSDK 5.3_Windows_ZK10.0\C#\Demo capture bình thường → SDK/driver/sensor OK. Agent đã từng capture thành công trước đó → regression.
created: 2026-08-18
updated: 2026-08-18
---

# Debug Session: zk9500-init-regression

## Trigger (verbatim)
PS C:\Users\admin\Music\FingerprintAgent\scripts\diagnostic> .\Test-ZK9500-Timing.ps1
Health: healthy, deviceId=1967261401078, model=ZK9500, vendorErrorCode=NONE
T1 NO_FINGER: FAIL 3.64s | HTTP 500
T2 FINGER_PRE: FAIL 3.57s | HTTP 500
T3 FINGER_DURING: FAIL 3.56s | HTTP 500
Verdict: [!] T1 fail in <4s — Initialize failed, NOT SDK timeout

## Symptoms (confirmed)

| # | Aspect | Observation |
|---|--------|-------------|
| 1 | Expected | ZK9500 capture fingerprint via agent |
| 2 | Actual | HTTP 500 in 3.5s, error "ZKTeco: no finger detected within 0s" |
| 3 | Error message | `ZKTeco: no finger detected within 0s` (note: "0s" is misleading — see Hypothesis A) |
| 4 | Health check | healthy, deviceId=1967261401078, vendorErrorCode=NONE, inBackoff=false, backoffStep=0 |
| 5 | Vendor demo | C:\Users\admin\ZKFingerSDK 5.3_ZK10.0\...C#\Demo chạy bình thường với cùng scanner |
| 6 | Timeline | Agent đã từng capture thành công → regression, không phải first-install |
| 7 | Logs | `[WARN] ZKTecoAdapter scan failed: ZKTeco: no finger detected within 0s` xuất hiện mỗi request; service đã restart nhiều lần |
| 8 | Reproduction | 100% — 3 test đều fail giống nhau |

## Current Focus

- **hypothesis (CONFIRMED)**: ZKTecoAdapter.Scan() retry loop giới hạn "rolling capture window" = `3 attempts × ~1s SDK timeout + 2 × 100ms = ~3.2s`. Comment ở line 169-171 sai khi assume SDK per-call timeout = 2s. Thực tế SDK ZKFPM_AcquireFingerprint trên ZK9500 block ~1s trước khi trả ERROR_CAPTURE. Tổng wait 3.2s không đủ cho user click button → với tay → đặt ngón tay (cần 5-10s UX).
- **additional bugs found**:
  1. Integer division bug ở error message line 205: `maxAttempts * retryDelayMs / 1000` = `3*100/1000` = `0` (int math) → message hiển thị "within 0s" gây hiểu nhầm
  2. ScannerManager.cs:325-327 tạo `adapterCts` 3s budget nhưng KHÔNG pass token vào `adapter.Scan()` ở line 336 — dead code, không cancel SDK call
  3. ScannerManager.cs:312 total 10s CTS chỉ check giữa các adapter iterations, không cancel blocking call
- **regression commit**: `4c7c358 feat(03): ZK9500 capture fix + active /health probe` introduced retry loop thay cho single blocking call. Trước commit này dùng `_device.AcquireFingerprintAsync(CancellationToken.None).GetAwaiter().GetResult()` (block đến khi finger hoặc ScannerManager timeout).
- **test**: So sánh pre/post commit `4c7c358` của ZKTecoAdapter.cs (git show). Vendor demo raw P/Invoke capture thành công → chứng minh raw call là đúng, chỉ timing budget sai.
- **expecting**: Fix = thay attempt-based loop thành elapsed-time-based loop (Stopwatch + budget 8000ms), fix integer division trong error message.
- **next_action**: Apply fix to ZKTecoAdapter.cs lines 168-206, build, verify tests pass.
- **reasoning_checkpoint**: 
  - Root cause = attempt-counted loop tạo quá ngắn capture window
  - Vendor demo OK → SDK OK
  - Health=NONE → Initialize() OK
  - 3.5s observed = 3 × ~1s SDK + 2 × 100ms sleep ≈ 3.2s + overhead
  - Fix: budget-based loop (8s) thay vì attempt-based (3 attempts)

## Evidence

- 2026-08-18T07:13:25: NO_FINGER test → "[WARN] ZKTecoAdapter scan failed: ZKTeco: no finger detected within 0s"
- 2026-08-18T07:13:36: FINGER_PRE test → "[WARN] ZKTecoAdapter scan failed: ZKTeco: no finger detected within 0s"
- 2026-08-18T07:13:40: FINGER_DURING test → "[WARN] ZKTecoAdapter scan failed: ZKTeco: no finger detected within 0s"
- Health endpoint trả về: `{"status":"healthy","deviceId":"1967261401078","model":"ZK9500","vendorErrorCode":"NONE","uptime":"00:04:38","inBackoff":false,"backoffStep":0}`
- Vendor demo path confirmed hoạt động: C:\Users\admin\ZKFingerSDK 5.3_ZK10.0\ZKFingerSDK 5.3_Windows_ZK10.0\C#\Demo

## Eliminated

- hypothesis: Initialize() thực sự fail → ELIMINATED (health=NONE, không có ERROR_INITLIB/INIT/OPEN trong logs)
- hypothesis: Sensor/driver chết → ELIMINATED (vendor demo cùng sensor chạy OK)
- hypothesis: ScannerManager 10s timeout fire → ELIMINATED (3.5s < 10s)
- hypothesis: Hardware LED off = scanner chết → ELIMINATED (LED không ổn định nhưng vendor demo vẫn capture được)
- hypothesis: SDK internal timeout khác 1s → ELIMINATED (3.5s total = 3 calls + 2 sleeps confirms ~1s/call)

## Resolution

### Phase 1 (commit `575fcce`) — Fix rolling-capture timing
(root_cause: ZKTecoAdapter.Scan() retry loop (commit 4c7c358) giới hạn capture window ~3.2s thay vì ~6s như comment claim. SDK ZKFPM_AcquireFingerprint per-call timeout thực tế = ~1s (không phải 2s). User UX cần 5-10s để click→với tay→đặt ngón tay. Error message cũng có integer division bug hiển thị "within 0s".)
(fix: Thay attempt-based loop bằng elapsed-time-based loop (Stopwatch + 8000ms budget). Fix integer division bug dùng Stopwatch.ElapsedMilliseconds trực tiếp. Update comment để document ScannerManager's per-adapter CTS là dead code.)
(verification: dotnet build Release → 0 errors, 2 pre-existing xUnit1031 warnings (unchanged); dotnet test → 54/58 passed, 4 failed — all 4 failures in ScannerManagerProbeIntegrationTests requiring real ZK9500 hardware (test code explicitly states "Do NOT mark as passing on machines without hardware"). Pre-existing environmental failures, NOT regressions from this fix.)
(files_changed: src/FingerprintAgent/Adapters/ZKTecoAdapter.cs)

### Phase 2 — Discover P/Invoke bypass was unnecessary
Investigation of the wrapper source (via ilspycmd decompile of ZkTecoFingerPrint v1.2.1) revealed
the wrapper is NOT buggy — it exposes TWO overloads of `AcquireFingerprintAsync`:

| Overload | Behavior | Works on ZK9500? |
|---|---|---|
| `AcquireFingerprintAsync(CancellationToken ct)` | Queries parameter 106 to size image buffer | **No** — param 106 unimplemented, returns -8 immediately |
| `AcquireFingerprintAsync(byte[] buffer, CancellationToken ct)` | Skips parameter 106, writes directly into caller buffer | **Yes** |

The original code (pre-`4c7c358`) called the parameterless overload, hence the failure on ZK9500.
Commit `4c7c358` added a raw P/Invoke bypass under the (incorrect) belief that the wrapper
was buggy. The bypass worked but added 60+ LOC of unnecessary complexity.

Re-implementing `ZKTecoAdapter.Scan()` to use the buffer-overload eliminates the P/Invoke
declaration, manual `Marshal.AllocHGlobal`/`FreeHGlobal` for image + template buffers,
and the manual `BitmapFormat.GetBitmap` call. Verified on real ZK9500:
- `Test-Capture.ps1` → SUCCESS with valid SHA-256 verificationData
- `Test-ZK9500-Timing.ps1` T1 (no finger) → FAIL at ~10s with "no finger detected within 8s"
- T2 (finger pre-placed) → SUCCESS in 4-5s
- T3 (finger during) → SUCCESS in 4-5s
