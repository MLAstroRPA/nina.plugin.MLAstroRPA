# MLAstroRPA WebSocket Protocol Guide

This document is intended to:
- Help you build a Mobile/PC app that controls the mount the same way as the Web UI.
- Describe the WebSocket protocol currently used by the ESP32 backend.
- Provide practical message examples for fast implementation.

## 1. Connection

- Endpoint: `ws://<device-ip>/ws`
- **mDNS (khuyến nghị cho plugin/PC):** `ws://MLAstroRPA.local/ws` (hostname cố định của firmware;
  nếu mDNS không hoạt động thì dùng IP trực tiếp, ví dụ `ws://192.168.4.1/ws`).
- If using HTTPS reverse proxy: `wss://<device-ip>/ws`
- Data format: JSON text frames
- Frame type: text only (`WS_TEXT`)

### 1.1 Client roles (Web vs PC)

| Vai | Số lượng | Quyền |
| :--- | :--- | :--- |
| **Web client** (browser UI) | tối đa **1** | Điều khiển khi không có PC giữ quyền |
| **PC client** (plugin/PC qua WebSocket) | tối đa **1** | Luôn ưu tiên: điều khiển + monitor |

Important notes:
- Chỉ **1 Web client**: client mới không gửi handshake trong ~1.5 s sẽ bị từ chối
  (`{"cmd":"connectionRejected"}` rồi đóng kết nối) — giữ nguyên hành vi cũ.
- Khi PC đã giữ quyền: Web **vẫn nhận telemetry/log (monitor)** nhưng **mọi lệnh điều khiển bị khóa**
  (trả `{"status":"locked"}`). Web tự mở khóa trở lại khi PC nhả quyền (không cần refresh).
- Nếu Serial control đang active (`[MLAstroRPA-TC]`), handshake qua WebSocket bị từ chối.

### 1.2 PC handshake over WebSocket

Plugin/PC dùng **cùng endpoint** `/ws` như Web UI và phân biệt bằng từ khóa handshake
(giống serial). Gửi ngay frame đầu tiên sau khi kết nối:

```json
{ "cmd": "handshake", "data": { "key": "MLAstroRPA-TC", "client": "NINA-MLAstroRPA+TPPA/2.0.2.0" } }
```

Server trả lời:

```json
{ "cmd": "handshakeResult", "result": true, "transport": "ws", "fw_ver": "v1.2.x", "sn": "AA:BB:CC:DD:EE:FF", "serial_locked": true, "link": "AP" }
```

`link` cho biết phiên WS này đi vào qua interface NÀO của thiết bị (tính theo `remoteIP` của TỪNG client):

| Giá trị | Nghĩa |
| --- | --- |
| `"AP"` | Client đang join hotspot của ESP32 (IP client thuộc subnet AP) |
| `"STA"` | Client đi qua router mà ESP32 đã join |

Từ chối (PC thứ hai / Serial đang giữ quyền / sai key):

```json
{ "cmd": "handshakeResult", "result": false, "reason": "Another PC session is already in control" }
```

Sau khi handshake thành công:
- PC trở thành controller duy nhất; `stopAllMotion(true)` được gọi để tránh chạy ngoài tầm kiểm soát.
- Mọi Web client nhận `{"cmd":"controlTakenBySerial", "serial_locked":true, "reason":"PC took over control (Wireless)..."}`
  → Web chuyển sang monitor + khóa điều khiển.
- **PC thứ hai bị từ chối và đóng kết nối** (close code `1008`). Ngoại lệ: cùng IP và socket cũ đã chết
  (plugin restart) → takeover phiên cũ.

### 1.3 Nhả quyền

- Chủ động: `{ "cmd": "releaseControl" }` → trả `{"cmd":"releaseControlResult","result":true}`.
- Tự động khi PC client ngắt kết nối (hoặc socket timeout — server ping mỗi 15 s):
  `{"cmd":"controlReleased", "serial_locked":false, "reason":"PC disconnected. Web control available."}`
  → Web mở khóa và nhận lại quyền điều khiển ngay, không cần refresh.

> **Ghi chú:** `serial_locked` trong telemetry phản ánh "Web có đang là master hay không".
> Telemetry được gửi cho **mọi** client, kể cả khi Web đang bị khóa.

### 1.4 Bảng ánh xạ Serial → WebSocket (cho app/plugin)

Giao thức WebSocket **giữ bộ lệnh JSON riêng** (không dùng lại từ khóa serial): JSON có ngữ nghĩa
(`axis`/`direction`/`speed`) và hỗ trợ những thứ serial không có (`simultaneous` cho ALIGN 2 trục,
telemetry push 250 ms, `handshake`). Bảng dưới là ánh xạ cho app đang chuyển từ serial sang WS:

