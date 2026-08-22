# FingerprintAgent — Release-Crash Review

**Ngày:** 2026-08-22
**HEAD:** `72540bb` (test(04): regression tests for security.bindIp, CT threading, ProbeConnection guard)
**Phạm vi:** spec `.md` (AGENTS, SCANNER_SETUP, DEPLOYMENT, README, E2E README) + review cũ
`.planning/reviews/full-project-review.md` (đối chiếu trạng thái fix) + toàn bộ source `src/`,
`installer/`, build Release.

**Kết quả build Release tại HEAD:** ✅ 0 warning / 0 error (SDK vendor vắng → stub adapters).

**Trọng tâm:** nguyên nhân gây crash khi chạy bản Release / MSI production — đặc biệt các vector:
unhandled exception trên thread bất kỳ (giết process trong .NET Framework), native interop
(AccessViolation không catch được mặc định), thiếu assembly khi đóng gói, race lúc shutdown,
lệch hợp đồng doc↔code khiến installer/IT hành động sai.

---

## 🔴 CRITICAL — chặn release, crash chắc chắn hoặc gần như chắc chắn

### C1. MSI thiếu ~30/34 file dependency → service cài bằng MSI chết ngay khi start

- Build output (`src/FingerprintAgent.Host/bin/Release/net48/`) có **34 file**, nhưng
  `installer/Components/Service.wxs:20-31` (`cmp_Binaries`) chỉ đóng gói đúng **4 file**:
  `FingerprintAgent.exe`, `FingerprintAgent.Library.dll`, `Newtonsoft.Json.dll`, `config.template.json`.
- Thiếu nặng nhất: **toàn bộ `Microsoft.Extensions.Configuration*.dll`** (5 file) +
  `DependencyInjection` (2) + `FileProviders` (2) + `Primitives` + `FileSystemGlobbing` — dùng bởi
  `ConfigLoader.Load()`, và đây là **lệnh gọi đầu tiên trong `OnStart()`**
  (`src/FingerprintAgent/Service/FingerprintAgentService.cs:44`).
- Còn thiếu: `ZkTecoFingerPrint.dll`, `DPUruNet.dll`, `SourceAFIS.dll`, `Dahomey.Cbor.dll`,
  `System.Reactive.dll`, các shim `System.Memory/Buffers/Text.Json/...`, và
  **`FingerprintAgent.exe.config`** (binding redirects cho `System.Buffers/Memory/Runtime.CompilerServices.Unsafe`
  — thiếu nó thì kể cả đủ DLL vẫn nguy cơ `FileLoadException`).
- Cơ chế crash trên máy sạch: SCM start service → JIT `ConfigLoader.Load()` →
  `FileNotFoundException: Microsoft.Extensions.Configuration...` → catch ở `OnStart` ghi EventLog rồi
  rethrow (`FingerprintAgentService.cs:47-52`) → **service không bao giờ start được**. Trên máy dev
  chạy `dotnet run` không thấy vì bin đầy đủ — crash "chỉ có ở release".
- E2E workflow có bước cài MSI + chờ `/health` sẽ bắt được lỗi này, nhưng là `workflow_dispatch`
  thủ công — rõ ràng chưa chạy green ở HEAD hiện tại.

### C2. MSI hiện không build được — biến preprocessor WiX chưa từng được định nghĩa

- `installer/Components/Service.wxs:22,26,28,30` dùng `$(var.FingerprintAgent.Host.TargetDir)`;
  `installer/Components/CustomActions.wxs:32` dùng `$(var.FingerprintAgent.Installer.TargetDir)` —
  nhưng `installer/FingerprintAgent.Installer.wixproj` chỉ truyền `-dVersion=$(Version)` cho candle,
  **không** truyền `-d<Project>.TargetDir=...`, **không** heat harvest. Custom `Build` target còn
  override pipeline chuẩn của `wix.targets` (nơi thường sinh các biến đó).
- Kết quả: candle fail `CNDL0103 undefined preprocessor variable`.
- Điều này giải thích C1: **từ khi thêm các NuGet dependency mới, đường release chưa từng build
  thành công nên không ai phát hiện thiếu file.**

