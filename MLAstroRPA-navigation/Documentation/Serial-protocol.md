# MLAstro RPA - PC Serial Control Protocol Guide

This document describes the serial communication protocol used to control the MLAstro RPA (Robotic Pointing Assembly) via a PC or 3rd-party software.

## 1. Connection Settings
To communicate with the ESP32 via USB Serial, use the following port settings:
*   **Baud Rate:** `115200`
*   **Data Bits:** `8`
*   **Parity:** `None`
*   **Stop Bits:** `1`
*   **Line Ending:** Every command **MUST** be terminated with a newline character (`\n` / `LF` / Hex: `$0A`) or carriage return (`\r` / `CR` / Hex: `$0D`).

---

## 2. Handshake (Taking Control)
By default, the Web UI has full control of the mount. Before sending any movement or configuration commands via Serial, the PC software must initiate a handshake.

*   **Send:** `[MLAstroRPA-TC]\n`
*   **System Reply:** `ok,<firmware_version>,SN:<base_mac>\n`
    *   Example: `ok,firmware 1.2.43,SN:AA:BB:CC:DD:EE:F0\n`

**Behavior:** 
Once the handshake is accepted, the ESP32 will lock out all Web UI control to prevent conflicts. If a user manually reloads the web page, the ESP32 will drop the PC control, print `DISCONNECTED\n` to the Serial port, and wait for a new handshake.

---

## 3. Command Syntax & Logic
Commands generally follow the format `CommandPrefix:Value\n`.

### Button Press & Release Logic (UI Mapping)
To perfectly emulate GUI buttons (Mouse Down / Mouse Up), action commands support state flags:
*   `:1` represents **Button Pressed** (Start action).
*   `:0` represents **Button Released** (Stop action).

### Jog Mode Safety Watchdog (Important!)
To prevent equipment damage in case of software crashes or disconnected cables during continuous movement (Jog Mode), a **500ms Watchdog** is implemented.
*   When moving in Jog Mode, the PC software **must continually send the move command** (e.g., `MAzL:1\n`) every `200ms` to `300ms` while the button is held down.
*   If the ESP32 does not receive a repeated command within 500ms, it will automatically hit the brakes and stop the motors.
*   When the user releases the button, explicitly send the stop command (`MAzL:0\n`) to stop immediately without waiting for the timeout.
*   *(Note: This watchdog is ignored when the system is in Relative/Angle movement mode).*

---

## 4. Command Reference Table

### System & Stop Commands
| Command | Action / Description |
| :--- | :--- |
| `ESTOP:1\n` | **Emergency Stop**: Hard stop immediately (No deceleration). |
| `STOP:1\n` | **Soft Stop**: Smoothly decelerate all motors to a halt. |
| `ReER:1\n` | **Reset Error**: Clears the driver error state (`hasDriverError`) and returns the system to `READY`. Stops both motors. Works like the "RESET ERROR" button in the Web UI System Log. |
| `Disconnect\n` | **Graceful release**: stops motors smoothly and releases the Serial control handshake. The host sends this right before closing the port — required when the **Communication Watchdog is disabled**, because the firmware then never auto-releases the handshake (otherwise the mount stays locked to the dead Serial session until reboot). |
| `ESTOP:0\n` / `STOP:0\n` | Ignored (Button release event). |

### Movement Commands
*Use `:1` to start moving (the PC app should resend this every 300ms to indicate the button is still held) and `:0` to stop. Applies to both Jog and Relative modes.*
| Command | Action / Description |
| :--- | :--- |
| `MAzL:1\n` | Move Azimuth Left (Negative direction). |
| `MAzR:1\n` | Move Azimuth Right (Positive direction). |
| `MAlU:1\n` | Move Altitude Up (Positive direction). |
| `MAlD:1\n` | Move Altitude Down (Negative direction). |
| `MAzL:0\n` | Stop Azimuth axis immediately (Decelerates smoothly). |