| Serial | WebSocket | Ghi chú |
| :--- | :--- | :--- |
| `[MLAstroRPA-TC]` | `{"cmd":"handshake","data":{"key":"MLAstroRPA-TC"}}` | chỉ gửi 1 lần sau khi kết nối |
| `?` | *(bỏ qua)* | telemetry do firmware đẩy ~250 ms |
| `MAzL:1` / `MAzR:1` / `MAlU:1` / `MAlD:1` | `{"cmd":"move","data":{"axis":"az\|alt","direction":±1,"speed":1..5}}` | giữ nút = chỉ cần gửi **một lần**; không cần lặp |
| `MAzL:0` (nhả nút) | `{"cmd":"stopMove","data":{"axis":"az\|alt"}}` | giảm tốc mềm đúng trục; `stop` là hard-stop (nút STOP/E-STOP) |
| `ReDe`/`ReAM`/`ReAS` + `JoRe:1` + `MAlU:1` | `{"cmd":"moveRelative","data":{"axis":…,"direction":±1,"angle":deg,"speed":1..5}}` | WS tách riêng lệnh dịch một góc |
| `JoRe:1` / `ReDe:X` / `ReAM:X` / `ReAS:X` | `{"cmd":"saveConfig","data":{"relative":{"mode":true,"d":X,"m":X,"s":X}}}` | WS **không có lệnh mode riêng**: phải lưu xuống device (giống Web UI) rồi đọc lại — xem 6.1b |
| `AzED..,AlED..,AAll:1` | `{"cmd":"align","data":{"ra_error":arcsec,"dec_error":arcsec,"simultaneous":true}}` | arcsec **có dấu**; `AzAN` → chỉ AZ, `AlAN` → chỉ ALT |
| `AzED/AzEM/AzES/AzDi` (+`Al…`) **không kèm** `AzAN`/`AlAN`/`AAll` | `{"cmd":"saveConfig","data":{"align":{"az":{"d":…,"m":…,"s":…,"dir":bool},"alt":{…}}}}` | Serial chỉ **ghi giá trị sai số** vào FRAM (không chạy motor) — WS không được dịch thành `align` |
| `ApplyConf:1` | `{"cmd":"applyConfig","data":{…toàn bộ cài đặt…}}` | WS không có lệnh "apply tất cả" rời |
| `STOP:1` / `ESTOP:1` | `{"cmd":"stop"}` / `{"cmd":"forceStop"}` | |
| `ReER:1` | `{"cmd":"resetError"}` | |
| `SetH:1` / `RetH:1` / `RstH:1` | `setHome` / `returnHome` / `resetHome` | |
| `SLvl:X` | `{"cmd":"speedLevel","data":{"level":X}}` | |
| chuỗi cấu hình + `Save&Reboot:1` | `{"cmd":"saveConfig","data":{...}}` rồi `{"cmd":"reboot"}` | payload dạng `limits`/`motor`/`backlash`/`align`/`wifi`/`wifi_ap` |
| `Disconnect` | `{"cmd":"releaseControl"}` | nhả quyền, Web mở khóa ngay |
| `reboot` | `{"cmd":"reboot"}` | giống nút REBOOT trên Web UI: broadcast `sys_status: REBOOTING` → `delay(500)` → `ESP.restart()`. ⚠ client phải **đang giữ quyền** (PC-controller hoặc web master), nếu không sẽ nhận `status:"locked"` ⇒ **đừng gửi `releaseControl` trước `reboot`** |
| `APss` / `APpa:X` / `APip` / `APsu` | `{"cmd":"saveConfig","data":{"wifi_ap":{"ssid":…,"pass":…,"ip":…,"subnet":…},"no_reboot":true}}` + `{"cmd":"reboot"}` | ghi FRAM như Serial; `no_reboot` bắt buộc để nhận được ack `configSaved` (xem 5.4) |
| `STAs` / `STAp:X` | `{"cmd":"saveConfig","data":{"wifi":{"ssid":…,"pass":…},"no_reboot":true}}` + `{"cmd":"reboot"}` | như trên |
| `STAp:?` / `APpa:?` (truy vấn) | `{"cmd":"getConfig","data":{"keys":["pass"\|"wifi_ap"]}}` | trả về `configRead` — **chỉ gửi cho client đã hỏi** (xem 5.5). Password không nằm trong snapshot/broadcast |
| `Home:X` (read-only), `STAi` (bị bỏ qua) | *(bỏ qua, không phải lệnh)* | firmware cũng bỏ qua ở đường Serial |
| `STOP:0` / `ESTOP:0` (nhả nút) | *(bỏ qua)* | sự kiện nhả nút, không gửi gì |
| `AzDi:X` / `AlDi:X` **đơn độc** (toggle hướng) | `{"cmd":"saveConfig","data":{"align":{"az":{…},"alt":{…}}}}` | dòng setter **một phần** (chỉ `AzDi`) — các trường còn lại phải gửi lại đúng giá trị đang lưu, không được xoá về 0 |