### C3. Race song-song capture → native use-after-free → AccessViolationException giết process

Vector crash nghiêm trọng nhất trong runtime:

- `ScannerManager.ScanAsync` gọi `adapter.Initialize()` trước mỗi lần scan
  (`src/FingerprintAgent/Adapters/ScannerManager.cs:343`) và `TryProbe` cũng gọi thẳng
  (`ScannerManager.cs:128`) — **không guard nào chống chạy đè capture đang in-flight**.
- `ZKTecoAdapter.InitializeInternal()` dispose `_device` (`ZKTecoAdapter.cs:136-141`) mà **không
  kiểm tra `_captureInProgress`** — counter này chỉ được check trong `ProbeConnection()`
  (fix `92b848f` mới vá 1 trong nhiều đường vào).
- Kịch bản thực tế: HIS double-click/retry → 2 POST `/api/capture` đồng thời. Thread A snapshot
  `_device` ra ngoài lock rồi `AcquireFingerprintAsync` (`ZKTecoAdapter.cs:212-257`); thread B đi qua
  `Initialize()` → dispose device A đang cầm → lần retry sau A dùng handle đã free → AV native.
- Trong net48, `AccessViolationException` **không bị bắt bởi `catch (Exception)` mặc định** (trừ khi
  `legacyCorruptedStateExceptionsPolicy=true` hoặc `HandleProcessCorruptedStateExceptions` — grep
  thấy không dùng ở đâu) → process chết. `ScannerManager.cs:359-361` bắt `Exception` vô ích với AV.
- Không có semaphore serialize capture nào — một máy quét vật lý bị gọi đồng thời tự do.

### C4. Adapter SecuGen bản thật không thể compile → release với SecuGen bất khả thi

- `SecuGenAdapter.cs` chỉ định nghĩa stub types trong `#if !SECUGEN_SDK_PRESENT` (L5-27). Khi SDK
  present, `SGFingerPrintManager`/`SGFPMDeviceName`/`SGDevInfo` phải resolve từ assembly thật —
  nhưng file **không hề có `using SecuGen.FDxSDKPro.Windows;`** → CS0246 trên mọi máy có
  `lib/SecuGen/`.
- Nghĩa là code SecuGen thật **chưa bao giờ được biên dịch**; chạy `Setup-VendorSdk.ps1` rồi build
  sẽ fail ngay. Release với SecuGen hiện tại là bất khả thi.

### C5. Immediate Custom Actions chạy sai thời điểm chuỗi MSI

Trong MSI, custom action `Execute="immediate"` chạy lúc script-generation, **trước** khi deferred
action (`InstallFiles`, `StartServices`) thực sự thực thi:

| Custom Action | Hậu quả |
|---|---|
| `SeedProgramDataConfig` (immediate, `After="InstallFiles"`) | Fresh install: `config.template.json` chưa được copy vào INSTALLFOLDER → `FileNotFoundException` (`CustomActions.cs:316-319`) → Failure → **install luôn thất bại**. Upgrade: âm thầm merge bằng template cũ còn nằm lại từ bản trước |
| `ProbeHealthAfterInstall` (immediate, `After="StartServices"`) | Probe `/health` trước khi service start thật → `ConnectionRefused` ×5 (~15s+) → `ActionResult.Failure` → **rollback mọi install** |
| `StopRunningService` (immediate, `Before="InstallFiles"`) | OK — tự gọi `sc.exe` đồng bộ ngay lúc immediate (hoạt động được nhờ vậy) |

Cùng với C2: **đường installer chưa từng test end-to-end thành công ở HEAD này.**

### C6. Không có handler exception toàn cục → crash không để lại dấu vết trong agent.log

Không có `AppDomain.CurrentDomain.UnhandledException` lẫn `TaskScheduler.UnobservedTaskException`
trong toàn project. Với service P/Invoke vendor SDK, crash kiểu AV/threadpool-escape chỉ còn
Windows EventLog Application (WER dump), trong khi `DEPLOYMENT.md §7.5` hướng dẫn IT tra
`agent.log` kèm correlation ID — crash-type exception **không bao giờ xuất hiện ở đó**.
Forensics sự cố y tế gần như mù.

