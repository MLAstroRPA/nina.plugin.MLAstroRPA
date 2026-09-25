# Changelog

> **License & origin.** This repository is distributed under the **Mozilla Public License 2.0
> (MPL-2.0)** — see `LICENSE`. Part of the code history comes from the original open-source
> **Three Point Polar Alignment (TPPA)** plugin for NINA by
> [Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment) (MPL-2.0). The TPPA wizard has
> since been moved out into a separate plugin/repository (it talks to this one through the NINA
> message broker), so this repository contains only the MLAstro Robotic Polar Alignment
> hardware-control plugin. When redistributing, comply with MPL-2.0: retain the license and
> notices and credit the original author.

## 2.3.0.0 — 2026-09-26

### Added — TPPA bridge: MLAstroRPA runs the polar alignment loop of the Three Point Polar Alignment plugin, taking the measurements, moving the axes, holding the capture windows and honouring pause, stop and cancel
### Changed — bridge code renamed from TppaBroker/ExternalCorrection to the Bridge vocabulary (BridgeClient, BridgeContract, BridgePayloads, BridgeRunner, BridgeEngine); every wire string is unchanged
### Removed — the confirmation count and the session time limit settings: TPPA owns the finish policy and its own session time limit
### Removed — the Stop external session button on the options page (STOP / FORCE STOP on the dock and the broker switch already cancel a running session)
### Fixed — a stop now stops the axes before the session is cancelled and only once, and the aborted move is no longer reported as a hardware fault
### Fixed — a pause no longer lets a planned move start
### Fixed — losing the firmware link (serial or wireless) cancels the TPPA session instead of leaving it waiting

## 2.2.2.0 — 2026-09-25

### Changed — options tabs renamed and reordered: CONTROL, HARDWARE SETTING, SOFTWARE SETTING, CONNECTION
### Changed — SOFTWARE SETTING tab: TPPA/broker status moved above the expander, overshoot and reverse-direction toggles moved up next to the assign toggle
### Changed — plugin identity fully separated from MLAstroRPA+TPPA (own PluginId, MSI UpgradeCode, namespaces and resource keys) so both plugins can stay installed side by side
### Changed — broker log shows direction (RPA → TPPA / TPPA → RPA) with a colour legend
### Changed — English tooltips on the options page; overshoot values sit under their own direction and follow the master toggle
### Fixed — polled telemetry no longer overwrites the azimuth/altitude inputs while editing
### Fixed — commands dropped because the link is not connected are now logged

## 2.2.1.0 — 2026-09-20

### Fixed — AP/STA connection status indicators
### Fixed — WebSocket link watchdog disconnect handling

## 2.2.0.0

### MLAstroRPA — HeaderBar: AP/STA link status
- Dock header shows a `AP: connected <IP>` / `AP: ready <IP>` / `AP: error` row (replacing the old `Connection:` label, plain lowercase text like the `STA:` row) and a `STA: 📶/📶!/📶x <IP>` row; both rows and the firmware version are hidden while the device is not connected, and the data comes from firmware ≥ 1.7.0 (`link`, `ap_ready`, `ap_ip`, `sta_qual`, `sta_ip` / tokens `APrd`, `WQu`)

### MLAstroRPA — Save All & Reboot (Serial): wait for the device's `All Setting Saved` marker before resetting the ESP32

## 2.1.1.0

### MLAstroRPA — Wireless connect
- Report a failed wireless connect (mDNS/DNS, wrong address, refused handshake) in the toast/status + log, then scan the serial (COM) connection
- On a successful Serial fallback, switch the Connection type back to **Serial** automatically and keep MLAstro as the COM owner
- Bound the wireless retry: a few attempts inside a 5 s window (per-attempt resolve/connect timeout), then fail with the reason
- Cap Wireless auto-reconnect (after Reset ESP32) at 30 s and include the reason in the connection error dialog
- Warning toast when the CONNECTION tab *Connect* fails over Wireless

