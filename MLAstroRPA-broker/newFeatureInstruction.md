Kế hoạch làm plugin MLAstro-broker-TPPA:
1. Sửa broker TPPA để đáp ứng luồng hoạt động nhưa sau:
TPPA cài đặt: (không chọn system, không bật `Do automated adjustment`) bật sẵn chế độ Auto continue expose sau khi solve ra sai số. 
Bắt đầu routine: nhấn start, TPPA chụp 3 hình solve ra sai số gửi qua broker, TPPA kiểm ra cờ adjusting=true tạm dừng chụp hình, false tiếp tục lặp lại chụp 1 hình và đưa ra sai số.... 
2. MLAstroRPA:
a. loại bỏ thành phần TPPA trong MLAstroRPA-TPPA plugin
các cài đặt trên hình đưa sang cho MLAstro plugin (tab `SOFTWARE SETTING` thay cho tab `TPPA OPTION`)
b. Hoạt động: mở sẵn kết nối đến TPPA qua broker, mở sẵn kết nối đến MLAstroRPA hardware (serial/wireless). chờ và Nhận sai số trên broker, báo adjusting, tiến hành điều chỉnh (tự ra lệnh nudgeAz/Alt dựa vào sai số nhận được), trả lời complete/adjusting=false, tiếp tục đợi sai số tiếp theo.

---
---

# KẾ HOẠCH CHI TIẾT (mở rộng)

## 0. Chốt phạm vi

- **2 plugin tách rời**, chỉ nói chuyện qua NINA message broker (cùng process NINA, không serialize).
- **TPPA (fork upstream 2.2.7.0)**: đo (3 điểm + vòng lặp 1 ảnh), plate solve, tính sai số, quản lý session/camera/slew, hiển thị overlay. **Không điều khiển hardware PA.**
- **MLAstro plugin (tách từ repo hiện tại)**: sở hữu COM/WS, chiến lược hiệu chỉnh, UI riêng. Không còn `MLAstroLink` / mượn cổng / external-control.
- Không fork phần tính toán sai số của TPPA (đúng như đề xuất của họ).

## 0b. Phản hồi tác giả TPPA (2026-09-24) — điều chỉnh kế hoạch

**Tác giả xác nhận**: hoan nghênh PR; **không** cần coi việc dùng estimator mới là điều kiện tiên quyết; base trên TPPA mới nhất; chốt message cho **vòng cơ bản** (TPPA cho measurement → cấp adjustment window → đo lại sau khi ta xác nhận move+settle xong); **bắt buộc có cancellation + completion tường minh** để TPPA không tự finish trước khi pha tiếp cận cuối của ta xong.

### Phân biệt 2 khái niệm (không được lẫn trong kế hoạch/PR)

| Khái niệm | Vai trò | Trong external mode |
|---|---|---|
| `ContinuousPolarErrorEstimator` | Ước lượng sai số còn lại từ plate solve mới (lấy sai số trước làm điểm đoán, phát hiện vượt target và đảo dấu sai số) | **Vẫn chạy** — sinh ra số liệu `Measurement` cho ta |
| `AutomatedAdjustmentController` | Học đáp ứng hardware để chọn move/overshoot | **Không dùng** — ta sở hữu movement + overshoot strategy |

⇒ Không port, không phụ thuộc estimator; chỉ tiêu thụ số liệu. Bắt đầu integration trên **legacy calculation mode** (chế độ mặc định vẫn có), chuyển sang continuous sau — không chặn tiến độ.
⇒ Nếu thấy estimator cho kết quả sai trong lúc chạy overshoot: thu thập **TPPA version + settings + log có plate solve trước/sau overshoot** và gửi tác giả (bug riêng), **không** gộp vào PR broker.

### Điều chỉnh cụ thể so với các mục dưới