---

## 🟠 HIGH — lỗi nghiêm trọng vận hành / crash theo kịch bản

### H1. Log không bao giờ rotate dù config quảng cáo ngược lại → disk full dần dần

- `config.template.json:27-28` khai báo `maxSizeMb:10, maxFiles:5`; `DEPLOYMENT.md §8` hứa
  "tự xoay vòng, giữ 5 file × 10 MB" — nhưng `Logging/AgentLogger.cs` **không có logic rotation**,
  chỉ `FileStream(Append)` vô hạn (`AgentLogger.cs:45-50`).
- Log phình vô hạn → disk full → `_writer.WriteLine` throw `IOException` (`AgentLogger.cs:107`).
  Đa số call site có catch, nhưng log trong `HttpServer.ContinueWith` (`HttpServer.cs:140`) nuốt
  silent, và health-check/capture biến thành lỗi khó đoán. Với máy trắm bệnh viện chạy hàng tháng,
  đây là sự cố vận hành chắc chắn xảy ra.

### H2. Auto-update: download MSI bị kẹp timeout 30s → luôn fail rồi tự disable vĩnh viễn

- `UpdateCheckService.cs:28` đặt `HttpClient.Timeout = 30s` áp cho **mọi** request kể cả tải MSI
  (`GetStreamAsync`, L488). MSI 20–100MB qua mạng bệnh viện gần như chắc chắn vượt 30s →
  `TaskCanceledException` → `HandleInstallFailureAsync` ghi `update.enabled=false` vào config
  (L592-613) → auto-update chết vĩnh viễn sau lần thử đầu. Mâu thuẫn trực tiếp với cam kết
  `DEPLOYMENT.md §5.2/§5.3`.

### H3. Console-mode shutdown: double Close() native NGOÀI try/catch trên signal thread

- `Host/Program.cs:42-43`: Ctrl+C handler gọi `service.StopConsole()` (đã `ZkTecoFingerHost.Close()`
  bên trong `OnStop`, `FingerprintAgentService.cs:193`) rồi **gọi `Close()` lần thứ hai không bọc
  try/catch**. Nếu wrapper throw khi teardown lần 2 → exception trên thread xử lý signal →
  `exitEvent.Set()` (L44) không chạy → treo đến khi kill tay (hoặc fail-fast console).
- Đường `FA_CONSOLE_TIMEOUT` hết hạn cũng bỏ sạch cleanup (không `StopConsole`, không dispose logger)
  (`Host/Program.cs:58-63`).

### H4. Ngân sách timeout 20s bị phá từ nhiều phía

- SCAN-06 retry chạy **trước** khi tạo `totalCts` 20s (`ScannerManager.cs:299-320`): active adapter
  disconnected-nhưng-init-ok cho phép capture tới 15s riêng lẻ, sau đó vòng priority lại được thêm
  ≤20s nữa → worst-case ~35s+.
- Adapter kế thừa `BaseScannerAdapter` (SecuGen/Futronic) là blocking call **bỏ qua CancellationToken
  hoàn toàn** (`BaseScannerAdapter.cs:35-58`): native SDK treo thì `CancelAfter(20s)` vô dụng,
  `Stop()` drain chờ đầy 30s mỗi lần tắt service.
- ZKTeco budget nội bộ 15s (`ZKTecoAdapter.cs:244`) + retry delay có thể vượt mốc trước khi token
  được kiểm tra tại checkpoint kế.

### H5. /health trả HTTP sai hợp đồng doc → installer dialog sai

- `HealthHandler.cs:47`: `(connected || backoffStep < 3) ? 200 : 503`. Fresh install KHÔNG có scanner:
  `connected=false`, `backoffStep=0` → **HTTP 200** với `status:"degraded"` → installer CA classify
  mọi 2xx = Healthy (`CustomActions.cs:257-259`) → hiện dialog *"Cài đặt thành công! Dịch vụ đang chạy"*
  thay vì dialog *"cắm máy quét"* như `DEPLOYMENT.md §2` hứa.