### MLAstroRPA — Connection UI
- Name the transport in the connect toast/status: *Successfully connected to MLAstroRPA over Serial (COM5)* / *… over Wireless (WebSocket @ MLastroRPA.local)*
- Orange ⚠️ warning next to the *Connection type* selector: switching Serial ↔ Wireless disconnects the current session
- TPPA **Test Connect**: test the wireless transport first when Wireless is selected

### MLAstroRPA — Wireless parity with Serial (TPPA session)
- TPPA connecting over Wireless now locks MLAstro CONTROL / CONFIGURATION (external-control)
- STOP / FORCE STOP pressed on MLAstro during a wireless TPPA session now reaches TPPA (toast + the whole PA routine stops)
- Notify *"Disconnected by MLAstro plugin - TPPA session closed."* when MLAstro closes the wireless session
- Toast wording uses `FORCE-STOP` instead of `E-STOP` (protocol `ESTOP:1` / `forceStop` unchanged)

## 2.1.0.0

### MLAstroRPA — Wireless connection (WebSocket over mDNS)
- New connection type selector in the CONNECTION tab: Serial or **Wireless** (address `MLAstroRPA.local`, or the device IP as fallback)
- The WebSocket transport performs the PC handshake and then behaves like the COM port (same UI, dock and telemetry pipeline)
- TPPA automated adjustment works over Wireless; switching transport disconnects the other one first

### MLAstroRPA — Complete text <-> JSON translator
- Commands: Serial text -> WebSocket JSON (jog/stopMove, relative, align, home, speed, settings, reboot, `saveConfig`/`applyConfig`)
- Telemetry: JSON -> the firmware's serial telemetry text, with every token (limits, motor, backlash, WiFi, align errors, `Mpos` header)
- Two-way setting sync via `saveConfig` + `config_pushed`, so web UI, dock and device never disagree
- WiFi/AP settings and passwords can be saved over Wireless

### MLAstroRPA — System log & dock parity with the Web UI
- Web-style log table (keyword colours, newest on top, 50 lines) instead of the raw serial terminal
- Same buttons as the web: RESET ERROR, Export CSV, Clear; `alert` messages shown as NINA toasts
- Telemetry keys are no longer parsed as alarms (only `ERROR:` lines are)

### MLAstroRPA — Jog, Relative and soft-limit safety
- Jog release decelerates via `stopMove`; STOP / E-STOP stay hard stops
- Jog buttons lock when the soft limit refuses, and re-enable after 2 s or when the axis moves away
- Relative mode stays on (synthesized telemetry carries `JoRe/ReDe/ReAM/ReAS`)

### MLAstroRPA — Alarm panel
- `CmdRf` decoded into readable warnings (`RfRelAz/Al`, `RfAlnAz/Al/Ov`, `RfJogAz/Al`); newest alarm on top
- CLEAR only clears the displayed history; one notification per event

### MLAstroRPA — CONNECTION tab cleanup
- Serial panel: removed Data Bits / Parity / Stop Bits and Refresh Ports (always 115200 8N1)
- **Reset ESP32** (renamed from *Reboot device*) moved next to **Connect**

### MLAstroRPA — Reboot / Reset ESP32 over Wireless
- Fixed: the reset button did nothing over Wireless (missing `reboot` mapping + `releaseControl` sent too early)
- Now sends `{"cmd":"reboot"}` like the Web UI button and reconnects automatically

### MLAstroRPA — WiFi passwords + settings
- Passwords are no longer broadcast: the 👁 button fetches them on demand from the device
- Empty credential fields are never sent — a blank box means "keep the current value"
- Telemetry-mirrored settings no longer keep stale values or fabricated defaults

### MLAstroRPA — Relative distance by keyboard
- Type D/M/S directly in the dock and web UI: degrees 0–5, minutes/seconds 0–59 (clamped, Enter sends)