1. **§1.3** — thay `Adjusting` boolean bằng **adjustment window có ID**: `BeginAdjustment` → `AdjustmentGranted(WindowId, MeasurementId, MaxWindowMs)` → `RequestMeasurement(WindowId, StationaryAndSettled)`. Cờ boolean chỉ giữ làm fallback. Vẫn giữ TTL + watchdog ở §1.5.
2. **§1.3** — đưa `RequestCompletion` + `Cancel`/`Stopped` vào **MVP** (không để v1.1) vì tác giả yêu cầu explicit completion/cancellation.
3. **§2.2** — cổng giữ chụp không chỉ "đợi cờ adjusting" mà là **đợi `RequestMeasurement`** trong window; hết TTL window ⇒ TPPA tự nhả + báo `SessionState`.
4. **§2.2/§2.3** — TPPA **không tự finish** khi đạt tolerance nếu ta chưa `RequestCompletion`; khi session bị cancel (user Stop / đóng cửa sổ / skip sequence item) phải phát `StopRequested` và **chờ `Stopped` ack** rồi mới kết thúc.
5. **§4/§6** — PR đã được green-light ⇒ bỏ rủi ro "có thể bị từ chối"; **phạm vi PR1 = basic cycle + cancellation + completion**, chưa gồm overshoot nâng cao.
6. **§5** — thêm case test: cancel giữa window, completion trước/sau tolerance, và case thu log estimator (không fix trong PR này).
7. **§7 câu 2** — "window có ID" **đã chốt** theo đề xuất của tác giả (không cần bàn lại).

## 1. Hợp đồng broker v1 (phải chốt trước khi code)

### 1.1 Topic (2 topic, phân biệt bằng `Kind`)

| Topic | Ai publish | Ai subscribe |
|---|---|---|
| `PolarAlignmentPlugin_PolarAlignment_ExternalEvent` | TPPA | MLAstro |
| `PolarAlignmentPlugin_PolarAlignment_ExternalCommand` | MLAstro | TPPA |

Giữ nguyên 4 topic cũ (`..._DockablePolarAlignmentVM_StartAlignment` / `_StopAlignment`, `..._PolarAlignment_PauseAlignment` / `_ResumeAlignment`) và 2 message cũ (`Progress`, `AlignmentError`) — **không đổi contract cũ**.

### 1.2 Envelope (mọi message)

`SessionId`(= `CorrelationId`), `CommandId`, `ReplyTo`, `SequenceNumber` (monotonic), `Kind`, `Version`, `IntendedRecipient`. Trùng `CommandId` ⇒ trả lại reply cũ (idempotent). Request cũ/sai thứ tự ⇒ từ chối kèm mã lỗi (`StaleMeasurement`, `UnsupportedKind`, `VersionMismatch`, `Busy`).

### 1.3 Message MVP

| Kind | Chiều | Field chính |
|---|---|---|
| `Capabilities` | TPPA → MLAstro | interface version, danh sách Kind, TPPA version, TTL/timeout mặc định |
| `Measurement` | TPPA → MLAstro | `MeasurementId`, timestamp, `Status` (Valid/Unstable/CaptureFailed), `AzimuthErrorArcMin`, `AltitudeErrorArcMin` (có dấu), `Northern`, `ToleranceReached`, `ToleranceArcMin`, `CalculationMode`, `IsFirstMeasurement` |
| `BeginAdjustment` | MLAstro → TPPA | `MeasurementId` (muốn sửa theo lần đo nào) |
| `AdjustmentGranted` | TPPA → MLAstro | `WindowId`, `MeasurementId`, `MaxWindowMs` |
| `RequestMeasurement` | MLAstro → TPPA | `WindowId`, `StationaryAndSettled=true` ⇒ TPPA đóng window + chụp lần mới |
| `RequestCompletion` | MLAstro → TPPA | yêu cầu verify/kết thúc (TPPA không tự finish trước khi nhận) |
| `Adjusting` (fallback) | MLAstro → TPPA | `Adjusting` (bool), `MeasurementId`, `ExpectCompletionWithinMs` — chỉ dùng khi chưa có window |
| `SessionState` | TPPA → MLAstro | state, `WindowId?`, `AdjustingActive`, `SamplesTaken`, `LastMeasurementId`, tolerance |
| `StopRequested` | TPPA → MLAstro | reason (user stop / sequence cancel / window closed / disconnect / timeout) |
| `Stopped` | MLAstro → TPPA | ack dừng, `HardwareStopStatus` (ok/unknown/fault) |
| `Fault` | MLAstro → TPPA | lý do, hardware stop status |

Giữ đúng đề xuất của họ (Kind-based) để PR dễ được nhận. **MVP = 11 Kind trong bảng trên** (đã gồm cancellation `StopRequested`/`Stopped` + completion `RequestCompletion` theo yêu cầu tác giả); các Kind nâng cao khác (`GetSessionState`, mở rộng window/measurement) để v1.1.

### 1.4 Đơn vị & dấu (điểm dễ sai nhất)