- Nhánh 503 chỉ đạt sau ≥3 bước backoff — thực tế không bao giờ thấy lúc probe cài đặt.
- `DEPLOYMENT.md §4.2` cũng nói 503=degraded — trái code.

### H6. Backoff {10,30,60,120}s về bản chất không tồn tại

- `ApplyBackoff` chỉ chạy khi TẤT CẢ adapter fail ngay ở `Initialize()`
  (`ScannerManager.cs:372-374`); nếu có adapter init-ok nhưng scan-fail thì return `lastResult`
  mà **không ApplyBackoff** (L368-369).
- Khi backoff được set, `InBackoff` **không hề gate `ScanAsync`** — capture vẫn chạy bình thường;
  nó chỉ đổi HTTP status của `/health` ở step≥3. AGENTS.md mô tả "exponential backoff resets on
  success" — trạng thái có reset nhưng không có hành vi chờ nào cả.

### H7. CaptureHandler: body không giới hạn + JSON null → NRE

- Body đọc bằng `ReadToEndAsync` không giới hạn kích thước (`CaptureHandler.cs:33-36`) → trang web
  local độc/gián đoạn có thể POST hàng GB → memory pressure/OOM. CORS không chặn request cross-origin
  kiểu simple (form/text).
- Body `"null"` qua được check `IsNullOrWhiteSpace` → `DeserializeObject` trả `null` →
  `request.ThamChieuId` ném NRE (`CaptureHandler.cs:61`) → 500 thay vì 400.

---

## 🟡 MEDIUM / LOW — đáng sửa nhưng ít khả năng giết process

| # | Vấn đề | Vị trí |
|---|---|---|
| M1 | `UpdatePriority` dispose adapter cũ trong khi scan trên adapter đó có thể in-flight (race hẹp, native risk) | `ScannerManager.cs:241-252` |
| M2 | Shutdown edge: drain 30s hết mà capture vẫn treo → `scanner.Dispose()`/`ZkTecoFingerHost.Close()` dưới native call sống → AV lúc tắt service | `FingerprintAgentService.cs:169-193` |
| M3 | UpdateCheck `TimerCallback` sync-over-async `.GetAwaiter().GetResult()` block threadpool hàng phút; finally của `CheckForUpdateAsync` "resurrect" state=Running sau `Stop()` | `UpdateCheckService.cs:317, 427-437` |
| M4 | CorsMiddleware: preflight bị 403 vẫn gắn ACAO/method headers trước khi check allowlist | `CorsMiddleware.cs:49-84` |
| M5 | `EventLog.WriteEntry` mỗi dòng log (kể cả INFO) → spam Application log; source chưa đăng ký thì swallow exception mỗi lần (perf) | `AgentLogger.cs:111,158-170` |
| M6 | Docs lệch thực tế: E2E README nói mockMode default `true` (thật ra template là `false`); AGENTS.md bảo "No CI/CD" (workflows đã tồn tại); SCANNER_SETUP priority khác template | docs |
| M7 | `ScannerManager.IsConnected` nhánh ternary `_mockMode ? X : X` trùng nhau (dead conditional) | `ScannerManager.cs:48-50` |
| M8 | Regex redact base64 `(?:[A-Za-z0-9+/]{4}){10,}` có nguy cơ backtracking O(n²) trên message dài lạ | `AgentLogger.cs:22-24` |
| M9 | `Stop()`/`Dispose()` của HttpServer không có lock bảo vệ `_disposed` — gọi đồng thời từ OnStop + Dispose có cửa sổ nhỏ để `_cts.Cancel()` trên CTS đã dispose (contained ở service level, nhưng bẩn) | `HttpServer.cs:85-124, 237-245` |

---

## ✅ Những gì review cũ (.planning/reviews/full-project-review.md) report và ĐÃ FIX ở HEAD