> Cách khác nếu muốn "một bộ từ khóa duy nhất": app tự viết lớp dịch như plugin NINA đang làm
> (`MlastroWebSocketService.Translate`). Khuyến nghị giữ WS-native và dịch ở phía client.

### 1.5 Replay log cho client mới

Khi một client kết nối, firmware gửi lại các dòng log gần đây nhất (ring buffer 12 dòng) để Web UI
vừa mở/refresh vẫn thấy được các lỗi vừa xảy ra.

- Replay được **hoãn 1 s** và **bỏ qua với client PC** (đã handshake `MLAstroRPA-TC`): plugin/PC giữ
  bảng log riêng của nó, nếu nhận replay thì bảng log sẽ hiện lại sự kiện của phiên trước ngay khi
  vừa kết nối.
- Web client (không handshake) nhận replay như cũ sau khoảng 1 s.

## 2. Message structure
### 2.1 Client -> Server

All commands are sent in this format:

```json
{
  "cmd": "<command_name>",
  "data": {}
}
```

### 2.2 Server -> Client

Server sends two message categories:
- Periodic state stream (about every 250 ms) for UI updates.
- Event/response messages for commands, errors, tuning, calibration, OTA, etc.

## 3. First message after connect

Immediately after a successful connection, server sends a large init snapshot:

```json
{
  "serial_locked": true,
  "role": "monitor",
  "control_owner": "pc-wireless",
  "link": "AP",
  "speedLevel": 3,
  "fw_ver": "v2.x.x",
  "homed": true,
  "wifi_ap": {
    "ssid": "MLAstroRPA-A1B2C3",
    "ip": "192.168.4.1",
    "subnet": "255.255.255.0",
    "mac": "AA:BB:CC:DD:EE:01"
  },
  "ip": "192.168.1.50",
  "ssid": "YourWiFi",
  "sta_mac": "AA:BB:CC:DD:EE:FF",
  "align_mode": { "simultaneous": true },
  "limits": {
    "az_min": -9,
    "az_max": 9,
    "alt_min": -14,
    "alt_max": 14,
    "enable_softlimit": true,
    "has_factory_zero": true
  },
  "motor": {
    "az_run_ma": 1000,
    "az_hold_ma": 500,
    "az_microsteps": 64,
    "alt_run_ma": 1000,
    "alt_hold_ma": 500,
    "alt_microsteps": 64,
    "max_speed": 200,
    "az_accel": 30000,
    "az_decel": 30000,
    "alt_accel": 30000,
    "alt_decel": 30000,
    "show_steps": false,
    "az_spd": 1000,
    "alt_spd": 1000,
    "az_reverse": false,
    "alt_reverse": false,
    "az_spread_cycle": false,
    "alt_spread_cycle": false,
    "enable_hardlimit": true,
    "show_hardlimit_monitor": false,
    "az_sg_thrs": [0, 0, 0, 0, 0],
    "alt_sg_thrs": [0, 0, 0, 0, 0],
    "az_tcool_presets": [0, 0, 0, 0, 0, 0],
    "alt_tcool_presets": [0, 0, 0, 0, 0, 0],
    "stall_time": 200,
    "escape_rotations": 3
  },
  "backlash": {
    "enable": false,
    "az_steps": 0,
    "alt_steps": 0,
    "overshoot": true,
    "overshoot_d": 0,
    "overshoot_m": 0,
    "overshoot_s": 0,
    "overshoot_up": false,
    "overshoot_down": true
  },
  "serial": {
    "baud": 115200,
    "databits": 8,
    "stopbits": 1,
    "parity": 0,
    "watchdog": true,
    "log": false,
    "simplify_telemetry": false
  },
  "relative": {
    "mode": false,
    "d": 0,
    "m": 0,
    "s": 1
  },
  "align": {
    "az": { "d": 0, "m": 0, "s": 0, "dir": false },
    "alt": { "d": 0, "m": 0, "s": 0, "dir": false }
  }
}
```

Field notes:

- `serial_locked` — `true` when a PC (Serial or Wireless) owns control; Web clients are then locked to
  monitoring.
- `role` — `master` (this client owns control) or `monitor`.
- `control_owner` — `pc-serial` | `pc-wireless` | `web` | `none`.
- Everything from `wifi_ap` downwards is the **configuration block**; the very same block is re-sent
  on every change (section 3.1), so a client should treat this frame as "(re)load my settings cache".
- ⚠️ **Password KHÔNG có trong frame này** (cả `wifi_ap.pass` lẫn `pass` của STA): frame được gửi cho
  mọi client nên password sẽ bị phát tán. Client muốn xem password thì **hỏi riêng** bằng lệnh
  `getConfig` (xem 5.5) và sẽ nhận `configRead` — chỉ gửi cho client đã hỏi.
- Section `wifi_ap` vì vậy chỉ còn `ssid` / `ip` / `subnet` / `mac`; `ip` và `ssid` ở cấp cao nhất là
  thông tin STA hiện tại (IP do router cấp, có thể rỗng khi chưa kết nối).

