# MLAstroRPA+TPPA — Bảng text giao diện trang Options

Tài liệu liệt kê toàn bộ text hiển thị trên trang **Options** của plugin (4 tab: `TPPA OPTION`,
`CONTROL`, `CONNECTION`, `CONFIGURATION`), gồm text tĩnh, text động (binding) và **toàn bộ tooltip**.

- Cập nhật lần cuối: 2026-09-14
- Nguồn: `Options.xaml`, `MLAstroRPA-navigation\Plugin\MLAstroOptions.xaml`,
  `MLAstroRPA-navigation\Dockables\PolarAlignmentDockable.xaml`,
  `MLAstroRPA-navigation\Dockables\HeaderBar.xaml`
- Cột "Điều kiện hiện": text chỉ xuất hiện khi thoả điều kiện tương ứng.

---

## 1. Thanh header (hiện trên MỌI tab của Options)

| Text hiển thị | Nguồn | Ghi chú |
|---|---|---|
| `MLAstro RPA` | tĩnh | FontSize 18, đậm |
| `Robotic Polar Alignment` | tĩnh | phụ đề |
| `Firmware {0}` | binding `FirmwareVersion` | vd `Firmware 1.2.71`. CHỈ hiện khi đã kết nối + handshake OK (`ControlsVisibility`); chưa kết nối thì ẩn |
| `Status:` | tĩnh | + giá trị động `SystemStatus` (màu theo `StatusForeground`) |
| `ApStatusText` (`AP: connected <IP>` / `AP: ready <IP>` / `AP: error`) | động | dòng AP của thiết bị (THAY nhãn `Connection:` cũ) — chữ thường, KHÔNG in đậm, cùng font/màu xám `#BDC3C7` với dòng `STA:`; ToolTip = `ConnectionStatusText` (cổng COM / kết nối wireless). **connected** = chính PC đang đi qua hotspot (firmware báo `link`=AP); **ready** = AP đã lên nhưng PC đi đường khác (STA/cáp USB); **error** = AP không lên (`ap_ready`=0). Ẩn hẳn khi chưa kết nối (`ControlsVisibility`) |
| `STA: <icon> <IP>` | động | dòng chất lượng đường STA: `📶x` = chưa vào router (`sta_qual`=0) · `📶!` = có router nhưng không internet (=1) · `📶` = có internet (=2); IP = `sta_ip`. Ẩn khi chưa kết nối |
| `FORCE`⏎`STOP` | tĩnh | nút đỏ, luôn hiện |
| `RESET`⏎`ERROR` | tĩnh | chỉ hiện khi `ResetErrorButtonVisibility` = STATUS ERROR |

---

## 2. Tab `TPPA OPTION`