- **I2** watcher subscribe-trước-enable ✅ (`ConfigFileWatcher.cs:40-41`)
- **I4** HandleRequestAsync nuốt exception ✅ giờ log có correlation ID (`HttpServer.cs:213-215`)
- **C1/C4** UpdateCheckService build errors + dead InstallTimeout ✅
- **I1** CT shutdown giờ live trong update flow (`_shutdownCts`, `UpdateCheckService.cs:141,164,317`)
- CT threading HttpServer→CaptureHandler (`HttpServer.cs:200`), bindIp authoritative
  (`HttpServer.cs:42-56`), guard ProbeConnection vs capture-in-flight (`92b848f` — nhưng mới vá một
  phần, xem C3)
- ConfigFileWatcher bọc try/catch quanh cả `ConfigReloaded.Invoke` → handler ném exception không
  giết process ✅ (`ConfigFileWatcher.cs:56-78`)
- **C3 cũ (SCM-restart contract)**: xác nhận vẫn đúng như verify trước — phụ thuộc
  `Service.wxs:62-68` giữ `Stop="both"`/`Start="install"`; lưu ý cửa sổ 30s stop có thể bị msiexec
  force-kill nếu drain kéo dài (liên quan M2).

## 💪 Điểm tốt (giữ nguyên)

- `AtomicFileWriter` — write temp + File.Replace, preserve ACL, cleanup temp.
- `ConfigMerger` — phân biệt user-deleted vs template-null, merge array element-wise.
- Lock ordering ScannerManager có comment rõ ràng; snapshot pattern cho concurrent UpdatePriority+Scan.
- Correlation IDs 10-hex xuyên suốt HTTP → adapter → update.
- CORS atomic HashSet swap; ZKTeco tránh double-Close host từ Dispose đúng thiết kế.
- Build Release sạch 0 warning / 0 error; test suite xUnit đầy đủ theo system boundary.

---

## Khuyến nghị ưu tiên fix

### P0 — đóng gói (chặn release tuyệt đối)
1. Sửa wixproj truyền `-dFingerprintAgent.Host.TargetDir` / `-dFingerprintAgent.Installer.TargetDir`
   (hoặc quay lại pipeline chuẩn wix.targets thay custom Build target).
2. Ship **đầy đủ build output**: heat harvest thay vì hard-code 4 file; phải gồm
   `FingerprintAgent.exe.config`.
3. Chuyển `SeedProgramDataConfig` / `ProbeHealthAfterInstall` sang `Execute="deferred"`
   (+ `Impersonate="no"`, tham số qua `Property`→`CustomActionData` kiểu `[INSTALLFOLDER]config.template.json`).
4. Bắt buộc chạy e2e workflow (cài MSI thật) green trước khi tag release.

### P0 — runtime crash
5. Serialize capture bằng `SemaphoreSlim(1,1)` trong ScannerManager (hoặc check `_captureInProgress`
   trong mọi đường `Initialize()`/`InitializeInternal()`).
6. Thêm `AppDomain.CurrentDomain.UnhandledException` (+ `UnobservedTaskException`) logger ghi ra
   agent.log + EventLog.

### P1
7. Thêm `using SecuGen.FDxSDKPro.Windows;` + verify compile với SDK present (CI matrix có/không SDK).
8. Log rotation thật theo `maxSizeMb/maxFiles`.
9. Tách timeout download khỏi HttpClient chung (per-request cts dài hơn cho MSI).
10. Bọc try/catch `Close()` lần 2 trong `Host/Program.cs`; dọn path FA_CONSOLE_TIMEOUT expiry.
11. Sửa `/health` status contract (200 healthy / 503 degraded khớp DEPLOYMENT.md) + CA classify theo JSON body chứ không chỉ status code.

### P2
12. Các mục M1–M9 + đồng bộ docs (AGENTS.md CI/CD, E2E README mockMode, SCANNER_SETUP priority).

---

---

## 📦 Phụ lục A — Đánh giá NuGet `ZkTecoFingerPrint 1.2.1` (nguy cơ crash từ wrapper)