### Configuration Commands
| Command | Action / Description |
| :--- | :--- |
| `SLvl:X\n` | Set Speed Level, where `X` is `1` to `5`. (e.g., `SLvl:3\n`) |
| `JoRe:X\n` | Switch Movement Mode. `0` = Jog (Continuous), `1` = Relative (Angle). |

### Home Management
| Command | Action / Description |
| :--- | :--- |
| `SetH:1\n` | **Set Home Here**: Marks the current position as the `(0, 0)` reference. |
| `RetH:1\n` | **Return to Home**: Slew both axes back to the `(0, 0)` coordinate. |
| `RstH:1\n` | **Reset Home**: Clears the home status and coordinates. |
| `Home:X\n` | *(Read-Only)* Returns current home status (`1` = Homed, `0` = Not homed). |


### Relative Setup Commands (Angle Input)
*Pre-fill these values before executing a Move command in Relative mode (`JoRe:1`).*
| Command | Action / Description |
| :--- | :--- |
| `ReDe:X\n` | Set Degrees (e.g., `ReDe:45\n`). |
| `ReAM:X\n` | Set Arc Minutes (e.g., `ReAM:30\n`). |
| `ReAS:X\n` | Set Arc Seconds. |

### Alignment Commands
*Pre-fill error offsets before calling the Execute Alignment commands, or chain them together in a single string.*

> **Two-step semantics — setters vs triggers.** `AzED/AzEM/AzES/AzDi` (+ `Al...`) are **setters**: they
> only store the offset in FRAM, they never move the motors. Motion happens on `AzAN:1` / `AlAN:1` /
> `AAll:1`, which use the **stored** values and direction (`AlED/AzDi` …). Each setter writes its field
> immediately, so a partial line (e.g. only `AzDi:1`, the direction toggle) keeps the other fields
> untouched — never resend fields as `0` unless you really want to clear them. A negative degree/minute/
> second value also implies direction `0` (Left/Down). Over WebSocket the same two steps map to
> `saveConfig {align}` (setters) and `align {ra_error,dec_error}` (trigger).

| Command | Action / Description |
| :--- | :--- |
| `AzED:X\n` | Set Azimuth Error Degrees. |
| `AzEM:X\n` | Set Azimuth Error Arc Minutes. |
| `AzES:X\n` | Set Azimuth Error Arc Seconds. |
| `AzDi:X\n` | Set Azimuth Error Direction (`1` = Right/Positive, `0` = Left/Negative). |
| `AlED:X\n` | Set Altitude Error Degrees. |
| `AlEM:X\n` | Set Altitude Error Arc Minutes. |
| `AlES:X\n` | Set Altitude Error Arc Seconds. |
| `AlDi:X\n` | Set Altitude Error Direction (`1` = Up/Positive, `0` = Down/Negative). |
| `AzAN:1\n` | **Execute**: Align Azimuth axis only. |
| `AlAN:1\n` | **Execute**: Align Altitude axis only. |
| `AAll:1\n` | **Execute**: Align Both axes sequentially (Azimuth first, then Altitude). |

> **💡 Pro Tip: Alignment Command Chaining**
> You can pack the offset parameters and the execute command into a single line separated by commas. The ESP32 will automatically parse, save them to memory, and trigger the alignment.
> *   **Align Azimuth only:** `AzED:1,AzEM:30,AzES:0,AzDi:1,AzAN:1\n`
> *   **Align Altitude only:** `AlED:2,AlEM:15,AlES:0,AlDi:1,AlAN:1\n`
> *   **Align Both axes:** `AzED:1,AzEM:30,AzES:0,AzDi:1,AlED:2,AlEM:15,AlES:0,AlDi:0,AAll:1\n`
>
> *(Note: The ESP32 will reply with a single `ok\n` string for the entire chained command. The PC software should then wait for the `AzAN:COMPLETED\n`, `AlAN:COMPLETED\n`, or `AAll:COMPLETED\n` push trigger to confirm the alignment is done).*