### MLAstroRPA — Dock / options UI restyle
- Movement pad: the four direction buttons are rounded triangles with the Alt/Az label centred on the triangle's centroid; STOP is a centred square button
- Speed Level buttons are circles (40×40); the header FORCE STOP is a red circle inside a square frame (same look as the web UI)
- Relative steppers: removed the surrounding frame and the Degrees/Minutes/Seconds labels (values are still typed, clamped and sent on Enter)

### MLAstroRPA — Align error values shared instantly (dock <-> Web UI)
- Pressing **Enter** in an Az/Alt error field sends that axis' D/M/S + direction to the device right away (no need to press an Align button first)
- The device now broadcasts the align D/M/S + direction to every WebSocket client, so the Web UI mirrors the values changed from the dock/Serial without a page refresh

## 2.0.2.0

### MLAstroRPA — dockable layout cleanup
- Removed SET HOME HERE / RESET HOME from the dock (still available in CONFIGURATION / WebUI); RETURN TO HOME and ALIGN ALL restyled

### Installer / build tooling
- The MSI build compares its version with the top changelog entry and asks for confirmation when they differ

## 2.0.0.10 

### TPPA / automated correction

- Correction now waits for **Start** (connects, then pauses) instead of nudging right after the first error solve
- New **Automated adjustment timeout** (default 10 min, `0` disables): stops the routine if the tolerance is not reached
- **Correction axis mode**: `Auto` (larger error only) or **Both axes together** (default, single ALIGN command on the device)
- **Auto-reverse** hidden and off by default; axis direction via the Reverse Azimuth / Reverse Altitude toggles
- Fixed a false *"Unable to connect"* toast (raised from a background thread)

## 2.0.0.9 — Merged MLAstroRPA into TPPA (nina.plugin.MLAstroRPA_TPPA)

Single options page with tabs **TPPA OPTION / CONTROL / CONNECTION / CONFIGURATION** and a single
`IPluginManifest` (`PolarAlignmentPlugin`).

### MLAstroRPA — main features
- **CONTROL** — manual jog/move and home, live position & polar-alignment error, alarm history, FORCE STOP / RESET ERROR
- **CONNECTION** — COM port, connect/disconnect, ESP32 reset, live serial terminal (Hex send + HandShake)
- **CONFIGURATION** — soft limits, TMC2209 drivers (AZ/ALT), backlash & P.A. overshoot, WiFi (AP + Station), save-all & reboot

### Added to TPPA
- New **MLAstroRPA** polar-alignment system (beside None / UPAS / OAPA): handshake on connect, DMS corrections, structured align command + `ok`, completes on `READY` / `ALIGN_COMPLETED`
- **Alt-axis overshoot**: master enable + per-direction toggles + 0–240 arcminute amount
- **Correction Safety Factor** (default 75%, range 1–100%) for Azimuth and for Altitude when overshoot is inactive
- **Axis reversal** toggles (default ON, persisted) and **auto-reverse** (default OFF)
- Removed the **"Log polling data"** option; shared base classes/hooks removed code duplication between alignment systems

---

## Older versions (TPPA upstream)

All versions **2.2.5.0 and older** are the upstream **Three Point Polar Alignment (TPPA)** plugin by
[Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment) — they are NOT maintained in this
repository and are listed here only as the reference for the behaviour this plugin inherits.

- **Forked from:** upstream branch `master`, commit
  [`16b785ee2a3a36d2bf509968db3c307312e82017`](https://github.com/isbeorn/nina.plugin.polaralignment/commit/16b785ee2a3a36d2bf509968db3c307312e82017)
  (*"Merge pull request #10 from michelebergo/rename-aapa-to-oapa"*, 2026-04-11) — upstream plugin
  version **2.2.5.0**. The matching upstream tag `2.2.5.0` points at
  `ff981e8e14fbceefff7dcb0bf0861deff49a2a92` (2026-03-21).
- **Imported here by:** commit `859f659` — *"Create project: Copy TPPA in to this project make base
  code. Implement MLAstroRPA plugin into base code"* (2026-09-03).
- Upstream work released after that fork point (upstream `2.2.6.0` and later) is **not** included in
  this plugin; check the upstream repository for that history.