**Nguồn:** `github.com/rainxh11/ZkTecoFingerPrint` @ `31e594135845af4fecd6d4e7251e13ce1086e097`
(chính xác commit của v1.2.1 theo `.nuspec`). Deps: SourceAFIS 3.13.0 + System.Reactive 6.0.0.
Đối tượng review: `ZkTecoFingerHost.cs`, `ZkFingerPrintDevice.cs`, `ZkFingerPrintResult.cs`, `ZkResult.cs`.

### W1. HIGH — Constructor `ZkFingerPrintResult` chạy SourceAFIS + GDI+ trên MỌI capture thành công (lãng phí + thêm surface lỗi)

`ZkFingerPrintResult(byte[] bitmap, int width, int height, ...)` eagerly làm 3 việc nặng:

```csharp
Bitmap = BitmapFormat.GetBitmap(bitmap, width, height).ToArray(); // build BMP (System.Drawing/GDI+)
var image = new FingerprintImage(width, height, bitmap);
Template = new FingerprintTemplate(image);                        // SourceAFIS minutiae extraction (~100-300ms CPU)
TemplateHash = Extensions.Hash(Template.ToByteArray())...;
```

- Overload buffer mà `ZKTecoAdapter` dùng (`AcquireFingerprintAsync(byte[], ct)`) news object này
  ngay khi native trả `Ok` — tức mỗi capture thành công đều **trả giá SourceAFIS extraction + GDI+
  mà agent không hề dùng** (matching là việc backend HIS; PNG encode do tự ta làm từ raw buffer).
- Hậu quả: (+100–300ms latency/capture), áp lực allocation, và **mở rộng surface exception**: nếu
  GDI+ ném (kinh điển: generic `OutOfMemoryException` của GDI+) hoặc SourceAFIS ném trên frame
  degenerate → capture native THÀNH CÔNG vẫn bị biến thành `CAPTURE_FAILED` ở adapter
  (exception bị catch tại `ZKTecoAdapter.cs:293`). Không thể tránh qua API public — đây là behavior
  cứng trong constructor.
- Khắc phục ngắn hạn: chấp nhận. Dài hạn: chuyển sang raw `zkfp2` P/Invoke theo fallback đã ghi sẵn
  trong SCANNER_SETUP.md / `02-RESEARCH.md §5 Option A` (đồng thời bỏ dep SourceAFIS/System.Reactive).

### W2. HIGH — Không try/finally quanh khối P/Invoke → leak HGlobal khi delegate ném exception

Cả hai overload `AcquireFingerprintAsync`:

```csharp
var pointer = Marshal.AllocHGlobal(buffer.Length);      // native heap
...
var response = await Task.Run(() => ZKFPM_AcquireFingerprint(...), ct);
if (response == Ok) Marshal.Copy(...);
Marshal.FreeHGlobal(pointer);                            // ← KHÔNG nằm trong finally
```

Nếu `Task.Run` delegate ném (`DllNotFoundException`, OOM, thread abort lúc shutdown) → cả hai
`FreeHGlobal` + `ArrayPool.Return` bị skip → **native memory leak + rented array không trả pool**
tại mỗi lần lỗi. Xác suất thấp nhưng tích lũy trong service chạy dài.

### W3. HIGH — Wrapper KHÔNG có bất kỳ lock/guard nào cho handle lifecycle → xác nhận C3 phải fix ở tầng mình

- `Close()` chỉ làm: fire event `OnClosing` → mọi device còn sống `Dispose()` (=
  `ZKFPM_CloseDevice`) **đồng bộ trên thread gọi Close**, rồi `ZKFPM_Terminate()`.
- Nếu `Close()`/`device.Dispose()` xảy ra trong khi thread khác đang block bên trong
  `ZKFPM_AcquireFingerprint(handle, ...)` → native dùng handle đã close/terminated → UB/AV.
- Static host hoàn toàn không có locking; `_captureInProgress` guard của ta chỉ che đường
  `ProbeConnection`. **Mọi đường `Initialize()`/`UpdatePriority-dispose`/shutdown-dispose khác đều
  có thể đâm vào capture in-flight.** Fix serialization (SemaphoreSlim) bắt buộc ở ScannerManager.