### Advanced Settings & Configuration
*These commands update settings in memory. They will not persist after a restart unless followed by the `Save&Reboot:1\n` command. You can chain multiple commands separated by commas.*

**Motor & Soft Limits**
*   `AzL1:X\n` / `AlL1:X\n` : Set Soft Limit Min (Degrees) for Azimuth/Altitude.
*   `AzL2:X\n` / `AlL2:X\n` : Set Soft Limit Max (Degrees).
*   `AzRD:X\n` / `AlRD:X\n` : Reverse Direction (`0` = Normal, `1` = Reversed).
*   `AzIR:X\n` / `AlIR:X\n` : Set Run Current (mA).
*   `AzIH:X\n` / `AlIH:X\n` : Set Hold Current (mA).
*   `AzSB:X\n` / `AlSB:X\n` : Set Startup Booster (%).
*   `AzSC:X\n` / `AlSC:X\n` : Set Soft CoolStep (%).
*   `AzMS:X\n` / `AlMS:X\n` : Set Microsteps (e.g., `8`, `16`, `32`, `64`).
*   `AzAc:X\n` / `AlAc:X\n` : Set Acceleration.
*   `AzDec:X\n` / `AlDe:X\n` : Set Deceleration.
*   `AzSD:X\n` / `AlSD:X\n` : Set Steps per Degree.
*   `AzRM:X\n` / `AlRM:X\n` : Set Run Mode (`0` = StealthChop, `1` = SpreadCycle).

**Backlash Settings**
*   `Back:X\n` : Enable/Disable Backlash Compensation (`0` = Disable, `1` = Enable).
*   `Over:X\n` : Enable/Disable the Alt-axis 2-leg P.A Overshoot Routine (`0` = Disable, `1` = Enable). Master switch cho cả 2 chiều.
*   `OvUp:X\n` : Enable/Disable Move up overshoot (`0` = Disable, `1` = Enable). Áp dụng khi trục Alt chạy LÊN.
*   `OvDn:X\n` : Enable/Disable Move down overshoot (`0` = Disable, `1` = Enable). Áp dụng khi trục Alt chạy XUỐNG.
*   `OvD:X\n` : Set Overshoot Amount Degrees (`0` to `10`). Độ vượt đích của trục Alt.
*   `OvM:X\n` : Set Overshoot Amount Arc Minutes (`0` to `59`).
*   `OvS:X\n` : Set Overshoot Amount Arc Seconds (`0` to `59`).
*   `AzBl:X\n` / `AlBl:X\n` : Set Azimuth/Altitude Backlash Steps.

**Network Settings (WiFi & Access Point)**
*   `STAs:X\n` : Set Station (WiFi) SSID (local WiFi network that ESP32 connects to).
*   `STAp:X\n` : Set Station (WiFi) Password.
*   `STAi:X\n` : *(Read-Only)* Returns `ok` but ignores set requests (Station IP is assigned by router DHCP).
*   `STAp:?\n` : **Query WiFi Password Only**. Returns `STAp:X\n` (Station WiFi password).
*   `STAm` : *(Read-Only)* Station MAC address (available in Telemetry).
*   `APss:X\n` : Set Access Point (Hotspot) SSID (WiFi name broadcast by ESP32).
*   `APpa:X\n` : Set Access Point Password.
*   `APpa:?\n` : **Query AP Password Only**. Returns `APpa:X\n` (Access Point password).
*   `APip:X\n` : Set Access Point IP Address (e.g., `192.168.4.1`).
*   `APma` : *(Read-Only)* Access Point MAC address (available in Telemetry).
*   *(Note: Access Point subnet `APsu` is always `255.255.255.0` by default, and Station IP `STAi` can be read via Telemetry).*