| # | Nhãn hiển thị | Loại điều khiển | Điều kiện hiện |
|---|---|---|---|
| 1 | `Default Move Rate` | TextBox | luôn |
| 2 | `Default East Direction` | CheckBox | luôn |
| 3 | `Default Target Distance` (°) | UnitTextBox | luôn |
| 4 | `Default Search Radius` (°) | UnitTextBox | luôn |
| 5 | `Axis move timeout factor` (x) | UnitTextBox | luôn |
| 6 | `Default azimuth offset from pole` (°) | UnitTextBox | luôn |
| 7 | `Default altitude offset from pole` (°) | UnitTextBox | luôn |
| 8 | `Default Alignment Tolerance` (arcmin) | UnitTextBox | luôn |
| 9 | `Altitude Error Color` | ColorPicker | luôn |
| 10 | `Azimuth Error Color` | ColorPicker | luôn |
| 11 | `Total Error Color` | ColorPicker | luôn |
| 12 | `Target Circle Color` | ColorPicker | luôn |
| 13 | `Sucessful Step Color` | ColorPicker | luôn (còn typo "Sucessful") |
| 14 | `Log polar alignment error adjustments?` | CheckBox | luôn |
| 15 | `Adjust for refraction?` | CheckBox | luôn |
| 15b | `This option is currently under test. Feedback if this results in better polar alignment is appreciated!` | TextBlock | khi tick "Adjust for refraction?" |
| 16 | `Stop Tracking when done?` | CheckBox | luôn |
| 17 | `Auto pause between continuous exposures?` | CheckBox | luôn |
| 18 | `Polar Alignment System` | ComboBox: `None`, `MLAstroRPA` | luôn |
| 19 | `Correction axis mode` | ComboBox: `Auto (largest error first)`, `Both axes together (one ALIGN)` | khi chọn MLAstroRPA |
| 20 | `Automated adjustment timeout` (min) | UnitTextBox | khi có system |
| 21 | `Reverse Azimuth Axis?` | CheckBox | khi có system |
| 22 | `Reverse Altitude Axis?` | CheckBox | khi có system |
| 23 | `Azimuth backlash compensation` (steps) | UnitTextBox | khi có system |
| 24 | `Correction Safety Factor` (%) | UnitTextBox | khi chọn MLAstroRPA |
| 25 | `Do automated adjustments?` | CheckBox | khi có system |
| 26 | `Automated adjustment settle time` (s) | UnitTextBox | khi bật automated + có system |
| 27 | `Make sure to set your gear ratio to achieve 1 arcminute per step for each axis!` | TextBlock italic | khi bật automated |
| 28 | `Reset All Settings` | Button | luôn |
| 29 | `Avalon Polar Alignment System`, `Test Connect` / `Disconnect`, `Azimuth`, `Altitude`, `Speed`, `GearRatio`, `-0.1` `+0.1` `-1` `+1` `-10` `+10`, `Move` | GroupBox + nút | **ẩn** (không có trong ComboBox hiện tại) |
| 30 | `OAPA Polar Alignment System`, `Test Connect` / `Disconnect` | GroupBox | **ẩn** |
| 31 | `MLAstro Robotic Polar Alignment System` | GroupBox | khi chọn MLAstroRPA |
| 32 | `Enable overshoot` | CheckBox | trong group MLAstro |
| 33 | `Run overshoot for moving Up` (arcmin) | CheckBox + UnitTextBox | trong group MLAstro |
| 34 | `Run overshoot for moving Down` (arcmin) | CheckBox + UnitTextBox | trong group MLAstro |
| 35 | `TestConnectStatus` (xanh lá, wrap) | binding động | trong group MLAstro |

---

## 3. Tab `CONTROL` (`PolarAlignmentDockable`)

| Khu vực | Text hiển thị |
|---|---|
| Notice mất kết nối | `⚠️ Not connected` + `Open the plugin CONNECTION tab and connect to the MLAstro RPA hardware (Serial or Wireless) to start controlling.` (chỉ khi `ControlsVisibility=Collapsed`) |
| 🕹️ Manual Movement | `Speed Level:` · nút `1` `2` `3` `4` `5` · `Jog (Hold)` · `Relative (Step)` |
| Relative steps | `Degrees` / `Minutes` / `Seconds` + nút `-` `+` (đơn vị `°`, `'`, `"`) |
| D-pad | `▲ Alt` · `◄ Az` · `⏹ STOP` · `► Az` · `▼ Alt` |
| 📐 Position | `Azimuth (from home):` · `Altitude (from home):` · `🏠 Homed:` · `↻ RETURN TO HOME` |
| 🎯 Polar Alignment | `Az Moved` · `Alt Moved` · `Az Error:` (`°` `'` `"`) · `Left` / `Right` · `Align Az` · `Alt Error:` · `Up` / `Down` · `Align Alt` · `✓ ALIGN ALL` |
| ⚠️ Alarm History | `🗑 CLEAR` · cột DataGrid: `State` `Severity` `Description` `Activated` `Cleared` |

---

## 4. Tab `CONNECTION`

