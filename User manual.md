# MLAstroRPA — User manual (N.I.N.A. plugin)

The MLAstro RPA plugin is the hardware side of the MLAstro Robotic Polar Alignment system. It talks to
the MLAstro RPA controller over **USB serial** or **Wi-Fi**, moves the altitude and azimuth axes, shows
live position, status and alarms, and keeps the controller's own settings (motor drivers, soft limits,
backlash, Wi-Fi).

This manual explains the **polar alignment workflow with the Three Point Polar Alignment (TPPA) plugin**
first, then every tab of the plugin options page.

Related documents: [`FAQ.md`](FAQ.md) · [`Changelog.md`](Changelog.md) ·
[`MLAstroRPA-navigation/Documentation/Serial-protocol.md`](MLAstroRPA-navigation/Documentation/Serial-protocol.md) ·
TPPA external-correction contract (in the Three Point Polar Alignment repository).

---

## Polar alignment workflow (with TPPA)

The plugin does not measure anything by itself: **TPPA measures, this plugin moves the axes.** TPPA keeps
plate solving and estimating the polar error, while the plugin receives that error through the NINA
message broker, turns it into moves and honours pause, stop and cancel.

### 1. What you need

- A supported MLAstro RPA controller, connected over USB serial or Wi-Fi.
- N.I.N.A. 3.1.2 or newer.
- The **Three Point Polar Alignment** plugin installed, a camera, working plate solving, and a mount whose
  right ascension axis can be moved.
- Gear ratio set so that each axis achieves roughly **1 arcminute per step**.

### 2. Connect the controller (CONNECTION tab)

1. Open **Options → Plugins → MLAstroRPA → CONNECTION**.
2. **Connection type:** `Wireless connection` (recommended, no cable) or `Serial connection`.
   - Wireless: type the `Address` (`MLAstroRPA.local` or the device IP, see `AP:` / `STA:` in the header).
   - Serial: pick the `COM Port` (or press `Auto scan COM port`).
3. Press **Connect**. `Handshake: OK!` confirms the link; the header then shows `Firmware …`,
   `Status: …`, `AP: …` and `STA: …`.

### 3. Prepare the device (HARDWARE SETTING tab)