### 3.1 Config push (cấu hình thay đổi khi client đang mở)

Mọi thay đổi cấu hình — bất kể đến từ đường nào (Web UI, PC qua WebSocket, hay Serial
`ApplyConf`/`Save&Reboot`) — đều được **push lại ngay** cho tất cả client WebSocket:

```json
{ "config_pushed": true, "speedLevel": 3, "fw_ver": "v2.x.x", "homed": true,
  "wifi_ap": { ... }, "limits": { ... }, "motor": { ... }, "serial": { ... },
  "backlash": { ... }, "relative": { ... }, "align": { ... } }
```

- Các section cấu hình (`wifi_ap` + thông tin STA + `align_mode` + `limits` + `motor` +
  `serial` + `backlash` + `relative` + `align`) do **một hàm duy nhất** (`fillConfigSections()`)
  sinh ra, dùng chung cho frame này và frame snapshot lúc kết nối → thêm một cài đặt mới chỉ
  cần sửa một chỗ, web và PC không thể lệch nhau.
- Client (web/plugin) phải coi `config_pushed` (hoặc sự có mặt của `speedLevel`/`fw_ver`) là
  **làm mới toàn bộ cache cấu hình**, không giữ bản cũ đọc được lúc kết nối.
- `saveConfig` và `applyConfig` gửi frame này TRƯỚC ack `configSaved`/`configApplied`.

## 4. Telemetry stream (server push)

Roughly every 250 ms, server pushes one telemetry packet:

```json
{
  "pos_az": 1.234,
  "pos_alt": -0.456,
  "align_moved_az": 0.12,
  "align_moved_alt": -0.03,
  "steps_az": 1234,
  "steps_alt": -567,
  "out_speed_az": 0.1,
  "out_speed_alt": -0.2,
  "speed_az": 120.5,
  "speed_alt": 110.2,
  "homed": true,
  "isAutoMoving": false,
  "isCalibrating": false,
  "sys_status": "READY",
  "rssi": -55,
  "sta_qual": 2,
  "sta_ip": "192.168.1.50",
  "ap_ready": true,
  "ap_ip": "192.168.4.1",
  "clients": [
    { "mac": "AA:BB:CC:DD:EE:FF", "ip": "192.168.4.2", "name": "Unknown" }
  ]
}
```

Chất lượng đường STA + trạng thái AP (dùng cho dòng `AP:` / `STA:` của plugin và Web UI):

| Field | Nghĩa |
| --- | --- |
| `sta_qual` | `0` = chưa vào router · `1` = đã vào router + có IP nhưng KHÔNG có internet · `2` = có internet |
| `sta_ip` | IP LAN mà router cấp cho thiết bị (`""` khi chưa vào router) |
| `ap_ready` | AP (hotspot) của thiết bị đã lên và có IP |
| `ap_ip` | IP hotspot của thiết bị (`""` khi AP không lên) |

> `ap_ready`/`ap_ip` + `link` (ở `handshakeResult` và frame đầu tiên) cho phép client hiển thị đúng dòng
> `AP: connected/ready/error <IP>`: **connected** khi chính client đi qua AP, **ready** khi AP đã lên
> nhưng client đi đường khác (STA/cáp USB), **error** khi AP không lên.
> `sta_qual` dò bằng TCP-connect probe tới `1.1.1.1:53` / `8.8.8.8:53` (timeout 1.2 s) mỗi 10 s,
> chạy ở task riêng Core 1 priority thấp nên không ảnh hưởng telemetry. Firmware < 1.7.0 không
> gửi 2 field này — plugin suy ra mức tối thiểu từ `rssi` (> -1000 ⇒ đang vào router).

If `show_hardlimit_monitor=true`, telemetry also includes:
- `running`
- `diag_az`, `diag_alt`
- `sg_az`, `sg_alt`
- `cs_az`, `cs_alt`
- `tstep_az`, `tstep_alt`

## 4.1 ERROR telemetry (server push, edge-triggered)

Trạng thái lỗi/cảnh báo được gửi riêng (KHÔNG nằm trong gói 250 ms) mỗi khi trạng thái **đổi**:

```json
{ "error": "ERROR:Sys:0,AzNC:0,AlNC:0,...,AzSL:0,AlSL:0,Esc:0,CmdRf:0" }
```

- Cùng nội dung mã lỗi như dòng `ERROR:` của Serial (xem `Serial-protocol.md` §7) → alarm panel
  của web/plugin giống nhau ở mọi môi trường điều khiển.
- `CmdRf` = bitfield "lệnh bị từ chối vì soft-limit" (bit tự tắt sau ~1.5 s không còn bị từ chối).
- **Đường WebSocket gửi độc lập với đường Serial**: không phụ thuộc buffer TX của UART (telemetry
  250 ms chiếm gần hết đường TX nên nếu chờ buffer thì client WebSocket gần như không bao giờ
  nhận được alarm). Mỗi đường có mốc "đã gửi lần cuối" riêng.