- Gửi **cả độ và arcmin** để MLAstro không phải tự đổi (tránh lệch làm tròn).
- Số gửi đi = **đúng** `PolarErrorDetermination.CurrentMountAxis{Azimuth,Altitude}Error.Degree` đang dùng cho UI/vòng lặp.
- Hướng sửa **không gửi qua payload**: MLAstro tự suy từ dấu của `AzimuthErrorArcMin`/`AltitudeErrorArcMin` + cờ `Northern` (az dương = Left, âm = Right; alt dương = Down ở bắc bán cầu, Up ở nam) — 2 property dẫn xuất trong `TppaMeasurement`.
- `CalculationMode`: legacy / continuous (estimator) — chỉ để log/hiển thị, không đổi hành vi.

### 1.5 An toàn & vòng đời (bắt buộc)

1. **Window có TTL** (`ExternalAdjustingTimeoutSec`, mặc định 300 s): hết TTL mà chưa nhận `RequestMeasurement` (hoặc `Adjusting=false` nếu dùng fallback) ⇒ TPPA **tự đóng window**, log, publish `SessionState` + lý do (`WindowExpired`) để MLAstro biết.
2. **Heartbeat**: TPPA publish `SessionState` mỗi `ExternalHeartbeatSec` (2 s) trong lúc giữ window; MLAstro phải gửi ít nhất 1 message mỗi 5 s khi đang sửa (im lặng dài hơn ⇒ TPPA coi MLAstro treo ⇒ đóng window).
3. **Không chụp khi window đang mở**; tuyệt đối không capture/slew chồng lên lúc MLAstro đang quay motor.
4. **Không tự kết thúc khi đạt tolerance** ở external mode: chỉ set `ToleranceReached=true` và tiếp tục chờ lệnh; MLAstro quyết định dừng (publish `Stop` hoặc để user bấm Stop).
5. **Trần thời gian session** (`AutomatedAdjustmentTimeout` tái dùng) vẫn có hiệu lực.
6. Handler broker chỉ **queue rồi trả về ngay** (Publish của NINA `await` callback) — không `await` việc dài trong `OnMessageReceived`.

## 2. Workstream A — TPPA fork, nhánh `Feature-broker-for-RPA`

### 2.1 Setting mới (mặc định OFF ⇒ an toàn khi merge)

| Setting | Mặc định | Ý nghĩa |
|---|---|---|
| `ExternalCorrectionEnabled` | false | Bật chế độ điều khiển ngoài |
| `ExternalAutoContinue` | true (khi bật external) | Tự chạy: 3 điểm → publish measurement → (chờ `RequestMeasurement`) → lặp 1 ảnh; **bỏ cổng "Press Start"** |
| `ExternalAdjustingTimeoutSec` | 300 | TTL của adjustment window |
| `ExternalHeartbeatSec` | 2 | Chu kỳ `SessionState` |

Trong external mode: `AutoPause` bị bỏ qua, `DoAutomatedAdjustments` không cần, không chọn system.

### 2.2 Điểm hook (theo đúng bản 2.2.7.0)

| File / vị trí | Việc phải làm |
|---|---|
| `Instructions/PolarAlignment.cs:594` (chỗ đang `Publish(PolarAlignmentErrorMessage)`) | Publish thêm `Measurement` cùng số liệu (giữ nguyên message cũ) |
| Ngay sau khi tạo xong `PolarErrorDetermination` (kết thúc 3 điểm) | Publish `Measurement` **đầu tiên** + gửi `IsFirstMeasurement=true` |
| Trong vòng lặp, ngay trước `if (Properties.Settings.Default.AutoPause)` (`:641`) | `await WaitForExternalMeasurementRequest(token)` — giữ không chụp trong lúc window mở; chỉ chụp lại khi nhận `RequestMeasurement` (kèm TTL) |
| `Instructions/PolarAlignment.cs:569` | External mode: bỏ yêu cầu system/`Connected` |
| `TPAPAVM.MoveCloser` (`TPAPAVM.cs:285`) | External mode ⇒ `return` sớm (không dùng `automatedAdjustmentController`) |
| `Instructions/AutoFinishGate.cs` | External mode ⇒ không tự kết thúc (changelog 2.2.6.5: cơ chế 2 lần solve liên tiếp) — chỉ báo `ToleranceReached` |
| `DockablePolarAlignmentVM` + sequence item | Subscribe `ExternalCommand`; xử lý async |

### 2.3 Những thứ KHÔNG được đụng (để PR gọn, dễ merge)

Không sửa OAPA/UPAS/Avalon, không sửa estimator/`AutomatedAdjustmentController`, không đổi message/topic cũ, không đổi UI ngoài **1 nhóm setting mới**, không đổi branding/PluginId.

### 2.4 Test phía TPPA