Check **Soft Limits**, the **Motor Driver (TMC2209)** values (especially `Steps/Degree` and
`Reverse Direction`), **Backlash** and, if you use Wi-Fi, the **Network config**. Then press
**✓ SAVE ALL & REBOOT** — see [HARDWARE SETTING tab](#hardware-setting-tab).

### 4. Enable the external correction (SOFTWARE SETTING tab)

1. Turn on **Assign to Three Point Polar Alignment plugin**. The plugin now announces its capabilities on
   the NINA message broker; while it is off, TPPA keeps its normal behaviour and no session is handed over.
2. Set the correction strategy (they apply to the **next** session):
   - **Correction axis mode** — `Both axes` sends one ALIGN command for both axes, `Larger error axis`
     corrects only the axis with the bigger remaining error.
   - **Correction factor** — share of the measured error that is actually corrected (0.75 = 75 %).
   - **Enable software overshoot** with **Overshoot when moving up / down (arcmin)** — travel past the
     target so the next measurement removes the mechanical play; only one direction can overshoot.
   - **Auto change direction** / **Reverse Azimuth-Altitude direction (software)** — fixes a wrong
     move direction; while *Auto change direction* is on, the two reverse checkboxes are locked.
   - **Detect direction clamp (arcmin)** — upper limit for a single correction step, and the distance a
     wrong-direction move may travel before the plugin notices.
   - **Automated adjustment settle time (s)** — wait after each move before the next measurement is
     requested, so the axis can settle.

### 5. Configure TPPA
- When Asigned MLAstroRPA plugin in `SOFTWARE SETTING` no need to select any Polar alignment system on TPPA.

### 6. Run the session

1. Start TPPA (Advanced Sequencer instruction or the Imaging-tab tool pane) and let it run.
2. TPPA takes the **three reference points** while the mount moves along right ascension only, then
   publishes the first measurement. Until then the plugin does not move anything.
3. From that point on the loop is: the plugin asks TPPA to hold the capture, TPPA grants a capture
   window, the plugin **moves the axes**, waits the *settle time* and asks for the next measurement.
   TPPA measures again and publishes the new error — this repeats until the tolerance is reached.
4. **Pause** in TPPA stops the loop: the plugin stops a move in progress and does not start a new one.
   **Stop / cancel** ends the session; TPPA is always the owner of the session.
5. The plugin reports back when it believes it is done; TPPA verifies with its own policy and closes the
   session with the final errors.

### 7. Watch what happens

- **SOFTWARE SETTING → Broker log** shows the traffic:
  **RPA → TPPA (blue)** = sent to TPPA · **TPPA → RPA (green)** = received from TPPA ·
  **notice (yellow)** = local status of the plugin.
- The two status lines above the *External correction* expander show the broker state and the current
  session state.
- The session also ends (with a reason written to the log) when the broker is switched off, the
  controller is disconnected, the firmware link is lost, or the hardware faults.

### 8. Rules of thumb

- Keep the controller connected in **one plugin only**: the port and the controller firmware accept a
  single session at a time.
- Use `Backlash` compensation if the axis direction reverses during a correction, otherwise each
  reversal loses the mechanical play.
- Keep the **soft limits** enabled: every correction move the plugin sends is refused outside them.

---

## The tabs at a glance

| Tab | What it is for |
|---|---|
| **CONTROL** | Jog pad, position readout, manual polar alignment (Az / Alt error + ALIGN), alarm history. |
| **HARDWARE SETTING** | Controller parameters: soft limits, motor drivers, backlash, Wi-Fi / mDNS, save actions. |
| **SOFTWARE SETTING** | External correction (TPPA) options and the broker log. |
| **CONNECTION** | Serial port or wireless link, handshake, device reset, system log and the serial terminal. |

The header above the tabs shows the plugin identity, `Firmware …`, `Status: …`, the device link lines
`AP: …` / `STA: …`, the red **FORCE STOP** and (only in an error state) **RESET ERROR**.

---

## CONTROL tab

The CONTROL tab hosts the same panel as the **MLAstroRPA dockable** in the Imaging tab. When the
controller is not connected, the panel shows the notice
`⚠️ Not connected — Open the plugin CONNECTION tab and connect to the MLAstro RPA hardware (Serial or
Wireless) to start controlling.`

### 🕹️ Manual Movement

1. Pick a **Speed Level** — `1` … `5`, higher is faster (level 5 is the `Max Motor RPM` from the
   controller settings).
2. Choose the move mode:
   - **Jog (Hold)** — press and hold an arrow to move continuously, release to stop.
   - **Relative (Step)** — set the step size in `Degrees` / `Minutes` / `Seconds` (with `-` / `+`) and
     press an arrow once to move exactly that amount.
3. Use the directional pad: **▲ Alt** / **▼ Alt** (altitude), **◄ Az** / **► Az** (azimuth),
   **⏹ STOP** to stop both axes.

In an emergency use the red **FORCE STOP** in the header — it halts both axes immediately.

### 📐 Position

`Azimuth (from home)` and `Altitude (from home)` show the current position, `🏠 Homed` the homing state.

- **↻ RETURN TO HOME** — drive both axes back to the home position.

### 🎯 Polar Alignment (manual correction, no TPPA needed)

1. Enter the measured error in **Az Error** (`°` `'` `"`) and pick the direction `Left` / `Right`.
2. Enter the **Alt Error** and pick `Up` / `Down`.
3. Press **Align Az** or **Align Alt** to correct a single axis, or **✓ ALIGN ALL** to correct both at
   once (axes sequentially or simultaneously, depending on the `ALIGN ALL Mode` setting on the device).
4. **Az Moved** / **Alt Moved** show the correction that was applied.
5. When the alignment is good, press **💾 SAVE ALIGNED POSITION** to remember the current position.
6. If the RPA is moved by accident, press **🎯 FALLBACK SAVED POSITION** to drive both axes back to the
   saved aligned position.

### ⚠️ Alarm History

Coded alarm list with `State`, `Severity`, `Description`, `Activated` and `Cleared` columns. **🗑 CLEAR**
empties the list. Every warning and error has a cause and a fix — see `Log & Error table.md` in the
firmware documentation (it also covers every log line the device can print).

---

## HARDWARE SETTING tab

Every value here is stored in the controller's non-volatile FRAM and only becomes permanent when you
press **✓ SAVE ALL & REBOOT** (see [Configuration Management](#-configuration-management)).

> The whole tab is the same feature set as the **CONFIG** tab of the controller's own Web UI, so both
> tools can be used side by side — just not at the same time on two sessions.

### Soft Limits (Degrees)

Software limits — the controller refuses to move outside the configured angle range (measured from home).

- **AZ Min / AZ Max** — azimuth range (default ±9°).
- **ALT Min / ALT Max** — altitude range (default ±14°).

> Min must be smaller than Max.

### Motor Driver (TMC2209)

Configured independently for the **AZ Motor** and **ALT Motor** (tick **Reverse Direction** if a motor
spins the wrong way).

| Setting | Meaning |
|---|---|
| **Run Current (mA)** | Current while the motor is moving. |
| **Hold Current (mA)** | Current while the motor is stationary (auto-capped at Run Current). |
| **Start-up Booster (%)** | Extra current at start-up (100–150 %) to overcome inertia, then reduced. |
| **Soft CoolStep (%)** | Minimum current scale at high speed (**60–100 %**) — current is gradually lowered as the motor reaches speed, keeping it cool and quiet. |
| **Microsteps** | 2–256 microsteps (8–16 is typical). |
| **Accel / Decel (steps/s²)** | Acceleration / deceleration rate. |
| **Steps/Degree** | Steps per degree (5-decimal precision). If unknown, enter the measured value by hand. |
| **Mode: StealthChop / SpreadCycle** | StealthChop = quiet, for light load. SpreadCycle = stronger, more precise at speed, better for stall detection. |

### Backlash

- **Enable Anti Backlash on firmware** — compensate for gear backlash in firmware.
- **AZ Backlash / ALT Backlash (steps)** — compensation steps applied on every direction change.

**How it works:** there is always a small amount of play between the motor shaft and the output (gearbox /
lead screw). When the direction reverses, the first few steps only take up that play, so without
compensation the axis lands short by exactly the play amount. The firmware tracks the last travel
direction of each axis; when a new move goes the **opposite** way it shifts the position counter by the
configured backlash steps *before* computing the distance, so the motor travels those extra steps on
purpose. To tune it, measure the play of each axis and enter it in steps — too small leaves residual
error, too large overshoots.

### Network config

**mDNS Name** — host name used for `<name>.local` (1–31 characters: letters, digits, hyphen; the
`.local` suffix is added automatically). Empty = `mlastrorpa`. Takes effect after **✓ SAVE ALL &
REBOOT**.

**Access Point (Hotspot)** — the controller always broadcasts its own network, so it can be reached
directly, even without a router.

| Field | Meaning |
|---|---|
| **AP SSID** | Network name shown in Wi-Fi lists. **Default:** `MLAstroRPA-XXXX` (`XXXX` = last 3 hex of the MAC, unique per unit). |
| **AP Password** | Password to join the AP (min 8 characters). **Default:** `password`. Shown masked — press **👁** to fetch the stored value. **Leave blank to keep the current password.** |
| **AP IP Address** | Address used to open the Web UI / connect while on the AP. **Default:** `192.168.4.1`. **Leave blank to keep the current value.** |
| **Subnet Mask** | AP network mask. **Default:** `255.255.255.0`. **Leave blank to keep the current value.** |

**Station Mode (Connect to Router)** — join your own network (the controller keeps the AP alive at the
same time).

| Field | Meaning |
|---|---|
| **WiFi SSID** | Your router's network name. |
| **WiFi Password** | Password of that router, shown masked — press **👁** to fetch the stored value. **Leave blank to keep the current password.** |
| **Current STA Mode IP** | Read-only — the IP the router assigned (e.g. `192.168.1.50`), also shown as `STA:` in the header. |

Because the STA IP is DHCP-assigned it may change on reboot; the controller also remembers the **last 5
networks** and tries them in order at boot.

### 💾 Configuration Management

| Button | What it does |
|---|---|
| **⚡ APPLY SETTINGS** | Apply to RAM only — lost on reboot. Good for quick tests. |
| **✓ SAVE ALL & REBOOT** | Persist everything to FRAM and reboot. Use this to keep changes. |
| **⏻ REBOOT** | Reboot without saving (discards unapplied changes). |

Note: *Apply sends settings to device memory without saving. Save All persists to FRAM and reboots the
device.*

---

## SOFTWARE SETTING tab

Above the expander, two status lines report the broker state and the current session state.

### External correction (three point polar alignment)

| Control | What it does |
|---|---|
| **Assign to Three Point Polar Alignment plugin** | When on, the plugin announces its capabilities on the NINA message broker, receives the error from TPPA and drives the motors. TPPA must be told to hand the session over (see the [workflow](#polar-alignment-workflow-with-tppa)). |
| **Correction axis mode** | `Both axes` = one ALIGN command moves both axes together. `Larger error axis` = only the axis with the bigger remaining error is moved, the other is measured again first. |
| **Correction factor** | Multiplied into the measured error before the move is sent (0.75 = correct 75 % of it). The altitude axis uses the full error instead when overshoot runs for that direction. |
| **Automated adjustment settle time (s)** | Seconds the plugin waits after a correction move before it asks TPPA for the next measurement, so the axis can settle (backlash, vibration). `0` = measure straight away. |
| **Enable software overshoot** | Move past the target by the overshoot amount; the next measurement corrects whatever is left. |
| **Overshoot when moving up / down (arcmin)** | The overshoot amount for that direction. Only one direction can overshoot — picking one clears the other. |
| **Auto change direction** | When a wrong direction is detected, the plugin flips it itself and stores it so the next runs keep it. While it is on, the two reverse checkboxes below are locked. |
| **Reverse Azimuth / Altitude direction (software)** | Flips the direction of the ALIGN commands this plugin sends. This is a **software** flip — it is not the firmware `Reverse Direction` of the HARDWARE SETTING tab. |
| **Detect direction clamp (arcmin)** | Upper limit for a single correction step (a bigger error is corrected in several steps), and the same value clamps the wrong-direction detection, so a mis-set direction only travels this far before the plugin notices. |

### Broker log

Live log of the plugin ↔ TPPA traffic:
**RPA → TPPA (blue)** = sent to TPPA · **TPPA → RPA (green)** = received from TPPA ·
**notice (yellow)** = local status of this plugin.

---

## CONNECTION tab

### Connection type

`Serial connection` (USB cable) or `Wireless connection` (Wi-Fi / WebSocket). Switching disconnects the
current session.

### Serial connection

| Control | What it does |
|---|---|
| **COM Port** | Port of the controller. |
| **Auto scan COM port** | Scans the available ports and picks the controller automatically. |
| **Connect / Disconnect** | Opens or releases the port. |
| **Reset ESP32** | Restarts the controller over the link. |
| **Handshake: …** | `OK!` when the controller answered the handshake, otherwise the reason (e.g. time-out). |
| **Hide polling telemetry on terminal** | Hides the periodic `<?>` polling traffic from the terminal view. |
| **Polling Period** | How often the plugin asks the controller for telemetry (`100`–`1000` ms). |

### Wireless connection

| Control | What it does |
|---|---|
| **Address** | `hostname (MLAstroRPA.local)` or the device IP (the `STA:` line in the header shows the current IP). |
| **Connect / Disconnect** | Opens the WebSocket session and performs the handshake. |
| **Reset ESP32** | Sends the reboot command to the controller. |
| **System log** | Log lines pushed by the controller, newest on top. Buttons: **⚠ RESET ERROR** (clears a driver / limit error state), **Export CSV**, **Clear**. Right-click for `Copy` / `Clear`. |

### Serial Terminal

Raw terminal for both connection types: type a command, press **Send**. With **Hex** ticked the input
accepts up to 16 hex characters and sends them as the corresponding bytes. Right-click for `Copy`,
`Clear` and `Hex display` (switches the display between text and hex).

---

## Tips

- Keep the controller connected in **one plugin only** — the port and the controller firmware accept a
  single session at a time.
- The plugin keeps its own log table; the controller's `System log` in the CONNECTION tab is what the
  device itself reports, so both together usually explain any odd behaviour.
- After a firmware update the plugin re-reads `Firmware …` from the device: if it stays empty, the
  handshake did not complete.
- Soft-limit events are warnings: the controller simply refuses that move. A red **ERROR** state blocks
  movement until you fix the cause and press **⚠ RESET ERROR**.