> ⚠️ **Quy ước cho các lệnh ghi mạng `STAs` / `STAp` / `APss` / `APpa` / `APip`: tham số RỖNG =**
> **GIỮ NGUYÊN giá trị đang lưu** (không ghi rỗng). Lý do: client (WPF binding / web `collectConfig`)
> có thể gửi giá trị trung gian `""` trong lúc người dùng đang gõ lại mật khẩu; ghi thẳng giá trị rỗng
> sẽ xoá mật khẩu trong FRAM và sau đó STA **không bao giờ** qua được 4-way handshake
> (`reason 15`). Muốn xoá hẳn thì đặt một giá trị mới hợp lệ rồi `Save&Reboot`.
> Xem thêm cảnh báo lúc boot trong `Documentation/Log & Error table.md` §1.1.

**Save & Reboot**
*   `Save&Reboot:1\n` : Saves all current memory parameters to FRAM **without the firmware rebooting itself**. After the FRAM write completes, the ESP sends an **independent** notification line `All Setting Saved\n`, then replies `ok\n`.
    *   The PC/plugin is responsible for the reboot: wait for the `All Setting Saved` marker (save confirmed), then reset the ESP32 via the serial EN pin (DTR/RTS). No `REBOOTING` line is printed and no automatic reboot happens.

> **💡 Command Chaining Example:**
> Instead of sending settings one by one, you can combine them into a single string separated by commas. The system will parse them sequentially.
> **Send:** `AzL1:-9.0,AzL2:9.0,AzRD:0,AzMS:64,APss:MLAstro,Save&Reboot:1\n`

### Telemetry & Monitoring
To continuously monitor the system, the PC software should periodically send the following query command (e.g., every 300ms).

| Command | Action / Description |
| :--- | :--- |
| `?\n` | Request current system status, coordinates, and all active configuration parameters. |

**Telemetry Response Format:**
The ESP32 will reply immediately with a data string formatted as follows:
`<STATUS|Mpos:X.XXXXX,Y.YYYYY|>DATA_SETTING`

*   `STATUS`: Represents the current machine state (`READY`, `MOVING`, `HOMING`, `ALIGNING`, `CALIBRATING`, `ERROR`, `ALIGN_COMPLETED`, `HOME_COMPLETED`, `CALIB_COMPLETED`, `CENTER_COMPLETED`, `TUNING_COMPLETED`).
*   `Mpos`: The angle moved relative to the last alignment start position (Azimuth, Altitude in Decimal Degrees).
*   `DATA_SETTING`: A comma-separated list of all current system configuration variables.

**Full List of Telemetry Data Keys (DATA_SETTING):**
*   **System:** `Scal` (Fixed scale = 1), `WSta` (WiFi Status: 1=Connected, 0=Disconnected), `WQu` (STA Quality: 0=not joined the router, 1=joined but no internet, 2=internet OK), `APrd` (AP Ready: 1=hotspot up with an IP, 0=AP failed), `SLvl` (Current Speed Level 1-5), `Home` (Homed status: 1/0).
*   **Relative Move:** `JoRe` (Mode: 0=Jog, 1=Relative), `ReDe` (Deg), `ReAM` (Min), `ReAS` (Sec).
*   **Alignment Settings:** `AzED`, `AzEM`, `AzES`, `AzDi` (Direction: 1/0), `AlED`, `AlEM`, `AlES`, `AlDi` (Direction: 1/0).
*   **Azimuth Settings:** `AzPH` (Current Position in Deg), `AzL1`/`AzL2` (Soft Limits Min/Max), `AzRD` (Reverse Dir: 1/0), `AzIR`/`AzIH` (Run/Hold Current mA), `AzSB`/`AzSC` (Startup Boost/Soft CoolStep %), `AzMS` (Microsteps), `AzAc`/`AzDec` (Accel/Decel), `AzSD` (Steps/Degree), `AzRM` (Run Mode: 1=SpreadCycle, 0=StealthChop).
*   **Altitude Settings:** `AlPH` (Current Position in Deg), `AlL1`/`AlL2` (Soft Limits Min/Max), `AlRD` (Reverse Dir: 1/0), `AlIR`/`AlIH` (Run/Hold Current mA), `AlSB`/`AlSC` (Startup Boost/Soft CoolStep %), `AlMS` (Microsteps), `AlAc`/`AlDe` (Accel/Decel), `AlSD` (Steps/Degree), `AlRM` (Run Mode: 1=SpreadCycle, 0=StealthChop).
*   **Backlash:** `Back` (Enabled: 1/0), `Over` (Overshoot Routine Enabled: 1/0), `OvD`/`OvM`/`OvS` (Overshoot Amount: Degrees/Minutes/Seconds), `OvUp`/`OvDn` (Move Up/Down Overshoot Enabled: 1/0), `AzBl`/`AlBl` (Backlash Steps).
*   **Access Point:** `APss` (SSID), `APma` (MAC), `APip` (IP), `APsu` (Subnet).
*   **Station (WiFi):** `STAs` (SSID), `STAm` (MAC), `STAi` (Current IP assigned by router).