### W4. MEDIUM — Cancellation không thể abort native call (đúng như tài liệu hoá của ta)

`Task.Run(action, ct)`: ct chỉ ngăn task START. Khi native đang chạy (~1s nội bộ), cancel không có
tác dụng; task chạy tới khi native return. Behavior khớp claim "cancel at next retry checkpoint"
của SCANNER_SETUP.md — contained, không crash, nhưng giải thích vì sao drain-shutdown cần đủ dài.

### W5. MEDIUM — Handle leak trong `OpenDevice`

`ZKFPM_OpenDevice` thành công nhưng `ZKFPM_GetCaptureParamsEx` fail → return sớm **không đóng
handle** → leak handle device native. Kèm theo: kết quả 2 lệnh `GetParameters` (serial/product)
bị ignore. Probe flaky USB lặp lại lâu dài sẽ tích tụ.

### W6. LOW-MEDIUM — `GetParameters` copy theo size do native ghi back

`Marshal.Copy(num, paramValue, 0, size)` với `size` là giá trị `ref` mà native ghi lại — nếu native
báo size lớn hơn buffer 64-byte → `ArgumentOutOfRangeException` (managed, may mắn) hoặc tệ nhất
overflow vùng `AllocHGlobal` (heap corruption). Xác suất thấp, không verify được phía native.

### W7. Điểm tốt / xác nhận an toàn

- **Không có static constructor** → không có rủi ro `TypeInitializationException` bị cache vĩnh viễn;
  thiếu `libzkfp.dll` chỉ ném `DllNotFoundException` per-call — các call site của ta đã bọc catch
  (ngoại lệ duy nhất: `Host/Program.cs:43` console-mode, xem H3).
- **Double `Close()` benign**: lần 2 chỉ `ZKFPM_Terminate()` trả error code, không throw (H3 hạ mức
  nghiêm trọng — trừ trường hợp DLL vắng mặt thì vẫn DllNotFoundException như trên).
- Cancellation token không gây leak: sau await, FreeHGlobal luôn chạy trên path bình thường kể cả
  khi response ≠ Ok.
- `netstandard2.0` chạy tốt trên net48; ArrayPool/System.Reactive đã có sẵn trong output.

**Kết luận wrapper:** dùng được ngắn hạn với điều kiện (1) serialize capture ở tầng ScannerManager
(C3/W3), (2) hiểu rằng mỗi capture mang theo chi phí SourceAFIS vô ích (W1). Trung hạn nên cân nhắc
fallback `zkfp2` P/Invoke tự chủ để bỏ phụ thuộc + bỏ W1/W2.

---

*Phụ lục B — Thay đổi config kèm review này:* `scanner.priority` rút về `["ZKTeco"]` trong
`src/FingerprintAgent/config.json` + `config.template.json` (build Release ✅ 0 warning).
Lưu ý: máy ĐÃ cài đặt đọc `C:\ProgramData\FingerprintAgent\config.json` đè template và smart-merge
giữ nguyên array cũ — phải sửa/xóa file ProgramData đó thủ công trên máy test.

---

## 📦 Phụ lục C — Đánh giá migrate `ZkTecoFingerPrint` NuGet → raw P/Invoke

> **➡ Plan chi tiết triển khai:** `.planning/plans/zkteco-pinvoke-migration-plan.md`

**Kết luận: effort THẤP — ~1 ngày dev + ½ ngày test trên thiết bị thật.**

Ba nguồn prior art che gần hết rủi ro kỹ thuật:

1. `tests/.../Scanner/ZkSdkProbe.cs` — DllImport raw **đã chạy trên thiết bị thật**:
   `ZKFPM_Init/Terminate/GetDeviceCount/OpenDevice/CloseDevice/GetParameters`; xác nhận param codes
   1=width, 2=height, 3=dpi; quirk param-106 trên ZK9500.