| Khu vực | Text hiển thị |
|---|---|
| Kiểu kết nối | `Connection type` → `Serial connection` / `Wireless connection` |
| Serial | `COM Port` · nút `Connect`/`Disconnect` (động `SerialConnectButtonText`) · `Reset ESP32` · `AutoReconnectStatus` (động) · `SerialConnectionStatus` (động) · `Handshake: ` + `SerialHandshakeStatus` (động) · `Pause polling '?'` |
| Serial – timing | `Handshake Timeout:` + `ms (300 - 5000)` · `Polling Period:` + `ms (100 - 1000)` |
| Wireless | `Address:` + `hostname (MLAstroRPA.local) or device IP` · `Reset ESP32` · `System log:` · nút `⚠ RESET ERROR` / `Export CSV` / `Clear` · context menu: `Copy` / `Clear` |
| Terminal (cả 2 chế độ) | `Serial Terminal` · nút `Send` · checkbox `Hex` · context menu: `Copy`, `Clear`, `Hex display` |

---

## 5. Tab `CONFIGURATION`

| Khu vực | Text hiển thị |
|---|---|
| Notice | `⚠️ Not connected` + `Open the CONNECTION tab and connect to the MLAstro RPA hardware (Serial or Wireless) to configure device settings.` |
| Soft Limits (Degrees) | `AZ Limits`: `AZ Min` `AZ Max` · `ALT Limits`: `ALT Min` `ALT Max` |
| Motor Driver (TMC2209) | `AZ Motor` / `ALT Motor`; mỗi motor: `Reverse Direction`, `Run Current (mA)`, `Hold Current (mA)`, `Start-up Booster (%)`, `Soft CoolStep (%)`, `Microsteps` (1, 2, 4, 8, 16, 32, 64, 128, 256), `Accel (steps/s²)`, `Decel (steps/s²)`, `Steps/Degree`, `Mode` (`StealthChop` / `SpreadCycle`) |
| Backlash & P.A Overshoot | `Enable Anti Backlash on firmware`, `AZ Backlash (steps)`, `ALT Backlash (steps)`, `Enable Alt P.A Overshoot on firmware`, `Move up overshoot`, `Move down overshoot`, `Overshoot Amount:` (`°` `'` `"`) |
| WiFi Configuration | `Access Point (Hotspot)`: `AP SSID`, `AP Password` (nút 👁), `AP IP Address`, `Subnet Mask` · `Station Mode (Connect to Router)`: `WiFi SSID`, `WiFi Password` (nút 👁), `Current STA Mode IP` |
| Configuration Management | `💾 Configuration Management` · `⚡ APPLY SETTINGS` · `✓ SAVE ALL & REBOOT` · `⏻ REBOOT` · note `Apply sends settings to device memory without saving. Save All persists to FRAM and reboots the device.` |

---

## 6. Bảng TOOLTIP — hover vào đâu thì hiện gì