- Ngay sau handshake thành công, mốc so sánh được **reset** → client vừa kết nối nhận ngay trạng
  thái lỗi đang tồn tại, không phải chờ lỗi mới xuất hiện.
- **Chọn MỘT kênh để thông báo cho người dùng**: cùng một sự việc (v.d. jog chạm soft-limit) vừa có
  mã lỗi ở đây vừa có frame `alert` (mục 6.1). Kênh mã lỗi là kênh **có ở cả Serial và WebSocket**,
  nên app/plugin nên dùng nó làm nguồn thông báo duy nhất (bảng Alarm + toast) và chỉ giữ `alert`
  trong file log — tránh 2 hộp thoại cho cùng một sự việc khi điều khiển bằng WebSocket.

## 5. Command reference (Client -> Server)

## 5.1 Movement and control

### `move`
Continuous jog movement (press and hold).

```json
{ "cmd": "move", "data": { "axis": "az", "direction": 1, "speed": 3 } }
```

- `axis`: `"az"` or `"alt"`
- `direction`: `1` or `-1`
- `speed`: 1..5

### `moveRelative`
Move by angle.

```json
{ "cmd": "moveRelative", "data": { "axis": "alt", "direction": -1, "angle": 0.25, "speed": 3 } }
```

### `stopMove`
Per-axis **decelerating** stop — send it when a jog button is released (WebSocket equivalent of the
Serial `MAzL:0` / `MAlU:0`).

```json
{ "cmd": "stopMove", "data": { "axis": "alt" } }
```

- `axis`: `"az"` or `"alt"`.
- Only that axis is touched, and it is stopped the way a serial jog release stops it: with a
  deceleration ramp while the axis is running, or by cancelling the armed far target
  (`setCurrentPosition()`) when it is already idle — so a released jog can never keep moving.
- A 2 s safety net force-cancels the target if the axis is somehow still running.

### `stop`
Soft-stop **both** axes and abort every running workflow. Decelerates with the configured deceleration
(`setAcceleration(decel)` + `stop()`) and then cancels the armed far target, so the axes are guaranteed
to halt. This is what the STOP button sends.

```json
{ "cmd": "stop", "data": {} }
```

Broadcast reply: `{ "sys_status": "STOPPED" }`.

### `forceStop`
Emergency stop (E-STOP): cancels the far target **immediately** on both axes
(`setCurrentPosition()`, i.e. no deceleration ramp) and aborts every running workflow. This is what the
E-STOP button sends.

```json
{ "cmd": "forceStop", "data": {} }
```

Broadcast reply: `{ "sys_status": "FORCED_STOP" }`.

### `speedLevel`
Set global speed level.

```json
{ "cmd": "speedLevel", "data": { "level": 3 } }
```

### `resetError`
Unlock system after hard limit/driver error.

```json
{ "cmd": "resetError", "data": {} }
```

## 5.2 Home / factory zero

### `setHome`
Set current position as home (0,0).

```json
{ "cmd": "setHome", "data": {} }
```

### `resetHome`
Unset home state.

```json
{ "cmd": "resetHome", "data": {} }
```

### `returnHome`
Return to home if homed.

```json
{ "cmd": "returnHome", "data": {} }
```

### `setFactoryZero`
Set factory zero at current position and also set home.

```json
{ "cmd": "setFactoryZero", "data": {} }
```

## 5.3 Align / calibration / center

### `align`
Error values are in arcseconds.

```json
{ "cmd": "align", "data": { "ra_error": 120.0, "dec_error": -45.0 } }
```

### `calibAxis`
Calibrate one axis or all axes.

```json
{ "cmd": "calibAxis", "data": { "axis": "all", "travel_az": 20.0, "travel_alt": 30.0 } }
```

- `axis`: `"az" | "alt" | "all"`
- `travel_az`, `travel_alt` are optional

### `autoCenter`
Auto-center after calibration.

```json
{ "cmd": "autoCenter", "data": { "axis": "all" } }
```

## 5.4 Config

### `saveConfig`
Apply and save to FRAM (may reboot when WiFi/AP settings are changed).

```json
{
  "cmd": "saveConfig",
  "data": {
    "origin": "pcPlugin",
    "limits": { "az_min": -9, "az_max": 9, "alt_min": -14, "alt_max": 14, "enable_softlimit": true },
    "motor": { "az_run_ma": 1000, "az_hold_ma": 500, "alt_run_ma": 1000, "alt_hold_ma": 500 },
    "backlash": { "enable": false, "az_steps": 0, "alt_steps": 0 },
    "relative": { "mode": false, "d": 0, "m": 0, "s": 1 },
    "align": {
      "az": { "d": 0, "m": 0, "s": 0, "dir": false },
      "alt": { "d": 0, "m": 0, "s": 0, "dir": false }
    },
    "align_mode": { "simultaneous": true },
    "wifi": { "ssid": "MyWiFi", "pass": "12345678" },
    "wifi_ap": { "ssid": "MLAstroRPA-A1B2C3", "pass": "MLAstroRPA", "ip": "192.168.4.1", "subnet": "255.255.255.0" },
    "no_reboot": true
  }
}
```