---

## 6. System Responses
Upon receiving a command, the ESP32 will immediately process it and return a string response.

*   `ok\n` : Command was successfully received and executed.
*   `error: System Locked\n` : Action rejected because the system is in an Error/Hardlimit state (Needs reset).
*   `error: Hard Limit\n` : Action rejected because the requested direction is blocked by a physical StallGuard trigger.
*   `error: Soft Limit\n` : Action rejected because the target coordinates are outside the configured minimum/maximum safety angles.
*   `error: Not homed\n` : Action rejected (e.g., trying to Return Home without setting one first).
*   `error: Unknown command\n` : The command prefix is invalid.

---

## 7. Dedicated error telemetry line (`ERROR:...`)

Independent of command replies, the firmware pushes an **edge-triggered** error line (sent only when
the state changes) — identical on Serial and over WebSocket (`{"error":"ERROR:..."}`):

```
ERROR:Sys:0,AzNC:0,AlNC:0,AzOT:0,AlOT:0,AzPW:0,AlPW:0,AzSA:0,AzSB:0,AlSA:0,AlSB:0,AzOL:0,AlOL:0,AzHL:0,AlHL:0,AzSL:0,AlSL:0,Esc:0,CmdRf:0
```

Each entry is `Code:value` with `0 = no issue`, `1 = WARNING`, `2 = ERROR`.
Codes: driver status (`AzNC/AlNC`, `AzOT/AlOT`, `AzPW/AlPW`, `AzSA/AzSB/AlSA/AlSB`, `AzOL/AlOL`),
`AzHL/AlHL` (hard limit blocking), `AzSL/AlSL` (soft-limit stop), `Esc` (hard-limit escape) and
`CmdRf` (refused commands — see below).

### `CmdRf` — bitfield of commands REFUSED by the soft limit

`CmdRf` carries **one bit per command type refused** because of the soft limit. A bit is set the moment
a command is refused and is cleared automatically after ~1.5 s without a new refusal of that type
(i.e. "the command is no longer being issued"):

| Bit | Value | Meaning |
| :--- | :--- | :--- |
| 0 | `0x01` | relative move AZ out of range |
| 1 | `0x02` | relative move ALT out of range |
| 2 | `0x04` | align: AZ target out of range |
| 3 | `0x08` | align: ALT target out of range |
| 4 | `0x10` | jog AZ refused while the axis is **parked** at the soft limit |
| 5 | `0x20` | jog ALT refused while the axis is **parked** at the soft limit |
| 6 | `0x40` | align: ALT overshoot leg out of range |

Example: `CmdRf:17` (= `0x11`) → relative move AZ **and** jog AZ are currently being refused.
All of these are **WARNING** level: they never set the system error state and never lock the axes.

> **Never duplicates `AzSL`/`AlSL`:** the jog bits (4/5) are raised **only** while `AzSL`/`AlSL` are off,
> i.e. when the axis is already parked at the edge and the user keeps pressing jog. When the axis was
> just decelerated to a stop at the edge, `AzSL`/`AlSL` already report it, so the jog bit stays off —
> every event produces exactly **one** alarm row / one notification.

---
*End of Protocol Guide*