`SimulatedExternalController` (chạy trong unit test + chế độ debug): cấp/đóng window, gửi `RequestMeasurement` quá muộn (TTL hết hạn → `WindowExpired`), gửi `RequestMeasurement` không có window, `RequestCompletion` trước/sau tolerance, `Cancel` → chờ `Stopped` ack, ngắt heartbeat, gửi Kind lạ, gửi trùng `CommandId`, đóng cửa sổ TPPA giữa window. Chạy cho **cả 2 entry point** (dock tool + sequence instruction).

## 3. Workstream B — MLAstro plugin

### 3.1 Tách repo

- **Bỏ khỏi plugin MLAstro**: `Instructions/**`, `Dockables/DockablePolarAlignmentVM.cs`, `TPAPAVM.cs`, `Vector3.cs`, `RefractionParameters.cs`, `Avalon/**`, `OAPA/**` (phần TPPA), `Converters/**` của overlay, `Options.xaml(.cs)` của TPPA.
- **Bỏ luôn phần mượn cổng**: `MLAstroLink.cs`, `SharedMlastroSerial.cs` (không còn ai tranh cổng COM) và interface `IPolarAlignmentSystem*`.
- **Giữ**: `MLAstroRPA-navigation/**` (CONTROL / CONNECTION / CONFIGURATION), `MLAstroRPA-implement/Services/**` (Serial + WebSocket), resources/icon của MLAstro.
- `PolarAlignmentPlugin.cs` → `MLAstroPlugin.cs`, **giữ `RootNamespace`** (Settings/Locale/pack URI) nhưng **cấp `PluginId` (GUID) MỚI** — `1352D162-…` đã là PluginId của bản gộp **MLAstroRPA+TPPA đã phát hành**, dùng lại thì NINA coi 2 DLL là cùng một plugin (quyết định 2026-09-25). Hệ quả: settings MLAstro lưu theo GUID cũ không còn được đọc (phải cấu hình lại); đổi `AssemblyName` phải sửa đồng bộ pack URI (bài học 2026-09-03).
- Driver MLAstro (`UniversalPolarAlignmentMLAstroRPA`) rút gọn thành **`HardwareAligner`** (không implement interface của TPPA): `AlignBothAxes(azArcMin, altArcMin)`, `Abort()`, `GetStatus()` — tái dùng `MoveBothAxes` / `RunAlignMove` (đã có: gửi ALIGN, chờ ack `ok`, poll `?` tới `READY`/`ALIGN_COMPLETED`, timeout 90 s).

### 3.2 Tab `SOFTWARE SETTING` (thay `TPPA OPTION`)

| Setting trên hình | Nguồn mới | Ai dùng |
|---|---|---|
| Correction axis mode | MLAstro (`...CorrectionMode`) | Correction engine: `Both` = 1 lệnh ALIGN 2 trục; `Auto` = trục sai số lớn hơn |
| Automated adjustment timeout | MLAstro | Trần thời gian vòng sửa phía MLAstro |
| Reverse Azimuth / Altitude Axis | MLAstro (đã có) | Đảo dấu lệnh trước khi gửi firmware |
| Azimuth backlash compensation | MLAstro | Bù backlash trục Az (giữ đơn vị bước/arcmin như hiện tại) |
| Correction Safety Factor | MLAstro (`...CorrectionFactorPercent`, 75%) | Nhân vào arcmin trước khi di chuyển |
| Enable overshoot / Run overshoot Up / Down (+ arcmin) | MLAstro | Cộng vượt target rồi quay lại (chuỗi nhiều lần đo — hợp với `RequestMeasurement`) |
| Do automated adjustments | **Không cần** | External mode đã ngầm định; có thể giữ 1 checkbox "External correction (TPPA broker)" |

### 3.3 Correction engine (MVP — bám công thức bản merged, KHÔNG port model học)

1. Nhận `Measurement` (valid) → log + hiển thị.
2. `Adjusting=true` (kèm `MeasurementId`) **trước khi** quay motor (để TPPA ngừng chụp ngay).
3. Tính lệnh:
   - `azArcMin = |AzimuthErrorArcMin| × SafetyFactor` (dấu chỉ chọn hướng: âm = Right, dương = Left)
   - `altArcMin = |AltitudeErrorArcMin| × SafetyFactor` (+ overshoot nếu bật cho hướng hiện tại)
   - Mode `Both` ⇒ `AlignBothAxes(az, alt)` (1 lệnh); `Auto` ⇒ chỉ trục lớn hơn.
   - Reverse Az/Alt ⇒ đảo dấu; kẹp biên an toàn (max step) để tránh nhảy lớn khi số liệu lỗi.