- Mọi section **tuỳ chọn**: chỉ cần gửi những nhóm muốn đổi; thiếu section nào thì giá trị đang lưu
  của section đó được giữ nguyên.
- `origin` (tuỳ chọn) được firmware echo lại trong ack `{"status":"configSaved","origin":…}` để web
  phân biệt lượt lưu của PC plugin với lượt lưu của chính web UI.
- `wifi` (STA) và `wifi_ap` là **hai nhóm ghi FRAM + cần reboot**: mặc định firmware tự
  `ESP.restart()` sau khi lưu (500 ms) và **không** gửi ack. Client nên gửi kèm `no_reboot: true`,
  chờ ack `configSaved` rồi tự gửi `{"cmd":"reboot"}` (đúng như web UI/plugin đang làm).
- Sau khi lưu, firmware push lại toàn bộ cấu hình (frame `config_pushed` — xem 3.1) trước ack.
- ⚠️ **`wifi.pass` / `wifi.ssid` / `wifi_ap.*` rỗng = GIỮ NGUYÊN giá trị đang lưu trong FRAM**
  (`ConfigManager::saveWiFi`/`saveAP` bỏ qua field rỗng; `handleSaveConfig` ghi log
  `SAVE: WiFi (STA) empty field(s) -> kept existing`). Đây là lớp bảo vệ bắt buộc vì UI có thể gửi
  `pass:""` khi ô mật khẩu chưa được sync/đang gõ dở — nếu ghi thẳng thì mật khẩu STA mất và mọi lần
  kết nối sau đó fail `reason 15` (`4WAY_HANDSHAKE_TIMEOUT`) và không tự khỏi được.

### `applyConfig`
Apply to RAM only, do not save to FRAM.

```json
{ "cmd": "applyConfig", "data": { "limits": { "az_min": -9, "az_max": 9, "alt_min": -14, "alt_max": 14 } } }
```

## 5.5 WiFi / system / admin

### `scanWifi`

```json
{ "cmd": "scanWifi", "data": {} }
```

### `connectWifi`
Do not save to FRAM, runtime reconnect/test only.

```json
{ "cmd": "connectWifi", "data": { "ssid": "MyWiFi", "pass": "12345678" } }
```

### `getConfig`
Đọc **theo yêu cầu** các field cấu hình (dùng cho password WiFi). Đối xứng với `STAp:?` / `APpa:?`
của đường Serial.

```json
{ "cmd": "getConfig", "data": { "keys": ["pass"] } }        // password WiFi (STA)
{ "cmd": "getConfig", "data": { "keys": ["wifi_ap"] } }     // ssid + password của AP
{ "cmd": "getConfig" }                                        // mặc định: cả hai
```

Phản hồi (**chỉ** gửi cho client đã hỏi, KHÔNG broadcast):

```json
{ "cmd": "configRead", "data": { "ssid": "YourWiFi", "pass": "duck1352",
                                 "wifi_ap": { "ssid": "MLAstroRPA", "pass": "MLAstroRPA" } } }
```

> ⚠️ **Vì sao password không nằm trong snapshot/broadcast:** frame snapshot và `config_pushed` được
> gửi cho **mọi** client và còn được push lại mỗi khi cấu hình đổi ⇒ nếu để password ở đó thì nó bị
> phát tán liên tục và không thể thu hồi. Nay client chỉ nhận password khi **chủ động hỏi** (web/plugin
> bấm nút con mắt 👁 → gửi `getConfig` → nhận `configRead` → mới hiển thị).
> `getConfig` **được phép cả khi client không giữ quyền điều khiển** vì đây chỉ là lệnh đọc.
> Mật khẩu **rỗng** trong `configRead` là thông tin ĐÚNG (thiết bị đang không có mật khẩu), không phải lỗi.

### `reboot`

```json
{ "cmd": "reboot", "data": {} }
```

Flow (giống hệt nút REBOOT trên Web UI): firmware log `System Rebooting command received...`, broadcast
`{"sys_status":"REBOOTING"}` cho **mọi** client, `delay(500)` rồi `ESP.restart()`. Mọi state (phiên PC,
quyền điều khiển) nằm trong RAM nên bị xoá theo — client chỉ cần kết nối lại sau vài giây.

⚠️ Chỉ client **đang giữ quyền điều khiển** (PC-controller qua `handshake`, hoặc web master) mới được
reboot; client khác nhận `{"status":"locked"}` và thiết bị **không** restart. Vì vậy đừng gửi
`releaseControl` ngay trước `reboot`.