| # | Hover vào (element) | Tab / Khu vực | Nội dung tooltip | Key resource |
|---|---|---|---|---|
| T1 | Nhãn `Default Alignment Tolerance` | TPPA OPTION | Setting the Polar Alignment Tolerance to non-zero will specify a tolerance in arcminutes where the polar alignment routine automatically completes when below the threshold | inline `TextBlock.ToolTip` |
| T2 | Nhãn `Correction axis mode` **và** ComboBox của nó | TPPA OPTION | How the automated loop corrects polar alignment. Auto = only the axis with the larger error is nudged this pass (the other axis is measured again first). Both axes together = Azimuth + Altitude are corrected at the same time in a single ALIGN command (faster). | `MLAstroRPACorrectionModeToolTip` |
| T3 | Nhãn `Automated adjustment timeout` **và** UnitTextBox | TPPA OPTION | Maximum minutes the automated correction phase may run before the polar alignment stops itself automatically (safety limit). 0 disables the timeout. Time spent waiting (Start / auto-pause) is not counted. | `AutomatedAdjustmentTimeoutToolTip` |
| T4 | Nhãn `Correction Safety Factor` **và** UnitTextBox | TPPA OPTION | Safety factor applied to the automated correction moves (Azimuth axis always, and Altitude axis when overshoot is not used for that direction). 100 % corrects the full measured error; lower values correct only a percentage of it. Range: 1–100 % (default 75). | `MLAstroRPACorrectionFactorToolTip` |
| T5 | Nút `Test Connect` | TPPA OPTION → group MLAstro | Creates the selected hardware system, scans available serial ports for a matching device, and starts status polling. | `AlignmentSystemConnectToolTip` |
| T6 | Nút `Disconnect` | TPPA OPTION → group MLAstro | Disposes the current hardware connection and stops status polling. | `AlignmentSystemDisconnectToolTip` |
| T7 | Nhãn `Enable overshoot` **và** CheckBox | TPPA OPTION → group MLAstro | When enabled, the Alt axis corrects the full 100% of the error and then moves a fixed overshoot amount past the target for the selected direction(s). | `MLAstroRPAOvershootEnabledToolTip` |
| T8 | Nhãn `Run overshoot for moving Up` **và** CheckBox | TPPA OPTION → group MLAstro | When the Alt axis must correct upwards (as shown on screen), move the full 100% of the error plus the configured overshoot amount past the target. Range: 0–240 arcminutes (0 = correct the full error with no overshoot). | `MLAstroRPAOvershootUpToolTip` |
| T9 | Nhãn `Run overshoot for moving Down` **và** CheckBox | TPPA OPTION → group MLAstro | When the Alt axis must correct downwards (as shown on screen), move the full 100% of the error plus the configured overshoot amount past the target. Range: 0–240 arcminutes (0 = correct the full error with no overshoot). | `MLAstroRPAOvershootDownToolTip` |
| T10 | ToolTip trên dòng `AP: ...` | Header (mọi tab) | `ConnectionStatusText` (động — theo trạng thái kết nối) | binding |
| T11 | Nút `FORCE STOP` | Header | FORCE STOP (Emergency) | inline |
| T12 | Nút `RESET ERROR` | Header (khi ERROR) | RESET ERROR - clears driver error (sends ReEr:1) | inline |
| T13 | Nút `🗑 CLEAR` | CONTROL → Alarm History | Xoá toàn bộ lịch sử Alarm | inline |
| T14 | Nút `👁` cạnh AP Password | CONFIGURATION → WiFi | Show/Hide AP password | inline |
| T15 | Nút `👁` cạnh WiFi Password | CONFIGURATION → WiFi | Show/Hide WiFi password | inline |
| T16 | Ô `Current STA Mode IP` | CONFIGURATION → WiFi | Lấy từ telemetry (STAi). Trống = chưa có IP từ router. | inline |

---

## 7. Ghi chú / việc cần dọn

- **Tooltip chết:** 2 resource `MLAstroRPAEnableAutoReverseToolTip` và
  `MLAstroRPADetectDirectionToolTip` vẫn còn trong `Options.xaml` nhưng **không gắn vào UI nào**
  (mục "Enable auto-reverse" / "Detecting direction offset" đã bị ẩn từ 2026-09-07).
  Nếu không có kế hoạch bật lại thì nên xoá khi dọn code.
- **Typo UI:** `Sucessful Step Color` → đúng chính tả là `Successful Step Color`.
- **Mojibake:** `Accel (steps/sÂ²)` / `Decel (steps/sÂ²)` trong XAML hiển thị đúng là `steps/s²`
  nhưng chuỗi bị mã hoá sai ký tự `²` — nên thay bằng `²` hoặc viết `steps/s2`.
- Nhóm `Avalon` và `OAPA` (mục 29, 30) hiện **không thể chọn** từ ComboBox `Polar Alignment System`
  (chỉ có `None` / `MLAstroRPA`) nên thực tế không bao giờ hiện trên UI.