4. Chờ `ALIGN_COMPLETED` (+ settle) → `Adjusting=false` + `ExpectCompletionWithinMs` đã dùng.
5. Chờ `Measurement` tiếp theo; nếu `ToleranceReached` → dừng theo policy (dừng khi đạt N lần liên tiếp ≤ tolerance, N cấu hình) rồi gửi `Stop`.
6. Lỗi (timeout / mất link / motor fault) ⇒ `Fault` + dừng session; tự reconnect hardware ở lần chạy sau.

### 3.4 Hardware link

- Kết nối sẵn serial/wireless như hiện tại (watchdog + stale-serial takeover đã có ở firmware ≥ 1.7/1.2.2).
- TPPA **không** mở cổng nữa ⇒ không còn `BeginExternalControl` / `PauseQueryGlobal`; MLAstro poll `?` liên tục trong suốt session.

## 4. Lộ trình & tiêu chí hoàn thành

| Mốc | Nội dung | Done khi |
|---|---|---|
| M0 | Chốt hợp đồng §1 + tạo nhánh/repo | file `broker-contract.md` được review, có ví dụ payload |
| M1 | TPPA fork: publish `Measurement` + cổng `adjusting` + 4 setting | `SimulatedExternalController` chạy hết 6 case ở §2.4 |
| M2 | MLAstro plugin: tách TPPA ra, build Release sạch, tab `SOFTWARE SETTING` | plugin chạy độc lập, không còn tham chiếu TPPA |
| M3 | Broker client + correction engine + hardware | chạy thật: 3 điểm → sửa → đo lại, hội tụ dưới tolerance |
| M4 | Hardening: TTL/watchdog/`Fault`/reconnect/log tương quan | test crash MLAstro, rút cáp, Stop giữa chừng |
| M5 | Docs + PR upstream + release nội bộ | PR mở trên `isbeorn/nina.plugin.polaralignment`, docs kèm theo |

## 5. Test plan (tóm tắt)

- Hội tụ: 3 điểm → sửa nhiều vòng → đạt `ToleranceReached` → Stop.
- Overshoot-and-return: bật overshoot Up, kiểm tra chuỗi đo-sửa-đo.
- Mode `Auto` vs `Both`; bán cầu Nam; Reverse Az/Alt.
- Lỗi: solve fail liên tiếp; MLAstro treo khi `adjusting=true` (TTL nhả); rút cáp giữa lúc sửa; user bấm Stop trên UI TPPA giữa cửa sổ sửa; đóng cửa sổ TPPA; sequence item bị skip.
- Broker: Kind lạ, trùng `CommandId`, request sai thứ tự, TPPA load sau MLAstro.

## 6. Rủi ro & câu hỏi mở

1. **Cờ `adjusting` dạng boolean** dễ race (không biết theo lần đo nào) ⇒ đã bù bằng `MeasurementId` + TTL; nếu upstream muốn "window có ID" thì chuyển sang `AdjustmentGranted/Id` (đề xuất của họ).
2. **Chính sách dừng**: TPPA (upstream) cần **2 lần solve liên tiếp** ≤ tolerance; ở external mode ta **tự quyết** — cần ghi rõ trong docs để user không tưởng TPPA tự dừng.
3. **Estimator**: bản external dùng đúng mode đang cấu hình trong TPPA (legacy/continuous) — cần ghi trong FAQ để tránh so sánh "sai số khác nhau".
4. **PR**: upstream có thể đã tự làm feature này ⇒ phải mở issue/PR **sớm** với hợp đồng §1 trước khi code xong; nhánh phải sạch, diff nhỏ, có test.
5. **Không phát hành fork TPPA cho user** khi PR chưa merge (trùng plugin identity ⇒ 2 TPPA trong NINA).

## 7. Việc phải chốt trước khi code (blocking)

1. `ExternalEvent`/`ExternalCommand` — chấp nhận đúng tên topic của họ hay dùng tên khác?
2. Có cần `AdjustmentGranted` có ID (đúng spec của họ) hay chỉ boolean `adjusting` (đơn giản hơn)?
3. Chính sách dừng phía MLAstro: dừng sau 1 hay N lần ≤ tolerance?
4. `SafetyFactor` + `Overshoot` **chắc chắn** thuộc MLAstro (không còn trong TPPA)?
5. Có gửi kèm `DeclinationSpread` / cờ "stability" trong `Measurement` không?
6. Tên plugin/assembly mới của MLAstro và có giữ `PluginId` cũ không (đề xuất: giữ).