### `factoryReset`
Clear config (invalidate magic) and reboot.

```json
{ "cmd": "factoryReset", "data": {} }
```

### `loginAdmin`

```json
{ "cmd": "loginAdmin", "data": { "pass": "admin-password" } }
```

### `changePassword`

```json
{ "cmd": "changePassword", "data": { "old_pass": "old", "new_pass": "new" } }
```

### `setSerialLog`
Enable/disable forwarding the device log to WebSocket clients (the `log` frames). Same setting as the
*serial log* checkbox in the Admin config.

```json
{ "cmd": "setSerialLog", "data": { "enabled": true } }
```

Reply: `{ "status": "serialLogUpdated", "enabled": true }`.

### `setCommWatchdog`
Enable/disable the Serial communication watchdog (the firmware releases the Serial handshake when the
PC stops polling). Same setting as the Admin config checkbox.

```json
{ "cmd": "setCommWatchdog", "data": { "enabled": true } }
```

Reply: `{ "status": "commWatchdogUpdated", "enabled": true }`.

## 5.6 OTA / tuning

### `otaUpdate`

```json
{ "cmd": "otaUpdate", "data": { "type": "firmware", "url": "https://.../firmware.bin", "reboot_after": true } }
```

- `type`: `"firmware"` or `"spiffs"`

### `startTuning`

```json
{ "cmd": "startTuning", "data": { "axis": 0, "fwdTime": 10, "revTime": 20 } }
```

### `startTuningTcool`

```json
{ "cmd": "startTuningTcool", "data": { "axis": 1, "fwdTime": 10, "revTime": 20 } }
```

- `axis`: `0 = az`, `1 = alt`

## 6. Important event/response messages (Server -> Client)

### 6.1 Generic status/alert

```json
{ "status": "configSaved", "timestamp": 123456 }
{ "status": "configApplied" }
{ "status": "serialLogUpdated", "enabled": true }
{ "status": "commWatchdogUpdated", "enabled": true }
{ "sys_status": "READY" }
{ "sys_status": "REBOOTING" }
{ "alert": "System Locked! Please Reset Error first." }
{ "error": "ERROR:Sys:0,AzNC:0,AlNC:0,AzOT:0,...,Esc:0" }
{ "log": "[12:00:01] ..." }
```

`configSaved` mang theo `origin` do client gửi (`"origin":"webSave"` mặc định). App/plugin nên gửi
`origin` riêng (vd `"pcPlugin"`) để Web UI không hiểu nhầm ack này là ack của lượt *SAVE ALL & REBOOT*.

`alert` là **kênh thông báo phụ** (Web UI hiển thị bằng modal): nó chỉ mang câu chữ diễn giải cho
người dùng, **không** phải nguồn dự liệu. Mọi sự việc quan trọng đều **đồng thời** xuất hiện trong
`error` (mã lỗi) và/or `log` — vì vậy app/plugin nên báo cho người dùng **một lần** theo mã lỗi và
chỉ ghi `alert` vào file log (xem ghi chú ở mục 4.1).

`error` (edge-triggered — chỉ gửi khi trạng thái lỗi THAY ĐỔI) dùng **đúng định dạng dòng
`ERROR:` của Serial**, mỗi mã là `Code:value` với `0 = không lỗi`, `1 = WARNING`, `2 = ERROR`.
Các mã: `Sys, AzNC, AlNC, AzOT, AlOT, AzPW, AlPW, AzSA, AzSB, AlSA, AlSB, AzOL, AlOL,
AzHL, AlHL, AzSL, AlSL, Esc, CmdRf`. App/plugin nên bỏ prefix `ERROR:` rồi tách theo `,` để hiển thị
bảng Alarm và log lỗi.

`CmdRf` là **bitfield "lệnh bị TỪ CHỐI vì soft-limit"** (không phải 0/1/2): bit bật ngay khi lệnh bị
từ chối và tự tắt sau ~1.5 s nếu loại lệnh đó không còn bị từ chối nữa. App/plugin giải mã từng bit:
`0x01` relative move AZ · `0x02` relative move ALT · `0x04` align target AZ · `0x08` align target ALT ·
`0x10` jog AZ tại giới hạn · `0x20` jog ALT tại giới hạn · `0x40` align nhánh overshoot (ALT).
VD `CmdRf:17` (= 0x11) → relative AZ **và** jog AZ đang bị từ chối. Tất cả đều mức **WARNING**,
không khóa hệ thống.

### 6.1b `relative` (broadcast khi chế độ Jog/Relative thay đổi)

Mọi thay đổi mode/độ dịch tương đối — dù đến từ client Web (`saveConfig`/`applyConfig`), từ app PC
hay từ **lệnh Serial** `JoRe:1` / `ReDe:X` / `ReAM:X` / `ReAS:X` — đều được firmware phát lại ngay
cho **tất cả** client:

```json
{ "relative": { "mode": true, "d": 0, "m": 30, "s": 0 } }
```

Web UI tự cập nhật toggle Jog/Relative khi nhận message này (không cần F5). App/plugin nên làm tương
tự: đẩy setting xuống device rồi **đọc ngược lại** từ message này (và từ block `relative` trong
snapshot lúc kết nối — mục 3) thay vì chỉ tự ghi nhớ nội bộ, nhờ vậy mọi client luôn hiển thị cùng
một giá trị và không lệch với backend.

Cùng nguyên tắc này, đổi tốc độ cũng được phát lại ngay cho mọi client:
`{ "speedLevel": 3 }` (từ WS `speedLevel` hoặc từ lệnh Serial `SLvl:3`) → Web UI đổi nút Speed đang
active mà không cần F5.

### 6.2 Admin response

```json
{ "cmd": "loginAdmin", "result": true }
{ "cmd": "changePassword", "result": false }
```

### 6.3 WiFi scan response

```json
{
  "wifi_scan": [
    { "ssid": "HomeWiFi", "rssi": -60, "auth": "SECURE" },
    { "ssid": "OpenAP", "rssi": -75, "auth": "OPEN" }
  ]
}
```

### 6.4 Calibration result

```json
{ "cmd": "calibResult", "axis": "az", "steps": 12345, "spd": 617.25, "travel": 20 }
```

```json
{
  "cmd": "calibResult",
  "axis": "all",
  "az_steps": 12000,
  "alt_steps": 18000,
  "az_spd": 600,
  "alt_spd": 600,
  "az_travel": 20,
  "alt_travel": 30
}
```

### 6.5 Tuning result

```json
{ "cmd": "tuningResult", "axis": "az", "avg_sg_results": [10, 20, 30, 40, 50] }
```

```json
{ "cmd": "tuningTcoolResult", "axis": "alt", "avg_tstep_results": [100, 200, 300, 400, 500] }
```

### 6.6 OTA progress/result

```json
{ "ota_progress": 35 }
```

```json
{
  "ota_progress": 100,
  "sys_status": "REBOOTING",
  "ota_done": true,
  "ota_type": "firmware",
  "reboot_after": true
}
```

```json
{ "ota_status": "FAILED", "alert": "OTA Firmware Failed: ..." }
```

## 7. Third-party app flow to mirror Web UI behavior

## 7.1 Startup sequence

1. Connect WebSocket `/ws`.
2. Receive init snapshot (config + state).
3. Render UI.
4. Keep listening to telemetry stream for state synchronization.

## 7.2 Jog button (press/hold/release)

1. On button down: send `move`.
2. While holding: optionally resend `move` every 150-300 ms as app-side fail-safe (not strictly required by WS, but recommended).
3. On button up: send `stop`.

## 7.3 Relative move

1. Send `moveRelative`.
2. Track `sys_status`, `isAutoMoving`, `pos_az/pos_alt` until back to `READY`.

## 7.4 Save settings

1. Send `saveConfig` with full object (limits/motor/backlash/relative/align).
2. Wait for `status=configSaved`.
3. If WiFi/AP changed, device may send `sys_status=REBOOTING`.

## 8. Error handling recommendations

- Always parse defensively: not every key exists in every frame.
- Prioritize `alert` for popup/error display.
- Handle WS auto-reconnect.
- Disable control buttons when disconnected.

## 9. Minimal pseudo-code (cross-platform)

```text
connect(ws://ip/ws)
onOpen:
  state.connected = true

onMessage(json):
  if json.alert -> showAlert(json.alert)
  if json.status -> showToast(json.status)
  if json.cmd == "loginAdmin" -> handleLogin(json.result)
  if json.cmd == "calibResult" -> handleCalib(json)
  if json.cmd == "tuningResult" or "tuningTcoolResult" -> handleTuning(json)
  mergeTelemetry(json)

send(cmd, data):
  ws.send(JSON.stringify({ cmd, data }))
```

## 10. Supported `cmd` list (current)

- `align`
- `applyConfig`
- `autoCenter`
- `calibAxis`
- `changePassword`
- `connectWifi`
- `factoryReset`
- `forceStop`
- `handshake`
- `loginAdmin`
- `move`
- `moveRelative`
- `otaUpdate`
- `reboot`
- `releaseControl`
- `resetError`
- `resetHome`
- `returnHome`
- `saveConfig`
- `scanWifi`
- `setCommWatchdog`
- `setFactoryZero`
- `setHome`
- `setSerialLog`
- `speedLevel`
- `startTuning`
- `startTuningTcool`
- `stop`
- `stopMove`

---

This document is based on the current implementation in firmware (`src/Web/WebControl.cpp`, `src/main.cpp`) and frontend (`data/script.js`). If backend keys/cmds change, update this file accordingly.