2. Commit `4c7c358` (git history) — `[DllImport] ZKFPM_AcquireFingerprint` hoàn chỉnh, ghi chú
   quan trọng: **StdCall (không Cdecl)**, template buffer 2048, retry loop trên `ERROR_CAPTURE`.
3. Source wrapper (`rainxh11/ZkTecoFingerPrint@31e5941`) — mọi signature còn lại nhìn thấy rõ
   (`GetCaptureParamsEx`, param 1102=product name / 1103=serial).

### Blast radius

| File | Số tham chiếu | Công việc |
|---|---|---|
| `Adapters/ZKTecoAdapter.cs` | 77 | Nơi duy nhất cần viết lại thật (~60% file); error-string dictionary theo int code đã có sẵn |
| `Service/FingerprintAgentService.cs` | 3 | using + 1 dòng `Close()` → mechanical |
| `Host/Program.cs` | 2 | using + 1 dòng `Close()` → mechanical |
| `IScannerAdapter.cs` + 3 test files | 6 | chỉ comment doc — không sửa code |

Không có gì ngoài `ZKTecoAdapter.cs` đòi hỏi suy luận logic.

### So sánh phương án

| Tiêu chí | Giữ NuGet | Migrate P/Invoke |
|---|---|---|
| Effort ban đầu | 0 | **~1 ngày dev + ½ ngày test thiết bị** |
| Latency mỗi capture | +100–300ms SourceAFIS/GDI+ vô ích (W1) | Chỉ native call + PNG encode tự có |
| Crash surface | W1 (GDI+/SourceAFIS ném sau capture OK), W2 (leak HGlobal khi exception), handle race ngoài kiểm soát (W3) | Tự viết try/finally; handle lifecycle nằm hoàn toàn trong `_hostLock` của mình → fix C3/W3 cùng lúc thuận tay hơn |
| Dependencies trong output/MSI | `ZkTecoFingerPrint` + `SourceAFIS` + `System.Reactive` (1.36MB) + `Dahomey.Cbor` | Bỏ hết 4 DLL → C1 (thiếu file MSI) nhẹ gánh hơn |
| Supply-chain | Package ~13 sao, 1 maintainer, last update 2023 | Tự chủ 7 DllImport đã verify trên chính thiết bị ZK9500 |
| Rủi ro hồi quy | Không đổi | Thấp — logic adapter (retry/backoff/error mapping/PNG) giữ nguyên, chỉ thay tầng gọi xuống; unit test không cần thiết bị vẫn pass vì stub path không đổi |

### Các bước nếu triển khai

1. Serialize capture (`SemaphoreSlim(1,1)`) ở ScannerManager **trước** — fix độc lập, cần thiết dù
   giữ hay migrate (C3/W3).
2. Tạo `ZkNativeHost.cs` (~120–150 dòng: 7 DllImport + int constants + OpenDevice pattern) +
   rewrite nội bộ `ZKTecoAdapter.cs` giữ nguyên cấu trúc hiện có (host lock, `_captureInProgress`,
   SCAN-10 retry, rolling-capture 15s, ToPngGrayscale riêng — đã không phụ thuộc wrapper từ trước).
3. Rename 2 call-site `Close()`; xóa `<PackageReference Include="ZkTecoFingerPrint" />`; build +
   unit suite (stub path không đổi).
4. Smoke test trên máy có ZK9500: capture loop, `/health` probe, unplug/replug, shutdown paths,
   2 request song song (sau bước 1). Verify thêm: raw `ZKFPM_Init()` lần 2 trả AlreadyInit(=1);
   serial qua param 1103.

---

*Kết luận tổng: code core runtime khá chắc tay (lock ordering, atomic config write, correlation IDs,
cancellation threading đều tốt), nhưng đường release (MSI + WiX) đang gãy ở nhiều tầng và chưa từng
được validate end-to-end, cùng một race native song-song nghiêm trọng — chưa nên ship theo nghĩa
"release cho IT bệnh viện". Việc rút config về ZKTeco-only làm giảm surface adapter nhưng KHÔNG
khử C3/W3 (race nằm trong chính ZKTeco) — cần serialize capture trước khi release thật.*
